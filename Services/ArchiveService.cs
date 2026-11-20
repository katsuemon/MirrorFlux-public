using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Hst.Compression.Lha;
using MirrorFlux.Models;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;
using SharpCompress.Writers.SevenZip;
using SharpCompress.Writers.Tar;

namespace MirrorFlux.Services;

/// <summary>
/// アーカイブをフォルダーのように扱うための操作をまとめる。パスの判定、一覧の読み取り、閲覧用の一時ファイル、
/// 追加・削除・フォルダー作成・名前変更を担う。書き込みは一時ファイルで作り直してから原本と置き換える。
/// </summary>
public static partial class ArchiveService
{
    /// <summary>
    /// SharpCompress の読み取り設定を返す。ZIP は ZipNameEncoding で名前の文字コードを判断する。
    /// 明示された UTF-8 フラグや Unicode 名のフィールドは SharpCompress 側でそのまま優先される。
    /// </summary>
    internal static ReaderOptions GetReaderOptions(string path) =>
        string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase)
            ? new ReaderOptions { ArchiveEncoding = new ArchiveEncoding { Default = ZipNameEncoding.Instance } }
            : new ReaderOptions();

    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip",
        ".7z",
        ".rar",
        ".lzh",
        ".lha",
        ".tar",
        ".tgz",
        ".gz",
        ".bz2",
        ".xz",
        ".arj"
    };

    /// <summary>
    /// 拡張子がアーカイブとして開ける形式か。先頭の「.」は省略してよい。
    /// </summary>
    public static bool IsSupportedArchiveExtension(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension)) return false;
        if (!extension.StartsWith('.')) extension = "." + extension;
        return SupportedExtensions.Contains(extension);
    }

    /// <summary>
    /// 対応する拡張子の実在するファイルか。
    /// </summary>
    public static bool IsSupportedArchive(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var ext = Path.GetExtension(path);
        return IsSupportedArchiveExtension(ext) && File.Exists(path);
    }

    /// <summary>
    /// パスが指すアーカイブへ書き込めるか。書き込めないときは reason に理由を入れる。
    /// </summary>
    public static bool CanModifyArchive(string path, out string? reason)
    {
        reason = null;
        if (!TrySplitArchivePath(path, out var archiveFilePath, out _))
        {
            reason = "アーカイブパスではありません";
            return false;
        }

        // RAR はライセンス上、書き込みに対応しない。
        var ext = Path.GetExtension(archiveFilePath);
        if (string.Equals(ext, ".rar", StringComparison.OrdinalIgnoreCase))
        {
            reason = "RAR形式はライセンス上書き込みに対応していません (ZIP/7Z等をご利用ください)";
            return false;
        }

        // ARJ・BZ2・XZ は展開専用。
        if (string.Equals(ext, ".arj", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(ext, ".bz2", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(ext, ".xz", StringComparison.OrdinalIgnoreCase))
        {
            reason = $"{ext.ToUpperInvariant()}形式は現在展開専用です (ZIP/7Z/LZHをご利用ください)";
            return false;
        }

        // 書き込み対応の形式だけ許可する。
        if (WritableExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
            return true;
        reason = $"{ext.ToUpperInvariant()}形式は現在展開専用です (ZIP/7Z/LZH/TARをご利用ください)";
        return false;
    }

    /// <summary>
    /// パスをアーカイブファイルのパスとアーカイブ内のパスに分ける。アーカイブに関係しないパスなら false を返す。
    /// </summary>
    public static bool TrySplitArchivePath(string path, out string archiveFilePath, out string internalPath)
    {
        archiveFilePath = string.Empty;
        internalPath = string.Empty;

        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            var normalized = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            // パスそのものがアーカイブファイルなら、アーカイブのルートとして扱う。
            if (File.Exists(normalized))
            {
                var ext = Path.GetExtension(normalized);
                if (IsSupportedArchiveExtension(ext))
                {
                    archiveFilePath = normalized;
                    internalPath = string.Empty;
                    return true;
                }
            }

            // 親をさかのぼり、途中にアーカイブファイルがあれば残りをアーカイブ内のパスとする。
            var curr = normalized;
            while (!string.IsNullOrEmpty(curr))
            {
                var parent = Path.GetDirectoryName(curr);
                if (string.IsNullOrEmpty(parent))
                {
                    break;
                }

                if (File.Exists(parent))
                {
                    var ext = Path.GetExtension(parent);
                    if (IsSupportedArchiveExtension(ext))
                    {
                        archiveFilePath = parent;
                        internalPath = normalized.Substring(parent.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                        return true;
                    }
                }

                curr = parent;
            }
        }
        catch
        {
            // パスの書式が不正なときはアーカイブとして扱わない。
        }

        return false;
    }

    public static bool IsInsideArchive(string path)
    {
        return TrySplitArchivePath(path, out _, out _);
    }

    /// <summary>
    /// アーカイブ内の項目を指すパスか。アーカイブファイル自身は含めない。
    /// </summary>
    public static bool IsArchiveEntry(string path)
    {
        return TrySplitArchivePath(path, out _, out var internalPath) && !string.IsNullOrEmpty(internalPath);
    }

    /// <summary>
    /// 親フォルダーのパスを返す。アーカイブ内では、アーカイブ内の親フォルダー、アーカイブのルート、アーカイブを置いたフォルダーの順にさかのぼる。
    /// </summary>
    public static string GetParentPath(string path)
    {
        if (TrySplitArchivePath(path, out var archiveFilePath, out var internalPath))
        {
            if (string.IsNullOrEmpty(internalPath))
            {
                // アーカイブのルートにいる場合、親はアーカイブを置いた実フォルダー。
                return Path.GetDirectoryName(archiveFilePath) ?? archiveFilePath;
            }

            var parentInternal = Path.GetDirectoryName(internalPath);
            if (string.IsNullOrEmpty(parentInternal))
            {
                // アーカイブ直下のフォルダーにいる場合、親はアーカイブのルート。
                return archiveFilePath;
            }

            return Path.Combine(archiveFilePath, parentInternal);
        }

        return Directory.GetParent(path)?.FullName ?? path;
    }

    /// <summary>
    /// アーカイブ内のパスなら「[ZIP]」のような形式の表示を返す。
    /// </summary>
    public static string GetArchiveTag(string path)
    {
        if (TrySplitArchivePath(path, out var archiveFilePath, out _))
        {
            var ext = Path.GetExtension(archiveFilePath).TrimStart('.').ToUpperInvariant();
            return $"[{ext}]";
        }
        return string.Empty;
    }

    /// <summary>
    /// アーカイブ内のパスなら、アーカイブのファイル名とサイズの表示を返す。
    /// </summary>
    public static string GetArchiveInfoText(string path)
    {
        if (TrySplitArchivePath(path, out var archiveFilePath, out _))
        {
            try
            {
                var fi = new FileInfo(archiveFilePath);
                if (fi.Exists)
                {
                    return $"{fi.Name} ({FileEntry.FormatFileSize(fi.Length)})";
                }
            }
            catch
            {
            }
        }
        return string.Empty;
    }

    /// <summary>
    /// アーカイブの項目の名前・種類・サイズ・日時。
    /// </summary>
    private sealed record ArchiveItemRecord(string Key, bool IsDirectory, long Size, DateTime LastWriteTime);

    /// <summary>
    /// アーカイブ内の1フォルダー分をファイル一覧の項目として返す。先頭に親フォルダー「..」を置く。
    /// </summary>
    public static IReadOnlyList<FileEntry> ReadArchiveEntries(string archiveFilePath, string internalPath, bool showHidden = false)
    {
        var entries = new List<FileEntry>();

        // 親フォルダー「..」を作る。
        var parentPath = GetParentPath(string.IsNullOrEmpty(internalPath) ? archiveFilePath : Path.Combine(archiveFilePath, internalPath));
        entries.Add(new FileEntry(parentPath, "..", isDirectory: true, isParent: true, length: 0, lastWriteTime: DateTime.MinValue));

        // アーカイブの全項目を読み、表示するフォルダーの接頭辞を決める。
        var items = LoadArchiveItemRecords(archiveFilePath);

        var normalizedInternal = WindowsArchiveKey(internalPath);
        var internalPrefix = string.IsNullOrEmpty(normalizedInternal) ? string.Empty : normalizedInternal + "\\";

        var seenDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // 大文字・小文字だけが違うファイルは別の項目として表示する。
        var seenFiles = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in items)
        {
            // 表示するフォルダーより下の項目だけを取り出し、そこからの相対名にする。
            var key = WindowsArchiveKey(item.Key);
            if (string.IsNullOrEmpty(key)) continue;

            string rel;
            if (string.IsNullOrEmpty(internalPrefix))
            {
                rel = key;
            }
            else
            {
                if (!key.StartsWith(internalPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                rel = key.Substring(internalPrefix.Length);
            }

            if (string.IsNullOrEmpty(rel)) continue;

            int slashIdx = rel.IndexOf('\\');
            if (slashIdx >= 0)
            {
                // さらに下の階層の項目は、直下のフォルダーとして1回だけ加える。
                var dirName = rel.Substring(0, slashIdx);
                if (seenDirs.Add(dirName))
                {
                    var dirFullPath = string.IsNullOrEmpty(normalizedInternal)
                        ? Path.Combine(archiveFilePath, dirName)
                        : Path.Combine(archiveFilePath, normalizedInternal, dirName);
                    entries.Add(new FileEntry(dirFullPath, dirName, isDirectory: true, isParent: false, length: 0, lastWriteTime: item.LastWriteTime));
                }
            }
            else
            {
                // 直下の項目はフォルダーとファイルに分けて加える。
                if (item.IsDirectory)
                {
                    if (seenDirs.Add(rel))
                    {
                        var dirFullPath = string.IsNullOrEmpty(normalizedInternal)
                            ? Path.Combine(archiveFilePath, rel)
                            : Path.Combine(archiveFilePath, normalizedInternal, rel);
                        entries.Add(new FileEntry(dirFullPath, rel, isDirectory: true, isParent: false, length: 0, lastWriteTime: item.LastWriteTime));
                    }
                }
                else
                {
                    if (seenFiles.Add(rel))
                    {
                        var fileFullPath = string.IsNullOrEmpty(normalizedInternal)
                            ? Path.Combine(archiveFilePath, rel)
                            : Path.Combine(archiveFilePath, normalizedInternal, rel);
                        entries.Add(new FileEntry(fileFullPath, rel, isDirectory: false, isParent: false, length: item.Size, lastWriteTime: item.LastWriteTime));
                    }
                }
            }
        }

        return entries;
    }

    /// <summary>
    /// LZH のファイル名に使う文字コード。CP932（Shift_JIS）を使い、取得できなければ UTF-8 にする。
    /// </summary>
    internal static Encoding GetLhaEncoding()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(932);
        }
        catch
        {
            return Encoding.UTF8;
        }
    }

    /// <summary>
    /// LHA リーダーは Unix 時刻を UTC で返す。一覧・同名判定・書き戻しはほかの形式と同じローカル時刻で扱うため変換する。
    /// </summary>
    internal static DateTime LhaLocalTime(DateTime time) => time.Kind == DateTimeKind.Utc ? time.ToLocalTime() : time;

    /// <summary>
    /// LZH から読んだ1項目。書き直しのために中身も持つ。
    /// </summary>
    private sealed record LhaItem(string Key, byte[] Data, long Size, DateTime Time, bool IsDirectory = false);

    /// <summary>LZH を書き直すときの内容と更新日時。名前は大文字・小文字を区別して保持する。</summary>
    private sealed class LhaRewrite
    {
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, DateTime> Times { get; } = new(StringComparer.Ordinal);
        /// <summary>
        /// 中身と日時を登録する。日時がなければ書き込み時の既定にする。
        /// </summary>
        public void Set(string key, byte[] data, DateTime? time)
        {
            Files[key] = data;
            if (time is { } stamp) Times[key] = stamp; else Times.Remove(key);
        }
    }

    /// <summary>
    /// 残す項目を日時付きで引き継ぐ。フォルダー項目は、配下にファイルがなければ空フォルダーの目印「.empty」に置き換える。
    /// </summary>
    private static LhaRewrite KeepLhaItems(IEnumerable<LhaItem> items, Func<LhaItem, bool> keep)
    {
        // 先にファイルを登録し、その後でファイルを持たないフォルダーだけ目印を加える。
        var kept = items.Where(keep).ToList();
        var rewrite = new LhaRewrite();
        foreach (var file in kept.Where(item => !item.IsDirectory))
            rewrite.Set(NormalizeArchiveKey(file.Key), file.Data, file.Time);
        foreach (var directory in kept.Where(item => item.IsDirectory))
        {
            string key = NormalizeArchiveKey(directory.Key);
            if (key.Length > 0 && !rewrite.Files.Keys.Any(name => name.StartsWith(key + "/", StringComparison.OrdinalIgnoreCase)))
                rewrite.Set(key + "/.empty", Array.Empty<byte>(), directory.Time);
        }
        return rewrite;
    }

    /// <summary>
    /// LZH の項目ヘッダーを同期的に取り出す。UI スレッドなど同期コンテキストがある場所では、待ち合わせで固まらないよう別スレッドで読む。
    /// </summary>
    internal static IEnumerable<LzHeader> GetLhaHeadersSync(LhaArchive archive)
    {
        if (SynchronizationContext.Current == null)
            return archive.Entries().GetAwaiter().GetResult();
        return Task.Run(async () => await archive.Entries().ConfigureAwait(false)).GetAwaiter().GetResult();
    }

    /// <summary>
    /// LZH の全項目を読む。readData が true ならファイルの中身もメモリへ展開する。
    /// </summary>
    private static List<LhaItem> ReadAllLhaItems(string archiveFilePath, bool readData = true)
    {
        var items = new List<LhaItem>();
        if (!File.Exists(archiveFilePath)) return items;

        // ファイル名は CP932 で読む。日時がない項目は現在時刻にする。
        var lhaOptions = new LhaOptions { Encoding = GetLhaEncoding() };
        using var fs = File.OpenRead(archiveFilePath);
        using var archive = new LhaArchive(fs, lhaOptions);
        var headers = GetLhaHeadersSync(archive);
        foreach (var h in headers)
        {
            var key = (h.Name ?? string.Empty).Replace('\\', '/');
            var time = h.UnixLastModifiedStamp != DateTime.MinValue ? LhaLocalTime(h.UnixLastModifiedStamp) : DateTime.Now;
            byte[] data = Array.Empty<byte>();
            bool isDir = IsLhaDirectory(h);
            if (readData && !isDir)
            {
                using var ms = new MemoryStream();
                archive.Extract(h, ms);
                data = ms.ToArray();
            }
            items.Add(new LhaItem(key, data, h.OriginalSize, time, isDir));
        }
        return items;
    }

    /// <summary>
    /// アーカイブの全項目の名前・種類・サイズ・日時を読む。LZH は LhaSharp、それ以外は SharpCompress で読む。
    /// </summary>
    private static List<ArchiveItemRecord> LoadArchiveItemRecords(string archiveFilePath)
    {
        var records = new List<ArchiveItemRecord>();
        var ext = Path.GetExtension(archiveFilePath);

        // LZH/LHA はヘッダーから読む。
        if (IsLhaExtension(ext))
        {
            var lhaOptions = new LhaOptions { Encoding = GetLhaEncoding() };
            using var fs = File.OpenRead(archiveFilePath);
            using var archive = new LhaArchive(fs, lhaOptions);
            var headers = GetLhaHeadersSync(archive);
            foreach (var h in headers)
            {
                var isDir = IsLhaDirectory(h);
                var time = h.UnixLastModifiedStamp != DateTime.MinValue ? LhaLocalTime(h.UnixLastModifiedStamp) : DateTime.Now;
                var key = h.Name ?? string.Empty;
                records.Add(new ArchiveItemRecord(key, isDir, h.OriginalSize, time));
            }
        }
        // ほかの形式は SharpCompress で読む。
        else
        {
            using var archive = RarArchiveAdapter.Open(archiveFilePath, GetReaderOptions(archiveFilePath));
            foreach (var entry in archive.Entries)
            {
                var time = entry.LastModifiedTime ?? DateTime.MinValue;
                records.Add(new ArchiveItemRecord(entry.Key ?? string.Empty, entry.IsDirectory, entry.Size, time));
            }
        }

        return records;
    }

    private static readonly string ViewTempRoot = Path.Combine(Path.GetTempPath(), "MirrorFlux", "view_temp");
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> ViewSources = new(StringComparer.OrdinalIgnoreCase);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, List<string>> ArchiveFolderTempImages = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>
    /// 閲覧用の一時ファイルなら、元のアーカイブ内のパスを返す。それ以外はそのまま返す。
    /// </summary>
    public static string GetOriginalViewPath(string path) => ViewSources.TryGetValue(path, out var source) ? source : path;

    /// <summary>
    /// 一時ファイルと同じフォルダーへ一緒に取り出した画像の一時ファイル一覧を返す。
    /// </summary>
    public static IReadOnlyList<string> GetArchiveFolderImageTempPaths(string tempFilePath)
    {
        var dir = Path.GetDirectoryName(tempFilePath);
        if (!string.IsNullOrEmpty(dir) && ArchiveFolderTempImages.TryGetValue(dir, out var list))
        {
            return list;
        }
        return Array.Empty<string>();
    }
    /// <summary>
    /// アーカイブ内のファイルを仮想的なフルパスの一覧で返す。アーカイブの外を指す名前は除く。
    /// </summary>
    public static string[] ReadArchiveFilePaths(string archivePath)
    {
        string prefix = Path.GetFullPath(archivePath).TrimEnd('\\') + "\\";
        return LoadArchiveItemRecords(archivePath).Where(item => !item.IsDirectory)
            .Select(item => Path.GetFullPath(Path.Combine(prefix, item.Key.Replace('/', '\\').TrimStart('\\'))))
            .Where(path => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    /// <summary>
    /// アーカイブ内のファイルを閲覧用の一時ファイルへ取り出し、そのパスを返す。
    /// MDX なら同じフォルダーの PDX も取り出し、画像なら同じフォルダーの画像を左右キーでめくれるよう登録する。
    /// </summary>
    public static async Task<string?> ExtractEntryToTempFileAsync(string virtualPath)
    {
        if (!TrySplitArchivePath(virtualPath, out var archiveFilePath, out var internalPath) ||
            string.IsNullOrEmpty(internalPath))
        {
            return null;
        }

        try
        {
            // 中身を読み出す。
            var bytes = await ExtractArchiveEntryBytesAsync(archiveFilePath, internalPath);
            if (bytes == null)
            {
                return null;
            }

            var fileName = Path.GetFileName(internalPath);
            if (string.IsNullOrEmpty(fileName))
            {
                fileName = "entry.bin";
            }

            // 毎回別の一時フォルダーへ元の名前で書き、元のパスを記録する。
            var dir = Path.Combine(ViewTempRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var tempFilePath = Path.Combine(dir, fileName);
            await File.WriteAllBytesAsync(tempFilePath, bytes);
            ViewSources[tempFilePath] = virtualPath;

            // MDX は曲データが指す PDX（拡張子省略時は「.pdx」付きも探す）を同じフォルダーから取り出して隣に置く。
            string ext = Path.GetExtension(fileName).ToLowerInvariant();
            if (ext == ".mdx")
            {
                string bank = MirrorFlux.Services.Music.PdxResolver.ReadBankName(bytes);
                if (!string.IsNullOrWhiteSpace(bank))
                {
                    string normalized = WindowsArchiveKey(internalPath);
                    string parent = Path.GetDirectoryName(normalized) ?? "";
                    string bankName = Path.GetFileName(bank);
                    string[] names = Path.HasExtension(bankName) ? new[] { bankName } : new[] { bankName, bankName + ".pdx" };
                    var records = await Task.Run(() => LoadArchiveItemRecords(archiveFilePath));
                    var entry = records.FirstOrDefault(item => !item.IsDirectory && names.Any(name =>
                        string.Equals(WindowsArchiveKey(item.Key), Path.Combine(parent, name), StringComparison.OrdinalIgnoreCase)));
                    if (entry != null)
                    {
                        byte[]? pdx = await ExtractArchiveEntryBytesAsync(archiveFilePath, entry.Key);
                        if (pdx != null)
                        {
                            string pdxPath = Path.Combine(dir, Path.GetFileName(entry.Key.Replace('/', '\\')));
                            await File.WriteAllBytesAsync(pdxPath, pdx);
                            ViewSources[pdxPath] = Path.Combine(archiveFilePath, entry.Key);
                        }
                    }
                }
            }

            // 画像なら、同じフォルダーの画像をすべて一時ファイルの予定パスとして登録し、左右キーで連続してめくれるようにする。
            if (MirrorFlux.Controls.GraphicViewerControl.IsSupportedExtension(ext))
            {
                var normInternal = WindowsArchiveKey(internalPath);
                var parentDir = Path.GetDirectoryName(normInternal) ?? string.Empty;
                var items = await Task.Run(() => LoadArchiveItemRecords(archiveFilePath));

                var siblingList = new List<string>();

                foreach (var item in items.Where(i => !i.IsDirectory))
                {
                    var k = WindowsArchiveKey(item.Key);
                    var itemParent = Path.GetDirectoryName(k) ?? string.Empty;
                    if (string.Equals(parentDir, itemParent, StringComparison.OrdinalIgnoreCase))
                    {
                        var itemExt = Path.GetExtension(k).ToLowerInvariant();
                        if (MirrorFlux.Controls.GraphicViewerControl.IsSupportedExtension(itemExt))
                        {
                            var siblingName = Path.GetFileName(k);
                            var siblingTempPath = Path.Combine(dir, siblingName);
                            var siblingVirtual = Path.Combine(archiveFilePath, k);
                            ViewSources[siblingTempPath] = siblingVirtual;
                            siblingList.Add(siblingTempPath);
                        }
                    }
                }

                if (siblingList.Count == 0)
                {
                    siblingList.Add(tempFilePath);
                }

                // 000.jpg, 001.jpg, 1.jpg, 2.jpg, 10.jpg のような自然順に並べる。
                siblingList.Sort(NaturalStringComparer.Instance);
                ArchiveFolderTempImages[dir] = siblingList;

                // アーカイブを1回だけ開き、残りの画像を背景でまとめて取り出す。
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await ExtractAllSiblingImagesAsync(archiveFilePath, parentDir, dir);
                    }
                    catch { }
                });
            }

            return tempFilePath;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 登録済みの閲覧用一時ファイルがまだ書かれていなければ、その場でアーカイブから取り出す。
    /// </summary>
    public static bool EnsureEntryExtracted(string tempFilePath)
    {
        try
        {
            // 中身のある一時ファイルが既にあればそのまま使う。
            if (File.Exists(tempFilePath))
            {
                var fi = new FileInfo(tempFilePath);
                if (fi.Length > 0) return true;
            }

            // 一時ファイルから元のアーカイブ内のパスを引く。
            if (!ViewSources.TryGetValue(tempFilePath, out var virtualPath))
            {
                return false;
            }

            if (!TrySplitArchivePath(virtualPath, out var archiveFilePath, out var internalPath))
            {
                return false;
            }

            // UI スレッドから呼ばれるため、展開中の await が UI へ戻ろうとして固まらないよう別スレッドで実行する。
            var bytes = Task.Run(() => ExtractArchiveEntryBytesAsync(archiveFilePath, internalPath)).GetAwaiter().GetResult();
            if (bytes != null && bytes.Length > 0)
            {
                var dir = Path.GetDirectoryName(tempFilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                PublishViewTempFile(tempFilePath, output => output.Write(bytes));
                return File.Exists(tempFilePath);
            }
        }
        catch { }

        return false;
    }

    /// <summary>
    /// 同じフォルダーの画像を背景でまとめて閲覧用一時ファイルへ書き出す。
    /// </summary>
    private static Task ExtractAllSiblingImagesAsync(string archiveFilePath, string parentDir, string dir) => Task.Run(() =>
    {
        // アーカイブを1回だけ開き、同じフォルダーの画像のうちまだないものを書き出す。
        string normParent = WindowsArchiveKey(parentDir);
        ReadArchiveEntries(archiveFilePath, entry =>
        {
            if (entry.IsDirectory || !string.Equals(normParent, entry.Parent, StringComparison.OrdinalIgnoreCase) ||
                !MirrorFlux.Controls.GraphicViewerControl.IsSupportedExtension(Path.GetExtension(entry.Key).ToLowerInvariant()))
                return true;
            string destPath = Path.Combine(dir, entry.Name);
            if (!File.Exists(destPath))
            {
                using var input = entry.Open();
                PublishViewTempFile(destPath, output => input.CopyTo(output));
            }
            return true;
        });
    });

    /// <summary>
    /// 閲覧用の一時ファイルを書く。背景の一括取り出しと必要時の取り出しが同じファイルを同時に書くことがあるため、
    /// 別名で書き終えてから置く。先に置かれていればそちらを使う。
    /// </summary>
    private static void PublishViewTempFile(string path, Action<Stream> write)
    {
        string part = path + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            using (var output = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536))
                write(output);
            if (!File.Exists(path)) File.Move(part, path);
        }
        catch (IOException) when (File.Exists(path)) { }
        finally { if (File.Exists(part)) File.Delete(part); }
    }

    /// <summary>
    /// 閲覧用の一時ファイルと登録をすべて消す。
    /// </summary>
    public static void CleanupTempFiles()
    {
        ViewSources.Clear();
        ArchiveFolderTempImages.Clear();
        try
        {
            if (Directory.Exists(ViewTempRoot))
            {
                Directory.Delete(ViewTempRoot, true);
            }
        }
        catch { }
    }

    /// <summary>
    /// アーカイブ内の1ファイルの中身を読み出す。見つからなければ空の配列を返す。
    /// </summary>
    public static Task<byte[]> ExtractArchiveEntryBytesAsync(string archiveFilePath, string internalKey) => Task.Run(() =>
    {
        // アーカイブの読み取りは処理を止めることがある（LHA のヘッダー解析は内部で待つ）ため、UI のコンテキストでは実行しない。
        // 大文字・小文字だけ違う項目があるときは、指定した名前と完全に同じ項目を優先して開く。
        string normKey = WindowsArchiveKey(internalKey);
        byte[]? found = null;
        ReadArchiveEntries(archiveFilePath, entry =>
        {
            if (entry.IsDirectory || !string.Equals(entry.Key, normKey, StringComparison.OrdinalIgnoreCase)) return true;
            bool exact = string.Equals(entry.Key, normKey, StringComparison.Ordinal);
            if (found == null || exact)
            {
                using var input = entry.Open();
                using var buffer = new MemoryStream();
                input.CopyTo(buffer);
                found = buffer.ToArray();
            }
            return !exact;
        });
        return found ?? Array.Empty<byte>();
    });

    /// <summary>
    /// 指定した複数の項目について、先頭 maxBytes バイトだけを読み出す。結果は正規化した名前と元の名前の両方で引ける。
    /// </summary>
    public static Dictionary<string, byte[]> ReadArchiveEntryPrefixes(string archiveFilePath, IReadOnlyCollection<string> internalKeys, int maxBytes = 2048)
    {
        var results = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        if (internalKeys.Count == 0 || !File.Exists(archiveFilePath)) return results;

        // 正規化した名前から元の名前を引けるようにする。
        var wantedMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string rawKey in internalKeys)
            wantedMap.TryAdd(WindowsArchiveKey(rawKey), rawKey);

        // アーカイブを1回だけ走査し、すべて見つかったら打ち切る。
        byte[] buf = new byte[maxBytes];
        int foundCount = 0;
        try
        {
            ReadArchiveEntries(archiveFilePath, entry =>
            {
                if (entry.IsDirectory || !wantedMap.TryGetValue(entry.Key, out string? origKey) || results.ContainsKey(entry.Key)) return true;
                using var input = entry.Open();
                int total = 0;
                while (total < maxBytes)
                {
                    int read = input.Read(buf, total, maxBytes - total);
                    if (read <= 0) break;
                    total += read;
                }
                byte[] data = buf.AsSpan(0, total).ToArray();
                results[entry.Key] = data;
                results[origKey] = data;
                return ++foundCount < wantedMap.Count;
            });
        }
        catch { }
        return results;
    }

    /// <summary>
    /// 指定した複数の項目を1回の走査で開き、read で読んだ値を返す（全体を取り出さずに必要な部分だけ読むとき用）。
    /// 結果は正規化した名前と元の名前の両方で引ける。read が null を返した項目は載せない。
    /// </summary>
    public static Dictionary<string, T> ReadArchiveEntryValues<T>(string archiveFilePath, IReadOnlyCollection<string> internalKeys, Func<string, Stream, T?> read)
        where T : class
    {
        var results = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        if (internalKeys.Count == 0 || !File.Exists(archiveFilePath)) return results;
        var wantedMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string rawKey in internalKeys)
            wantedMap.TryAdd(WindowsArchiveKey(rawKey), rawKey);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            ReadArchiveEntries(archiveFilePath, entry =>
            {
                if (entry.IsDirectory || !wantedMap.TryGetValue(entry.Key, out string? origKey) || !seen.Add(entry.Key)) return true;
                using (var input = entry.Open())
                {
                    if (read(origKey, input) is { } value) { results[entry.Key] = value; results[origKey] = value; }
                }
                return seen.Count < wantedMap.Count;
            });
        }
        catch { }
        return results;
    }

    public static Task<(int CopiedCount, int SkippedCount, List<string> SuccessfulPaths)> ExtractEntriesAsync(
        string archiveFilePath, IReadOnlyList<FileEntry> entries, string destinationDir,
        Func<ConflictInfo, Task<ConflictResolution>> conflictResolver, Action<string, string> logAction,
        CancellationToken cancellationToken = default)
        => ArchiveExtractionService.ExtractEntriesAsync(archiveFilePath, entries, destinationDir, conflictResolver, logAction, cancellationToken);

    /// <summary>
    /// アーカイブを同じフォルダーの一時ファイルへ書き、write が true を返したときだけ target として公開する。
    /// 失敗・中止時と false のときは一時ファイルを消し、原本に触れない。overwrite が false なら既存の target は上書きしない。
    /// </summary>
    private static async Task PublishViaTemporaryAsync(string target, Func<string, Task<bool>> write,
        CancellationToken cancellationToken = default, bool overwrite = true)
    {
        string temporary = target + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            bool publish = await write(temporary);
            cancellationToken.ThrowIfCancellationRequested();
            if (publish) File.Move(temporary, target, overwrite);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    /// <summary><see cref="PublishViaTemporaryAsync"/> の同期版。write は同期で完了する。</summary>
    private static void PublishViaTemporary(string target, Func<string, bool> write, CancellationToken cancellationToken = default) =>
        PublishViaTemporaryAsync(target, temporary => Task.FromResult(write(temporary)), cancellationToken).GetAwaiter().GetResult();

    /// <summary>
    /// LZH を一時ファイルへ書き直し、書き終えてから原本と置き換える。
    /// </summary>
    private static void SaveLhaFiles(string archiveFilePath, LhaRewrite rewrite, CancellationToken cancellationToken = default) =>
        PublishViaTemporary(archiveFilePath, temporary =>
        {
            LhaArchiveWriter.Write(temporary, rewrite.Files, cancellationToken, rewrite.Times);
            return true;
        }, cancellationToken);

    /// <summary>ZIP を一時コピー上で更新し、成功時だけ原本と置き換える。変更がなければ原本に触れない。</summary>
    private static void UpdateZipAtomically(string archiveFilePath, Func<System.IO.Compression.ZipArchive, bool> update) =>
        PublishViaTemporary(archiveFilePath, temporary =>
        {
            // 更新モードは失敗時にも途中の変更を保存することがあるので、触るのは一時コピーだけにする。
            using var original = new FileStream(archiveFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                original.CopyTo(output);
            using var stream = new FileStream(temporary, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            using var zip = new System.IO.Compression.ZipArchive(stream, ZipArchiveMode.Update, false, ZipNameEncoding.Instance);
            return update(zip);
        });

    /// <summary>7z を一時ファイルへ書き出し、成功時だけ原本と置き換える。失敗・中止時は一時ファイルを残さない。</summary>
    private static void Replace7zAtomically(string archiveFilePath, Action<SevenZipWriter> write) =>
        PublishViaTemporary(archiveFilePath, temporary =>
        {
            using var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new SevenZipWriter(output, new SevenZipWriterOptions());
            write(writer);
            return true;
        });

    /// <summary>
    /// 7z の項目をフォルダーとして書き戻すか。末尾が「/」の空ファイルとして書かれたフォルダーも既存のアーカイブにあるため、その形もフォルダーとして扱う。
    /// </summary>
    private static bool Is7zDirectoryEntry(string key, bool isDirectory) =>
        isDirectory || key.EndsWith('/') || key.EndsWith('\\');

    /// <summary>
    /// アーカイブ内にフォルダーを作る。形式ごとに書き直してから原本と置き換える。
    /// </summary>
    public static async Task<bool> CreateArchiveDirectoryAsync(
        string archiveFilePath,
        string internalPath,
        string newDirName,
        Action<string, string> logAction)
    {
        var ext = Path.GetExtension(archiveFilePath);
        var normInternal = WindowsArchiveKey(internalPath);
        var targetDirInternal = string.IsNullOrEmpty(normInternal) ? newDirName : $"{normInternal}\\{newDirName}";
        var virtualFullPath = Path.Combine(archiveFilePath, targetDirInternal);

        // ZIP は末尾「/」の項目を加える。既にあれば変更しない。
        if (string.Equals(ext, ".zip", StringComparison.OrdinalIgnoreCase))
        {
            await Task.Run(() => UpdateZipAtomically(archiveFilePath, zip =>
            {
                var entryKey = targetDirInternal.Replace('\\', '/').Trim('/') + "/";
                if (zip.GetEntry(entryKey) != null) return false;
                zip.CreateEntry(entryKey);
                return true;
            }));
        }
        // LZH は空フォルダーの目印「.empty」を加えて書き直す。
        else if (IsLhaExtension(ext))
        {
            await Task.Run(() =>
            {
                var rewrite = KeepLhaItems(ReadAllLhaItems(archiveFilePath, readData: true), _ => true);
                var dirPlaceholder = targetDirInternal.Replace('\\', '/').Trim('/') + "/.empty";
                if (!rewrite.Files.ContainsKey(dirPlaceholder)) rewrite.Set(dirPlaceholder, Array.Empty<byte>(), null);
                SaveLhaFiles(archiveFilePath, rewrite);
            });
        }
        // 7z は既存の項目をすべて写し、最後にフォルダーを加える。
        else if (string.Equals(ext, ".7z", StringComparison.OrdinalIgnoreCase))
        {
            await Task.Run(() => Replace7zAtomically(archiveFilePath, writer =>
            {
                using var existingArchive = RarArchiveAdapter.Open(archiveFilePath, GetReaderOptions(archiveFilePath));
                foreach (var entry in existingArchive.Entries)
                {
                    var key = entry.Key ?? string.Empty;
                    if (Is7zDirectoryEntry(key, entry.IsDirectory))
                    {
                        writer.WriteDirectory(key, entry.LastModifiedTime);
                    }
                    else
                    {
                        using var s = entry.OpenEntryStream();
                        writer.Write(key, s, entry.LastModifiedTime);
                    }
                }
                writer.WriteDirectory(targetDirInternal.Replace('\\', '/').Trim('/'), DateTime.Now);
            }));
        }
        else if (string.Equals(ext, ".tar", StringComparison.OrdinalIgnoreCase))
        {
            await Task.Run(() => CreateTarDirectory(archiveFilePath, targetDirInternal));
        }
        else
        {
            throw new NotSupportedException($"{ext}形式でのディレクトリ作成はサポートされていません");
        }

        logAction(virtualFullPath, "MKDIR");
        return true;
    }

    /// <summary>
    /// アーカイブから選択された項目を削除し、削除した件数を返す。形式ごとに書き直してから原本と置き換える。
    /// </summary>
    public static async Task<int> DeleteArchiveEntriesAsync(
        string archiveFilePath,
        IReadOnlyList<FileEntry> entriesToDelete,
        Action<string, string> logAction)
    {
        var ext = Path.GetExtension(archiveFilePath);
        int deletedCount = 0;

        // ファイルは選んだ1件だけを消すため名前を完全一致で照合する。フォルダーは一覧で統合表示されるため大文字・小文字を区別しない。
        var fileKeysToDelete = new HashSet<string>(StringComparer.Ordinal);
        var keysToDelete = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var prefixesToDelete = new List<string>();
        bool IsDeleteKey(string key, bool isDirectory) => isDirectory ? keysToDelete.Contains(key) : fileKeysToDelete.Contains(key);

        // 選択された項目から、削除する名前とフォルダーの接頭辞（両方の区切り）を集める。
        foreach (var entry in entriesToDelete)
        {
            if (entry.IsParent) continue;
            if (TrySplitArchivePath(entry.FullPath, out _, out var internalPath) && !string.IsNullOrEmpty(internalPath))
            {
                var norm = WindowsArchiveKey(internalPath);
                if (entry.IsDirectory)
                {
                    prefixesToDelete.Add(norm + "\\");
                    prefixesToDelete.Add(norm + "/");
                    keysToDelete.Add(norm);
                    keysToDelete.Add(norm + "/");
                }
                else
                {
                    fileKeysToDelete.Add(norm);
                }
            }
        }

        if (fileKeysToDelete.Count == 0 && keysToDelete.Count == 0 && prefixesToDelete.Count == 0) return 0;

        // ZIP は一時コピー上で該当する項目を消す。
        if (string.Equals(ext, ".zip", StringComparison.OrdinalIgnoreCase))
        {
            await Task.Run(() => UpdateZipAtomically(archiveFilePath, zip =>
            {
                var toRemove = zip.Entries.Where(e =>
                {
                    var k = WindowsArchiveKey(e.FullName);
                    if (IsDeleteKey(k, (e.FullName ?? string.Empty).EndsWith('/'))) return true;
                    if (prefixesToDelete.Any(p => (e.FullName ?? string.Empty).StartsWith(p, StringComparison.OrdinalIgnoreCase) ||
                                                  (e.FullName ?? string.Empty).Replace('/', '\\').StartsWith(p, StringComparison.OrdinalIgnoreCase))) return true;
                    return false;
                }).ToList();

                foreach (var r in toRemove)
                {
                    r.Delete();
                    deletedCount++;
                }
                return toRemove.Count > 0;
            }));
        }
        // LZH は残す項目だけで書き直す。対象がなければ書き直さない。
        else if (IsLhaExtension(ext))
        {
            await Task.Run(() =>
            {
                var existingItems = ReadAllLhaItems(archiveFilePath, readData: true);
                bool ShouldDelete(LhaItem item)
                {
                    var k = WindowsArchiveKey(item.Key);
                    return IsDeleteKey(k, item.IsDirectory) || prefixesToDelete.Any(p => k.StartsWith(p, StringComparison.OrdinalIgnoreCase));
                }
                deletedCount = existingItems.Count(ShouldDelete);
                if (deletedCount > 0) SaveLhaFiles(archiveFilePath, KeepLhaItems(existingItems, item => !ShouldDelete(item)));
            });
        }
        // 7z は削除対象以外の項目を写して書き直す。
        else if (string.Equals(ext, ".7z", StringComparison.OrdinalIgnoreCase))
        {
            await Task.Run(() => Replace7zAtomically(archiveFilePath, writer =>
            {
                using var existingArchive = RarArchiveAdapter.Open(archiveFilePath, GetReaderOptions(archiveFilePath));
                foreach (var entry in existingArchive.Entries)
                {
                    var key = entry.Key ?? string.Empty;
                    var k = WindowsArchiveKey(key);
                    bool shouldDelete = IsDeleteKey(k, Is7zDirectoryEntry(key, entry.IsDirectory)) || prefixesToDelete.Any(p => key.StartsWith(p, StringComparison.OrdinalIgnoreCase) ||
                                                                                               key.Replace('/', '\\').StartsWith(p, StringComparison.OrdinalIgnoreCase));
                    if (shouldDelete)
                    {
                        deletedCount++;
                    }
                    else if (Is7zDirectoryEntry(key, entry.IsDirectory))
                    {
                        writer.WriteDirectory(key, entry.LastModifiedTime);
                    }
                    else
                    {
                        using var s = entry.OpenEntryStream();
                        writer.Write(key, s, entry.LastModifiedTime);
                    }
                }
            }));
        }
        else if (string.Equals(ext, ".tar", StringComparison.OrdinalIgnoreCase))
        {
            deletedCount = await Task.Run(() => DeleteTarEntries(archiveFilePath, fileKeysToDelete, keysToDelete, prefixesToDelete));
        }
        else
        {
            throw new NotSupportedException($"{ext}形式での削除はサポートされていません");
        }

        // 選択された項目ごとにフルパスでログを残す。
        foreach (var entry in entriesToDelete)
        {
            logAction(entry.FullPath, "DEL");
        }

        return deletedCount;
    }

    /// <summary>
    /// アーカイブへ追加する1ファイル。選択元の項目、表示用の元パス、格納名、中身の開き方を持つ。
    /// </summary>
    private sealed class FileItemToAdd
    {
        public required string SourceEntryFullPath { get; init; }
        public required string DisplaySourcePath { get; init; }
        public required string TargetKey { get; set; }
        public required long Size { get; init; }
        public required DateTime LastWriteTime { get; init; }
        public required Func<Task<Stream>> OpenReadAsync { get; init; }
    }

    /// <summary>
    /// 選択された項目を、アーカイブへ追加するファイルの一覧に展開する。元は実フォルダーでもアーカイブ内でもよい。
    /// </summary>
    private static async Task<List<FileItemToAdd>> CollectFilesToAddAsync(
        IReadOnlyList<FileEntry> entries,
        string internalDestPath)
    {
        var result = new List<FileItemToAdd>();
        var normDest = WindowsArchiveKey(internalDestPath);

        foreach (var entry in entries)
        {
            if (entry.IsParent) continue;

            if (TrySplitArchivePath(entry.FullPath, out var srcArcPath, out var srcInternal) && !string.IsNullOrEmpty(srcInternal))
            {
                // コピー元がアーカイブ内のフォルダーなら、その下のファイルをすべて加える。中身は必要になったときに読み出す。
                if (entry.IsDirectory)
                {
                    var normSrc = WindowsArchiveKey(srcInternal);
                    var prefix = string.IsNullOrEmpty(normSrc) ? string.Empty : normSrc + "\\";
                    var srcItems = LoadArchiveItemRecords(srcArcPath);
                    foreach (var item in srcItems.Where(i => !i.IsDirectory))
                    {
                        var itemKey = WindowsArchiveKey(item.Key);
                        if (!string.IsNullOrEmpty(prefix) && !itemKey.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                        var relInDir = string.IsNullOrEmpty(prefix) ? itemKey : itemKey.Substring(prefix.Length);
                        var targetKey = string.IsNullOrEmpty(normDest)
                            ? $"{entry.Name}/{relInDir}".Replace('\\', '/')
                            : $"{normDest}/{entry.Name}/{relInDir}".Replace('\\', '/');

                        result.Add(new FileItemToAdd
                        {
                            SourceEntryFullPath = entry.FullPath,
                            DisplaySourcePath = $"{entry.FullPath}\\{relInDir}",
                            TargetKey = targetKey,
                            Size = item.Size,
                            LastWriteTime = item.LastWriteTime,
                            OpenReadAsync = async () =>
                            {
                                var bytes = await ExtractArchiveEntryBytesAsync(srcArcPath, item.Key);
                                return new MemoryStream(bytes);
                            }
                        });
                    }
                }
                // アーカイブ内の1ファイル。
                else
                {
                    var targetKey = string.IsNullOrEmpty(normDest)
                        ? entry.Name
                        : $"{normDest}/{entry.Name}".Replace('\\', '/');

                    result.Add(new FileItemToAdd
                    {
                        SourceEntryFullPath = entry.FullPath,
                        DisplaySourcePath = entry.FullPath,
                        TargetKey = targetKey,
                        Size = entry.Length,
                        LastWriteTime = entry.LastWriteTime,
                        OpenReadAsync = async () =>
                        {
                            var bytes = await ExtractArchiveEntryBytesAsync(srcArcPath, srcInternal);
                            return new MemoryStream(bytes);
                        }
                    });
                }
            }
            else
            {
                // コピー元が実フォルダーなら、リンクを除いた下のファイルをすべて加える。
                if (entry.IsDirectory)
                {
                    if (Directory.Exists(entry.FullPath))
                    {
                        foreach (var file in FileOperationPaths.EnumerateTransferFiles(entry.FullPath))
                        {
                            var rel = Path.GetRelativePath(entry.FullPath, file.FullName);
                            var targetKey = string.IsNullOrEmpty(normDest)
                                ? $"{entry.Name}/{rel}".Replace('\\', '/')
                                : $"{normDest}/{entry.Name}/{rel}".Replace('\\', '/');

                            result.Add(new FileItemToAdd
                            {
                                SourceEntryFullPath = entry.FullPath,
                                DisplaySourcePath = file.FullName,
                                TargetKey = targetKey,
                                Size = file.Length,
                                LastWriteTime = file.LastWriteTime,
                                OpenReadAsync = () => Task.FromResult<Stream>(File.OpenRead(file.FullName))
                            });
                        }
                    }
                }
                // 実ファイル。リンクは拒否する。
                else
                {
                    if (File.Exists(entry.FullPath))
                    {
                        FileOperationPaths.RejectLinks(entry.FullPath);
                        var fi = new FileInfo(entry.FullPath);
                        var targetKey = string.IsNullOrEmpty(normDest)
                            ? entry.Name
                            : $"{normDest}/{entry.Name}".Replace('\\', '/');

                        result.Add(new FileItemToAdd
                        {
                            SourceEntryFullPath = entry.FullPath,
                            DisplaySourcePath = entry.FullPath,
                            TargetKey = targetKey,
                            Size = fi.Length,
                            LastWriteTime = fi.LastWriteTime,
                            OpenReadAsync = () => Task.FromResult<Stream>(File.OpenRead(entry.FullPath))
                        });
                    }
                }
            }
        }

        return result;
    }

    /// <summary>
    /// アーカイブ内の指定フォルダーへ項目を追加する。同名項目を確認し、形式ごとに書き直してから原本と置き換える。
    /// </summary>
    public static async Task<(int CopiedCount, int SkippedCount, List<string> SuccessfulPaths)> AddEntriesToArchiveAsync(
        string archiveFilePath,
        string internalDestPath,
        IReadOnlyList<FileEntry> entriesToAdd,
        Func<ConflictInfo, Task<ConflictResolution>> conflictResolver,
        Action<string, string> logAction,
        CancellationToken cancellationToken = default)
    {
        int copiedCount = 0;
        int skippedCount = 0;
        var copiedItems = new List<FileItemToAdd>();

        // アーカイブ自身やそれを含むフォルダーを追加しようとしたら中止する。
        var selfItem = entriesToAdd.FirstOrDefault(entry => FileOperationPaths.ContainsArchive(entry.FullPath, entry.IsDirectory, archiveFilePath));

        if (selfItem != null)
        {
            throw new InvalidOperationException($"アーカイブファイル自身 ({selfItem.Name}) をアーカイブ内に追加することはできません。");
        }

        // 追加するファイルを集める。TAR は空フォルダーだけでも追加するので先へ進む。
        var filesToAdd = await CollectFilesToAddAsync(entriesToAdd, internalDestPath);
        var ext = Path.GetExtension(archiveFilePath);
        if (filesToAdd.Count == 0 && !string.Equals(ext, ".tar", StringComparison.OrdinalIgnoreCase))
        {
            return (0, 0, new List<string>());
        }

        var context = new ConflictSession();

        if (string.Equals(ext, ".zip", StringComparison.OrdinalIgnoreCase))
        {
            return await AddEntriesToZipAsync(archiveFilePath, filesToAdd,
                conflictResolver, logAction, cancellationToken);
        }
        else if (IsLhaExtension(ext))
        {
            // LZH は既存の項目を読み込み、追加するファイルの名前と中身をメモリに用意する。
            var existingItems = ReadAllLhaItems(archiveFilePath, readData: true);
            var pendingCopies = new List<Action>();
            var index = existingItems.Select(f => new ArchiveTarget(NormalizeArchiveKey(f.Key), f.Size, f.Time, f.IsDirectory)).ToList();
            var replaced = new HashSet<string>(StringComparer.Ordinal);
            var readyFiles = new List<(string Key, byte[] Data, DateTime Time)>();

            foreach (var item in filesToAdd)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string? key = await ResolveArchiveTargetAsync(archiveFilePath, item, index, replaced, true,
                    context, conflictResolver, logAction, cancellationToken);
                if (key == null) { skippedCount++; continue; }

                using var s = await item.OpenReadAsync();
                using var ms = new MemoryStream();
                await s.CopyToAsync(ms, cancellationToken);
                readyFiles.Add((key, ms.ToArray(), item.LastWriteTime));

                // 成功ログと件数は書き直しが済んでから反映する。
                string completedKey = key;
                pendingCopies.Add(() =>
                {
                    logAction($"{item.DisplaySourcePath} -> {archiveFilePath}\\{completedKey.Replace('/', '\\')}", "COPY");
                    copiedCount++;
                    copiedItems.Add(item);
                });
                await Task.Delay(5, cancellationToken);
            }

            // 上書きを承認した項目だけを外し、残りは更新日時ごと引き継ぐ。全件スキップなら原本を書き直さない。
            var rewrite = KeepLhaItems(existingItems, f => !replaced.Contains(NormalizeArchiveKey(f.Key)));
            foreach (var r in readyFiles) rewrite.Set(r.Key, r.Data, r.Time);
            if (readyFiles.Count > 0) SaveLhaFiles(archiveFilePath, rewrite, cancellationToken);
            foreach (var completed in pendingCopies) completed();
        }
        else if (string.Equals(ext, ".7z", StringComparison.OrdinalIgnoreCase))
        {
            // 置き換えが成立するまで成功ログを出さず、失敗・中止時は一時ファイルを残さない。
            var committedLogs = new List<string>();
            await PublishViaTemporaryAsync(archiveFilePath, async tempFile =>
            {
                using (var outFs = new FileStream(tempFile, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new SevenZipWriter(outFs, new SevenZipWriterOptions()))
                {
                    // 既存の項目の索引を作る。
                    using var existingArchive = RarArchiveAdapter.Open(archiveFilePath, GetReaderOptions(archiveFilePath));
                    var existingEntries = existingArchive.Entries.ToList();

                    var index = existingEntries.Select(e => new ArchiveTarget(NormalizeArchiveKey(e.Key ?? string.Empty), e.Size,
                        e.LastModifiedTime ?? DateTime.MinValue, Is7zDirectoryEntry(e.Key ?? string.Empty, e.IsDirectory))).ToList();
                    var replaced = new HashSet<string>(StringComparer.Ordinal);

                    foreach (var item in filesToAdd)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        // 書き込み済みの項目は取り消せないため、同じ操作で追加した名前への上書きは受け付けない。
                        string? key = await ResolveArchiveTargetAsync(archiveFilePath, item, index, replaced, false,
                            context, conflictResolver, logAction, cancellationToken);
                        if (key == null) { skippedCount++; continue; }

                        using var s = await item.OpenReadAsync();
                        writer.Write(key, s, item.LastWriteTime);

                        committedLogs.Add($"{item.DisplaySourcePath} -> {archiveFilePath}\\{key.Replace('/', '\\')}");
                        copiedCount++;
                        copiedItems.Add(item);
                        await Task.Delay(5, cancellationToken);
                    }

                    // 既存の項目のうち、上書きされていないものを書き戻す。
                    foreach (var entry in existingEntries)
                    {
                        var key = entry.Key ?? string.Empty;
                        var k = key.Replace('\\', '/').Trim('/');
                        if (!replaced.Contains(k))
                        {
                            if (Is7zDirectoryEntry(key, entry.IsDirectory))
                            {
                                writer.WriteDirectory(key, entry.LastModifiedTime);
                            }
                            else
                            {
                                using var s = entry.OpenEntryStream();
                                writer.Write(key, s, entry.LastModifiedTime);
                            }
                        }
                    }
                }
                return copiedCount > 0;
            }, cancellationToken);
            foreach (var line in committedLogs) logAction(line, "COPY");
        }
        else if (string.Equals(ext, ".tar", StringComparison.OrdinalIgnoreCase))
        {
            return await AddEntriesToTarAsync(archiveFilePath, internalDestPath, entriesToAdd,
                filesToAdd, conflictResolver, logAction, cancellationToken);
        }
        else
        {
            throw new NotSupportedException($"{ext}形式へのコピーはサポートされていません");
        }

        return (copiedCount, skippedCount, GetCompletedArchiveRoots(filesToAdd, copiedItems));
    }

    /// <summary>
    /// 「名前_1.拡張子」のように連番を付け、既存の名前と重ならない格納名を作る。
    /// </summary>
    private static string GenerateAutoNumberedArchiveKey(IEnumerable<string> existingKeys, string originalKey)
    {
        var existingSet = new HashSet<string>(existingKeys.Select(k => k.Replace('\\', '/').Trim('/')), StringComparer.OrdinalIgnoreCase);
        var dirPart = Path.GetDirectoryName(originalKey)?.Replace('\\', '/');
        var baseName = Path.GetFileNameWithoutExtension(originalKey);
        var ext = Path.GetExtension(originalKey);

        // 空いている番号が見つかるまで数を増やす。
        int counter = 1;
        string candidate;
        do
        {
            var name = $"{baseName}_{counter}{ext}";
            candidate = string.IsNullOrEmpty(dirPart) ? name : $"{dirPart}/{name}";
            counter++;
        } while (existingSet.Contains(candidate));

        return candidate;
    }
}
