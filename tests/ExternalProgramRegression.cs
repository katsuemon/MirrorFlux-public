using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Windows.Input;
using MirrorFlux.Input;
using MirrorFlux.Models;
using MirrorFlux.Services;
using MirrorFlux.ViewModels;

internal static class ExternalProgramRegression
{
    public static async Task Run(string root, Action<bool, string> check)
    {
        const string example = @">KEY_E -s-- C:\Tools\viewer.exe %markfiles;";
        check(IniKeyParser.TryParseLine(example, out var binding) && binding!.Key == Key.E && binding.Modifiers == ModifierKeys.Shift &&
            binding.Command == AppCommand.ExternalProgram && binding.ExternalCommand == @"C:\Tools\viewer.exe %markfiles", "External executable key syntax parses the requested example");
        const string quoted = """>KEY_F3 -s-- "C:\Tools #1\viewer.exe" --title "a # b; c" "" "C:\trailing\\" "a\"b" %markfiles; # comment""";
        check(IniKeyParser.TryParseLine(quoted, out var fn) && ExternalCommandTemplate.TryParse(fn!.ExternalCommand!, out var parsed) &&
            parsed!.Executable == @"C:\Tools #1\viewer.exe" && parsed.Arguments.SequenceEqual(new[] { "--title", "a # b; c", "", @"C:\trailing\", "a\"b", "%markfiles" }),
            "Executable templates preserve quoted spaces, hash/semicolon, empty arguments and escaped quotes/backslashes");
        foreach (var item in new[] { binding!, fn! })
            check(IniKeyParser.TryParseLine(IniKeyParser.FormatLine(item), out var restored) && restored == item, "External key serialization retains the full command");
        foreach (string invalid in new[] { ">KEY_E -s-- &unknown;", ">KEY_E -s-- \"unterminated.exe", ">KEY_E -s-- x.exe; y.exe", ">KEY_E -s-- x.exe --files=%markfiles;" })
            check(!IniKeyParser.TryParseLine(invalid, out _), "Malformed external/built-in command is rejected");

        string ini = Path.Combine(root, "external-keys.ini");
        File.WriteAllText(ini, "[keys.file]\n" + example + "\n" + quoted + "\n");
        var store = new SettingsStore(ini);
        var settings = store.Load().Settings;
        var keys = new KeyBindingService(); keys.ApplyBindings(settings.KeyBindings);
        check(keys.TryResolve(Key.E, ModifierKeys.Shift, out var command) && command.ExternalCommand == binding!.ExternalCommand, "Keyboard resolution retains the executable and arguments");
        check(keys.TryResolve(Key.F3, ModifierKeys.Shift, out var fkey) && FunctionKeyBindings.TryResolve(settings.KeyBindings, 3, ModifierKeys.Shift, out var button) && fkey == button,
            "External function key and button resolve identically");
        string savedIni = Path.Combine(root, "external-saved.ini");
        var saved = new SettingsStore(savedIni);
        check(saved.TrySave(settings, out _) && saved.Load().Settings.KeyBindings.Any(b => b == fn), "New INI serialization and reload retain quoted external commands");
        check(store.TrySave(settings, out _) && store.Load().Settings.KeyBindings.Any(b => b == binding), "Existing INI session save preserves external commands");

        string folder = Directory.CreateDirectory(Path.Combine(root, "external source")).FullName;
        string first = Path.Combine(folder, "日本語 & one.txt"), second = Path.Combine(folder, "two; #file.txt"), selected = Path.Combine(folder, "selected.txt");
        foreach (string path in new[] { first, second, selected }) File.WriteAllText(path, "generated text");
        Directory.CreateDirectory(Path.Combine(folder, "marked directory"));
        var vm = new MainViewModel(store);
        await vm.ActivePane.LoadAsync(folder);
        vm.ActivePane.SelectedItem = vm.ActivePane.Entries.Single(e => e.FullPath == selected);
        foreach (var entry in vm.ActivePane.Entries) entry.IsMarked = entry.FullPath == first || entry.FullPath == second || entry.IsDirectory && !entry.IsParent;
        string[] expected = vm.ActivePane.Entries.Where(e => e.IsMarked && !e.IsDirectory).Select(e => e.FullPath).ToArray();
        ProcessStartInfo? launched = null; int launches = 0;
        vm.ExternalProgramLauncher = request => { launched = request; launches++; };
        await vm.ExecuteAsync(command);
        check(launches == 1 && launched!.FileName == @"C:\Tools\viewer.exe" && launched.ArgumentList.SequenceEqual(expected) && !launched.UseShellExecute && launched.WorkingDirectory == folder,
            "Marked files are separate arguments in one launch; directories and unmarked cursor are excluded");
        check(vm.ActivePane.Entries.Count(e => e.IsMarked && !e.IsDirectory) == 2, "External launch preserves file marks");
        vm.ActivePane.ApplySearch("selected");
        await vm.ExecuteAsync(command);
        check(launched!.ArgumentList.SequenceEqual(expected), "Filtered-out marks remain included");
        vm.ActivePane.ApplySearch("");
        foreach (var entry in vm.ActivePane.Entries) entry.IsMarked = false;
        await vm.ExecuteAsync(command);
        check(launched!.ArgumentList.Count == 0, "No marks launches without file arguments, even with a cursor selection");
        await vm.ExecuteAsync(new ResolvedCommand(AppCommand.ExternalProgram, ExternalCommand: @".\tools\app.exe --new %markfiles"));
        check(launched!.FileName == Path.GetFullPath(@".\tools\app.exe", SettingsStore.AppDirectory) && launched.ArgumentList.SequenceEqual(new[] { "--new" }),
            "Relative executable resolves beside MF and fixed arguments remain when marks are empty");

        // Launch this test executable as an argument recorder; no editor or shell is started.
        string output = Path.Combine(root, "external arguments.json");
        foreach (var entry in vm.ActivePane.Entries) entry.IsMarked = expected.Contains(entry.FullPath);
        vm.ExternalProgramLauncher = request =>
        {
            using var process = Process.Start(request)!;
            if (!process.WaitForExit(15000) || process.ExitCode != 0) throw new Exception("Argument recorder failed");
        };
        await vm.ExecuteAsync(new ResolvedCommand(AppCommand.ExternalProgram, ExternalCommand: $"\"{Environment.ProcessPath}\" --external-args-probe \"{output}\" %markfiles;"));
        check(File.Exists(output) && JsonSerializer.Deserialize<string[]>(File.ReadAllText(output))!.SequenceEqual(expected),
            "Real child process receives Japanese/spaces/metacharacter file paths unchanged");

        int beforeFailure = launches;
        vm.ExternalProgramLauncher = _ => { launches++; throw new IOException("simulated launch failure"); };
        await vm.ExecuteAsync(command);
        check(launches == beforeFailure + 1 && vm.StatusMessage.Contains("外部実行できませんでした") && vm.ActivePane.Entries.Count(e => e.IsMarked && !e.IsDirectory) == 2,
            "Launch failure is reported without clearing marks");
        File.Delete(first);
        beforeFailure = launches;
        await vm.ExecuteAsync(command);
        check(launches == beforeFailure, "Missing marked file prevents partial launch");

        string archive = Path.Combine(root, "external.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(zip.CreateEntry("entry.txt").Open())) writer.Write("archive content");
        await vm.ActivePane.LoadAsync(archive);
        foreach (var entry in vm.ActivePane.Entries) entry.IsMarked = !entry.IsDirectory && !entry.IsParent;
        vm.ExternalProgramLauncher = request => launched = request;
        await vm.ExecuteAsync(command);
        check(launched!.ArgumentList.Count == 1 && File.ReadAllText(launched.ArgumentList[0]) == "archive content" && launched.WorkingDirectory == root,
            "Marked archive entries are extracted before passing usable temporary paths");
    }
}
