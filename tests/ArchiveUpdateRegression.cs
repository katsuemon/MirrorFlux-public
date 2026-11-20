using System.IO;
using System.IO.Compression;
using MirrorFlux.Models;
using MirrorFlux.Services;

internal static class ArchiveUpdateRegression
{
    private static FileEntry Entry(string path) => new(path, Path.GetFileName(path), Directory.Exists(path), false,
        File.Exists(path) ? new FileInfo(path).Length : 0, File.GetLastWriteTime(path));
    private static Task<ConflictResolution> Overwrite(ConflictInfo _) => Task.FromResult(new ConflictResolution(ConflictAction.Overwrite, false));
    private static Task<ConflictResolution> Skip(ConflictInfo _) => Task.FromResult(new ConflictResolution(ConflictAction.Skip, false));
    private static void MakeZip(string path, params (string Name, string Text)[] files)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var file in files)
        {
            using var writer = new StreamWriter(zip.CreateEntry(file.Name).Open());
            writer.Write(file.Text);
        }
    }
    private static string Read(string path, string key)
    {
        using var zip = ZipFile.OpenRead(path);
        using var reader = new StreamReader(zip.GetEntry(key)!.Open());
        return reader.ReadToEnd();
    }
    private static bool NoStage(string path) => !Directory.EnumerateFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".tmp.*").Any();

    public static async Task Run(string root, Action<bool, string> check)
    {
        string area = Directory.CreateDirectory(Path.Combine(root, "archive-update")).FullName;
        string source = Directory.CreateDirectory(Path.Combine(area, "source")).FullName;
        string item = Path.Combine(source, "item.txt");
        string added = Path.Combine(source, "added.txt");
        File.WriteAllText(item, "replacement");
        File.WriteAllText(added, "new-content");

        string failed = Path.Combine(area, "failed.zip");
        MakeZip(failed, ("item.txt", "original"), ("keep.txt", "keep"));
        byte[] before = File.ReadAllBytes(failed);
        var logs = new List<string>();
        bool threw = false;
        using (var locked = new FileStream(item, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            try { await ArchiveService.AddEntriesToArchiveAsync(failed, "", [Entry(added), Entry(item)],
                Overwrite, (_, tag) => logs.Add(tag)); }
            catch (IOException) { threw = true; }
        }
        check(threw && File.ReadAllBytes(failed).SequenceEqual(before), "ZIP source failure rolls back the entire batch byte-for-byte");
        check(NoStage(failed) && !logs.Contains("COPY"), "ZIP failed batch removes staging and emits no successful copy logs");

        string canceled = Path.Combine(area, "canceled.zip");
        MakeZip(canceled, ("item.txt", "original"));
        before = File.ReadAllBytes(canceled);
        using (var cts = new CancellationTokenSource())
        {
            threw = false;
            try { await ArchiveService.AddEntriesToArchiveAsync(canceled, "", [Entry(added), Entry(item)],
                info => { cts.Cancel(); return Overwrite(info); }, (_, _) => { }, cts.Token); }
            catch (OperationCanceledException) { threw = true; }
            check(threw && before.SequenceEqual(File.ReadAllBytes(canceled)) && NoStage(canceled),
                "ZIP token cancellation after an earlier copy preserves the original");
        }
        threw = false;
        try { await ArchiveService.AddEntriesToArchiveAsync(canceled, "", [Entry(added), Entry(item)],
            _ => Task.FromResult(new ConflictResolution(ConflictAction.Cancel, false)), (_, _) => { }); }
        catch (OperationCanceledException) { threw = true; }
        check(threw && before.SequenceEqual(File.ReadAllBytes(canceled)) && NoStage(canceled),
            "ZIP conflict cancellation rolls back earlier copies");

        string lockedZip = Path.Combine(area, "locked.zip");
        MakeZip(lockedZip, ("item.txt", "original"));
        before = File.ReadAllBytes(lockedZip);
        threw = false;
        using (var locked = new FileStream(lockedZip, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            try { await ArchiveService.AddEntriesToArchiveAsync(lockedZip, "", [Entry(item)], Overwrite, (_, _) => { }); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { threw = true; }
        }
        check(threw && before.SequenceEqual(File.ReadAllBytes(lockedZip)) && NoStage(lockedZip),
            "ZIP publish failure preserves the original and removes staging");

        string renamed = Path.Combine(area, "renamed.zip");
        MakeZip(renamed, ("item.txt", "original"), ("other.txt", "other-original"));
        int prompts = 0;
        before = File.ReadAllBytes(renamed);
        var result = await ArchiveService.AddEntriesToArchiveAsync(renamed, "", [Entry(item)], _ =>
            Task.FromResult(++prompts == 1 ? new ConflictResolution(ConflictAction.Rename, true, "OTHER.txt") :
                new ConflictResolution(ConflictAction.Skip, false)), (_, _) => { });
        check(prompts == 2 && result.CopiedCount == 0 && result.SkippedCount == 1 && result.SuccessfulPaths.Count == 0,
            "ZIP rename rechecks a case-insensitive collision and permits skipping");
        check(before.SequenceEqual(File.ReadAllBytes(renamed)) && NoStage(renamed),
            "ZIP all-skipped rename leaves both existing entries untouched");
        prompts = 0;
        result = await ArchiveService.AddEntriesToArchiveAsync(renamed, "", [Entry(item)], _ =>
            Task.FromResult(++prompts == 1 ? new ConflictResolution(ConflictAction.Rename, false, "other.txt") :
                new ConflictResolution(ConflictAction.Overwrite, false)), (_, _) => { });
        check(prompts == 2 && Read(renamed, "other.txt") == "replacement" && Read(renamed, "item.txt") == "original",
            "ZIP renamed target is overwritten only after its own approval");
        check(result.CopiedCount == 1 && result.SuccessfulPaths.SequenceEqual(new[] { item }), "ZIP committed copy reports its successful source");

        before = File.ReadAllBytes(renamed);
        threw = false;
        try { await ArchiveService.AddEntriesToArchiveAsync(renamed, "", [Entry(item)],
            _ => Task.FromResult(new ConflictResolution(ConflictAction.Rename, false, "../escape.txt")), (_, _) => { }); }
        catch (IOException) { threw = true; }
        check(threw && before.SequenceEqual(File.ReadAllBytes(renamed)) && NoStage(renamed), "ZIP rejects path-like rename without changing the archive");

        string first = Path.Combine(Directory.CreateDirectory(Path.Combine(area, "first")).FullName, "fresh.txt");
        string second = Path.Combine(Directory.CreateDirectory(Path.Combine(area, "second")).FullName, "fresh.txt");
        File.WriteAllText(first, "first"); File.WriteAllText(second, "second");
        prompts = 0;
        result = await ArchiveService.AddEntriesToArchiveAsync(renamed, "", [Entry(first), Entry(second)], _ =>
        { prompts++; return Task.FromResult(new ConflictResolution(ConflictAction.AutoRename, false)); }, (_, _) => { });
        check(prompts == 1 && result.CopiedCount == 2 && Read(renamed, "fresh.txt") == "first" && Read(renamed, "fresh_1.txt") == "second",
            "ZIP resolves collisions with files added earlier in the same batch");

        File.SetLastWriteTime(item, new DateTime(2000, 1, 1));
        result = await ArchiveService.AddEntriesToArchiveAsync(renamed, "", [Entry(item)],
            _ => Task.FromResult(new ConflictResolution(ConflictAction.Update, false)), (_, _) => { });
        check(result.SkippedCount == 1 && result.SuccessfulPaths.Count == 0 && Read(renamed, "item.txt") == "original",
            "ZIP update preserves newer existing content and keeps source marked");
        File.SetLastWriteTime(item, DateTime.Now);

        string applyZip = Path.Combine(area, "apply.zip");
        MakeZip(applyZip, ("item.txt", "old-item"), ("added.txt", "old-added"));
        prompts = 0;
        result = await ArchiveService.AddEntriesToArchiveAsync(applyZip, "", [Entry(item), Entry(added)], _ =>
        { prompts++; return Task.FromResult(new ConflictResolution(ConflictAction.Skip, true)); }, (_, _) => { });
        check(prompts == 1 && result.SkippedCount == 2 && result.SuccessfulPaths.Count == 0,
            "ZIP apply-to-all skip persists across items without clearing marks");

        foreach (string extension in new[] { ".zip", ".7z", ".lzh", ".lha", ".tar" })
        {
            string formatRoot = Directory.CreateDirectory(Path.Combine(area, extension[1..])).FullName;
            string folder = Directory.CreateDirectory(Path.Combine(formatRoot, "folder")).FullName;
            string a = Path.Combine(folder, "a.txt");
            File.WriteAllText(a, "old-a");
            string archive = Path.Combine(formatRoot, "partial" + extension);
            await ArchiveService.CreateArchiveFromEntriesAsync(archive, [Entry(folder)], 6, (_, _) => { });
            File.WriteAllText(a, "new-a");
            File.WriteAllText(Path.Combine(folder, "b.txt"), "new-b");
            string single = Path.Combine(formatRoot, "single.txt");
            File.WriteAllText(single, "single");
            result = await ArchiveService.AddEntriesToArchiveAsync(archive, "", [Entry(folder), Entry(single)], Skip, (_, _) => { });
            check(result.CopiedCount == 2 && result.SkippedCount == 1 && !result.SuccessfulPaths.Contains(folder) && result.SuccessfulPaths.Contains(single),
                $"{extension} partial folder stays marked while an independent completed file clears");
            result = await ArchiveService.AddEntriesToArchiveAsync(archive, "", [Entry(folder)], Overwrite, (_, _) => { });
            check(result.CopiedCount == 2 && result.SkippedCount == 0 && result.SuccessfulPaths.SequenceEqual(new[] { folder }),
                $"{extension} fully copied folder is reported once");
            result = await ArchiveService.AddEntriesToArchiveAsync(archive, "", [Entry(folder)], Skip, (_, _) => { });
            check(result.CopiedCount == 0 && result.SkippedCount == 2 && result.SuccessfulPaths.Count == 0,
                $"{extension} fully skipped folder stays marked");
        }

        // ZIPの削除・フォルダー作成は一時コピー上で行い、失敗時に原本を変えない。
        string zipEdit = Path.Combine(area, "edit.zip");
        MakeZip(zipEdit, ("item.txt", "original"), ("keep.txt", "keep"), ("dir/", ""));
        var zipItem = ArchiveService.ReadArchiveEntries(zipEdit, "").Single(e => e.Name == "item.txt");
        before = File.ReadAllBytes(zipEdit);
        threw = false;
        using (var reader = new FileStream(zipEdit, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            try { await ArchiveService.DeleteArchiveEntriesAsync(zipEdit, [zipItem], (_, _) => { }); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { threw = true; }
        }
        check(threw && before.SequenceEqual(File.ReadAllBytes(zipEdit)) && NoStage(zipEdit),
            "ZIP delete publish failure preserves the original and removes staging");
        await ArchiveService.CreateArchiveDirectoryAsync(zipEdit, "", "dir", (_, _) => { });
        check(before.SequenceEqual(File.ReadAllBytes(zipEdit)) && NoStage(zipEdit), "ZIP mkdir of an existing folder leaves the archive untouched");
        await ArchiveService.CreateArchiveDirectoryAsync(zipEdit, "", "made", (_, _) => { });
        check(await ArchiveService.DeleteArchiveEntriesAsync(zipEdit, [zipItem], (_, _) => { }) == 1 && NoStage(zipEdit) &&
            Read(zipEdit, "keep.txt") == "keep", "ZIP delete removes only the target through staging");
        using (var zip = ZipFile.OpenRead(zipEdit))
            check(zip.GetEntry("item.txt") == null && zip.GetEntry("made/") != null && zip.GetEntry("dir/") != null,
                "ZIP staged mkdir and delete keep folder entries");

        // 7zのフォルダー項目は、作成・追加・削除・フォルダー作成の後もフォルダーとして残す。
        string sevenRoot = Directory.CreateDirectory(Path.Combine(area, "seven-dirs")).FullName;
        string top = Directory.CreateDirectory(Path.Combine(sevenRoot, "top")).FullName;
        Directory.CreateDirectory(Path.Combine(top, "sub"));
        File.WriteAllText(Path.Combine(top, "a.txt"), "a");
        File.WriteAllBytes(Path.Combine(top, "empty.bin"), []);
        string seven = Path.Combine(sevenRoot, "dirs.7z");
        await ArchiveService.CreateArchiveFromEntriesAsync(seven, [Entry(top)], 6, (_, _) => { });
        bool SevenShape(params string[] directories)
        {
            using var archive = RarArchiveAdapter.Open(seven, new SharpCompress.Readers.ReaderOptions());
            var entries = archive.Entries.Select(e => (Key: (e.Key ?? "").Replace('\\', '/'), e.IsDirectory)).ToArray();
            return directories.All(d => entries.Any(e => e.IsDirectory && e.Key.Trim('/') == d)) &&
                entries.Where(e => !e.IsDirectory).All(e => !e.Key.EndsWith('/') && !directories.Contains(e.Key)) &&
                entries.Any(e => !e.IsDirectory && e.Key == "top/empty.bin");
        }
        check(SevenShape("top", "top/sub"), "7z create stores folders as folder entries and keeps empty files as files");
        await ArchiveService.CreateArchiveDirectoryAsync(seven, "top", "made", (_, _) => { });
        check(SevenShape("top", "top/sub", "top/made") && NoStage(seven), "7z mkdir writes a folder entry and keeps existing folders");
        result = await ArchiveService.AddEntriesToArchiveAsync(seven, "", [Entry(added)], Overwrite, (_, _) => { });
        check(result.CopiedCount == 1 && SevenShape("top", "top/sub", "top/made") && NoStage(seven), "7z add keeps existing folder entries");
        var sevenFile = ArchiveService.ReadArchiveEntries(seven, "top").Single(e => e.Name == "a.txt");
        check(await ArchiveService.DeleteArchiveEntriesAsync(seven, [sevenFile], (_, _) => { }) == 1 &&
            SevenShape("top", "top/sub", "top/made") && NoStage(seven), "7z delete keeps existing folder entries");

        before = File.ReadAllBytes(seven);
        logs.Clear();
        threw = false;
        try { await ArchiveService.AddEntriesToArchiveAsync(seven, "", [Entry(item), Entry(added)],
            _ => Task.FromResult(new ConflictResolution(ConflictAction.Cancel, false)), (_, tag) => logs.Add(tag)); }
        catch (OperationCanceledException) { threw = true; }
        check(threw && before.SequenceEqual(File.ReadAllBytes(seven)) && NoStage(seven) && !logs.Contains("COPY"),
            "7z conflict cancellation preserves the original, removes staging and logs no copy");

        await CaseAndRenameChecks(area, item, check);
        await LhaTimeChecks(area, check);
        await UiThreadAndBusyChecks(area, check);
    }

    private sealed class NonPumpingContext : SynchronizationContext
    {
        // 画面スレッドが待機中で、戻ってきた続きの処理を実行できない状況を再現する。
        public override void Post(SendOrPostCallback d, object? state) { }
    }

    private static async Task UiThreadAndBusyChecks(string area, Action<bool, string> check)
    {
        string root = Directory.CreateDirectory(Path.Combine(area, "ui-thread")).FullName;
        string zip = Path.Combine(root, "pictures.zip");
        MakeZip(zip, ("a.png", new string('a', 300000)), ("b.png", new string('b', 300000)));
        string? temp = await ArchiveService.ExtractEntryToTempFileAsync(Path.Combine(zip, "b.png"));
        bool extracted = false;
        if (temp != null)
        {
            File.Delete(temp);
            var thread = new Thread(() =>
            {
                SynchronizationContext.SetSynchronizationContext(new NonPumpingContext());
                extracted = ArchiveService.EnsureEntryExtracted(temp);
            }) { IsBackground = true };
            thread.Start();
            extracted = thread.Join(TimeSpan.FromSeconds(20)) && extracted && File.Exists(temp) && new FileInfo(temp).Length == 300000;
        }
        check(extracted, "ZIP entry extraction completes when the calling context cannot run continuations");

        // 壊れたアーカイブ内を指すジャンプ先は、例外ではなくエラー文字列で返す（実アプリで異常終了を確認した経路）。
        foreach (string extension in new[] { ".rar", ".zip", ".7z", ".lzh", ".tar" })
        {
            string broken = Path.Combine(root, "broken" + extension);
            File.WriteAllBytes(broken, Enumerable.Range(1, 200).Select(i => (byte)i).ToArray());
            bool resolved = true; string error = "";
            bool threwOnResolve = false;
            try { resolved = JumpListService.TryResolve(Path.Combine(broken, "sub"), out _, out error); }
            catch (Exception) { threwOnResolve = true; }
            check(!threwOnResolve && !resolved && error.Length > 0, $"Jump target inside a broken {extension} reports an error instead of throwing");
        }

        var vm = new MirrorFlux.ViewModels.MainViewModel(new SettingsStore(Path.Combine(root, "settings.ini")));
        var gate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = vm.RunFileOperationAsync(() => gate.Task);
        bool during = vm.IsFileOperationRunning;
        gate.SetResult(1);
        await running;
        bool afterSuccess = vm.IsFileOperationRunning;
        bool threw = false;
        try { await vm.RunFileOperationAsync<int>(() => throw new IOException("failed")); }
        catch (IOException) { threw = true; }
        check(during && !afterSuccess && threw && !vm.IsFileOperationRunning, "File operations report running only while their work is in progress");
    }

    private static async Task<string> Text(string archive, string key) =>
        System.Text.Encoding.UTF8.GetString(await ArchiveService.ExtractArchiveEntryBytesAsync(archive, key));
    private static string[] Names(string archive) =>
        ArchiveService.ReadArchiveEntries(archive, "").Where(e => !e.IsParent).Select(e => e.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();

    // 大文字・小文字だけ違う項目を巻き添えにしないこと、7Z・LZHの別名指定を検証・再確認すること。
    private static async Task CaseAndRenameChecks(string area, string item, Action<bool, string> check)
    {
        string root = Directory.CreateDirectory(Path.Combine(area, "case")).FullName;
        string lower = Path.Combine(Directory.CreateDirectory(Path.Combine(root, "source")).FullName, "readme");
        File.WriteAllText(lower, "new");
        foreach (string extension in new[] { ".zip", ".7z", ".lzh", ".tar" })
        {
            string archive = Path.Combine(root, "variants" + extension);
            var files = new (string Name, string Text)[] { ("README", "upper"), ("readme", "lower"), ("keep.txt", "keep") };
            if (extension == ".zip") MakeZip(archive, files);
            else if (extension == ".7z")
            {
                using var output = File.Create(archive);
                using var writer = new SharpCompress.Writers.SevenZip.SevenZipWriter(output, new SharpCompress.Writers.SevenZip.SevenZipWriterOptions());
                foreach (var file in files) writer.Write(file.Name, new MemoryStream(System.Text.Encoding.UTF8.GetBytes(file.Text)), DateTime.Now);
            }
            else if (extension == ".lzh")
                LhaArchiveWriter.Write(archive, files.ToDictionary(f => f.Name, f => System.Text.Encoding.UTF8.GetBytes(f.Text), StringComparer.Ordinal));
            else
            {
                using var output = File.Create(archive);
                using var writer = new System.Formats.Tar.TarWriter(output, System.Formats.Tar.TarEntryFormat.Pax);
                foreach (var file in files)
                    writer.WriteEntry(new System.Formats.Tar.PaxTarEntry(System.Formats.Tar.TarEntryType.RegularFile, file.Name)
                        { DataStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(file.Text)) });
            }
            check(Names(archive).SequenceEqual(new[] { "README", "keep.txt", "readme" }), $"{extension} lists case-variant files separately");
            check(await Text(archive, "readme") == "lower" && await Text(archive, "README") == "upper", $"{extension} opens the exact case-variant entry");
            await ArchiveService.CreateArchiveDirectoryAsync(archive, "", "made", (_, _) => { });
            check(await Text(archive, "readme") == "lower" && await Text(archive, "README") == "upper", $"{extension} mkdir keeps both case-variant entries");
            if (extension != ".tar")
            {
                var added = await ArchiveService.AddEntriesToArchiveAsync(archive, "", [Entry(lower)], Overwrite, (_, _) => { });
                check(added.CopiedCount == 1 && await Text(archive, "readme") == "new" && await Text(archive, "README") == "upper",
                    $"{extension} overwrite replaces only the exact-name entry");
            }
            var target = ArchiveService.ReadArchiveEntries(archive, "").Single(e => e.Name == "readme");
            check(await ArchiveService.DeleteArchiveEntriesAsync(archive, [target], (_, _) => { }) == 1 &&
                Names(archive).SequenceEqual(new[] { "README", "keep.txt", "made" }) && await Text(archive, "README") == "upper",
                $"{extension} delete removes only the selected case variant");
        }

        foreach (string extension in new[] { ".7z", ".lzh" })
        {
            string archive = Path.Combine(root, "rename" + extension);
            var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
                { ["item.txt"] = "original"u8.ToArray(), ["other.txt"] = "other-original"u8.ToArray() };
            if (extension == ".lzh") LhaArchiveWriter.Write(archive, files);
            else
            {
                using var output = File.Create(archive);
                using var writer = new SharpCompress.Writers.SevenZip.SevenZipWriter(output, new SharpCompress.Writers.SevenZip.SevenZipWriterOptions());
                foreach (var file in files) writer.Write(file.Key, new MemoryStream(file.Value), DateTime.Now);
            }
            byte[] before = File.ReadAllBytes(archive);
            int prompts = 0;
            var result = await ArchiveService.AddEntriesToArchiveAsync(archive, "", [Entry(item)], _ =>
                Task.FromResult(++prompts == 1 ? new ConflictResolution(ConflictAction.Rename, false, "OTHER.txt") :
                    new ConflictResolution(ConflictAction.Skip, false)), (_, _) => { });
            check(prompts == 2 && result.CopiedCount == 0 && result.SkippedCount == 1 && before.SequenceEqual(File.ReadAllBytes(archive)) && NoStage(archive),
                $"{extension} rename rechecks the new name and skipping leaves the archive untouched");
            bool threw = false;
            try { await ArchiveService.AddEntriesToArchiveAsync(archive, "", [Entry(item)],
                _ => Task.FromResult(new ConflictResolution(ConflictAction.Rename, false, "../escape.txt")), (_, _) => { }); }
            catch (IOException) { threw = true; }
            check(threw && before.SequenceEqual(File.ReadAllBytes(archive)) && NoStage(archive), $"{extension} rejects path-like rename without changing the archive");
            prompts = 0;
            result = await ArchiveService.AddEntriesToArchiveAsync(archive, "", [Entry(item)], _ =>
                Task.FromResult(++prompts == 1 ? new ConflictResolution(ConflictAction.Rename, false, "other.txt") :
                    new ConflictResolution(ConflictAction.Overwrite, false)), (_, _) => { });
            check(prompts == 2 && result.CopiedCount == 1 && await Text(archive, "other.txt") == File.ReadAllText(item) && await Text(archive, "item.txt") == "original",
                $"{extension} renamed target is overwritten only after its own approval");
            prompts = 0;
            result = await ArchiveService.AddEntriesToArchiveAsync(archive, "", [Entry(item)], _ =>
                Task.FromResult(++prompts == 1 ? new ConflictResolution(ConflictAction.Rename, false, "fresh.txt") :
                    new ConflictResolution(ConflictAction.Cancel, false)), (_, _) => { });
            check(prompts == 1 && result.CopiedCount == 1 && await Text(archive, "fresh.txt") == File.ReadAllText(item) && await Text(archive, "item.txt") == "original",
                $"{extension} rename to a free name adds without touching the original entry");
        }
    }

    // LZHを編集しても、触れていない項目の更新日時は変えない。
    private static async Task LhaTimeChecks(string area, Action<bool, string> check)
    {
        string root = Directory.CreateDirectory(Path.Combine(area, "lha-time")).FullName;
        string old = Path.Combine(root, "old.txt");
        string extra = Path.Combine(root, "extra.txt");
        File.WriteAllText(old, "old");
        File.WriteAllText(extra, "extra");
        var stamp = new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Local);
        var extraStamp = new DateTime(2010, 11, 12, 13, 14, 16, DateTimeKind.Local);
        File.SetLastWriteTime(old, stamp);
        File.SetLastWriteTime(extra, extraStamp);
        string archive = Path.Combine(root, "time.lzh");
        await ArchiveService.CreateArchiveFromEntriesAsync(archive, [Entry(old)], 6, (_, _) => { });
        bool Stamped(string name, DateTime expected) => Math.Abs((ArchiveService.ReadArchiveEntries(archive, "")
            .Single(e => e.Name == name).LastWriteTime - expected).TotalSeconds) <= 2;
        check(Stamped("old.txt", stamp), "LZH create stores the source modification time and lists it in local time");
        await ArchiveService.CreateArchiveDirectoryAsync(archive, "", "made", (_, _) => { });
        check(Stamped("old.txt", stamp), "LZH mkdir keeps existing modification times");
        await ArchiveService.AddEntriesToArchiveAsync(archive, "", [Entry(extra)], Overwrite, (_, _) => { });
        check(Stamped("old.txt", stamp) && Stamped("extra.txt", extraStamp), "LZH add keeps existing times and stores the new file time");
        var target = ArchiveService.ReadArchiveEntries(archive, "").Single(e => e.Name == "extra.txt");
        check(await ArchiveService.DeleteArchiveEntriesAsync(archive, [target], (_, _) => { }) == 1 && Stamped("old.txt", stamp),
            "LZH delete keeps the remaining modification times");
    }
}
