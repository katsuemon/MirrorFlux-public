using System.IO;
using System.Windows.Input;
using MirrorFlux.Input;
using MirrorFlux.Services;
using MirrorFlux.ViewModels;

internal static class ViewerFunctionBindingRegression
{
    public static void Run(string root, Action<bool, string> check)
    {
        var defaults = ViewerFunctionBindings.CreateDefaults().ToArray();
        // 視聴対象が増えても数を固定しない。Fキーは全ビューアーの通常・Shift各12、一般キーは動画のみ。
        var functionDefaults = defaults.Where(b => b.InputKey == null).ToArray();
        var generalDefaults = defaults.Where(b => b.InputKey != null).ToArray();
        check(functionDefaults.Length == ViewerFunctionBindings.Viewers.Count * 24 &&
            ViewerFunctionBindings.Viewers.All(viewer => new[] { ModifierKeys.None, ModifierKeys.Shift }.All(modifiers =>
                Enumerable.Range(1, 12).All(number => functionDefaults.Count(b => b.Viewer == viewer && b.Modifiers == modifiers && b.Number == number) == 1))),
            "Every viewer defines all normal and Shift function keys");
        check(generalDefaults.Length > 0 && generalDefaults.All(b => b.Viewer is "video" or "music" && b.Number == 0) &&
            generalDefaults.Select(b => (b.Viewer, b.InputKey, b.Modifiers)).Distinct().Count() == generalDefaults.Length &&
            generalDefaults.Any(b => b.Viewer == "music" && b.InputKey == Key.T && b.Command == "titles"),
            "General-key defaults belong to the video and sound viewers without duplicates (T switches VGM titles)");
        check(defaults.All(b => ViewerFunctionBindings.TryParse(b.Viewer, ViewerFunctionBindings.Format(b), out var parsed) && parsed == b),
            "All default viewer assignments round trip");
        foreach (var invalid in new[] { ">KEY_F0 ---- &search;", ">KEY_F13 ---- &search;", ">KEY_F2 ---- &playpause;", ">KEY_F2 ---- &search;garbage" })
            check(!ViewerFunctionBindings.TryParse("text", invalid, out _), "Invalid viewer mapping ignored: " + invalid);
        var template = new SettingsStore(Path.Combine(Directory.GetCurrentDirectory(), "mf.default.ini")).Load().Settings;
        check(functionDefaults.All(b => ViewerFunctionBindings.Resolve(template.ViewerKeyBindings, b.Viewer, b.Number, b.Modifiers)?.Command == b.Command),
            "Default INI matches all viewer defaults");
        check(generalDefaults.All(b => template.ViewerKeyBindings.LastOrDefault(t =>
                t.Viewer == b.Viewer && t.InputKey == b.InputKey && t.Modifiers == b.Modifiers)?.Command == b.Command),
            "Default INI matches all video and sound general-key defaults");
        var path = Path.Combine(root, "viewer-functions.ini");
        File.WriteAllText(path, """
            [keys.text]
            >KEY_F2 ---- &search;
            >KEY_F2 ---- &follow;
            >KEY_F2 -s-- &search;
            >KEY_F12 ---- &none;
            >KEY_F3 ---- &playpause;
            >KEY_F3 ---c &findprevious;
            [keys.hex]
            >KEY_F5 -s-- &endian;
            [keys.graphic]
            >KEY_F2 ---- &next;
            >KEY_F3 -s-- &rotate;
            [keys.pdf]
            >KEY_F2 ---- &fitwidth;
            >KEY_F3 -s-- &next;
            [keys.music]
            >KEY_F2 ---- &stop;
            >KEY_F3 -s-- &playpause;
            >KEY_T ---- &none;
            >KEY_Y -s-- &titles;
            >KEY_F10 -s-- &titles;
            [keys.unknown]
            >KEY_F2 ---- &exit;
            [keys.file]
            >KEY_F2 ---- &sort;
            """);
        var store = new SettingsStore(path);
        var settings = store.Load().Settings;
        check(settings.KeyBindings.Last(b => b.Key == Key.F2).Command == AppCommand.Sort, "Viewer and unknown sections never leak into file mappings");
        check(ViewerFunctionBindings.Resolve(settings.ViewerKeyBindings, "text", 3, ModifierKeys.None)?.Command == "findnext", "Invalid cross-viewer command retains default");
        var music = settings.ViewerKeyBindings.Where(b => b.Viewer == "music" && b.InputKey != null).ToArray();
        check(music.LastOrDefault(b => b.InputKey == Key.T && b.Modifiers == ModifierKeys.None)?.Command == "none" &&
            music.LastOrDefault(b => b.InputKey == Key.Y && b.Modifiers == ModifierKeys.Shift)?.Command == "titles" &&
            ViewerFunctionBindings.Resolve(settings.ViewerKeyBindings, "music", 10, ModifierKeys.Shift)?.Number == 13,
            "Sound viewer title switch key is configurable in [keys.music], including on a function key");
        check(store.TrySave(settings, out _), "Viewer mappings save");
        var reloaded = store.Load().Settings;
        // 一般キーはNumber=0を共有するため、保存時の並び替えに左右されないようキー込みで照合する。
        string? Effective(IEnumerable<ViewerFunctionBinding> bindings, ViewerFunctionBinding b) => bindings.LastOrDefault(x =>
            x.Viewer == b.Viewer && x.Number == b.Number && x.InputKey == b.InputKey && x.Modifiers == b.Modifiers)?.Command;
        check(settings.ViewerKeyBindings.All(b => Effective(settings.ViewerKeyBindings, b) == Effective(reloaded.ViewerKeyBindings, b)),
            "Every effective viewer mapping survives save and reload");
        var vm = new MainViewModel(store) { IsViewerActive = true };
        int normal = 0, shifted = 0;
        vm.ViewerFunctionKeyRequested += n => normal = n;
        vm.ViewerShiftFunctionKeyRequested += n => shifted = n;
        vm.ExecuteViewerFunctionKey(2, ModifierKeys.None);
        check(normal == 0 && shifted == 2 && vm.Fn02Label == "末尾追従", "Text normal key can invoke Shift action with matching label");
        normal = shifted = 0;
        vm.FunctionShiftPressed = true;
        vm.ExecuteFunctionKeyAsync(2).GetAwaiter().GetResult();
        check(normal == 2 && shifted == 0 && vm.Fn02Label == vm.Settings.ViewerFunctionLabels[1], "Text Shift button can invoke normal search action");
        normal = shifted = 0;
        vm.ExecuteViewerFunctionKey(12, ModifierKeys.None);
        check(normal == 0 && shifted == 0, "Disabled viewer close key emits no action");
        vm.ExecuteViewerFunctionKey(3, ModifierKeys.Control);
        check(normal == 4, "Ctrl viewer assignment dispatches configured action");
        vm.IsHexViewer = true;
        vm.ExecuteFunctionKeyAsync(5).GetAwaiter().GetResult();
        check(shifted == 7 && vm.Fn05Label == "Endian", "HEX has independent Shift command and label");
        vm.IsHexViewer = false;
        foreach (var (viewer, expectedNormal, expectedShift) in new[] { ("graphic", 4, 7), ("pdf", 5, 4), ("music", 8, 2) })
        {
            vm.IsGraphicViewer = viewer == "graphic";
            vm.IsPdfViewer = viewer == "pdf";
            vm.IsMusicPlayerViewer = viewer == "music";
            vm.ExecuteViewerFunctionKey(2, ModifierKeys.None);
            check(normal == expectedNormal, viewer + " normal dispatch uses its own section");
            vm.ExecuteFunctionKeyAsync(3).GetAwaiter().GetResult();
            check(normal == expectedShift && vm.Fn03Label.Length > 0, viewer + " Shift button uses its own section and label");
        }
    }
}
