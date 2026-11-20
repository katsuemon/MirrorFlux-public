using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using MirrorFlux.Controls;
using MirrorFlux.Services.Music;

internal static class MusicPlayAllRegression
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
        thread.Start();
        thread.Join();
        if (failure != null) throw failure;
    }

    private static async Task VerifyAsync()
    {
        string folder = Path.Combine(Path.GetTempPath(), "mf-playall-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string first = Path.Combine(folder, "01.vgm"), second = Path.Combine(folder, "02.vgm");
            File.WriteAllText(first, "first"); File.WriteAllText(second, "second");
            var player = new Player();
            var viewer = new MusicPlayerControl(player);
            try
            {
                if (!viewer.Load(first)) throw new Exception("Cannot load PLAY ALL fixture");
                viewer.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                var transport = (MusicTransportDisplay)viewer.FindName("TransportDisplay");
                transport.TargetLoops = 0;
                transport.IsPlayAllEnabled = true;
                if (transport.TargetLoops != 2)
                    throw new Exception("Enabling PLAY ALL with infinite looping must select 2 LOOP");
                transport.TargetLoops = 0;
                if (transport.TargetLoops != 0)
                    throw new Exception("Manual loop selection must remain available while PLAY ALL is on");
                viewer.ExecuteFunctionKey(5); // PLAY ALL off
                transport.TargetLoops = 1;
                viewer.ExecuteFunctionKey(5); // PLAY ALL on with an explicit loop target
                if (!transport.IsPlayAllEnabled || transport.TargetLoops != 1)
                    throw new Exception("F5 must retain an explicit 1 LOOP target when enabling PLAY ALL");
                player.EndWithoutEvent();
                viewer.RenderSnapshot(player.GetSnapshot());
                if (player.CurrentFilePath != first || player.PlayCalls != 1)
                    throw new Exception("PLAY ALL must keep a silent gap after non-looping EOF");
                await Task.Delay(850);
                if (player.PlayCalls != 1)
                    throw new Exception("PLAY ALL gap ended before one second");
                for (int attempt = 0; attempt < 40 && player.PlayCalls == 1; attempt++)
                    await Task.Delay(50);
                if (player.CurrentFilePath != second || player.PlayCalls != 2)
                    throw new Exception("PLAY ALL must advance once after the one-second EOF gap");
                viewer.RenderSnapshot(player.GetSnapshot());
                if (player.PlayCalls != 2)
                    throw new Exception("PLAY ALL must advance only once per completed track");
                player.EndWithoutEvent();
                viewer.RenderSnapshot(player.GetSnapshot());
                viewer.HandleKey(Key.F8, ModifierKeys.None);
                await Task.Delay(1150);
                if (player.PlayCalls != 2)
                    throw new Exception("Explicit stop must cancel the pending PLAY ALL transition");
                await viewer.PlaySelectedTrackAsync();
                transport.IsRepeatEnabled = true;
                player.EndWithoutEvent();
                viewer.RenderSnapshot(player.GetSnapshot());
                await Task.Delay(850);
                if (player.PlayCalls != 3)
                    throw new Exception("REPEAT must keep the one-second gap");
                for (int attempt = 0; attempt < 40 && player.PlayCalls == 3; attempt++)
                    await Task.Delay(50);
                if (player.PlayCalls != 4 || player.CurrentFilePath != second)
                    throw new Exception("REPEAT must restart the same non-looping track after the gap");
                Console.WriteLine("PASS PLAY ALL and REPEAT advance non-looping EOF after one second, once, and respect stop");

                // ループ回数を指定していれば、「全曲」がオフでもループしない曲の後に次の曲へ進む。
                transport.IsRepeatEnabled = false;
                viewer.ExecuteFunctionKey(5); // PLAY ALL off
                transport.TargetLoops = 2;
                if (transport.IsPlayAllEnabled || transport.TargetLoops != 2)
                    throw new Exception("Fixture must have PLAY ALL off with 2 LOOP");
                player.EndWithoutEvent();
                viewer.RenderSnapshot(player.GetSnapshot());
                for (int attempt = 0; attempt < 60 && player.PlayCalls == 4; attempt++)
                    await Task.Delay(50);
                if (player.PlayCalls != 5 || player.CurrentFilePath != first)
                    throw new Exception("A loop target must continue to the next track after non-looping EOF");
                // ∞（ループ回数の指定なし）で「全曲」もオフなら、従来どおりその曲で止まる。
                transport.TargetLoops = 0;
                player.EndWithoutEvent();
                viewer.RenderSnapshot(player.GetSnapshot());
                await Task.Delay(1400);
                if (player.PlayCalls != 5)
                    throw new Exception("Infinite looping without PLAY ALL must stop after non-looping EOF");
                Console.WriteLine("PASS A loop target continues after non-looping tracks; infinite looping without PLAY ALL stops");
            }
            finally { viewer.Close(); }
        }
        finally { Directory.Delete(folder, true); }
    }

    private sealed class Player : IMusicPlaybackService
    {
        private bool _ended;
        public int PlayCalls { get; private set; }
        public string? CurrentFilePath { get; private set; }
        public bool IsPlaying => CurrentFilePath != null && !_ended;
        public bool IsPaused => false;
        public event Action? PlaybackStarted { add { } remove { } }
        public event Action? PlaybackStopped { add { } remove { } }
        public event Action? PlaybackPaused { add { } remove { } }
        public event Action? PlaybackResumed { add { } remove { } }
        public bool CanPlay(string path) => Path.GetExtension(path).Equals(".vgm", StringComparison.OrdinalIgnoreCase);
        public bool Play(string path) { PlayCalls++; CurrentFilePath = path; _ended = false; return File.Exists(path); }
        public void EndWithoutEvent() => _ended = true;
        public void TogglePause() { }
        public void Stop() { CurrentFilePath = null; _ended = false; }
        public MusicPlaybackSnapshot GetSnapshot() => new() { IsPlaying = IsPlaying };
    }
}
