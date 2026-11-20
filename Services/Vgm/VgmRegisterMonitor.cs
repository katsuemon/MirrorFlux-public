using MirrorFlux.Services.Music;

namespace MirrorFlux.Services.Vgm;

/// <summary>
/// OPL系（YM2413・YM3812・YMF262など）とK051649（SCC）のレジスタ書込みを記録し、鍵盤・レベルメーター用のチャンネル情報を作る。
/// 補助の音声レンダラーの有無とは関係なく、レジスタの内容を保持する。
/// </summary>
internal sealed class VgmRegisterMonitor(string chip, uint rawClock, int instance)
{
    private readonly uint _clock = rawClock & 0x3fffffff;
    private readonly bool _sccPlus = (rawClock & 0x80000000) != 0;
    private readonly string _name = (chip == "YM2413" && (rawClock & 0x80000000) != 0 ? "VRC7" : chip) +
        (instance == 0 ? "" : $" #{instance + 1}");
    private readonly byte[] _regs = new byte[1024];
    private readonly bool[] _used = new bool[chip == "YMF262" ? 18 : 9];
    private readonly int[] _keyGens = new int[chip == "YMF262" ? 18 : 9];
    private byte _sccTest;

    /// <summary>K051649（SCC）への書込みを記録する。ポート0・4は波形、1は周期、2は音量、3はキーオン、5はテストレジスタ。</summary>
    internal void WriteScc(int port, int address, byte value)
    {
        if (port is 0 or 4)
        {
            // 波形は範囲外の番地と、テストレジスタで書込みが禁止されている間は書かない。
            if (address >= (_sccPlus ? 160 : 128) || (_sccTest & 0x40) != 0 ||
                (!_sccPlus && address >= 96 && (_sccTest & 0x80) != 0)) return;
            _regs[address] = value;
        }
        else if (port == 5) _sccTest = value;
        else if (port is >= 1 and <= 3)
        {
            _regs[port * 256 + address] = value;
            if (port == 3)
            {
                _regs[0x300] = value;
                for (int ch = 0; ch < 5; ch++) _used[ch] |= (value & (1 << ch)) != 0;
            }
        }
    }

    /// <summary>OPL系への書込みを記録し、キーオンの立ち上がりを数える。</summary>
    internal void WriteOpl(int address, byte value)
    {
        // OPL3の互換モードでは上位ポートが下位ポートの鏡像になる。
        if (chip == "YMF262" && address >= 0x100 && address != 0x105 && (_regs[0x105] & 1) == 0)
            address &= 0xff;
        // OPLLは各チャンネルレジスタ群の最後の7番地が鏡像になっている。
        if (chip == "YM2413")
        {
            if (address is >= 0x19 and <= 0x1f or >= 0x29 and <= 0x2f or >= 0x39 and <= 0x3f) address -= 9;
        }
        // キーオンのレジスタなら、チャンネルを使用済みにしてキーオン回数を数える。
        int keyBase = chip == "YM2413" ? 0x20 : 0xb0;
        int keyBit = chip == "YM2413" ? 0x10 : 0x20;
        int port = chip == "YMF262" ? address & 0x100 : 0;
        int channel = address - port - keyBase;
        bool wasKeyOn = (_regs[address] & keyBit) != 0;
        _regs[address] = value;
        if ((uint)channel < 9 && (value & keyBit) != 0)
        {
            int chIndex = channel + port / 0x100 * 9;
            _used[chIndex] = true;
            if (!wasKeyOn) _keyGens[chIndex]++;
        }
        // リズムモードのキーオンは6〜8チャンネルを使用済みにする。
        if (address == (chip == "YM2413" ? 0x0e : 0xbd) && (value & 0x20) != 0)
        {
            _used[6] |= (value & 0x10) != 0;
            _used[7] |= (value & 0x09) != 0;
            _used[8] |= (value & 0x06) != 0;
        }
    }

    /// <summary>全チャンネルの表示用情報を作る。</summary>
    internal IEnumerable<MusicChannelSnapshot> Capture(bool playing)
    {
        if (chip != "K051649")
        {
            // OPL系のチャンネル数。VRC7の旋律チャンネルは6個。
            int channels = chip == "YMF262" ? 18 : chip == "YM2413" && (rawClock & 0x80000000) != 0 ? 6 : 9;
            for (int ch = 0; ch < channels; ch++) yield return OplChannel(ch, playing);
        }
        else
        {
            // SCC: 周期から周波数を求め、波形を符号付きから表示用に変換する。SCC+でない場合、5チャンネル目は4チャンネル目の波形を使う。
            for (int ch = 0; ch < 5; ch++)
            {
                int period = _regs[0x100 + ch * 2] | ((_regs[0x101 + ch * 2] & 15) << 8);
                double hz = period > 8 ? _clock / (16.0 * (period + 1)) : 0;
                int volume = _regs[0x200 + ch] & 15;
                bool on = playing && hz > 0 && volume > 0 && (_regs[0x300] & (1 << ch)) != 0;
                int start = (!_sccPlus && ch == 4 ? 3 : ch) * 32;
                var wave = new byte[32];
                for (int i = 0; i < wave.Length; i++) wave[i] = (byte)(_regs[start + i] ^ 0x80);
                yield return new() { Id = $"{_name}:{ch}", ChipName = _name, Label = $"SCC{ch + 1:00}",
                    Kind = MusicChannelKind.Psg, IsInUse = _used[ch], IsKeyOn = on, NoteNumber = Note(hz),
                    Frequency = hz, Level = on ? volume / 15.0 : 0, Volume = volume, Waveform = wave, DataString = $"T:{period:X3}" };
            }
        }
    }

    /// <summary>OPL系でキャリアになるオペレーターの番号。</summary>
    private static readonly int[] OplCarrier = [3, 4, 5, 11, 12, 13, 19, 20, 21];
    /// <summary>OPL系の1チャンネル分の表示用情報を作る。</summary>
    private MusicChannelSnapshot OplChannel(int ch, bool playing)
    {
        // OPL3ならポートとチャンネル番号を分け、4オペレーター接続の組を判定する。組の後ろ側は表示上消す。
        bool opll = chip == "YM2413";
        bool opl3 = chip == "YMF262";
        int port = opl3 ? ch / 9 : 0;
        int local = opl3 ? ch % 9 : ch;
        int baseAddress = port * 0x100;
        bool newMode = opl3 && (_regs[0x105] & 1) != 0;
        int pair = local % 3;
        bool fourOp = newMode && local < 6 && (_regs[0x104] & (1 << (port * 3 + pair))) != 0;
        bool secondary = fourOp && local >= 3;
        // F-Numberとブロックから周波数を求め、キャリアの総レベルから音量を求める。
        int high = _regs[baseAddress + (opll ? 0x20 : 0xb0) + local];
        int fnum = _regs[baseAddress + (opll ? 0x10 : 0xa0) + local] | ((high & (opll ? 1 : 3)) << 8);
        int block = (high >> (opll ? 1 : 2)) & 7;
        double hz = Math.ScaleB(_clock / (opl3 ? 288.0 : 72.0) * fnum, block - (opll ? 19 : 20));
        int maxVolume = opll ? 15 : 63;
        int volume = opll ? 15 - (_regs[0x30 + local] & 15) : 63 - (_regs[baseAddress + 0x40 + OplCarrier[local]] & 63);
        if (fourOp && !secondary)
        {
            int secondCarrier = 63 - (_regs[baseAddress + 0x40 + OplCarrier[local + 3]] & 63);
            volume = Math.Max(volume, secondCarrier);
        }
        // リズムモードの6〜8チャンネルは、打楽器ごとのキーと音量に置き換える。
        int rhythm = _regs[opll ? 0x0e : 0xbd];
        bool drum = port == 0 && local >= 6 && local <= 8 && (rhythm & 0x20) != 0;
        bool key = (high & (opll ? 0x10 : 0x20)) != 0;
        string label = $"FM{ch + 1:00}";
        if (drum)
        {
            int first = local == 6 ? 0x10 : local == 7 ? 0x08 : 0x02;
            int second = local == 6 ? 0 : local == 7 ? 0x01 : 0x04;
            int otherVolume = opll ? 15 - (_regs[0x30 + local] >> 4) : 63 - (_regs[0x40 + OplCarrier[local] - 3] & 63);
            volume = Math.Max((rhythm & first) != 0 ? volume : 0, (rhythm & second) != 0 ? otherVolume : 0);
            key = (rhythm & (first | second)) != 0;
            label = local == 6 ? "BD" : local == 7 ? "SD/HH" : "CYM/TOM";
        }
        if (secondary) { key = false; volume = 0; }
        // 出力先は新モードのOPL3だけ左右を持ち、それ以外は中央とする。
        int pan = !newMode ? 3 : ((_regs[baseAddress + 0xc0 + local] & 0x10) != 0 ? 2 : 0) |
            ((_regs[baseAddress + 0xc0 + local] & 0x20) != 0 ? 1 : 0);
        bool on = playing && key && volume > 0 && pan != 0 && (drum || hz > 0);
        // 音程の揺れ（PM）が有効かと、表示する音高を決める。打楽器は決まった音高で表示する。
        bool hasHwPm = !drum && (opll ? (_regs[0x01] & 0x40) != 0 : (_regs[baseAddress + 0x20 + OplCarrier[local]] & 0x40) != 0);
        int? note = drum ? local == 6 ? 36 : local == 7 ? ((rhythm & 8) != 0 ? 38 : 42) : ((rhythm & 2) != 0 ? 49 : 45) : secondary ? null : Note(hz);
        return new() { Id = $"{_name}:FM:{ch}", ChipName = _name, Label = label, Kind = drum ? MusicChannelKind.Rhythm : MusicChannelKind.Fm,
            IsInUse = _used[ch] && !secondary, IsKeyOn = on, KeyGeneration = _keyGens[ch], HasHardwarePitchLfo = hasHwPm, NoteNumber = note, Frequency = drum || secondary ? null : hz,
            Level = on ? volume / (double)maxVolume : 0, Volume = volume, Pan = pan,
            DataString = secondary ? $"4OP PAIR FM{ch - 2:00}" : fourOp ? $"4OP FNUM:{fnum:X3}" : $"FNUM:{fnum:X3}" };
    }

    private static int? Note(double hz) => hz > 0 ? Math.Clamp((int)Math.Round(69 + 12 * Math.Log2(hz / 440)), 0, 127) : null;

    /// <summary>表示用のレジスタ一覧を返す。</summary>
    internal IEnumerable<MusicRegisterBank> Registers()
    {
        for (int port = 0; port < (chip == "K051649" ? 4 : chip == "YMF262" ? 2 : 1); port++)
            yield return new() { ChipName = _name, Port = port, Values = _regs.AsSpan(port * 256, 256).ToArray() };
    }
}
