using System.Diagnostics;
using System.IO;
using MirrorFlux.Input;
using MirrorFlux.Models;
using MirrorFlux.Services;
using MirrorFlux.ViewModels;

internal static class SafetyRegression
{
    public static async Task Run(string root, Action<bool, string> check)
    {
        string area = Directory.CreateDirectory(Path.Combine(root, "safety")).FullName;
        string source = Directory.CreateDirectory(Path.Combine(area, "source")).FullName;
        string destination = Directory.CreateDirectory(Path.Combine(area, "destination")).FullName;
        string file = Path.Combine(source, "keep.txt");
        File.WriteAllText(file, "keep");
        foreach (var result in new[] { (5, false), (0, true), (1223, true) })
        {
            var deleted = RecycleBinService.DeleteToRecycleBinWithResult(file, _ => result);
            check(!deleted.Success && File.ReadAllText(file) == "keep", "Recycle failure/cancellation never permanently deletes file");
            deleted = RecycleBinService.DeleteToRecycleBinWithResult(source, _ => result);
            check(!deleted.Success && Directory.Exists(source), "Recycle failure/cancellation never recursively deletes directory");
        }
        string alias = Path.Combine(area, "alias");
        CreateJunction(alias, source);
        try
        {
            bool rejected = false;
            try { FileOperationPaths.ValidateDirectoryTransfer(source, Path.Combine(alias, "child")); }
            catch (IOException) { rejected = true; }
            check(rejected, "Physical descendant through junction is rejected");
            rejected = false;
            try { FileOperationPaths.ValidateFileTransfer(file, Path.Combine(alias, "keep.txt")); }
            catch (IOException) { rejected = true; }
            check(rejected && File.ReadAllText(file) == "keep", "Alias cannot overwrite same physical file");
            // A selected root under a junction is allowed; links within the extraction root are not.
            check(FileOperationPaths.ExtractionTarget(alias, "new.txt") == Path.Combine(alias, "new.txt"), "Junction ancestor of chosen extraction root remains usable");
        }
        finally { Directory.Delete(alias); }
        string escape = Path.Combine(destination, "escape");
        CreateJunction(escape, source);
        try
        {
            bool rejected = false;
            try { FileOperationPaths.ExtractionTarget(destination, "escape", "keep.txt"); }
            catch (IOException) { rejected = true; }
            check(rejected && File.ReadAllText(file) == "keep", "Extraction cannot write through nested junction");
        }
        finally { Directory.Delete(escape); }
        string cycle = Path.Combine(source, "loop");
        CreateJunction(cycle, source);
        try
        {
            var errors = new List<string>();
            await FileCopyService.CopyEntriesAsync([new(source, "source", true, false, 0, DateTime.Now)], destination,
                _ => Task.FromResult(new ConflictResolution(ConflictAction.Overwrite, false)), (m, level) => { if (level == "ERR") errors.Add(m); });
            check(errors.Count > 0 && !Directory.Exists(Path.Combine(destination, "source", "loop")), "Recursive copy stops at junction cycle");
        }
        finally { Directory.Delete(cycle); }
        var vm = new MainViewModel(new SettingsStore(Path.Combine(area, "settings.ini")));
        await vm.ActivePane.LoadAsync(source);
        vm.ActivePane.SelectedItem = vm.ActivePane.Entries.First(e => e.FullPath == file);
        await vm.ExecuteAsync(new ResolvedCommand(AppCommand.SyncPanePath));
        check(vm.LeftPane.CurrentPath == vm.RightPane.CurrentPath && vm.StatusMessage.StartsWith("反対側のパスを同期"), "Path sync only synchronizes panes without starting music");
    }

    private static void CreateJunction(string path, string target)
    {
        var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("/c"); start.ArgumentList.Add("mklink"); start.ArgumentList.Add("/J"); start.ArgumentList.Add(path); start.ArgumentList.Add(target);
        using var process = Process.Start(start)!;
        process.WaitForExit();
        if (process.ExitCode != 0) throw new IOException("Cannot create junction fixture: " + process.StandardError.ReadToEnd());
    }
}
