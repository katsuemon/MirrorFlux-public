using MirrorFlux.Services.Music;

namespace MirrorFlux.Services.Vgm;

/// <summary>VGMの再生状態を共通の音楽プレイヤー画面へ渡す唯一の窓口。</summary>
public sealed class VgmPlaybackBackend : IMusicPlaybackBackend
{
    private readonly VgmPlayerService _player = VgmPlayerService.Instance;
    private MusicPlaybackSnapshot? _lastMapped;
    private long _lastSnapshotTimestamp;
    private string? _lastSnapshotPath;
    public string FormatName => "VGM";
    public IReadOnlyCollection<string> SupportedExtensions { get; } = Array.AsReadOnly(MirrorFlux.Services.Music.MusicFormats.Vgm);
    public string? CurrentFilePath => _player.CurrentVgm?.FilePath;
    public bool IsPlaying => _player.IsPlaying;
    public bool SetChannelMuted(MusicChannelSnapshot channel, bool muted) => _player.SetChannelMuted(channel, muted);
    public void SetOutputGain(float gain) => _player.SetOutputGain(gain);
    public bool IsPaused => _player.IsPaused;
    public event Action? PlaybackStarted { add => _player.PlaybackStarted += value; remove => _player.PlaybackStarted -= value; }
    public event Action? PlaybackStopped { add => _player.PlaybackStopped += value; remove => _player.PlaybackStopped -= value; }
    public event Action? PlaybackPaused { add => _player.PlaybackPaused += value; remove => _player.PlaybackPaused -= value; }
    public event Action? PlaybackResumed { add => _player.PlaybackResumed += value; remove => _player.PlaybackResumed -= value; }
    public bool Play(string filePath) => _player.Play(filePath);
    public string? LastError => _player.LastError;
    /// <summary>VGMだけは、音声出力デバイスがなくても実機（NDSIF）があれば再生できる。</summary>
    public bool CanPlayWithoutAudioDevice => true;
    public void TogglePause() => _player.TogglePause();
    public void Stop() => _player.Stop();
    /// <summary>表示用のスナップショットを返す。同じ曲・同じ状態なら1/60秒以内は前回の結果を使い回し、音高だけ更新する。</summary>
    public MusicPlaybackSnapshot GetSnapshot()
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        string? path = _player.CurrentVgm?.FilePath;
        if (_lastMapped != null && string.Equals(path, _lastSnapshotPath, StringComparison.OrdinalIgnoreCase) &&
            _lastMapped.IsPlaying == _player.IsPlaying && _lastMapped.IsPaused == _player.IsPaused &&
            now - _lastSnapshotTimestamp < System.Diagnostics.Stopwatch.Frequency / 60)
            return _player.ApplyRealtimePitches(_lastMapped);
        _lastSnapshotTimestamp = now;
        _lastSnapshotPath = path;
        // 新しいスナップショットを作り、ファイル形式と音源の表示名を付ける。
        _lastMapped = MapSnapshot(_player.GetSnapshot()) with
        {
            FormatName = _player.CurrentVgm?.FormatName ?? "VGM",
            FileTypeDisplay = _player.CurrentVgm?.FileTypeDisplay ?? "VGM",
            SoundSourceDisplay = _player.CurrentVgm?.SoundSourceDisplay ?? ""
        };
        return _player.ApplyRealtimePitches(_lastMapped);
    }
    /// <summary>VGM用のスナップショットを共通の音楽プレイヤー用に写す。配列は複製して渡す。</summary>
    public static MusicPlaybackSnapshot MapSnapshot(VgmVisualSnapshot source) => new()
    {
        IsPlaying = source.IsPlaying, IsPaused = source.IsPaused,
        FmCoreName = source.FmCoreName, ExtDeviceName = source.ExtDeviceName,
        CurrentTime = source.CurrentTime, TotalTime = source.TotalTime, LoopDuration = source.LoopDuration, LoopCount = source.LoopCount,
        Title = source.Title, GameTitle = source.GameTitle, Author = source.Author,
        SystemName = source.SystemName, FormatName = "VGM",
        SoundSourceDisplay = string.Join(" + ", source.MusicChannels.Select(channel => channel.ChipName).Where(c => !string.IsNullOrWhiteSpace(c) && c != "MIX").Distinct()),
        Channels = source.MusicChannels.Select(channel => channel with { KeyboardNotes = channel.KeyboardNotes.ToArray() }).ToArray(), SpectrumLevels = source.SpectrumLevels.ToArray(),
        RegisterBanks = source.RegisterBanks.Select(bank => bank with { Values = bank.Values.ToArray() }).ToArray(),
        SpectrumKind = source.SpectrumKind
    };
    public void Dispose() => _player.Dispose();
}
