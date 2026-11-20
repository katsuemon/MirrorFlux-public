using System.IO;
using System.IO.Compression;
using Hst.Compression.Lha;
using SharpCompress.Archives;

namespace MirrorFlux.Services;

public static partial class ArchiveService
{
    /// <summary>
    /// 読み取り用に開いたアーカイブの1項目。Key は「\」区切りで前後の区切りを除いた名前、Name はその最後の部分。
    /// Open はアーカイブを開いている間（visit の中）だけ使える。
    /// </summary>
    private readonly record struct ArchiveReadEntry(string Key, bool IsDirectory, Func<Stream> Open)
    {
        public string Name => Path.GetFileName(Key);
        public string Parent => Path.GetDirectoryName(Key) ?? string.Empty;
    }

    /// <summary>追加・削除・フォルダー作成・新規作成に対応する形式。それ以外は展開専用。</summary>
    internal static readonly string[] WritableExtensions = [".zip", ".7z", ".lzh", ".lha", ".tar"];

    /// <summary>拡張子（"." 付き）が LZH / LHA か。</summary>
    internal static bool IsLhaExtension(string? extension) =>
        string.Equals(extension, ".lzh", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(extension, ".lha", StringComparison.OrdinalIgnoreCase);

    /// <summary>アーカイブ内の名前を「\」区切りにし、前後の区切りを除く。</summary>
    internal static string WindowsArchiveKey(string? key) => (key ?? string.Empty).Replace('/', '\\').Trim('\\');

    /// <summary>LZH の項目がフォルダーか。属性・圧縮方式「-lhd-」・末尾の区切りのどれかで判断する。</summary>
    internal static bool IsLhaDirectory(LzHeader header) =>
        (header.Attribute & 0x10) != 0 ||
        string.Equals(header.Method, "-lhd-", StringComparison.OrdinalIgnoreCase) ||
        (header.Name != null && (header.Name.EndsWith('/') || header.Name.EndsWith('\\')));

    /// <summary>
    /// アーカイブの項目を順に visit へ渡し、false が返ったら打ち切る。
    /// ZIP は標準ライブラリ、LZH/LHA は LhaSharp で読み、読めなければ SharpCompress で最初から読み直す。
    /// そのため同じ項目をもう一度渡すことがある。SharpCompress でも読めなければ例外を投げる。
    /// LZH の項目は Open のたびに全体をメモリへ展開する。
    /// </summary>
    private static void ReadArchiveEntries(string archiveFilePath, Func<ArchiveReadEntry, bool> visit)
    {
        // ZIP は標準ライブラリで読む。ファイル名の文字コードは ZipNameEncoding で判断する。
        string ext = Path.GetExtension(archiveFilePath);
        if (string.Equals(ext, ".zip", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var fs = File.OpenRead(archiveFilePath);
                using var zip = new ZipArchive(fs, ZipArchiveMode.Read, false, ZipNameEncoding.Instance);
                foreach (var entry in zip.Entries)
                    if (!visit(new(WindowsArchiveKey(entry.FullName), string.IsNullOrEmpty(entry.Name), entry.Open)))
                        return;
                return;
            }
            catch { }
        }
        // LZH/LHA は LhaSharp で読み、項目を開くたびにメモリへ展開する。
        else if (IsLhaExtension(ext))
        {
            try
            {
                using var fs = File.OpenRead(archiveFilePath);
                using var archive = new LhaArchive(fs, new LhaOptions { Encoding = GetLhaEncoding() });
                foreach (var header in GetLhaHeadersSync(archive))
                {
                    var current = header;
                    if (!visit(new(WindowsArchiveKey(current.Name), IsLhaDirectory(current), () =>
                    {
                        var buffer = new MemoryStream();
                        archive.Extract(current, buffer);
                        buffer.Position = 0;
                        return buffer;
                    })))
                        return;
                }
                return;
            }
            catch { }
        }

        // ほかの形式と、上で読めなかった ZIP・LZH は SharpCompress で読み直す。
        using var sharp = RarArchiveAdapter.Open(archiveFilePath, GetReaderOptions(archiveFilePath));
        foreach (var entry in sharp.Entries)
            if (!visit(new(WindowsArchiveKey(entry.Key), entry.IsDirectory, entry.OpenEntryStream)))
                return;
    }
}
