using System.Buffers.Binary;
using System.IO;
using System.Text.RegularExpressions;

namespace MirrorFlux.Services;

/// <summary>
/// RAR の分割書庫のボリュームを探し、RAR4 の分割書庫を1つの書庫につなぐ。
/// </summary>
internal static class RarVolumes
{
    /// <summary>
    /// 同じ書庫に属するボリュームを順番どおりに返す。「名前.partN.rar」と「名前.rar, .r00, .r01…」の両方の形に対応し、欠けや重複があれば例外にする。
    /// </summary>
    internal static string[] Find(string input)
    {
        // 「.partN.rar」の形なら、同じ接頭辞の全パートを番号順に集める。
        string path = Path.GetFullPath(input), directory = Path.GetDirectoryName(path)!;
        var match = Regex.Match(Path.GetFileName(path), @"^(.*)\.part(\d+)\.rar$", RegexOptions.IgnoreCase);
        if (match.Success)
        {
            string prefix = match.Groups[1].Value;
            var parts = Directory.EnumerateFiles(directory).Select(p => (Path: p,
                Match: Regex.Match(Path.GetFileName(p), "^" + Regex.Escape(prefix) + @"\.part(\d+)\.rar$", RegexOptions.IgnoreCase)))
                .Where(x => x.Match.Success).Select(x => (x.Path, Index: int.Parse(x.Match.Groups[1].Value)))
                .OrderBy(x => x.Index).ToArray();
            for (int i = 0; i < parts.Length; i++)
                if (parts[i].Index != i + 1) throw new InvalidDataException("RAR volume is missing or duplicated");
            return parts.Select(x => x.Path).ToArray();
        }
        // それ以外は「.rar」の後に「.r00」「.r01」…を続ける。
        var result = new List<string> { path };
        string stem = Path.Combine(directory, Path.GetFileNameWithoutExtension(path));
        var numbered = Directory.EnumerateFiles(directory).Select(p => (Path: p,
            Match: Regex.Match(p, "^" + Regex.Escape(stem) + @"\.r(\d\d)$", RegexOptions.IgnoreCase)))
            .Where(x => x.Match.Success).Select(x => (x.Path, Index: int.Parse(x.Match.Groups[1].Value))).OrderBy(x => x.Index).ToArray();
        for (int i = 0; i < numbered.Length; i++)
        {
            if (numbered[i].Index != i) throw new InvalidDataException("RAR volume is missing");
            result.Add(numbered[i].Path);
        }
        return result.ToArray();
    }

    /// <summary>
    /// RAR4 の分割書庫は、1つの圧縮データをボリュームのヘッダーで区切っている。展開も原本の変更もせずに区切りを外し、
    /// 一時フォルダーに1つの書庫としてつないで、そのパスを返す。ソリッドの印と各ファイルの最終的な CRC はそのまま保つ。
    /// </summary>
    internal static string JoinRar4(IReadOnlyList<string> paths)
    {
        string target = Path.Combine(Path.GetTempPath(), "mf-rar-volume-" + Guid.NewGuid().ToString("N") + ".rar");
        try
        {
            // 一時ファイルに RAR4 の署名を書く。
            using var output = new FileStream(target, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
            output.Write("Rar!\x1a\x07\x00"u8);
            byte[]? pending = null, pendingName = null;
            long headerPosition = 0, packedTotal = 0;
            bool mainWritten = false;
            byte[] buffer = new byte[65536];
            foreach (string path in paths)
            {
                // 各ボリュームが RAR4 であることを確かめる。
                using var input = File.OpenRead(path);
                byte[] signature = new byte[7]; input.ReadExactly(signature);
                if (!signature.AsSpan().SequenceEqual("Rar!\x1a\x07\x00"u8)) throw new InvalidDataException("RAR volumes have different formats");
                while (input.Position < input.Length)
                {
                    // ヘッダーを読み、CRC とデータの長さ（大きなファイルは上位32ビットも）を確かめる。
                    byte[] prefix = new byte[7]; input.ReadExactly(prefix);
                    int type = prefix[2], flags = U16(prefix, 3), size = U16(prefix, 5);
                    if (size < 7) throw new InvalidDataException("Invalid RAR header size");
                    byte[] header = new byte[size]; prefix.CopyTo(header, 0); input.ReadExactly(header.AsSpan(7));
                    if ((Crc32(header.AsSpan(2)) & 0xffff) != U16(header, 0)) throw new InvalidDataException("RAR header CRC mismatch");
                    long dataSize = (flags & 0x8000) != 0 || type == 0x74 ? (size >= 11 ? U32(header, 7) : throw new InvalidDataException("RAR header truncated")) : 0;
                    if (type == 0x74 && (flags & 0x100) != 0)
                    {
                        if (size < 40) throw new InvalidDataException("RAR large-file header truncated");
                        dataSize |= (long)U32(header, 32) << 32;
                    }
                    if (dataSize < 0 || dataSize > input.Length - input.Position) throw new InvalidDataException("RAR volume is truncated");
                    // 最初のメインヘッダー（0x73）だけを、分割の印を外して書く。暗号化された書庫は扱わない。
                    if (type == 0x73 && !mainWritten)
                    {
                        if ((flags & 0x80) != 0) throw new InvalidDataException("Encrypted RAR is not supported");
                        Set16(header, 3, flags & ~0x101); FixCrc(header); output.Write(header); mainWritten = true;
                    }
                    // ファイルヘッダー（0x74）。最初の断片なら大きなファイル用の形に広げたヘッダーを先に書き、
                    // 続きの断片なら同じファイルの続きであることを確かめる。
                    if (type == 0x74)
                    {
                        if (!mainWritten || size < 32) throw new InvalidDataException("Invalid RAR file header");
                        int nameStart = (flags & 0x100) != 0 ? 40 : 32, nameLength = U16(header, 26);
                        if (nameLength > size - nameStart) throw new InvalidDataException("RAR filename exceeds header");
                        byte[] name = header.AsSpan(nameStart, nameLength).ToArray();
                        if ((flags & 1) == 0)
                        {
                            if (pending != null) throw new InvalidDataException("RAR continuation is missing");
                            pending = (flags & 0x100) != 0 ? (byte[])header.Clone() : Promote(header);
                            pendingName = name; packedTotal = 0; headerPosition = output.Position; output.Write(pending);
                        }
                        else if (pending == null || !name.AsSpan().SequenceEqual(pendingName) || header[25] != pending[25])
                            throw new InvalidDataException("RAR volumes do not form a matching file");
                        // 圧縮データの断片をそのまま写す。
                        long left = dataSize;
                        while (left != 0)
                        {
                            int count = (int)Math.Min(left, buffer.Length); input.ReadExactly(buffer.AsSpan(0, count)); output.Write(buffer, 0, count); left -= count;
                        }
                        packedTotal = checked(packedTotal + dataSize);
                        // 最後の断片なら、先に書いたヘッダーへ分割の印を外した値・合計の圧縮サイズ・最終 CRC を書き戻す。
                        if ((flags & 2) == 0)
                        {
                            Set16(pending!, 3, U16(pending!, 3) & ~3);
                            Set32(pending!, 7, (uint)packedTotal); Set32(pending!, 32, (uint)(packedTotal >> 32));
                            Set32(pending!, 16, U32(header, 16)); FixCrc(pending!);
                            long end = output.Position; output.Position = headerPosition; output.Write(pending!); output.Position = end;
                            pending = null; pendingName = null;
                        }
                    }
                    else input.Position += dataSize;
                }
            }
            // 途中で終わっていないか確かめ、終端のヘッダーを書く。
            if (pending != null || !mainWritten) throw new InvalidDataException("RAR volume set is incomplete");
            byte[] finish = [0, 0, 0x7b, 0, 0, 7, 0]; FixCrc(finish); output.Write(finish);
            return target;
        }
        catch { File.Delete(target); throw; }
    }
    /// <summary>
    /// ファイルヘッダーに上位32ビットのサイズ欄（8バイト）を加え、大きなファイル用の形に広げる。
    /// </summary>
    private static byte[] Promote(byte[] header)
    {
        if (header.Length > ushort.MaxValue - 8) throw new InvalidDataException("RAR header cannot be expanded");
        byte[] expanded = new byte[header.Length + 8]; header.AsSpan(0, 32).CopyTo(expanded); header.AsSpan(32).CopyTo(expanded.AsSpan(40));
        Set16(expanded, 3, U16(header, 3) | 0x100); Set16(expanded, 5, expanded.Length); return expanded;
    }
    private static int U16(byte[] b, int o) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o));
    private static uint U32(byte[] b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o));
    private static void Set16(byte[] b, int o, int v) => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o), checked((ushort)v));
    private static void Set32(byte[] b, int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o), v);
    private static void FixCrc(byte[] b) => Set16(b, 0, (int)(Crc32(b.AsSpan(2)) & 0xffff));
    /// <summary>
    /// CRC-32 を計算する。RAR4 のヘッダー CRC はこの下位16ビットを使う。
    /// </summary>
    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        uint crc = uint.MaxValue;
        foreach (byte value in bytes) { crc ^= value; for (int i = 0; i < 8; i++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0u); }
        return ~crc;
    }
}
