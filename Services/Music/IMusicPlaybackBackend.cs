namespace MirrorFlux.Services.Music;

/// <summary>
/// 形式ごとの再生エンジン（デコーダー）の共通の約束。
/// 新しい形式は、これを実装して MusicPlaybackService に登録すれば追加できる。
/// 形式固有のヘッダー・レジスタ・サンプルバンクはこの内側に閉じ込める。
/// </summary>
public interface IMusicPlaybackBackend : IDisposable
{
    string FormatName { get; }
    IReadOnlyCollection<string> SupportedExtensions { get; }
    bool SupportsFullViewer => true;
    string? CurrentFilePath { get; }
    bool IsPlaying { get; }
    bool IsPaused { get; }
    event Action? PlaybackStarted;
    event Action? PlaybackStopped;
    event Action? PlaybackPaused;
    event Action? PlaybackResumed;
    bool Play(string filePath);
    /// <summary>
    /// 直前の Play が失敗した理由（利用者に見せる文言）。
    /// 理由が分からなければ null。
    /// </summary>
    string? LastError => null;
    /// <summary>
    /// 音声出力デバイスがなくても再生を試みるか。
    /// 実機出力を持つ形式だけ true。
    /// </summary>
    bool CanPlayWithoutAudioDevice => false;
    bool SelectTrack(int index) => false;
    bool SetChannelMuted(MusicChannelSnapshot channel, bool muted) => false;
    bool CanSeek => false;
    bool Seek(TimeSpan position) => false;
    void SetOutputGain(float gain) { }
    void TogglePause();
    void Stop();
    MusicPlaybackSnapshot GetSnapshot();
}

/// <summary>
/// 画面側から使う音楽再生の窓口。
/// サウンドバーとサウンドビューアはこれを通して同じ再生を操作する。
/// </summary>
public interface IMusicPlaybackService
{
    string? CurrentFilePath { get; }
    bool IsPlaying { get; }
    bool IsPaused { get; }
    event Action? PlaybackStarted;
    event Action? PlaybackStopped;
    event Action? PlaybackPaused;
    event Action? PlaybackResumed;
    bool CanPlay(string filePath);
    bool CanOpenFullViewer(string filePath) => CanPlay(filePath);
    bool Play(string filePath);
    /// <summary>
    /// 直前の Play が失敗した理由（例:「音声出力デバイスがありません」）。
    /// 理由が分からなければ null。
    /// </summary>
    string? LastError => null;
    bool SelectTrack(int index) => false;
    bool SetChannelMuted(MusicChannelSnapshot channel, bool muted) => false;
    bool CanSeek => false;
    bool Seek(TimeSpan position) => false;
    void SetFadeGain(float gain) { }
    void TogglePause();
    void Stop();
    MusicPlaybackSnapshot GetSnapshot();
}
