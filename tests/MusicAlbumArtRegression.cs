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

internal static class MusicAlbumArtRegression
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
                try { await VerifyAsync(); }
                catch (Exception ex) { failure = ex; }
                finally { frame.Continue = false; }
            });
            Dispatcher.PushFrame(frame);
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        if (failure != null) throw failure;
    }

    private static async Task VerifyAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "mf-album-art-" + Guid.NewGuid().ToString("N"));
        var player = new Player();
        var viewer = new MusicPlayerControl(player);
        try
        {
            string a = Path.Combine(root, "a"), b = Path.Combine(root, "b"), empty = Path.Combine(root, "empty");
            foreach (string dir in new[] { a, b, empty })
            {
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "track.vgm"), "test");
            }
            WriteCover(a, Colors.Red);
            WriteCover(b, Colors.Blue);
            viewer.Load(Path.Combine(a, "track.vgm"));
            viewer.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            await CheckAsync("playing track cover", Colors.Red);
            viewer.NavigateToDirectory(b);
            Check("browsing another album keeps playing cover", Colors.Red);
            viewer.HandleKey(System.Windows.Input.Key.Back, System.Windows.Input.ModifierKeys.None);
            Check("parent navigation keeps playing cover", Colors.Red);
            viewer.NavigateToDirectory(empty);
            await CheckAsync("browsing a folder without art keeps playing cover", Colors.Red);
            player.TogglePause();
            viewer.NavigateToDirectory(b);
            await CheckAsync("paused track retains cover", Colors.Red);
            player.Stop(); // Keep CurrentFilePath, as a decoder may do after EOF.
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check("stop event switches to browsed folder", Colors.Blue);
            viewer.NavigateToDirectory(a);
            await CheckAsync("stopped navigation follows folder", Colors.Red);
            viewer.NavigateToDirectory(empty);
            await CheckAsync("stopped navigation clears missing cover", null);
            player.Play(Path.Combine(b, "track.vgm"));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check("external playback start uses actual session track", Colors.Blue);
            player.FinishWithoutEvent();
            viewer.RenderSnapshot(player.GetSnapshot());
            Check("natural EOF without event returns to browsed folder", null);
            viewer.NavigateToDirectory(a);
            var list = (ListBox)viewer.FindName("TrackListBox");
            list.SelectedIndex = list.Items.Count - 1;
            await viewer.PlaySelectedTrackAsync();
            await CheckAsync("playing selected track updates cover", Colors.Red);
            viewer.NavigateToDirectory(empty);
            list.SelectedIndex = list.Items.Count - 1;
            await viewer.PlaySelectedTrackAsync();
            await CheckAsync("playing a new track without cover clears old art", null);

            string zipPath = Path.Combine(root, "album.zip");
            ZipFile.CreateFromDirectory(a, zipPath);
            string extracted = (await ArchiveService.ExtractEntryToTempFileAsync(Path.Combine(zipPath, "track.vgm")))!;
            player.Play(extracted);
            await CheckAsync("archive playback resolves original cover", Colors.Red);
            viewer.NavigateToDirectory(b);
            await CheckAsync("archive playback keeps cover while browsing", Colors.Red);
            player.Stop();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            viewer.NavigateToDirectory(zipPath);
            Task stale = viewer.UpdateAlbumArtAsync();
            viewer.NavigateToDirectory(empty);
            await viewer.UpdateAlbumArtAsync();
            await stale;
            Check("older archive request cannot overwrite latest folder", null);
            viewer.NavigateToDirectory(zipPath);
            Task closing = viewer.UpdateAlbumArtAsync();
            viewer.Close();
            await closing;
            Check("pending load cannot restore art after close", null);
        }
        finally
        {
            viewer.Close();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }

        async Task CheckAsync(string name, Color? expected)
        {
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await viewer.UpdateAlbumArtAsync();
            Check(name, expected);
        }
        void Check(string name, Color? expected)
        {
            var source = ((Image)viewer.FindName("AlbumArtImage")).Source as BitmapSource;
            if (expected == null)
            {
                if (source != null) throw new Exception(name + ": unexpected artwork");
            }
            else
            {
                if (source == null) throw new Exception(name + ": missing artwork");
                var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
                byte[] pixel = new byte[4];
                converted.CopyPixels(new Int32Rect(0, 0, 1, 1), pixel, 4, 0);
                if (pixel[0] != expected.Value.B || pixel[1] != expected.Value.G || pixel[2] != expected.Value.R)
                    throw new Exception(name + ": wrong artwork");
            }
            Console.WriteLine("PASS " + name);
        }
    }

    private static void WriteCover(string directory, Color color)
    {
        var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null,
            new byte[] { color.B, color.G, color.R, 255 }, 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(directory, "cover.png"));
        encoder.Save(stream);
    }

    private sealed class Player : IMusicPlaybackService
    {
        public string? CurrentFilePath { get; private set; }
        public bool IsPlaying { get; private set; }
        public bool IsPaused { get; private set; }
        public event Action? PlaybackStarted;
        public event Action? PlaybackStopped;
        public event Action? PlaybackPaused;
        public event Action? PlaybackResumed;
        public bool CanPlay(string path) => Path.GetExtension(path) == ".vgm";
        public bool Play(string path)
        {
            CurrentFilePath = path; IsPlaying = true; IsPaused = false;
            PlaybackStarted?.Invoke(); return true;
        }
        public void TogglePause()
        {
            IsPaused = !IsPaused; IsPlaying = !IsPaused;
            if (IsPaused) PlaybackPaused?.Invoke(); else PlaybackResumed?.Invoke();
        }
        public void Stop() { FinishWithoutEvent(); PlaybackStopped?.Invoke(); }
        public void FinishWithoutEvent() { IsPlaying = false; IsPaused = false; }
        public MusicPlaybackSnapshot GetSnapshot() => new() { IsPlaying = IsPlaying, IsPaused = IsPaused };
    }
}
