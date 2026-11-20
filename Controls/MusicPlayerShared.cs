using System.Windows.Media;
using MirrorFlux.Services.Music;

namespace MirrorFlux.Controls;

/// <summary>サウンドビューアとサウンドバーで共通の、再生状態の表記と操作。</summary>
internal static class MusicPlayerShared
{
    /// <summary>状態表示の文字（PAUSE / PLAY / STOP）。</summary>
    public static string StatusText(MusicPlaybackSnapshot snapshot) =>
        snapshot.IsPaused ? "PAUSE" : snapshot.IsPlaying ? "PLAY" : "STOP";

    /// <summary>状態表示の色。一時停止中は琥珀色、それ以外は水色。</summary>
    public static Brush StatusBrush(MusicPlaybackSnapshot snapshot) =>
        snapshot.IsPaused ? MusicDisplayDrawing.Amber : MusicDisplayDrawing.Cyan;

    /// <summary>曲の長さ。分からないときは「--:--」。</summary>
    public static string TotalTimeText(TimeSpan total) =>
        total > TimeSpan.Zero ? MusicDisplayDrawing.FormatTime(total) : "--:--";

    /// <summary>「経過 / 長さ」の表記。</summary>
    public static string TimeText(MusicPlaybackSnapshot snapshot) =>
        $"{MusicDisplayDrawing.FormatTime(snapshot.CurrentTime)} / {TotalTimeText(snapshot.TotalTime)}";

    /// <summary>左右の出力レベル。音源が値を持たなければ Pan の左右ビット（2=左、1=右）から 0 か 1 にする。</summary>
    public static (double Left, double Right) PanLevels(MusicChannelSnapshot channel) =>
        (channel.PanLeftLevel ?? ((channel.Pan & 2) != 0 ? 1 : 0), channel.PanRightLevel ?? ((channel.Pan & 1) != 0 ? 1 : 0));

    /// <summary>曲番号を delta だけ進める（端では反対側へ回る）。曲が1つ以下か、切り替えられなければ false。</summary>
    public static bool SelectRelativeTrack(IMusicPlaybackService player, MusicPlaybackSnapshot snapshot, int delta)
    {
        if (snapshot.TrackCount <= 1) return false;
        int next = ((snapshot.TrackIndex + delta) % snapshot.TrackCount + snapshot.TrackCount) % snapshot.TrackCount;
        return player.SelectTrack(next);
    }

    /// <summary>
    /// 再生位置を seconds 秒動かす。0 と曲の長さ（分かる場合）の間に収める。
    /// シークできない形式かどうかは再生側の Seek が判断し、そのとき false を返す。
    /// </summary>
    public static bool SeekRelative(IMusicPlaybackService player, MusicPlaybackSnapshot snapshot, double seconds)
    {
        var target = snapshot.CurrentTime + TimeSpan.FromSeconds(seconds);
        if (snapshot.TotalTime > TimeSpan.Zero && target > snapshot.TotalTime) target = snapshot.TotalTime;
        if (target < TimeSpan.Zero) target = TimeSpan.Zero;
        return player.Seek(target);
    }
}

/// <summary>
/// 再生サービスのイベント（開始・停止・一時停止・再開）の購読と、画面更新ごとの描画呼び出しをまとめる。
/// 描画は CompositionTarget.Rendering に乗せ、同じフレームで2回以上呼ばない。
/// </summary>
internal sealed class MusicPlaybackHooks
{
    private readonly IMusicPlaybackService _player;
    private readonly Action _started;
    private readonly Action _changed;
    private readonly Action _frame;
    private TimeSpan _lastRenderingTime;

    /// <param name="started">再生開始時（他の3つと分けたいとき）。</param>
    /// <param name="changed">停止・一時停止・再開時。</param>
    /// <param name="frame">フレームごとの描画（FrameActive の間だけ）。</param>
    public MusicPlaybackHooks(IMusicPlaybackService player, Action started, Action changed, Action frame)
    {
        _player = player;
        _started = started;
        _changed = changed;
        _frame = frame;
    }

    public bool Subscribed { get; private set; }
    public bool FrameActive { get; private set; }

    /// <summary>再生サービスのイベントを購読する。二重には購読しない。</summary>
    public void Subscribe()
    {
        if (Subscribed) return;
        _player.PlaybackStarted += _started;
        _player.PlaybackStopped += _changed;
        _player.PlaybackPaused += _changed;
        _player.PlaybackResumed += _changed;
        Subscribed = true;
    }

    /// <summary>フレームごとの描画を止め、再生サービスのイベント購読を解除する。</summary>
    public void Unsubscribe()
    {
        SetFrameActive(false);
        if (!Subscribed) return;
        _player.PlaybackStarted -= _started;
        _player.PlaybackStopped -= _changed;
        _player.PlaybackPaused -= _changed;
        _player.PlaybackResumed -= _changed;
        Subscribed = false;
    }

    /// <summary>フレームごとの描画呼び出しを登録または解除する。状態が同じなら何もしない。</summary>
    public void SetFrameActive(bool active)
    {
        if (active == FrameActive) return;
        if (active) CompositionTarget.Rendering += OnRendering;
        else CompositionTarget.Rendering -= OnRendering;
        FrameActive = active;
    }

    /// <summary>
    /// 画面更新ごとに描画を呼ぶ。Rendering は1フレームに複数回来ることがあるため、
    /// 描画時刻が前回と同じなら呼ばない。
    /// </summary>
    private void OnRendering(object? sender, EventArgs e)
    {
        if (e is RenderingEventArgs args)
        {
            if (args.RenderingTime == _lastRenderingTime) return;
            _lastRenderingTime = args.RenderingTime;
        }
        _frame();
    }
}
