using System.IO;
using MirrorFlux.Models;

namespace MirrorFlux.Services;

/// <summary>
/// 実ファイル・実フォルダーの移動を行う。同名ファイルの確認、項目ごとのログ、移動できないときのコピーによる代替を扱う。
/// </summary>
public static class FileMoveService
{
    /// <summary>
    /// 選択された項目を移動先フォルダーへ移し、件数と完全に移動できた項目を返す。1件の失敗で止めず残りを続ける。
    /// </summary>
    public static async Task<(int MovedCount, int SkippedCount, List<string> SuccessfulPaths)> MoveEntriesAsync(
        IReadOnlyList<FileEntry> entries,
        string destinationDir,
        Func<ConflictInfo, Task<ConflictResolution>> conflictResolver,
        Action<string, string> logAction,
        CancellationToken cancellationToken = default)
    {
        int movedCount = 0;
        int skippedCount = 0;
        var successfulPaths = new List<string>();

        // 「すべてに適用」の選択をこの操作の中で覚えておく。
        var context = new ConflictSession();

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (entry.IsParent) continue;

            var targetName = entry.Name;
            var targetDestPath = Path.Combine(destinationDir, targetName);

            try
            {
                if (entry.IsDirectory)
                {
                    // 自分の下のフォルダーへは移動できない。
                    if (FileOperationPaths.TryIsSameOrDescendant(destinationDir, entry.FullPath))
                    {
                        logAction($"移動失敗: {entry.FullPath}\n  [原因: 自身の子フォルダーへの移動はできません]", "ERR");
                        continue;
                    }

                    // フォルダーは中身ごと移し、元が残らなければ成功とする。
                    var (dirMoved, dirSkipped, dirSuccess) = await MoveDirectoryRecursiveAsync(
                        entry.FullPath,
                        targetDestPath,
                        conflictResolver,
                        context,
                        logAction,
                        cancellationToken);

                    movedCount += dirMoved;
                    skippedCount += dirSkipped;
                    if (dirSuccess)
                    {
                        successfulPaths.Add(entry.FullPath);
                        FileOperationJob.Current?.ItemCompleted(entry.FullPath);
                    }
                }
                else
                {
                    // ファイルは1件移動する。
                    var outcome = await MoveFileWithConflictHandlingAsync(
                        entry.FullPath,
                        targetDestPath,
                        conflictResolver,
                        context,
                        logAction,
                        cancellationToken);

                    if (outcome == MoveOutcome.Moved)
                    {
                        movedCount++;
                        successfulPaths.Add(entry.FullPath);
                        FileOperationJob.Current?.ItemCompleted(entry.FullPath);
                    }
                    else if (outcome == MoveOutcome.Skipped)
                    {
                        skippedCount++;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                string reason = FileCopyService.FormatFailureReason(ex);
                logAction($"移動失敗: {entry.FullPath}\n  [原因: {reason}]", "ERR");
            }
        }

        return (movedCount, skippedCount, successfulPaths);
    }

/// <summary>
/// 1ファイルの移動結果。
/// </summary>
private enum MoveOutcome
    {
        Moved,
        Skipped,
        Canceled
    }

    /// <summary>
    /// フォルダーを中身ごと再帰的に移動し、空になった元フォルダーを消す。Success は元フォルダーが残らなかったか。
    /// </summary>
    private static async Task<(int MovedCount, int SkippedCount, bool Success)> MoveDirectoryRecursiveAsync(
        string sourceDir,
        string destDir,
        Func<ConflictInfo, Task<ConflictResolution>> conflictResolver,
        ConflictSession context,
        Action<string, string> logAction,
        CancellationToken cancellationToken)
    {
        // 移動先が自分の中でないかなどを確かめ、移動先フォルダーを作る。
        FileOperationPaths.ValidateDirectoryTransfer(sourceDir, destDir);
        if (!Directory.Exists(destDir))
        {
            Directory.CreateDirectory(destDir);
            logAction(destDir, "MKDIR");
        }

        int movedCount = 0;
        int skippedCount = 0;

        var dirInfo = new DirectoryInfo(sourceDir);

        // ファイルの一覧を取る。取れなければログを出して空として続ける。
        FileInfo[] files;
        try
        {
            files = dirInfo.GetFiles();
        }
        catch (Exception ex)
        {
            string reason = FileCopyService.FormatFailureReason(ex);
            logAction($"フォルダー内ファイル一覧取得失敗: {sourceDir}\n  [原因: {reason}]", "ERR");
            files = [];
        }

        // ファイルを移動する。失敗しても次へ進む。
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var targetFilePath = Path.Combine(destDir, file.Name);
            try
            {
                var outcome = await MoveFileWithConflictHandlingAsync(
                    file.FullName,
                    targetFilePath,
                    conflictResolver,
                    context,
                    logAction,
                    cancellationToken);

                if (outcome == MoveOutcome.Moved)
                {
                    movedCount++;
                }
                else if (outcome == MoveOutcome.Skipped)
                {
                    skippedCount++;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                string reason = FileCopyService.FormatFailureReason(ex);
                logAction($"移動失敗: {file.FullName}\n  [原因: {reason}]", "ERR");
            }
        }

        // 下のフォルダーの一覧を取る。
        DirectoryInfo[] subDirs;
        try
        {
            subDirs = dirInfo.GetDirectories();
        }
        catch (Exception ex)
        {
            string reason = FileCopyService.FormatFailureReason(ex);
            logAction($"サブフォルダー一覧取得失敗: {sourceDir}\n  [原因: {reason}]", "ERR");
            subDirs = [];
        }

        // 下のフォルダーを再帰的に移動する。
        foreach (var subDir in subDirs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var targetSubDir = Path.Combine(destDir, subDir.Name);
            var (subMoved, subSkipped, _) = await MoveDirectoryRecursiveAsync(
                subDir.FullName,
                targetSubDir,
                conflictResolver,
                context,
                logAction,
                cancellationToken);

            movedCount += subMoved;
            skippedCount += subSkipped;
        }

        // 移動し終えて空になった元フォルダーを消す。中身が残っていれば消さない。
        try
        {
            if (Directory.Exists(sourceDir) && !Directory.EnumerateFileSystemEntries(sourceDir).Any())
            {
                RemoveReadOnlyIfPresent(sourceDir);
                Directory.Delete(sourceDir);
            }
        }
        catch (Exception ex)
        {
            string reason = FileCopyService.FormatFailureReason(ex);
            logAction($"フォルダー削除失敗: {sourceDir}\n  [原因: {reason}]", "WARN");
        }

        bool fullyMoved = !Directory.Exists(sourceDir);
        return (movedCount, skippedCount, fullyMoved);
    }

    /// <summary>
    /// 1ファイルの移動を、実行中のファイル操作の進み具合（処理中のファイル）として記録しながら行う。
    /// </summary>
    private static async Task<MoveOutcome> MoveFileWithConflictHandlingAsync(
        string sourceFilePath,
        string destFilePath,
        Func<ConflictInfo, Task<ConflictResolution>> conflictResolver,
        ConflictSession context,
        Action<string, string> logAction,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var job = FileOperationJob.Current;
        long size = 0;
        try { size = new FileInfo(sourceFilePath).Length; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        job?.BeginFile(sourceFilePath, size);
        try { return await MoveFileCoreAsync(sourceFilePath, destFilePath, conflictResolver, context, logAction, cancellationToken); }
        finally { job?.EndFile(); }
    }

    /// <summary>
    /// 同名ファイルを確認してから1ファイルを移動する。ドライブが違うときは、ファイルの途中でも中止できるようコピーしてから元を消す。
    /// </summary>
    private static async Task<MoveOutcome> MoveFileCoreAsync(
        string sourceFilePath,
        string destFilePath,
        Func<ConflictInfo, Task<ConflictResolution>> conflictResolver,
        ConflictSession context,
        Action<string, string> logAction,
        CancellationToken cancellationToken)
    {

        var srcInfo = new FileInfo(sourceFilePath);
        if (!srcInfo.Exists) return MoveOutcome.Skipped;

        var targetPath = destFilePath;

        // 移動元と移動先が同じならスキップする。
        if (string.Equals(Path.GetFullPath(sourceFilePath), Path.GetFullPath(targetPath), StringComparison.OrdinalIgnoreCase))
        {
            logAction($"スキップ (移動元と移動先が同一): {sourceFilePath}", "WARN");
            return MoveOutcome.Skipped;
        }

        // 同名ファイルがあれば確認する。上書きが承認されたら上書き移動へ進む。
        var resolved = await FileConflicts.ResolveTargetAsync(sourceFilePath, targetPath, srcInfo.LastWriteTime,
            destInfo => FileCopyService.BuildConflictInfo(srcInfo, destInfo), context, conflictResolver, logAction);
        if (resolved is not { } target) return MoveOutcome.Skipped;
        targetPath = target.Path;
        if (target.Overwrite) return await MoveOverwritingAsync(sourceFilePath, targetPath, logAction, cancellationToken);

        FileOperationPaths.ValidateFileTransfer(sourceFilePath, targetPath);

        // 移動先のフォルダーを用意し、元の読み取り専用属性を外す。
        var targetDirectory = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(targetDirectory) && !Directory.Exists(targetDirectory))
        {
            Directory.CreateDirectory(targetDirectory);
            logAction(targetDirectory, "MKDIR");
        }

        RemoveReadOnlyIfPresent(sourceFilePath);

        // ドライブが違えば、コピーを書き終えてから元を消す（中止しても元は残る）。
        if (!IsSameVolume(sourceFilePath, targetPath))
            return await MoveAcrossVolumesAsync(sourceFilePath, targetPath, overwrite: false, logAction, cancellationToken);

        // 移動する。失敗したらコピーして元を消す方法で試す。
        try
        {
            File.Move(sourceFilePath, targetPath);
            logAction($"{sourceFilePath} -> {targetPath}", "MOVE");
            return MoveOutcome.Moved;
        }
        catch (Exception ex)
        {
            if (await TryFallbackMoveViaStreamAsync(sourceFilePath, targetPath, overwrite: false, cancellationToken))
            {
                logAction($"{sourceFilePath} -> {targetPath}", "MOVE");
                return MoveOutcome.Moved;
            }
            string reason = FileCopyService.FormatFailureReason(ex);
            logAction($"移動失敗: {sourceFilePath} -> {targetPath}\n  [原因: {reason}]", "ERR");
            return MoveOutcome.Skipped;
        }
    }

    /// <summary>
    /// 既存の同名ファイルを置き換えて移動する。隠し・システムファイルは上書きしない。
    /// </summary>
    private static async Task<MoveOutcome> MoveOverwritingAsync(
        string sourceFilePath, string targetPath,
        Action<string, string> logAction, CancellationToken cancellationToken)
    {
        // 移動先が隠し・システムファイルなら中止する。
        if (File.Exists(targetPath))
        {
            var attr = File.GetAttributes(targetPath);
            if ((attr & (FileAttributes.Hidden | FileAttributes.System)) != 0)
            {
                string attrText = (attr & FileAttributes.Hidden) != 0 && (attr & FileAttributes.System) != 0 ? "隠し+システム" :
                                  (attr & FileAttributes.Hidden) != 0 ? "隠し" : "システム";
                logAction($"上書き移動失敗: {sourceFilePath} -> {targetPath}\n  [原因: 移動先が{attrText}ファイルのため上書きできません]", "ERR");
                return MoveOutcome.Skipped;
            }
        }
        // 両方の読み取り専用属性を外して上書き移動する。失敗したらコピーによる移動を試す。
        RemoveReadOnlyIfPresent(targetPath);
        RemoveReadOnlyIfPresent(sourceFilePath);
        if (!IsSameVolume(sourceFilePath, targetPath))
            return await MoveAcrossVolumesAsync(sourceFilePath, targetPath, overwrite: true, logAction, cancellationToken);
        try
        {
            FileOperationPaths.ValidateFileTransfer(sourceFilePath, targetPath);
            File.Move(sourceFilePath, targetPath, overwrite: true);
            logAction($"{sourceFilePath} -> {targetPath}", "MOVE");
            return MoveOutcome.Moved;
        }
        catch (Exception ex)
        {
            if (await TryFallbackMoveViaStreamAsync(sourceFilePath, targetPath, overwrite: true, cancellationToken))
            {
                logAction($"{sourceFilePath} -> {targetPath}", "MOVE");
                return MoveOutcome.Moved;
            }
            string reason = FileCopyService.FormatFailureReason(ex);
            logAction($"移動失敗: {sourceFilePath} -> {targetPath}\n  [原因: {reason}]", "ERR");
            return MoveOutcome.Skipped;
        }
    }

    /// <summary>ドライブ（ボリュームのルート）が同じか。同じなら File.Move は名前の付け替えだけで済む。</summary>
    internal static bool IsSameVolume(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetPathRoot(Path.GetFullPath(a)), Path.GetPathRoot(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return true; }
    }

    /// <summary>
    /// 別のドライブへの移動。一時ファイル経由でコピーし終えてから元を消すので、途中で中止しても元のファイルは残る。
    /// </summary>
    private static async Task<MoveOutcome> MoveAcrossVolumesAsync(string sourceFilePath, string targetPath, bool overwrite,
        Action<string, string> logAction, CancellationToken cancellationToken)
    {
        try
        {
            FileOperationPaths.ValidateFileTransfer(sourceFilePath, targetPath);
            await FileTransferWriter.CopyAsync(sourceFilePath, targetPath, overwrite, cancellationToken);
            RemoveReadOnlyIfPresent(sourceFilePath);
            File.Delete(sourceFilePath);
            logAction($"{sourceFilePath} -> {targetPath}", "MOVE");
            return MoveOutcome.Moved;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            string reason = FileCopyService.FormatFailureReason(ex);
            logAction($"移動失敗: {sourceFilePath} -> {targetPath}\n  [原因: {reason}]", "ERR");
            return MoveOutcome.Skipped;
        }
    }

    /// <summary>
    /// File.Move が使えないときの代替。一時ファイルへコピーし、完成してから移動先へ置き、最後に元を消す。
    /// </summary>
    internal static async Task<bool> TryFallbackMoveViaStreamAsync(string sourceFilePath, string targetPath, bool overwrite, CancellationToken cancellationToken)
    {
        FileOperationPaths.ValidateFileTransfer(sourceFilePath, targetPath);
        // 完成したコピーだけを公開する。失敗時に消すのは自分の一時ファイルだけで、
        // ほかのプロセスが作った移動先を切り詰めたり消したりしない。
        string temporaryPath = Path.Combine(Path.GetDirectoryName(targetPath)!, $".mf-move-{Guid.NewGuid():N}.tmp");
        bool ownsTemporaryFile = false;
        try
        {
            // 一時ファイルへ中身をコピーし、更新日時を合わせる。
            using (var inStream = new FileStream(sourceFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var outStream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, useAsync: true))
            {
                ownsTemporaryFile = true;
                byte[] buffer = new byte[1 << 20];
                int read;
                var job = FileOperationJob.Current;
                while ((read = await inStream.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await outStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    job?.AddBytes(read);
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            try { File.SetLastWriteTime(temporaryPath, File.GetLastWriteTime(sourceFilePath)); } catch { }
            // 移動先へ置いてから元を消す。
            File.Move(temporaryPath, targetPath, overwrite);
            ownsTemporaryFile = false;
            RemoveReadOnlyIfPresent(sourceFilePath);
            File.Delete(sourceFilePath);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch { return false; }
        finally
        {
            // 置けなかった一時ファイルを片付ける。
            if (ownsTemporaryFile)
            {
                try { File.Delete(temporaryPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }

    /// <summary>
    /// ファイルまたはフォルダーの読み取り専用属性があれば外す。外せなくても続ける。
    /// </summary>
    private static void RemoveReadOnlyIfPresent(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var attr = File.GetAttributes(path);
                if ((attr & FileAttributes.ReadOnly) != 0)
                {
                    File.SetAttributes(path, attr & ~FileAttributes.ReadOnly);
                }
            }
            else if (Directory.Exists(path))
            {
                var dirInfo = new DirectoryInfo(path);
                if ((dirInfo.Attributes & FileAttributes.ReadOnly) != 0)
                {
                    dirInfo.Attributes &= ~FileAttributes.ReadOnly;
                }
            }
        }
        catch
        {
            // 属性を外せなくても無視する。
        }
    }


}
