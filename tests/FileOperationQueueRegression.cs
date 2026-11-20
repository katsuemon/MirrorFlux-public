using System.IO;
using System.Windows.Input;
using MirrorFlux.Input;
using MirrorFlux.Models;
using MirrorFlux.Services;
using MirrorFlux.ViewModels;

/// <summary>ファイル操作の中止・順番待ち・進行欄の検査。</summary>
internal static class FileOperationQueueRegression
{
    private static FileEntry Entry(string path) => new(path, Path.GetFileName(path), Directory.Exists(path), false,
        File.Exists(path) ? new FileInfo(path).Length : 0, DateTime.Now);

    public static void Run(string root, Action<bool, string> check)
    {
        string dir = Path.Combine(root, "operation-queue");
        Directory.CreateDirectory(dir);
        try
        {
            CancelMidFile(dir, check);
            ProgressOfCopy(dir, check);
            QueueAndCancel(root, check);
            PanelLines(check);
            Keys(root, check);
        }
        finally { Directory.Delete(dir, true); }
    }

    // コピーをファイルの途中で中止しても、コピー先の既存ファイルは変わらず、書きかけの一時ファイルも残らない。
    private static void CancelMidFile(string dir, Action<bool, string> check)
    {
        string source = Path.Combine(dir, "big.bin"), destination = Path.Combine(dir, "dest.bin");
        const long size = 512L << 20;
        using (var stream = new FileStream(source, FileMode.Create)) stream.SetLength(size);
        File.WriteAllText(destination, "old");
        var job = new FileOperationJob("コピー", dir, dir, 1);
        job.MarkRunning();
        job.BeginFile(source, size);
        var copy = Task.Run(async () => { using (job.Enter()) await FileTransferWriter.CopyAsync(source, destination, true, job.Token); });
        while (!copy.IsCompleted && job.GetSnapshot(0).CurrentDone == 0) Thread.Sleep(0);
        long doneBeforeCancel = job.GetSnapshot(0).CurrentDone;
        job.Cancel();
        bool canceled = false;
        try { copy.GetAwaiter().GetResult(); } catch (OperationCanceledException) { canceled = true; }
        check(canceled && doneBeforeCancel is > 0 and < size, "Copy can be cancelled in the middle of a file");
        check(File.ReadAllText(destination) == "old" && !Directory.EnumerateFiles(dir, ".mf-copy-*.tmp").Any(),
            "Cancelled copy keeps the existing destination and removes its temporary file");
        File.Delete(source);
        File.Delete(destination);
    }

    // フォルダーのコピーは、始める前に数えた件数と処理した件数がそろい、終わった項目が記録される。
    private static void ProgressOfCopy(string dir, Action<bool, string> check)
    {
        string folder = Path.Combine(dir, "folder"), target = Path.Combine(dir, "target");
        Directory.CreateDirectory(Path.Combine(folder, "sub"));
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(folder, "a.txt"), "a");
        File.WriteAllText(Path.Combine(folder, "b.txt"), "bb");
        File.WriteAllText(Path.Combine(folder, "sub", "c.txt"), "ccc");
        var items = new[] { Entry(folder) };
        var job = new FileOperationJob("コピー", dir, target, 1);
        job.MarkRunning();
        Task.Run(async () =>
        {
            using (job.Enter())
            {
                job.PlanFileSystemItems(items);
                await FileCopyService.CopyEntriesAsync(items, target, _ => Task.FromResult(new ConflictResolution(ConflictAction.Skip, true)), (_, _) => { }, job.Token);
            }
        }).GetAwaiter().GetResult();
        var snapshot = job.GetSnapshot(3);
        check(job.Counts == (3, 3) && snapshot.TotalBytes == 6 && snapshot.DoneBytes == 6 && snapshot.Upcoming.Count == 0,
            "Copy progress counts the planned files and bytes in processing order");
        check(job.CompletedItems.SequenceEqual([folder]), "Copy records the marked item it finished");
        check(FileMoveService.IsSameVolume(@"C:\a\b.txt", @"c:\x") && !FileMoveService.IsSameVolume(@"C:\a", @"D:\a"),
            "Moves across drives are detected by the path root");
    }

    // 後から始めた操作は前の操作が終わるまで待ち、中止は実行中の操作だけに効く。
    private static void QueueAndCancel(string root, Action<bool, string> check)
    {
        var vm = new MainViewModel(new SettingsStore(Path.Combine(root, "operation-queue.ini")));
        var release = new TaskCompletionSource();
        bool secondStarted = false;
        var first = new FileOperationJob("コピー", @"C:\src", @"C:\dst", 2);
        var second = new FileOperationJob("移動", @"C:\work", @"D:\backup", 14);
        var firstTask = vm.RunFileOperationAsync(async () => { await release.Task; return 1; }, first);
        var secondTask = vm.RunFileOperationAsync(() => { secondStarted = true; return Task.FromResult(2); }, second);
        for (int i = 0; i < 200 && !first.IsRunning; i++) Thread.Sleep(5);
        Thread.Sleep(50);
        check(first.IsRunning && !second.IsRunning && !secondStarted && ReferenceEquals(vm.RunningFileJob, first) && vm.IsFileOperationRunning,
            "A file operation started during another one waits its turn");
        vm.RequestCancelFileOperation(_ => true);
        check(first.IsCancellationRequested && !second.IsCancellationRequested, "Cancel stops only the running operation");
        release.SetResult();
        firstTask.GetAwaiter().GetResult();
        secondTask.GetAwaiter().GetResult();
        check(secondStarted && !vm.IsFileOperationRunning, "The waiting operation runs after the running one ends");
        vm.RequestCancelFileOperation(_ => true);
        check(vm.StatusMessage == "中止するファイル操作はありません", "Cancel without a running operation only reports it");
    }

    // 進行欄の各行。
    private static void PanelLines(Action<bool, string> check)
    {
        var running = new FileOperationJob("コピー", @"D:\Photos", @"E:\Backup", 2);
        running.SetPlan(Enumerable.Range(1, 8).Select(i => ($@"D:\Photos\IMG_{i}.CR3", 1000L)).ToList());
        running.MarkRunning();
        running.BeginFile(@"D:\Photos\IMG_1.CR3", 1000); running.AddBytes(1000); running.EndFile();
        running.BeginFile(@"D:\Photos\IMG_2.CR3", 1000); running.AddBytes(500);
        var waiting = new FileOperationJob("移動", @"C:\Work\Export", @"E:\Backup\Export", 14);
        var lines = MainViewModel.BuildOperationPanel([running, waiting], "Esc");
        check(lines.Header == @"Copying 1/2  D:\Photos → E:\Backup" && lines.KeyHint == "Esc:Cancel", "Progress header shows the job, route and cancel key in English");
        check(lines.Summary.StartsWith("1 / 8 files  1.46 KByte / 7.81 KByte", StringComparison.Ordinal) && lines.Summary.Contains("Elapsed "),
            "Progress summary shows counts, sizes and elapsed time");
        check(lines.CurrentPath == @"D:\Photos\IMG_2.CR3" && lines.CurrentPercentText == "50%", "Progress shows the current file and its percentage");
        check(lines.Upcoming.Count == 3 && lines.Upcoming[0] == @"D:\Photos\IMG_3.CR3" && lines.Upcoming[2].EndsWith("… 3 more", StringComparison.Ordinal),
            "Progress lists the next files and the rest count");
        check(lines.Waiting.Count == 1 && lines.Waiting[0] == @"Waiting 2/2  Move  C:\Work\Export → E:\Backup\Export (14 items)", "Progress lists the waiting job");
        check(MainViewModel.BuildOperationPanel([new FileOperationJob("削除", @"C:\x", null, 1)], "Esc").Waiting.Count == 0 &&
            MainViewModel.OperationKindText("アーカイブ追加").Running == "Adding to archive" && MainViewModel.OperationKindText("?").Running == "Processing",
            "Every operation kind has an English name");
        running.Cancel();
        check(MainViewModel.BuildOperationPanel([running], "Esc") is { KeyHint: "" } cancelling && cancelling.Header.StartsWith("Cancelling: Copy 1/1", StringComparison.Ordinal),
            "Progress shows that cancelling is in progress");
    }

    // 中止のキー（既定 Esc、&opcancel;）。
    private static void Keys(string root, Action<bool, string> check)
    {
        check(IniKeyParser.TryParseLine(">KEY_ESC    ---- &opcancel;", out var item) && item!.Key == Key.Escape && item.Command == AppCommand.OperationCancel &&
              IniKeyParser.TryParseLine(IniKeyParser.FormatLine(item), out var again) && again!.Command == AppCommand.OperationCancel,
            "opcancel parses and round trips");
        check(new KeyBindingService().TryResolve(Key.Escape, ModifierKeys.None, out var resolved) && resolved.Command == AppCommand.OperationCancel,
            "Esc cancels file operations by default");
        string template = File.ReadAllText(Path.GetFullPath("mf.default.ini"));
        check(template.Contains(">KEY_ESC    ---- &opcancel;", StringComparison.Ordinal), "Default INI binds Esc to opcancel");
        string path = Path.Combine(root, "opcancel-old.ini");
        File.WriteAllText(path, "[keys.file]\n>KEY_C ---- &filecopy;\n");
        var loaded = new SettingsStore(path).Load().Settings;
        check(loaded.KeyBindings.Any(b => b.Key == Key.Escape && b.Command == AppCommand.OperationCancel),
            "An older INI without opcancel gets Esc for cancelling");
    }
}