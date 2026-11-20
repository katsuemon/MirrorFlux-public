using System.IO;
using System.Diagnostics;
using System.Globalization;
using MirrorFlux.Services.Vgm;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MirrorFlux.Services;
using MirrorFlux.Services.Music;
using MirrorFlux.Models;
using MirrorFlux.ViewModels;
using MirrorFlux.Input;

namespace MirrorFlux.Controls;

/// <summary>
/// サウンドビューアのビューア。共有の再生セッションを表示し、フォルダ内の曲リストから選曲と連続再生を行う。
/// </summary>
public partial class MusicPlayerControl : UserControl, IViewerControl
{
    private readonly DispatcherTimer _renderTimer;
    private readonly IMusicPlaybackService _player;
    private readonly FilePaneViewModel? _sourcePane;
    /// <summary>
    /// 曲リストの1項目。フォルダやアーカイブも含む。InternalTitle は MDX の曲内タイトルか VGM の GD3 の曲名。
    /// </summary>
    private sealed record BrowserItem(string Path, string Name, bool IsDirectory = false, bool IsParent = false, string? InternalTitle = null)
    {
        public bool IsMdx => !IsDirectory && string.Equals(System.IO.Path.GetExtension(Path), ".mdx", StringComparison.OrdinalIgnoreCase);
        public bool IsVgm => !IsDirectory && System.IO.Path.GetExtension(Path).ToLowerInvariant() is ".vgm" or ".vgz";
    }

    /// <summary>
    /// 曲リストに表示する1行。Head は「番号・種類・名前」、Title は MDX のタイトルの列。
    /// HeadWidth は Title の列をそろえるための Head の幅（NaN なら文字の幅のまま）。
    /// </summary>
    public sealed record TrackRow(string Head, string Title, double HeadWidth)
    {
        public override string ToString() => Title.Length == 0 ? Head : $"{Head}  {Title}";
    }

    // VGM の行に GD3 の曲名を出すか（偽ならファイル名）。ビューアを開き直しても保つ。
    private static bool s_showVgmTitles = true;
    internal static bool ShowVgmTitles { get => s_showVgmTitles; set => s_showVgmTitles = value; }
    private readonly IReadOnlyList<ViewerFunctionBinding> _bindings;
    private readonly List<BrowserItem> _folderTracks = new();
    private int _browseGeneration;
    private string _browsePath = "";
    private string _currentFilePath = "";
    private int _selectedTrack = -1;
    private readonly MusicPlaybackHooks _hooks;
    private bool _updatingSelection;
    private MusicPlaybackSnapshot _snapshot = new();
    private string? _registerBankLabel;
    private bool _openingTrack;
    private bool _closed;
    private string? _lastAlbumArtPath;
    private int _albumArtGeneration;
    private bool _userRequestedStop;
    private Stopwatch? _autoAdvanceFade;
    private readonly DispatcherTimer _autoAdvanceTimer;
    private bool _observedCurrentTrackPlaying;
    private bool _advanceAfterFade;

    private string _statusMessage = "";
    private string _auxiliaryFileStatus = "";

    public string FilePath => _currentFilePath;
    public string BrowsePath => _browsePath;
    public string? SelectedBrowsePath => _selectedTrack >= 0 && _selectedTrack < _folderTracks.Count ? _folderTracks[_selectedTrack].Path : null;
    public void NavigateToDirectory(string path) { BrowseDirectory(path); Focus(); }
    public string StatusText => !string.IsNullOrEmpty(_statusMessage)
        ? _statusMessage
        : (_auxiliaryFileStatus.Length > 0 ? _auxiliaryFileStatus + " | " : "")
          + (_selectedTrack >= 0 && _selectedTrack < _folderTracks.Count ? _folderTracks[_selectedTrack].Path : _browsePath);
    public string LocationText => _folderTracks.Count > 0
        ? $"FILE {_selectedTrack + 1:D3} / {_folderTracks.Count:D3}"
        : "FILE 000 / 000";
    public bool IsPromptActive => false;
    public event EventHandler? StatusChanged;
    public event EventHandler? CloseRequested;

    public MusicPlayerControl() : this(MusicPlaybackService.Instance) { }

    /// <summary>
    /// 再生サービスと元のパネルを受け取り、表示部品・タイマー・トランスポートの操作をつなぐ。
    /// </summary>
    public MusicPlayerControl(IMusicPlaybackService player, FilePaneViewModel? sourcePane = null, IEnumerable<ViewerFunctionBinding>? bindings = null)
    {
        _player = player;
        _sourcePane = sourcePane;
        _bindings = (bindings ?? ViewerFunctionBindings.CreateDefaults()).Where(b => b.Viewer == "music" && b.InputKey != null).ToArray();
        _hooks = new MusicPlaybackHooks(player, OnPlaybackStarted, OnPlaybackChanged, OnFrame);
        InitializeComponent();
        // 鍵盤のホイールでチャンネルをスクロールし、レベルメーターのクリックでチャンネルをミュートする。
        KeyboardRows.PreviewMouseWheel += (_, e) =>
        {
            if (e.Delta != 0) KeyboardRows.ScrollChannels(e.Delta > 0 ? -1 : 1);
            e.Handled = true;
        };
        ViewerLevelDisplay.MouseLeftButtonDown += OnMeterClick;
        KeyboardRows.FirstChannelChanged += _ => RenderVisibleChannelRange(_snapshot);
        // 毎秒60回の描画タイマーと、曲間の待ち時間を数えるタイマーを用意する。
        _renderTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / 60) };
        _renderTimer.Tick += (_, _) => RenderSnapshot(_player.GetSnapshot());
        _autoAdvanceTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _autoAdvanceTimer.Tick += (_, _) => AdvanceAfterGap();
        VelocityHeader.SizeChanged += (_, _) => UpdateVelocityHeaderAlignment();
        // トランスポート表示の操作を再生処理へつなぐ。全曲再生を有効にした時点で停止中なら選択曲から再生する。
        TransportDisplay.PlayClicked += () => OnPlayClick(this, new RoutedEventArgs());
        TransportDisplay.StopClicked += () => OnStopClick(this, new RoutedEventArgs());
        TransportDisplay.PauseClicked += () => OnPauseClick(this, new RoutedEventArgs());
        TransportDisplay.FastForwardClicked += OnFastForwardClick;
        TransportDisplay.RewindClicked += OnRewindClick;
        TransportDisplay.PlayAllClicked += () =>
        {
            if (TransportDisplay.IsPlayAllEnabled && !_player.IsPlaying && !_snapshot.IsPaused && !_autoAdvanceTimer.IsEnabled)
                _ = PlaySelectedTrackAsync();
        };
        TransportDisplay.RepeatClicked += () => { };
        TransportDisplay.ShuffleClicked += () => { };
        TransportDisplay.LoopToggleClicked += () => { };
        TransportDisplay.SeekRequested += OnSeekRequested;
        Loaded += OnLoaded;
        Unloaded += (_, _) => Detach();
    }

    /// <summary>
    /// DPIが変わったら、新しいピクセル幅で配置を計算し直す。
    /// </summary>
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        Dispatcher.BeginInvoke(UpdateVelocityHeaderAlignment, DispatcherPriority.Render);
    }

    private double _lastVelocityHeaderWidth = -1;
    private double _lastVelocityHeaderDpi = -1;

    /// <summary>
    /// 中央列の部品の位置を、レベルメーターのチャンネル位置(物理ピクセル単位)に合わせる。
    /// 見出し、7セグメント表示、曲情報の各行、トランスポート表示の左右をそろえる。
    /// </summary>
    private void UpdateVelocityHeaderAlignment()
    {
        // 幅とDPIが前回と同じなら何もしない。
        double width = VelocityHeader.ActualWidth;
        if (width <= 0) return;
        double dpiScaleX = VisualTreeHelper.GetDpi(VelocityHeader).DpiScaleX;
        if (Math.Abs(width - _lastVelocityHeaderWidth) < 0.001 && Math.Abs(dpiScaleX - _lastVelocityHeaderDpi) < 0.001)
            return;
        _lastVelocityHeaderWidth = width;
        _lastVelocityHeaderDpi = dpiScaleX;

        // CH01・CH09 の見出しを各チャンネルの左端に、右寄せの見出しを最後のバーの右端に合わせる。
        double pixelX = 1 / dpiScaleX;
        var first = MusicDisplayDrawing.ChannelSlot(width, 0, 16, dpiScaleX);
        var ninth = MusicDisplayDrawing.ChannelSlot(width, 8, 16, dpiScaleX);
        var last = MusicDisplayDrawing.ChannelSlot(width, 15, 16, dpiScaleX);
        double levelRight = last.Right - ViewerLevelDisplay.ChannelGapPixels * pixelX;
        double rightMargin = Math.Max(0, width - levelRight);
        VelocityCh01Text.Margin = new Thickness(first.Left, 0, 0, 0);
        VelocityCh09Text.Margin = new Thickness(Math.Max(0, ninth.Left - width / 2), 0, 0, 0);
        VelocityGraphicsText.Margin = new Thickness(0, 0, rightMargin, 0);
        SpectrumGraphicsText.Margin = new Thickness(0, 0, rightMargin, 0);
        // 右寄せの7セグメント表示と音源コア表示を27ピクセル間隔で縦に並べる。
        const double pitch = 27.0;
        const double topY = 471.0;

        CurrentClockSevenSegment.Margin = new Thickness(0, 0, rightMargin, topY);
        ElapsedTimeDisplay.Margin = new Thickness(0, 0, rightMargin, topY - pitch);
        TotalTimeSevenSegment.Margin = new Thickness(0, 0, rightMargin, topY - pitch * 2);
        LoopCountSevenSegment.Margin = new Thickness(0, 0, rightMargin, topY - pitch * 3);
        FmCoreText.Width = 114;
        FmCoreText.Margin = new Thickness(0, 0, rightMargin, LoopCountSevenSegment.Margin.Bottom - 27);
        ExtDeviceText.Width = 114;
        ExtDeviceText.Margin = new Thickness(0, 0, rightMargin, FmCoreText.Margin.Bottom - 27);

        // 曲情報の行は最初のチャンネルより20ピクセル左から始め、右の7セグメント表示の手前で止める。音源の行だけ右端まで使う。
        const double gd3ShiftLeft = 20.0;
        double gd3Left = first.Left - gd3ShiftLeft;
        double gd3RightMargin = rightMargin + CurrentClockSevenSegment.FixedWidth + 20;
        double gd3RowWidth = Math.Max(0, width - gd3RightMargin - gd3Left);

        double fullGd3RowWidth = Math.Max(0, width - rightMargin - gd3Left);

        SetGd3RowBounds(Gd3TrackTitleRow, gd3Left, gd3RowWidth, topY);
        SetGd3RowBounds(Gd3GameTitleRow, gd3Left, gd3RowWidth, topY - pitch);
        SetGd3RowBounds(Gd3SystemNameRow, gd3Left, gd3RowWidth, topY - pitch * 2);
        SetGd3RowBounds(Gd3AuthorRow, gd3Left, gd3RowWidth, topY - pitch * 3);
        SetGd3RowBounds(Gd3FileTypeRow, gd3Left, gd3RowWidth, topY - pitch * 4);
        SetGd3RowBounds(Gd3SoundSourceRow, gd3Left, fullGd3RowWidth, topY - pitch * 5);
        ControllerGraphicsText.Margin = new Thickness(0, 0, rightMargin, 312);
        TransportDisplay.Margin = new Thickness(gd3Left, 0, rightMargin, 259);
    }

    private static void SetGd3RowBounds(FrameworkElement row, double left, double width, double bottomMargin)
    {
        row.HorizontalAlignment = HorizontalAlignment.Left;
        row.Width = width;
        row.Margin = new Thickness(left, 0, 0, bottomMargin);
    }

    /// <summary>
    /// 表示されたら再生イベントの購読と描画タイマーを始める。
    /// </summary>
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _hooks.Subscribe();
        _hooks.SetFrameActive(true);
        RenderSnapshot(_player.GetSnapshot());
        _ = UpdateAlbumArtAsync();
        _renderTimer.Start();
    }

    /// <summary>
    /// 再生フレームごとの通知。表示中で再生中のときだけ描画する。
    /// </summary>
    private void OnFrame()
    {
        if (!_hooks.Subscribed || _closed || !IsVisible) return;
        if (_player.IsPlaying && !_snapshot.IsPaused)
            RenderSnapshot(_player.GetSnapshot());
    }

    private readonly Dictionary<string, MusicChannelSnapshot> _masked = new();
    /// <summary>
    /// ミュートしたチャンネルをすべて元に戻す。
    /// </summary>
    private void ClearMasks()
    {
        foreach (var channel in _masked.Values) _player.SetChannelMuted(channel, false);
        _masked.Clear();
    }
    /// <summary>
    /// 新しい曲の再生が始まったら、自動送りとフェードを取り消し、ミュートを解除する。ミュート解除はUIスレッドで行う。
    /// </summary>
    private void OnPlaybackStarted()
    {
        CancelAutomaticAdvance();
        ResetAutomaticFade();
        if (Dispatcher.CheckAccess()) ClearMasks();
        else Dispatcher.BeginInvoke(ClearMasks);
        OnPlaybackChanged();
    }
    /// <summary>
    /// レベルメーターでクリックしたバーのチャンネルのミュートを切り替える。
    /// </summary>
    private void OnMeterClick(object sender, MouseButtonEventArgs e)
    {
        var meter = (MusicMeterDisplay)sender;
        double x = e.GetPosition(meter).X;
        for (int i = 0; i < meter.ChannelCount; i++)
            if (x >= meter.GetBarLeft(i) && x < meter.GetBarRight(i)) { ToggleMask(i); e.Handled = true; break; }
    }
    /// <summary>
    /// 表示中の位置から実際のチャンネルを求めてミュートを切り替える。音源が対応しないときは何もしない。
    /// </summary>
    private void ToggleMask(int slot)
    {
        int index = KeyboardRows.FirstChannel + slot;
        if ((uint)index >= _snapshot.Channels.Length) return;
        var channel = _snapshot.Channels[index];
        bool mute = !_masked.ContainsKey(channel.Id);
        if (!_player.SetChannelMuted(channel, mute)) return;
        if (mute) _masked[channel.Id] = channel; else _masked.Remove(channel.Id);
        RenderVisibleChannelRange(_snapshot);
    }

    /// <summary>
    /// 表示を外すときに、自動送り・ミュート・描画タイマー・購読を片付ける。再生自体は止めない。
    /// </summary>
    private void Detach()
    {
        CancelAutomaticAdvance();
        _observedCurrentTrackPlaying = false;
        ResetAutomaticFade();
        ClearMasks();
        _renderTimer.Stop();
        _hooks.Unsubscribe();
    }

    /// <summary>
    /// 再生状態が変わったら、UIスレッドで表示・アートワーク・ステータスを更新する。
    /// </summary>
    private void OnPlaybackChanged() => Dispatcher.BeginInvoke(() =>
    {
        if (!_hooks.Subscribed) return;
        var snap = _player.GetSnapshot();
        RenderSnapshot(snap);
        _ = UpdateAlbumArtAsync();
        StatusChanged?.Invoke(this, EventArgs.Empty);
    });

    /// <summary>
    /// 曲を開く。同じ曲を再生中ならそのまま続け、曲リストは元のパネルまたは曲のあるフォルダから作る。
    /// </summary>
    public bool Load(string filePath)
    {
        // 同じ曲が再生中でなければ再生を始める。
        if (!File.Exists(filePath) || !_player.CanOpenFullViewer(filePath)) return false;
        var snapshot = _player.GetSnapshot();
        bool isAlreadyPlayingThis = (snapshot.IsPlaying || _player.IsPlaying || _player.IsPaused) &&
            IsSameTrack(_player.CurrentFilePath, filePath);
        if (!isAlreadyPlayingThis && !_player.Play(filePath))
            return false;
        // 曲リストを作り、表示を更新する。
        _closed = false;
        _currentFilePath = ArchiveService.GetOriginalViewPath(filePath);
        if (_sourcePane != null)
            BrowseDirectory(_sourcePane.CurrentPath, _sourcePane.SelectedItem?.FullPath);
        else
            BrowseDirectory(ArchiveService.GetParentPath(_currentFilePath), _currentFilePath);
        RenderSnapshot(_player.GetSnapshot());
        StatusChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// 2つのパスが同じ曲を指すかを調べる。アーカイブ内の表示用パスやフルパスの違いも同じとみなす。
    /// </summary>
    private static bool IsSameTrack(string? pathA, string? pathB)
    {
        if (string.IsNullOrEmpty(pathA) || string.IsNullOrEmpty(pathB)) return false;
        if (string.Equals(pathA, pathB, StringComparison.OrdinalIgnoreCase)) return true;
        string origA = ArchiveService.GetOriginalViewPath(pathA);
        string origB = ArchiveService.GetOriginalViewPath(pathB);
        if (string.Equals(origA, origB, StringComparison.OrdinalIgnoreCase)) return true;
        try
        {
            if (File.Exists(pathA) && File.Exists(pathB) &&
                string.Equals(Path.GetFullPath(pathA), Path.GetFullPath(pathB), StringComparison.OrdinalIgnoreCase))
                return true;
        }
        catch { }
        return false;
    }

    /// <summary>
    /// サウンドビューアを閉じる。共有の再生セッションは意図して止めずに残す。
    /// </summary>
    public void Close() { _closed = true; ++_albumArtGeneration; Detach(); SetAlbumArt(null, null); }

    /// <summary>
    /// フォルダの内容から曲リストを作る。元のパネルと同じフォルダならその並び順を使い、フォルダ・アーカイブ・再生できる曲だけを載せる。
    /// </summary>
    private void BrowseDirectory(string directory, string? selectedPath = null)
    {
        // フォルダを読み、載せる項目を集める。
        var items = new List<BrowserItem>();
        try
        {
            IReadOnlyList<FileEntry> entries = _sourcePane != null &&
                string.Equals(directory, _sourcePane.CurrentPath, StringComparison.OrdinalIgnoreCase)
                ? _sourcePane.Entries
                : FilePaneViewModel.SortEntriesList(DirectoryReader.Read(directory, _sourcePane?.ShowHidden ?? false),
                    _sourcePane?.SortMode ?? PaneSortMode.Name, _sourcePane?.SortAscending ?? true, _sourcePane?.SortDirectoriesFirst ?? true);
            bool physical = Directory.Exists(directory);
            foreach (var entry in entries)
            {
                bool browsable = entry.IsDirectory || (physical && ArchiveService.IsSupportedArchive(entry.FullPath));
                if (browsable || _player.CanOpenFullViewer(entry.FullPath))
                    items.Add(new(entry.FullPath, entry.Name, browsable, entry.IsParent));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _statusMessage = $"OPEN FAILED: {ex.Message}";
            StatusChanged?.Invoke(this, EventArgs.Empty);
            return;
        }
        // 曲リストを差し替え、指定の曲(なければ先頭)を選ぶ。
        _browsePath = directory;
        _folderTracks.Clear();
        _folderTracks.AddRange(items);
        _selectedTrack = _folderTracks.FindIndex(item => string.Equals(item.Path, selectedPath, StringComparison.OrdinalIgnoreCase));
        if (_selectedTrack < 0 && _folderTracks.Count > 0) _selectedTrack = 0;
        _updatingSelection = true;
        TrackListBox.Items.Clear();
        foreach (var row in BuildTrackRows())
            TrackListBox.Items.Add(row);
        _updatingSelection = false;
        UpdateSelection();
        // MDX・VGM のタイトルとアートワークは後から読み込む。
        _ = EnrichTitlesAsync();
        _ = UpdateAlbumArtAsync();
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 曲リスト中の MDX の曲内タイトルと VGM の GD3 の曲名を別スレッドで読み、リストの表示に加える。途中でフォルダが変わったら結果を捨てる。
    /// </summary>
    private async Task EnrichTitlesAsync()
    {
        // 対象の MDX・VGM を集め、別スレッドでタイトルを読む。
        int browseGeneration = ++_browseGeneration;
        string browsePath = _browsePath;
        var candidates = _folderTracks
            .Select((item, index) => (item, index))
            .Where(value => value.item.IsMdx || value.item.IsVgm)
            .ToArray();
        if (candidates.Length == 0) return;

        var resolvedTitles = await Task.Run(() => ReadTitlesBatch(browsePath, candidates));
        if (_closed || browseGeneration != _browseGeneration || resolvedTitles.Count == 0) return;

        // UIスレッドで行の表示を差し替える。
        await Dispatcher.InvokeAsync(() =>
        {
            if (_closed || browseGeneration != _browseGeneration) return;
            foreach (var (index, title) in resolvedTitles)
                if ((uint)index < (uint)_folderTracks.Count)
                    _folderTracks[index] = _folderTracks[index] with { InternalTitle = title };
            RefreshTrackRows();
        }, DispatcherPriority.Background);
    }

    /// <summary>
    /// 曲のタイトルを読む。MDX は先頭2048バイトのタイトル、VGM・VGZ は GD3 の曲名。
    /// アーカイブ内なら1回の走査でまとめて読む。読めないファイルは飛ばす。
    /// </summary>
    private static List<(int Index, string Title)> ReadTitlesBatch(string browsePath, (BrowserItem item, int index)[] candidates)
    {
        var results = new List<(int Index, string Title)>(candidates.Length);
        static string? Read(string name, Stream input)
        {
            string title;
            if (string.Equals(Path.GetExtension(name), ".mdx", StringComparison.OrdinalIgnoreCase))
            {
                byte[] buf = new byte[2048];
                int total = 0;
                while (total < buf.Length)
                {
                    int r = input.Read(buf, total, buf.Length - total);
                    if (r <= 0) break;
                    total += r;
                }
                title = MdxFile.ReadTitle(buf.AsSpan(0, total));
            }
            else title = VgmFile.ReadGd3Title(input);
            return string.IsNullOrWhiteSpace(title) ? null : title.Trim();
        }
        try
        {
            // アーカイブ内のフォルダなら、各曲のアーカイブ内パスを求めて1回の走査で読む。
            if (ArchiveService.TrySplitArchivePath(browsePath, out string archivePath, out _))
            {
                string prefix = archivePath.TrimEnd('\\', '/') + "\\";
                var keyMap = new List<(int Index, string Key)>(candidates.Length);
                foreach (var (item, index) in candidates)
                {
                    string internalKey = item.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                        ? item.Path.Substring(prefix.Length).Replace('/', '\\').Trim('\\')
                        : (ArchiveService.TrySplitArchivePath(item.Path, out _, out string splitKey) ? splitKey.Replace('/', '\\').Trim('\\') : "");
                    if (!string.IsNullOrEmpty(internalKey))
                        keyMap.Add((index, internalKey));
                }
                var titles = ArchiveService.ReadArchiveEntryValues(archivePath, keyMap.Select(x => x.Key).ToArray(), Read);
                foreach (var (index, key) in keyMap)
                    if (titles.TryGetValue(key, out string? title))
                        results.Add((index, title));
                return results;
            }

            // 通常のフォルダなら、各ファイルを開いて読む。
            foreach (var (item, index) in candidates)
            {
                try
                {
                    if (!File.Exists(item.Path)) continue;
                    using var fs = new FileStream(item.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    if (Read(item.Path, fs) is { } title) results.Add((index, title));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch { }
        return results;
    }

    /// <summary>
    /// 曲リストの行を作る。VGM は切り替えに応じて GD3 の曲名かファイル名を出す。
    /// MDX はファイル名の後ろにタイトルを置き、タイトルの位置を同じフォルダの MDX でそろえる。
    /// </summary>
    private List<TrackRow> BuildTrackRows()
    {
        var heads = new string[_folderTracks.Count];
        double mdxWidth = 0;
        Typeface? typeface = null;
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        for (int index = 0; index < _folderTracks.Count; index++)
        {
            var item = _folderTracks[index];
            string type = item.IsParent ? "UP" : item.IsDirectory ? "DIR" : Path.GetExtension(item.Path).TrimStart('.').ToUpperInvariant();
            string name = item.IsVgm && s_showVgmTitles && !string.IsNullOrWhiteSpace(item.InternalTitle) ? item.InternalTitle! : item.Name;
            heads[index] = $"{index + 1,3:D2}  {type,-5}  {name}";
            if (item.IsMdx && !string.IsNullOrWhiteSpace(item.InternalTitle))
            {
                typeface ??= new Typeface(TrackListBox.FontFamily, TrackListBox.FontStyle, TrackListBox.FontWeight, TrackListBox.FontStretch);
                var text = new FormattedText(heads[index] + "  ", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                    typeface, TrackListBox.FontSize, Brushes.Black, pixelsPerDip);
                mdxWidth = Math.Max(mdxWidth, Math.Ceiling(text.WidthIncludingTrailingWhitespace));
            }
        }
        // 長いファイル名があってもタイトルが見えるよう、名前の列は一覧の幅の6割までにする。
        double available = TrackListBox.ActualWidth - 8;
        if (available > 0) mdxWidth = Math.Min(mdxWidth, Math.Floor(available * 0.6));
        var rows = new List<TrackRow>(heads.Length);
        for (int index = 0; index < heads.Length; index++)
        {
            var item = _folderTracks[index];
            bool column = item.IsMdx && !string.IsNullOrWhiteSpace(item.InternalTitle) && mdxWidth > 0;
            rows.Add(new TrackRow(heads[index], column ? item.InternalTitle! : "", column ? mdxWidth : double.NaN));
        }
        return rows;
    }

    /// <summary>曲リストの表示を作り直す。選択位置は保つ。</summary>
    private void RefreshTrackRows()
    {
        var rows = BuildTrackRows();
        _updatingSelection = true;
        try
        {
            for (int index = 0; index < rows.Count && index < TrackListBox.Items.Count; index++)
                if (!Equals(TrackListBox.Items[index], rows[index]))
                    TrackListBox.Items[index] = rows[index];
            if (_selectedTrack >= 0 && _selectedTrack < TrackListBox.Items.Count)
                TrackListBox.SelectedIndex = _selectedTrack;
        }
        finally
        {
            _updatingSelection = false;
        }
    }

    /// <summary>VGM の行の表示を GD3 の曲名とファイル名で切り替える。</summary>
    internal void ToggleVgmTitles()
    {
        s_showVgmTitles = !s_showVgmTitles;
        RefreshTrackRows();
        _statusMessage = s_showVgmTitles ? "LIST: TITLE" : "LIST: FILE NAME";
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 親フォルダの曲リストへ移り、今いたフォルダを選ぶ。
    /// </summary>
    private void GoToParent()
    {
        if (_openingTrack || _closed || string.IsNullOrEmpty(_browsePath)) return;
        string parent = ArchiveService.GetParentPath(_browsePath);
        if (!string.Equals(parent, _browsePath, StringComparison.OrdinalIgnoreCase))
            BrowseDirectory(parent, _browsePath);
    }

    /// <summary>
    /// 曲リストの選択を内部の選択位置に合わせて見える位置へスクロールし、ステータスを更新する。
    /// </summary>
    private void UpdateSelection()
    {
        _updatingSelection = true;
        TrackListBox.SelectedIndex = _selectedTrack;
        if (TrackListBox.SelectedItem != null) TrackListBox.ScrollIntoView(TrackListBox.SelectedItem);
        _updatingSelection = false;
        _statusMessage = "";
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 形式に依存しない再生スナップショット1つを描画する。画面外での決まった内容のプレビューにも使う。
    /// あわせて、目標ループ到達時のフェードと曲の終わりの自動送りを判断する。
    /// </summary>
    public void RenderSnapshot(MusicPlaybackSnapshot snapshot)
    {
        // 曲の終わりを判定する。PlaybackStopped を出さずに終端へ達するデコーダーがあるため、再生中を見た後に止まったことで判断する。
        if (!snapshot.IsPlaying && !snapshot.IsPaused && _player.CurrentFilePath == null && !_advanceAfterFade)
            CancelAutomaticAdvance();
        bool completedTrack = _observedCurrentTrackPlaying && !snapshot.IsPlaying && !snapshot.IsPaused &&
            !_openingTrack && !_userRequestedStop && !_closed && _hooks.Subscribed &&
            _player.CurrentFilePath != null && !_autoAdvanceTimer.IsEnabled;
        if (snapshot.IsPlaying) _observedCurrentTrackPlaying = true;
        else if (!snapshot.IsPaused) _observedCurrentTrackPlaying = false;
        // 曲が変わったら鍵盤を先頭チャンネルへ戻し、再生の有無が変わったらアートワークを更新する。
        if (_snapshot.Title != snapshot.Title) KeyboardRows.ScrollChannels(-36);
        bool albumArtModeChanged = (_snapshot.IsPlaying || _snapshot.IsPaused) != (snapshot.IsPlaying || snapshot.IsPaused);
        _snapshot = snapshot;
        if (albumArtModeChanged) _ = UpdateAlbumArtAsync();
        // 時計・時間・曲情報・トランスポートの表示を更新する。
        var now = DateTime.Now;
        CurrentClockSevenSegment.Text = now.ToString("HH:mm");
        ElapsedTimeDisplay.Text = MusicDisplayDrawing.FormatTime(snapshot.CurrentTime);
        TotalTimeSevenSegment.Text = MusicPlayerShared.TotalTimeText(snapshot.TotalTime);
        LoopCountSevenSegment.Text = Math.Clamp(snapshot.LoopCount, 0, 9999).ToString("D4");
        FmCoreText.Text = snapshot.FmCoreName;
        ExtDeviceText.Text = snapshot.ExtDeviceName;
        TitleText.Text = string.IsNullOrWhiteSpace(snapshot.Title) ? "NO MUSIC DATA LOADED" : snapshot.Title;
        Gd3TrackTitleText.Text = string.IsNullOrWhiteSpace(snapshot.Title) ? "—" : snapshot.Title;
        Gd3GameTitleText.Text = string.IsNullOrWhiteSpace(snapshot.GameTitle) ? "—" : snapshot.GameTitle;
        Gd3SystemNameText.Text = string.IsNullOrWhiteSpace(snapshot.SystemName) ? "—" : snapshot.SystemName;
        Gd3AuthorText.Text = string.IsNullOrWhiteSpace(snapshot.Author) ? "—" : snapshot.Author;
        Gd3FileTypeText.Text = string.IsNullOrWhiteSpace(snapshot.ResolvedFileTypeDisplay) ? "—" : snapshot.ResolvedFileTypeDisplay;
        _auxiliaryFileStatus = snapshot.AuxiliaryFileStatus;
        Gd3SoundSourceText.Text = string.IsNullOrWhiteSpace(snapshot.ResolvedSoundSourceDisplay) ? "—" : snapshot.ResolvedSoundSourceDisplay;
        TransportDisplay.CanSeek = _player.CanSeek;
        TransportDisplay.UpdateState(snapshot.IsPlaying, snapshot.IsPaused, snapshot.CurrentTime, snapshot.TotalTime, snapshot.LoopCount, snapshot.LoopDuration);
        // 目標ループ回数に達したらフェードアウトを始め、音量が0になったら次の曲へ進む。条件が外れたらフェードを取り消す。
        if (_autoAdvanceFade != null)
        {
            if (TransportDisplay.TargetLoops == 0 || snapshot.LoopCount < TransportDisplay.TargetLoops || _userRequestedStop)
            {
                ResetAutomaticFade();
            }
            else if (snapshot.IsPlaying)
            {
                if (snapshot.IsPaused) _autoAdvanceFade.Stop();
                else
                {
                    _autoAdvanceFade.Start();
                    float gain = MusicFadeGain.At(_autoAdvanceFade.Elapsed);
                    _player.SetFadeGain(gain);
                    if (gain <= 0) AdvanceAfterTargetLoop();
                }
            }
        }
        else if (snapshot.IsPlaying && !snapshot.IsPaused && !_userRequestedStop && !_openingTrack &&
                 _folderTracks.Count > 0 && TransportDisplay.TargetLoops > 0 &&
                 snapshot.LoopCount >= TransportDisplay.TargetLoops)
        {
            _autoAdvanceFade = Stopwatch.StartNew();
        }
        // 鍵盤、レジスタ、スペクトラム、チャンネルごとの表示を更新する。
        KeyboardRows.SetChannels(snapshot.Channels);
        ViewerGrid.Height = 12 * 42;
        UpdateRegisterDisplay();
        SpectrumDisplay.SetLevels(snapshot.SpectrumLevels.Select(level => (double)level).ToArray(), Array.Empty<string>(), snapshot.IsPaused);
        RenderVisibleChannelRange(snapshot);
        // 曲が終わったら、フェード中・リピート・連続再生のいずれかなら次の曲を予約する。
        if (completedTrack)
        {
            bool fadePending = _autoAdvanceFade != null;
            ResetAutomaticFade();
            if (fadePending || TransportDisplay.IsRepeatEnabled || ContinuesAfterTrack)
                ScheduleAutomaticAdvance(fadePending);
        }
    }

    /// <summary>
    /// 鍵盤で見えている16チャンネル分の、レベルメーター・パン・ノート番号の表示を更新する。ミュート中のチャンネルはレベルを0にする。
    /// </summary>
    private void RenderVisibleChannelRange(MusicPlaybackSnapshot snapshot)
    {
        // 見出しのチャンネル番号を更新する。
        int firstChannel = KeyboardRows.FirstChannel;
        VelocityCh01Text.Text = $"CH{firstChannel + 1:D2}";
        VelocityCh09Text.Text = $"CH{firstChannel + 9:D2}";
        // 見えているチャンネルの値を集め、ミュート状態をビットで記録する。
        var viewerLevels = new double[16];
        var viewerEnvelopes = new bool[16];
        var viewerPanLeft = new double[16];
        var viewerPanRight = new double[16];
        var viewerNotes = new int?[16];
        var viewerNoteActive = new bool[16];
        var viewerPanActive = new bool[16];
        int masked = 0;
        int visibleCount = Math.Min(Math.Max(0, snapshot.Channels.Length - firstChannel), 16);
        for (int index = 0; index < visibleCount; index++)
        {
            var channel = snapshot.Channels[firstChannel + index];
            bool muted = _masked.ContainsKey(channel.Id);
            if (muted) masked |= 1 << index;
            viewerLevels[index] = muted ? 0 : channel.Level;
            viewerEnvelopes[index] = channel.UsesEnvelopeTiming;
            (viewerPanLeft[index], viewerPanRight[index]) = MusicPlayerShared.PanLevels(channel);
            viewerNotes[index] = channel.NoteNumber;
            viewerNoteActive[index] = channel.IsKeyOn || channel.Level > .001;
            viewerPanActive[index] = channel.IsInUse && !muted;
        }
        // 各表示へ反映する。
        KeyboardRows.SetVisibleMask(masked);
        ViewerLevelDisplay.MaskedChannels = masked;
        ViewerLevelDisplay.SetLevels(viewerLevels, Array.Empty<string>(), snapshot.IsPaused, viewerEnvelopes);
        ViewerNoteDisplay.SetNotes(viewerNotes, viewerNoteActive);
        ViewerPanDisplay.SetPanLevels(viewerPanLeft, viewerPanRight, viewerPanActive);
        UpdateVelocityHeaderAlignment();
    }

    /// <summary>
    /// 選択中のレジスタバンク(なければ最初のバンク)をレジスタ表示に出す。
    /// </summary>
    private void UpdateRegisterDisplay()
    {
        var bank = _snapshot.RegisterBanks.FirstOrDefault(b => b.Label == _registerBankLabel)
            ?? _snapshot.RegisterBanks.FirstOrDefault();
        _registerBankLabel = bank?.Label;
        RegisterBankText.Text = bank == null ? "NO YM REGISTERS" : bank.Label;
        RegisterDisplay.SetValues(bank?.Values);
    }

    /// <summary>
    /// レジスタ表示を次のバンクへ切り替える。
    /// </summary>
    private void ToggleRegisterBank()
    {
        var banks = _snapshot.RegisterBanks;
        if (banks.Length == 0) return;
        int index = Array.FindIndex(banks, b => b.Label == _registerBankLabel);
        _registerBankLabel = banks[(index + 1) % banks.Length].Label;
        UpdateRegisterDisplay();
    }

    /// <summary>
    /// 曲リストで選んだ項目を開く。フォルダなら移動し、曲なら再生する。アーカイブ内の曲は一時ファイルへ展開してから再生する。
    /// </summary>
    public async Task PlaySelectedTrackAsync(bool stayInViewer = true)
    {
        // フォルダや親フォルダなら曲リストを移動する。
        if (_openingTrack || _closed || _selectedTrack < 0 || _selectedTrack >= _folderTracks.Count) return;
        var selected = _folderTracks[_selectedTrack];
        string path = selected.Path;
        if (selected.IsDirectory)
        {
            if (selected.IsParent) GoToParent();
            else BrowseDirectory(path);
            return;
        }
        // 自動送りとフェードを取り消してから開く。
        _openingTrack = true;
        CancelAutomaticAdvance();
        ResetAutomaticFade();
        _userRequestedStop = false;
        try
        {
            // アーカイブ内の曲は展開してから再生し、失敗したら理由をステータスに出す。
            string? playablePath = path;
            if (ArchiveService.IsArchiveEntry(path))
            {
                _statusMessage = $"EXTRACTING: {path}";
                StatusChanged?.Invoke(this, EventArgs.Empty);
                playablePath = await ArchiveService.ExtractEntryToTempFileAsync(path);
            }
            if (_closed) return;
            if (playablePath == null || !_player.Play(playablePath))
            {
                _statusMessage = _player.LastError is { } reason ? $"{reason}: {path}" : $"PLAYBACK FAILED: {path}";
                StatusChanged?.Invoke(this, EventArgs.Empty);
                return;
            }
            // 再生できたら表示を新しい曲に合わせる。
            _currentFilePath = path;
            KeyboardRows.ScrollChannels(-36);
            _ = UpdateAlbumArtAsync();
            RenderSnapshot(_player.GetSnapshot());
            UpdateSelection();
            StatusChanged?.Invoke(this, EventArgs.Empty);
            if (!stayInViewer) CloseRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _statusMessage = $"PLAYBACK FAILED: {ex.Message}";
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
        finally { _openingTrack = false; }
    }

    /// <summary>
    /// ミュート切り替えのキーから表示位置を求める。1～8 は CH01～08、Shift+1～8 は CH09～16 に当たる。該当しなければ -1。
    /// </summary>
    private static int MaskSlot(Key key, ModifierKeys mods)
    {
        if (mods != ModifierKeys.None && mods != ModifierKeys.Shift) return -1;
        int slot = key is >= Key.D1 and <= Key.D8 ? key - Key.D1 :
            key is >= Key.NumPad1 and <= Key.NumPad8 ? key - Key.NumPad1 : -1;
        return slot < 0 ? -1 : slot + (mods == ModifierKeys.Shift ? 8 : 0);
    }
    /// <summary>
    /// キー入力を処理する。ミュートのキーは押しっぱなしの繰り返しを無視して受け取り済みにする。
    /// </summary>
    public bool HandleKeyDown(KeyEventArgs e)
    {
        Key key = e.NormalizedKey();
        return (e.IsRepeat && MaskSlot(key, Keyboard.Modifiers) >= 0) || HandleKey(key, Keyboard.Modifiers);
    }

    /// <summary>
    /// サウンドビューアのキー操作。ミュート、親フォルダへ移動、ファンクションキー、再生操作、曲リストの移動を扱う。
    /// </summary>
    public bool HandleKey(Key key, ModifierKeys modifiers)
    {
        // ミュート、親フォルダへ移動、ファンクションキーを先に処理する。
        int slot = MaskSlot(key, modifiers);
        if (slot >= 0) { ToggleMask(slot); return true; }
        // INI の [keys.music] で割り当てたファンクションキー以外のキー（既定は T で曲名とファイル名の切り替え）。
        if (_bindings.LastOrDefault(b => b.InputKey == key && b.Modifiers == modifiers) is { } binding)
        {
            if (binding.Command == "titles") ToggleVgmTitles();
            return true;
        }
        if (key == Key.Back || (key == Key.Up && modifiers.HasFlag(ModifierKeys.Alt)))
        {
            GoToParent();
            return true;
        }
        if (key >= Key.F1 && key <= Key.F12 && modifiers == ModifierKeys.None)
        {
            ExecuteFunctionKey(key - Key.F1 + 1);
            return true;
        }
        // 閉じる、一時停止、前後の曲、ページ送り(Shift 付きは鍵盤のスクロール)、曲リストの移動と再生。
        switch (key)
        {
            case Key.Escape:
            case Key.Q: CloseRequested?.Invoke(this, EventArgs.Empty); return true;
            case Key.Space: _player.TogglePause(); return true;
            case Key.Left:
            case Key.Right:
                if (MusicPlayerShared.SelectRelativeTrack(_player, _snapshot, key == Key.Left ? -1 : 1))
                {
                    RenderSnapshot(_player.GetSnapshot());
                    StatusChanged?.Invoke(this, EventArgs.Empty);
                }
                return true;
            case Key.PageUp:
            case Key.PageDown:
                int direction = key == Key.PageUp ? -1 : 1;
                if (modifiers.HasFlag(ModifierKeys.Shift)) KeyboardRows.ScrollChannels(direction * 12);
                else if (_folderTracks.Count > 0)
                {
                    int visibleRows = Math.Max(1, (int)Math.Floor(TrackListBox.ActualHeight / 18.0));
                    int pageRows = Math.Max(1, visibleRows - 2);
                    _selectedTrack = Math.Clamp(_selectedTrack + direction * pageRows, 0, _folderTracks.Count - 1);
                    UpdateSelection();
                }
                return true;
            case Key.Up:
            case Key.Down:
                if (_folderTracks.Count > 0)
                {
                    _selectedTrack = (_selectedTrack + (key == Key.Up ? -1 : 1) + _folderTracks.Count) % _folderTracks.Count;
                    UpdateSelection();
                }
                return true;
            case Key.Return:
                _ = PlaySelectedTrackAsync();
                return true;
        }
        return false;
    }

    /// <summary>
    /// F1～F12 に割り当てたサウンドビューアの機能を実行する。番号は実際のキー割り当てを解決した後の機能番号。
    /// </summary>
    public void ExecuteFunctionKey(int keyNumber)
    {
        switch (keyNumber)
        {
            case 1: // ヘルプ
                ShowHelp();
                break;
            case 13: // VGM の曲名とファイル名の切り替え（ファンクションキーに割り当てたとき）
                ToggleVgmTitles();
                break;
            case 2: // 再生/一時停止
                if (_snapshot.IsPaused) _player.TogglePause();
                else if (_snapshot.IsPlaying) _player.TogglePause();
                else _ = PlaySelectedTrackAsync();
                break;
            case 3: // 前曲
                AdvanceToPreviousTrack();
                break;
            case 4: // 次曲
                AdvanceToNextTrack(TransportDisplay.IsShuffleEnabled);
                break;
            case 5: // 全曲連続再生
                TransportDisplay.IsPlayAllEnabled = !TransportDisplay.IsPlayAllEnabled;
                break;
            case 6: // 1曲リピート
                TransportDisplay.IsRepeatEnabled = !TransportDisplay.IsRepeatEnabled;
                break;
            case 7: // シャッフル
                TransportDisplay.IsShuffleEnabled = !TransportDisplay.IsShuffleEnabled;
                break;
            case 8: // 演奏停止
                OnStopClick(this, new RoutedEventArgs());
                break;
            case 9: // ループ回数切替 (∞ LOOP → 1 LOOP → 2 LOOP)
                TransportDisplay.TargetLoops = TransportDisplay.TargetLoops switch
                {
                    0 => 1,
                    1 => 2,
                    _ => 0
                };
                break;
            case 10: // 5秒早送り
                OnFastForwardClick();
                break;
            case 11: // レジスタ切替
                ToggleRegisterBank();
                break;
            case 12: // 終了
                CloseRequested?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    /// <summary>
    /// サウンドビューアの操作一覧を表示する。
    /// </summary>
    private void ShowHelp()
    {
        string titleKeys = string.Join(" / ", _bindings.Where(b => b.Command == "titles")
            .Select(b => (b.Modifiers.HasFlag(ModifierKeys.Shift) ? "Shift+" : "") + (b.Modifiers.HasFlag(ModifierKeys.Control) ? "Ctrl+" : "") +
                (b.Modifiers.HasFlag(ModifierKeys.Alt) ? "Alt+" : "") + b.InputKey));
        MessageBox.Show(
            "サウンドビューア 操作一覧:\n\n" +
            "  [Space / F2] 再生 / 一時停止\n" +
            "  [Enter] 選択した曲を再生\n" +
            "  [F8] 演奏停止\n" +
            "  [F3 / F4] 前の曲 / 次の曲\n" +
            "  [F5] 全曲連続再生 (PLAY ALL) 切替\n" +
            "  [F6] 1曲リピート (REPEAT) 切替\n" +
            "  [F7] シャッフル (SHUFFLE) 切替\n" +
            "  [F9] ループ回数切替 (∞ LOOP → 1 LOOP → 2 LOOP)\n" +
            "  [F10] 5秒早送り (FF)\n" +
            "  [F11] 音源レジスタ表示切替 (PORT 0 / 1)\n" +
            "  [Esc / Q / F12] 閉じる (バックグラウンドでサウンドバー継続)\n\n" +
            "その他の操作:\n" +
            "  [↑ / ↓] 曲リストの選択移動\n" +
            "  [1–8 / Shift + 1–8 / メータークリック] 表示中のチャンネルのマスク切替\n" +
            "  [PgUp / PgDn] 曲リストのページ送り\n" +
            "  [Shift + PgUp / PgDn] 鍵盤表示チャンネルのスクロール\n" +
            "  [← / →] マルチトラック音源のサブソング切替\n" +
            "  [BackSpace / Alt+↑] 親フォルダへ移動\n" +
            (titleKeys.Length > 0 ? $"  [{titleKeys}] VGM の曲リストを曲名 / ファイル名で切替\n" : ""),
            "サウンドビューア ヘルプ",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }
    /// <summary>
    /// マウスで曲リストの選択が変わったら内部の選択位置に反映する。
    /// </summary>
    private void OnTrackSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingSelection || TrackListBox.SelectedIndex < 0) return;
        _selectedTrack = TrackListBox.SelectedIndex;
        UpdateSelection();
    }
    /// <summary>
    /// 曲リストの行をダブルクリックしたら、その項目を開く。行以外の場所は無視する。
    /// </summary>
    private void OnTrackDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(TrackListBox, source) is ListBoxItem)
            _ = PlaySelectedTrackAsync();
    }
    /// <summary>
    /// 一時停止中なら再開し、それ以外は選択中の曲を再生する。
    /// </summary>
    private void OnPlayClick(object sender, RoutedEventArgs e)
    {
        if (_snapshot.IsPaused) _player.TogglePause();
        else _ = PlaySelectedTrackAsync();
    }
    private void OnPauseClick(object sender, RoutedEventArgs e) => _player.TogglePause();
    /// <summary>
    /// 利用者の操作で停止する。自動送りとフェードを取り消し、次の曲へ進まないようにする。
    /// </summary>
    private void OnStopClick(object sender, RoutedEventArgs e)
    {
        _userRequestedStop = true;
        CancelAutomaticAdvance();
        _observedCurrentTrackPlaying = false;
        ResetAutomaticFade();
        _player.Stop();
    }
    /// <summary>
    /// フォルダを除いた曲の中で前の曲を選んで再生する。先頭からは末尾へ回る。
    /// </summary>
    private void AdvanceToPreviousTrack()
    {
        // フォルダを除いた曲の位置を集める。
        if (_folderTracks.Count == 0) return;
        var validIndices = _folderTracks
            .Select((item, idx) => (item, idx))
            .Where(x => !x.item.IsDirectory)
            .Select(x => x.idx)
            .ToList();
        if (validIndices.Count == 0) return;

        // 1つ前の曲を選んで再生する。
        int currentPos = validIndices.IndexOf(_selectedTrack);
        int prevPos = (currentPos - 1 + validIndices.Count) % validIndices.Count;
        _selectedTrack = validIndices[prevPos];
        UpdateSelection();
        _ = PlaySelectedTrackAsync();
    }

    /// <summary>
    /// フォルダを除いた曲の中で次の曲を選んで再生する。シャッフル時は今の曲以外から無作為に選ぶ。
    /// </summary>
    private void AdvanceToNextTrack(bool shuffle)
    {
        // フォルダを除いた曲の位置を集める。
        if (_folderTracks.Count == 0) return;
        var validIndices = _folderTracks
            .Select((item, idx) => (item, idx))
            .Where(x => !x.item.IsDirectory)
            .Select(x => x.idx)
            .ToList();
        if (validIndices.Count == 0) return;

        // シャッフルなら無作為に、そうでなければ次の曲(末尾からは先頭)を選んで再生する。
        if (shuffle && validIndices.Count > 1)
        {
            int next;
            do { next = validIndices[Random.Shared.Next(validIndices.Count)]; }
            while (next == _selectedTrack && validIndices.Count > 1);
            _selectedTrack = next;
        }
        else
        {
            int currentPos = validIndices.IndexOf(_selectedTrack);
            int nextPos = (currentPos + 1) % validIndices.Count;
            _selectedTrack = validIndices[nextPos];
        }
        UpdateSelection();
        _ = PlaySelectedTrackAsync();
    }

    /// <summary>
    /// 目標ループ後のフェードが終わったら再生を止め、曲間の待ちの後で次へ進むよう予約する。
    /// </summary>
    private void AdvanceAfterTargetLoop()
    {
        _autoAdvanceFade?.Stop();
        _autoAdvanceFade = null;
        _observedCurrentTrackPlaying = false;
        _player.Stop();
        ScheduleAutomaticAdvance(true);
    }

    /// <summary>
    /// 1秒の曲間を置いてから次の曲へ進むようタイマーを始める。停止操作の後や閉じた後は予約しない。
    /// </summary>
    private void ScheduleAutomaticAdvance(bool afterFade)
    {
        if (_closed || _userRequestedStop || !_hooks.Subscribed) return;
        _advanceAfterFade = afterFade;
        _autoAdvanceTimer.Stop();
        _autoAdvanceTimer.Start();
    }

    /// <summary>
    /// 曲間の待ちが終わったら、リピートなら同じ曲を、連続再生なら次の曲を再生する。
    /// </summary>
    private void AdvanceAfterGap()
    {
        _autoAdvanceTimer.Stop();
        bool afterFade = _advanceAfterFade;
        _advanceAfterFade = false;
        if (_closed || _userRequestedStop || _openingTrack || !_hooks.Subscribed) return;
        if (TransportDisplay.IsRepeatEnabled) _ = PlaySelectedTrackAsync();
        else if (afterFade || ContinuesAfterTrack) AdvanceToNextTrack(TransportDisplay.IsShuffleEnabled);
    }

    /// <summary>
    /// 曲の終わりで次の曲へ進むか。「全曲」がオンのとき、またはループ回数（1 LOOP など）を指定しているとき。
    /// ループ回数を指定すると、ループする曲はフェード後に次へ進むので、ループしない曲（ジングルなど）でもそこで止めずに続ける。
    /// </summary>
    private bool ContinuesAfterTrack => TransportDisplay.IsPlayAllEnabled || TransportDisplay.TargetLoops > 0;

    /// <summary>
    /// 予約済みの自動送りを取り消す。
    /// </summary>
    private void CancelAutomaticAdvance()
    {
        _autoAdvanceTimer.Stop();
        _advanceAfterFade = false;
    }

    /// <summary>
    /// 進行中のフェードを取り消し、音量を元に戻す。
    /// </summary>
    private void ResetAutomaticFade()
    {
        if (_autoAdvanceFade == null) return;
        _autoAdvanceFade.Stop();
        _autoAdvanceFade = null;
        _player.SetFadeGain(1f);
    }
    private void OnFastForwardClick() => MusicPlayerShared.SeekRelative(_player, _snapshot, 5);
    private void OnRewindClick() => MusicPlayerShared.SeekRelative(_player, _snapshot, -5);
    private void OnSeekRequested(TimeSpan target)
    {
        if (_player.CanSeek) _player.Seek(target);
    }
    /// <summary>曲リストに使える高さ。リストは18の整数行で表示し、端数は下に空ける。</summary>
    internal double TrackListAvailableHeight => TrackListContainer.ActualHeight;

    /// <summary>
    /// 曲リストの高さを18ピクセルの整数行に合わせる。
    /// </summary>
    private void OnTrackListContainerSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Now Playing の下の空きをすべて使う。ファイラーのログ欄はサウンドビューアの高さを制限しない。1行に満たない端数は下に空ける。
        const double itemHeight = 18.0;
        double available = e.NewSize.Height;
        int rowCount = Math.Max(0, (int)Math.Floor(available / itemHeight));
        TrackListBox.Height = rowCount * itemHeight;
        // 幅が変わったら、MDX のタイトルの列の位置（一覧の幅の6割まで）を決め直す。
        if (e.WidthChanged && _folderTracks.Any(item => item.IsMdx && item.InternalTitle != null))
            Dispatcher.BeginInvoke(RefreshTrackRows, DispatcherPriority.Background);
    }

    // アートワークとして探す画像の拡張子と、優先するファイル名。
    private static readonly string[] ImageExtensions =
    [
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".ico", ".tif", ".tiff"
    ];

    private static readonly string[] PreferredArtNames =
    [
        "cover", "folder", "front", "album", "jacket", "artwork", "art"
    ];

    /// <summary>
    /// アルバムアートワークを更新する。再生中はその曲のフォルダ、停止中は選択中の曲と表示中のフォルダから画像を探す。
    /// 古い要求の結果は世代番号で捨てる。
    /// </summary>
    public async Task UpdateAlbumArtAsync()
    {
        // 画像を探す曲とフォルダを決める。
        if (_closed) return;
        int generation = ++_albumArtGeneration;
        string? track = null;
        string? dir = _browsePath;
        if (_player.IsPlaying || _player.IsPaused)
        {
            // アートワークは共有の再生セッションのものなので、別のフォルダを見ていても再生中の曲のフォルダから探す。
            track = _player.CurrentFilePath;
            if (string.IsNullOrEmpty(track)) track = _currentFilePath;
            if (!string.IsNullOrEmpty(track))
            {
                track = ArchiveService.GetOriginalViewPath(track);
                dir = ArchiveService.GetParentPath(track);
            }
            else dir = null;
        }
        else if (_selectedTrack >= 0 && _selectedTrack < _folderTracks.Count && !_folderTracks[_selectedTrack].IsDirectory)
        {
            track = _folderTracks[_selectedTrack].Path;
        }

        if (string.IsNullOrEmpty(dir))
        {
            SetAlbumArt(null, null);
            return;
        }

        // 元のパネルと同じフォルダならその一覧を使う。アーカイブ内は別スレッドで探す。
        IReadOnlyList<FileEntry>? cachedEntries = null;
        if (_sourcePane != null && string.Equals(dir, _sourcePane.CurrentPath, StringComparison.OrdinalIgnoreCase))
        {
            cachedEntries = _sourcePane.Entries.ToList();
        }

        string? imagePath;
        if (cachedEntries == null && ArchiveService.TrySplitArchivePath(dir, out _, out _))
        {
            string dirCopy = dir;
            string? trackCopy = track;
            imagePath = await Task.Run(() => FindAlbumArtImage(dirCopy, trackCopy, null));
            if (_closed || generation != _albumArtGeneration) return;
        }
        else
        {
            imagePath = FindAlbumArtImage(dir, track, cachedEntries);
        }

        // 前回と同じ画像なら読み直さない。違えば読み込んで表示する。
        if (string.Equals(imagePath, _lastAlbumArtPath, StringComparison.OrdinalIgnoreCase))
            return;

        if (imagePath == null)
        {
            SetAlbumArt(null, null);
            return;
        }

        var bitmap = await LoadAlbumArtBitmapAsync(imagePath);
        if (_closed || generation != _albumArtGeneration) return;
        SetAlbumArt(bitmap, imagePath);
    }

    /// <summary>
    /// フォルダからアートワークの画像を選ぶ。cover などの決まった名前、曲と同じ名前、名前順で最初の画像の順に探す。
    /// </summary>
    private static string? FindAlbumArtImage(string directory, string? trackPath, IReadOnlyList<FileEntry>? cachedEntries = null)
    {
        try
        {
            // パネルの一覧かアーカイブ内の一覧から探す。
            if (cachedEntries != null || ArchiveService.TrySplitArchivePath(directory, out _, out _))
            {
                IEnumerable<FileEntry> sourceEntries;
                if (cachedEntries != null)
                {
                    sourceEntries = cachedEntries;
                }
                else if (ArchiveService.TrySplitArchivePath(directory, out string archivePath, out string internalPath))
                {
                    sourceEntries = ArchiveService.ReadArchiveEntries(archivePath, internalPath);
                }
                else
                {
                    return null;
                }

                var entries = sourceEntries
                    .Where(e => !e.IsDirectory && ImageExtensions.Contains(Path.GetExtension(e.Name), StringComparer.OrdinalIgnoreCase))
                    .ToList();
                if (entries.Count == 0) return null;

                foreach (string pref in PreferredArtNames)
                {
                    var match = entries.FirstOrDefault(e => string.Equals(Path.GetFileNameWithoutExtension(e.Name), pref, StringComparison.OrdinalIgnoreCase));
                    if (match != null) return match.FullPath;
                }

                if (!string.IsNullOrEmpty(trackPath))
                {
                    string trackBase = Path.GetFileNameWithoutExtension(trackPath);
                    var match = entries.FirstOrDefault(e => string.Equals(Path.GetFileNameWithoutExtension(e.Name), trackBase, StringComparison.OrdinalIgnoreCase));
                    if (match != null) return match.FullPath;
                }

                var sorted = entries.OrderBy(e => e.Name, NaturalStringComparer.Instance).First();
                return sorted.FullPath;
            }

            // 通常のフォルダからファイルを列挙して探す。
            if (Directory.Exists(directory))
            {
                var files = Directory.EnumerateFiles(directory)
                    .Where(f => ImageExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                    .ToList();
                if (files.Count == 0) return null;

                foreach (string pref in PreferredArtNames)
                {
                    var match = files.FirstOrDefault(f => string.Equals(Path.GetFileNameWithoutExtension(f), pref, StringComparison.OrdinalIgnoreCase));
                    if (match != null) return match;
                }

                if (!string.IsNullOrEmpty(trackPath))
                {
                    string trackBase = Path.GetFileNameWithoutExtension(trackPath);
                    var match = files.FirstOrDefault(f => string.Equals(Path.GetFileNameWithoutExtension(f), trackBase, StringComparison.OrdinalIgnoreCase));
                    if (match != null) return match;
                }

                var sorted = files.OrderBy(f => f, NaturalStringComparer.Instance).First();
                return sorted;
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// アートワークの画像を読み込む。アーカイブ内なら一時ファイルへ展開する。失敗したら null を返す。
    /// </summary>
    private static async Task<BitmapSource?> LoadAlbumArtBitmapAsync(string imagePath)
    {
        try
        {
            string actualPath = imagePath;
            if (ArchiveService.IsArchiveEntry(imagePath))
            {
                string? extracted = await ArchiveService.ExtractEntryToTempFileAsync(imagePath);
                if (string.IsNullOrEmpty(extracted)) return null;
                actualPath = extracted;
            }
            if (!File.Exists(actualPath)) return null;

            return ImageFileDecoder.Decode(actualPath);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// アートワークの画像を表示する。枠は画像がなくても表示したままにする。
    /// </summary>
    private void SetAlbumArt(BitmapSource? bitmap, string? path)
    {
        _lastAlbumArtPath = path;
        AlbumArtImage.Source = bitmap;
        AlbumArtContainer.Visibility = Visibility.Visible;
    }
}
