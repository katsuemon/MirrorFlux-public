using System.Diagnostics;
using NAudio.Wave;

namespace MirrorFlux.Services.Music;

/// <summary>
/// 音声出力デバイスがないときの代わりの再生器。
/// 音は捨て、プロバイダーを実時間で読み進めるだけ。
/// VGMの実機（NDSIF）出力はプロバイダーの読み出しに合わせて送られるため、これで実機だけで再生できる。
/// 読み出しは再生位置より少しだけ先行させ、通常のWaveOutと同じく一時停止・停止に応じる。
/// </summary>
public sealed class SilentWavePlayer : IWavePlayer, IWavePosition
{
    internal const int LeadMilliseconds = 100;
    private const int ChunkMilliseconds = 10;
    private readonly object _sync = new();
    private readonly Stopwatch _clock = new();
    private IWaveProvider? _source;
    private Thread? _thread;
    private volatile bool _stopRequested;
    private long _bytesRead;
    private long _playedBytesAtPause;
    private volatile PlaybackState _state = PlaybackState.Stopped;
    private readonly ManualResetEventSlim _wake = new(false);

    public event EventHandler<StoppedEventArgs>? PlaybackStopped;
    public float Volume { get; set; } = 1f;
    public PlaybackState PlaybackState => _state;
    public WaveFormat OutputWaveFormat => _source?.WaveFormat ?? new WaveFormat(44100, 16, 2);

    /// <summary>読み出すプロバイダーを設定する。</summary>
    public void Init(IWaveProvider waveProvider)
    {
        ArgumentNullException.ThrowIfNull(waveProvider);
        lock (_sync)
        {
            if (_thread != null) throw new InvalidOperationException("Already initialized");
            _source = waveProvider;
        }
    }

    /// <summary>
    /// 再生を始める。
    /// 初回は読み出し用のスレッドを作る。
    /// </summary>
    public void Play()
    {
        lock (_sync)
        {
            if (_source == null) throw new InvalidOperationException("Init must be called first");
            if (_state == PlaybackState.Playing) return;
            _state = PlaybackState.Playing;
            _clock.Start();
            if (_thread == null)
            {
                _stopRequested = false;
                _thread = new Thread(Pump) { IsBackground = true, Name = "SilentWavePlayer", Priority = ThreadPriority.AboveNormal };
                _thread.Start();
            }
            _wake.Set();
        }
    }

    /// <summary>
    /// 一時停止する。
    /// 止めた時点の再生位置を覚える。
    /// </summary>
    public void Pause()
    {
        lock (_sync)
        {
            if (_state != PlaybackState.Playing) return;
            _state = PlaybackState.Paused;
            _clock.Stop();
            _playedBytesAtPause = PlayedBytes();
        }
    }

    /// <summary>停止し、読み出しスレッドの終了を最大1秒待つ。</summary>
    public void Stop()
    {
        Thread? thread;
        lock (_sync)
        {
            if (_state == PlaybackState.Stopped && _thread == null) return;
            _stopRequested = true;
            _state = PlaybackState.Stopped;
            thread = _thread;
            _wake.Set();
        }
        if (thread != null && thread != Thread.CurrentThread) thread.Join(1000);
    }

    /// <summary>
    /// 実時間で進んだ分（バイト）。
    /// 読み出した量を超えない。
    /// </summary>
    public long GetPosition()
    {
        lock (_sync) return _state == PlaybackState.Paused ? _playedBytesAtPause : PlayedBytes();
    }

    /// <summary>
    /// 時計から求めた再生バイト数。
    /// ブロック境界にそろえ、読み出した量で頭打ちにする。
    /// </summary>
    private long PlayedBytes()
    {
        var format = OutputWaveFormat;
        long elapsed = (long)(_clock.Elapsed.TotalSeconds * format.AverageBytesPerSecond);
        elapsed -= elapsed % format.BlockAlign;
        return Math.Min(elapsed, Interlocked.Read(ref _bytesRead));
    }

    /// <summary>
    /// 読み出しスレッド。
    /// 時計より LeadMilliseconds だけ先まで10msずつ読み、曲の終わりか停止で抜ける。
    /// </summary>
    private void Pump()
    {
        Exception? error = null;
        try
        {
            var source = _source!;
            var format = source.WaveFormat;
            int chunk = Math.Max(format.BlockAlign, format.AverageBytesPerSecond * ChunkMilliseconds / 1000 / format.BlockAlign * format.BlockAlign);
            long lead = (long)format.AverageBytesPerSecond * LeadMilliseconds / 1000;
            var buffer = new byte[chunk];
            while (!_stopRequested)
            {
                // 再生中でなければ起こされるまで待ち、先まで読めていれば少し待つ。
                if (_state != PlaybackState.Playing) { _wake.Wait(20); _wake.Reset(); continue; }
                long target = (long)(_clock.Elapsed.TotalSeconds * format.AverageBytesPerSecond) + lead;
                if (Interlocked.Read(ref _bytesRead) >= target) { Thread.Sleep(2); continue; }
                // 読めなくなったら曲の終わり。
                int read = source.Read(buffer.AsSpan());
                if (read <= 0) break;
                Interlocked.Add(ref _bytesRead, read);
            }
        }
        catch (Exception ex) { error = ex; }
        // 終わったら停止状態にし、止めた以外の理由なら停止を通知する。
        lock (_sync)
        {
            _state = PlaybackState.Stopped;
            _clock.Stop();
            _thread = null;
        }
        if (!_stopRequested || error != null) PlaybackStopped?.Invoke(this, new StoppedEventArgs(error));
    }

    public void Dispose()
    {
        Stop();
        _wake.Dispose();
    }
}
