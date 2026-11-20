using System.Buffers.Binary;
using System.IO;

namespace MirrorFlux.Services.Vgm;

/// <summary>
/// VGMファイルを検証し、音源チップの一覧と命令列に変換する。
/// 命令の解釈はlibvgmの再生処理ではなく、公開されているVGM 1.71仕様に従う。
/// 受け入れは慎重に行い、宣言された音源チップと命令がすべて対応している場合だけ再生対象にする。
/// </summary>
internal sealed class IndependentVgmProgram
{
    /// <summary>ヘッダーで宣言された音源チップ1個分。種類、何個目か、クロック、表示名、音量を持つ。</summary>
    internal readonly record struct Device(int Type, int Instance, uint Clock, string Name, double Volume = 1, double SsgVolume = 1);
    /// <summary>解釈済みの命令1個分。データ上の位置と長さ、書込み先の音源チップとレジスタ値を持つ。</summary>
    internal readonly record struct Command(byte Code, int Offset, int Length, int Type = -1, int Instance = 0, uint Port = 0, uint Address = 0, uint Value = 0);
    internal readonly List<Device> Devices = [];
    internal readonly List<Command> Commands = [];
    internal int LoopIndex = -1;
    internal double Gain = 1;
    internal byte[] Data = [];
    internal byte[] Header = [];
    /// <summary>ヘッダー内のクロック欄の位置と、音源チップ種別・名前の対応表。</summary>
    private static readonly (int Field, int Type, string Name)[] Clocks = [
        (0x0c,0,"SN76489"),(0x10,1,"YM2413"),(0x2c,2,"YM2612"),(0x30,3,"YM2151"),
        (0x38,4,"SegaPCM"),(0x40,5,"RF5C68"),(0x44,6,"YM2203"),(0x48,7,"YM2608"),
        (0x4c,8,"YM2610"),(0x50,9,"YM3812"),(0x54,10,"YM3526"),(0x58,11,"Y8950"),
        (0x5c,12,"YMF262"),(0x60,13,"YMF278B"),(0x64,14,"YMF271"),(0x68,15,"YMZ280B"),
        (0x6c,16,"RF5C164"),(0x70,17,"PWM"),(0x74,18,"AY8910"),(0x80,19,"GameBoy"),
        (0x84,20,"NES APU"),(0x88,21,"MultiPCM"),(0x8c,22,"uPD7759"),(0x90,23,"OKIM6258"),
        (0x98,24,"OKIM6295"),(0x9c,25,"K051649"),(0xa0,26,"K054539"),(0xa4,27,"HuC6280"),
        (0xa8,28,"C140"),(0xac,29,"K053260"),(0xb0,30,"Pokey"),(0xb4,31,"QSound"),
        (0xb8,32,"SCSP"),(0xc0,33,"WonderSwan"),(0xc4,34,"VSU"),(0xc8,35,"SAA1099"),
        (0xcc,36,"ES5503"),(0xd0,37,"ES5506"),(0xd8,38,"X1-010"),(0xdc,39,"C352"),(0xe0,40,"IremGA20"),(0xe4,41,"Mikey"),(0xe8,42,"K007232"),(0xec,43,"K005289"),(0xf0,44,"MSM5205"),(0xf4,45,"MSM5232"),(0xf8,46,"BSMT2000"),(0xfc,47,"ICS2115")];
    internal static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
    internal static bool HasChip(int type) => type is >= 0 and <= 48;
    /// <summary>VGMファイル全体を検証し、音源チップの一覧、命令列、ループ位置、音量補正を求める。</summary>
    internal static IndependentVgmProgram Parse(VgmFile file)
    {
        // 対応する版か確かめ、命令データの範囲と拡張ヘッダーの位置を検証する。
        if (file.Version < 0x100 || file.Version > 0x172) throw new NotSupportedException("VGM version outside 1.00–1.72");
        var result = new IndependentVgmProgram { Data = (byte[])file.RawData.Clone() };
        byte[] data = result.Data;
        int start = file.DataOffset;
        long declaredEnd = U32(data, 4) == 0 ? data.Length : 4L + U32(data, 4);
        if (start < 64 || start >= data.Length || declaredEnd > data.Length || declaredEnd <= start)
            throw new InvalidDataException("VGMデータ範囲が不正です。");
        int end = (int)declaredEnd;
        int headerEnd = start, extra = 0;
        if (file.Version >= 0x170 && start >= 0xc0 && U32(data, 0xbc) != 0)
        {
            long position = 0xbc + (long)U32(data, 0xbc);
            if (position < 0xc0 || position > start - 4) throw new InvalidDataException("VGM拡張ヘッダーの範囲が不正です。");
            extra = (int)position; headerEnd = extra;
        }
        // ヘッダー欄はデータ開始位置（拡張ヘッダーがあればその手前）より前にあるときだけ有効とし、それ以外は0として読む。
        result.Header = data[..headerEnd];
        uint Header(int offset) => offset + 4 <= headerEnd ? U32(data, offset) : 0;

        // クロック欄から音源チップを登録する。ビット30が立っていれば2個目も置く。
        // ビット31はYM2413欄ではVRC7、MSM5205欄ではMSM6585を示し、ES5506欄では0のときES5505を示す。
        foreach (var (field, type, name) in Clocks)
        {
            uint clock = field switch { 0x10 => file.Ym2413Clock, 0x2c => file.Ym2612Clock, 0x30 => file.Ym2151Clock, _ => Header(field) };
            if (clock == 0) continue;
            if (!HasChip(type)) throw new NotSupportedException("chip: " + name);
            if ((clock & 0x3fffffff) is < 1000 or > 100000000) throw new InvalidDataException("VGM音源クロックが範囲外です。");
            if (type == 39 && (start <= 0xd6 || data[0xd6] == 0)) throw new InvalidDataException("VGM C352クロック分周値が0です。");
            string display = type == 1 && (clock & 0x80000000) != 0 ? "VRC7" : type == 37 && (clock & 0x80000000) == 0 ? "ES5505" : type == 44 && (clock & 0x80000000) != 0 ? "MSM6585" : name;
            for (int instance = 0; instance < ((clock & 0x40000000) == 0 ? 1 : 2); instance++)
                result.Devices.Add(new(type, instance, clock, display + (instance == 0 ? "" : " #2")));
        }

        // 拡張ヘッダーから2個目のクロック表と音量表を読む。
        if (extra != 0)
        {
            uint size = U32(data, extra);
            if (size < 4 || size > start - extra) throw new InvalidDataException("VGM拡張ヘッダー長が不正です。");
            // 拡張ヘッダー内の表の位置を求め、表がデータ開始位置を越えないか確かめる。
            int Table(int field, int entrySize)
            {
                uint relative = U32(data, field); if (relative == 0) return 0;
                long position = field + (long)relative;
                if (position < extra + size || position >= start || position + 1 + (long)data[(int)position] * entrySize > start)
                    throw new InvalidDataException("VGM拡張テーブルが範囲外です。");
                return (int)position;
            }
            // 2個目の音源チップのクロックを上書きする。
            if (size >= 8)
            {
                int table = Table(extra + 4, 5);
                if (table != 0) for (int i = 0, p = table + 1; i < data[table]; i++, p += 5)
                    {
                        int index = result.Devices.FindIndex(d => d.Type == data[p] && d.Instance == 1);
                        uint clock = U32(data, p + 1);
                        if (index >= 0)
                        {
                            if ((clock & 0x3fffffff) is < 1000 or > 100000000) throw new InvalidDataException("VGM追加クロックが範囲外です。");
                            result.Devices[index] = result.Devices[index] with { Clock = clock };
                        }
                    }
            }
            // 音源チップごとの音量を読む。ビット15の有無で計算方法を変え、SSG側の音量は別に持つ。
            if (size >= 12)
            {
                int table = Table(extra + 8, 4);
                if (table != 0) for (int i = 0, p = table + 1; i < data[table]; i++, p += 4)
                    {
                        int type = data[p] & 127, instance = data[p + 1] & 1;
                        int index = result.Devices.FindIndex(d => d.Type == type && d.Instance == instance);
                        if (index < 0) continue;
                        bool ssg = (data[p] & 128) != 0;
                        ushort value = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(p + 2));
                        double factor = (value & 0x8000) != 0 ? (value & 0x7fff) / 256.0 * (ssg && instance == 0 ? 2 : 1) : value / (ssg || type is 7 or 8 ? 64.0 : 128.0);
                        result.Devices[index] = ssg ? result.Devices[index] with { SsgVolume = factor } : result.Devices[index] with { Volume = factor };
                    }
            }
        }
        // 対応する音源チップが1個もなければ再生しない。
        if (result.Devices.Count == 0) throw new NotSupportedException("no supported chips");
        // ヘッダーの音量補正値（0x7C）を倍率に変える。0xC1は-64として扱う。
        if (headerEnd > 0x7c)
        {
            int modifier = data[0x7c]; if (modifier > 0xc0) modifier = modifier == 0xc1 ? -64 : modifier - 256;
            result.Gain = Math.Pow(2, modifier / 32.0);
        }
        // 命令の長さ不足と、宣言されていない音源チップへの書込みを検出する。
        void Need(int p, int count) { if (count < 0 || p > end - count) throw new InvalidDataException("VGM命令が途中で切れています。"); }
        void DeviceExists(int type, int instance)
        {
            if (!result.Devices.Any(d => d.Type == type && d.Instance == instance))
                throw new NotSupportedException("write to undeclared chip");
        }
        // 走査中の経過サンプル数、待ち時間、PCMバンク、DACストリームの状態を追う。
        long samples = 0, loopSamples = -1;
        var waits = new VgmWaitTimer();
        int pcmCursor = 0;
        var banks = new VgmPcmBanks();
        var streams = new VgmDacStreams(banks, (_, _, _, _) => { });

        // 命令を先頭から順に解釈して命令列に加える。
        for (int p = start; p < end;)
        {
            if (p == file.LoopOffset) { result.LoopIndex = result.Commands.Count; loopSamples = samples; }
            var decoded = VgmCommandDecoder.Decode(data, p, end);
            byte op = data[p]; int size = decoded.Length, type = decoded.Type, instance = decoded.Instance; uint port = decoded.Port;
            // ヘッダーで宣言されていない音源チップ宛ての書込みは、書込み先なしとして扱う。
            if (type >= 0 && !result.Devices.Any(d => d.Type == type && d.Instance == instance)) type = -1;
            if (decoded.Type < 0) switch (op)
                {
                    // 0x68: PCM RAMへの転送。転送元がPCMバンクの範囲内か確かめる。
                    case 0x68:
                        size = 12; Need(p, size); if (data[p + 1] != 0x66) throw new InvalidDataException("VGM RAM識別子が不正です。");
                        type = VgmCommandDecoder.RamType(data[p + 2] & 0x7f); instance = data[p + 2] >> 7;
                        if (type >= 0)
                        {
                            DeviceExists(type, instance); uint count = VgmCommandDecoder.U24(data, p + 9); if (count == 0) count = 0x1000000;
                            if ((long)VgmCommandDecoder.U24(data, p + 3) + count > (banks.Get((byte)(data[p + 2] & 0x7f))?.Data.Count ?? 0)) throw new InvalidDataException("VGM RAM転送元が範囲外です。");
                        }
                        break;
                    // 0x61〜0x64・0x70〜0x8F: 待ち時間。0x64は0x62・0x63の待ち時間の上書きで、0x8nは旧式DACへの書込みも兼ねる。
                    case >= 0x61 and <= 0x64 or >= 0x70 and <= 0x8f:
                        size = VgmWaitTimer.CommandLength(op); Need(p, size);
                        if (op == 0x64 && data[p + 1] is not (0x62 or 0x63)) throw new InvalidDataException("VGM待機上書きの対象が不正です。");
                        if (op >= 0x80)
                        {
                            DeviceExists(2, 0);
                            if (pcmCursor >= (banks.Get(0)?.Data.Count ?? 0)) throw new NotSupportedException("legacy DAC bank bounds");
                            pcmCursor++;
                        }
                        waits.TryWait(data.AsSpan(p, size), out int waited); samples += waited; break;
                    // 0xE0: 旧式DACのデータ位置を移す。
                    case 0xe0:
                        size = 5; Need(p, size);
                        if (U32(data, p + 1) > (uint)(banks.Get(0)?.Data.Count ?? 0)) throw new NotSupportedException("legacy DAC seek bounds");
                        pcmCursor = (int)U32(data, p + 1); break;
                    // 0x67: データブロック。0x00〜0x7FはPCMバンクに読み込み、ROM・RAM向けは書込み範囲を確かめる。
                    case 0x67:
                        Need(p, 7);
                        if (data[p + 1] != 0x66) throw new InvalidDataException("VGMデータブロック識別子が不正です。");
                        uint length = U32(data, p + 3); instance = (int)(length >> 31); length &= 0x7fffffff;
                        if (length > 64 * 1024 * 1024) throw new NotSupportedException("sample block exceeds 64 MiB");
                        size = 7 + (int)length; Need(p, size);
                        byte kind = data[p + 2];
                        if (kind <= 0x7f && instance == 0)
                        {

                            banks.Load(kind, data.AsSpan(p + 7, (int)length), p + 7);
                        }
                        else if (VgmCommandDecoder.MemoryTarget(kind).Type >= 0)
                        {
                            (type, port) = VgmCommandDecoder.MemoryTarget(kind); DeviceExists(type, instance);
                            if (kind >= 0xc0) { int metadata = kind < 0xe0 ? 2 : 4; if (length < metadata) throw new InvalidDataException("VGM RAMヘッダーが不足しています。"); break; }
                            if (length < 8) throw new InvalidDataException("VGM ROMヘッダーが不足しています。");
                            uint memorySize = U32(data, p + 7), address = U32(data, p + 11); if (type == 37) address &= 0x0fffffff;
                            if (memorySize > 64 * 1024 * 1024) throw new NotSupportedException("ROM exceeds 64 MiB");
                            if (address > memorySize || length - 8 > memorySize - address) throw new InvalidDataException("VGM ROM書込み範囲が不正です。");
                        }
                        // 種類の分からないブロックは公開仕様のとおり読み飛ばす。
                        break;
                    // 0x90〜0x95: DACストリームの制御。
                    case >= 0x90 and <= 0x95:
                        size = VgmDacStreams.CommandLength(op); Need(p, size);
                        if (op == 0x90 && data[p + 1] != 255)
                        {
                            int streamType = data[p + 2] & 0x7f, streamInstance = data[p + 2] >> 7;
                            DeviceExists(streamType, streamInstance);
                        }
                        streams.Command(data.AsSpan(p, size));

                        break;
                    // 0x66: 終了。ループの妥当性を確かめ、全体の音量補正を決めて返す。
                    case 0x66:
                        result.Commands.Add(new(op, p, 1));
                        // ループ開始位置が命令の境目にあり、ループ区間に待ち時間があるか確かめる。
                        if (file.LoopOffset != 0 && (result.LoopIndex < 0 || samples <= loopSamples))
                            throw new InvalidDataException("VGMループが命令境界にないか、待ち時間がありません。");
                        // ループ内の待ち時間上書き（0x64）は、1周目で数えた待ち時間を0にすることがある。
                        // 2周目以降はこの時点に残った上書き値でループを始めるため、その条件で待ち時間を数え直す。
                        if (result.LoopIndex >= 0)
                        {
                            long repeated = 0; var loopWaits = waits.Copy();
                            foreach (var c in result.Commands.Skip(result.LoopIndex))
                                if (loopWaits.TryWait(data.AsSpan(c.Offset, c.Length), out int loopWaited)) repeated += loopWaited;
                            if (repeated <= 0) throw new InvalidDataException("VGMループの繰り返しに待ち時間がありません。");
                        }
                        // 旧式DACを使うループでは、データを再び読む前に0xE0による位置合わせが必要。
                        if (result.LoopIndex >= 0)
                        {
                            bool seek = false;
                            foreach (var c in result.Commands.Skip(result.LoopIndex))
                            {
                                if (c.Code == 0xe0) seek = true;
                                if (c.Code is >= 0x80 and <= 0x8f && !seek) throw new NotSupportedException("DAC loop without seek");
                            }
                        }
                        // 宣言された音源チップごとの音量を使い、既存のヘッドルームの方針に合わせて全体の音量を正規化する。
                        double weight = result.Devices.Sum(d => (d.Type is 7 or 8 ? 64 : 128) * d.Volume + (d.Type is 6 or 7 or 8 ? 64 * d.SsgVolume : 0));
                        double normalization = 1;
                        if (weight > 0)
                        {
                            while (weight <= 384) { weight *= 2; normalization *= 2; }
                            while (weight > 768) { weight /= 2; normalization /= 2; }
                        }
                        result.Gain *= normalization / 4;
                        return result;
                    // 0x33〜0x3E: 引数1バイトの命令として読み飛ばす。
                    case >= 0x33 and <= 0x3e: size = 2; Need(p, size); break;
                    // 0x45〜0x4E: 引数はVGM 1.60以降が2バイト、それより前は1バイト。
                    case >= 0x45 and <= 0x4e: size = file.Version >= 0x160 ? 3 : 2; Need(p, size); break;
                    // 0xCA〜0xCF・0xD7〜0xDFは引数3バイト、0xE2以降は引数4バイトとして読み飛ばす。
                    case >= 0xca and <= 0xcf or >= 0xd7 and <= 0xdf: size = 4; Need(p, size); break;
                    case >= 0xe2: size = 5; Need(p, size); break;
                    default: throw new NotSupportedException($"command 0x{op:X2}");
                }
            // 解釈した命令を記録して次の命令へ進む。
            result.Commands.Add(new(op, p, size, type, instance, port, decoded.Address, decoded.Value)); p += size;
        }
        throw new InvalidDataException("VGM終了命令がありません。");
    }
}
