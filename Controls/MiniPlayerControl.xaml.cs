using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MirrorFlux.Services.Music;

namespace MirrorFlux.Controls;

/// <summary>
/// ファイル一覧の下に出すサウンドバー。再生中だけ表示し、
/// チップチューンでは鍵盤とレベルメーター、音声ファイルではスペクトラムを描く。
/// </summary>
public partial class MiniPlayerControl : UserControl
{
    private readonly IMusicPlaybackService _player;
    private readonly DispatcherTimer _renderTimer;
    private readonly MusicPlaybackHooks _hooks;
    public event Action? ExpandRequested;

    public MiniPlayerControl() : this(MusicPlaybackService.Instance) { }
    /// <summary>再生サービスとつなぎ、更新タイマーと表示状態の切り替えを用意する。</summary>
    public MiniPlayerControl(IMusicPlaybackService player)
    {
        // 再生イベントとフレームごとの描画をまとめて扱う。
        _player = player;
        _hooks = new MusicPlaybackHooks(player, OnPlaybackChanged, OnPlaybackChanged, OnFrame);
        InitializeComponent();
        MiniSeekBar.SeekRequested += OnSeekRequested;
        UpdateKeyboardWidth();
        // 一時停止中も表示を保つため、約60回/秒の低優先タイマーでも更新する。
        _renderTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / 60) };
        _renderTimer.Tick += (_, _) => Refresh();
        // 画面に出ている間だけ購読し、見えなくなったら更新を止める。
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
        IsVisibleChanged += (_, _) =>
        {
            if (!_hooks.Subscribed) return;
            if (IsVisible) Refresh();
            else { _renderTimer.Stop(); _hooks.SetFrameActive(false); }
        };
    }

    /// <summary>画面更新ごとに、表示中かつ再生中なら描き直す。</summary>
    private void OnFrame()
    {
        if (!_hooks.Subscribed || !IsVisible) return;
        if (_player.IsPlaying)
            Refresh();
    }
    /// <summary>
    /// 鍵盤63白鍵分の幅を現在のDPIの鍵ピッチから求め、各表示とサウンドバー全体の幅をそろえる。
    /// </summary>
    private void UpdateKeyboardWidth()
    {
        if (KeyboardDisplay == null) return;
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        double width = 63 * MusicKeyboardDisplay.KeyPitch(dpi);
        KeyboardDisplay.Width = MeterDisplay.Width = PanDisplay.Width = MiniSeekBar.Width = MiniSpectrumDisplay.Width = MiniSpectrumRuler.Width = width;
        if (TitleText != null) TitleText.Width = width;
        if (StreamTitleText != null) StreamTitleText.Width = width;
        Width = width + 8;
    }
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        UpdateKeyboardWidth();
    }
    protected override Size MeasureOverride(Size constraint)
    {
        UpdateKeyboardWidth();
        return base.MeasureOverride(constraint);
    }
    /// <summary>再生イベントを購読して表示を更新する。</summary>
    private void Attach()
    {
        if (_hooks.Subscribed) return;
        _hooks.Subscribe();
        Refresh();
    }
    /// <summary>更新を止め、再生イベントの購読を解除する。</summary>
    private void Detach()
    {
        _renderTimer.Stop();
        _hooks.Unsubscribe();
    }
    /// <summary>再生イベントは別スレッドから来ることがあるため、UIスレッドに移してから更新する。</summary>
    private void OnPlaybackChanged() => Dispatcher.BeginInvoke(() => { if (_hooks.Subscribed) Refresh(); });
    /// <summary>再生状態を取り直し、表示の有無と更新の続け方を決めて描く。</summary>
    private void Refresh()
    {
        // 再生していなければ隠し、更新を止めて最後の状態だけ描く。
        var snapshot = _player.GetSnapshot();
        Visibility = snapshot.IsPlaying ? Visibility.Visible : Visibility.Collapsed;
        if (!snapshot.IsPlaying)
        {
            _renderTimer.Stop();
            _hooks.SetFrameActive(false);
            RenderSnapshot(snapshot);
            return;
        }
        // 再生中でも画面に見えていなければ更新を止める。
        if (IsLoaded && !IsVisible)
        {
            _renderTimer.Stop();
            _hooks.SetFrameActive(false);
            return;
        }
        // 表示中はタイマーを回し、一時停止していなければ画面更新ごとにも描く。
        _renderTimer.Start();
        _hooks.SetFrameActive(!snapshot.IsPaused);
        RenderSnapshot(snapshot);
    }

    /// <summary>曲番号の切り替えを出すのは、1ファイルに複数曲を持つ NSF / NSFE / HES だけ。</summary>
    private static bool HasSubsongs(MusicPlaybackSnapshot snapshot) =>
        snapshot.TrackCount > 1 && snapshot.FormatName is "NSF" or "NSFE" or "HES";

    /// <summary>再生状態のスナップショットをサウンドバーの各表示へ反映する。</summary>
    public void RenderSnapshot(MusicPlaybackSnapshot snapshot)
    {
        // 上段の再生状態、一時停止ボタンの絵、時間を更新する。
        string status = MusicPlayerShared.StatusText(snapshot);
        Brush statusBrush = MusicPlayerShared.StatusBrush(snapshot);
        string time = MusicPlayerShared.TimeText(snapshot);
        PlayStatusText.Text = status;
        PlayStatusText.Foreground = statusBrush;
        PauseIcon.Text = snapshot.IsPaused ? "" : "";
        SevenSegmentTime.Text = time;
        // 複数曲の形式なら曲番号と曲送りボタンを出す。曲数が255を超えるときは3桁の16進で表す。
        bool showSubsong = HasSubsongs(snapshot);
        SubsongPanel.Visibility = showSubsong ? Visibility.Visible : Visibility.Collapsed;
        TrackButtonsPanel.Visibility = showSubsong ? Visibility.Visible : Visibility.Collapsed;
        PauseButton.Margin = showSubsong ? new Thickness(0) : new Thickness(2, 0, 0, 0);
        int trackDigits = snapshot.TrackCount > 0xff ? 3 : 2;
        SubsongText.Width = trackDigits == 3 ? 66 : 48;
        string trackFormat = trackDigits == 3 ? "X3" : "X2";
        SubsongText.Text = $"{(snapshot.TrackIndex + 1).ToString(trackFormat)}/{snapshot.TrackCount.ToString(trackFormat)}";
        PlayStatusText.Width = showSubsong ? 34 : double.NaN;
        PlayStatusText.TextAlignment = TextAlignment.Left;
        PlayStatusText.Margin = new Thickness(0, 0, showSubsong ? 4 : 6, 0);
        TitleText.Text = snapshot.Title;
        // スペクトラムだけの音声ファイルか、チャンネル表示のあるチップチューンかで表示を切り替える。
        bool spectrumOnly = snapshot.UseSpectrumOnly;
        ChiptuneHeaderPanel.Visibility = spectrumOnly ? Visibility.Collapsed : Visibility.Visible;
        StreamHeaderPanel.Visibility = spectrumOnly ? Visibility.Visible : Visibility.Collapsed;
        ChannelMonitorPanel.Visibility = spectrumOnly ? Visibility.Collapsed : Visibility.Visible;
        SpectrumPanel.Visibility = spectrumOnly ? Visibility.Visible : Visibility.Collapsed;
        ExpandButton.Visibility = spectrumOnly ? Visibility.Collapsed : Visibility.Visible;
        // 音声ファイルは状態・時間・曲名、シークバー、スペクトラムを描いて終わる。
        if (spectrumOnly)
        {
            StreamPlayStatusText.Text = status;
            StreamPlayStatusText.Foreground = statusBrush;
            StreamSevenSegmentTime.Text = time;
            StreamTitleText.Text = snapshot.Title;

            double progress = snapshot.TotalTime > TimeSpan.Zero
                ? Math.Clamp(snapshot.CurrentTime.TotalSeconds / snapshot.TotalTime.TotalSeconds, 0, 1)
                : 0;
            MiniSeekBar.SetProgress(progress);
            MiniSpectrumDisplay.SetLevels(snapshot.SpectrumLevels.Select(level => (double)level).ToArray(), Array.Empty<string>(), snapshot.IsPaused);
            return;
        }
        // チップチューンは鍵盤を描き、先頭16チャンネルのレベルと左右の出力を集めてメーターに渡す。
        KeyboardDisplay.SetChannels(snapshot.Channels);
        var levels = new double[16];
        var panLeft = new double[16];
        var panRight = new double[16];
        var envelopeTimed = new bool[16];
        var panActive = new bool[16];
        int count = Math.Min(snapshot.Channels.Length, levels.Length);
        for (int index = 0; index < count; index++)
        {
            var channel = snapshot.Channels[index];
            levels[index] = channel.Level;
            (panLeft[index], panRight[index]) = MusicPlayerShared.PanLevels(channel);
            envelopeTimed[index] = channel.UsesEnvelopeTiming;
            panActive[index] = channel.IsInUse;
        }
        MeterDisplay.SetLevels(levels, Array.Empty<string>(), snapshot.IsPaused, envelopeTimed);
        PanDisplay.SetPanLevels(panLeft, panRight, panActive);
    }
    /// <summary>
    /// 再生中のサウンドバーの Ctrl キー操作を処理する。
    /// P で一時停止、S で停止、M でサウンドビューア表示、↑↓で曲送り、←→で5秒シーク。
    /// キーリピートでは切り替え系を繰り返さない。処理したら真を返す。
    /// </summary>
    internal bool HandleShortcut(Key key, ModifierKeys modifiers, bool repeat)
    {
        if (modifiers != ModifierKeys.Control || !_player.IsPlaying) return false;
        switch (key)
        {
            case Key.P:
                if (!repeat) _player.TogglePause();
                return true;
            case Key.S:
                if (!repeat) _player.Stop();
                return true;
            case Key.M:
                if (!repeat) ExpandRequested?.Invoke();
                return true;
            case Key.Up:
            case Key.Down:
                if (!repeat) SelectRelativeTrack(key == Key.Up ? -1 : 1);
                return true;
            case Key.Left:
            case Key.Right:
                var snapshot = _player.GetSnapshot();
                if (snapshot.TotalTime <= TimeSpan.Zero) return false;
                return MusicPlayerShared.SeekRelative(_player, snapshot, key == Key.Left ? -5 : 5);
            default:
                return false;
        }
    }

    private void OnPauseClick(object sender, RoutedEventArgs e) => _player.TogglePause();
    private void OnStopClick(object sender, RoutedEventArgs e) => _player.Stop();
    private void OnExpandClick(object sender, RoutedEventArgs e) => ExpandRequested?.Invoke();
    /// <summary>シークバーで押された割合の位置へ移動する。曲の長さが分からなければ何もしない。</summary>
    private void OnSeekRequested(double ratio)
    {
        var snapshot = _player.GetSnapshot();
        if (snapshot.TotalTime <= TimeSpan.Zero) return;
        var target = TimeSpan.FromSeconds(Math.Clamp(ratio, 0, 1) * snapshot.TotalTime.TotalSeconds);
        if (_player.Seek(target)) Refresh();
    }
    private void OnPreviousTrackClick(object sender, RoutedEventArgs e) => SelectRelativeTrack(-1);
    private void OnNextTrackClick(object sender, RoutedEventArgs e) => SelectRelativeTrack(1);
    /// <summary>複数曲の形式のときだけ、曲番号を delta だけ進めて表示を更新する。</summary>
    private void SelectRelativeTrack(int delta)
    {
        var snapshot = _player.GetSnapshot();
        if (HasSubsongs(snapshot) && MusicPlayerShared.SelectRelativeTrack(_player, snapshot, delta)) Refresh();
    }
}
