using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using MirrorFlux.Input;
using MirrorFlux.Models;
using MirrorFlux.Services;
using MirrorFlux.ViewModels;

internal static class ExplorerFolderRegression
{
    public static async Task Run(string root, Action<bool, string> check)
    {
        string parent = Directory.CreateDirectory(Path.Combine(root, "explorer-folders")).FullName;
        string folder = Directory.CreateDirectory(Path.Combine(parent, "日本語 folder, name")).FullName;
        var vm = new MainViewModel(new SettingsStore(Path.Combine(root, "explorer.ini")));
        await vm.ActivePane.LoadAsync(parent);
        ProcessStartInfo? launched = null;
        vm.ExplorerLauncher = request => launched = request;
        vm.ActivePane.SelectedItem = vm.ActivePane.Entries.Single(e => e.FullPath == folder);
        await vm.ExecuteAsync(new ResolvedCommand(AppCommand.ExtExecWindows, Modifiers: ModifierKeys.Shift));
        check(launched != null && Path.GetFileName(launched.FileName) == "explorer.exe" &&
            launched.ArgumentList.SequenceEqual(new[] { folder }) && !launched.UseShellExecute && vm.ActivePane.CurrentPath == parent,
            "Shift Enter opens the selected folder as one Explorer argument without navigating the pane");
        launched = null;
        await vm.ExecuteAsync(new ResolvedCommand(AppCommand.ExtExec));
        check(launched == null && vm.ActivePane.CurrentPath == folder, "Plain Enter retains internal folder navigation");
        vm.ActivePane.SelectedItem = vm.ActivePane.Entries.Single(e => e.IsParent);
        await vm.ExecuteAsync(new ResolvedCommand(AppCommand.ExtExecWindows, Modifiers: ModifierKeys.Shift));
        check(launched?.ArgumentList.Single() == parent && vm.ActivePane.CurrentPath == folder, "Shift Enter on parent opens the actual parent folder");
        launched = null;
        vm.ActivePane.SelectedItem = new FileEntry(Path.Combine(parent, "archive.zip", "virtual-folder"), "virtual-folder", true, false, 0, DateTime.Now);
        await vm.ExecuteAsync(new ResolvedCommand(AppCommand.ExtExecWindows, Modifiers: ModifierKeys.Shift));
        check(launched == null && vm.ActivePane.CurrentPath == folder && vm.StatusMessage.Contains("開けませんでした"),
            "Virtual or missing directories do not launch Explorer or navigate");
        vm.ActivePane.SelectedItem = new FileEntry(folder, "folder", true, false, 0, DateTime.Now);
        vm.ExplorerLauncher = _ => throw new InvalidOperationException("simulated launch failure");
        await vm.ExecuteAsync(new ResolvedCommand(AppCommand.ExtExecWindows, Modifiers: ModifierKeys.Shift));
        check(vm.StatusMessage.Contains("開けませんでした") && vm.ActivePane.CurrentPath == folder, "Explorer launch errors preserve the pane and report failure");
    }
}
