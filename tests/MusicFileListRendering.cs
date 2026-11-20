using System.IO;
using System.IO.Compression;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MirrorFlux.Controls;
using MirrorFlux.Services;
using MirrorFlux.Services.Music;
using MirrorFlux.Services.Vgm;
using MirrorFlux.ViewModels;

internal static class MusicFileListRendering
{
    public static void Run()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            var frame = new DispatcherFrame();
            dispatcher.BeginInvoke(async () =>
            {
                try
                {
                    string settingsPath = Path.Combine(SettingsStore.AppDirectory, SettingsStore.SettingsFileName);
                    byte[]? savedSettings = File.Exists(settingsPath) ? File.ReadAllBytes(settingsPath) : null;
                    try
                    {
                        foreach (int logRows in new[] { 4, 6 })
                        {
                            File.WriteAllText(settingsPath, $"[settings]\n%logRows {logRows}\n");
                            await VerifyAsync(logRows);
                        }
                    }
                    finally
                    {
                        if (savedSettings != null) File.WriteAllBytes(settingsPath, savedSettings);
                        else File.Delete(settingsPath);
                    }
                }
                catch (Exception ex) { failure = ex; }
                finally { frame.Continue = false; }
            });
            Dispatcher.PushFrame(frame);
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        if (failure != null) throw failure;
    }

    private static async Task VerifyAsync(int logRows)
    {
        string root = Path.Combine(Path.GetTempPath(), "mf-track-list-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string archivePath = Path.Combine(root, "songs.zip");
            using (var zip = ZipFile.Open(archivePath, ZipArchiveMode.Create))
                foreach (var name in new[] { "a/same.vgm", "b/same.vgm", "note.txt" })
                {
                    using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                    writer.Write(name);
                }
            string source = Path.Combine(archivePath, "a", "same.vgm");
            string temp = (await ArchiveService.ExtractEntryToTempFileAsync(source))!;
            var player = new Player();
            var viewer = new MusicPlayerControl(player);
            viewer.Resources["PrimaryText"] = Brushes.LightGray;
            viewer.Resources["MutedText"] = Brushes.SlateGray;
            viewer.Resources["AccentText"] = Brushes.LightBlue;
            if (!viewer.Load(temp)) throw new Exception("Cannot load extracted track");
            var list = (ListBox)viewer.FindName("TrackListBox");
            if (list.Items.Count != 2 || list.SelectedIndex != 1 || !list.Items[0].ToString()!.Contains(".."))
                throw new Exception("Archive list lost source, nested entries or selection");
            string playingBeforeNavigation = player.CurrentFilePath!;
            viewer.HandleKey(System.Windows.Input.Key.Back, System.Windows.Input.ModifierKeys.None);
            if (list.Items.Count != 3 || !list.SelectedItem!.ToString()!.EndsWith("  a") || player.CurrentFilePath != playingBeforeNavigation)
                throw new Exception("Archive parent navigation must select the folder and preserve playback");
            list.SelectedIndex = 2; // b
            await viewer.PlaySelectedTrackAsync();
            list.SelectedIndex = 1; // same.vgm
            await viewer.PlaySelectedTrackAsync();
            if (File.ReadAllText(player.CurrentFilePath!) != "b/same.vgm" || viewer.FilePath != Path.Combine(archivePath, "b", "same.vgm"))
                throw new Exception("Selected archive entry was not extracted and played");
            int playsBeforeReopen = player.PlayCalls;
            var reopened = new MusicPlayerControl(player);
            if (!reopened.Load(player.CurrentFilePath!) || ((ListBox)reopened.FindName("TrackListBox")).Items.Count != 2)
                throw new Exception("Reopening from mini lost archive context");
            if (player.PlayCalls != playsBeforeReopen)
                throw new Exception("Reopening full viewer restarted music playback instead of continuing it");
            string secondTemp = (await ArchiveService.ExtractEntryToTempFileAsync(Path.Combine(archivePath, "b", "same.vgm")))!;
            var reopenedSecondTemp = new MusicPlayerControl(player);
            if (!reopenedSecondTemp.Load(secondTemp) || player.PlayCalls != playsBeforeReopen)
                throw new Exception("Reopening full viewer with alternative temp extraction must preserve current playback");
            reopenedSecondTemp.Close();
            list.SelectedIndex = 0;
            await viewer.PlaySelectedTrackAsync(); // .. to archive root
            if (!list.SelectedItem!.ToString()!.EndsWith("  b")) throw new Exception("Parent entry did not select exited archive folder");
            viewer.HandleKey(System.Windows.Input.Key.Up, System.Windows.Input.ModifierKeys.Alt);
            if (!list.SelectedItem!.ToString()!.EndsWith("songs.zip")) throw new Exception("Leaving archive must select the archive file");
            await viewer.PlaySelectedTrackAsync(); // re-enter archive
            if (list.Items.Count != 3) throw new Exception("Cannot reopen archive from full viewer");
            viewer.Measure(new Size(1200, 720)); viewer.Arrange(new Rect(0, 0, 1200, 720)); viewer.UpdateLayout();
            if (list.Visibility != Visibility.Visible || list.ActualHeight < 60 || list.TranslatePoint(new Point(), viewer).Y < 500)
                throw new Exception($"File selector layout: visible={list.IsVisible}, height={list.ActualHeight}, y={list.TranslatePoint(new Point(), viewer).Y}, viewer={viewer.ActualHeight}");
            var image = new RenderTargetBitmap(1200, 720, 96, 96, PixelFormats.Pbgra32);
            image.Render(viewer);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using (var stream = File.Create(Path.Combine(Path.GetTempPath(), "mf-track-list.png"))) encoder.Save(stream);
            viewer.Close(); reopened.Close();
            string local = Path.Combine(root, "local.vgm"); File.WriteAllText(local, "local");
            File.WriteAllText(Path.Combine(root, "second.vgz"), "second");
            for (int i = 0; i < 16; i++) File.WriteAllText(Path.Combine(root, $"track{i:D2}.vgm"), "track");
            var localViewer = new MusicPlayerControl(player);
            localViewer.Resources["PrimaryText"] = Brushes.LightGray;
            localViewer.Resources["MutedText"] = Brushes.SlateGray;
            localViewer.Resources["AccentText"] = Brushes.LightBlue;
            localViewer.Resources["HighlightText"] = Brushes.CornflowerBlue;
            localViewer.Resources["InteractionSelectionBackground"] = new SolidColorBrush(Color.FromRgb(32, 42, 72));
            if (!localViewer.Load(local) || ((ListBox)localViewer.FindName("TrackListBox")).Items.Count != 20)
                throw new Exception("Local folder selection includes unsupported files");
            var localList = (ListBox)localViewer.FindName("TrackListBox");
            int closeRequests = 0;
            localViewer.CloseRequested += (_, _) => closeRequests++;
            VerifyLayoutAndPaging(localViewer, localList, 720, 1, logRows);
            localViewer.HandleKey(System.Windows.Input.Key.Return, System.Windows.Input.ModifierKeys.None);
            if (closeRequests != 0 || player.CurrentFilePath != Path.Combine(root, "track15.vgm"))
                throw new Exception("Enter must play the selected file and keep the viewer open");
            string lastPlayed = player.CurrentFilePath!;
            localViewer.HandleKey(System.Windows.Input.Key.Back, System.Windows.Input.ModifierKeys.None);
            if (!localList.SelectedItem!.ToString()!.EndsWith(Path.GetFileName(root)) || player.CurrentFilePath != lastPlayed)
                throw new Exception("Local parent must select exited folder without changing playback");
            await localViewer.PlaySelectedTrackAsync();
            if (localList.Items.Count != 20) throw new Exception("Cannot re-enter local music folder");
            foreach (double scale in new[] { 1.0, 1.25, 1.5, 2.0 })
            foreach (double height in new[] { 720.0, 665.0, 760.0, 720.0 })
                VerifyLayoutAndPaging(localViewer, localList, height, scale, logRows);
            localViewer.HandleKey(System.Windows.Input.Key.Escape, System.Windows.Input.ModifierKeys.None);
            if (closeRequests != 1) throw new Exception("Escape no longer closes the viewer");
            localViewer.Close();
            await VerifyFilerContextAsync(root, player);
            if (logRows == 4) await VerifyTitlesAsync(root, player);
            Console.WriteLine($"PASS music hierarchy navigation, playback, file-list resize and paging at 100/125/150/200% DPI with logRows={logRows}");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void VerifyLayoutAndPaging(MusicPlayerControl viewer, ListBox list,
        double height, double scale, int logRows)
    {
        VisualTreeHelper.SetRootDpi(viewer, new DpiScale(scale, scale));
        viewer.InvalidateMeasure();
        viewer.Measure(new Size(1200, height));
        viewer.Arrange(new Rect(0, 0, 1200, height));
        viewer.UpdateLayout();
        list.SelectedIndex = list.Items.Count - 1;
        list.ScrollIntoView(list.SelectedItem);
        viewer.UpdateLayout();
        var scroll = FindScroll(list)!;
        double top = list.TranslatePoint(new Point(), viewer).Y;
        double bottom = top + list.ActualHeight;
        int expectedRows = (int)Math.Floor((height - top) / 18.0);
        double tolerance = 1 / scale + .01;
        if ((top < 540 - tolerance || top > 542 + tolerance) || Math.Abs(scroll.ViewportHeight - expectedRows) > .01 ||
            height - bottom >= 18 + tolerance || bottom > height + tolerance ||
            Math.Abs(list.TranslatePoint(new Point(list.ActualWidth, 0), viewer).X - 1200) > tolerance ||
            scroll.VerticalOffset != Math.Floor(scroll.VerticalOffset) || list.FontSize != 14)
            throw new Exception($"Selector leaves empty rows or clips items: logRows={logRows}, DPI={scale:P0}, height={height}, top={top}, bottom={bottom}, viewport={scroll.ViewportHeight}, expected={expectedRows}");

        // Two visible rows overlap between pages, regardless of INI defaults or window size.
        int pageRows = Math.Max(1, expectedRows - 2);
        viewer.HandleKey(System.Windows.Input.Key.PageUp, System.Windows.Input.ModifierKeys.None);
        if (list.SelectedIndex != Math.Max(0, list.Items.Count - 1 - pageRows))
            throw new Exception($"PageUp must follow the visible viewport ({expectedRows} rows)");
        viewer.HandleKey(System.Windows.Input.Key.PageDown, System.Windows.Input.ModifierKeys.None);
        viewer.HandleKey(System.Windows.Input.Key.PageDown, System.Windows.Input.ModifierKeys.None);
        if (list.SelectedIndex != list.Items.Count - 1) throw new Exception("PageDown must clamp at the last file");
        list.SelectedIndex = 0;
        viewer.HandleKey(System.Windows.Input.Key.PageUp, System.Windows.Input.ModifierKeys.None);
        if (list.SelectedIndex != 0) throw new Exception("PageUp must clamp at the first file");
        list.SelectedIndex = list.Items.Count - 1;
        list.ScrollIntoView(list.SelectedItem);
        viewer.UpdateLayout();
        if (height == 720)
        {
            var image = new RenderTargetBitmap((int)(1200 * scale), (int)(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            image.Render(viewer);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using var output = File.Create(Path.Combine(Path.GetTempPath(), $"mf-selector-log{logRows}-{scale * 100:0}.png"));
            encoder.Save(output);
        }
    }

    private static async Task VerifyFilerContextAsync(string root, Player player)
    {
        string folder = Directory.CreateDirectory(Path.Combine(root, "selector")).FullName;
        string archive = Path.Combine(folder, "03.zip");
        string[] names = ["track10.vgm", "track2.vgm", "first.vgz"];
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            foreach (string name in names)
            {
                File.WriteAllText(Path.Combine(folder, name), name);
                using var writer = new StreamWriter(zip.CreateEntry("nested/" + name).Open());
                writer.Write(name);
            }
        }
        Directory.CreateDirectory(Path.Combine(folder, "track5"));
        File.WriteAllText(Path.Combine(folder, "hidden-from-player.txt"), "text");
        string playing = player.CurrentFilePath!;
        int playCalls = player.PlayCalls;
        var pane = new FilePaneViewModel("test", folder);
        foreach (var mode in Enum.GetValues<PaneSortMode>())
        foreach (bool ascending in new[] { true, false })
        {
            pane.SetSort(mode, ascending, directoriesFirst: ascending);
            await pane.LoadAsync(folder, "track2.vgm");
            var viewer = new MusicPlayerControl(player, pane);
            if (!viewer.Load(playing) || viewer.BrowsePath != folder || viewer.SelectedBrowsePath != pane.SelectedItem?.FullPath)
                throw new Exception("Full player must start at the selected filer track, independently of current audio");
            var list = (ListBox)viewer.FindName("TrackListBox");
            void MatchPane()
            {
                var expected = pane.Entries.Where(e => e.IsDirectory || player.CanPlay(e.FullPath) ||
                    ArchiveService.IsSupportedArchive(e.FullPath)).Select(e => e.FullPath).ToArray();
                if (list.Items.Count != expected.Length) throw new Exception("Unexpected selector entries");
                for (int i=0; i<expected.Length; i++)
                {
                    list.SelectedIndex = i;
                    if (viewer.SelectedBrowsePath != expected[i]) throw new Exception($"Selector order differs for {mode}, ascending={ascending}, row {i}");
                }
            }
            MatchPane();
            string nested = Path.Combine(archive, "nested");
            viewer.NavigateToDirectory(nested);
            await pane.LoadAsync(nested, "track10.vgm");
            MatchPane();
            viewer.Close();
            var fromArchive = new MusicPlayerControl(player, pane);
            if (!fromArchive.Load(playing) || fromArchive.BrowsePath != nested ||
                fromArchive.SelectedBrowsePath != Path.Combine(nested, "track10.vgm"))
                throw new Exception("Archive selector lost the original virtual selection");
            fromArchive.Close();
        }
        if (player.CurrentFilePath != playing || player.PlayCalls != playCalls)
            throw new Exception("Opening the selector at the filer cursor restarted playback");
        var selected = new MusicPlayerControl(player, pane);
        selected.Load(playing);
        await selected.PlaySelectedTrackAsync();
        if (player.PlayCalls != playCalls + 1 || selected.FilePath != pane.SelectedItem!.FullPath ||
            File.ReadAllText(player.CurrentFilePath!) != "track10.vgm")
            throw new Exception("Playing the filer selection must open the highlighted archive track");
        selected.Close();
        Console.WriteLine("PASS filer selection independent of playback, all four sort modes in local and archive folders");
    }

    /// <summary>GD3 の曲名（英語・日本語）を持つ最小の VGM を作る。title が null なら GD3 を付けない。</summary>
    internal static byte[] CreateVgm(string? english, string? japanese = null)
    {
        var data = new List<byte>(new byte[0x40]);
        data[0] = (byte)'V'; data[1] = (byte)'g'; data[2] = (byte)'m'; data[3] = (byte)' ';
        void Put(int at, int value) { var b = BitConverter.GetBytes(value); for (int i = 0; i < 4; i++) data[at + i] = b[i]; }
        Put(0x08, 0x150); Put(0x34, 0x40 - 0x34);
        data.AddRange(Enumerable.Repeat((byte)0x62, 3000)); data.Add(0x66);
        if (english != null)
        {
            Put(0x14, data.Count - 0x14);
            var strings = new List<byte>();
            foreach (string text in new[] { english, japanese ?? "", "", "", "", "", "", "", "", "", "" })
            { strings.AddRange(System.Text.Encoding.Unicode.GetBytes(text)); strings.Add(0); strings.Add(0); }
            data.AddRange("Gd3 "u8.ToArray()); data.AddRange(BitConverter.GetBytes(0x100)); data.AddRange(BitConverter.GetBytes(strings.Count));
            data.AddRange(strings);
        }
        Put(0x04, data.Count - 4);
        return data.ToArray();
    }

    private static byte[] Gzip(byte[] data)
    {
        using var output = new MemoryStream();
        using (var gz = new GZipStream(output, CompressionLevel.Fastest)) gz.Write(data);
        return output.ToArray();
    }

    private static byte[] CreateMdx(string title) =>
        [.. System.Text.CodePagesEncodingProvider.Instance.GetEncoding(932)!.GetBytes(title), 0x0d, 0x0a, 0x1a, 0];

    /// <summary>
    /// VGM の行は既定で GD3 の曲名を出し、T でファイル名と切り替わる。MDX はタイトルの列がそろう。アーカイブ内でも曲名を読む。
    /// </summary>
    private static async Task VerifyTitlesAsync(string root, Player player)
    {
        string folder = Directory.CreateDirectory(Path.Combine(root, "titles")).FullName;
        File.WriteAllBytes(Path.Combine(folder, "a.vgm"), CreateVgm("Song A", "曲A"));
        File.WriteAllBytes(Path.Combine(folder, "b.vgz"), Gzip(CreateVgm("Song B")));
        File.WriteAllBytes(Path.Combine(folder, "c.vgm"), CreateVgm(null));
        File.WriteAllBytes(Path.Combine(folder, "m.mdx"), CreateMdx("MDX ONE"));
        File.WriteAllBytes(Path.Combine(folder, "much-longer-name.mdx"), CreateMdx("二番目"));
        string archive = Path.Combine(folder, "z.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            using (var entry = zip.CreateEntry("in/z.vgz").Open()) entry.Write(Gzip(CreateVgm("Song Z")));
            using (var entry = zip.CreateEntry("in/y.mdx").Open()) entry.Write(CreateMdx("Archived MDX"));
        }

        bool saved = MusicPlayerControl.ShowVgmTitles;
        MusicPlayerControl.ShowVgmTitles = true;
        try
        {
            if (VgmFile.ReadGd3Title(new MemoryStream(Gzip(CreateVgm("Song B")))) != "Song B" ||
                VgmFile.ReadGd3Title(new MemoryStream(CreateVgm("Song A", "曲A"))) != "曲A" ||
                VgmFile.ReadGd3Title(new MemoryStream(CreateVgm(null))) != "")
                throw new Exception("GD3 title reader must prefer the Japanese title, read VGZ and ignore files without GD3");

            var viewer = new MusicPlayerControl(player);
            viewer.Resources["PrimaryText"] = Brushes.LightGray;
            viewer.Measure(new Size(900, 600)); viewer.Arrange(new Rect(0, 0, 900, 600)); viewer.UpdateLayout();
            if (!viewer.Load(Path.Combine(folder, "a.vgm"))) throw new Exception("Cannot load the title test track");
            var list = (ListBox)viewer.FindName("TrackListBox");
            MusicPlayerControl.TrackRow Row(string name) =>
                list.Items.Cast<MusicPlayerControl.TrackRow>().ElementAt(Enumerable.Range(0, list.Items.Count)
                    .First(i => { list.SelectedIndex = i; return Path.GetFileName(viewer.SelectedBrowsePath) == name; }));
            async Task WaitFor(Func<bool> done, string what)
            {
                for (int i = 0; i < 200 && !done(); i++) await Task.Delay(25);
                if (!done()) throw new Exception("Titles were not read: " + what + " | " + string.Join(" | ", list.Items.Cast<object>()));
            }
            await WaitFor(() => Row("a.vgm").Head.EndsWith("曲A") && Row("m.mdx").Title.Length > 0, "local folder");
            var m = Row("m.mdx"); var longer = Row("much-longer-name.mdx");
            if (!Row("b.vgz").Head.EndsWith("Song B") || !Row("c.vgm").Head.EndsWith("c.vgm") || Row("a.vgm").Title != "")
                throw new Exception("VGM rows must show the GD3 title (or the file name without one) in place of the name");
            if (m.Title != "MDX ONE" || longer.Title != "二番目" || !m.Head.EndsWith("m.mdx") || double.IsNaN(m.HeadWidth) ||
                m.HeadWidth != longer.HeadWidth || m.HeadWidth <= 0)
                throw new Exception($"MDX titles must line up in one column: {m.HeadWidth} / {longer.HeadWidth}");

            viewer.HandleKey(System.Windows.Input.Key.T, System.Windows.Input.ModifierKeys.None);
            if (MusicPlayerControl.ShowVgmTitles || !Row("a.vgm").Head.EndsWith("a.vgm") || !Row("b.vgz").Head.EndsWith("b.vgz") || Row("m.mdx").Title != "MDX ONE")
                throw new Exception("T must switch VGM rows to file names and keep MDX titles");
            viewer.HandleKey(System.Windows.Input.Key.T, System.Windows.Input.ModifierKeys.None);
            if (!Row("a.vgm").Head.EndsWith("曲A")) throw new Exception("T again must bring the GD3 titles back");

            viewer.NavigateToDirectory(Path.Combine(archive, "in"));
            await WaitFor(() => Row("z.vgz").Head.EndsWith("Song Z") && Row("y.mdx").Title == "Archived MDX", "archive");
            viewer.Close();
            Console.WriteLine("PASS VGM rows show GD3 titles (T toggles file names), MDX titles line up, also inside archives");
        }
        finally { MusicPlayerControl.ShowVgmTitles = saved; }
    }

    private static ScrollViewer? FindScroll(DependencyObject parent)
    {
        if (parent is ScrollViewer scroll) return scroll;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            if (FindScroll(VisualTreeHelper.GetChild(parent, i)) is { } found) return found;
        return null;
    }

    private sealed class Player : IMusicPlaybackService
    {
        public int PlayCalls { get; private set; }
        public string? CurrentFilePath { get; private set; }
        public bool IsPlaying => CurrentFilePath != null;
        public bool IsPaused => false;
        public event Action? PlaybackStarted { add { } remove { } }
        public event Action? PlaybackStopped { add { } remove { } }
        public event Action? PlaybackPaused { add { } remove { } }
        public event Action? PlaybackResumed { add { } remove { } }
        public bool CanPlay(string path) => Path.GetExtension(path).ToLowerInvariant() is ".vgm" or ".vgz" or ".mdx";
        public bool Play(string path) { PlayCalls++; CurrentFilePath = path; return File.Exists(path); }
        public void TogglePause() { }
        public void Stop() { }
        public MusicPlaybackSnapshot GetSnapshot() => new() { IsPlaying = IsPlaying };
    }
}
