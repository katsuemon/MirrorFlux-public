using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Formats.Tar;
using MirrorFlux.Models;

namespace MirrorFlux.Services;

public static partial class ArchiveService
{
    /// <summary>
    /// TAR へ書き込むフォルダー項目。Key は「/」区切りの格納名、RootPath は選択元の項目のパス。
    /// </summary>
    private sealed record TarDirectoryItem(string Key, string SourcePath, DateTime LastWriteTime, string RootPath);


    /// <summary>
    /// TAR の項目を読み、名前・サイズ・日時・フォルダーかどうかの索引を作る。
    /// </summary>
    private static List<ArchiveTarget> ReadTarIndex(string archiveFilePath)
    {
        var result = new List<ArchiveTarget>();
        using var input = File.OpenRead(archiveFilePath);
        using var reader = new TarReader(input);
        TarEntry? entry;
        while ((entry = reader.GetNextEntry()) != null)
        {
            string key = NormalizeArchiveKey(entry.Name);
            if (key.Length == 0) continue;
            result.Add(new ArchiveTarget(key, entry.Length, entry.ModificationTime.LocalDateTime,
                entry.EntryType == TarEntryType.Directory));
        }
        return result;
    }

    /// <summary>
    /// 既存の TAR の項目を新しい TAR へ写す。omit が true を返した項目は写さず、その件数を返す。
    /// </summary>
    private static int CopyTarEntries(string archiveFilePath, TarWriter writer,
        Func<string, bool> omit, CancellationToken cancellationToken)
    {
        int omitted = 0;
        using var input = File.OpenRead(archiveFilePath);
        using var reader = new TarReader(input);
        TarEntry? entry;
        while ((entry = reader.GetNextEntry()) != null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (omit(NormalizeArchiveKey(entry.Name)))
            {
                omitted++;
                continue;
            }
            writer.WriteEntry(entry);
        }
        return omitted;
    }

    /// <summary>
    /// 一時ファイルに新しい TAR を書き、書き終えてから原本と置き換える。
    /// </summary>
    private static void WriteTarAtomically(string archiveFilePath, Action<TarWriter> write) =>
        PublishViaTemporary(archiveFilePath, temporary =>
        {
            using var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new TarWriter(output, TarEntryFormat.Pax);
            write(writer);
            return true;
        });

    /// <summary>
    /// TAR 内にフォルダーを作る。同名の項目が既にあれば何もしない。
    /// </summary>
    private static void CreateTarDirectory(string archiveFilePath, string internalPath)
    {
        string key = NormalizeArchiveKey(internalPath);
        var existing = ReadTarIndex(archiveFilePath);
        if (existing.Any(item => item.Key.Equals(key, StringComparison.OrdinalIgnoreCase)))
            return;
        // 既存の項目を写したうえで、末尾にフォルダー項目を加える。
        WriteTarAtomically(archiveFilePath, writer =>
        {
            CopyTarEntries(archiveFilePath, writer, _ => false, CancellationToken.None);
            writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, key + "/")
            {
                ModificationTime = DateTimeOffset.Now
            });
        });
    }

    /// <summary>
    /// TAR から指定の項目を除いて書き直し、削除した件数を返す。
    /// </summary>
    private static int DeleteTarEntries(string archiveFilePath, HashSet<string> fileKeysToDelete,
        HashSet<string> keysToDelete, List<string> prefixesToDelete)
    {
        // ファイルは完全一致で照合し、大文字・小文字だけ違う隣の項目を残す。フォルダーは大文字・小文字を区別しない。
        var files = fileKeysToDelete.Select(NormalizeArchiveKey).ToHashSet(StringComparer.Ordinal);
        var keys = keysToDelete.Select(NormalizeArchiveKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var prefixes = prefixesToDelete.Select(NormalizeArchiveKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool Omit(string key) => files.Contains(key) || keys.Contains(key) ||
            prefixes.Any(prefix => key.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase));
        // 対象がなければ書き直さない。
        int count = ReadTarIndex(archiveFilePath).Count(item => Omit(item.Key));
        if (count == 0) return 0;
        WriteTarAtomically(archiveFilePath, writer =>
            CopyTarEntries(archiveFilePath, writer, Omit, CancellationToken.None));
        return count;
    }

    /// <summary>
    /// 追加する項目に含まれるフォルダーを、選択したフォルダー自身とその下のフォルダーまで集める。
    /// 元がアーカイブ内のフォルダーならアーカイブの項目から、実フォルダーならディスクから集める。
    /// </summary>
    private static List<TarDirectoryItem> CollectTarDirectories(
        IReadOnlyList<FileEntry> entries, string internalDestPath)
    {
        // 同じ名前は1回だけ登録する。
        string destination = NormalizeArchiveKey(internalDestPath);
        var result = new List<TarDirectoryItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string key, string source, DateTime time, string root)
        {
            key = NormalizeArchiveKey(key);
            if (seen.Add(key)) result.Add(new TarDirectoryItem(key, source, time, root));
        }

        foreach (var entry in entries.Where(e => e.IsDirectory && !e.IsParent))
        {
            // 選択したフォルダー自身を登録する。
            string rootKey = string.IsNullOrEmpty(destination) ? entry.Name : destination + "/" + entry.Name;
            Add(rootKey, entry.FullPath, entry.LastWriteTime, entry.FullPath);
            // アーカイブ内のフォルダーなら、その下のフォルダー項目を登録する。
            if (TrySplitArchivePath(entry.FullPath, out string sourceArchive, out string internalPath) &&
                !string.IsNullOrEmpty(internalPath))
            {
                string prefix = NormalizeArchiveKey(internalPath) + "/";
                foreach (var item in LoadArchiveItemRecords(sourceArchive).Where(i => i.IsDirectory))
                {
                    string sourceKey = NormalizeArchiveKey(item.Key);
                    if (sourceKey.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        Add(rootKey + "/" + sourceKey[prefix.Length..],
                            entry.FullPath + "\\" + sourceKey[prefix.Length..].Replace('/', '\\'),
                            item.LastWriteTime, entry.FullPath);
                }
            }
            // 実フォルダーなら、リンクを拒否しながら下のフォルダーを再帰的にたどる。
            else if (Directory.Exists(entry.FullPath))
            {
                void Visit(string path)
                {
                    FileOperationPaths.RejectLinks(path);
                    foreach (string child in Directory.EnumerateDirectories(path))
                    {
                        FileOperationPaths.RejectLinks(child);
                        string relative = Path.GetRelativePath(entry.FullPath, child).Replace('\\', '/');
                        Add(rootKey + "/" + relative, child, Directory.GetLastWriteTime(child), entry.FullPath);
                        Visit(child);
                    }
                }
                Visit(entry.FullPath);
            }
        }
        return result;
    }

    /// <summary>
    /// TAR へファイルとフォルダーを追加する。同名項目の扱いを先にすべて決めてから、一時ファイルへ書き直して原本と置き換える。
    /// </summary>
    private static async Task<(int CopiedCount, int SkippedCount, List<string> SuccessfulPaths)> AddEntriesToTarAsync(
        string archiveFilePath, string internalDestPath, IReadOnlyList<FileEntry> entriesToAdd,
        List<FileItemToAdd> filesToAdd, Func<ConflictInfo, Task<ConflictResolution>> conflictResolver,
        Action<string, string> logAction, CancellationToken cancellationToken)
    {
        // 追加するものがなければ何もしない。
        var directories = CollectTarDirectories(entriesToAdd, internalDestPath);
        if (filesToAdd.Count == 0 && directories.Count == 0)
            return (0, 0, new List<string>());

        // 既存の項目の索引を作る。
        var index = ReadTarIndex(archiveFilePath);
        var existing = index.GroupBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);
        var accepted = new List<FileItemToAdd>();
        var replaced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var context = new ConflictSession();
        int skipped = 0;

        foreach (var item in filesToAdd)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // 書き込みは同名項目の扱いをすべて決めた後に行う。TAR は1つの名前に1項目だけ持たせるので、
            // 同じ操作で先に追加した項目は別名で避けるだけで、上書きはできない。
            string? key = await ResolveArchiveTargetAsync(archiveFilePath, item, index, replaced, false,
                context, conflictResolver, logAction, cancellationToken);
            if (key == null) { skipped++; continue; }
            item.TargetKey = key;
            accepted.Add(item);
        }

        // ファイルとフォルダーの名前がぶつかる場合は中止する。
        var newDirectories = directories.Where(dir => !existing.ContainsKey(dir.Key)).ToList();
        if (newDirectories.Any(dir => accepted.Any(file =>
            file.TargetKey.Equals(dir.Key, StringComparison.OrdinalIgnoreCase))))
            throw new IOException("ファイルとフォルダーの名前が重複しています。");
        if (directories.Any(dir => existing.TryGetValue(dir.Key, out var old) && !old.IsDirectory))
            throw new IOException("同名のファイルがあるためフォルダーを追加できません。");

        // 上書きされる項目を除いて既存の項目を写し、新しいフォルダーとファイルを書き足す。
        await PublishViaTemporaryAsync(archiveFilePath, async temporary =>
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new TarWriter(output, TarEntryFormat.Pax))
            {
                CopyTarEntries(archiveFilePath, writer, replaced.Contains, cancellationToken);
                foreach (var dir in newDirectories)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, dir.Key + "/")
                    {
                        ModificationTime = new DateTimeOffset(dir.LastWriteTime == DateTime.MinValue
                            ? DateTime.Now : dir.LastWriteTime)
                    });
                }
                foreach (var item in accepted)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using var source = await item.OpenReadAsync();
                    writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, item.TargetKey)
                    {
                        DataStream = source,
                        ModificationTime = new DateTimeOffset(item.LastWriteTime == DateTime.MinValue
                            ? DateTime.Now : item.LastWriteTime)
                    });
                }
            }
            return true;
        }, cancellationToken);

        // 置き換えが済んでから、追加した項目ごとにログを残す。
        foreach (var dir in newDirectories)
            logAction($"{dir.SourcePath} -> {archiveFilePath}\\{dir.Key.Replace('/', '\\')}", "COPY");
        foreach (var item in accepted)
            logAction($"{item.DisplaySourcePath} -> {archiveFilePath}\\{item.TargetKey.Replace('/', '\\')}", "COPY");
        var successful = GetCompletedArchiveRoots(filesToAdd, accepted,
            directories.Select(dir => dir.RootPath));
        return (accepted.Count + newDirectories.Count, skipped, successful);
    }
}
