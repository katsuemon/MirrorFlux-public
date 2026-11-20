namespace MirrorFlux.Services.Music;

/// <summary>NSF・SPC などを Game Music Emu で鳴らす再生バックエンド。</summary>
public sealed class GmePlaybackBackend : WaveOutPlaybackBackend<GmeWaveProvider>, IMusicPlaybackBackend
{
    public override string FormatName => "GME";
    public override IReadOnlyCollection<string> SupportedExtensions { get; } = MusicFormats.Gme;
    protected override GmeWaveProvider CreateProvider(string path) => new(path);

    /// <summary>
    /// 曲内のトラックを切り替える。
    /// 出力を止めて切り替え、一時停止中でなければ再開する。
    /// </summary>
    public bool SelectTrack(int index)
    {
        lock (Sync)
        {
            if (Provider == null) return false;
            Output?.Stop();
            bool selected = Provider.SelectTrack(index);
            if (!Provider.IsPaused) Output?.Play();
            if (selected) RaisePlaybackStarted();
            return selected;
        }
    }

    public bool SetChannelMuted(MusicChannelSnapshot channel, bool muted) { lock (Sync) return Provider?.SetChannelMuted(channel, muted) ?? false; }
}
