namespace MirrorFlux.Services.Music;

/// <summary>MP3・WAV などの一般の音声ファイルを NAudio で鳴らす再生バックエンド。</summary>
public sealed class AudioFilePlaybackBackend : WaveOutPlaybackBackend<AudioFileWaveProvider>, IMusicPlaybackBackend
{

    public override string FormatName => Provider?.FormatName ?? "AUDIO";
    public override IReadOnlyCollection<string> SupportedExtensions { get; } = MusicFormats.Audio;
    public bool SupportsFullViewer => false;
    public bool CanSeek { get { lock (Sync) return Provider?.CanSeek ?? false; } }
    protected override AudioFileWaveProvider CreateProvider(string path) => new(path);

    /// <summary>
    /// 指定位置へ移る。
    /// 曲の終わりで止まっていた場合は、一時停止中でなければ出力を再開する。
    /// </summary>
    public bool Seek(TimeSpan position)
    {
        lock (Sync)
        {
            if (Provider == null) return false;
            bool wasEnded = Provider.Ended;
            if (!Provider.Seek(position)) return false;
            if (wasEnded && !Provider.IsPaused)
            {
                Output?.Play();
            }
            return true;
        }
    }
}
