using System.IO;
using MirrorFlux.Services.Vgm;
using MirrorFlux.Models;

namespace MirrorFlux.Services.Music;

/// <summary>
/// いま使っている再生エンジンを持つ。
/// サウンドバーとサウンドビューアは同じ再生を途切れさせずに共有する。
/// </summary>
public sealed class MusicPlaybackService : IMusicPlaybackService, IDisposable
{
    private static readonly Lazy<MusicPlaybackService> Shared = new(() => new([new VgmPlaybackBackend(), new GmePlaybackBackend(), new MdxPlaybackBackend(), new AudioFilePlaybackBackend()], new SettingsStore().Load().Settings.MasterVolumePercent));
    public static MusicPlaybackService Instance => Shared.Value;
    private readonly List<IMusicPlaybackBackend> _backends = [];
    private IMusicPlaybackBackend? _active;
    private bool _disposed;
    private readonly float _masterGain;
    public MusicPlaybackService(IEnumerable<IMusicPlaybackBackend> backends, int masterVolumePercent = 50)
    {
        _masterGain = Math.Clamp(masterVolumePercent, 0, 100) / 100f;
        foreach (var backend in backends) RegisterBackend(backend);
    }

    public string? CurrentFilePath => _active?.CurrentFilePath;
    public bool IsPlaying => _active?.IsPlaying ?? false;
    public bool IsPaused => _active?.IsPaused ?? false;
    public bool CanSeek => _active?.CanSeek ?? false;
    public event Action? PlaybackStarted;
    public event Action? PlaybackStopped;
    public event Action? PlaybackPaused;
    public event Action? PlaybackResumed;

    /// <summary>
    /// 再生エンジンを登録する。
    /// 同じ拡張子を扱うエンジンは重ねて登録できない。
    /// </summary>
    public void RegisterBackend(IMusicPlaybackBackend backend)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(backend);
        if (_backends.Any(b => b.SupportedExtensions.Intersect(backend.SupportedExtensions, StringComparer.OrdinalIgnoreCase).Any()))
            throw new ArgumentException("A decoder is already registered for this extension.", nameof(backend));
        _backends.Add(backend);
        // 再生中のエンジンからの通知だけを外へ伝える。
        backend.PlaybackStarted += () => { if (ReferenceEquals(_active, backend)) PlaybackStarted?.Invoke(); };
        backend.PlaybackStopped += () => { if (ReferenceEquals(_active, backend)) PlaybackStopped?.Invoke(); };
        backend.PlaybackPaused += () => { if (ReferenceEquals(_active, backend)) PlaybackPaused?.Invoke(); };
        backend.PlaybackResumed += () => { if (ReferenceEquals(_active, backend)) PlaybackResumed?.Invoke(); };
    }

    private IMusicPlaybackBackend? FindBackend(string path) => _backends.FirstOrDefault(b =>
        b.SupportedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase));
    public bool CanPlay(string filePath) => !_disposed && FindBackend(filePath) != null;
    public bool CanOpenFullViewer(string filePath) => !_disposed && FindBackend(filePath) is { SupportsFullViewer: true };
    /// <summary>
    /// 直前の Play が失敗した理由（例:「音声出力デバイスがありません」）。
    /// 成功時・理由不明なら null。
    /// </summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// 拡張子に合うエンジンで再生を始める。
    /// 別のエンジンが鳴っていれば止めてから切り替える。
    /// </summary>
    public bool Play(string filePath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        LastError = null;
        var backend = FindBackend(filePath);
        if (backend == null) return false;
        // 実機出力を持たない形式は、音声出力デバイスがなければ開く前に断る。
        if (!backend.CanPlayWithoutAudioDevice && !AudioOutput.HasDevice)
        {
            LastError = AudioOutput.NoDeviceMessage;
            return false;
        }
        // エンジンを切り替え、全体音量を設定して再生する。失敗したら理由を残す。
        if (!ReferenceEquals(_active, backend)) _active?.Stop();
        _active = backend;
        backend.SetOutputGain(_masterGain);
        bool started = backend.Play(filePath);
        if (!started)
            LastError = backend.LastError ?? (!backend.CanPlayWithoutAudioDevice && !AudioOutput.HasDevice ? AudioOutput.NoDeviceMessage : null);
        return started;
    }
    public void SetFadeGain(float gain) => _active?.SetOutputGain(_masterGain * Math.Clamp(gain, 0f, 1f));
    public void TogglePause() => _active?.TogglePause();
    public bool SetChannelMuted(MusicChannelSnapshot channel, bool muted) => _active?.SetChannelMuted(channel, muted) ?? false;
    /// <summary>
    /// トラックを切り替える。
    /// フェード中だった音量は元に戻す。
    /// </summary>
    public bool SelectTrack(int index)
    {
        _active?.SetOutputGain(_masterGain);
        return _active?.SelectTrack(index) ?? false;
    }
    public bool Seek(TimeSpan position) => _active?.Seek(position) ?? false;
    public void Stop() => _active?.Stop();
    public MusicPlaybackSnapshot GetSnapshot() => _active?.GetSnapshot() ?? new();
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var backend in _backends) backend.Dispose();
        _active = null;
    }
}
