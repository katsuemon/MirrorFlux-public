using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using MirrorFlux.Controls;
using MirrorFlux.Models;
using MirrorFlux.Services;
using MirrorFlux.Services.Music;
using MirrorFlux.ViewModels;

internal static class TextViewerFeaturesRegression
{
    public static void Run(Action<bool, string> check)
    {
        MirrorFlux.Input.IniKeyParser.TryParseLine(">KEY_DELETE ---- &delete;", out var legacyDelete);
        MirrorFlux.Input.IniKeyParser.TryParseLine(">KEY_ENTER ---- &extexrc;", out var legacyExec);
        check(legacyDelete != null && MirrorFlux.Input.IniKeyParser.FormatLine(legacyDelete).Contains("&filedelete;"), "Legacy delete definitions save with canonical filedelete spelling");
        check(legacyExec != null && MirrorFlux.Input.IniKeyParser.FormatLine(legacyExec).Contains("&extexec;"), "Legacy extexrc definitions save with corrected extexec spelling");
        check(ViewerSearch.Text("one ONE one", "one", false, false).Count == 3 && ViewerSearch.Text("one ONE one", "one", false, true).Count == 2, "Text search respects case option and counts all matches");
        check(ViewerSearch.Text("x12\nx34", @"x\d+", true, false).Count == 2, "Regular expression search spans the whole document");
        check(ViewerSearch.Bytes([1, 2, 3, 1, 9, 3], ViewerSearch.ParseBytes("01 ?? 03")).Count == 2, "HEX byte pattern supports wildcard and independent matches");
        check(ViewerSearch.Address("0x10", 32) == 16 && ViewerSearch.Address("d:10", 32) == 10, "HEX address accepts hexadecimal and explicit decimal");
        var pane = new FilePaneViewModel("selection-test");
        var first = new FileEntry(@"C:\a.txt", "a.txt", false, false, 1, DateTime.Now);
        var folder = new FileEntry(@"C:\folder", "folder", true, false, 0, DateTime.Now);
        var second = new FileEntry(@"C:\b.txt", "b.txt", false, false, 1, DateTime.Now);
        var third = new FileEntry(@"C:\c.txt", "c.txt", false, false, 1, DateTime.Now);
        foreach (var entry in new[] { first, second, third }) pane.Entries.Add(entry);
        pane.SelectedItem = third;
        check(pane.SelectionPathAfterRemoving([second, third]) == first.FullPath, "Delete cursor selects the previous surviving entry");
        pane.SelectedItem = first;
        check(pane.SelectionPathAfterRemoving([first]) == second.FullPath, "Delete cursor falls forward only when no previous entry exists");
        pane.Entries.Insert(1, folder);
        check(pane.FindAdjacentFile(first.FullPath, 1) == second && pane.FindAdjacentFile(first.FullPath, -1) == third,
            "Adjacent text navigation skips folders and wraps at both ends");
        string viewModelIni = Path.Combine(Path.GetTempPath(), "MF-viewer-selection-" + Guid.NewGuid().ToString("N") + ".ini");
        File.WriteAllText(viewModelIni, "%leftPath C:\\\n%rightPath C:\\\n");
        var viewModel = new MainViewModel(new SettingsStore(viewModelIni));
        viewModel.LeftPane.Entries.Add(first); viewModel.LeftPane.Entries.Add(second); viewModel.LeftPane.SelectedItem = first;
        check(viewModel.SelectViewedFile(second.FullPath) && viewModel.LeftPane.SelectedItem == second,
            "Closing a text viewer can restore the cursor to its last displayed file");
        File.Delete(viewModelIni);
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            var task = Exercise(check);
            task.ContinueWith(_ => dispatcher.BeginInvokeShutdown(DispatcherPriority.Background), TaskScheduler.Default);
            Dispatcher.Run();
            try { task.GetAwaiter().GetResult(); } catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure != null) throw failure;
    }
    private static async Task Exercise(Action<bool, string> check)
    {
        string root = Path.Combine(Path.GetTempPath(), "MF-viewer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var viewer = new TextViewerControl { Width = 1100, Height = 650, FontSize = 14 };
        viewer.Resources["PaneText"] = new SolidColorBrush(Color.FromRgb(196, 200, 218));
        viewer.Resources["MutedText"] = new SolidColorBrush(Color.FromRgb(128, 140, 168));
        viewer.Resources["ScreenBackground"] = Brushes.Black;
        viewer.Resources["ViewerBlack"] = Brushes.Black;
        viewer.Resources["TextViewerBackground"] = new SolidColorBrush(Color.FromRgb(13, 16, 26));
        viewer.Resources["TextMinimapBackground"] = new SolidColorBrush(Color.FromRgb(9, 12, 20));
        viewer.Resources["HighlightText"] = new SolidColorBrush(Color.FromRgb(132, 148, 252));
        viewer.Resources["InactiveBorder"] = Brushes.SlateGray;
        viewer.Resources["DarkPurpleBackground"] = new SolidColorBrush(Color.FromRgb(25, 17, 46));
        viewer.Resources["DarkPurpleHighlight"] = new SolidColorBrush(Color.FromRgb(56, 40, 96));
        viewer.Resources["MarkColor"] = new SolidColorBrush(Color.FromRgb(74, 77, 78));
        viewer.Resources["MarkForeground"] = new SolidColorBrush(Color.FromRgb(216, 216, 216));
        // This isolated test does not load App.xaml; give AvalonEdit the resolved
        // viewer background that the real application supplies through ViewerBlack.
        ((TextEditor)viewer.FindName("Editor")).Background = Brushes.Black;
        try
        {
            string path = Path.Combine(root, "test.cfg");
            string content = "[section]\r\nkey=\"日本語\"\r\nvalue=123\r\nkey=end\r\n";
            File.WriteAllText(path, content, new UTF8Encoding(true));
            check(viewer.Load(path, false), "New text viewer loads UTF8 with BOM");
            int navigation = 0;
            viewer.FileNavigationRequested += direction => navigation = direction;
            viewer.ExecuteShiftFunctionKey(5);
            check(navigation == -1, "Shift+F5 requests the previous file in text mode");
            viewer.ExecuteShiftFunctionKey(6);
            check(navigation == 1, "Shift+F6 requests the next file in text mode");
            viewer.Measure(new Size(1100, 650)); viewer.Arrange(new Rect(0, 0, 1100, 650)); viewer.UpdateLayout();
            var editor = (TextEditor)viewer.FindName("Editor");
            check(editor.WordWrap, "Text viewer wraps by default");
            using (var source = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("MF-text-navigation-test") { Width = 1, Height = 1 }))
            {
                System.Windows.Input.KeyEventArgs Key(System.Windows.Input.Key key) => new(System.Windows.Input.Keyboard.PrimaryDevice, source, 0, key);
                editor.Select(3, 8);
                check(viewer.HandleKeyDown(Key(System.Windows.Input.Key.End), System.Windows.Input.ModifierKeys.None) &&
                    editor.CaretOffset == editor.Document.TextLength && editor.SelectionLength == 0,
                    "End moves to document end and clears the previous selection");
                check(viewer.HandleKeyDown(Key(System.Windows.Input.Key.Home), System.Windows.Input.ModifierKeys.None) && editor.CaretOffset == 0,
                    "Home moves to document start");
                check(!viewer.HandleKeyDown(Key(System.Windows.Input.Key.Home), System.Windows.Input.ModifierKeys.Shift) &&
                    !viewer.HandleKeyDown(Key(System.Windows.Input.Key.End), System.Windows.Input.ModifierKeys.Control),
                    "Modified Home/End retain native selection and navigation");
            }
            string boundaryPath = Path.Combine(root, "utf8-boundary.txt");
            string boundaryText = new string('a', 65534) + "日本語\n末尾";
            File.WriteAllText(boundaryPath, boundaryText, new UTF8Encoding(false));
            viewer.Load(boundaryPath, false);
            check(editor.Text == boundaryText && viewer.StatusText.Contains("UTF-8"), "Viewer decodes UTF8 across the 64KiB sample boundary without corruption");
            viewer.Load(path, false);
            viewer.ExecuteFunctionKey(2);
            check(((Border)viewer.FindName("SearchBar")).Visibility == Visibility.Visible, "F2 opens search without permanent toolbar");
            bool wrapped = editor.WordWrap; viewer.ExecuteFunctionKey(10);
            check(editor.WordWrap != wrapped, "F10 toggles word wrapping");

            editor.CaretOffset = editor.Text.IndexOf('日');
            check(viewer.LocationText.Contains("「日」") && viewer.LocationText.Contains("U+65E5") && viewer.LocationText.Contains("E6 97 A5"), "Caret status shows Japanese character Unicode and UTF8 bytes");
            editor.Text = "A\t \r\n😀";
            editor.CaretOffset = 0;
            check(viewer.LocationText.Contains("U+0041") && viewer.LocationText.Contains("UTF-8:41"), "Caret status shows ASCII character code");
            editor.CaretOffset = 1;
            check(viewer.LocationText.Contains("[TAB]") && viewer.LocationText.Contains("U+0009"), "Caret status labels tab characters");
            editor.CaretOffset = 2;
            check(viewer.LocationText.Contains("[SPACE]") && viewer.LocationText.Contains("U+0020"), "Caret status labels spaces");
            editor.CaretOffset = 3;
            check(viewer.LocationText.Contains("[CRLF]") && viewer.LocationText.Contains("0D 0A"), "Caret status shows CRLF delimiter codes");
            editor.CaretOffset = 5;
            check(viewer.LocationText.Contains("「😀」") && viewer.LocationText.Contains("U+1F600") && viewer.LocationText.Contains("F0 9F 98 80"), "Caret status combines surrogate pair into one Unicode character");
            editor.CaretOffset = editor.Document.TextLength;
            check(viewer.LocationText.Contains("[EOF]"), "Caret status handles end of file without reading beyond it");
            string sjis = Path.Combine(root, "sjis.txt"); File.WriteAllBytes(sjis, CharsetDetector.ShiftJis.GetBytes("日本語"));
            viewer.Load(sjis, false);
            check(viewer.LocationText.Contains("「日」") && viewer.LocationText.Contains("93 FA"), "Caret status follows detected Shift JIS encoding");
            viewer.Load(path, false); editor = (TextEditor)viewer.FindName("Editor");
            editor.Select(11, 17);
            check(viewer.SelectedTextOrLine() == editor.Text.Substring(11, 17), "Text selection copies exact multi-line character range");
            viewer.JumpToByte(14); int originalByte = viewer.CurrentByteOffset;
            viewer.ToggleHexMode(); check(viewer.CurrentByteOffset == originalByte, "Text to HEX retains byte position");
            viewer.ToggleHexMode(); check(viewer.CurrentByteOffset == originalByte, "HEX to text retains byte position");
            var searchTheme = new AppSettings { ColSearchBg = "#FFE347", ColSearchFg = "#111111", ColSearchCurrentBg = "#FF9440", ColSearchCurrentFg = "#121212" };
            var searchStore = new SettingsStore(Path.Combine(root, "search-colors.ini"));
            check(searchStore.TrySave(searchTheme, out _) && searchStore.Load().Settings.ColSearchBg == "#FFE347" &&
                searchStore.Load().Settings.ColSearchFg == "#111111" && searchStore.Load().Settings.ColSearchCurrentBg == "#FF9440" &&
                searchStore.Load().Settings.ColSearchCurrentFg == "#121212", "Dedicated search colors survive INI save and load");
            await viewer.SearchForAsync(@"key=.+", true);
            check(viewer.MatchCount == 2, "Viewer search reports all regex matches");
            viewer.NextMatch(true); int first = editor.SelectionStart; viewer.NextMatch(true);
            check(editor.SelectionStart != first && editor.SelectionLength > 0, "Next match advances actual selected range");
            check(((SolidColorBrush)editor.TextArea.SelectionBrush).Color == Color.FromRgb(255, 140, 66) &&
                ((SolidColorBrush)editor.TextArea.SelectionForeground).Color == Color.FromRgb(16, 16, 24), "Current search match has high contrast orange and dark text");
            editor.Select(0, 1);
            check(ReferenceEquals(editor.TextArea.SelectionBrush, viewer.Resources["MarkColor"]), "Ordinary text selection retains its normal theme color");
            await viewer.SearchForAsync("[", true);
            check(viewer.SearchError.Length > 0 && viewer.MatchCount == 0, "Invalid regex is reported without crashing");
            await VerifyTextDecorations(root, check);
            ScenarioHighlightRegression.Run(check);
            var settings = new AppSettings();
            check(ViewerSyntaxService.Resolve("a.cfg", "{\"x\": 1}", settings) == "json" && ViewerSyntaxService.Resolve("a.cfg", "[section]\nx=1", settings) == "ini", "CFG auto highlighting distinguishes JSON and INI");
            foreach (var (name, sample) in new[] { ("a.ini", "[section]\nx=1"), ("a.json", "{\"x\": 1}"), ("a.xml", "<a/>"), ("a.cs", "class A {}"), ("a.cpp", "#include <stdio.h>\nint main() { return 0; }") })
            {
                editor.Text = sample; ViewerSyntaxService.Apply(editor, name, settings);
                check(editor.SyntaxHighlighting != null, "Embedded syntax definition loads: " + Path.GetExtension(name));
            }
            var sourceStore = new SettingsStore(Path.Combine(root, "source-settings.ini"));
            File.WriteAllText(sourceStore.SettingsPath, ".txt ---- &textviewer;\n%tvColorUrl #55AAFF\n");
            var sourceSettings = sourceStore.Load().Settings;
            check(new[] { ".c", ".cpp", ".h", ".cc", ".cxx", ".hpp", ".hh", ".hxx", ".doc", ".man", ".text" }.All(ext => sourceSettings.ExtBindings.Any(b => b.Extension == ext && b.Command == MirrorFlux.Input.AppCommand.TextViewer)), "Existing INI gains C C++ header and documentation viewer associations");
            check(new[] { "c", "cpp", "h", "cc", "cxx", "hpp", "hh", "hxx" }.All(ext => sourceSettings.TVSyntaxMappings[ext] == "cpp"), "C and C++ extensions map to embedded CPP syntax");
            check(sourceStore.TrySave(sourceSettings, out _) && sourceStore.Load().Settings.TVColorUrl == "#55AAFF", "URL highlight color survives INI roundtrip");
            editor.Text = "https://example.com/test";
            viewer.Measure(new Size(1100, 650)); viewer.Arrange(new Rect(0, 0, 1100, 650)); viewer.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.Render);
            var link = editor.TextArea.TextView.VisualLines.SelectMany(line => line.Elements).OfType<ICSharpCode.AvalonEdit.Rendering.VisualLineLinkText>().First();
            check(((SolidColorBrush)link.TextRunProperties.ForegroundBrush).Color == Color.FromRgb(105, 201, 255), "URL rendering uses visible dedicated link color");
            await viewer.SearchForAsync("example");
            viewer.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.Render);
            check(editor.TextArea.TextView.VisualLines.SelectMany(line => line.Elements).OfType<ICSharpCode.AvalonEdit.Rendering.VisualLineLinkText>().Any(element => ReferenceEquals(element.TextRunProperties.ForegroundBrush, ViewerPalette.Get(editor, "SearchForeground"))), "URL search matches retain high contrast search text color");
            settings.TVHighlight = false; ViewerSyntaxService.Apply(editor, "a.ini", settings);
            check(editor.SyntaxHighlighting == null, "INI can disable highlighting"); settings.TVHighlight = true;
            string rules = Path.Combine(root, "syntax"); Directory.CreateDirectory(rules);
            File.WriteAllText(Path.Combine(rules, "ini.xshd"), "<broken>"); settings.TVSyntaxPath = rules;
            ViewerSyntaxService.Apply(editor, "a.ini", settings);
            check(editor.SyntaxHighlighting == null, "Invalid external syntax falls back to plain text");
            File.WriteAllText(Path.Combine(rules, "ini.xshd"), "<SyntaxDefinition name=\"Custom\" xmlns=\"http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008\"><RuleSet/></SyntaxDefinition>");
            ViewerSyntaxService.Apply(editor, "a.ini", settings);
            check(editor.SyntaxHighlighting?.Name == "Custom", "Optional XSHD overrides embedded syntax");
            settings.PdxSearchPaths = root + ";" + rules; settings.TVSyntaxMappings["cfg"] = "ini";
            var store = new SettingsStore(Path.Combine(root, "settings.ini"));
            check(store.TrySave(settings, out _) && store.Load().Settings.PdxSearchPaths == settings.PdxSearchPaths && store.Load().Settings.TVSyntaxMappings["cfg"] == "ini", "PDX paths and syntax mappings survive INI roundtrip");
            viewer.Close();
            string big = Path.Combine(root, "large.txt");
            await File.WriteAllTextAsync(big, string.Concat(Enumerable.Repeat("alpha beta gamma delta\n", 530000)) + "LAST_MARKER\n");
            check(new FileInfo(big).Length > 10 * 1024 * 1024 && viewer.Load(big, false), "Files beyond old 10 MB limit start loading");
            await viewer.PendingLoad;
            editor = (TextEditor)viewer.FindName("Editor");
            check(editor.Text.EndsWith("LAST_MARKER\n"), "Large text includes actual end of file");
            await viewer.SearchForAsync("LAST_MARKER");
            check(viewer.MatchCount == 1, "Search reaches beyond former 10 MB cutoff");
            var map = (TextMinimap)viewer.FindName("Minimap"); map.Navigate?.Invoke(0.9);
            check(editor.TextArea.Caret.Line > 400000, "Minimap navigation reaches corresponding document section");
            viewer.ToggleHexMode(); var hex = (HexViewerControl)viewer.FindName("HexView");
            hex.Jump(hex.DataLength - 1); check(hex.Caret > 10 * 1024 * 1024, "HEX reaches file end beyond prior dump cutoff");
            await viewer.SearchForAsync("4C 41 53 54 5F 4D 41 52 4B 45 52", bytes: true);
            check(viewer.MatchCount == 1, "HEX byte search covers entire large file");
            hex.SetData([1, 2, 3, 4, 5, 6, 7, 8]); hex.SelectRange(0, 4);
            check(hex.CopyText(false) == "01 02 03 04", "HEX selection copies selected bytes only");
            hex.BigEndian = false; check(hex.Inspect().Contains("Int32:67305985"), "HEX inspector reads little endian Int32");
            hex.BigEndian = true; check(hex.Inspect().Contains("Int32:16909060"), "HEX inspector reads big endian Int32");
            foreach (int size in new[] { 8, 16, 32 }) { hex.BytesPerRow = size; check(hex.BytesPerRow == size, "HEX bytes-per-row setting: " + size); }
            viewer.Close(); viewer.Load(path, false); viewer.JumpToByte(5); int before = viewer.CurrentByteOffset;
            await File.AppendAllTextAsync(path, "append=1\r\n"); await viewer.RefreshFromDiskAsync();
            check(viewer.CurrentByteOffset == before && ((TextEditor)viewer.FindName("Editor")).Text.Contains("append=1"), "External append reloads while preserving reading position");
            viewer.SetFollow(true);
            await File.AppendAllTextAsync(path, "tail=2\r\n"); await viewer.RefreshFromDiskAsync();
            check(((TextEditor)viewer.FindName("Editor")).CaretOffset == ((TextEditor)viewer.FindName("Editor")).Document.TextLength, "Tail follow moves to appended file end");
            await File.WriteAllTextAsync(path, "short"); await viewer.RefreshFromDiskAsync();
            check(((TextEditor)viewer.FindName("Editor")).Text == "short", "Tail reload handles truncation");
            viewer.Close();
            await File.WriteAllTextAsync(path, "; configuration preview\n[General]\n" + string.Concat(Enumerable.Range(1, 180).Select(i => $"key_{i}=\"value {i}\"\n")));
            viewer.Load(path, false); viewer.SetFollow(false);
            await viewer.SearchForAsync(@"key_\d+", true);
            ((Border)viewer.FindName("SearchBar")).Visibility = Visibility.Visible;
            viewer.NextMatch(true);
            viewer.Measure(new Size(1100, 650)); viewer.Arrange(new Rect(0, 0, 1100, 650)); viewer.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.Render);
            foreach (double dpi in new[] { 96.0, 120.0 })
            {
                var bitmap = new RenderTargetBitmap((int)(1100 * dpi / 96), (int)(650 * dpi / 96), dpi, dpi, PixelFormats.Pbgra32); bitmap.Render(viewer);
                string output = Path.Combine(Environment.CurrentDirectory, ".dotnet", "viewer-" + (int)dpi + ".png"); Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                using var stream = File.Create(output); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(stream);
            }
            check(map.ActualWidth > 0, "Minimap renders at 100 and 125 percent DPI");
            viewer.ToggleHexMode(); hex.BytesPerRow = 16; viewer.ExecuteShiftFunctionKey(6); hex.SelectRange(8, 24);
            await viewer.SearchForAsync("6B 65 79", bytes: true);
            viewer.Measure(new Size(1100, 650)); viewer.Arrange(new Rect(0, 0, 1100, 650)); viewer.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.Render);
            var hexBitmap = new RenderTargetBitmap(1375, 813, 120, 120, PixelFormats.Pbgra32); hexBitmap.Render(viewer);
            using (var output = File.Create(Path.Combine(Environment.CurrentDirectory, ".dotnet", "viewer-hex-120.png"))) { var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(hexBitmap)); encoder.Save(output); }
            viewer.Close();
            var pane = new FilePaneViewModel("test", root); await pane.LoadAsync(root);
            pane.SelectedItem = pane.Entries.First(e => e.FullPath == path); pane.SelectedItem.IsMarked = true;
            using var monitor = new PaneDirectoryMonitor(pane, Dispatcher.CurrentDispatcher);
            await File.WriteAllTextAsync(path, "saved externally with a larger size"); File.SetLastWriteTime(path, DateTime.Now.AddMinutes(1));
            for (int i = 0; i < 100 && pane.Entries.First(e => e.FullPath == path).Length != new FileInfo(path).Length; i++) await Task.Delay(30);
            check(pane.SelectedItem?.FullPath == path && pane.SelectedItem.IsMarked && pane.SelectedItem.Length == new FileInfo(path).Length && pane.SelectedItem.LastWriteTime == File.GetLastWriteTime(path), "External save watcher updates metadata while preserving selection and marks");
            await VerifyColorToggleAndExternalOpen(root, check);
            await VerifyDocumentLifecycle(root, check);
            await Pdx(root, check);
        }
        finally { viewer.Close(); Directory.Delete(root, true); }
    }
    private static async Task VerifyTextDecorations(string root, Action<bool, string> check)
    {
        string path = Path.Combine(root, "mixed-endings.txt");
        const string text = "A日B\rA日B\nA日B\r\n\r\n";
        File.WriteAllText(path, text);
        foreach (double scale in new[] { 1.0, 1.25 })
        {
            var control = new TextViewerControl { Width = 420, Height = 160, FontSize = 16 };
            control.Resources["InactiveBorder"] = Brushes.Magenta;
            control.Resources["PaneText"] = Brushes.White;
            control.Resources["ViewerBlack"] = Brushes.Black;
            control.Load(path, false);
            var editor = (TextEditor)control.FindName("Editor");
            editor.Background = Brushes.Black;
            var view = editor.TextArea.TextView;
            control.Measure(new Size(420, 160));
            VisualTreeHelper.SetRootDpi(control, new DpiScale(scale, scale));
            control.Arrange(new Rect(0, 0, 420, 160));
            control.UpdateLayout();

            int width = (int)Math.Ceiling(view.ActualWidth * scale);
            int height = (int)Math.Ceiling(view.ActualHeight * scale);
            var cursorLayer = view.Layers[view.Layers.Count - 1];
            byte[] Render(int offset, bool showCursor = true)
            {
                cursorLayer.Opacity = showCursor ? 1 : 0;
                if (editor.CaretOffset != offset) editor.CaretOffset = offset;
                control.UpdateLayout();
                var bitmap = new RenderTargetBitmap(width, height, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                bitmap.Render(view);
                var data = new byte[width * height * 4];
                bitmap.CopyPixels(data, width * 4, 0);
                return data;
            }

            var endings = Render(text.Length, false);
            string MarkerPixels(int number)
            {
                var line = view.GetVisualLine(number)!;
                int y0 = (int)Math.Round(line.VisualTop * scale);
                int y1 = (int)Math.Round((line.VisualTop + line.Height) * scale);
                var points = new List<int>();
                for (int y = y0; y < y1; y++)
                    for (int x = 0; x < width; x++)
                    {
                        int i = (y * width + x) * 4;
                        if (endings[i] > endings[i + 1] + 40 && endings[i + 2] > endings[i + 1] + 40)
                            points.Add((y - y0) * width + x);
                    }
                return string.Join(",", points);
            }
            string[] marks = [MarkerPixels(1), MarkerPixels(2), MarkerPixels(3)];
            check(marks.All(mark => mark.Length != 0) && marks.Distinct().Count() == 3 &&
                MarkerPixels(4).Length != 0 && MarkerPixels(5).Length == 0,
                $"Mixed CR, LF, CRLF render distinct scrollbar-colored markers, including empty line and EOF at {scale:P0}");
            editor.SelectAll();
            check(editor.SelectedText == text, "Selection preserves original mixed delimiters");
            editor.Select(0, 0);

            (int Width, int Top) Box(int offset)
            {
                var normal = Render(offset, false);
                var data = Render(offset);
                int left = width, right = -1, top = height;
                for (int i = 0; i < data.Length; i += 4)
                    if (Math.Abs(data[i + 2] - normal[i + 2]) > 60)
                    {
                        int x = i / 4 % width;
                        left = Math.Min(left, x);
                        right = Math.Max(right, x);
                        top = Math.Min(top, i / 4 / width);
                    }
                return (right - left + 1, top);
            }
            int narrow = Box(0).Width, full = Box(1).Width, marker = Box(3).Width, eof = Box(text.Length).Width;
            check(narrow > 2 && full > narrow && marker > 2 && eof > 2,
                $"Box caret widths at {scale:P0}: Latin {narrow}, fullwidth {full}, end marker {marker}, EOF {eof}");

            int cellPixels = (int)Math.Round(view.WideSpaceWidth * scale);
            check(new[] { 3, 7, 11, 13 }.All(offset => Math.Abs(Box(offset).Width - cellPixels) <= 1),
                $"CR, LF and stacked CRLF all occupy one cell at {scale:P0}");
            bool ReversesInk(int offset)
            {
                var before = Render(offset, false);
                var after = Render(offset);
                int inkDarkened = 0, backgroundLightened = 0;
                for (int i = 2; i < after.Length; i += 4)
                {
                    if (before[i] - after[i] > 60) inkDarkened++;
                    if (after[i] - before[i] > 60) backgroundLightened++;
                }
                return inkDarkened > 0 && backgroundLightened > 0;
            }
            check(ReversesInk(1) && ReversesInk(3) && ReversesInk(11),
                $"Caret swaps the glyph and background, including compact EOL labels at {scale:P0}");
            editor.CaretOffset = 11;
            System.Windows.Documents.EditingCommands.MoveRightByCharacter.Execute(null, editor.TextArea);
            bool crossed = editor.CaretOffset == 13;
            System.Windows.Documents.EditingCommands.MoveLeftByCharacter.Execute(null, editor.TextArea);
            check(crossed && editor.CaretOffset == 11, "Arrow navigation crosses CRLF with one key press each way");

            editor.Text = new string('a', 70) + "日";
            editor.WordWrap = true;
            var wrappedBox = Box(70);
            var wrappedLine = view.GetVisualLine(1)!;
            double lastRowTop = wrappedLine.GetTextLineVisualYPosition(wrappedLine.TextLines[^1],
                ICSharpCode.AvalonEdit.Rendering.VisualYPosition.TextTop) - view.VerticalOffset;
            check(wrappedLine.TextLines.Count > 1 && wrappedBox.Width > narrow &&
                Math.Abs(wrappedBox.Top - Math.Round(lastRowTop * scale)) <= 1,
                $"Box caret moves to the actual wrapped row at {scale:P0}");
            if (scale == 1 && TextViewerControl.CursorBlinkInterval is { } interval)
            {
                using var host = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("MF caret blink test")
                    { Width = 420, Height = 160, PositionX = -10000, PositionY = -10000, WindowStyle = unchecked((int)0x90000000) });
                host.RootVisual = control;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                editor.CaretOffset = 1;
                var lit = Box(1);
                bool off = false, onAgain = false;
                var timeout = System.Diagnostics.Stopwatch.StartNew();
                while (!onAgain && timeout.Elapsed < interval * 3 + TimeSpan.FromSeconds(1))
                {
                    await Task.Delay(35);
                    var frame = Box(1);
                    if (frame.Width <= 0) off = true;
                    if (off && frame.Width > 0) onAgain = true;
                }
                check(lit.Width > 0 && onAgain, $"Focused text cursor blinks off and back on (initial={lit.Width}, off={off}, visible={cursorLayer.IsVisible})");
                host.RootVisual = null;
            }
            control.Close();
        }
    }

    private static async Task VerifyColorToggleAndExternalOpen(string root, Action<bool, string> check)
    {
        string path = Path.Combine(root, "color-toggle.ini"); await File.WriteAllTextAsync(path, "[section]\nx=1\n");
        var control = new TextViewerControl(); control.Load(path, false);
        var editor = (TextEditor)control.FindName("Editor");
        using (var source = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("MF-system-key-test") { Width = 1, Height = 1 }))
        {
            var systemF10 = new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, source, 0, System.Windows.Input.Key.F10);
            typeof(System.Windows.Input.KeyEventArgs).GetMethod("MarkSystem", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(systemF10, null);
            bool handled = control.HandleKeyDown(systemF10, System.Windows.Input.ModifierKeys.Shift);
            check(systemF10.Key == System.Windows.Input.Key.System && handled && editor.SyntaxHighlighting == null, "System-key Shift F10 is consumed and toggles syntax instead of opening window menu");
            control.HandleKeyDown(systemF10, System.Windows.Input.ModifierKeys.Shift);
        }
        control.ExecuteShiftFunctionKey(10);
        check(editor.SyntaxHighlighting == null && control.StatusText.Contains("Syntax : OFF"), "Shift F10 disables coloring and updates status");
        await File.AppendAllTextAsync(path, "y=2\n"); await control.RefreshFromDiskAsync();
        check(editor.SyntaxHighlighting == null, "Color off state survives external reload");
        control.ToggleHexMode(); control.ToggleHexMode();
        check(editor.SyntaxHighlighting == null, "Color off state survives TEXT HEX switches");
        control.ExecuteShiftFunctionKey(10);
        check(editor.SyntaxHighlighting != null && control.StatusText.Contains("Syntax : ON"), "Shift F10 restores syntax coloring");
        System.Diagnostics.ProcessStartInfo? request = null;
        control.AssociatedFileLauncher = value => request = value;
        control.OpenWithWindowsAssociation();
        check(request?.FileName == path && request.UseShellExecute && editor.Text.Contains("y=2"), "External open uses Windows association for current file without closing viewer");
        control.AssociatedFileLauncher = _ => throw new System.ComponentModel.Win32Exception("association unavailable");
        control.OpenWithWindowsAssociation();
        check(control.StatusText.Contains("関連付けで開けませんでした") && editor.Text.Contains("y=2"), "Missing Windows association reports status without crashing or closing");
        control.Close();
    }
    private static async Task VerifyDocumentLifecycle(string root, Action<bool, string> check)
    {
        string many = Path.Combine(root, "many-lines.txt"), replacement = Path.Combine(root, "replacement.txt");
        await File.WriteAllTextAsync(many, string.Concat(Enumerable.Repeat("row text\n", 1500)));
        await File.WriteAllTextAsync(replacement, string.Concat(Enumerable.Repeat("new " + new string('x', 2000) + "\n", 600)));
        TextViewerControl CreateDeepViewer()
        {
            var control = new TextViewerControl { Width = 900, Height = 500, FontSize = 14 };
            control.Load(many, false); control.Measure(new Size(900, 500)); control.Arrange(new Rect(0, 0, 900, 500)); control.UpdateLayout();
            var editor = (TextEditor)control.FindName("Editor");
            editor.CaretOffset = editor.Document.GetLineByNumber(838).Offset;
            editor.ScrollToLine(838); control.UpdateLayout();
            return control;
        }
        var closing = CreateDeepViewer();
        int closeEvents = 0; closing.StatusChanged += (_, _) => closeEvents++;
        closing.Close();
        check(closeEvents == 0, "Closing scrolled viewer at line 838 does not read stale caret or emit status events");
        var asynchronous = CreateDeepViewer();
        asynchronous.Load(replacement, false); await asynchronous.PendingLoad;
        check(asynchronous.LocationText.Contains("Ln 1/") && asynchronous.LocationText.Contains("U+006E"), "Async document replacement resets deep caret before describing characters");
        asynchronous.Close();
        var truncated = CreateDeepViewer();
        await File.WriteAllTextAsync(many, "short"); await truncated.RefreshFromDiskAsync();
        check(truncated.LocationText.Contains("Ln 1/1") && truncated.LocationText.Contains("[EOF]"), "Truncation from many lines clamps caret and updates status without crashing");
        truncated.Close();
        await File.WriteAllTextAsync(many, string.Concat(Enumerable.Repeat("row text\n", 1500)));
        var direct = CreateDeepViewer();
        ((TextEditor)direct.FindName("Editor")).Document = new ICSharpCode.AvalonEdit.Document.TextDocument("changed");
        check(direct.LocationText.Contains("Ln 1/1") && direct.LocationText.Contains("U+0063"), "Reentrant scroll notification during direct document replacement ignores stale caret");
        direct.Close();
        var cancelled = CreateDeepViewer();
        cancelled.Load(replacement, false); int events = 0; cancelled.StatusChanged += (_, _) => events++;
        cancelled.Close(); await cancelled.PendingLoad;
        check(events == 0, "Closing pending async load prevents late caret status updates");
        var switching = CreateDeepViewer(); int offset = switching.CurrentByteOffset;
        switching.ToggleHexMode(); switching.ToggleHexMode();
        check(switching.CurrentByteOffset == offset && switching.LocationText.Contains("Ln 838/"), "Deep text HEX mode changes keep position and safe character status");
        switching.Close();
    }
    private static async Task Pdx(string root, Action<bool, string> check)
    {
        string folder = Path.Combine(root, "mdx"), bankFolder = Path.Combine(root, "banks"); Directory.CreateDirectory(folder); Directory.CreateDirectory(bankFolder);
        string path = Path.Combine(folder, "song.mdx");
        byte[] original = MdxRegression.CreateTestMdx();
        byte[] mdx = [.. original.Take(7), .. Encoding.ASCII.GetBytes("bank\0"), .. original.Skip(8)]; await File.WriteAllBytesAsync(path, mdx);
        byte[] bank = new byte[768 + 32]; BinaryPrimitives.WriteUInt32BigEndian(bank.AsSpan(0, 4), 768); BinaryPrimitives.WriteUInt32BigEndian(bank.AsSpan(4, 4), 32);
        string pdx = Path.Combine(bankFolder, "bank.pdx"); await File.WriteAllBytesAsync(pdx, bank);
        var loaded = MdxFile.Load(path, [bankFolder]);
        check(loaded.PdxResolvedPath == pdx && loaded.PdxData?.Length == bank.Length, "MDX resolves and reads PDX from configured fallback folder");
        await File.WriteAllBytesAsync(Path.Combine(folder, "bank.pdx"), bank);
        check(MdxFile.Load(path, [bankFolder]).PdxResolvedPath == Path.Combine(folder, "bank.pdx"), "PDX beside MDX wins over fallback folder");
        using (var provider = new MdxWaveProvider(path)) check(provider.Snapshot().AuxiliaryFileStatus.Contains("bank.pdx"), "Music snapshot reports actual loaded PDX path");
        string zip = Path.Combine(root, "music.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            using (var stream = archive.CreateEntry("album/song.mdx").Open()) await stream.WriteAsync(mdx);
            using (var stream = archive.CreateEntry("album/bank.pdx").Open()) await stream.WriteAsync(bank);
        }
        string? extracted = await ArchiveService.ExtractEntryToTempFileAsync(Path.Combine(zip, "album", "song.mdx"));
        check(extracted != null && MdxFile.Load(extracted).PdxData?.Length == bank.Length, "Archived MDX extracts matching sibling PDX before playback");
    }
}
