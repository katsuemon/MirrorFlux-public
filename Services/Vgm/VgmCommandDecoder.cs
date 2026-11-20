using System.Buffers.Binary;
using System.IO;
namespace MirrorFlux.Services.Vgm;

/// <summary>
/// 公開VGM 1.71仕様のバス表現に従い、レジスタ書込み命令を音源チップ種別・番号・ポート・アドレス・値に分解する。
/// 値は音源チップのネイティブ実装を作る前に一度だけ解釈する。
/// </summary>
internal static class VgmCommandDecoder
{
    /// <summary>指定位置の命令を解釈する。レジスタ書込みでない命令は長さ1の命令として返す。</summary>
    internal static IndependentVgmProgram.Command Decode(byte[] data, int p, int end)
    {
        // 2個目向け（0xA1〜0xAF）は1個目の命令コードに戻し、命令の長さとレジスタ書込みかどうかを決める。
        byte op = data[p], primary = op is >= 0xa1 and <= 0xaf ? (byte)(op - 0x50) : op;
        int length = op is >= 0xc0 and <= 0xd6 ? 4 : op == 0xe1 ? 5 : op is 0x30 or 0x31 or 0x32 or 0x3f or 0x4f or 0x50 ? 2 : 3;
        bool register = primary is >= 0x51 and <= 0x5f || op is 0x30 or 0x31 or 0x32 or 0x3f or 0x4f or 0x50 or 0xa0 or >= 0xb0 and <= 0xc8 or >= 0xd0 and <= 0xd6 or 0xe1 or >= 0x40 and <= 0x44 or 0xc9;
        if (!register) return new(op, p, 1);
        if (p > end - length) throw new InvalidDataException("VGMレジスタ命令が途中で切れています。");
        // 既定ではアドレスと値を1バイトずつ読み、命令ごとの形式に合わせて読み直す。
        int type, instance = 0; uint port = 0, address = data[p + 1], value = length >= 3 ? data[p + 2] : 0u;
        // 0x32: MSM5205。1バイトに番号・アドレス・値を詰めている。
        if (op == 0x32) { type = 44; instance = data[p + 1] >> 7; address = (uint)(data[p + 1] >> 4) & 7; value = (uint)(data[p + 1] & 15); }
        // 0x40〜0x44: Mikey・K007232・K005289・MSM5232・ICS2115。K005289は値の上位4ビットがアドレス側に入る。
        else if (op is >= 0x40 and <= 0x44) { type = op == 0x40 ? 41 : op == 0x41 ? 42 : op == 0x42 ? 43 : op == 0x43 ? 45 : 47; instance = data[p + 1] >> 7; address &= 0x7f; if (op == 0x42) { address >>= 4; value |= (uint)(data[p + 1] & 15) << 8; } }
        // 0xC9: BSMT2000。アドレス0x7Fは制御レジスタへの書込みとして扱う。
        else if (op == 0xc9) { type = 46; instance = data[p + 1] >> 7; address &= 0x7f; value = (uint)(data[p + 2] << 8 | data[p + 3]); port = 258; if (address == 0x7f) { port = 0; address = (uint)(0x10 + data[p + 2]); value = data[p + 3]; } }
        // 0x51〜0x5F（2個目は0xA1〜0xAF）: ヤマハ系FM音源。0x53・0x57・0x59・0x5Fは2つ目のポート。
        else if (primary is >= 0x51 and <= 0x5f)
        {
            type = primary switch { 0x51 => 1, 0x52 or 0x53 => 2, 0x54 => 3, 0x55 => 6, 0x56 or 0x57 => 7, 0x58 or 0x59 => 8, 0x5a => 9, 0x5b => 10, 0x5c => 11, 0x5d => 15, _ => 12 };
            instance = op != primary ? 1 : 0; port = primary is 0x53 or 0x57 or 0x59 or 0x5f ? 1u : 0;
        }
        // 0x30・0x3F・0x4F・0x50: SN76489の書込みとGame Gearのステレオ設定。0x30・0x3Fは2個目向け。
        else if (op is 0x30 or 0x3f or 0x4f or 0x50) { type = 0; instance = op is 0x30 or 0x3f ? 1 : 0; address = op is 0x3f or 0x4f ? 1u : 0; value = data[p + 1]; }
        // 0x31: AY8910のステレオ設定。
        else if (op == 0x31) { type = 18; instance = data[p + 1] >> 7; port = 259; address = 0; value = (uint)(data[p + 1] & 0x3f); }
        // 0xA0・0xB0〜0xBF: アドレスと値が1バイトずつの音源チップ。0xB2（PWM）はアドレス4ビットと値12ビット。
        else if (op is 0xa0 or >= 0xb0 and <= 0xbf)
        {
            type = op switch { 0xa0 => 18, 0xb0 => 5, 0xb1 => 16, 0xb2 => 17, 0xb3 => 19, 0xb4 => 20, 0xb5 => 21, 0xb6 => 22, 0xb7 => 23, 0xb8 => 24, 0xb9 => 27, 0xba => 29, 0xbb => 30, 0xbc => 33, 0xbd => 35, 0xbe => 37, _ => 40 };
            instance = (int)(address >> 7); address &= 0x7f;
            if (op == 0xb2) { address = (uint)(data[p + 1] >> 4) & 7; value = (uint)((data[p + 1] & 15) << 8 | data[p + 2]); }
        }
        // 0xC0〜0xD6・0xE1: アドレスや値が複数バイトの音源チップ。命令ごとに並び方が違う。
        else
        {
            instance = data[p + 1] >> 7;
            type = op switch { 0xc0 => 4, 0xc1 => 5, 0xc2 => 16, 0xc3 => 21, 0xc4 => 31, 0xc5 => 32, 0xc6 => 33, 0xc7 => 34, 0xc8 => 38, 0xd0 => 13, 0xd1 => 14, 0xd2 => 25, 0xd3 => 26, 0xd4 => 28, 0xd5 => 36, 0xd6 => 37, _ => 39 };
            if (op <= 0xc2) { address = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(p + 1)); instance = (int)(address >> 15); address &= 0x7fff; value = data[p + 3]; port = op == 0xc0 ? 0u : 256; }
            else if (op == 0xc3) { address &= 0x7f; value = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(p + 2)); port = 257; }
            else if (op == 0xc4) { instance = 0; address = data[p + 3]; value = (uint)(data[p + 1] << 8 | data[p + 2]); }
            else if (op is >= 0xd0 and <= 0xd2) { port = (uint)(data[p + 1] & 0x7f); address = data[p + 2]; value = data[p + 3]; }
            else if (op == 0xd6) { port = 258; address &= 0x7f; value = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(p + 2)); }
            else { address = (uint)((data[p + 1] & 0x7f) << 8 | data[p + 2]); value = op == 0xe1 ? (uint)(data[p + 3] << 8 | data[p + 4]) : data[p + 3]; if (op == 0xc6) port = 256; }
        }
        return new(op, p, length, type, instance, port, address, value);
    }
    /// <summary>データブロック種別（0x80以降）から、ROM・RAMの書込み先となる音源チップ種別とバンクを求める。</summary>
    internal static (int Type, uint Bank) MemoryTarget(byte kind) => kind switch
    {
        0x80 => (4, 0),
        0x81 => (7, 2),
        0x82 => (8, 1),
        0x83 => (8, 2),
        0x84 or 0x87 => (13, 3),
        0x85 => (14, 0),
        0x86 => (15, 0),
        0x88 => (11, 2),
        0x89 => (21, 0),
        0x8a => (22, 0),
        0x8b => (24, 0),
        0x8c => (26, 0),
        0x8d => (28, 0),
        0x8e => (29, 0),
        0x8f => (31, 0),
        0x90 => (37, 0),
        0x91 => (38, 0),
        0x92 => (39, 0),
        0x93 => (40, 0),
        0x94 => (42, 0),
        0x95 => (46, 0),
        0x96 => (47, 0),
        0xc3 => (43, 0),
        0xc0 => (5, 0),
        0xc1 => (16, 0),
        0xc2 => (20, 0),
        0xe0 => (32, 0),
        0xe1 => (36, 0),
        _ => (-1, 0)
    };
    /// <summary>PCM RAM転送（0x68）のバンク種別から音源チップ種別を求める。</summary>
    internal static int RamType(int bank) => bank switch { 1 => 5, 2 => 16, 6 => 32, 7 => 20, _ => -1 };
    internal static uint U24(byte[] data, int p) => (uint)(data[p] | data[p + 1] << 8 | data[p + 2] << 16);
}
