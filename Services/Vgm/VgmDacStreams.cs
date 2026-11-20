using System.Buffers.Binary;
using System.IO;

namespace MirrorFlux.Services.Vgm;

/// <summary>VGMのDACストリーム（0x90〜0x95）を、44.1kHzの整数クロックで音源チップへ書き込む。</summary>
internal sealed class VgmDacStreams(VgmPcmBanks banks, Action<byte, byte, byte, byte> write, bool strictBounds = true, bool stopOnSetup = true, Action<byte, byte, byte, ushort>? writeWord = null)
{
    /// <summary>DACストリーム1本分の設定と再生位置。</summary>
    private sealed class Stream
    {
        internal byte Chip, Port, Register, Bank, Step = 1, Base;
        internal uint Frequency;
        // 1回に書くバイト数。PWM・QSound・ES5506・C352と、種別0の音源チップでレジスタのビット4が0のときは2バイトとする。
        internal int Width => (Chip & 0x7f) is 17 or 31 or 37 or 39 || (Chip & 0x7f) == 0 && (Register & 0x10) == 0 ? 2 : 1;
        internal int Stride => Step * Width;
        internal int BaseBytes => Base * Width;
        internal int Origin, Count, Index;
        internal long Due;
        internal bool Configured, Active, Reverse, Loop;
    }
    private readonly Stream[] _streams = Enumerable.Range(0, 255).Select(_ => new Stream()).ToArray();
    /// <summary>DACストリーム命令のバイト数を返す。対象外の命令なら0。</summary>
    internal static int CommandLength(byte op) => op switch { 0x90 or 0x91 or 0x95 => 5, 0x92 => 6, 0x93 => 11, 0x94 => 2, _ => 0 };
    /// <summary>DACストリーム命令を1個処理する。</summary>
    internal void Command(ReadOnlySpan<byte> bytes)
    {
        // 命令の長さを確かめる。ストリーム番号255は全ストリームの停止にだけ使う。
        if (bytes.Length == 0 || CommandLength(bytes[0]) == 0 || bytes.Length < CommandLength(bytes[0]))
            throw new InvalidDataException("VGM DACストリーム命令が途中で切れています。");
        byte op = bytes[0], id = bytes[1];
        if (id == 255)
        {
            if (op == 0x94) foreach (var stream in _streams) stream.Active = false;
            return;
        }
        // 0x90: 書込み先の音源チップ・ポート・レジスタを設定する。
        var s = _streams[id];
        if (op == 0x90)
        {
            s.Chip = bytes[2]; s.Port = bytes[3]; s.Register = bytes[4]; s.Configured = true;
            if (stopOnSetup) s.Active = false;
            return;
        }
        // 0x91〜0x95: データバンク、周波数、開始、停止、データブロック単位の開始。
        if (!s.Configured) return;
        switch (op)
        {
            case 0x91: s.Bank = bytes[2]; s.Step = Math.Max((byte)1, bytes[3]); s.Base = bytes[4]; break;
            case 0x92:
                uint frequency = BinaryPrimitives.ReadUInt32LittleEndian(bytes[2..]);
                s.Frequency = frequency; break;
            case 0x93: Start(s, BinaryPrimitives.ReadUInt32LittleEndian(bytes[2..]), bytes[6], BinaryPrimitives.ReadUInt32LittleEndian(bytes[7..])); break;
            case 0x94: s.Active = false; break;
            case 0x95:
                // 0x95: 指定したデータブロックの先頭から再生する。フラグはビット0がループ、ビット4が逆再生。
                int block = BinaryPrimitives.ReadUInt16LittleEndian(bytes[2..]); byte flags = bytes[4];
                if (strictBounds && (flags & ~0x11) != 0) throw new NotSupportedException("DAC fast-start flags");
                var bank = banks.Get(s.Bank);
                if (bank == null || block >= bank.Blocks.Count)
                {
                    if (strictBounds) throw new NotSupportedException("DAC block is not loaded");
                    s.Active = false; return;
                }
                var (offset, length) = bank.Blocks[block];
                int count = Math.Max(0, (length - s.BaseBytes - s.Width + s.Stride) / s.Stride);
                Start(s, (uint)offset, (byte)(1 | (flags & 0x10) | ((flags & 1) << 7)), (uint)count);
                break;
        }
    }
    /// <summary>再生を始める。mode下位2ビットで長さの指定方法（命令数・ミリ秒・データ末尾まで）を選び、ビット4は逆再生、ビット7はループ。</summary>
    private void Start(Stream s, uint offset, byte mode, uint length)
    {
        if (strictBounds && (mode & ~0x93) != 0) throw new NotSupportedException("DAC length mode");
        // データバンクが未読込なら厳密モードでは拒否し、そうでなければ止める。
        var bank = banks.Get(s.Bank);
        if (bank == null)
        {
            if (strictBounds) throw new NotSupportedException("DAC bank is not loaded");
            s.Active = false; return;
        }
        // 開始位置を決め、バンクの範囲に収まる長さに切り詰める。
        if (offset != uint.MaxValue)
        {
            if (strictBounds && (long)offset + s.BaseBytes > bank.Data.Count) throw new NotSupportedException("DAC start outside bank");
            s.Origin = (int)Math.Min((long)offset + s.BaseBytes, bank.Data.Count);
        }
        int available = Math.Max(0, (bank.Data.Count - s.Origin - s.Width + s.Stride) / s.Stride);
        long count = (mode & 3) switch { 1 => length, 2 => (long)((ulong)s.Frequency * length / 1000), 3 => available, _ => s.Count };
        if (strictBounds && count > available) throw new NotSupportedException("DAC length outside bank");
        s.Count = (int)Math.Min(count, available); s.Index = 0; s.Reverse = (mode & 0x10) != 0; s.Loop = (mode & 0x80) != 0;
        s.Due = 0; s.Active = s.Count > 0;
    }
    /// <summary>書込み時刻に達したストリームのデータを音源チップへ書き込む。</summary>
    internal void EmitDue()
    {
        foreach (var s in _streams)
        {
            if (!s.Active || s.Frequency == 0) continue;
            while (s.Active && s.Due <= 0)
            {
                var bank = banks.Get(s.Bank);
                long index = (long)s.Origin + (s.Reverse ? (long)s.Count - 1 - s.Index : s.Index) * s.Stride;
                if (bank == null || index < 0 || index > bank.Data.Count - s.Width) { s.Active = false; break; }
                if (s.Width == 2) writeWord?.Invoke(s.Chip, s.Port, s.Register, (ushort)(bank.Data[(int)index] | bank.Data[(int)index + 1] << 8));
                else write(s.Chip, s.Port, s.Register, bank.Data[(int)index]);
                s.Due += 44100; s.Index++;
                if (s.Index >= s.Count)
                {
                    if (s.Loop) s.Index = 0;
                    else s.Active = false;
                }
            }
        }
    }
    /// <summary>次にどれかのストリームが書き込むまでのフレーム数を返す。</summary>
    internal int FramesUntilWrite()
    {
        long next = int.MaxValue;
        foreach (var s in _streams)
            if (s.Active && s.Frequency > 0) next = Math.Min(next, Math.Max(1, (s.Due + s.Frequency - 1) / s.Frequency));
        return (int)next;
    }
    /// <summary>指定フレーム数だけ時間を進める。</summary>
    internal void Advance(int frames)
    {
        foreach (var s in _streams) if (s.Active) s.Due -= (long)frames * s.Frequency;
    }
}
