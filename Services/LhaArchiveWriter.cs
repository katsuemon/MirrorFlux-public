using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace MirrorFlux.Services;

/// <summary>
/// LZH（LHA）アーカイブを書き出す。ヘッダーは公開されている LHA 形式のレベル2ヘッダーに従って自前で作り、
/// 圧縮データは BSD-2-Clause の LhASsA の LH5 エンコーダー（mf_lha.dll）で作る。Amiga.FileFormats のコードは使っていない。
/// </summary>
internal static class LhaArchiveWriter
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CompressDelegate(byte[] input, uint length, byte[] output, ref uint capacity);
    private static readonly Lazy<CompressDelegate> Compress = new(() => Marshal.GetDelegateForFunctionPointer<CompressDelegate>(
        NativeLibrary.GetExport(NativeAssetLibrary.Load("Lha", "mf_lha.dll"), "mf_lha_compress")));
    private static readonly Encoding Names = CreateEncoding();
    /// <summary>
    /// ファイル名に使う CP932 を用意する。表せない文字は置き換えずに例外にする。
    /// </summary>
    private static Encoding CreateEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }
    /// <summary>
    /// 名前と中身の組を LZH として path に書く。times は項目ごとの更新日時で、指定のない項目だけ現在時刻で書く。
    /// </summary>
    internal static void Write(string path, IReadOnlyDictionary<string, byte[]> files, CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, DateTime>? times = null)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        foreach (var (name, data) in files)
        {
            // 名前を検証し、ファイル名とフォルダー部分（0xFF 区切り）を CP932 のバイト列にする。
            cancellationToken.ThrowIfCancellationRequested();
            string normalized = name.Replace('\\', '/');
            string[] parts = normalized.Split('/');
            if (parts.Any(p => p is "" or "." or ".." || p.Contains(':') || p.Contains('\0')))
                throw new IOException("LHA内のファイル名が不正です: " + name);
            byte[] fileName, directory;
            try
            {
                fileName = Names.GetBytes(parts[^1]);
                using var directories = new MemoryStream();
                foreach (string part in parts[..^1]) { directories.Write(Names.GetBytes(part)); directories.WriteByte(0xff); }
                directory = directories.ToArray();
            }
            catch (EncoderFallbackException ex) { throw new IOException("LHAのファイル名はCP932で表現できる文字にしてください: " + name, ex); }
            if (34L + fileName.Length + (directory.Length == 0 ? 0 : 3L + directory.Length) >= ushort.MaxValue)
                throw new IOException("LHAのファイル名が長すぎます: " + name);
            // LH5 で圧縮し、元より小さくならなければ無圧縮（-lh0-）で格納する。
            byte[] packed = data;
            bool compressed = false;
            if (data.Length > 0)
            {
                byte[] candidate = new byte[data.Length];
                uint size = (uint)candidate.Length;
                int status = Compress.Value(data, (uint)data.Length, candidate, ref size);
                // 無圧縮で格納した方が小さいとき、エンコーダーは UNSUPPORTED（9）を返す。
                if (status == 0 && size > 0 && size < data.Length) { packed = candidate[..(int)size]; compressed = true; }
                else if (status != 9) throw new IOException($"LHA圧縮に失敗しました (status {status}): {name}");
            }
            cancellationToken.ThrowIfCancellationRequested();
            // レベル2ヘッダーを組み立てる。
            using var header = new MemoryStream();
            using (var writer = new BinaryWriter(header, Encoding.UTF8, true))
            {
                writer.Write((ushort)0);
                writer.Write(Encoding.ASCII.GetBytes(compressed ? "-lh5-" : "-lh0-"));
                writer.Write((uint)packed.Length); writer.Write((uint)data.Length);
                writer.Write(UnixSeconds(times != null && times.TryGetValue(name, out var stamp) ? stamp : null, now));
                writer.Write((byte)0x20); writer.Write((byte)2); writer.Write(Crc(data)); writer.Write((byte)'M');
                void Extension(byte type, byte[] bytes)
                {
                    writer.Write(checked((ushort)(bytes.Length + 3))); writer.Write(type); writer.Write(bytes);
                }
                // ヘッダー長の下位バイトが0になるときは、末尾を埋めずに省略可能な情報バイトを1つ足して避ける。
                // 古い読み取り側は下位バイト0を終端とみなし、Hst は埋め草を拒否するため。
                int length = 34 + fileName.Length + (directory.Length == 0 ? 0 : 3 + directory.Length);
                Extension(0, length % 256 == 0 ? [0, 0, 0] : [0, 0]);
                Extension(1, fileName);
                if (directory.Length > 0) Extension(2, directory);
                writer.Write((ushort)0);
            }
            // ヘッダー長と、オフセット 27 のヘッダー CRC を書き込んでから、ヘッダーと圧縮データを出力する。
            if (header.Length > ushort.MaxValue) throw new IOException("LHAのファイル名が長すぎます: " + name);
            byte[] bytes = header.ToArray();
            BinaryPrimitives.WriteUInt16LittleEndian(bytes, (ushort)bytes.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(27), Crc(bytes));
            output.Write(bytes); output.Write(packed);
        }
        // アーカイブの終端を書く。
        cancellationToken.ThrowIfCancellationRequested();
        output.WriteByte(0);
    }
    /// <summary>
    /// 更新日時を Unix 時刻の秒にする。レベル2ヘッダーは符号なし32ビットで持つため、範囲外や指定なしは現在時刻にする。
    /// </summary>
    private static uint UnixSeconds(DateTime? time, long now)
    {
        if (time is not { } value || value == DateTime.MinValue) return (uint)now;
        DateTime utc = value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Local).ToUniversalTime();
        long seconds = new DateTimeOffset(utc).ToUnixTimeSeconds();
        return seconds is > 0 and <= uint.MaxValue ? (uint)seconds : (uint)now;
    }
    /// <summary>
    /// LHA で使う CRC-16（多項式 0xA001）を計算する。
    /// </summary>
    private static ushort Crc(ReadOnlySpan<byte> bytes)
    {
        ushort crc = 0;
        foreach (byte value in bytes)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++) crc = (ushort)((crc >> 1) ^ ((crc & 1) != 0 ? 0xa001 : 0));
        }
        return crc;
    }
}
