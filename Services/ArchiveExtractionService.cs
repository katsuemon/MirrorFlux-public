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

using static MirrorFlux.Services.ArchiveService;

namespace MirrorFlux.Services;

/// <summary>
/// アーカイブ内の項目を実フォルダーへ展開する。
/// </summary>
internal static class ArchiveExtractionService
{
    /// <summary>
    /// 展開元の1項目。Key は「\」区切りで前後に区切りを付けない。Write は出力先へ中身を書き、アーカイブを開いている間だけ使える。
    /// </summary>
    private sealed record ExtractSource(string Key, bool IsDirectory, long Size, DateTime Time, Func<Stream, CancellationToken, Task> Write);

    /// <summary>
    /// アーカイブを開き、選択された項目を展開先フォルダーへ展開する。
    /// </summary>
    public static async Task<(int CopiedCount, int SkippedCount, List<string> SuccessfulPaths)> ExtractEntriesAsync(
        string archiveFilePath,
        IReadOnlyList<FileEntry> entries,
        string destinationDir,
        Func<ConflictInfo, Task<ConflictResolution>> conflictResolver,
        Action<string, string> logAction,
        CancellationToken cancellationToken = default)
    {
        // 展開先フォルダーがなければ作る。
        if (!Directory.Exists(destinationDir))
        {
            Directory.CreateDirectory(destinationDir);
            logAction(destinationDir, "MKDIR");
        }

        // 形式ごとに違うのは開き方と項目の読み方だけ。展開の手順は ExtractSourcesAsync で共通にする。
        if (ArchiveService.IsLhaExtension(Path.GetExtension(archiveFilePath)))
        {
            using var fs = File.OpenRead(archiveFilePath);
            using var archive = new LhaArchive(fs, new LhaOptions { Encoding = GetLhaEncoding() });
            var sources = GetLhaHeadersSync(archive).Select(header => new ExtractSource(
                ArchiveService.WindowsArchiveKey(header.Name), ArchiveService.IsLhaDirectory(header), header.OriginalSize,
                header.UnixLastModifiedStamp != DateTime.MinValue ? ArchiveService.LhaLocalTime(header.UnixLastModifiedStamp) : DateTime.MinValue,
                (output, _) =>
                {
                    archive.Extract(header, output);
                    return Task.CompletedTask;
                })).ToList();
            return await ExtractSourcesAsync(sources, entries, destinationDir, conflictResolver, logAction, cancellationToken);
        }
        else
        {
            using var archive = RarArchiveAdapter.Open(archiveFilePath, GetReaderOptions(archiveFilePath));
            var sources = archive.Entries.Select(entry => new ExtractSource(
                ArchiveService.WindowsArchiveKey(entry.Key), entry.IsDirectory, entry.Size, entry.LastModifiedTime ?? DateTime.MinValue,
                async (output, token) =>
                {
                    using var input = entry.OpenEntryStream();
                    await input.CopyToAsync(output, token);
                })).ToList();
            return await ExtractSourcesAsync(sources, entries, destinationDir, conflictResolver, logAction, cancellationToken);
        }
    }

    /// <summary>
    /// 選択された項目（ファイル、またはフォルダーごと）を destinationDir へ展開する。
    /// フォルダーは中の項目をすべて展開し、1件もスキップせず1件以上展開できたときだけ成功に数える。
    /// 同名ファイルの確認は1回の操作で共有し、「すべてに適用」が効くようにする。
    /// </summary>
    private static async Task<(int CopiedCount, int SkippedCount, List<string> SuccessfulPaths)> ExtractSourcesAsync(
        IReadOnlyList<ExtractSource> sources,
        IReadOnlyList<FileEntry> entries,
        string destinationDir,
        Func<ConflictInfo, Task<ConflictResolution>> conflictResolver,
        Action<string, string> logAction,
        CancellationToken cancellationToken)
    {
        int copiedCount = 0;
        int skippedCount = 0;
        var successfulPaths = new List<string>();
        var context = new ConflictSession();
        FileOperationJob.Current?.SetPlan(PlanExtraction(sources, entries));

        foreach (var entry in entries)
        {
            // 親フォルダー行とアーカイブ外のパスは飛ばし、アーカイブ内のキーに直す。
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.IsParent) continue;
            if (!TrySplitArchivePath(entry.FullPath, out _, out var internalPath)) continue;
            var normalizedInternal = ArchiveService.WindowsArchiveKey(internalPath);

            if (entry.IsDirectory)
            {
                // フォルダー内のすべての項目を再帰的に展開する。
                var dirPrefix = string.IsNullOrEmpty(normalizedInternal) ? string.Empty : normalizedInternal + "\\";
                bool dirSuccess = true;
                bool anyCopied = false;
                foreach (var source in sources.Where(source =>
                    dirPrefix.Length == 0 || source.Key.StartsWith(dirPrefix, StringComparison.OrdinalIgnoreCase)))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var relPathInDir = string.IsNullOrEmpty(normalizedInternal)
                        ? source.Key
                        : source.Key.Substring(normalizedInternal.Length).TrimStart('\\');
                    var targetFilePath = FileOperationPaths.ExtractionTarget(destinationDir, entry.Name, relPathInDir);

                    // フォルダー項目は展開先にフォルダーを作るだけ。
                    if (source.IsDirectory)
                    {
                        if (!Directory.Exists(targetFilePath))
                        {
                            Directory.CreateDirectory(targetFilePath);
                            logAction(targetFilePath, "MKDIR");
                        }
                        continue;
                    }

                    // ファイルを展開し、結果を数える。
                    var outcome = await ExtractFileAsync(targetFilePath, Path.Combine(entry.FullPath, relPathInDir),
                        source.Size, source.Time, source.Write, conflictResolver, context, logAction, cancellationToken);
                    if (outcome == CopyOutcome.Copied)
                    {
                        copiedCount++;
                        anyCopied = true;
                    }
                    else if (outcome == CopyOutcome.Skipped)
                    {
                        skippedCount++;
                        dirSuccess = false;
                    }
                    await Task.Delay(5, cancellationToken);
                }

                if (dirSuccess && anyCopied) { successfulPaths.Add(entry.FullPath); FileOperationJob.Current?.ItemCompleted(entry.FullPath); }
            }
            else
            {
                // 単一ファイル。大文字・小文字だけ違う項目があるときは完全に同じ名前を優先する。
                var source = sources.Where(source => !source.IsDirectory &&
                        string.Equals(source.Key, normalizedInternal, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(source => string.Equals(source.Key, normalizedInternal, StringComparison.Ordinal))
                    .FirstOrDefault();
                if (source is not null)
                {
                    var targetFilePath = FileOperationPaths.ExtractionTarget(destinationDir, entry.Name);
                    var outcome = await ExtractFileAsync(targetFilePath, entry.FullPath, source.Size, source.Time, source.Write,
                        conflictResolver, context, logAction, cancellationToken);
                    if (outcome == CopyOutcome.Copied)
                    {
                        copiedCount++;
                        successfulPaths.Add(entry.FullPath);
                        FileOperationJob.Current?.ItemCompleted(entry.FullPath);
                    }
                    else if (outcome == CopyOutcome.Skipped)
                    {
                        skippedCount++;
                    }
                }
                await Task.Delay(5, cancellationToken);
            }
        }

        return (copiedCount, skippedCount, successfulPaths);
    }

    /// <summary>
    /// 1ファイルの展開結果。
    /// </summary>
    private enum CopyOutcome
    {
        Copied,
        Skipped,
        Canceled
    }

    /// <summary>展開するファイルの一覧を、展開と同じ順で作る（進行欄の件数・量・次のファイルに使う）。</summary>
    private static List<(string Path, long Size)> PlanExtraction(IReadOnlyList<ExtractSource> sources, IReadOnlyList<FileEntry> entries)
    {
        var plan = new List<(string, long)>();
        foreach (var entry in entries)
        {
            if (entry.IsParent || !TrySplitArchivePath(entry.FullPath, out _, out var internalPath)) continue;
            var normalizedInternal = ArchiveService.WindowsArchiveKey(internalPath);
            if (entry.IsDirectory)
            {
                var dirPrefix = string.IsNullOrEmpty(normalizedInternal) ? string.Empty : normalizedInternal + "\\";
                foreach (var source in sources.Where(source => !source.IsDirectory &&
                    (dirPrefix.Length == 0 || source.Key.StartsWith(dirPrefix, StringComparison.OrdinalIgnoreCase))))
                {
                    var relPathInDir = string.IsNullOrEmpty(normalizedInternal) ? source.Key : source.Key.Substring(normalizedInternal.Length).TrimStart('\\');
                    plan.Add((Path.Combine(entry.FullPath, relPathInDir), source.Size));
                }
            }
            else plan.Add((entry.FullPath, entry.Length));
        }
        return plan;
    }

    /// <summary>
    /// 1ファイルの展開を、実行中のファイル操作の進み具合（処理中のファイルと書いた量）として記録しながら行う。
    /// </summary>
    private static async Task<CopyOutcome> ExtractFileAsync(
        string targetFilePath,
        string virtualSourcePath,
        long sourceSize,
        DateTime fileTime,
        Func<Stream, CancellationToken, Task> writeContent,
        Func<ConflictInfo, Task<ConflictResolution>> conflictResolver,
        ConflictSession context,
        Action<string, string> logAction,
        CancellationToken cancellationToken)
    {
        var job = FileOperationJob.Current;
        job?.BeginFile(virtualSourcePath, sourceSize);
        try
        {
            Func<Stream, CancellationToken, Task> write = job == null ? writeContent
                : (output, token) => writeContent(new ProgressWriteStream(output, job), token);
            return await ExtractFileCoreAsync(targetFilePath, virtualSourcePath, sourceSize, fileTime, write,
                conflictResolver, context, logAction, cancellationToken);
        }
        finally { job?.EndFile(); }
    }

    /// <summary>
    /// 1ファイルを一時ファイルへ書き出してから展開先へ置き換える。書き込み途中で中止・失敗しても既存の同名ファイルは壊さない。
    /// </summary>
    private static async Task<CopyOutcome> ExtractFileCoreAsync(
        string targetFilePath,
        string virtualSourcePath,
        long sourceSize,
        DateTime fileTime,
        Func<Stream, CancellationToken, Task> writeContent,
        Func<ConflictInfo, Task<ConflictResolution>> conflictResolver,
        ConflictSession context,
        Action<string, string> logAction,
        CancellationToken cancellationToken)
    {
        // 展開先のフォルダーを用意する。
        cancellationToken.ThrowIfCancellationRequested();
        string? targetDir = Path.GetDirectoryName(targetFilePath);
        if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir))
        {
            Directory.CreateDirectory(targetDir);
            logAction(targetDir, "MKDIR");
        }

        // 同名ファイルがあれば確認し、書き込み先と上書きの可否を決める。
        var resolved = await FileConflicts.ResolveTargetAsync(virtualSourcePath, targetFilePath, fileTime,
            destInfo => FileConflicts.Uncompared(virtualSourcePath, sourceSize, fileTime, destInfo, destInfo.Length, destInfo.LastWriteTime),
            context, conflictResolver, logAction);
        if (resolved is not { } target) return CopyOutcome.Skipped;
        string targetPath = target.Path;
        bool overwrite = target.Overwrite;

        FileOperationPaths.RejectLinks(targetPath);
        string temporary = Path.Combine(Path.GetDirectoryName(targetPath)!, $".mf-extract-{Guid.NewGuid():N}.tmp");
        bool published = false;
        FileAttributes? originalAttributes = null;
        try
        {
            // 同じフォルダーの一時ファイルへ中身を書き、更新日時を合わせる。
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 65536, useAsync: true))
            {
                await writeContent(output, cancellationToken);
                await output.FlushAsync(cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (fileTime != DateTime.MinValue)
            {
                try { File.SetLastWriteTime(temporary, fileTime); } catch { }
            }

            // 置き換える直前にリンクを再確認し、読み取り専用属性を一時的に外す。
            FileOperationPaths.RejectLinks(targetPath);
            cancellationToken.ThrowIfCancellationRequested();
            if (overwrite && File.Exists(targetPath))
            {
                originalAttributes = File.GetAttributes(targetPath);
                if ((originalAttributes.Value & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(targetPath, originalAttributes.Value & ~FileAttributes.ReadOnly);
            }
            // 一時ファイルを移動して一度に置き換える。失敗したら属性を元に戻す。
            try
            {
                File.Move(temporary, targetPath, overwrite);
                published = true;
            }
            catch
            {
                if (originalAttributes.HasValue && File.Exists(targetPath))
                    try { File.SetAttributes(targetPath, originalAttributes.Value); } catch { }
                throw;
            }

            logAction($"{virtualSourcePath} -> {targetPath}", "COPY");
            return CopyOutcome.Copied;
        }
        finally
        {
            // 置き換えに至らなかった一時ファイルを片付ける。
            if (!published && File.Exists(temporary))
                try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
