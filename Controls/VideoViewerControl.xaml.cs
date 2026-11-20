using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using MirrorFlux.Input;
using MirrorFlux.Services;
using MirrorFlux.Services.Music;

namespace MirrorFlux.Controls;

/// <summary>
/// 動画ビューア。Windows と WPF の再生機能だけで再生し、コーデックの同梱や外部プロセスは使わない。
/// </summary>
public partial class VideoViewerControl : UserControl, IViewerControl
{
    private readonly IReadOnlyList<ViewerFunctionBinding> _bindings;
    private readonly DispatcherTimer _timer;
    private bool _closed = true;
    private bool _opened;
    private bool _playing;
    private bool _ended;
    private bool _playWhenOpened;
    private bool _updatingPosition;
    private string? _error;
    private TimeSpan _duration;

    public string FilePath { get; private set; } = "";
    public string StatusText { get; private set; } = "";
    public string LocationText { get; private set; } = "";
    public bool IsPromptActive => false;
    public event EventHandler? StatusChanged;
    public event EventHandler? CloseRequested;

    /// <summary>動画用のキー割り当てと音量を受け取り、表示更新のタイマーを用意する。</summary>
    public VideoViewerControl(IEnumerable<ViewerFunctionBinding> bindings, int volumePercent)
    {
        _bindings = bindings.Where(b => b.Viewer == "video").ToArray();
        InitializeComponent();
        Video.Volume = Math.Clamp(volumePercent, 0, 100) / 100.0;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => UpdateStatus();
        Loaded += (_, _) => Focus();
        Unloaded += (_, _) => Close();
        InputMethod.SetIsInputMethodEnabled(this, false);
    }

    /// <summary>動画ファイルを開き、開けたら自動で再生を始めるよう準備する。</summary>
    public bool Load(string filePath)
    {
        Close();
        if (!File.Exists(filePath)) return false;
        try
        {
            // 状態を初期化して読み込み中の表示を出す。
            FilePath = Path.GetFullPath(filePath);
            _closed = false;
            _opened = false;
            _playing = false;
            _ended = false;
            _playWhenOpened = true;
            _error = null;
            _duration = TimeSpan.Zero;
            Video.Source = new Uri(FilePath, UriKind.Absolute);
            Message.Text = "動画を読み込み中…";
            Message.Visibility = Visibility.Visible;
            // LoadedBehavior が Manual のときは、Play を呼ばないとファイルを開き始めない。
            Video.Play();
            UpdateStatus();
            return true;
        }
        catch (Exception ex)
        {
            Fail(ex);
            Close();
            return false;
        }
    }

    /// <summary>再生を止めて動画を閉じる。</summary>
    public void Close()
    {
        _closed = true;
        _timer.Stop();
        _playing = false;
        _opened = false;
        Video.Close();
        Video.Source = null;
    }

    /// <summary>動画が開けたら長さをシークバーに設定し、再生するか一時停止のまま待つ。</summary>
    private void OnMediaOpened(object sender, RoutedEventArgs e)
    {
        if (_closed || _error != null) return;
        try
        {
            // 長さが分かるときだけシークバーを使えるようにする。
            _opened = true;
            _duration = Video.NaturalDuration.HasTimeSpan ? Video.NaturalDuration.TimeSpan : TimeSpan.Zero;
            PositionSlider.Maximum = Math.Max(1, _duration.TotalSeconds);
            PositionSlider.IsEnabled = _duration > TimeSpan.Zero;
            Message.Visibility = Visibility.Collapsed;
            // 読み込み中に再生・一時停止が切り替えられていれば、その指定に従う。
            if (_playWhenOpened) StartPlayback();
            else Video.Pause();
            UpdateStatus();
        }
        catch (Exception ex) { Fail(ex); }
    }

    /// <summary>
    /// 動画を再生する。音楽が再生中なら一時停止して音が重ならないようにし、
    /// 最後まで再生した後なら先頭から再生し直す。
    /// </summary>
    private void StartPlayback()
    {
        var music = MusicPlaybackService.Instance;
        if (music.IsPlaying && !music.IsPaused) music.TogglePause();
        if (_ended) { Video.Position = TimeSpan.Zero; _ended = false; }
        Video.Play();
        _playing = true;
        _timer.Start();
    }

    /// <summary>最後まで再生したら再生終了の状態にする。</summary>
    private void OnMediaEnded(object sender, RoutedEventArgs e)
    {
        if (_closed) return;
        _playing = false;
        _ended = true;
        _timer.Stop();
        try
        {
            // 明示的に一時停止しておく。こうしないと、この後のシークで再生が勝手に再開してしまう。
            Video.Pause();
            UpdateStatus();
        }
        catch (Exception ex) { Fail(ex); }
    }

    private void OnMediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        if (!_closed) Fail(e.ErrorException);
    }

    /// <summary>再生できなかったときに動画を閉じ、理由をメッセージとステータスに出す。</summary>
    private void Fail(Exception error)
    {
        _timer.Stop();
        _playing = false;
        _opened = false;
        _error = "Windowsの再生機能で開けませんでした。形式・コーデック・ファイルを確認してください。";
        Video.Close();
        PositionSlider.IsEnabled = false;
        Message.Text = _error + "\n" + error.Message;
        Message.Visibility = Visibility.Visible;
        UpdateStatus();
    }

    /// <summary>再生位置を 0 から動画の長さの間に収めて移動する。</summary>
    private void Seek(TimeSpan position)
    {
        if (!_opened || _duration <= TimeSpan.Zero) return;
        Video.Position = TimeSpan.FromSeconds(Math.Clamp(position.TotalSeconds, 0, _duration.TotalSeconds));
        _ended = false;
        UpdateStatus();
    }

    /// <summary>利用者がシークバーを動かしたときだけシークする。表示更新による変化は無視する。</summary>
    private void OnPositionChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_updatingPosition && _opened) Seek(TimeSpan.FromSeconds(e.NewValue));
    }

    /// <summary>ステータス行、位置表示、シークバーを現在の再生状態に合わせる。</summary>
    private void UpdateStatus()
    {
        if (_closed) return;
        // ファイル名と状態、経過時間と長さと音量の文字を作る。
        TimeSpan position = _opened ? Video.Position : TimeSpan.Zero;
        string state = _error ?? (!_opened ? "読込中" : _playing ? "再生中" : _ended ? "再生終了" : "停止/休止");
        StatusText = $"{Path.GetFileName(ArchiveService.GetOriginalViewPath(FilePath))} | {state}";
        string volume = Video.IsMuted ? "消音" : $"音量 {Video.Volume:P0}";
        LocationText = $"{FormatTime(position)} / {(_duration > TimeSpan.Zero ? FormatTime(_duration) : "--:--")} | {volume}";
        TimeText.Text = LocationText;
        // シークバーを動かしてもシークが起きないよう、更新中の印を立てておく。
        _updatingPosition = true;
        try { PositionSlider.Value = position.TotalSeconds; }
        finally { _updatingPosition = false; }
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private static string FormatTime(TimeSpan time) =>
        $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}";

    /// <summary>
    /// INI のキー割り当てから操作を探して実行する。キーリピートではシークと音量だけを繰り返す。
    /// 割り当てが none（何もしない）でも真を返し、ホスト側の既定のキー動作を抑える。
    /// </summary>
    public bool HandleKeyDown(KeyEventArgs e)
    {
        Key key = e.NormalizedKey();
        var binding = _bindings.LastOrDefault(b => b.InputKey == key && b.Modifiers == Keyboard.Modifiers);
        if (binding == null) return false;
        if (!e.IsRepeat || binding.Command is "backward1" or "forward1" or "backward" or "forward" or "volumedown" or "volumeup")
            ExecuteAction(binding.Command);
        return true;
    }

    /// <summary>
    /// ファンクションキーの操作番号に対応する操作を実行する。
    /// 物理的なFキーから操作番号への変換はメインウィンドウが済ませてから呼ぶ。
    /// </summary>
    public void ExecuteFunctionKey(int keyNumber)
    {
        var action = ViewerFunctionBindings.FindActionByNumber("video", keyNumber);
        if (action != null) ExecuteAction(action.Command);
    }

    /// <summary>動画ビューアの操作名を実行し、ステータスを更新する。失敗したらエラー表示にする。</summary>
    private void ExecuteAction(string command)
    {
        if (_closed) return;
        try
        {
            // 閉じる、ヘルプ、再生/一時停止、停止、シーク、音量、消音を振り分ける。
            // 読み込み中の再生/一時停止は、開けたときに再生するかどうかの指定を切り替える。
            switch (command)
            {
                case "close": CloseRequested?.Invoke(this, EventArgs.Empty); return;
                case "help":
                    HelpText.Text = "動画ビューアー\nWindowsの再生機能を使用します。\n\n" + string.Join("\n", _bindings
                        .GroupBy(b => (b.Number, b.InputKey, b.Modifiers)).Select(g => g.Last())
                        .Where(b => b.Command != "none").Select(b => ViewerFunctionBindings.Format(b) + "  " + ViewerFunctionBindings.FindAction("video", b.Command)?.Label));
                    HelpPanel.Visibility = HelpPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
                    break;
                case "playpause":
                    if (_error != null) break;
                    if (!_opened) { _playWhenOpened = !_playWhenOpened; break; }
                    if (_playing) { Video.Pause(); _playing = false; _timer.Stop(); }
                    else StartPlayback();
                    break;
                case "stop":
                    _playWhenOpened = false;
                    if (_opened) Video.Stop();
                    _playing = false; _ended = false; _timer.Stop();
                    break;
                case "backward1": Seek(Video.Position - TimeSpan.FromSeconds(1)); break;
                case "forward1": Seek(Video.Position + TimeSpan.FromSeconds(1)); break;
                case "backward": Seek(Video.Position - TimeSpan.FromSeconds(10)); break;
                case "forward": Seek(Video.Position + TimeSpan.FromSeconds(10)); break;
                case "beginning": Seek(TimeSpan.Zero); break;
                case "volumedown": Video.Volume = Math.Max(0, Video.Volume - 0.05); break;
                case "volumeup": Video.Volume = Math.Min(1, Video.Volume + 0.05); break;
                case "mute": Video.IsMuted = !Video.IsMuted; break;
            }
            UpdateStatus();
        }
        catch (Exception ex) { Fail(ex); }
    }
}
