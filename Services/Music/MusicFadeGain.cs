namespace MirrorFlux.Services.Music;

/// <summary>自動で次の曲へ進むときのフェードの音量を求める。</summary>
internal static class MusicFadeGain
{
    public static readonly TimeSpan AutoAdvanceDuration = TimeSpan.FromSeconds(10);

    /// <summary>フェード開始からの経過時間に応じて、1から0へ直線的に下がる音量を返す。</summary>
    public static float At(TimeSpan elapsed)
        => (float)Math.Clamp(1d - elapsed.TotalSeconds / AutoAdvanceDuration.TotalSeconds, 0d, 1d);
}
