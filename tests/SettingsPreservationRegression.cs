using System.IO;
using MirrorFlux.Input;
using MirrorFlux.Models;
using MirrorFlux.Services;
using MirrorFlux.Services.Music;
using System.Windows.Input;

internal static class SettingsPreservationRegression
{
    public static void Run(string root, Action<bool, string> check)
    {
        string path = Path.Combine(root, "preserved.ini");
        File.WriteAllText(path, "# user comment\n%winWidth 1200\n%winHeight 700\n%leftPath C:/\n[keys.file]\n>KEY_F2 ---- &mark;\n[GME]\n%fmCore default\n%pdxSearchPaths old\n%ND6-NDSIF false\n");
        var store = new SettingsStore(path);
        var settings = store.Load().Settings;
        string edited = File.ReadAllText(path).Replace("%fmCore default", "%fmCore ymfm").Replace("old", "new-path")
            .Replace("%ND6-NDSIF false", "%ND6-NDSIF true").Replace("&mark;", "&sort;") + "# keep this too\n";
        File.WriteAllText(path, edited);
        settings.LeftPath = "D:/";
        check(store.TrySaveRuntimeState(settings, out _), "Session state save succeeds after external INI edit");
        string saved = File.ReadAllText(path);
        var reloaded = new SettingsStore(path).Load().Settings;
        check(!saved.Contains("%fmCore", StringComparison.OrdinalIgnoreCase) && reloaded.PdxSearchPaths == "new-path" && reloaded.Nd6Ndsif,
            "External GME edits survive session save");
        check(reloaded.KeyBindings.Last(b => b.Key == Key.F2).Command == AppCommand.Sort && saved.Contains("# keep this too") && saved.Contains("# user comment"),
            "External key mapping and comments survive session save");
        check(reloaded.LeftPath == "D:/" && !saved.Contains("%winWidth") && !saved.Contains("%winHeight"),
            "Session path is saved and obsolete window size entries are removed");

        // 空白で桁を揃えた設定は、終了時の状態保存後も名前と区切りの桁を保つ。
        string alignedPath = Path.Combine(root, "aligned.ini");
        File.WriteAllText(alignedPath, "[settings]\n%leftPath   C:/\n%rightPath  C:/\n%sortMode   name\n");
        var alignedStore = new SettingsStore(alignedPath);
        var alignedSettings = alignedStore.Load().Settings;
        alignedSettings.LeftPath = "D:/left";
        alignedSettings.RightPath = "D:/right";
        check(alignedStore.TrySaveRuntimeState(alignedSettings, out _), "Aligned INI state save succeeds");
        string[] alignedSaved = File.ReadAllLines(alignedPath);
        check(alignedSaved.Contains("%leftPath   D:/left") && alignedSaved.Contains("%rightPath  D:/right") && !alignedSaved.Any(line => line.Contains('\t')),
            "State save keeps hand-aligned columns and writes no tabs");

        // %saveState 0 は終了時にINIを書き換えない。左右のフォルダー・ソート順・ログレベルは書かれた値のまま残る。
        string frozenPath = Path.Combine(root, "frozen.ini");
        string frozenText = "[settings]\n%leftPath   C:/\n%rightPath  C:/\n%saveState  0\n%sortMode   name\n%fileRows   40\n";
        File.WriteAllText(frozenPath, frozenText);
        var frozenStore = new SettingsStore(frozenPath);
        var frozenSettings = frozenStore.Load().Settings;
        check(!frozenSettings.SaveState && frozenSettings.FileRows == 40 && new AppSettings().SaveState && new AppSettings().FileRows == 0,
            "saveState and fileRows are read from the INI, and default to saving and the standard height");
        frozenSettings.LeftPath = "D:/left"; frozenSettings.RightPath = "D:/right";
        frozenSettings.SortMode = "date"; frozenSettings.LogLevel = 3;
        check(frozenStore.TrySaveRuntimeState(frozenSettings, out string? frozenError) && frozenError == null &&
            File.ReadAllText(frozenPath) == frozenText, "saveState 0 leaves the INI untouched at exit");
        File.Delete(frozenPath);
        check(frozenStore.TrySaveRuntimeState(frozenSettings, out _) && !File.Exists(frozenPath),
            "saveState 0 does not recreate a deleted INI");
        string roundTripPath = Path.Combine(root, "frozen-roundtrip.ini");
        check(new SettingsStore(roundTripPath).TrySave(frozenSettings, out _) &&
            new SettingsStore(roundTripPath).Load().Settings is { SaveState: false, FileRows: 40 },
            "saveState and fileRows survive a full INI save");
        var rows = new AppSettings { FileRows = 3 };
        check(rows.FileRows == 5 && (rows.FileRows = 500) == 500 && rows.FileRows == 100 && (rows.FileRows = -1) == -1 && rows.FileRows == 0,
            "fileRows is limited to 5-100, and zero or less means the standard height");

        // Shift_JISで保存された設定は、読み込み・終了時保存で日本語を壊さない（保存後はUTF-8）。
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        string sjisPath = Path.Combine(root, "sjis.ini");
        string sjisText = "# 日本語のコメント\n%leftPath C:/データ\n%jumpPath01 C:/資料/表計算\n[keys.file]\n%fn01 ヘルプ表示\n";
        File.WriteAllBytes(sjisPath, System.Text.Encoding.GetEncoding(932).GetBytes(sjisText));
        var sjisStore = new SettingsStore(sjisPath);
        var sjisSettings = sjisStore.Load().Settings;
        check(sjisSettings.LeftPath == "C:/データ", "Shift_JIS INI loads Japanese values");
        sjisSettings.LeftPath = "D:/保存先";
        check(sjisStore.TrySaveRuntimeState(sjisSettings, out _), "Shift_JIS INI state save succeeds");
        string sjisSaved = new System.Text.UTF8Encoding(false, true).GetString(File.ReadAllBytes(sjisPath));
        check(sjisSaved.Contains("# 日本語のコメント") && sjisSaved.Contains("ヘルプ表示") && sjisSaved.Contains("C:/資料/表計算") &&
            sjisSaved.Contains("D:/保存先") && !sjisSaved.Contains('\uFFFD'),
            "Shift_JIS INI is saved as UTF-8 without losing Japanese text");
        string bomPath = Path.Combine(root, "bom.ini");
        File.WriteAllBytes(bomPath, [0xEF, 0xBB, 0xBF, .. System.Text.Encoding.UTF8.GetBytes("# コメント\n%leftPath C:/データ\n")]);
        check(new SettingsStore(bomPath).Load().Settings.LeftPath == "C:/データ", "UTF-8 INI with a byte order mark still loads");
        // A manual edit to the same state field wins over the running session.
        var second = new SettingsStore(path); var live = second.Load().Settings;
        File.WriteAllText(path, System.Text.RegularExpressions.Regex.Replace(File.ReadAllText(path), @"%leftPath[ \t]+D:/", "%leftPath E:/"));
        live.LeftPath = "F:/";
        check(second.TrySaveRuntimeState(live, out _) && new SettingsStore(path).Load().Settings.LeftPath == "E:/",
            "Concurrent manual edit of the current path is preserved");
        check(second.TrySave(live, out _) && !File.ReadAllText(path).TrimEnd().EndsWith("[keys.file]") &&
              !File.ReadAllText(path).Contains("%winWidth"), "Full serialization omits window size and empty trailing key section");
        File.WriteAllText(path, "[keys.text]\n>KEY_F2 ---- &search;\n[GME]\n>KEY_F2 ---- &close;\n%fmCore ymfm\n[unknown]\n%fmCore default\n[keys.file]\n>KEY_F2 ---- &sort;\n");
        reloaded = new SettingsStore(path).Load().Settings;
        check(ViewerFunctionBindings.Resolve(reloaded.ViewerKeyBindings, "text", 2, ModifierKeys.None)?.Command == "search",
            "GME and unknown sections do not inherit viewer key context or overwrite unrelated values");
        File.WriteAllText(path, "%width 1200\n%win_h 700\n");
        var aliasStore = new SettingsStore(path); var aliasSettings = aliasStore.Load().Settings;
        File.WriteAllText(path, "%width 1600\n%win_h 900\n"); aliasSettings.LeftPath = "G:/";
        check(aliasStore.TrySaveRuntimeState(aliasSettings, out _) &&
              !File.ReadAllText(path).Contains("%width") && !File.ReadAllText(path).Contains("%win_h"),
            "Legacy window size aliases are removed during session save");

        File.WriteAllText(path, "# keep fmCore comment\n%FMCORE default\n[GME]\nfmCore = ymfm\n%pdxSearchPaths samples\n");
        var obsoleteStore = new SettingsStore(path); var obsoleteSettings = obsoleteStore.Load().Settings;
        check(obsoleteStore.TrySaveRuntimeState(obsoleteSettings, out _) &&
              File.ReadAllText(path) == "# keep fmCore comment\n[GME]\n%pdxSearchPaths samples\n",
            "Session save removes obsolete FM selector spellings without touching comments or PDX paths");
        check(!SettingsIniWriter.Serialize(obsoleteSettings).Contains("fmCore", StringComparison.OrdinalIgnoreCase),
            "Full INI serialization does not recreate the removed FM selector");

        var themeSettings = new AppSettings();
        for (int i = 0; i < ThemeKeyCatalog.Core.Count; i++)
            ThemeKeyCatalog.Core[i].Set(themeSettings, $"#{i + 1:X6}");
        string themeText = SettingsIniWriter.Serialize(themeSettings);
        var themeReloaded = SettingsIniReader.Parse(themeText);
        check(ThemeKeyCatalog.Core.All(item => item.Get(themeReloaded) == item.Get(themeSettings) &&
              ThemeKeyCatalog.Purposes.ContainsKey(item.Key) &&
              themeText.Contains($"%theme_{item.Key}\t{item.Get(themeSettings)}\t# {item.Purpose}")),
            "all core theme keys round-trip through the shared catalog with their purpose comments");
        check(new AppSettings().MasterVolumePercent == 50 &&
              SettingsIniReader.Parse("%masterVolume 35\n").MasterVolumePercent == 35 &&
              SettingsIniReader.Parse("%masterVolume 150\n").MasterVolumePercent == 100,
            "master volume defaults to 50 and INI input is limited to 0-100");
        var grouped = SettingsIniReader.Parse("[GME]\n%masterVolume 31\n[theme]\n%theme_PrimaryText #123456\n%theme_MusicText #234567\n%masterVolume 99\n%fmCore default\n");
        check(grouped.MasterVolumePercent == 31 && grouped.ColText == "#123456" &&
              grouped.ThemeColors["MusicText"] == "#234567",
            "Grouped audio/theme settings load while theme section rejects unrelated settings");
        var renamedKeys = SettingsIniReader.Parse("%theme_MusicMmdspWhiteKey #111111\n%theme_MusicMmdspWhiteBorder #222222\n%theme_MusicMmdspBlackKey #333333\n");
        check(renamedKeys.ThemeColors["MusicKeyboardWhiteKey"] == "#111111" &&
              renamedKeys.ThemeColors["MusicKeyboardWhiteBorder"] == "#222222" &&
              renamedKeys.ThemeColors["MusicKeyboardBlackKey"] == "#333333" &&
              !renamedKeys.ThemeColors.Keys.Any(k => k.Contains("Mmdsp", StringComparison.OrdinalIgnoreCase)),
            "Keyboard theme colors written under their former names load under the current names");
        themeSettings.MasterVolumePercent = 35;
        check(SettingsIniReader.Parse(SettingsIniWriter.Serialize(themeSettings)).MasterVolumePercent == 35,
            "master volume survives full INI serialization");
    }
}
