using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using MirrorFlux.Controls;
using MirrorFlux.Input;
using MirrorFlux.Models;
using MirrorFlux.Services;
using MirrorFlux.ViewModels;

namespace MirrorFlux;

/// <summary>
/// 左右2つのパネル、ログ、ファンクションキー欄、ビューア、サウンドバーを持つメインウィンドウ。
/// キー入力を受けてMainViewModelのコマンドへ渡し、ウィンドウの高さやフォーカスを管理する。
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly KeyBindingService _keyBindings = new();
    private readonly DispatcherTimer _clockTimer;
    private PaneDirectoryMonitor? _leftDirectoryMonitor, _rightDirectoryMonitor;

    /// <summary>
    /// 設定を読み込んでViewModelを作り、キー割り当て・表示設定・ビューアやログのイベントを結び付ける。
    /// </summary>
    public MainWindow()
    {
        // 設定を読み込んだViewModelを画面に結び付ける。
        _viewModel = new MainViewModel(new SettingsStore());
        DataContext = _viewModel;

        // バージョン表示と、Shiftを押している間のファンクションキー表示の切り替えを設定する。
        InitializeComponent();
        var version = typeof(MainWindow).Assembly.GetName().Version!;
        AppVersionText.Text = $"—— Ver.{version.Major}.{version.Minor}.{version.Build} ——";
        AddHandler(Keyboard.PreviewKeyUpEvent, new KeyEventHandler((_, _) => UpdateFunctionModifiers()), true);
        Activated += (_, _) => { UpdateFunctionModifiers(); RestoreWindowHeight(); };
        Deactivated += (_, _) => _viewModel.FunctionShiftPressed = false;
        _viewModel.ViewerShiftFunctionKeyRequested += number =>
        {
            if (MainViewerHost.CurrentViewer is MirrorFlux.Controls.TextViewerControl text) text.ExecuteShiftFunctionKey(number);
        };

        _keyBindings.ApplyBindings(_viewModel.Settings.KeyBindings);

        // ログ欄の高さとフォントを設定から反映する。使えないフォント名は無視する。
        LogRow.Height = new GridLength(_viewModel.Settings.LogHeight);
        try
        {
            FontFamily = new System.Windows.Media.FontFamily(_viewModel.Settings.FontFamily);
        }
        catch
        {
        }
        FontSize = _viewModel.Settings.FontSize;

        // ログ追加、ビューアの開閉、サウンドバーからの拡大要求を受ける。
        _viewModel.Logs.CollectionChanged += Logs_CollectionChanged;

        _viewModel.OpenViewerRequested += OnOpenViewerRequested;
        _viewModel.CloseViewerRequested += OnCloseViewerRequested;
        _viewModel.ViewerFunctionKeyRequested += OnViewerFunctionKeyRequested;

        MiniPlayerView.ExpandRequested += OnMiniPlayerExpandRequested;

        // ビューアの状態表示をステータス欄へ写し、閉じる要求とテキストファイルの前後移動を受ける。
        MainViewerHost.StatusChanged += (_, _) =>
        {
            if (MainViewerHost.CurrentViewer is not null)
            {
                _viewModel.IsHexViewer = MainViewerHost.CurrentViewer is MirrorFlux.Controls.TextViewerControl text && text.IsHexMode;
                _viewModel.ViewerStatusText = MainViewerHost.CurrentViewer.StatusText;
                _viewModel.ViewerLocationText = MainViewerHost.CurrentViewer.LocationText;
            }
        };
        MainViewerHost.CloseRequested += (_, _) =>
        {
            _viewModel.CloseViewer();
        };
        MainViewerHost.TextFileNavigationRequested += OnTextFileNavigationRequested;

        // 時計表示を1秒ごとに更新する。
        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => _viewModel.UpdateClock();
        _clockTimer.Start();
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    /// <summary>
    /// ウィンドウハンドルができた時点で、タイトルバーを暗くし、起動時の位置と大きさを決め、スリープ時の停止を設定する。
    /// </summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        EnableDarkMode();
        StartupWindowPlacement.Apply(this);
        // 再生したままスリープすると復帰後に応答しなくなることがあるため、スリープに入る前に再生中の曲を止める。
        _suspendGuard = new MirrorFlux.Services.Music.SuspendPlaybackGuard(this, StopPlaybackForSuspend, RestoreWindowHeightAfterResume);
        Closed += (_, _) => _suspendGuard?.Dispose();
    }

    private MirrorFlux.Services.Music.SuspendPlaybackGuard? _suspendGuard;
    private double _displayWindowHeight = 832;
    private Rect? _positionBeforeSuspend;
    private int _suspendGeneration;

    /// <summary>復帰後、Windows の画面配置が落ち着いてから元の高さを適用する。</summary>
    private void RestoreWindowHeightAfterResume()
    {
        int generation = _suspendGeneration;
        void Restore()
        {
            if (generation != _suspendGeneration) return;
            RestoreWindowHeight();
            StartupWindowPlacement.RestorePosition(this, _positionBeforeSuspend);
        }
        Dispatcher.InvokeAsync(Restore, DispatcherPriority.ApplicationIdle);
        var timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(750) };
        timer.Tick += (_, _) => { timer.Stop(); Restore(); };
        timer.Start();
    }

    private void RestoreWindowHeight()
    {
        if (!IsLoaded || WindowState == WindowState.Minimized) return;
        if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal;
        SetWindowHeight(_displayWindowHeight);
    }

    /// <summary>
    /// スリープに入る前に、再生中または一時停止中の曲を止めてログに残す。
    /// </summary>
    private void StopPlaybackForSuspend()
    {
        _suspendGeneration++;
        _positionBeforeSuspend = StartupWindowPlacement.CapturePosition(this);
        var music = MirrorFlux.Services.Music.MusicPlaybackService.Instance;
        bool wasPlaying = music.IsPlaying || music.IsPaused;
        music.Stop();
        if (!wasPlaying) return;
        _viewModel.AddLog("スリープに入るため、再生中の曲を停止しました", "INFO");
    }

    /// <summary>
    /// タイトルバーをダークモードにする。Windows 10 20H1より前は属性番号が19なので、20が失敗したら19で試す。
    /// </summary>
    private void EnableDarkMode()
    {
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                int darkMode = 1;
                if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int)) != 0)
                {
                    DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref darkMode, sizeof(int));
                }
            }
        }
        catch
        {
        }
    }

    /// <summary>
    /// ログが追加されたら、描画のタイミングで最新の行までスクロールする。
    /// </summary>
    private void Logs_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if ((e.Action == NotifyCollectionChangedAction.Add || e.Action == NotifyCollectionChangedAction.Reset) && LogListBox.Items.Count > 0)
        {
            Dispatcher.InvokeAsync(() =>
            {
                if (LogListBox.Items.Count > 0)
                {
                    var lastItem = LogListBox.Items[^1];
                    if (lastItem is not null)
                    {
                        LogListBox.ScrollIntoView(lastItem);
                    }
                }
            }, DispatcherPriority.Render);
        }
    }

    /// <summary>
    /// 読み込み中のクリックを無視する。ダイアログ表示中にメインウィンドウをクリックしたら、ダイアログを前面へ出す。
    /// </summary>
    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        // 読み込み中はクリックを受け付けない。
        base.OnPreviewMouseDown(e);
        if (_viewModel.IsLoading)
        {
            e.Handled = true;
            return;
        }
        // 表示中のダイアログがあれば、そちらを前面へ出してクリックを止める。
        foreach (Window owned in OwnedWindows)
        {
            if (owned.IsVisible)
            {
                owned.Activate();
                e.Handled = true;
                return;
            }
        }
    }

    /// <summary>
    /// 読み込み中はホイール操作を無視する。
    /// </summary>
    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        base.OnPreviewMouseWheel(e);
        if (_viewModel.IsLoading)
        {
            e.Handled = true;
        }
    }

    private bool _isExitConfirmed = false;

    /// <summary>
    /// 終了前に確認する。ファイル操作中は終了させず、終了するときは開いているダイアログもすべて閉じる。
    /// </summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);

        // コピー・移動・削除・アーカイブ更新の途中で終了すると、書きかけのファイルや移動途中の状態が残るため止める。
        if (_viewModel.IsFileOperationRunning)
        {
            e.Cancel = true;
            _viewModel.StatusMessage = "ファイル操作中は終了できません。中止キー（既定 Esc）で中止してから終了してください";
            _viewModel.AddLog("ファイル操作中のため終了を中止しました", "WARN");
            return;
        }

        // 終了確認ダイアログを出し、「いいえ」なら終了を取り消す。
        if (!_isExitConfirmed)
        {
            var confirmDialog = ConfirmDialog.ForExit();
            confirmDialog.PositionCenter(this);
            bool? result = confirmDialog.ShowDialog();
            if (result != true)
            {
                e.Cancel = true;
                _viewModel.StatusMessage = "終了を中止しました";
                _viewModel.AddLog("終了を中止しました", "CANCEL");
                FocusActiveList();
                return;
            }
            _isExitConfirmed = true;
            _viewModel.AddLog("アプリケーションを終了します", "QUIT");
        }

        // 開いているダイアログや小ウィンドウをすべて閉じる。
        foreach (Window owned in OwnedWindows)
        {
            try
            {
                owned.DialogResult = false;
                owned.Close();
            }
            catch { }
        }
    }

    /// <summary>
    /// 初期化後にファイル一覧の行数を反映し、ビューア切り替えでの高さ変更とフォルダー監視を始める。
    /// </summary>
    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        // パネルの初期表示を読み込み、%fileRows に合わせて高さを決める。
        await _viewModel.InitializeAsync();
        ApplyFileRows();
        // サウンドビューアを開いている間はプレイヤー用の高さにし、閉じたらファイル一覧用の高さへ戻す。
        _viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(MainViewModel.IsMusicPlayerViewer) or nameof(MainViewModel.IsViewerActive))
                SetWindowHeight(_viewModel.IsViewerActive && _viewModel.IsMusicPlayerViewer ? PlayerWindowHeight() : _filerWindowHeight);
        };
        // 左右のパネルのフォルダーを監視し、ウィンドウが前面に戻ったときにも再確認する。
        _leftDirectoryMonitor = new PaneDirectoryMonitor(_viewModel.LeftPane, Dispatcher);
        _rightDirectoryMonitor = new PaneDirectoryMonitor(_viewModel.RightPane, Dispatcher);
        Activated += (_, _) => { _leftDirectoryMonitor?.Schedule(); _rightDirectoryMonitor?.Schedule(); };
        Closed += (_, _) => { _leftDirectoryMonitor?.Dispose(); _rightDirectoryMonitor?.Dispose(); };
        FocusActiveList();
    }

    /// <summary>
    /// 標準のウィンドウの高さ。サウンドビューアはこの高さで設計されているため、%fileRows で高さを変えてもプレイヤー表示中は戻す。
    /// </summary>
    private const double StandardWindowHeight = 832;
    private double _filerWindowHeight = StandardWindowHeight;

    /// <summary>
    /// %fileRows の行数がファイル一覧に入るよう、ウィンドウの高さを決める。0なら標準の高さのまま。
    /// </summary>
    private void ApplyFileRows()
    {
        // 行数の指定がない、または一覧の大きさがまだ決まっていなければ何もしない。
        int rows = _viewModel.Settings.FileRows;
        if (rows <= 0) { SetWindowHeight(StandardWindowHeight); return; }
        UpdateLayout();
        if (LeftList.ActualHeight <= 0) return;
        // 作業領域の高さをDPIで換算し、その範囲に収まる高さを求めて反映する。
        double workArea = StartupWindowPlacement.WorkAreaHeight(this) / System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleY;
        _filerWindowHeight = StartupWindowPlacement.HeightForRows(ActualHeight, LeftList.ActualHeight, rows,
            MinHeight, workArea > 0 ? Math.Max(MinHeight, workArea) : double.MaxValue);
        SetWindowHeight(_filerWindowHeight);
    }

    /// <summary>
    /// サウンドビューアの高さを決める。今の高さでも曲リストの行数が標準の高さと同じなら、ウィンドウを動かさずそのまま使う。
    /// 違うときだけ標準の高さへ戻す。
    /// </summary>
    private double PlayerWindowHeight()
    {
        if (Math.Abs(ActualHeight - StandardWindowHeight) < 0.5) return StandardWindowHeight;
        UpdateLayout();
        return MainViewerHost.CurrentViewer is MusicPlayerControl player &&
            StartupWindowPlacement.SameWholeRows(player.TrackListAvailableHeight, ActualHeight, StandardWindowHeight, 18)
            ? ActualHeight : StandardWindowHeight;
    }

    /// <summary>
    /// ウィンドウの高さを変え、下端が画面外へ出ないようにして、選択中の項目を見える位置に保つ。
    /// </summary>
    private void SetWindowHeight(double height)
    {
        _displayWindowHeight = height;
        // Windows がスリープ復帰やディスプレイ再接続で作業領域いっぱいに広げても、この高さを上限にする。
        MaxHeight = height;
        if (Math.Abs(ActualHeight - height) < 0.5) return;
        Height = height;
        UpdateLayout();
        StartupWindowPlacement.KeepBottomInside(this);
        ScrollActiveListIntoView();
    }

    /// <summary>
    /// ファイルをビューアで開く。開けたら表示状態を反映し、開けなければ理由をログとステータス欄に出す。
    /// </summary>
    private void OnOpenViewerRequested(
        string filePath,
        bool forceGraphicViewer,
        bool forceTextViewer,
        bool forceVgmPlayer = false,
        bool forcePdfViewer = false,
        bool forceHexViewer = false,
        bool forceVideoViewer = false)
    {
        // 開けたときは、どの種類のビューアかと状態表示を反映する。
        if (MainViewerHost.Open(filePath, forceGraphicViewer, forceTextViewer, forceVgmPlayer, forcePdfViewer, forceHexViewer, _viewModel.ActivePane, forceVideoViewer, _viewModel.Settings))
        {
            _viewModel.IsViewerActive = true;
            _viewModel.IsGraphicViewer = MainViewerHost.IsGraphicViewer;
            _viewModel.IsVideoViewer = MainViewerHost.IsVideoViewer;
            _viewModel.IsPdfViewer = MainViewerHost.IsPdfViewer;
            _viewModel.IsMusicPlayerViewer = MainViewerHost.IsMusicPlayerViewer;
            if (MainViewerHost.CurrentViewer is not null)
            {
                _viewModel.ViewerStatusText = MainViewerHost.CurrentViewer.StatusText;
                _viewModel.ViewerLocationText = MainViewerHost.CurrentViewer.LocationText;
            }
        }
        // 音楽ファイルを開けなかった理由が分かっているとき（例：音声出力デバイスがない）は、それを出す。
        else if (MirrorFlux.Services.Music.MusicPlaybackService.Instance.CanOpenFullViewer(filePath) &&
            MirrorFlux.Services.Music.MusicPlaybackService.Instance.LastError is { } reason)
        {
            _viewModel.AddLog($"{reason}: {filePath}", "ERR");
            _viewModel.StatusMessage = reason;
        }
        else
        {
            _viewModel.AddLog($"ビューアーで開けませんでした: {filePath}", "ERR");
            _viewModel.StatusMessage = "ファイルを開けませんでした";
        }
        // 音声出力デバイスがなくNanoDriveだけで鳴らすときは、その旨をログに残す。
        if (_viewModel.IsMusicPlayerViewer && MirrorFlux.Services.Vgm.VgmPlayerService.Instance.IsHardwareOnly)
            _viewModel.AddLog($"音声出力デバイスがないため、実機（NDSIF）だけで再生します: {filePath}", "INFO");
    }

    /// <summary>
    /// サウンドバーの拡大要求で、再生中の曲をサウンドビューアで開く。
    /// </summary>
    private void OnMiniPlayerExpandRequested()
    {
        var musicPath = MirrorFlux.Services.Music.MusicPlaybackService.Instance.CurrentFilePath;
        if (!string.IsNullOrEmpty(musicPath) &&
            System.IO.File.Exists(musicPath) &&
            MirrorFlux.Services.Music.MusicPlaybackService.Instance.CanOpenFullViewer(musicPath))
        {
            OnOpenViewerRequested(musicPath, forceGraphicViewer: false, forceTextViewer: false, forceVgmPlayer: true);
        }
    }

    /// <summary>
    /// ビューアを閉じる。見ていたファイルをファイル一覧で選択し、サウンドビューアで移動したフォルダーをパネルへ反映する。
    /// </summary>
    private async void OnCloseViewerRequested()
    {
        // 閉じる前に、サウンドビューアの表示フォルダーと選択、テキスト・画像ビューアのファイルを控える。
        string? musicDirectory = null, musicSelection = null;
        string? viewedFilePath = null;
        if (MainViewerHost.CurrentViewer is MirrorFlux.Controls.MusicPlayerControl music)
        {
            musicDirectory = music.BrowsePath; musicSelection = music.SelectedBrowsePath;
        }
        if (MainViewerHost.CurrentViewer is TextViewerControl text)
        {
            viewedFilePath = text.FilePath;
        }
        else if (MainViewerHost.CurrentViewer is GraphicViewerControl gv)
        {
            viewedFilePath = gv.FilePath;
        }
        // 見ていたファイルをファイル一覧で選択してから閉じる。
        if (!string.IsNullOrWhiteSpace(viewedFilePath))
        {
            _viewModel.SelectViewedFile(viewedFilePath);
        }
        MainViewerHost.CloseViewer();
        // サウンドビューアで表示していたフォルダーをパネルに開き直す。
        if (!string.IsNullOrWhiteSpace(musicDirectory))
        {
            try { await _viewModel.RestoreMusicBrowsePathAsync(musicDirectory, musicSelection); }
            catch (Exception ex) { _viewModel.StatusMessage = "OPEN FAILED: " + ex.Message; }
        }
        FocusActiveList();
    }

    /// <summary>
    /// サウンドビューアの曲リストで表示するドライブを、ドライブ選択ダイアログで選ばせる。
    /// </summary>
    private void OpenMusicDriveMenu(MirrorFlux.Controls.MusicPlayerControl viewer)
    {
        try
        {
            // ドライブ一覧を作り、今表示しているドライブを現在のドライブとして渡す。
            var drives = System.IO.DriveInfo.GetDrives().Select(d =>
            {
                bool ready = d.IsReady;
                return new DriveItem(char.ToUpperInvariant(d.Name[0]), d.DriveType.ToString().ToUpperInvariant(),
                    ready ? d.VolumeLabel : "NOT READY", d.Name, ready,
                    viewer.BrowsePath.StartsWith(d.Name, StringComparison.OrdinalIgnoreCase));
            }).ToArray();
            char current = viewer.BrowsePath.Length > 0 ? char.ToUpperInvariant(viewer.BrowsePath[0]) : 'C';
            // 選ばれたドライブが使えれば、そのルートへ移動する。
            var dialog = new DriveSelectDialog(drives, current);
            dialog.PositionOverPane(this, ReferenceEquals(_viewModel.ActivePane, _viewModel.LeftPane));
            if (dialog.ShowDialog() != true) return;
            if (dialog.SelectedDrive is { IsReady: true } selected) viewer.NavigateToDirectory(selected.RootPath);
        }
        catch (Exception ex) { _viewModel.StatusMessage = "OPEN FAILED: " + ex.Message; }
        viewer.Focus();
    }

    private void OnViewerFunctionKeyRequested(int keyNumber)
    {
        MainViewerHost.ExecuteFunctionKey(keyNumber);
    }

    /// <summary>
    /// テキストビューアで、同じフォルダーの前後のテキストファイルへ移る。
    /// </summary>
    private async void OnTextFileNavigationRequested(int direction)
    {
        if (MainViewerHost.CurrentViewer is TextViewerControl text)
            await _viewModel.OpenAdjacentTextFileAsync(text.FilePath, direction);
    }

    private void UpdateFunctionModifiers() => _viewModel.FunctionShiftPressed = Keyboard.Modifiers == ModifierKeys.Shift;

    /// <summary>
    /// メインウィンドウのキー入力を振り分ける。ビューア表示中はビューアへ、それ以外はキー割り当てに従ってコマンドを実行する。
    /// </summary>
    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // 読み込み中はキー入力を受け付けない。
        UpdateFunctionModifiers();
        if (_viewModel.IsLoading)
        {
            e.Handled = true;
            return;
        }
        // ダイアログ表示中はメインウィンドウ側のキー操作を止め、ダイアログを前面へ出す。
        foreach (Window owned in OwnedWindows)
        {
            if (owned.IsVisible)
            {
                owned.Activate();
                e.Handled = true;
                return;
            }
        }

        var key = e.NormalizedKey();

        // ビューア表示中のキー処理。
        if (_viewModel.IsViewerActive)
        {
            // サウンドビューアではLでドライブ選択を開く。ファンクションキーはビューア用の割り当てで処理する。
            if (key == Key.L && Keyboard.Modifiers == ModifierKeys.None && MainViewerHost.CurrentViewer is MirrorFlux.Controls.MusicPlayerControl music)
            {
                e.Handled = true; OpenMusicDriveMenu(music); return;
            }
            if (key >= Key.F1 && key <= Key.F12)
            {
                e.Handled = true;
                _viewModel.ExecuteViewerFunctionKey(key - Key.F1 + 1, Keyboard.Modifiers);
                return;
            }

            // 検索や行番号指定などの入力欄を表示中は、文字入力とEnter確定・Esc中止を優先する。
            if (MainViewerHost.IsPromptActive)
            {
                if (MainViewerHost.HandleKeyDown(e))
                {
                    e.Handled = true;
                }
                return;
            }

            // ビューア自身が処理したキーはここで終える。
            if (MainViewerHost.HandleKeyDown(e))
            {
                e.Handled = true;
                return;
            }

            // 動画ビューアのキーは閉じる操作も含めてすべて [keys.video] の割り当てに任せる。
            if (MainViewerHost.IsVideoViewer) { e.Handled = true; return; }

            // その他のビューアは Esc・Q・V・Enter で閉じる。
            if (key == Key.Escape || key == Key.Q || key == Key.V || key == Key.Return || key == Key.Enter)
            {
                e.Handled = true;
                _viewModel.CloseViewer();
                return;
            }

            return;
        }

        // 割り当てのないキーは、サウンドバー表示中ならその操作として試す。
        if (!_keyBindings.TryResolve(key, Keyboard.Modifiers, out var command))
        {
            if (MiniPlayerView.IsVisible && MiniPlayerView.HandleShortcut(key, Keyboard.Modifiers, e.IsRepeat))
                e.Handled = true;
            return;
        }

        // コマンドを実行し、パネル切り替えならフォーカスを、それ以外は選択項目の表示位置を整える。
        e.Handled = true;
        await _viewModel.ExecuteAsync(command);

        if (command.Command is AppCommand.SwitchPane or AppCommand.ActivateLeftPane or AppCommand.ActivateRightPane ||
            command.FollowingCommands?.Any(c => c.Command is AppCommand.SwitchPane or AppCommand.ActivateLeftPane or AppCommand.ActivateRightPane) == true)
        {
            FocusActiveList();
        }
        else if (!_viewModel.IsViewerActive)
        {
            ScrollActiveListIntoView();
        }
    }

    /// <summary>
    /// 左パネルをクリックしたら左パネルをアクティブにする。
    /// </summary>
    private void LeftPanel_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!ReferenceEquals(_viewModel.ActivePane, _viewModel.LeftPane))
        {
            _viewModel.ActivatePane(_viewModel.LeftPane);
            FocusActiveList();
        }
    }

    /// <summary>
    /// 右パネルをクリックしたら右パネルをアクティブにする。
    /// </summary>
    private void RightPanel_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!ReferenceEquals(_viewModel.ActivePane, _viewModel.RightPane))
        {
            _viewModel.ActivatePane(_viewModel.RightPane);
            FocusActiveList();
        }
    }

    private async void LeftSortBadge_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        await _viewModel.OpenSortMenuAsync(_viewModel.LeftPane);
    }

    private async void RightSortBadge_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        await _viewModel.OpenSortMenuAsync(_viewModel.RightPane);
    }

    private void LogLevelBadge_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _viewModel.CycleLogLevel(1);
    }

    private void LogLevelBadge_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _viewModel.CycleLogLevel(-1);
    }

    /// <summary>
    /// マウス操作で左のファイル一覧にフォーカスが移ったときだけ、左パネルをアクティブにする。
    /// </summary>
    private void LeftList_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (Mouse.LeftButton == MouseButtonState.Pressed || Mouse.RightButton == MouseButtonState.Pressed)
        {
            _viewModel.ActivatePane(_viewModel.LeftPane);
        }
    }

    /// <summary>
    /// マウス操作で右のファイル一覧にフォーカスが移ったときだけ、右パネルをアクティブにする。
    /// </summary>
    private void RightList_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (Mouse.LeftButton == MouseButtonState.Pressed || Mouse.RightButton == MouseButtonState.Pressed)
        {
            _viewModel.ActivatePane(_viewModel.RightPane);
        }
    }

    /// <summary>
    /// 選択が変わったら、その項目が見える位置までスクロールする。
    /// </summary>
    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListView listView && listView.SelectedItem is not null)
        {
            listView.ScrollIntoView(listView.SelectedItem);
        }
    }

    /// <summary>
    /// ファイル一覧の項目を左ダブルクリックしたら、拡張子に応じた実行を行う。
    /// </summary>
    private async void List_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.IsLoading || e.ChangedButton != MouseButton.Left) return;
        if (e.OriginalSource is DependencyObject dep)
        {
            var item = ItemsControl.ContainerFromElement(sender as ListView, dep) as ListViewItem;
            if (item?.DataContext is FileEntry)
            {
                await _viewModel.ExecuteAsync(new ResolvedCommand(AppCommand.ExtExec, 0));
            }
        }
    }

    /// <summary>
    /// 終了時に時計を止めて設定を保存する。保存できなかったときは理由を知らせる。
    /// </summary>
    private void Window_Closed(object? sender, EventArgs e)
    {
        _clockTimer.Stop();
        if (!_viewModel.SaveSettings(out var error) && _viewModel.CanSaveSettings && error is not null)
        {
            MessageBox.Show(error, "MirrorFlux", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    /// <summary>
    /// アクティブなパネルのファイル一覧にフォーカスし、選択中の項目にもフォーカスを置く。
    /// </summary>
    public void FocusActiveList()
    {
        var targetList = ReferenceEquals(_viewModel.ActivePane, _viewModel.LeftPane) ? LeftList : RightList;
        targetList.Focus();
        if (targetList.SelectedItem is not null &&
            targetList.ItemContainerGenerator.ContainerFromItem(targetList.SelectedItem) is ListViewItem item)
        {
            item.Focus();
        }
        ScrollActiveListIntoView();
    }

    /// <summary>
    /// パネルのファイル一覧の左上の位置を、ウィンドウ内の座標で返す。ダイアログの配置に使う。
    /// </summary>
    public Point GetPaneTopLeft(bool isLeftPane)
    {
        var target = isLeftPane ? (FrameworkElement)LeftList : (FrameworkElement)RightList;
        return target.TranslatePoint(new Point(0, 0), this);
    }

    public double GetPaneActualWidth(bool isLeftPane)
    {
        var target = isLeftPane ? (FrameworkElement)LeftList : (FrameworkElement)RightList;
        return target.ActualWidth;
    }

    /// <summary>
    /// アクティブなパネルの選択中の項目が見える位置までスクロールする。
    /// </summary>
    private void ScrollActiveListIntoView()
    {
        var targetList = ReferenceEquals(_viewModel.ActivePane, _viewModel.LeftPane) ? LeftList : RightList;
        if (targetList.SelectedItem is not null)
        {
            targetList.ScrollIntoView(targetList.SelectedItem);
        }
    }
}
