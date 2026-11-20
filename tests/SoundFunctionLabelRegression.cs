using System.IO;
using MirrorFlux.Services;
using MirrorFlux.ViewModels;

internal static class SoundFunctionLabelRegression
{
    public static void Run(Action<bool, string> check)
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".ini");
        try
        {
            File.WriteAllLines(path, Enumerable.Range(1, 12).Select(i => $"%mfn{i:D2}\t音楽{i:D2}"));
            var store = new SettingsStore(path);
            var settings = store.Load().Settings;
            check(store.TrySave(settings, out _), "Sound function labels save to INI");
            var reloaded = store.Load().Settings;
            check(Enumerable.Range(1, 12).All(i => reloaded.MusicFunctionLabels[i - 1] == $"音楽{i:D2}"),
                "All twelve sound function labels survive INI round trip");
            var vm = new MainViewModel(store) { IsViewerActive = true, IsMusicPlayerViewer = true };
            check(Enumerable.Range(1, 12).All(i => (string?)typeof(MainViewModel).GetProperty($"Fn{i:D2}Label")!.GetValue(vm) == $"音楽{i:D2}"),
                "Sound viewer displays all configured function labels");
        }
        finally { File.Delete(path); }
    }
}
