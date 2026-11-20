using System.IO;
using System.IO.Compression;
using MirrorFlux.Models;

namespace MirrorFlux.Services;

public static partial class ArchiveService
{
    /// <summary>
    /// ZIP へファイルを追加する。原本の複製を一時ファイルとして更新し、追加できた項目があれば原本と置き換える。
    /// </summary>
    private static async Task<(int CopiedCount, int SkippedCount, List<string> SuccessfulPaths)> AddEntriesToZipAsync(
        string archiveFilePath, List<FileItemToAdd> filesToAdd,
        Func<ConflictInfo, Task<ConflictResolution>> conflictResolver,
        Action<string, string> logAction, CancellationToken cancellationToken)
    {
        var copied = new List<FileItemToAdd>();
        var context = new ConflictSession();
        int skipped = 0;
        await PublishViaTemporaryAsync(archiveFilePath, async temporary =>
        {
            // 置き換え用を作る間は原本を読み取りロックしておく。更新モードの ZipArchive は、コピー中に例外が出ても
            // 削除済みや書きかけの項目を保存することがあるので、触るのは一時ファイルだけにする。
            using (var original = new FileStream(archiveFilePath, FileMode.Open, FileAccess.Read,
                FileShare.Read, 65536, useAsync: true))
            {
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 65536, useAsync: true))
                    await original.CopyToAsync(output, cancellationToken);

                // 一時ファイルを更新モードで開き、既存の項目の索引を作る。
                // ZipArchiveEntry.Length は書き込み用に開いた後は読めないので、情報を別に持ち、
                // 後の項目がこの操作で追加した項目とも同名判定できるようにする。
                using var stream = new FileStream(temporary, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                using var zip = new ZipArchive(stream, ZipArchiveMode.Update, false, ZipNameEncoding.Instance);
                var index = zip.Entries.Select(entry => new ArchiveTarget(NormalizeArchiveKey(entry.FullName),
                    entry.Length, entry.LastWriteTime.DateTime, entry.FullName.EndsWith('/'))).ToList();
                var replaced = new HashSet<string>(StringComparer.Ordinal);

                foreach (var item in filesToAdd)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // ZIP は項目をその場で置き換えるので、同じ操作で先に追加した項目を後の項目が上書きしてよい。
                    string? key = await ResolveArchiveTargetAsync(archiveFilePath, item, index, replaced, true,
                        context, conflictResolver, logAction, cancellationToken);
                    if (key == null) { skipped++; continue; }
                    // 完全に同じ名前があればそれを置き換え、なければ上で確認した大文字・小文字違いの項目を置き換える。
                    // 完全一致がある場合、大文字・小文字だけ違う隣の項目は残す。
                    var sameName = zip.Entries.Where(entry =>
                        NormalizeArchiveKey(entry.FullName).Equals(key, StringComparison.OrdinalIgnoreCase)).ToList();
                    var exact = sameName.Where(entry => NormalizeArchiveKey(entry.FullName).Equals(key, StringComparison.Ordinal)).ToList();
                    foreach (var existing in exact.Count > 0 ? exact : sameName)
                        existing.Delete();
                    // 新しい項目を作って中身を書く。
                    var newEntry = zip.CreateEntry(key, System.IO.Compression.CompressionLevel.Optimal);
                    newEntry.LastWriteTime = item.LastWriteTime;
                    using (var output = newEntry.Open())
                    using (var input = await item.OpenReadAsync())
                        await input.CopyToAsync(output, cancellationToken);
                    item.TargetKey = key;
                    copied.Add(item);
                }
            }
            return copied.Count > 0;
        }, cancellationToken);
        // 置き換えが済んでからログを残す。取り消された操作の成功ログは出さない。
        foreach (var item in copied)
            logAction($"{item.DisplaySourcePath} -> {archiveFilePath}\\{item.TargetKey.Replace('/', '\\')}", "COPY");
        return (copied.Count, skipped, GetCompletedArchiveRoots(filesToAdd, copied));
    }

    /// <summary>アーカイブ内の名前を「/」区切りにし、前後の区切りを除く。</summary>
    private static string NormalizeArchiveKey(string key) => key.Replace('\\', '/').Trim('/');

    /// <summary>アーカイブへの追加で同名判定に使う、書き込み後の内容を表す索引の1件。</summary>
    private sealed record ArchiveTarget(string Key, long Size, DateTime Time, bool IsDirectory, bool AddedInBatch = false);

    /// <summary>完全一致を優先し、なければ大文字・小文字だけ違う項目を同名として扱う。</summary>
    private static ArchiveTarget? FindArchiveTarget(List<ArchiveTarget> index, string key) =>
        index.FirstOrDefault(target => target.Key.Equals(key, StringComparison.Ordinal)) ??
        index.FirstOrDefault(target => target.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 追加先の名前を確定する。別名・自動連番で決めた名前も改めて同名項目を確認する。
    /// 上書きする既存項目は replaced に元の名前を記録する。スキップ時は null を返す。
    /// </summary>
    private static async Task<string?> ResolveArchiveTargetAsync(string archiveFilePath, FileItemToAdd item,
        List<ArchiveTarget> index, HashSet<string> replaced, bool allowBatchOverwrite, ConflictSession context,
        Func<ConflictInfo, Task<ConflictResolution>> conflictResolver, Action<string, string> logAction,
        CancellationToken cancellationToken)
    {
        // 同名項目がなくなるまで確認を繰り返す。
        string key = NormalizeArchiveKey(item.TargetKey);
        for (int attempt = 0; FindArchiveTarget(index, key) is { } existing; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (attempt >= 100) throw new IOException("アーカイブ内の同名項目を解決できません。");
            if (existing.IsDirectory) throw new IOException($"同名のフォルダーがあります: {key}");
            var resolution = await context.ResolveAsync(
                () => FileConflicts.Uncompared(item.DisplaySourcePath, item.Size, item.LastWriteTime,
                    new FileInfo(Path.GetFileName(key)), existing.Size, existing.Time), conflictResolver);
            cancellationToken.ThrowIfCancellationRequested();
            // 中止・スキップ・更新なしを処理する。
            if (resolution.Action == ConflictAction.Cancel) throw new OperationCanceledException();
            bool unchanged = resolution.Action == ConflictAction.Update && item.LastWriteTime <= existing.Time;
            if (resolution.Action == ConflictAction.Skip || unchanged)
            {
                logAction($"{(unchanged ? "スキップ (更新なし)" : "スキップ")}: {item.DisplaySourcePath} -> {archiveFilePath}\\{key.Replace('/', '\\')}", "SKIP");
                return null;
            }
            // 別名・自動連番は新しい名前で確認し直す。
            if (resolution.Action == ConflictAction.Rename) { key = RenameArchiveKey(key, resolution.NewName); continue; }
            if (resolution.Action == ConflictAction.AutoRename)
            {
                key = GenerateAutoNumberedArchiveKey(index.Select(target => target.Key), key);
                continue;
            }
            // 上書き、または追加元が新しい場合の更新。この項目に対して承認された上書きとして扱う。
            if (existing.AddedInBatch && !allowBatchOverwrite)
                throw new IOException($"同じ操作で追加した項目は上書きできません: {key}");
            if (!existing.AddedInBatch) replaced.Add(existing.Key);
            index.Remove(existing);
            break;
        }
        // 確定した名前をこの操作で追加した項目として索引に加える。
        index.Add(new ArchiveTarget(key, item.Size, item.LastWriteTime, false, true));
        return key;
    }

    /// <summary>
    /// 別名に指定された名前を検証し、同じフォルダー内の名前に置き換える。
    /// </summary>
    private static string RenameArchiveKey(string key, string? newName)
    {
        string name = newName?.Trim() ?? "";
        if (name.Length == 0 || name is "." or ".." ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains('/') || name.Contains('\\'))
            throw new IOException("有効なファイル名を指定してください。");
        int slash = key.LastIndexOf('/');
        return slash < 0 ? name : key[..(slash + 1)] + name;
    }
}
