using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;
using MirrorFlux.Input;
using MirrorFlux.Models;
using MirrorFlux.Services;
using MirrorFlux.ViewModels;

internal static class ArchiveCreateRegression
{
    public static async Task Run(string root, Action<bool, string> check)
    {
        string source = Path.Combine(root, "create-source");
        string folder = Path.Combine(source, "素材");
        Directory.CreateDirectory(Path.Combine(folder, "空"));
        File.WriteAllText(Path.Combine(folder, "曲.txt"), "sample", new UTF8Encoding(false));
        string single = Path.Combine(source, "readme.txt");
        File.WriteAllText(single, "hello", new UTF8Encoding(false));
        FileEntry[] marked =
        [
            new(folder, "素材", true, false, 0, Directory.GetLastWriteTime(folder)),
            new(single, "readme.txt", false, false, new FileInfo(single).Length, File.GetLastWriteTime(single))
        ];
        foreach (string extension in new[] { ".zip", ".7z", ".lzh", ".lha", ".tar" })
        {
            string archive = Path.Combine(root, "created" + extension);
            int count = await ArchiveService.CreateArchiveFromEntriesAsync(archive, marked,
                1, (_, _) => { });
            check(count >= 2 && File.Exists(archive), $"{extension} creates from marked entries");
            check(Encoding.UTF8.GetString(await ArchiveService.ExtractArchiveEntryBytesAsync(
                archive, "readme.txt")) == "hello", $"{extension} reads back the new file");
            string nested = Encoding.UTF8.GetString(await ArchiveService.ExtractArchiveEntryBytesAsync(
                archive, "素材/曲.txt"));
            check(nested == "sample", $"{extension} reads back nested file");
        }

        string compressible = Path.Combine(source, "compressible.bin");
        File.WriteAllBytes(compressible, Enumerable.Repeat((byte)'A', 65536).ToArray());
        var large = new FileEntry(compressible, "compressible.bin", false, false,
            new FileInfo(compressible).Length, File.GetLastWriteTime(compressible));
        long storedSize = 0, strongestSize = 0;
        for (int level = 0; level <= 9; level++)
        {
            string archive = Path.Combine(root, $"level-{level}.zip");
            await ArchiveService.CreateArchiveFromEntriesAsync(archive, [large], level, (_, _) => { });
            using var zip = ZipFile.OpenRead(archive);
            var entry = zip.GetEntry("compressible.bin")!;
            check(entry != null && entry.Length == 65536, $"ZIP level {level} creates a readable entry");
            if (level == 0) storedSize = entry!.CompressedLength;
            if (level == 9) strongestSize = entry!.CompressedLength;
        }
        check(storedSize > strongestSize, "ZIP numeric levels affect the actual compression");

        string unsupported = Path.Combine(source, "emoji-\U0001F600.txt");
        File.WriteAllText(unsupported, "unsupported name");
        FileEntry[] invalidNames = [new(unsupported, Path.GetFileName(unsupported), false, false, 16, DateTime.Now)];
        bool unicodeLhaRejected = false;
        try { await ArchiveService.CreateArchiveFromEntriesAsync(Path.Combine(root, "bad-unicode.lha"),
            invalidNames, 6, (_, _) => { }); }
        catch (IOException) { unicodeLhaRejected = true; }
        check(unicodeLhaRejected && !File.Exists(Path.Combine(root, "bad-unicode.lha")),
            "LHA refuses names it cannot encode instead of corrupting them");

        string existing = Path.Combine(root, "existing.zip");
        File.WriteAllText(existing, "do not replace");
        bool refused = false;
        try { await ArchiveService.CreateArchiveFromEntriesAsync(existing, marked,
            6, (_, _) => { }); }
        catch (IOException) { refused = true; }
        check(refused && File.ReadAllText(existing) == "do not replace",
            "new archive refuses to overwrite an existing file");

        string inside = Path.Combine(folder, "self.tar");
        bool blocked = false;
        try { await ArchiveService.CreateArchiveFromEntriesAsync(inside, marked,
            6, (_, _) => { }); }
        catch (IOException) { blocked = true; }
        check(blocked && !File.Exists(inside), "archive cannot include itself via source folder");

        foreach (var (line, command) in new[]
        {
            (">KEY_A ---- &markall;", AppCommand.MarkAll),
            (">KEY_A -s-- &markclear;", AppCommand.MarkClear),
            (">KEY_A ---c &archive;", AppCommand.Archive),
            (">KEY_I ---- &incrimentalsearch;", AppCommand.IncrementalSearch)
        })
            check(IniKeyParser.TryParseLine(line, out var binding) && binding!.Command == command,
                $"INI resolves {line}");

        string config = Path.Combine(root, "legacy-archive-keys.ini");
        File.WriteAllText(config, ">KEY_C ---- &filecopycheck;\n");
        var loaded = new SettingsStore(config).Load().Settings;
        var keys = new KeyBindingService();
        keys.ApplyBindings(loaded.KeyBindings);
        check(keys.TryResolve(Key.A, ModifierKeys.None, out var markAll) &&
              markAll.Command == AppCommand.MarkAll &&
              keys.TryResolve(Key.A, ModifierKeys.Shift, out var clear) &&
              clear.Command == AppCommand.MarkClear &&
              keys.TryResolve(Key.A, ModifierKeys.Control, out var create) &&
              create.Command == AppCommand.Archive &&
              keys.TryResolve(Key.I, ModifierKeys.None, out var search) &&
              search.Command == AppCommand.IncrementalSearch,
            "legacy INI gains the new file-list shortcuts");

        File.WriteAllText(config, ">KEY_A -s-- &archive;\n>KEY_A ---c &markall;\n");
        var custom = new SettingsStore(config).Load().Settings;
        keys.ApplyBindings(custom.KeyBindings);
        check(keys.TryResolve(Key.A, ModifierKeys.Shift, out var customShift) && customShift.Command == AppCommand.Archive &&
              keys.TryResolve(Key.A, ModifierKeys.Control, out var customCtrl) && customCtrl.Command == AppCommand.MarkAll,
            "Explicit legacy or custom A shortcuts take precedence over new defaults");
        var clearBinding = new KeyBindingItem(Key.A, ModifierKeys.Shift, AppCommand.MarkClear, "");
        check(IniKeyParser.TryParseLine(IniKeyParser.FormatLine(clearBinding), out var restored) && restored!.Command == AppCommand.MarkClear,
            "Mark-clear shortcut survives INI serialization");

        var vm = new MainViewModel(new SettingsStore(Path.Combine(root, "mark-clear.ini")));
        await vm.LeftPane.LoadAsync(source);
        await vm.RightPane.LoadAsync(source);
        vm.LeftPane.MarkAllVisible(); vm.RightPane.MarkAllVisible();
        vm.LeftPane.ApplySearch("read");
        await vm.ExecuteAsync(new ResolvedCommand(AppCommand.MarkClear));
        check(vm.LeftPane.Entries.All(e => !e.IsMarked) && vm.RightPane.Entries.Any(e => e.IsMarked) && vm.LeftPane.SearchText == "read",
            "Mark-clear command clears hidden marks only in the active pane and retains its filter");

        var pane = new FilePaneViewModel("test", source);
        await pane.LoadAsync(source);
        var result = pane.ApplySearch("read");
        check(result.Count == 1 && pane.SelectedItem?.Name == "readme.txt",
            "incremental search filters list and moves the cursor");
        check(pane.HasSearchText && pane.SearchBadgeText == "[read]",
            "incremental search exposes a bracketed query beside the sort badge");
        pane.MarkAllVisible();
        check(pane.Entries.Count(e => e.IsMarked) == 1 && pane.SelectedItem?.IsMarked == true,
            "mark all marks only currently visible entries");
        pane.ApplySearch("");
        check(pane.VisibleEntries.Cast<FileEntry>().Count() == pane.Entries.Count &&
              !pane.HasSearchText && pane.SearchBadgeText == "",
            "clearing incremental search restores the list and hides its badge");
    }
}
