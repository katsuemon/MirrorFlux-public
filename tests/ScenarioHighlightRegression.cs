using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Highlighting;
using MirrorFlux.Controls;
using MirrorFlux.Models;
using MirrorFlux.Services;

internal static class ScenarioHighlightRegression
{
    public static void Run(Action<bool, string> check)
    {
        string folder = Path.Combine(Path.GetTempPath(), "MF scenario test");
        string pattern = Path.Combine(folder, "reSG*.txt");
        var settings = new AppSettings(); settings.TVSyntaxRules.Add(new(pattern, "scenario"));
        string path = Path.Combine(folder, "reSG06_24S.txt");
        check(ViewerSyntaxService.Resolve(path, "", settings) == "scenario", "Folder rule overrides txt extension for scenario files");
        check(ViewerSyntaxService.Resolve(Path.Combine(folder, "notes.txt"), "", settings) == "none" && ViewerSyntaxService.Resolve(Path.Combine(folder + " other", "reSG06_24S.txt"), "", settings) == "none", "Scenario rule excludes other names and folders");
        check(ViewerSyntaxService.Resolve(Path.Combine(folder, "sub", "reSG06_24S.txt"), "", settings) == "none", "Folder glob does not silently match nested directories");
        check(ViewerSyntaxService.Resolve(path.ToUpperInvariant().Replace('\\', '/'), "", settings) == "scenario", "Syntax path rules accept slash variations and case insensitive paths");
        settings.TVSyntaxRules.Insert(0, new("reSG*.txt", "none"));
        check(ViewerSyntaxService.Resolve(path, "", settings) == "none", "First matching syntax rule controls priority"); settings.TVSyntaxRules.RemoveAt(0);
        string ini = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".ini");
        try
        {
            var store = new SettingsStore(ini);
            check(store.TrySave(settings, out _) && store.Load().Settings.TVSyntaxRules.SequenceEqual(settings.TVSyntaxRules), "Quoted syntax path rules with spaces survive INI roundtrip");
            check(!TextSyntaxRule.TryParse("\"unfinished rule", out _) && !TextSyntaxRule.TryParse("*.txt scenario extra", out _), "Malformed syntax rules are ignored");
        }
        finally { File.Delete(ini); }
        var prefixSettings = new AppSettings(); prefixSettings.TVSyntaxPrefixes["reSG"] = "scenario";
        check(ViewerSyntaxService.Resolve(Path.Combine(Path.GetTempPath(), "other folder", "reSG01_01.txt"), "", prefixSettings) == "scenario", "Scenario prefix selects files across different folders");
        check(ViewerSyntaxService.Resolve("other_reSG01.txt", "", prefixSettings) == "none", "Prefix only matches the start of the filename");
        prefixSettings.TVSyntaxPrefixes["reSG_test"] = "none";
        check(ViewerSyntaxService.Resolve("reSG_test01.txt", "", prefixSettings) == "none", "More specific syntax prefix wins");
        prefixSettings.TVSyntaxRules.Add(new("reSG*.txt", "ini"));
        check(ViewerSyntaxService.Resolve("reSG01.txt", "", prefixSettings) == "ini", "Explicit path rule has priority over filename prefix");
        string prefixIni = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".ini");
        try
        {
            var store = new SettingsStore(prefixIni);
            check(store.TrySave(prefixSettings, out _) && store.Load().Settings.TVSyntaxPrefixes["reSG"] == "scenario", "Syntax prefixes survive INI save and load");
        }
        finally { File.Delete(prefixIni); }
        const string sample = "// scene note\n【 reSG06_24S_TEST0001 】\n〔話者〕「台詞です」\n｛親字｝［よみ］\n【通常の括弧】 ［単独の括弧］\n地の文です。\n" +
            "＠ポロン「台詞」\n　＠モモ「\n本文中の＠ポロン「台詞」\n＠「台詞」\n＠モモ\n";
        var editor = new TextEditor { Text = sample };
        ViewerSyntaxService.Apply(editor, path, settings);
        check(editor.SyntaxHighlighting?.Name == "Scenario", "Embedded scenario highlighting loads");
        using var highlighter = new DocumentHighlighter(editor.Document, editor.SyntaxHighlighting!);
        bool Has(int line, string name) => highlighter.HighlightLine(line).Sections.Any(section => section.Color.Name == name);
        check(Has(1, "Comment") && Has(2, "Voice") && Has(3, "Speaker") && Has(4, "RubyReading"), "Scenario highlights comments branch voice IDs speakers and ruby readings");
        check(highlighter.HighlightLine(5).Sections.Count == 0 && highlighter.HighlightLine(6).Sections.Count == 0, "Scenario keeps ordinary brackets and narrative text plain");
        string[] SpeakerText(int line) => highlighter.HighlightLine(line).Sections
            .Where(section => section.Color.Name == "Speaker")
            .Select(section => editor.Document.GetText(section.Offset, section.Length).TrimStart()).ToArray();
        check(SpeakerText(7).SequenceEqual(["＠ポロン"]) && SpeakerText(8).SequenceEqual(["＠モモ"]),
            "Fullwidth at-sign speaker ends before the opening quote, including on an indented line");
        check(!Has(9, "Speaker") && !Has(10, "Speaker") && !Has(11, "Speaker"),
            "Inline at-signs, missing speaker names and missing opening quotes stay plain");
        check(editor.SyntaxHighlighting!.GetNamedColor("Voice").Foreground.GetColor(null) == (Color)ColorConverter.ConvertFromString(settings.TVColorScenarioVoice), "Scenario voice IDs use configured yellow palette color");
    }
    public static void Audit(string directory)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var files = Directory.GetFiles(directory, "reSG*.txt").OrderBy(path => path).ToArray();
                if (files.Length == 0) throw new Exception("No scenario files found");
                var hashes = files.ToDictionary(path => path, path => SHA256.HashData(File.ReadAllBytes(path)));
                var settings = new AppSettings(); settings.TVSyntaxRules.Add(new(Path.Combine(Path.GetFullPath(directory), "reSG*.txt"), "scenario"));
                int voices = 0, speakers = 0, ruby = 0;
                foreach (string path in files)
                {
                    var editor = new TextEditor { Text = CharsetDetector.ShiftJis.GetString(File.ReadAllBytes(path)) };
                    ViewerSyntaxService.Apply(editor, path, settings);
                    if (editor.SyntaxHighlighting?.Name != "Scenario") throw new Exception("Scenario rule was not selected");
                    using var highlighter = new DocumentHighlighter(editor.Document, editor.SyntaxHighlighting);
                    for (int i = 1; i <= editor.Document.LineCount; i++)
                    {
                        var line = editor.Document.GetLineByNumber(i); string text = editor.Document.GetText(line);
                        var sections = highlighter.HighlightLine(i).Sections;
                        bool voice = sections.Any(s => s.Color.Name == "Voice");
                        if (Regex.IsMatch(text.Trim(), @"^【\s*reSG\w+\s*】$") && !voice) throw new Exception("Voice ID missed by highlighting");
                        if (voice) voices++;
                        if (sections.Any(s => s.Color.Name == "Speaker")) speakers++;
                        ruby += sections.Count(s => s.Color.Name == "RubyReading");
                    }
                }
                foreach (string path in files) if (!hashes[path].SequenceEqual(SHA256.HashData(File.ReadAllBytes(path)))) throw new Exception("Source scenario was changed");
                var viewer = new TextViewerControl { Width = 1100, Height = 650, FontSize = 14 };
                viewer.Resources["PaneText"] = Brushes.LightGray; viewer.Resources["MutedText"] = Brushes.SlateGray;
                viewer.Resources["HighlightText"] = Brushes.CornflowerBlue; viewer.Resources["ScreenBackground"] = Brushes.Black;
                viewer.Load(files[0], false);
                var preview = (TextEditor)viewer.FindName("Editor"); ViewerSyntaxService.Apply(preview, files[0], settings);
                viewer.Measure(new Size(1100, 650)); viewer.Arrange(new Rect(0, 0, 1100, 650)); viewer.UpdateLayout();
                string output = Path.Combine(Environment.CurrentDirectory, ".dotnet", "scenario-preview-120.png"); Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                var bitmap = new RenderTargetBitmap(1375, 813, 120, 120, PixelFormats.Pbgra32); bitmap.Render(viewer);
                using (var stream = File.Create(output)) { var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(stream); }
                viewer.Close();
                Console.WriteLine($"PASS scenario audit: {files.Length} files; voice={voices}; speaker={speakers}; ruby={ruby}; source files unchanged");
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure != null) throw failure;
    }
}
