using System.IO;
using System.Windows.Input;
using MirrorFlux.Input;
using MirrorFlux.Services;

internal static class JumpListRegression
{
    public static void KeyboardInput()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var app = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
            System.Xml.Linq.XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            System.Xml.Linq.XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            var resources = System.Xml.Linq.XDocument.Load("App.xaml").Root!.Element(ns + "Application.Resources")!;
            var dictionary = new System.Xml.Linq.XElement(ns + "ResourceDictionary", new System.Xml.Linq.XAttribute(System.Xml.Linq.XNamespace.Xmlns + "x", x),
                resources.Elements(ns + "Style").Single(e => (string?)e.Attribute(x + "Key") == "RetroDialogListBoxItemStyle"));
            app.Resources = (System.Windows.ResourceDictionary)System.Windows.Markup.XamlReader.Parse(dictionary.ToString());
            var owner = new System.Windows.Window { Width = 1, Height = 1, Left = -20000, Top = -20000,
                ShowInTaskbar = false, Opacity = 0 };
            owner.Show();
            var dialog = new MirrorFlux.JumpListDialog(new Dictionary<int, string> { [1] = "shell:Desktop", [4] = Path.Combine(Path.GetTempPath(), "mf-missing-" + Guid.NewGuid().ToString("N")), [10] = "C:\\Ten" })
                { Owner = owner, Left = -20000, Top = -20000, Opacity = 0 };
            dialog.ContentRendered += (_, _) => dialog.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, new Action(async () =>
            {
                try
                {
                    var list = (System.Windows.Controls.ListBox)dialog.FindName("Paths");
                    if ((string?)list.Items[0].GetType().GetProperty("DisplayName")!.GetValue(list.Items[0]) != "デスクトップ")
                        throw new Exception("Known folder label is not available to the jump list binding");
                    Console.WriteLine($"Initial focus={Keyboard.FocusedElement?.GetType().Name}, selected={list.SelectedIndex}");
                    void Press(Key key)
                    {
                        var target = Keyboard.FocusedElement as System.Windows.UIElement ?? list;
                        var e = new KeyEventArgs(Keyboard.PrimaryDevice, System.Windows.PresentationSource.FromVisual(dialog)!, Environment.TickCount, key)
                            { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                        target.RaiseEvent(e);
                        if (!e.Handled) { e.RoutedEvent = Keyboard.KeyDownEvent; target.RaiseEvent(e); }
                    }
                    Press(Key.Down);
                    if (list.SelectedIndex != 1) throw new Exception($"First Down must select the second entry; actual={list.SelectedIndex}");
                    Console.WriteLine("PASS first Down advances from the initial selection");
                    Press(Key.D0);
                    if (list.SelectedIndex != 2) throw new Exception("Digit 0 must select sparse slot 10");
                    Press(Key.Up);
                    if (list.SelectedIndex != 1) throw new Exception("First Up after numeric selection must move from that entry");
                    Console.WriteLine("PASS arrow navigation follows numeric selection");
                    Press(Key.Enter);
                    var timeout = System.Diagnostics.Stopwatch.StartNew();
                    while (!dialog.IsEnabled && timeout.ElapsedMilliseconds < 5000) await Task.Delay(10);
                    if (!dialog.IsEnabled || string.IsNullOrEmpty(((System.Windows.Controls.TextBlock)dialog.FindName("ErrorText")).Text))
                        throw new Exception("Missing jump destination must restore input and show an error");
                    Press(Key.Down);
                    if (list.SelectedIndex != 2) throw new Exception("First Down after a validation error must move from the selected entry");
                    Console.WriteLine("PASS first Down after a missing-path error advances immediately");
                    Press(Key.Escape);
                    if (dialog.IsVisible) throw new Exception("Esc must dismiss the dialog immediately");
                    Console.WriteLine("PASS Escape dismisses jump list");
                }
                catch (Exception ex) { failure = ex; }
                finally { if (dialog.IsVisible) dialog.Close(); }
            }));
            dialog.ShowDialog(); owner.Close(); app.Shutdown();
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure != null) throw failure;
    }

    public static void Render()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var app = new MirrorFlux.App(); app.InitializeComponent();
                var paths = Enumerable.Range(1, 10).ToDictionary(i => i, i => $"C:\\Example\\Folder {i:00}");
                paths[1] = "shell:Desktop"; paths[2] = "shell:Downloads"; paths[3] = "shell:Personal";
                var dialog = new MirrorFlux.JumpListDialog(paths);
                var content = (System.Windows.FrameworkElement)dialog.Content;
                foreach (double scale in new[] { 1.0, 1.25 })
                {
                    System.Windows.Media.VisualTreeHelper.SetRootDpi(content, new System.Windows.DpiScale(scale, scale));
                    content.Measure(new System.Windows.Size(dialog.Width, double.PositiveInfinity));
                    content.Arrange(new System.Windows.Rect(0, 0, dialog.Width, content.DesiredSize.Height)); content.UpdateLayout();
                    var list = (System.Windows.Controls.ListBox)dialog.FindName("Paths");
                    if (list.Items.Count != 10 || list.SelectedIndex != 0) throw new Exception("Jump list initial selection incorrect");
                    string folder = Path.Combine(Environment.CurrentDirectory, ".dotnet", "jump-list-preview"); Directory.CreateDirectory(folder);
                    var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(dialog.Width * scale), (int)Math.Ceiling(content.ActualHeight * scale), 96 * scale, 96 * scale, System.Windows.Media.PixelFormats.Pbgra32);
                    bitmap.Render(content); var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                    using var stream = File.Create(Path.Combine(folder, $"jump-list-{(int)(96 * scale)}.png")); encoder.Save(stream);
                }
                Console.WriteLine("PASS jump list rendering: 10 entries at 100/125 percent");
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure != null) throw failure;
    }
    public static void Run(string root, Action<bool, string> check)
    {
        check(new MirrorFlux.Models.AppSettings().JumpPaths[3] == "shell:Personal", "Documents is the third default jump destination");
        foreach (var (alias, label) in new[] { ("Desktop", "デスクトップ"), ("Downloads", "ダウンロード"), ("Personal", "マイドキュメント"), ("My Pictures", "ピクチャ"), ("My Music", "ミュージック"), ("My Video", "ビデオ") })
        {
            string actual = JumpListService.ResolvePath("shell:" + alias);
            check(Path.IsPathFullyQualified(actual) && JumpListService.GetDisplayName("shell:" + alias) == label &&
                JumpListService.GetDisplayName(actual.ToUpperInvariant() + Path.DirectorySeparatorChar) == label,
                "Known folder resolves and displays Japanese name: " + alias);
            string child = Path.Combine(actual, "ordinary-child");
            check(JumpListService.GetDisplayName(child) == child, "Known-folder child retains its path: " + alias);
        }
        check(JumpListService.ResolvePath("shell:Personal") == Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments, Environment.SpecialFolderOption.DoNotVerify),
            "Documents uses the Windows configured location");
        check(JumpListService.ResolvePath(@"%USERPROFILE%\Desktop") == JumpListService.ResolvePath("shell:Desktop") &&
            JumpListService.GetDisplayName(@"%USERPROFILE%\Downloads") == "ダウンロード", "Legacy default jump paths follow Windows known folders");
        string blankIni = Path.Combine(root, "blank-document-jump.ini");
        File.WriteAllText(blankIni, "%jumpPath03\n");
        check(!new SettingsStore(blankIni).Load().Settings.JumpPaths.ContainsKey(3), "Explicitly blank jump slot stays disabled");
        string ini = Path.Combine(root, "jump.ini");
        File.WriteAllText(ini, $"%jumpPath01 \"{root}\"\n%jumpPath02\n%jumpPath03 %TEMP%\n%jumpPath10 .\n%jumpPath11 invalid\n>KEY_J -s-- &jumplist;\n");
        var store = new SettingsStore(ini); var settings = store.Load().Settings;
        check(settings.JumpPaths.Count == 3 && settings.JumpPaths[1] == root && !settings.JumpPaths.ContainsKey(11), "Jump list reads numbered slots and quoted paths, rejects out of range");
        check(settings.KeyBindings.Any(k => k.Command == AppCommand.JumpList && k.Modifiers == ModifierKeys.Shift), "jumplist command parses in INI key bindings");
        check(store.TrySave(settings, out _), "Jump list settings save");
        var loaded = store.Load().Settings;
        check(loaded.JumpPaths.SequenceEqual(settings.JumpPaths), "Jump list preserves sparse slots through save and reload");
        check(File.ReadAllText(ini).Contains("&jumplist;"), "jumplist command saves canonical name");
        check(JumpListService.TryResolve(root, out var target, out _) && target == root, "Jump list accepts existing directory");
        check(JumpListService.ResolvePath(".") == Path.GetFullPath(SettingsStore.AppDirectory), "Jump list resolves relative path beside executable");
        check(JumpListService.TryResolve("%TEMP%", out _, out _), "Jump list expands environment variables");
        check(!JumpListService.TryResolve(Path.Combine(root, "missing"), out _, out var error) && error.Length > 0, "Missing jump path reports error without navigating");
        check(!JumpListService.TryResolve("", out _, out _), "Blank jump path cannot navigate");

        // 「パス,"表示名"」で一覧の表示名を指定できる。
        check(JumpListService.GetDisplayName("\"C:\\My Documents\",\"ドキュメント\"") == "ドキュメント" &&
            JumpListService.ResolvePath("\"C:\\My Documents\",\"ドキュメント\"") == @"C:\My Documents", "Quoted path with label shows the label and navigates to the path");
        check(JumpListService.GetDisplayName("C:\\Work , \"作業\"") == "作業" && JumpListService.ResolvePath("C:\\Work , \"作業\"") == @"C:\Work",
            "Unquoted path with label");
        check(JumpListService.GetDisplayName("shell:Desktop,\"机\"") == "机", "Label overrides the Windows known-folder name");
        check(JumpListService.GetDisplayName(@"C:\a,b") == @"C:\a,b" && JumpListService.ResolvePath(@"C:\a,b") == @"C:\a,b", "A comma inside a plain path is not a label separator");
        check(JumpListService.GetDisplayName("\"C:\\x\",\"\"") == @"C:\x", "Empty label falls back to the path");
        check(JumpListService.TryResolve($"\"{root}\",\"作業\"", out var labeled, out _) && labeled == root, "Labeled entry navigates");
        string labelIni = Path.Combine(root, "jump-label.ini");
        File.WriteAllText(labelIni, $"%jumpPath02 \"{root}\",\"ドキュメント\"\n%jumpPath03 \"{root}\"\n");
        var labelStore = new SettingsStore(labelIni); var labelSettings = labelStore.Load().Settings;
        check(JumpListService.GetDisplayName(labelSettings.JumpPaths[2]) == "ドキュメント" && labelSettings.JumpPaths[3] == root, "INI keeps the label and strips quotes from a plain path");
        check(labelStore.TrySave(labelSettings, out _) && labelStore.Load().Settings.JumpPaths[2] == labelSettings.JumpPaths[2], "Label survives save and reload");
    }
}
