using System.Diagnostics;
using System.IO;
using MirrorFlux.Models;
using MirrorFlux.Services.Hardware;
using MirrorFlux.Services.Music;
using MirrorFlux.Services.Vgm;
using NAudio;
using NAudio.Wave;

/// <summary>
/// 音声出力デバイスがないとき: VGMは実機（NDSIF）へ出力する音源があれば無音の再生器で再生を始め、
/// 実機もなければ「音声出力デバイスがありません」で失敗する。実機を持たない形式は開く前に断る。
/// </summary>
internal static class NoAudioDeviceRegression
{
    private sealed class CountingProvider(int limitBytes = int.MaxValue) : IWaveProvider
    {
        public long Bytes;
        public WaveFormat WaveFormat { get; } = new(44100, 16, 2);
        public int Read(Span<byte> buffer)
        {
            int n = (int)Math.Min(buffer.Length, limitBytes - Interlocked.Read(ref Bytes));
            if (n <= 0) return 0;
            Interlocked.Add(ref Bytes, n);
            return n;
        }
    }

    private sealed class NoDeviceOutput : IWavePlayer
    {
        public static int Created;
        public NoDeviceOutput() => Interlocked.Increment(ref Created);
        public event EventHandler<StoppedEventArgs>? PlaybackStopped { add { } remove { } }
        public float Volume { get; set; }
        public PlaybackState PlaybackState => PlaybackState.Stopped;
        public WaveFormat OutputWaveFormat => new(44100, 16, 2);
        public void Init(IWaveProvider waveProvider) => throw new MmException(MmResult.BadDeviceId, "waveOutOpen");
        public void Play() { } public void Pause() { } public void Stop() { } public void Dispose() { }
    }

    private sealed class StubBackend : IMusicPlaybackBackend
    {
        public bool PlayCalled;
        public string FormatName => "STUB";
        public IReadOnlyCollection<string> SupportedExtensions { get; } = [".stubaudio"];
        public string? CurrentFilePath => null;
        public bool IsPlaying => false;
        public bool IsPaused => false;
        public event Action? PlaybackStarted { add { } remove { } }
        public event Action? PlaybackStopped { add { } remove { } }
        public event Action? PlaybackPaused { add { } remove { } }
        public event Action? PlaybackResumed { add { } remove { } }
        public bool Play(string filePath) { PlayCalled = true; return true; }
        public void TogglePause() { } public void Stop() { }
        public MusicPlaybackSnapshot GetSnapshot() => new();
        public void Dispose() { }
    }

    public static void Run(Action<bool, string> check)
    {
        // 無音の再生器は実時間で読み進め、少しだけ先行する。一時停止中は読まず、曲の終わりで止まる。
        var source = new CountingProvider();
        using (var silent = new SilentWavePlayer())
        {
            silent.Init(source);
            var sw = Stopwatch.StartNew();
            silent.Play();
            Thread.Sleep(400);
            long read = Interlocked.Read(ref source.Bytes);
            double expected = sw.Elapsed.TotalSeconds * 176400;
            check(silent.PlaybackState == PlaybackState.Playing && read > expected * 0.6 &&
                  read < expected + 176400 * (SilentWavePlayer.LeadMilliseconds + 60) / 1000.0,
                $"Silent player reads in real time with a small lead ({read} bytes in {sw.ElapsedMilliseconds} ms)");
            check(silent.GetPosition() <= read && silent.GetPosition() > 0, "Silent player position follows elapsed time and never passes what was read");
            silent.Pause();
            Thread.Sleep(50);
            long paused = Interlocked.Read(ref source.Bytes);
            Thread.Sleep(250);
            check(Interlocked.Read(ref source.Bytes) == paused && silent.PlaybackState == PlaybackState.Paused, "Silent player does not read while paused");
            silent.Play();
            Thread.Sleep(150);
            check(Interlocked.Read(ref source.Bytes) > paused, "Silent player resumes reading");
            silent.Stop();
            check(silent.PlaybackState == PlaybackState.Stopped, "Silent player stops");
        }
        var finite = new CountingProvider(17640);
        using (var silent = new SilentWavePlayer())
        {
            bool stopped = false;
            silent.PlaybackStopped += (_, e) => stopped = e.Exception == null;
            silent.Init(finite);
            silent.Play();
            for (int i = 0; i < 100 && !stopped; i++) Thread.Sleep(20);
            check(stopped && silent.PlaybackState == PlaybackState.Stopped, "Silent player reports the end of the stream");
        }

        // VGM: デバイスがなく実機もなければ、理由付きで失敗する。
        byte[] sn = SnVgm();
        string path = Path.Combine(Path.GetTempPath(), $"mf-noaudio-{Guid.NewGuid():N}.vgm");
        File.WriteAllBytes(path, sn);
        var player = VgmPlayerService.Instance;
        var oldOutput = player.CreateOutput;
        var oldProvider = player.CreateProvider;
        var oldCount = AudioOutput.DeviceCount;
        try
        {
            AudioOutput.DeviceCount = () => 0;
            player.CreateOutput = () => new NoDeviceOutput();
            player.CreateProvider = file => VgmWaveProviderFactory.Create(file, new AppSettings());
            check(!player.Play(path) && player.LastError == AudioOutput.NoDeviceMessage && !player.IsPlaying,
                "VGM without an audio device or NDSIF fails with the no-device message");

            // 実機（ND6）へ出力する音源があれば、無音の再生器で再生を始める。
            NdsifService.Instance.AttachStreamForTesting(new MemoryStream());
            player.CreateProvider = file => VgmWaveProviderFactory.Create(file, new AppSettings { Nd6Ndsif = true });
            bool started = player.Play(path);
            check(started && player.LastError == null && player.IsHardwareOnly && player.IsPlaying,
                "VGM without an audio device starts on NDSIF hardware only");
            var snapshot = player.GetSnapshot();
            Thread.Sleep(300);
            check(player.GetSnapshot().CurrentTime > snapshot.CurrentTime || player.GetSnapshot().CurrentTime > TimeSpan.Zero,
                "Hardware-only VGM playback advances in real time");
            player.Pause();
            check(player.IsPaused, "Hardware-only VGM playback pauses");
            player.Stop();
            check(!player.IsPlaying && !player.IsHardwareOnly, "Hardware-only VGM playback stops");

            // 実機を持たない形式は、デバイスがなければ開く前に断る。
            var stub = new StubBackend();
            var service = new MusicPlaybackService([stub]);
            check(!service.Play("x.stubaudio") && !stub.PlayCalled && service.LastError == AudioOutput.NoDeviceMessage,
                "Formats without hardware output report the no-device message without opening");
            AudioOutput.DeviceCount = () => 1;
            check(service.Play("x.stubaudio") && stub.PlayCalled && service.LastError == null,
                "Formats play normally when an audio device exists");
        }
        finally
        {
            player.Stop();
            player.CreateOutput = oldOutput;
            player.CreateProvider = oldProvider;
            AudioOutput.DeviceCount = oldCount;
            NdsifService.Instance.Disconnect();
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    // SN76489だけの1秒の曲（ND6の対象音源）。
    private static byte[] SnVgm()
    {
        var v = new byte[0x100];
        "Vgm "u8.ToArray().CopyTo(v, 0);
        BitConverter.GetBytes(0x171u).CopyTo(v, 8);
        BitConverter.GetBytes(3579545u).CopyTo(v, 0x0c);
        BitConverter.GetBytes((uint)(0x100 - 0x34)).CopyTo(v, 0x34);
        BitConverter.GetBytes(44100u).CopyTo(v, 0x18);
        byte[] body = [0x50, 0x9f, 0x61, 0x44, 0xac, 0x66];
        byte[] all = [.. v, .. body];
        BitConverter.GetBytes((uint)(all.Length - 4)).CopyTo(all, 4);
        return all;
    }
}
