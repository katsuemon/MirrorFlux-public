using System.IO;
using MirrorFlux.Models;

namespace MirrorFlux.Services;

/// <summary>
/// フォルダーまたはアーカイブ内のフォルダーを読み、ファイル一覧の項目を作る。
/// </summary>
public static class DirectoryReader
{
    /// <summary>
    /// 指定したフォルダーの項目を返す。先頭に親フォルダー「..」を置く。showHidden が false なら隠し・システム属性の項目を除く。
    /// </summary>
    public static IReadOnlyList<FileEntry> Read(string path, bool showHidden = false)
    {
        // アーカイブ内なら ArchiveService で読む。
        if (ArchiveService.TrySplitArchivePath(path, out var archiveFilePath, out var internalPath))
        {
            return ArchiveService.ReadArchiveEntries(archiveFilePath, internalPath, showHidden);
        }

        var fullPath = Path.GetFullPath(path);
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException($"ディレクトリが見つかりません: {fullPath}");
        }

        // 親フォルダーがあれば「..」を加える。
        var entries = new List<FileEntry>();
        var parent = Directory.GetParent(fullPath);
        if (parent is not null)
        {
            entries.Add(new FileEntry(parent.FullName, "..", true, true, 0, DateTime.MinValue));
        }

        // 各項目の属性・日時・サイズを読んで加える。
        foreach (var entryPath in Directory.EnumerateFileSystemEntries(fullPath))
        {
            try
            {
                var attributes = File.GetAttributes(entryPath);
                if (!showHidden && (attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0)
                {
                    continue;
                }
                var isDirectory = (attributes & FileAttributes.Directory) != 0;
                var name = Path.GetFileName(entryPath) ?? entryPath;
                var lastWriteTime = File.GetLastWriteTime(entryPath);
                var length = isDirectory ? 0 : new FileInfo(entryPath).Length;
                entries.Add(new FileEntry(entryPath, name, isDirectory, false, length, lastWriteTime));
            }
            catch (IOException)
            {
                // 一時的に読めない項目があっても、残りの項目は読み込む。
            }
            catch (UnauthorizedAccessException)
            {
                // 列挙はできても情報を読めない項目は飛ばす。
            }
        }

        return entries;
    }

    /// <summary>
    /// フォルダー以下の全ファイルの合計サイズを返す。読めない場所は飛ばし、失敗したら 0 を返す。
    /// </summary>
    public static long CalculateDirectorySize(string path)
    {
        try
        {
            var di = new DirectoryInfo(path);
            return di.EnumerateFiles("*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true
            }).Sum(f => f.Length);
        }
        catch
        {
            return 0;
        }
    }
}
