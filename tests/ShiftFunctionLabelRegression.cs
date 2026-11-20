using System.IO;
using System.Windows.Input;
using MirrorFlux.Input;
using MirrorFlux.Models;
using MirrorFlux.Services;
using MirrorFlux.ViewModels;

internal static class ShiftFunctionLabelRegression
{
    public static void Run(string root, Action<bool, string> check)
    {
        var vm = new MainViewModel(new SettingsStore(Path.Combine(root, "shift-labels.ini"))) { IsViewerActive = true };
        string normal = vm.Fn02Label; int notifications = 0;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName?.StartsWith("Fn") == true) notifications++; };
        vm.FunctionShiftPressed = true;
        check(notifications == 12 && vm.Fn02Label == "末尾追従" && vm.Fn03Label == "正規表現" && vm.Fn04Label == "大小区別", "Holding Shift refreshes twelve labels and shows text alternate functions");
        check(vm.Fn05Label == "前ファイル" && vm.Fn06Label == "次ファイル" && vm.Fn07Label == "" && vm.Fn09Label == "" && vm.Fn10Label == "Syntax", "Text alternate labels show adjacent-file commands and hide HEX-only commands");
        vm.IsHexViewer = true;
        check(vm.Fn05Label == "バイト検索" && vm.Fn06Label == "行幅" && vm.Fn07Label == "Endian" && vm.Fn09Label == "文字列コピー" && vm.Fn10Label == "", "HEX mode refreshes alternate functions and hides text syntax command");
        int called = 0; vm.ViewerShiftFunctionKeyRequested += n => called = n;
        vm.ExecuteFunctionKeyAsync(6).GetAwaiter().GetResult();
        check(called == 6, "Shift function button invokes alternate viewer action");
        vm.FunctionShiftPressed = false;
        check(vm.Fn02Label == normal, "Releasing Shift restores configured normal labels");
        vm.FunctionShiftPressed = true; vm.IsGraphicViewer = true;
        check(vm.Fn02Label == vm.Settings.GraphicViewerFunctionLabels[1], "Viewer with no Shift functions retains ordinary labels");
        vm.IsGraphicViewer = false; vm.IsMusicPlayerViewer = true;
        check(vm.Fn02Label == vm.Settings.MusicFunctionLabels[1], "Music viewer keeps its configured labels while Shift held");
        vm.IsViewerActive = false;
        vm.Settings.KeyBindings.Add(new KeyBindingItem(Key.F5, ModifierKeys.Shift, AppCommand.JumpList, "jumplist"));
        check(vm.Fn05Label == "パス選択" && vm.Fn06Label == "", "File pane displays configured Shift function and hides unassigned slots");
        vm.Settings.KeyBindings.Add(new KeyBindingItem(Key.F6, ModifierKeys.Shift, AppCommand.FunctionKey, "fn6"));
        vm.ExecuteFunctionKeyAsync(6).GetAwaiter().GetResult();
        check(vm.Fn06Label == "", "Self-referential Shift function mapping does not recurse");
    }
}
