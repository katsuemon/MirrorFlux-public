namespace MirrorFlux.Services.Music;

/// <summary>MDX を MXDRV 互換エンジンで鳴らす再生バックエンド。</summary>
public sealed class MdxPlaybackBackend : WaveOutPlaybackBackend<MdxWaveProvider>, IMusicPlaybackBackend
{
    public override string FormatName => "MDX";
    public override IReadOnlyCollection<string> SupportedExtensions { get; } = MusicFormats.Mdx;
    protected override MdxWaveProvider CreateProvider(string path) => new(path);
    // NanoDrive 8 への出力を有効にしていれば、音声出力デバイスがなくても基板だけで鳴らせる。
    public bool CanPlayWithoutAudioDevice => new SettingsStore().Load().Settings.Nd8Ndsif;

    public bool SetChannelMuted(MusicChannelSnapshot channel, bool muted) { lock (Sync) return Provider?.SetChannelMuted(channel, muted) ?? false; }

    /// <summary>
    /// 表示用の状態を返す。
    /// 鍵盤とレベルメーターは実際に聞こえている時刻に合わせる。
    /// </summary>
    public override MusicPlaybackSnapshot GetSnapshot()
    {
        lock (Sync)
        {
            if (Provider == null) return new();
            return Provider.Timeline.ApplyTo(Provider.Snapshot(), Output, IsPlaying, IsPaused);
        }
    }
}
