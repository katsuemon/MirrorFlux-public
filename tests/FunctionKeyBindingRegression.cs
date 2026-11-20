using System.IO;
using System.Windows.Input;
using MirrorFlux.Input;
using MirrorFlux.Models;
using MirrorFlux.Services;
using MirrorFlux.ViewModels;

internal static class FunctionKeyBindingRegression
{
    public static void Run(string root, Action<bool, string> check)
    {
        foreach (var binding in FunctionKeyBindings.CreateDefaults())
        {
            check(IniKeyParser.TryParseLine(IniKeyParser.FormatLine(binding), out var parsed) && parsed == binding,
                $"{binding.Key} default command round trips");
        }
        var path = Path.Combine(root, "function-keys.ini");
        File.WriteAllText(path, ">KEY_F2 ---- &sort;\n>KEY_F2 ---- &help;\n>KEY_F12 ---- &none;\n>KEY_F5 -s-- &help;\n>KEY_F7 ---c &mark;\n>KEY_F8 --a- &refresh;\n");
        var store = new SettingsStore(path);
        var loaded = store.Load().Settings;
        var keyboard = new KeyBindingService();
        keyboard.ApplyBindings(loaded.KeyBindings);
        foreach (var number in Enumerable.Range(1, 12))
        {
            FunctionKeyBindings.TryResolve(loaded.KeyBindings, number, ModifierKeys.None, out var button);
            check(keyboard.TryResolve(Key.F1 + number - 1, ModifierKeys.None, out var key) && key == button,
                $"F{number} keyboard and button resolve identically");
        }
        check(keyboard.TryResolve(Key.F2, ModifierKeys.None, out var duplicate) && duplicate.Command == AppCommand.Help,
            "Last duplicate mapping wins");
        check(keyboard.TryResolve(Key.F12, ModifierKeys.None, out var disabled) && disabled.Command == AppCommand.None,
            "Explicit none disables default exit");
        check(FunctionKeyBindings.CreateDefaults().ElementAt(3).Command == AppCommand.FileRename &&
              !IniKeyParser.TryParseCommand("edit", out _) && !IniKeyParser.TryParseCommand("settings", out _),
            "F4 defaults to rename and the removed edit/settings commands are not accepted");
        check(keyboard.TryResolve(Key.F7, ModifierKeys.Control, out var ctrl) && ctrl.Command == AppCommand.ToggleMark && ctrl.Modifiers == ModifierKeys.Control,
            "Ctrl mapping preserves modifiers");
        check(keyboard.TryResolve(Key.F8, ModifierKeys.Alt, out var alt) && alt.Command == AppCommand.Refresh,
            "Alt mapping resolves");
        check(store.TrySave(loaded, out _), "Function mappings save");
        var reloaded = store.Load().Settings;
        var restored = new KeyBindingService();
        restored.ApplyBindings(reloaded.KeyBindings);
        foreach (var binding in loaded.KeyBindings)
        {
            keyboard.TryResolve(binding.Key, binding.Modifiers, out var before);
            check(restored.TryResolve(binding.Key, binding.Modifiers, out var after) && before == after,
                $"{binding.Key}/{binding.Modifiers} retains behavior after save");
        }
        var vm = new MainViewModel(store);
        check(vm.Fn02Label == "ヘルプ" && vm.Fn04Label == "名前変更" && vm.Fn12Label == "", "Remapped and disabled labels match commands");
        vm.ExecuteFunctionKeyAsync(2).GetAwaiter().GetResult();
        check(vm.StatusMessage.StartsWith("F1:"), "Button executes last mapping after save and reload");
        vm.StatusMessage = "unchanged";
        vm.ExecuteFunctionKeyAsync(12).GetAwaiter().GetResult();
        check(vm.StatusMessage == "unchanged", "Disabled button performs no action");
        vm.FunctionShiftPressed = true;
        vm.ExecuteFunctionKeyAsync(5).GetAwaiter().GetResult();
        check(vm.Fn05Label == "ヘルプ" && vm.StatusMessage.StartsWith("F1:"), "Shift button and label use INI mapping");
        vm.StatusMessage = "unchanged";
        vm.ExecuteFunctionKeyAsync(2).GetAwaiter().GetResult();
        check(vm.StatusMessage == "unchanged", "Unassigned Shift button does not execute normal action");
    }
}
