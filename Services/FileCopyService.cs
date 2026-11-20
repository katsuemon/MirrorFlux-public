using System.IO;
using MirrorFlux.Models;

namespace MirrorFlux.Services;

/// <summary>
/// 実ファイル・実フォルダーのコピーを行う。同名ファイルの確認、項目ごとのログ、失敗理由の表示文を扱う。
/// </summary>
public static class FileCopyService
{
    /// <summary>
    /// 選択された項目を展開先フォルダーへコピーし、件数と完全にコピーできた項目を返す。
    /// </summary>
    public static async Task<FileCopyResult> CopyEntriesAsync(
        IReadOnlyList<FileEntry> entries, string destinationDir,
        Func<ConflictInfo, Task<ConflictResolution>> conflictResolver,
        Action<string, string> logAction, CancellationToken cancellationToken = default)
    {
        int copied = 0, skipped = 0, errors = 0;
        var successful = new List<string>();
        var context = new ConflictSession();
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.IsParent) continue;
            string target = Path.Combine(destinationDir, entry.Name);
            try
            {
                // 同じ場所へのコピーは連番付きの名前にする。
                if (string.Equals(Path.GetFullPath(entry.FullPath), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
                    target = FileOperationPaths.GenerateAutoNumberedPath(target);
                // フォルダーは中身ごとコピーし、エラーもスキップもなければ成功とする。空フォルダーは1件として数える。
                if (entry.IsDirectory)
                {
                    var result = await CopyDirectoryRecursiveAsync(entry.FullPath, target, conflictResolver, context, logAction, cancellationToken);
                    copied += result.Copied;
                    skipped += result.Skipped;
                    errors += result.Errors;
                    if (result.Errors == 0 && result.Skipped == 0)
                    {
                        successful.Add(entry.FullPath);
                        FileOperationJob.Current?.ItemCompleted(entry.FullPath);
                        if (result.Copied == 0) copied++;
                    }
                }
                // ファイルは1件コピーする。
                else
                {
                    var result = await CopyFileWithConflictHandlingAsync(entry.FullPath, target, conflictResolver, context, logAction, cancellationToken);
                    if (result == CopyOutcome.Copied) { copied++; successful.Add(entry.FullPath); FileOperationJob.Current?.ItemCompleted(entry.FullPath); }
                    else if (result == CopyOutcome.Skipped) skipped++;
                    else errors++;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                errors++;
                logAction($"コピー失敗: {entry.FullPath}\n  [原因: {FormatFailureReason(ex)}]", "ERR");
            }
        }
        return new(copied, skipped, errors, successful);
    }

/// <summary>
/// フォルダーを中身ごと再帰的にコピーする。1件の失敗で止めず、残りの項目を続ける。
/// </summary>
private static async Task<(int Copied, int Skipped, int Errors)> CopyDirectoryRecursiveAsync(
        string sourceDir, string destDir, Func<ConflictInfo, Task<ConflictResolution>> conflictResolver,
        ConflictSession context, Action<string, string> logAction, CancellationToken cancellationToken)
    {
        // コピー先が自分の中でないかなどを確かめ、コピー先フォルダーを作る。
        FileOperationPaths.ValidateDirectoryTransfer(sourceDir, destDir);
        if (!Directory.Exists(destDir))
        {
            Directory.CreateDirectory(destDir);
            logAction(destDir, "MKDIR");
        }
        // ファイルとフォルダーの一覧を取る。取れなければエラーとして数える。
        int copied = 0, skipped = 0, errors = 0;
        var directory = new DirectoryInfo(sourceDir);
        FileInfo[] files = [];
        DirectoryInfo[] directories = [];
        try { files = directory.GetFiles(); }
        catch (Exception ex) { errors++; logAction($"一覧取得失敗: {sourceDir}\n  [原因: {FormatFailureReason(ex)}]", "ERR"); }
        try { directories = directory.GetDirectories(); }
        catch (Exception ex) { errors++; logAction($"一覧取得失敗: {sourceDir}\n  [原因: {FormatFailureReason(ex)}]", "ERR"); }
        // ファイルをコピーする。
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var result = await CopyFileWithConflictHandlingAsync(file.FullName, Path.Combine(destDir, file.Name), conflictResolver, context, logAction, cancellationToken);
                if (result == CopyOutcome.Copied) copied++;
                else if (result == CopyOutcome.Skipped) skipped++;
                else errors++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { errors++; logAction($"コピー失敗: {file.FullName}\n  [原因: {FormatFailureReason(ex)}]", "ERR"); }
        }
        // 下のフォルダーを再帰的にコピーする。
        foreach (var child in directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var result = await CopyDirectoryRecursiveAsync(child.FullName, Path.Combine(destDir, child.Name), conflictResolver, context, logAction, cancellationToken);
                copied += result.Copied; skipped += result.Skipped; errors += result.Errors;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { errors++; logAction($"コピー失敗: {child.FullName}\n  [原因: {FormatFailureReason(ex)}]", "ERR"); }
        }
        return (copied, skipped, errors);
    }

    /// <summary>
    /// 1ファイルのコピー結果。
    /// </summary>
    private enum CopyOutcome
    {
        Copied,
        Skipped,
        Failed
    }

    /// <summary>
    /// 1ファイルのコピーを、実行中のファイル操作の進み具合（処理中のファイル）として記録しながら行う。
    /// </summary>
    private static async Task<CopyOutcome> CopyFileWithConflictHandlingAsync(
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
        try { return await CopyFileCoreAsync(sourceFilePath, destFilePath, conflictResolver, context, logAction, cancellationToken); }
        finally { job?.EndFile(); }
    }

    /// <summary>
    /// 同名ファイルを確認してから1ファイルをコピーする。
    /// </summary>
    private static async Task<CopyOutcome> CopyFileCoreAsync(
        string sourceFilePath,
        string destFilePath,
        Func<ConflictInfo, Task<ConflictResolution>> conflictResolver,
        ConflictSession context,
        Action<string, string> logAction,
        CancellationToken cancellationToken)
    {

        var srcInfo = new FileInfo(sourceFilePath);
        if (!srcInfo.Exists) throw new FileNotFoundException("コピー元が見つかりません。", sourceFilePath);

        // 同名ファイルがあれば確認し、書き込み先を決める。
        var resolved = await FileConflicts.ResolveTargetAsync(sourceFilePath, destFilePath, srcInfo.LastWriteTime,
            destInfo => BuildConflictInfo(srcInfo, destInfo), context, conflictResolver, logAction);
        if (resolved is not { } target) return CopyOutcome.Skipped;
        string targetPath = target.Path;
        bool overwrite = target.Overwrite;
        if (overwrite && string.Equals(Path.GetFullPath(sourceFilePath), Path.GetFullPath(targetPath), StringComparison.OrdinalIgnoreCase))
        {
            // 自分自身への上書きコピーは連番付きの複製にする。同じファイルは自分より新しくならないので「更新」はここに来ない。
            targetPath = FileOperationPaths.GenerateAutoNumberedPath(targetPath);
            overwrite = false;
        }

        FileOperationPaths.ValidateFileTransfer(sourceFilePath, targetPath);

        // コピー先のフォルダーを用意する。
        var targetDirectory = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(targetDirectory) && !Directory.Exists(targetDirectory))
        {
            Directory.CreateDirectory(targetDirectory);
            logAction(targetDirectory, "MKDIR");
        }

        // 上書き先が隠し・システムファイルなら上書きしない。読み取り専用は外してから上書きする。
        if (overwrite && File.Exists(targetPath))
        {
            var attr = File.GetAttributes(targetPath);
            if ((attr & (FileAttributes.Hidden | FileAttributes.System)) != 0)
            {
                string attrText = (attr & FileAttributes.Hidden) != 0 && (attr & FileAttributes.System) != 0 ? "隠し+システム" :
                                  (attr & FileAttributes.Hidden) != 0 ? "隠し" : "システム";
                logAction($"上書き失敗: {sourceFilePath} -> {targetPath}\n  [原因: コピー先が{attrText}ファイルのため上書きできません]", "ERR");
                return CopyOutcome.Failed;
            }

            if ((attr & FileAttributes.ReadOnly) != 0)
            {
                RemoveReadOnlyIfPresent(targetPath);
            }
        }

        // コピーしてログを残す。
        try
        {
            await FileTransferWriter.CopyAsync(sourceFilePath, targetPath, overwrite, cancellationToken);
            logAction($"{sourceFilePath} -> {targetPath}", "COPY");
            return CopyOutcome.Copied;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logAction($"コピー失敗: {sourceFilePath} -> {targetPath}\n  [原因: {FormatFailureReason(ex)}]", "ERR");
            return CopyOutcome.Failed;
        }
    }

    /// <summary>
    /// 例外をログに出す失敗理由の文に直す。IOException は Win32 のエラー番号で見分ける。
    /// </summary>
    public static string FormatFailureReason(Exception ex)
    {
        if (ex is UnauthorizedAccessException)
        {
            return "アクセスが拒否されました (権限不足または属性制限)";
        }
        if (ex is PathTooLongException)
        {
            return "パスが長すぎます (最大文字数超過)";
        }
        if (ex is DirectoryNotFoundException)
        {
            return "フォルダーが見つかりません";
        }
        if (ex is FileNotFoundException fnfEx)
        {
            var detail = string.IsNullOrWhiteSpace(fnfEx.FileName) ? fnfEx.Message : $"{fnfEx.FileName} ({fnfEx.Message})";
            return string.IsNullOrWhiteSpace(detail)
                ? "ファイルが見つかりません"
                : $"ファイルが見つかりません: {detail}";
        }
        if (ex is IOException ioEx)
        {
            int hr = ioEx.HResult & 0xFFFF;
            return hr switch
            {
                32 => "ファイルが別のプロセスによって使用されています (プロセスロック中)",
                33 => "ファイルの一部が別のプロセスによってロックされています",
                80 => "ファイルが既に存在します",
                112 => "ディスクの空き容量が不足しています",
                206 => "ファイル名または拡張子が長すぎます",
                _ => string.IsNullOrWhiteSpace(ex.Message) ? "I/Oエラーが発生しました" : ex.Message
            };
        }
        return string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message;
    }

    /// <summary>
    /// 読み取り専用属性があれば外す。外せなくても続ける。
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
        }
        catch
        {
            // 属性を外せなくても無視する。
        }
    }


    /// <summary>
    /// 実ファイル同士の同名ファイル確認に出す情報を作る。サイズが同じときだけ内容を読み比べる。
    /// </summary>
    public static ConflictInfo BuildConflictInfo(FileInfo src, FileInfo dest)
    {
        var (sizeMsg, timeMsg) = FileConflicts.CompareSizeAndTime(src.Length, src.LastWriteTime, dest.Length, dest.LastWriteTime);

        // 内容はサイズが一致するときだけ読み比べる。
        bool identical = false;
        string statusMsg;

        if (src.Length != dest.Length)
        {
            statusMsg = "サイズ不一致";
        }
        else if (src.Length == 0)
        {
            identical = true;
            statusMsg = "完全一致 (空ファイル)";
        }
        else
        {
            identical = CompareFilesFully(src.FullName, dest.FullName, src.Length);
            statusMsg = identical ? "内容一致 (完全一致)" : "内容相違";
        }

        return new ConflictInfo(
            src,
            dest,
            statusMsg,
            sizeMsg,
            timeMsg,
            identical
        );
    }

    /// <summary>
    /// 2つのファイルを先頭から最後まで読み比べる。読めなければ不一致とする。
    /// </summary>
    private static bool CompareFilesFully(string path1, string path2, long length)
    {
        try
        {
            using var s1 = new FileStream(path1, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var s2 = new FileStream(path2, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            // 開いた時点のサイズが違えば不一致。同じなら 64KB ずつ比べる。
            if (s1.Length != length || s2.Length != length) return false;
            byte[] b1 = new byte[65536];
            byte[] b2 = new byte[65536];
            while (length > 0)
            {
                int count = (int)Math.Min(b1.Length, length);
                s1.ReadExactly(b1.AsSpan(0, count));
                s2.ReadExactly(b2.AsSpan(0, count));
                if (!b1.AsSpan(0, count).SequenceEqual(b2.AsSpan(0, count))) return false;
                length -= count;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }
}
