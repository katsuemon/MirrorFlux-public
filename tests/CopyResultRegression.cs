using System.IO;
using MirrorFlux.Models;
using MirrorFlux.Services;

internal static class CopyResultRegression
{
    public static async Task Run(string root, Action<bool, string> check)
    {
        string source = Directory.CreateDirectory(Path.Combine(root, "copy-results-source")).FullName;
        string destination = Directory.CreateDirectory(Path.Combine(root, "copy-results-destination")).FullName;
        File.WriteAllText(Path.Combine(source, "good.txt"), "good");
        string locked = Path.Combine(source, "locked.txt"); File.WriteAllText(locked, "locked");
        var entry = new FileEntry(source, Path.GetFileName(source), true, false, 0, DateTime.Now);
        Task<ConflictResolution> Overwrite(ConflictInfo _) => Task.FromResult(new ConflictResolution(ConflictAction.Overwrite, false));
        using (var handle = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var partial = await FileCopyService.CopyEntriesAsync([entry], destination, Overwrite, (_, _) => { });
            check(partial.CopiedCount == 1 && partial.ErrorCount == 1 && partial.Status == FileTransferStatus.Partial,
                "Folder copy aggregates locked child as partial failure");
            check(partial.SuccessfulPaths.Count == 0, "Incomplete top-level folder is not eligible for unmarking");
            var pane = new MirrorFlux.ViewModels.FilePaneViewModel("test");
            await pane.LoadAsync(root);
            pane.Entries.First(e => e.FullPath == source).IsMarked = true;
            pane.Unmark(partial.SuccessfulPaths);
            await pane.LoadAsync(root, preserveMarks: true);
            check(pane.Entries.First(e => e.FullPath == source).IsMarked, "Partial copy marks survive source-pane reload");
            check(!Directory.EnumerateFiles(destination, ".mf-copy-*.tmp", SearchOption.AllDirectories).Any(), "Failed copies leave no staging files");
        }
        var complete = await FileCopyService.CopyEntriesAsync([entry], destination, Overwrite, (_, _) => { });
        check(complete.CopiedCount == 2 && complete.ErrorCount == 0 && complete.Status == FileTransferStatus.Complete && complete.SuccessfulPaths.Contains(source),
            "Retry completes folder and marks it successful");
        var skipped = await FileCopyService.CopyEntriesAsync([entry], destination,
            _ => Task.FromResult(new ConflictResolution(ConflictAction.Skip, false)), (_, _) => { });
        check(skipped.SkippedCount == 2 && skipped.Status == FileTransferStatus.Skipped && skipped.SuccessfulPaths.Count == 0,
            "Skipped children do not become successful empty-folder copies");
        string missing = Path.Combine(root, "missing-source");
        var failed = await FileCopyService.CopyEntriesAsync([new(missing, "missing", true, false, 0, DateTime.Now)], destination, Overwrite, (_, _) => { });
        check(failed.Status == FileTransferStatus.Failed && failed.CopiedCount == 0 && failed.SuccessfulPaths.Count == 0,
            "Directory enumeration failure is not an empty-directory success");
        string target = Path.Combine(destination, "protected.txt"); File.WriteAllText(target, "original");
        string? error = null;
        using (var lockedTarget = new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var result = await FileCopyService.CopyEntriesAsync([new(Path.Combine(source, "good.txt"), "protected.txt", false, false, 4, DateTime.Now)], destination, Overwrite,
                (message, level) => { if (level == "ERR") error = message; });
            check(result.ErrorCount == 1 && error?.Contains("プロセス") == true, "Locked destination reports sharing violation");
        }
        check(File.ReadAllText(target) == "original", "Locked destination remains unchanged");
        using (var cts = new CancellationTokenSource())
        {
            cts.Cancel();
            bool canceled = false;
            try { await FileTransferWriter.CopyAsync(Path.Combine(source, "good.txt"), target, true, cts.Token); }
            catch (OperationCanceledException) { canceled = true; }
            check(canceled && File.ReadAllText(target) == "original", "Canceled staged copy preserves existing destination");
        }
        string zipPath = Path.Combine(root, "copy-result.zip");
        using (var zip = System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Create))
            foreach (string name in new[] { "folder/a.txt", "folder/b.txt" })
                using (var writer = new StreamWriter(zip.CreateEntry(name).Open())) writer.Write("new");
        string extractRoot = Directory.CreateDirectory(Path.Combine(root, "partial-extraction")).FullName;
        Directory.CreateDirectory(Path.Combine(extractRoot, "folder"));
        File.WriteAllText(Path.Combine(extractRoot, "folder", "a.txt"), "keep");
        var folder = new FileEntry(zipPath + "\\folder", "folder", true, false, 0, DateTime.Now);
        var extracted = await ArchiveService.ExtractEntriesAsync(zipPath, [folder], extractRoot,
            _ => Task.FromResult(new ConflictResolution(ConflictAction.Skip, false)), (_, _) => { });
        check(extracted.CopiedCount == 1 && extracted.SkippedCount == 1 && extracted.SuccessfulPaths.Count == 0,
            "Partial archive extraction does not unmark the folder");
        extracted = await ArchiveService.ExtractEntriesAsync(zipPath, [folder], extractRoot, Overwrite, (_, _) => { });
        check(extracted.CopiedCount == 2 && extracted.SuccessfulPaths.Count == 1,
            "Complete archive extraction marks folder successful");

        string largeSource = Path.Combine(source, "large.bin");
        string largeTarget = Path.Combine(destination, "large.bin");
        const int largeLength = 11 * 1024 * 1024;
        using (var stream = File.Create(largeSource)) stream.SetLength(largeLength);
        File.Copy(largeSource, largeTarget);
        using (var stream = new FileStream(largeTarget, FileMode.Open, FileAccess.Write))
        {
            stream.Position = largeLength / 2;
            stream.WriteByte(1);
        }
        ConflictInfo? largeConflict = null;
        var largeEntry = new FileEntry(largeSource, "large.bin", false, false, largeLength, DateTime.Now);
        Task<ConflictResolution> CaptureAndSkip(ConflictInfo conflict)
        {
            largeConflict = conflict;
            return Task.FromResult(new ConflictResolution(ConflictAction.Skip, false));
        }
        await FileCopyService.CopyEntriesAsync([largeEntry], destination, CaptureAndSkip, (_, _) => { });
        check(largeConflict is { IsContentIdentical: false } && largeConflict.StatusMessage == "内容相違",
            "large files differing only in the middle are not reported identical");
        File.Copy(largeSource, largeTarget, overwrite: true);
        largeConflict = null;
        await FileCopyService.CopyEntriesAsync([largeEntry], destination, CaptureAndSkip, (_, _) => { });
        check(largeConflict is { IsContentIdentical: true } && largeConflict.StatusMessage.Contains("完全一致"),
            "large files are reported identical only after a full comparison");

        string safeArchive = Path.Combine(root, "safe-extract.zip");
        using (var zip = System.IO.Compression.ZipFile.Open(safeArchive, System.IO.Compression.ZipArchiveMode.Create))
        using (var writer = new StreamWriter(zip.CreateEntry("keep.txt").Open())) writer.Write("replacement");
        string safeRoot = Directory.CreateDirectory(Path.Combine(root, "safe-extract-target")).FullName;
        string safeTarget = Path.Combine(safeRoot, "keep.txt");
        File.WriteAllText(safeTarget, "original");
        var safeEntry = new FileEntry(safeArchive + "\\keep.txt", "keep.txt", false, false, 11, DateTime.Now);
        bool extractionFailed = false;
        using (var lockedTarget = new FileStream(safeTarget, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            try { await ArchiveService.ExtractEntriesAsync(safeArchive, [safeEntry], safeRoot, Overwrite, (_, _) => { }); }
            catch (IOException) { extractionFailed = true; }
            catch (UnauthorizedAccessException) { extractionFailed = true; }
        }
        check(extractionFailed && File.ReadAllText(safeTarget) == "original" &&
              !Directory.EnumerateFiles(safeRoot, ".mf-extract-*.tmp").Any(),
            "failed archive overwrite keeps the original file and removes staging data");
        File.SetAttributes(safeTarget, FileAttributes.ReadOnly);
        try
        {
            await ArchiveService.ExtractEntriesAsync(safeArchive, [safeEntry], safeRoot, Overwrite, (_, _) => { });
            check(File.ReadAllText(safeTarget) == "replacement" &&
                  !Directory.EnumerateFiles(safeRoot, ".mf-extract-*.tmp").Any(),
                "archive overwrite replaces a read-only destination only after complete extraction");
        }
        finally { File.SetAttributes(safeTarget, FileAttributes.Normal); }

        var session = new ConflictSession();
        int resolutionCalls = 0;
        Task<ConflictResolution> ApplyToAll(ConflictInfo _)
        {
            resolutionCalls++;
            return Task.FromResult(new ConflictResolution(ConflictAction.Skip, true));
        }
        ConflictInfo MakeInfo() => new(new FileInfo(largeSource), new FileInfo(largeTarget), "", "", "", false);
        await session.ResolveAsync(MakeInfo, ApplyToAll);
        await session.ResolveAsync(MakeInfo, ApplyToAll);
        check(resolutionCalls == 1, "apply-to-all conflict choice is reused within the batch");
        var renameSession = new ConflictSession();
        int renameCalls = 0;
        await renameSession.ResolveAsync(MakeInfo, _ =>
        {
            renameCalls++;
            return Task.FromResult(new ConflictResolution(ConflictAction.Rename, true, "other.bin"));
        });
        await renameSession.ResolveAsync(MakeInfo, _ =>
        {
            renameCalls++;
            return Task.FromResult(new ConflictResolution(ConflictAction.Skip, false));
        });
        check(renameCalls == 2, "rename conflict choice does not persist across entries");
        check(Path.GetFileName(FileOperationPaths.GenerateAutoNumberedPath(largeTarget)) == "large_1.bin",
            "copy, move and extraction share numbered target naming");

        string moveSource = Path.Combine(source, "update-move.txt");
        string moveTarget = Path.Combine(destination, "update-move.txt");
        File.WriteAllText(moveSource, "new");
        File.WriteAllText(moveTarget, "old");
        File.SetLastWriteTimeUtc(moveSource, DateTime.UtcNow.AddMinutes(-1));
        File.SetLastWriteTimeUtc(moveTarget, DateTime.UtcNow.AddMinutes(-2));
        var moveEntry = new FileEntry(moveSource, "update-move.txt", false, false, 3, DateTime.Now);
        var updated = await FileMoveService.MoveEntriesAsync([moveEntry], destination,
            _ => Task.FromResult(new ConflictResolution(ConflictAction.Update, false)), (_, _) => { });
        check(updated.MovedCount == 1 && File.ReadAllText(moveTarget) == "new" && !File.Exists(moveSource),
            "update move uses the shared overwrite path when source is newer");
        File.WriteAllText(moveSource, "stale");
        File.SetLastWriteTimeUtc(moveSource, DateTime.UtcNow.AddMinutes(-3));
        updated = await FileMoveService.MoveEntriesAsync([moveEntry], destination,
            _ => Task.FromResult(new ConflictResolution(ConflictAction.Update, false)), (_, _) => { });
        check(updated.SkippedCount == 1 && File.ReadAllText(moveTarget) == "new" && File.Exists(moveSource),
            "update move still skips an older source");

        string damagedArchive = Path.Combine(root, "damaged-extract.zip");
        byte[] payload = new byte[256 * 1024];
        new Random(42).NextBytes(payload);
        using (var zip = System.IO.Compression.ZipFile.Open(damagedArchive, System.IO.Compression.ZipArchiveMode.Create))
        using (var stream = zip.CreateEntry("damaged.bin").Open()) stream.Write(payload);
        long compressedLength;
        using (var zip = System.IO.Compression.ZipFile.OpenRead(damagedArchive))
            compressedLength = zip.GetEntry("damaged.bin")!.CompressedLength;
        byte[] archiveBytes = File.ReadAllBytes(damagedArchive);
        int payloadOffset = 30 + BitConverter.ToUInt16(archiveBytes, 26) + BitConverter.ToUInt16(archiveBytes, 28);
        archiveBytes[payloadOffset + (int)(compressedLength / 2)] ^= 0xff;
        File.WriteAllBytes(damagedArchive, archiveBytes);
        string damagedRoot = Directory.CreateDirectory(Path.Combine(root, "damaged-extract-target")).FullName;
        string damagedTarget = Path.Combine(damagedRoot, "damaged.bin");
        File.WriteAllText(damagedTarget, "original");
        bool damagedFailed = false;
        try
        {
            await ArchiveService.ExtractEntriesAsync(damagedArchive,
                [new FileEntry(damagedArchive + "\\damaged.bin", "damaged.bin", false, false, payload.Length, DateTime.Now)],
                damagedRoot, Overwrite, (_, _) => { });
        }
        catch (Exception ex) when (ex is IOException ||
            ex.GetType().FullName == "SharpCompress.Compressors.Deflate.ZlibException") { damagedFailed = true; }
        check(damagedFailed && File.ReadAllText(damagedTarget) == "original" &&
              !Directory.EnumerateFiles(damagedRoot, ".mf-extract-*.tmp").Any(),
            "damaged archive entry cannot partially replace an existing file");
    }
}
