using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MirrorFlux.Controls;
using MirrorFlux.Services.Music;
internal static class MiniSubsongRendering
{
    public static void Run()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var player = new RecordingMusicPlayer { Snapshot = new() { FormatName = "HES", IsPlaying = true, TrackIndex = 255, TrackCount = 256, CurrentTime = TimeSpan.FromSeconds(754), TotalTime = TimeSpan.FromSeconds(2756) } };
                var mini = new MiniPlayerControl(player);
                const ModifierKeys ctrl = ModifierKeys.Control;
                if (mini.HandleShortcut(Key.Space, ModifierKeys.None, false) || mini.HandleShortcut(Key.P, ModifierKeys.None, false))
                    throw new Exception("Plain filer keys were intercepted");
                mini.HandleShortcut(Key.P, ctrl, false);
                mini.HandleShortcut(Key.P, ctrl, true);
                if (!player.IsPaused) throw new Exception("Holding pause should toggle only once");
                mini.HandleShortcut(Key.P, ctrl, false);
                mini.HandleShortcut(Key.Right, ctrl, false);
                if (player.Snapshot.CurrentTime.TotalSeconds != 759) throw new Exception("Five-second seek failed");
                mini.HandleShortcut(Key.Left, ctrl, false);
                if (player.Snapshot.CurrentTime.TotalSeconds != 754) throw new Exception("Backward seek failed");
                mini.HandleShortcut(Key.Down, ctrl, false);
                if (player.Snapshot.TrackIndex != 0) throw new Exception("Next subsong should wrap");
                mini.HandleShortcut(Key.Up, ctrl, false);
                if (player.Snapshot.TrackIndex != 255) throw new Exception("Previous subsong should wrap");
                int expanded = 0;
                mini.ExpandRequested += () => expanded++;
                mini.HandleShortcut(Key.M, ctrl, false);
                mini.HandleShortcut(Key.S, ctrl, false);
                if (expanded != 1 || player.IsPlaying || mini.HandleShortcut(Key.P, ctrl, false))
                    throw new Exception("Expand, stop or inactive shortcut handling failed");
                player.Snapshot = player.Snapshot with { IsPlaying = true, CurrentTime = TimeSpan.FromSeconds(754) };
                foreach (double scale in new[] { 1.0, 1.25 })
                {
                    player.Snapshot = player.Snapshot with { TrackIndex = 255 };
                    VisualTreeHelper.SetRootDpi(mini, new DpiScale(scale, scale)); mini.RenderSnapshot(player.Snapshot);
                    mini.Measure(new Size(323, 120)); mini.Arrange(new Rect(0, 0, 323, 120)); mini.UpdateLayout();
                    var digits = (MusicSevenSegmentDisplay)mini.FindName("SubsongText");
                    var panel = (FrameworkElement)mini.FindName("SubsongPanel");
                    var time = (FrameworkElement)mini.FindName("SevenSegmentTime");
                    var trackButtons = (FrameworkElement)mini.FindName("TrackButtonsPanel");
                    var pause = (FrameworkElement)mini.FindName("PauseButton");
                    double edge = time.TranslatePoint(new Point(time.ActualWidth, 0), mini).X;
                    if (digits.Text != "100/100" || digits.DigitHeight != ((MusicSevenSegmentDisplay)time).DigitHeight || digits.DigitWidth != ((MusicSevenSegmentDisplay)time).DigitWidth || edge > panel.TranslatePoint(new Point(), mini).X + .5) throw new Exception($"Subsong digits collide with time or shrink: text={digits.Text}, edge={edge}, panel={panel.TranslatePoint(new Point(), mini).X}, h={digits.DigitHeight}/{((MusicSevenSegmentDisplay)time).DigitHeight}, w={digits.DigitWidth}/{((MusicSevenSegmentDisplay)time).DigitWidth}");
                    double timeX = time.TranslatePoint(new Point(), mini).X;
                    double digitsX = digits.TranslatePoint(new Point(), mini).X;
                    if (trackButtons.Visibility != Visibility.Visible || Math.Abs(trackButtons.TranslatePoint(new Point(trackButtons.ActualWidth, 0), mini).X - pause.TranslatePoint(new Point(), mini).X) > .5) throw new Exception("Track buttons are not attached to the right controls");
                    var next = (Button)mini.FindName("NextTrackButton"); next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    if (digits.Text != "001/100") throw new Exception($"Seven-segment song selection does not wrap: {digits.Text}");
                    player.Snapshot = player.Snapshot with { IsPaused = true };
                    mini.RenderSnapshot(player.Snapshot); mini.Measure(new Size(323, 120)); mini.Arrange(new Rect(0, 0, 323, 120)); mini.UpdateLayout();
                    if (Math.Abs(time.TranslatePoint(new Point(), mini).X - timeX) > .5 || Math.Abs(digits.TranslatePoint(new Point(), mini).X - digitsX) > .5) throw new Exception("Pause shifts the mini header layout");
                    mini.RenderSnapshot(player.Snapshot with { TrackIndex = 255 });
                    var bitmap = new RenderTargetBitmap((int)Math.Ceiling(323 * scale), (int)Math.Ceiling(120 * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32); bitmap.Render(mini);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var output = File.Create(Path.Combine(Environment.CurrentDirectory, ".dotnet", $"mini-subsong-{(int)(96*scale)}.png")); encoder.Save(output);
                }
                Console.WriteLine("PASS HES/NSF 3-digit seven-segment subsongs: selection and non-overlap at 100/125 percent");
            }
            catch (Exception ex) { failure = ex; }
        }); thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join(); if (failure != null) throw failure;
    }
    private sealed class RecordingMusicPlayer : IMusicPlaybackService
    {
        public int PlayCalls { get; private set; }
        public int SelectedTrack { get; private set; } = -1;
        public MusicPlaybackSnapshot Snapshot { get; set; } = new() { IsPlaying = true };
        public string? CurrentFilePath => null;
        public bool IsPlaying => Snapshot.IsPlaying;
        public bool IsPaused => Snapshot.IsPaused;
        public event Action? PlaybackStarted { add { } remove { } }
        public event Action? PlaybackStopped { add { } remove { } }
        public event Action? PlaybackPaused { add { } remove { } }
        public event Action? PlaybackResumed { add { } remove { } }
        public bool CanPlay(string filePath) => true;
        public bool Play(string filePath) { PlayCalls++; return true; }
        public bool SelectTrack(int index)
        {
            if (index < 0 || index >= Snapshot.TrackCount) return false;
            SelectedTrack = index;
            Snapshot = Snapshot with { TrackIndex = index, CurrentTime = TimeSpan.Zero };
            return true;
        }
        public void TogglePause() => Snapshot = Snapshot with { IsPaused = !Snapshot.IsPaused };
        public void Stop() => Snapshot = Snapshot with { IsPlaying = false };
        public bool Seek(TimeSpan position) { Snapshot = Snapshot with { CurrentTime = position }; return true; }
        public MusicPlaybackSnapshot GetSnapshot() => Snapshot;
    }
}
