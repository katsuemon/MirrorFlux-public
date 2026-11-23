using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using MirrorFlux.Input;
using MirrorFlux.Models;
using MirrorFlux.Services;
using MirrorFlux.Services.Music;

namespace MirrorFlux.ViewModels;

/// <summary>
/// メイン画面のビューモデル。左右のパネル、ログ、ファンクションキー、Loading表示、
/// 設定の保存、キーから起動したコマンドの振り分けを受け持つ。
/// ファイル操作やビューアなどは機能ごとの partial ファイルに分けてある。
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly SettingsStore _settingsStore;
    private readonly AppSettings _settings;
    private readonly string? _startupWarning;
    private string _statusMessage = "準備完了";
    private string _clockText = DateTime.Now.ToString("yyyy-MM-dd ddd HH:mm:ss", CultureInfo.CurrentCulture);
    private FilePaneViewModel _activePane;

    /// <summary>
    /// 設定を読み込み、テーマ・左右のパネル・並べ替え・コマンドを準備する。
    /// </summary>
    public MainViewModel(SettingsStore settingsStore)
    {
        // 設定を読み込む。保存できない場合や警告も控えておく。
        _settingsStore = settingsStore;
        var loaded = settingsStore.Load();
        _settings = loaded.Settings;
        CanSaveSettings = loaded.CanSave;
        _startupWarning = loaded.Warning;

        // 設定されたテーマ・カラーパレットを適用する。
        SettingsStore.ApplyTheme(_settings);

        var initialSort = _settings.SortMode.ToLowerInvariant() switch
        {
            "size" => PaneSortMode.Size,
            "date" => PaneSortMode.Date,
            "type" => PaneSortMode.Type,
            _ => PaneSortMode.Name
        };

        // 左右のパネルを作り、それぞれの並べ替え設定を適用する。個別の設定がなければ共通の設定を使う。
        var leftSortRaw = string.IsNullOrWhiteSpace(_settings.LeftSortMode) ? _settings.SortMode : _settings.LeftSortMode;
        var rightSortRaw = string.IsNullOrWhiteSpace(_settings.RightSortMode) ? _settings.SortMode : _settings.RightSortMode;
        var (leftSortMode, leftSortAsc, leftDirsFirst) = FilePaneViewModel.ParseSortSetting(leftSortRaw);
        var (rightSortMode, rightSortAsc, rightDirsFirst) = FilePaneViewModel.ParseSortSetting(rightSortRaw);
        LeftPane = new FilePaneViewModel("左") { IsActive = true, ShowHidden = _settings.ShowHidden };
        LeftPane.SetSort(leftSortMode, leftSortAsc, leftDirsFirst);
        RightPane = new FilePaneViewModel("右") { ShowHidden = _settings.ShowHidden };
        RightPane.SetSort(rightSortMode, rightSortAsc, rightDirsFirst);
        _activePane = LeftPane;

        // マークが変わったらプロンプトの表示を更新する。
        LeftPane.MarkedChanged += () => OnPropertyChanged(nameof(PromptBadgeText));
        RightPane.MarkedChanged += () => OnPropertyChanged(nameof(PromptBadgeText));

        FunctionKeyCommand = new RelayCommand<int>(key => _ = ExecuteFunctionKeyAsync(key));

        // 起動と設定ファイルの場所をログへ残す。
        AddLog("アプリケーションを起動しました", "INFO");
        AddLog($"設定ファイル: {_settingsStore.SettingsPath}", "INIT");
        if (_startupWarning is not null)
        {
            AddLog(_startupWarning, "WARN");
        }
        StatusMessage = "準備完了";

        // CPU使用率は前回との差で求めるので、最初の基準値をここで取っておく。
        UpdateCpuUsage();
    }

    /// <summary>
    /// GetSystemTimes で受け取る64ビットの時間値。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public uint DateTimeLow;
        public uint DateTimeHigh;

        public ulong ToUInt64() => ((ulong)DateTimeHigh << 32) | DateTimeLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FILETIME idleTime, out FILETIME kernelTime, out FILETIME userTime);

    /// <summary>
    /// GlobalMemoryStatusEx で受け取るメモリの状態。
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    private ulong _prevIdleTime;
    private ulong _prevKernelTime;
    private ulong _prevUserTime;
    private double _cpuUsage;

    public string CpuUsageText => $"[ CPU:{_cpuUsage,5:F1}%  FREE-MEM: {MemoryFreeText} ]";

    /// <summary>
    /// プロンプトに出す操作中のパネルのドライブ（例: C:¥ ）。
    /// </summary>
    public string PromptDriveText
    {
        get
        {
            try
            {
                var root = Path.GetPathRoot(ActivePane.CurrentPath);
                if (!string.IsNullOrEmpty(root))
                {
                    return $"{root.TrimEnd('\\')}¥ ";
                }
            }
            catch
            {
            }
            return "C:¥ ";
        }
    }

    public string PromptBadgeText => $"[X{ActivePane.MarkedSectors:D5}/0000.00]";

    public AppSettings Settings => _settings;
    public FilePaneViewModel LeftPane { get; }
    public FilePaneViewModel RightPane { get; }
    public FilePaneViewModel ActivePane => _activePane;
    public bool CanSaveSettings { get; }
    public ObservableCollection<LogMessage> Logs { get; } = [];
    public ICommand FunctionKeyCommand { get; }

    private bool _isViewerActive;
    private bool _isGraphicViewer;
    private bool _isPdfViewer;
    private bool _isVideoViewer;
    private bool _isMusicPlayerViewer;
    private string _viewerStatusText = string.Empty;
    private string _viewerLocationText = string.Empty;

    /// <summary>
    /// ビューアを表示中かどうか。変わったらファンクションキーの表示を切り替える。
    /// </summary>
    public bool IsViewerActive
    {
        get => _isViewerActive;
        set
        {
            if (SetProperty(ref _isViewerActive, value))
            {
                RefreshFunctionKeyLabels();
            }
        }
    }

    /// <summary>
    /// 画像ビューアを表示中かどうか。変わったらファンクションキーの表示を切り替える。
    /// </summary>
    public bool IsGraphicViewer
    {
        get => _isGraphicViewer;
        set
        {
            if (SetProperty(ref _isGraphicViewer, value))
            {
                RefreshFunctionKeyLabels();
            }
        }
    }

    /// <summary>
    /// 動画ビューアを表示中かどうか。変わったらファンクションキーの表示を切り替える。
    /// </summary>
    public bool IsVideoViewer
    {
        get => _isVideoViewer;
        set { if (SetProperty(ref _isVideoViewer, value)) RefreshFunctionKeyLabels(); }
    }

    /// <summary>
    /// PDFビューアを表示中かどうか。変わったらファンクションキーの表示を切り替える。
    /// </summary>
    public bool IsPdfViewer
    {
        get => _isPdfViewer;
        set
        {
            if (SetProperty(ref _isPdfViewer, value))
            {
                RefreshFunctionKeyLabels();
            }
        }
    }

    /// <summary>
    /// 音楽プレイヤーの画面を表示中かどうか。変わったらファンクションキーの表示を切り替える。
    /// </summary>
    public bool IsMusicPlayerViewer
    {
        get => _isMusicPlayerViewer;
        set
        {
            if (SetProperty(ref _isMusicPlayerViewer, value))
            {
                RefreshFunctionKeyLabels();
            }
        }
    }

    public string ViewerStatusText
    {
        get => _viewerStatusText;
        set => SetProperty(ref _viewerStatusText, value);
    }

    public string ViewerLocationText
    {
        get => _viewerLocationText;
        set => SetProperty(ref _viewerLocationText, value);
    }

    public event Action<string, bool, bool, bool, bool, bool, bool>? OpenViewerRequested;
    public event Action? CloseViewerRequested;
    public event Action<int>? ViewerFunctionKeyRequested;

    private bool _functionShiftPressed, _isHexViewer;
    /// <summary>
    /// Shiftを押している間はShift付きのファンクションキーの表示に切り替える。
    /// </summary>
    public bool FunctionShiftPressed
    {
        get => _functionShiftPressed;
        set { if (SetProperty(ref _functionShiftPressed, value)) RefreshFunctionKeyLabels(); }
    }
    /// <summary>
    /// バイナリビューアを表示中かどうか。変わったらファンクションキーの表示を切り替える。
    /// </summary>
    public bool IsHexViewer
    {
        get => _isHexViewer;
        set { if (SetProperty(ref _isHexViewer, value)) RefreshFunctionKeyLabels(); }
    }
    public event Action<int>? ViewerShiftFunctionKeyRequested;

    /// <summary>
    /// Shiftを押している間のファイル一覧のファンクションキーの表示を返す。
    /// Shiftを押していないか、Shift付きのファンクションキーが一つも設定されていなければ null を返す。
    /// </summary>
    private string? ShiftFunctionLabel(int number)
    {
        if (!FunctionShiftPressed) return null;
        if (!_settings.KeyBindings.Any(b => b.Modifiers == ModifierKeys.Shift && b.Key >= Key.F1 && b.Key <= Key.F12)) return null;
        return FunctionKeyBindings.TryResolve(_settings.KeyBindings, number, ModifierKeys.Shift, out var command)
            ? FunctionKeyBindings.Label(command.Command) : "";
    }

    /// <summary>
    /// ファイル一覧のファンクションキーの表示を返す。
    /// 既定のコマンドのままならINIの表示名を、別のコマンドに変えていればそのコマンド名を使う。
    /// </summary>
    private string FileFunctionLabel(int number, string configuredLabel)
    {
        FunctionKeyBindings.TryResolve(_settings.KeyBindings, number, ModifierKeys.None, out var command);
        var original = FunctionKeyBindings.CreateDefaults().ElementAt(number - 1).Command;
        if (command.Command == AppCommand.None) return "";
        return command.Command == original ? configuredLabel : FunctionKeyBindings.Label(command.Command);
    }

    /// <summary>
    /// ファンクションキーの表示をすべて更新する。
    /// </summary>
    public void RefreshFunctionKeyLabels()
    {
        // Fn01Label〜Fn12Label（ボタンの表示に結び付ける12個のプロパティ）を通知する。
        for (int number = 1; number <= 12; number++) OnPropertyChanged($"Fn{number:00}Label");
    }

    private string CurrentViewerContext => IsVideoViewer ? "video" : IsMusicPlayerViewer ? "music" : IsPdfViewer ? "pdf" : IsGraphicViewer ? "graphic" : IsHexViewer ? "hex" : "text";

    /// <summary>
    /// 指定番号のファンクションキーの表示を、ファイル一覧か表示中のビューアに合わせて返す。
    /// </summary>
    private string FunctionLabel(int number)
    {
        // ファイル一覧ではファイル一覧用の表示を使う。
        if (!IsViewerActive) return ShiftFunctionLabel(number) ?? FileFunctionLabel(number, _settings.FileFunctionLabels[number - 1]);
        // ビューアではキー設定から割り当てられた動作を探す。
        var action = ViewerFunctionBindings.Resolve(_settings.ViewerKeyBindings, CurrentViewerContext, number,
            FunctionShiftPressed ? ModifierKeys.Shift : ModifierKeys.None);
        if (action == null || action.Number == 0) return "";
        // Shift付き・PDF・動画、バイナリビューアのF6/F7は動作自体の表示名を使う。
        if (action.Shift || IsPdfViewer || IsVideoViewer || action.Number > 12) return action.Label;
        if (IsHexViewer && action.Number is 6 or 7) return action.Label;
        // それ以外はビューアの種類ごとのINIの表示名を使う。
        string[] labels = IsMusicPlayerViewer ? _settings.MusicFunctionLabels
            : IsGraphicViewer ? _settings.GraphicViewerFunctionLabels
            : _settings.ViewerFunctionLabels;
        return labels[action.Number - 1];
    }

    /// <summary>
    /// ビューア表示中のファンクションキーを、キー設定に従ってビューアへ伝える。
    /// </summary>
    public void ExecuteViewerFunctionKey(int number, ModifierKeys modifiers)
    {
        if (IsLoading || !IsViewerActive || number is < 1 or > 12) return;
        var action = ViewerFunctionBindings.Resolve(_settings.ViewerKeyBindings, CurrentViewerContext, number, modifiers);
        if (action == null || action.Number == 0) return;
        if (action.Shift) ViewerShiftFunctionKeyRequested?.Invoke(action.Number);
        else ViewerFunctionKeyRequested?.Invoke(action.Number);
    }

    public string Fn01Label => FunctionLabel(1);
    public string Fn02Label => FunctionLabel(2);
    public string Fn03Label => FunctionLabel(3);
    public string Fn04Label => FunctionLabel(4);
    public string Fn05Label => FunctionLabel(5);
    public string Fn06Label => FunctionLabel(6);
    public string Fn07Label => FunctionLabel(7);
    public string Fn08Label => FunctionLabel(8);
    public string Fn09Label => FunctionLabel(9);
    public string Fn10Label => FunctionLabel(10);
    public string Fn11Label => FunctionLabel(11);
    public string Fn12Label => FunctionLabel(12);

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    public string ClockText
    {
        get => _clockText;
        set => SetProperty(ref _clockText, value);
    }

    /// <summary>
    /// 空き物理メモリを、大きさに合わせてTB・GB・MB・KBで表示する。取得できなければ --- を返す。
    /// </summary>
    public string MemoryFreeText
    {
        get
        {
            try
            {
                var memStatus = new MEMORYSTATUSEX();
                memStatus.dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>();
                if (GlobalMemoryStatusEx(ref memStatus))
                {
                    ulong freeBytes = memStatus.ullAvailPhys;
                    if (freeBytes >= 1024UL * 1024UL * 1024UL * 1024UL)
                    {
                        return $"{freeBytes / (1024.0 * 1024.0 * 1024.0 * 1024.0):F1}TB";
                    }
                    if (freeBytes >= 1024UL * 1024UL * 1024UL)
                    {
                        return $"{freeBytes / (1024.0 * 1024.0 * 1024.0):F1}GB";
                    }
                    if (freeBytes >= 1024UL * 1024UL)
                    {
                        return $"{freeBytes / (1024.0 * 1024.0):F0}MB";
                    }
                    return $"{freeBytes / 1024.0:F0}KB";
                }
                return "---";
            }
            catch
            {
                return "---";
            }
        }
    }

    private readonly List<LogMessage> _allLogs = [];
    private bool _isLoading;
    private bool _isLoadingOverlayVisible;
    private string _loadingMessage = "Loading...";
    private string _loadingTitle = "NOW LOADING...";
    private int _loadingDepth;
    private CancellationTokenSource? _loadingOverlayCts;
    public const int LoadingOverlayDelayMs = 500;

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    public bool IsLoadingOverlayVisible
    {
        get => _isLoadingOverlayVisible;
        private set => SetProperty(ref _isLoadingOverlayVisible, value);
    }

    public string LoadingTitle
    {
        get => _loadingTitle;
        private set => SetProperty(ref _loadingTitle, value);
    }

    public string LoadingMessage
    {
        get => _loadingMessage;
        private set => SetProperty(ref _loadingMessage, value);
    }

    /// <summary>
    /// ログの表示レベル（0〜3）。変わったら表示するログを選び直す。
    /// </summary>
    public int LogLevel
    {
        get => _settings.LogLevel;
        set
        {
            int clamped = Math.Clamp(value, 0, 3);
            if (_settings.LogLevel != clamped)
            {
                _settings.LogLevel = clamped;
                OnPropertyChanged(nameof(LogLevel));
                OnPropertyChanged(nameof(LogLevelBadgeText));
                RefreshVisibleLogs();
            }
        }
    }

    public static string GetLogLevelName(int level) => level switch
    {
        0 => "最小",
        1 => "要所",
        2 => "標準",
        3 => "詳細",
        _ => "標準"
    };

    public static string GetLogLevelDescription(int level) => level switch
    {
        0 => "エラー・警告のみ",
        1 => "主要操作・結果サマリー",
        2 => "標準・ナビゲーション",
        3 => "詳細・すべて表示",
        _ => "標準"
    };

    public string LogLevelBadgeText => $"[Lv{LogLevel}:{GetLogLevelName(LogLevel)}]";

    /// <summary>
    /// ログの表示レベルを順に切り替え、変更をログへ残す。
    /// </summary>
    public void CycleLogLevel(int delta = 1)
    {
        int next = ((LogLevel + delta) % 4 + 4) % 4;
        LogLevel = next;
        string desc = $"ログ表示レベルを Lv{next} ({GetLogLevelDescription(next)}) に変更しました";
        AddLog(desc, "CONF");
        StatusMessage = desc;
    }

    /// <summary>
    /// ログの種類と内容から、そのログを表示する最小の表示レベルを決める。
    /// </summary>
    public static int DetermineLogVerbosity(string level, string message)
    {
        return level switch
        {
            // レベル0: エラー・警告と、利用者が求めたヘルプ・設定の表示。
            "ERR" or "WARN" or "HELP" or "CONF" => 0,

            // レベル1: 起動・終了・操作完了のサマリー・中止・ドライブ切り替え・ビューア・単発のリネームとフォルダー作成。
            "INIT" or "QUIT" or "DONE" or "CANCEL" or "DRIVE" or "VIEW" or "EDIT" or "RENAME" or "MKDIR" => 1,

            // レベル3: 個々のマーク切り替えとスキップの詳細。
            "MARK" or "SKIP" => 3,

            // レベル2: 個々のファイルのコピー・移動・削除と並べ替え。
            "COPY" or "MOVE" or "DEL" or "SORT" => 2,

            // ACT: パネル切り替えはレベル3、アーカイブ・サウンドバー・関連付け実行はレベル1、ほかはレベル2。
            "ACT" => message.StartsWith("パネル切替:", StringComparison.Ordinal) ? 3
                   : (message.StartsWith("アーカイブ", StringComparison.Ordinal) ||
                      message.StartsWith("[アーカイブ", StringComparison.Ordinal) ||
                      message.StartsWith("[サウンドバー", StringComparison.Ordinal) ||
                      message.StartsWith("関連付け実行:", StringComparison.Ordinal)) ? 1
                   : 2,

            // INFO: ファイル選択はレベル3、起動メッセージなどはレベル1。
            "INFO" => message.StartsWith("ファイル選択:", StringComparison.Ordinal) ? 3 : 1,

            _ => 2
        };
    }

    /// <summary>
    /// 保持しているすべてのログから、今の表示レベルで見せるものだけを表示し直す。
    /// </summary>
    private void RefreshVisibleLogs()
    {
        Logs.Clear();
        int currentLevel = LogLevel;
        foreach (var item in _allLogs)
        {
            if (item.VerbosityLevel <= currentLevel)
            {
                Logs.Add(item);
            }
        }
        OnPropertyChanged(nameof(LogCountText));
    }

    /// <summary>
    /// Loading表示を出しながら処理を実行する。入れ子で呼ばれても、いちばん外側が終わるまで表示を保つ。
    /// 待ちの画面は短い処理でちらつかないよう、一定時間たってから出す。
    /// </summary>
    public async Task RunWithLoadingAsync(string loadingText, Func<Task> action, string loadingTitle = "NOW LOADING...")
    {
        // 元の表示を控えてから、Loading中の表示に切り替える。
        string previousTitle = LoadingTitle;
        string previousMessage = LoadingMessage;
        LoadingTitle = loadingTitle;
        _loadingDepth++;
        LoadingMessage = loadingText;
        IsLoading = true;
        string prevStatus = StatusMessage;
        StatusMessage = loadingText;

        // いちばん外側の呼び出しでだけ、遅れて出る待ちの画面を予約する。
        if (_loadingDepth == 1)
        {
            _loadingOverlayCts?.Cancel();
            _loadingOverlayCts?.Dispose();
            var cts = new CancellationTokenSource();
            _loadingOverlayCts = cts;
            _ = ShowLoadingOverlayAfterDelayAsync(cts.Token);
        }

        try
        {
            await action();
        }
        finally
        {
            // 表示を戻す。いちばん外側が終わったら待ちの画面を取り消し、状態表示も元に戻す。
            // 処理中に状態表示が書き換えられていれば、その内容を残す。
            LoadingTitle = previousTitle;
            LoadingMessage = previousMessage;
            _loadingDepth = Math.Max(0, _loadingDepth - 1);
            if (_loadingDepth == 0)
            {
                _loadingOverlayCts?.Cancel();
                _loadingOverlayCts?.Dispose();
                _loadingOverlayCts = null;
                IsLoadingOverlayVisible = false;
                IsLoading = false;
                if (StatusMessage == loadingText)
                {
                    StatusMessage = prevStatus;
                }
            }
        }
    }

    /// <summary>
    /// 一定時間待っても処理が続いていれば、待ちの画面を出す。表示の変更はUIスレッドで行う。
    /// </summary>
    private async Task ShowLoadingOverlayAfterDelayAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(LoadingOverlayDelayMs, token);
            if (!token.IsCancellationRequested && _loadingDepth > 0)
            {
                // UIスレッド以外から来たときは、UIスレッドで改めて確かめてから表示する。
                if (Application.Current?.Dispatcher != null && !Application.Current.Dispatcher.CheckAccess())
                {
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        if (!token.IsCancellationRequested && _loadingDepth > 0)
                            IsLoadingOverlayVisible = true;
                    }, System.Windows.Threading.DispatcherPriority.Render);
                }
                else
                {
                    IsLoadingOverlayVisible = true;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public string LogCountText => $"{Logs.Count} 件";

    public string ActivePaneText => ReferenceEquals(ActivePane, LeftPane) ? "[左パネル]" : "[右パネル]";

    /// <summary>
    /// ログを1件追加する。複数行は行ごとに分け、古いログは300件を超えた分から捨てる。
    /// ファイル操作は別スレッドから呼ぶことがあるので、UIスレッドへ回して追加する。
    /// </summary>
    public void AddLog(string message, string level = "INFO")
    {
        // UIスレッド以外から呼ばれたら、UIスレッドで呼び直す。
        if (Application.Current?.Dispatcher != null && !Application.Current.Dispatcher.CheckAccess())
        {
            Application.Current.Dispatcher.Invoke(() => AddLog(message, level));
            return;
        }

        if (string.IsNullOrEmpty(message)) return;

        int verbosity = DetermineLogVerbosity(level, message);

        // エラー・警告で [原因: ...] が同じ行にあるときは、次の行に分けて出す。
        if ((level == "ERR" || level == "WARN") && !message.Contains('\n') && message.Contains(" [原因: "))
        {
            message = message.Replace(" [原因: ", "\n  [原因: ");
        }

        var lines = message.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        var time = DateTime.Now.ToString("HH:mm:ss", CultureInfo.CurrentCulture);

        // 行ごとに保持し、今の表示レベルで見せる行だけ表示にも加える。
        foreach (var line in lines)
        {
            var entry = new LogMessage(time, level, line, verbosity);
            _allLogs.Add(entry);
            if (verbosity <= LogLevel)
            {
                Logs.Add(entry);
            }
        }

        // 保持と表示をそれぞれ300件までに抑える。
        while (_allLogs.Count > 300)
        {
            var removed = _allLogs[0];
            _allLogs.RemoveAt(0);
            if (Logs.Count > 0 && ReferenceEquals(Logs[0], removed))
            {
                Logs.RemoveAt(0);
            }
        }
        while (Logs.Count > 300)
        {
            Logs.RemoveAt(0);
        }
        OnPropertyChanged(nameof(LogCountText));
    }

    private const string DefaultFallbackPath = @"C:\";

    /// <summary>
    /// 起動時に、設定された左右のパネルのフォルダーを読み込む。
    /// </summary>
    public async Task InitializeAsync()
    {
        await RunWithLoadingAsync("起動時パスを読み込み中...", async () =>
        {
            await LoadStartupPaneAsync(LeftPane, _settings.LeftPath);
            await LoadStartupPaneAsync(RightPane, _settings.RightPath);
        });
        AddLog($"左パネル: {LeftPane.CurrentPath} ({LeftPane.Entries.Count}項目)", "INIT");
        AddLog($"右パネル: {RightPane.CurrentPath} ({RightPane.Entries.Count}項目)", "INIT");
    }

    /// <summary>
    /// 起動時のフォルダーを読み込む。見つからないか読み込めなければ C:\ を表示する。
    /// </summary>
    private async Task LoadStartupPaneAsync(FilePaneViewModel pane, string? configuredPath)
    {
        var targetPath = string.IsNullOrWhiteSpace(configuredPath) ? DefaultFallbackPath : configuredPath.Trim();

        // フォルダー、またはアーカイブ内なら元のアーカイブファイルが存在するかを調べる。
        bool exists = await Task.Run(() => ArchiveService.TrySplitArchivePath(targetPath, out var archiveFilePath, out _)
            ? File.Exists(archiveFilePath)
            : Directory.Exists(targetPath));

        // 見つからなければ C:\ に切り替える。
        if (!exists)
        {
            if (!string.Equals(targetPath, DefaultFallbackPath, StringComparison.OrdinalIgnoreCase))
            {
                AddLog($"起動時パスが見つからないため {DefaultFallbackPath} を表示します ({pane.Title}): {targetPath}", "WARN");
            }
            targetPath = DefaultFallbackPath;
        }

        try
        {
            await pane.LoadAsync(targetPath);
            UpdateSelectionStatus();
            OnPropertyChanged(nameof(PromptDriveText));
        }
        catch (Exception exception)
        {
            // 読み込めなければ C:\ で読み直す。C:\ でも失敗したらエラーを出す。
            if (!string.Equals(targetPath, DefaultFallbackPath, StringComparison.OrdinalIgnoreCase))
            {
                AddLog($"起動時パスを読み込めないため {DefaultFallbackPath} を表示します ({pane.Title}): {targetPath} ({exception.Message})", "WARN");
                await LoadPaneAsync(pane, DefaultFallbackPath);
                return;
            }

            var msg = $"一覧を読み込めません ({pane.Title}): {exception.Message}";
            StatusMessage = msg;
            AddLog(msg, "ERR");
        }
    }

    /// <summary>
    /// 時計とCPU使用率の表示を更新する。
    /// </summary>
    public void UpdateClock()
    {
        ClockText = DateTime.Now.ToString("yyyy/MM/dd ddd. HH:mm:ss", CultureInfo.InvariantCulture);
        UpdateCpuUsage();
    }

    /// <summary>
    /// 前回からのアイドル時間と稼働時間の差からCPU使用率を求める。
    /// </summary>
    public void UpdateCpuUsage()
    {
        if (GetSystemTimes(out var idleTime, out var kernelTime, out var userTime))
        {
            ulong idle = idleTime.ToUInt64();
            ulong kernel = kernelTime.ToUInt64();
            ulong user = userTime.ToUInt64();

            // 初回は基準値を取るだけにする。カーネル時間にはアイドル時間が含まれるので、差し引いて使用率を出す。
            if (_prevKernelTime != 0 || _prevUserTime != 0)
            {
                ulong diffIdle = idle - _prevIdleTime;
                ulong diffKernel = kernel - _prevKernelTime;
                ulong diffUser = user - _prevUserTime;
                ulong total = diffKernel + diffUser;

                if (total > 0 && total >= diffIdle)
                {
                    ulong busy = total - diffIdle;
                    _cpuUsage = Math.Clamp((double)busy * 100.0 / total, 0.0, 100.0);
                }
            }

            // 次回のために今回の値を覚える。
            _prevIdleTime = idle;
            _prevKernelTime = kernel;
            _prevUserTime = user;
        }

        OnPropertyChanged(nameof(CpuUsageText));
    }

    /// <summary>
    /// 操作中のパネルを左右で入れ替える。
    /// </summary>
    public void SwitchPane()
    {
        SetActivePane(ReferenceEquals(ActivePane, LeftPane) ? RightPane : LeftPane);
        StatusMessage = $"{ActivePane.Title}パネルを選択中";
        AddLog($"パネル切替: {ActivePane.Title}パネル ({ActivePane.CurrentPath})", "ACT");
    }

    /// <summary>
    /// 左のパネルを操作中のパネルにする。
    /// </summary>
    public void ActivateLeftPane()
    {
        if (!ReferenceEquals(ActivePane, LeftPane))
        {
            SetActivePane(LeftPane);
            StatusMessage = $"{ActivePane.Title}パネルを選択中";
            AddLog($"パネル切替: {ActivePane.Title}パネル ({ActivePane.CurrentPath})", "ACT");
        }
    }

    /// <summary>
    /// 右のパネルを操作中のパネルにする。
    /// </summary>
    public void ActivateRightPane()
    {
        if (!ReferenceEquals(ActivePane, RightPane))
        {
            SetActivePane(RightPane);
            StatusMessage = $"{ActivePane.Title}パネルを選択中";
            AddLog($"パネル切替: {ActivePane.Title}パネル ({ActivePane.CurrentPath})", "ACT");
        }
    }

    /// <summary>
    /// 指定したパネルを操作中のパネルにする。
    /// </summary>
    public void ActivatePane(FilePaneViewModel pane)
    {
        if (!ReferenceEquals(_activePane, pane))
        {
            SetActivePane(pane);
            AddLog($"パネル切替: {pane.Title}パネル", "ACT");
        }
    }

    /// <summary>
    /// 反対側のパネルを、操作中のパネルと同じフォルダーへ移動する。
    /// </summary>
    public async Task SyncPanePathAsync()
    {
        var targetPane = ReferenceEquals(ActivePane, LeftPane) ? RightPane : LeftPane;
        var targetPath = ActivePane.CurrentPath;
        if (string.IsNullOrWhiteSpace(targetPath)) return;

        // 移動前の位置をドライブごとに覚えてから移動する。
        targetPane.RememberCurrentDriveState();
        AddLog($"反対側のパスを同期: {targetPath}", "ACT");
        await LoadPaneAsync(targetPane, targetPath);
        StatusMessage = $"反対側のパスを同期: {targetPath}";
    }

    /// <summary>
    /// キーやボタンから起動したコマンドを実行する。
    /// 例外でアプリ全体を終了させないよう、内容をログへ出してその操作だけを中止する。
    /// </summary>
    public async Task ExecuteAsync(ResolvedCommand resolved)
    {
        try
        {
            await ExecuteCoreAsync(resolved);
            if (resolved.FollowingCommands is { } following)
                foreach (var next in following)
                    await ExecuteCoreAsync(next with { Modifiers = resolved.Modifiers });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            StatusMessage = $"操作を完了できませんでした: {ex.Message}";
            AddLog($"操作を完了できませんでした ({resolved.Command}): {ex.Message}", "ERR");
        }
    }

    /// <summary>
    /// コマンドの種類ごとに対応する処理へ振り分ける。Loading中は受け付けない。
    /// </summary>
    private async Task ExecuteCoreAsync(ResolvedCommand resolved)
    {
        if (IsLoading) return;

        switch (resolved.Command)
        {
            // 外部アプリの起動とヘルプ。
            case AppCommand.ExternalProgram:
                await ExecuteExternalProgramAsync(resolved.ExternalCommand);
                break;
            case AppCommand.Help:
                AddLog($"[ヘルプ] [↑↓]選択 [←→]外向きは親へ/内向きは切替 [Tab]左右切替 [Enter]開く [BS]親へ [Space]マーク [Ctrl+R/F11]更新 [F1-F12]機能", "HELP");
                if (MirrorFlux.Services.Music.MusicPlaybackService.Instance.IsPlaying)
                    AddLog("[サウンドバー] Ctrl+P 一時停止/再開  Ctrl+S 停止  Ctrl+←/→ 5秒シーク  Ctrl+↑/↓ 曲番号  Ctrl+M サウンドビューア", "HELP");
                StatusMessage = string.Join(" ", Enumerable.Range(1, 12).Select(number => $"F{number}:{FunctionLabel(number)}"));
                break;
            case AppCommand.View:
                await OpenViewerAsync();
                break;
            // カーソル移動。上下は端で反対側へ回り込む。
            case AppCommand.MoveUp:
                ActivePane.MoveSelection(-1, wrap: true);
                UpdateSelectionStatus();
                break;
            case AppCommand.MoveDown:
                ActivePane.MoveSelection(1, wrap: true);
                UpdateSelectionStatus();
                break;
            case AppCommand.MovePageUp:
                ActivePane.MoveSelection(-Math.Max(1, VisibleRows - 1));
                UpdateSelectionStatus();
                break;
            case AppCommand.MovePageDown:
                ActivePane.MoveSelection(Math.Max(1, VisibleRows - 1));
                UpdateSelectionStatus();
                break;
            case AppCommand.MoveHome:
                ActivePane.SelectBoundary(false);
                UpdateSelectionStatus();
                break;
            case AppCommand.MoveEnd:
                ActivePane.SelectBoundary(true);
                UpdateSelectionStatus();
                break;
            // パネルの切り替えとフォルダーの移動。
            case AppCommand.SwitchPane:
                SwitchPane();
                break;
            case AppCommand.ParentLeft:
                if (ReferenceEquals(ActivePane, LeftPane)) await GoParentAsync();
                break;
            case AppCommand.ParentRight:
                if (ReferenceEquals(ActivePane, RightPane)) await GoParentAsync();
                break;
            case AppCommand.ActivateLeftPane:
                ActivateLeftPane();
                break;
            case AppCommand.ActivateRightPane:
                ActivateRightPane();
                break;
            case AppCommand.OpenSelected:
                await OpenSelectedAsync();
                break;
            case AppCommand.GoParent:
                await GoParentAsync();
                break;
            // マーク・絞り込み・アーカイブ作成・再読み込み。
            case AppCommand.ToggleMark:
                ToggleMark();
                break;
            case AppCommand.MarkAll:
                MarkAllVisible();
                break;
            case AppCommand.MarkClear:
                ActivePane.UnmarkAll();
                StatusMessage = "マークをすべて解除しました";
                break;
            case AppCommand.IncrementalSearch:
                OpenIncrementalSearch();
                break;
            case AppCommand.Archive:
                await CreateArchiveFromMarksAsync();
                break;
            case AppCommand.Refresh:
                AddLog($"一覧を再読込しました: {ActivePane.CurrentPath}", "ACT");
                await LoadPaneAsync(ActivePane, ActivePane.CurrentPath);
                break;
            case AppCommand.FunctionKey:
                await ExecuteFunctionKeyAsync(resolved.Argument);
                break;
            // コピーと移動。Check 付きは確認ダイアログを出す。
            case AppCommand.FileCopy:
                await CopySelectedOrMarkedAsync(confirmBeforeCopy: false);
                break;
            case AppCommand.FileCopyCheck:
                await CopySelectedOrMarkedAsync(confirmBeforeCopy: true);
                break;
            case AppCommand.FileMove:
                await MoveSelectedOrMarkedAsync(confirmBeforeMove: false);
                break;
            case AppCommand.FileMoveCheck:
                await MoveSelectedOrMarkedAsync(confirmBeforeMove: true);
                break;
            // 終了とダイアログを開く操作。
            case AppCommand.Exit:
                Application.Current.MainWindow?.Close();
                break;
            case AppCommand.DriveMenu:
                await OpenDriveMenuAsync();
                break;
            case AppCommand.JumpList:
                await OpenJumpListAsync();
                break;
            case AppCommand.ChangeDirectory:
                await ChangeDirectoryAsync();
                break;
            case AppCommand.OperationCancel:
                RequestCancelFileOperation();
                break;
            case AppCommand.LogLevel:
                CycleLogLevel(resolved.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1);
                break;
            case AppCommand.Sort:
                await OpenSortMenuAsync();
                break;
            case AppCommand.MakeDir:
                await CreateDirectoryAsync();
                break;
            case AppCommand.FileRename:
                await RenameSelectedFileAsync();
                break;
            case AppCommand.Delete:
                await DeleteSelectedOrMarkedAsync();
                break;
            // 拡張子実行と関連付け実行。
            case AppCommand.ExtExec:
                await ExecuteExtExecAsync(resolved.Modifiers, launchWithWindowsIfUnmapped: false);
                break;
            case AppCommand.ExtExecWindows:
                await ExecuteExtExecAsync(resolved.Modifiers, launchWithWindowsIfUnmapped: true);
                break;
            // ビューアを種類を指定して開く操作と、サウンドバーでの再生。
            case AppCommand.HexViewer:
                await OpenViewerAsync(forceHexViewer: true);
                break;
            case AppCommand.TextViewer:
                await OpenViewerAsync(forceTextViewer: true);
                break;
            case AppCommand.VideoViewer:
                await OpenViewerAsync(forceVideoViewer: true);
                break;
            case AppCommand.GraphicViewer:
                await OpenViewerAsync(forceGraphicViewer: true);
                break;
            case AppCommand.PdfViewer:
                await OpenViewerAsync(forcePdfViewer: true);
                break;
            case AppCommand.VgmPlayer:
                await OpenViewerAsync(forceVgmPlayer: true);
                break;
            case AppCommand.MiniPlayer:
                await PlayMiniPlayerAsync();
                break;
            case AppCommand.SyncPanePath:
                await SyncPanePathAsync();
                break;
        }
    }

    public void ExecuteFunctionKey(int keyNumber) => _ = ExecuteFunctionKeyAsync(keyNumber);

    /// <summary>
    /// ファンクションキーを実行する。ビューア表示中はビューアへ、ファイル一覧ではキー設定のコマンドへ回す。
    /// </summary>
    public async Task ExecuteFunctionKeyAsync(int keyNumber)
    {
        if (IsLoading || keyNumber is < 1 or > 12) return;
        // ビューア表示中はビューア用の割り当てで処理する。
        if (IsViewerActive)
        {
            ExecuteViewerFunctionKey(keyNumber, FunctionShiftPressed ? ModifierKeys.Shift : ModifierKeys.None);
            return;
        }
        // ファイル一覧ではShiftの状態に合うコマンドを探して実行する。
        var modifiers = FunctionShiftPressed ? ModifierKeys.Shift : ModifierKeys.None;
        if (FunctionKeyBindings.TryResolve(_settings.KeyBindings, keyNumber, modifiers, out var command))
            await ExecuteAsync(command);
    }

    /// <summary>
    /// 終了時の状態（左右のフォルダーと並べ替え）を設定ファイルへ保存する。
    /// </summary>
    public bool SaveSettings(out string? error)
    {
        // 左右のパネルの位置と並べ替えを設定へ書き戻す。
        _settings.LeftPath = LeftPane.CurrentPath;
        _settings.RightPath = RightPane.CurrentPath;
        _settings.LeftSortMode = LeftPane.FormatSortSetting();
        _settings.RightSortMode = RightPane.FormatSortSetting();
        _settings.SortMode = ActivePane.FormatSortSetting();

        // 保存先に書き込めないときは保存しない。
        if (!CanSaveSettings)
        {
            error = "設定保存先に書き込みできないため、設定を保存しませんでした。";
            return false;
        }

        return _settingsStore.TrySaveRuntimeState(_settings, out error);
    }

}
