using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MirrorFlux.Services;

namespace MirrorFlux.Controls;

/// <summary>画像を表示するグラフィックビューア。同じフォルダーやアーカイブ内の画像の前後移動、AIのアートボード切替、ズーム・回転・反転・背景切替を持つ。</summary>
public partial class GraphicViewerControl : UserControl, IViewerControl
{
    /// <summary>表示できる画像の拡張子。</summary>
    private static readonly HashSet<string> SupportedImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ai",
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".ico", ".tif", ".tiff", ".wdp", ".jxr",
        ".webp", ".heic", ".heif", ".avif", ".psd", ".dds", ".exr"
    };

    /// <summary>グラフィックビューアで開ける拡張子かを返す。</summary>
    public static bool IsSupportedExtension(string extension) =>
        SupportedImageExtensions.Contains(extension);

    private string _currentFilePath = string.Empty;
    private long _fileSize = 0;
    private BitmapSource? _originalBitmap;
    private double _imageWidth = 0;
    private double _imageHeight = 0;
    private string _imageFormatName = string.Empty;
    private CancellationTokenSource? _loadCts;
    private int _artboardIndex;
    private int _artboardCount;
    public Task PendingLoad { get; private set; } = Task.CompletedTask;

    // 同じフォルダー内で前後に移動する画像の一覧と、現在の位置。
    private string? _currentFolder;
    private List<string> _folderImages = [];
    private int _currentIndex = -1;

    private readonly ViewerViewport _viewport = new(0.05, 32, limitWindowFitToActualSize: true);

    // 左右反転の状態。
    private bool _isFlippedHorizontally = false;

    // 背景モード（0: 黒、1: 市松模様、2: 白）。
    private int _backgroundMode = 0;

    public string FilePath => _currentFilePath;
    public string StatusText { get; private set; } = string.Empty;
    public string LocationText { get; private set; } = string.Empty;
    public bool IsPromptActive => false;

    public event EventHandler? StatusChanged;
    public event EventHandler? CloseRequested;

    /// <summary>サイズ変更時の再フィットとマウス操作を設定する。</summary>
    public GraphicViewerControl()
    {
        InitializeComponent();
        // ウィンドウに合わせて表示しているときは、サイズ変更で合わせ直す。
        SizeChanged += (_, _) =>
        {
            if (_viewport.FitMode == ViewerFitMode.Window && _originalBitmap is not null)
            {
                FitToWindow();
            }
            else
            {
                UpdateTransform();
            }
        };
        Loaded += (_, _) => Focus();
        // ドラッグで移動し、ダブルクリックでウィンドウに合わせる表示と等倍を切り替える。
        _viewport.AttachMouse(RootGrid, this, UpdateTransform, () =>
        {
            if (_viewport.FitMode == ViewerFitMode.Window) SetZoom100();
            else FitToWindow();
        });
    }

    /// <summary>画像を開き、前後移動用に同じフォルダーの画像一覧を作る。</summary>
    public bool Load(string filePath)
    {
        // アーカイブ内の画像なら、ファイルがなければ一時フォルダーへ展開する。
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        if (!File.Exists(filePath))
        {
            ArchiveService.EnsureEntryExtracted(filePath);
        }

        if (!File.Exists(filePath))
        {
            return false;
        }

        // フォルダーが変わったときだけ画像一覧を作り直し、一覧上の位置を求める。
        _currentFilePath = Path.GetFullPath(filePath);
        var dir = Path.GetDirectoryName(_currentFilePath);
        if (!string.Equals(dir, _currentFolder, StringComparison.OrdinalIgnoreCase) || _folderImages.Count <= 1)
        {
            _currentFolder = dir;
            BuildFolderImageList(dir);
        }

        _currentIndex = _folderImages.FindIndex(f => string.Equals(f, _currentFilePath, StringComparison.OrdinalIgnoreCase));

        return LoadInternal(_currentFilePath);
    }

    /// <summary>前後移動に使う画像の一覧を作る。アーカイブ内なら書庫の画像を、そうでなければフォルダー内の画像を自然順で並べる。</summary>
    private void BuildFolderImageList(string? folder)
    {
        _folderImages.Clear();
        if (string.IsNullOrEmpty(folder))
        {
            return;
        }

        // アーカイブ内の画像なら、同じ書庫にある画像の一時ファイルパスを一覧にする。
        var archiveSiblings = ArchiveService.GetArchiveFolderImageTempPaths(_currentFilePath);
        if (archiveSiblings.Count > 0)
        {
            _folderImages = archiveSiblings.ToList();
            return;
        }

        // 通常のフォルダーなら、対応拡張子のファイルを自然順に並べる。
        if (!Directory.Exists(folder))
        {
            return;
        }

        try
        {
            var files = Directory.GetFiles(folder)
                .Where(f => IsSupportedExtension(Path.GetExtension(f)))
                .OrderBy(f => f, NaturalStringComparer.Instance)
                .ToList();
            _folderImages = files;
        }
        catch
        {
            _folderImages.Clear();
        }
    }

    /// <summary>画像を読み込んで表示する。AIは非同期でアートボードを読み込み、それ以外はここでデコードする。</summary>
    private bool LoadInternal(string filePath, int artboardIndex = 0, bool sameDocument = false)
    {
        // 前の読み込みを止める。同じ文書内のアートボード移動でなければアートボード数を初期化する。
        CancelPendingLoad();
        PendingLoad = Task.CompletedTask;
        _artboardIndex = artboardIndex;
        if (!sameDocument) _artboardCount = 0;
        try
        {
            // アーカイブ内の画像なら、ファイルがなければ展開する。
            if (!File.Exists(filePath))
            {
                ArchiveService.EnsureEntryExtracted(filePath);
            }

            if (!File.Exists(filePath))
            {
                return false;
            }

            var fileInfo = new FileInfo(filePath);
            _fileSize = fileInfo.Length;
            string ext = Path.GetExtension(filePath).ToLowerInvariant();

            // AIは読み込み中の表示を出して、指定したアートボードを非同期で読み込む。
            if (ext == ".ai")
            {
                _originalBitmap = null;
                MainImage.Source = null;
                _imageFormatName = "AI";
                OverlayMessageText.Text = "AI 読み込み中...";
                OverlayMessageText.Visibility = Visibility.Visible;
                UpdateStatus();
                var cts = new CancellationTokenSource();
                _loadCts = cts;
                PendingLoad = LoadIllustratorAsync(filePath, artboardIndex, cts);
                return true;
            }

            // それ以外の形式はすぐにデコードして表示する。
            BitmapSource bitmap = ImageFileDecoder.Decode(filePath);
            _imageFormatName = ext.TrimStart('.').ToUpperInvariant();

            SetBitmap(bitmap);
            return true;
        }
        catch (Exception ex)
        {
            ShowImageError(ex.Message);
            return false;
        }
    }

    /// <summary>AIのアートボードを非同期でデコードして表示する。取り消された結果は捨てる。</summary>
    private async Task LoadIllustratorAsync(string filePath, int artboardIndex, CancellationTokenSource cts)
    {
        try
        {
            var preview = await IllustratorImageDecoder.DecodeAsync(filePath, artboardIndex, cts.Token);
            if (cts.IsCancellationRequested) return;
            _artboardIndex = preview.ArtboardIndex;
            _artboardCount = preview.ArtboardCount;
            SetBitmap(preview.Bitmap);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested) ShowImageError(ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_loadCts, cts)) _loadCts = null;
            cts.Dispose();
        }
    }

    /// <summary>進行中のAI読み込みを止める。</summary>
    private void CancelPendingLoad()
    {
        _loadCts?.Cancel();
        _loadCts = null;
    }

    /// <summary>画像を設定し、回転・反転・倍率を初期化してウィンドウに合わせる。</summary>
    private void SetBitmap(BitmapSource bitmap)
    {
        _originalBitmap = bitmap;
        _imageWidth = bitmap.PixelWidth;
        _imageHeight = bitmap.PixelHeight;
        _viewport.Reset(_imageWidth, _imageHeight);
        _isFlippedHorizontally = false;
        ApplyImageSource();
        FitToWindow();
        OverlayMessageText.Visibility = Visibility.Collapsed;
        UpdateStatus();
    }

    /// <summary>画像を消して読み込みエラーを表示する。</summary>
    private void ShowImageError(string message)
    {
        _originalBitmap = null;
        _artboardCount = 0;
        MainImage.Source = null;
        OverlayMessageText.Text = $"画像を読み込めませんでした: {message}";
        OverlayMessageText.Visibility = Visibility.Visible;
        UpdateStatus();
    }

    /// <summary>回転と左右反転を反映した画像を表示する。</summary>
    private void ApplyImageSource()
    {
        if (_originalBitmap is null) return;

        MainImage.Source = ViewerVisuals.Transform(_originalBitmap, _viewport.RotationDegrees, _isFlippedHorizontally);
    }

    /// <summary>読み込みを止め、画像と一覧を解放する。</summary>
    public void Close()
    {
        CancelPendingLoad();
        MainImage.Source = null;
        _originalBitmap = null;
        _folderImages.Clear();
    }

    // ここからズーム・パン・フィットの処理。

    /// <summary>画像がウィンドウに収まるように合わせる。</summary>
    private void FitToWindow()
    {
        if (_originalBitmap is null || !_viewport.FitWindow(new Size(ActualWidth, ActualHeight))) return;
        UpdateTransform();
        UpdateStatus();
    }

    /// <summary>等倍表示にする。</summary>
    private void SetZoom100()
    {
        if (_originalBitmap is null) return;

        _viewport.ActualSize(new Size(ActualWidth, ActualHeight));
        UpdateTransform();
        UpdateStatus();
    }

    /// <summary>指定点を中心に拡大縮小する。</summary>
    private void ZoomAt(double factor, Point center)
    {
        if (_originalBitmap is null) return;

        if (!_viewport.ZoomAt(factor, center)) return;
        UpdateTransform();
        UpdateStatus();
    }

    /// <summary>表示倍率と位置を画像へ反映する。</summary>
    private void UpdateTransform()
    {
        if (_originalBitmap is null) return;

        _viewport.ApplyTo(ImageBorder);
    }

    // ここからマウス操作の処理。

    /// <summary>マウスホイールで、マウス位置を中心に拡大縮小する。</summary>
    private void RootGrid_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        var center = e.GetPosition(this);
        double factor = e.Delta > 0 ? 1.25 : 0.8;
        ZoomAt(factor, center);
    }

    // ここから前後の移動の処理。

    /// <summary>前後の画像へ移動する。AIに複数のアートボードがあれば、先にアートボードを移動する。</summary>
    private void NavigateRelative(int offset)
    {
        // 複数アートボードのAIでは、範囲内ならアートボードを移動する。フォルダーに画像が1つだけなら先頭と末尾をつなぐ。
        if (_artboardCount > 1)
        {
            int nextArtboard = _artboardIndex + offset;
            if (_folderImages.Count <= 1)
                nextArtboard = (nextArtboard + _artboardCount) % _artboardCount;
            if (nextArtboard >= 0 && nextArtboard < _artboardCount)
            {
                LoadInternal(_currentFilePath, nextArtboard, sameDocument: true);
                return;
            }
        }
        // フォルダー内の画像を、先頭と末尾をつないで移動する。
        if (_folderImages.Count <= 1) return;

        int newIndex = _currentIndex + offset;
        if (newIndex < 0) newIndex = _folderImages.Count - 1;
        else if (newIndex >= _folderImages.Count) newIndex = 0;

        // 前の画像へ戻るときは、最後のアートボードから表示する。
        _currentIndex = newIndex;
        _currentFilePath = _folderImages[_currentIndex];
        ArchiveService.EnsureEntryExtracted(_currentFilePath);
        LoadInternal(_currentFilePath, offset < 0 ? int.MaxValue : 0);
    }

    /// <summary>一覧の最初の画像へ移動する。</summary>
    private void NavigateFirst()
    {
        if (_folderImages.Count > 0)
        {
            _currentIndex = 0;
            _currentFilePath = _folderImages[0];
            ArchiveService.EnsureEntryExtracted(_currentFilePath);
            LoadInternal(_currentFilePath);
        }
    }

    /// <summary>一覧の最後の画像へ移動する。</summary>
    private void NavigateLast()
    {
        if (_folderImages.Count > 0)
        {
            _currentIndex = _folderImages.Count - 1;
            _currentFilePath = _folderImages[_currentIndex];
            ArchiveService.EnsureEntryExtracted(_currentFilePath);
            LoadInternal(_currentFilePath);
        }
    }

    // ここから回転・反転・背景の処理。

    /// <summary>右へ90度回転し、ウィンドウに合わせて表示しているときは合わせ直す。</summary>
    private void RotateRight()
    {
        _viewport.RotateRight();
        ApplyImageSource();
        if (_viewport.FitMode == ViewerFitMode.Window) FitToWindow();
        else UpdateTransform();
    }

    /// <summary>左右反転を切り替える。</summary>
    private void FlipHorizontal()
    {
        _isFlippedHorizontally = !_isFlippedHorizontally;
        ApplyImageSource();
        UpdateTransform();
    }

    /// <summary>背景を黒・市松・白の順に切り替える。</summary>
    private void CycleBackground()
    {
        _backgroundMode = ViewerVisuals.CycleBackground(RootGrid, this, _backgroundMode);
    }

    // ここからステータスバー更新の処理。

    /// <summary>ファイル名・形式・画像サイズ・倍率・アートボード・一覧上の位置を更新して通知する。</summary>
    private void UpdateStatus()
    {
        // ファイルサイズは、PDFビューアやファイル一覧と同じ表記にする。
        string fileName = Path.GetFileName(_currentFilePath);
        string sizeStr = Models.FileEntry.FormatFileSize(_fileSize);
        string zoomStr = _viewport.ZoomLabel;

        string dimStr = _originalBitmap is not null
            ? $"{_imageFormatName} {_imageWidth:F0}x{_imageHeight:F0}"
            : _imageFormatName;

        StatusText = $"{fileName}  [{dimStr}]  {sizeStr}  {zoomStr}";
        if (_originalBitmap is not null && _artboardCount > 0)
            StatusText += $"  Artboard {_artboardIndex + 1}/{_artboardCount}";

        // 位置表示は、一覧上の番号と総数にする。
        LocationText = _folderImages.Count > 0
            ? ViewerVisuals.PositionText("Img", _currentIndex >= 0 ? _currentIndex + 1 : 1, _folderImages.Count)
            : ViewerVisuals.PositionText("Img", 1, 1);

        StatusChanged?.Invoke(this, EventArgs.Empty);
    }


    // ここからキーボード処理。

    /// <summary>ビューアのキー操作を処理する。処理したら true を返す。</summary>
    public bool HandleKeyDown(KeyEventArgs e)
    {
        // キーを操作に変換し、スクロール操作なら画像を動かす。
        var action = ViewerInput.Map(ViewerInput.Normalize(e), Keyboard.Modifiers,
            hasWidthFit: false, hasFlip: true);
        Point pan = ViewerInput.PanDelta(action);
        if (pan != new Point(0, 0))
        {
            _viewport.Pan(pan.X, pan.Y);
            UpdateTransform();
            UpdateStatus();
            return true;
        }

        // それ以外の操作を実行する。
        switch (action)
        {
            case ViewerKeyAction.Close:
                CloseRequested?.Invoke(this, EventArgs.Empty);
                break;
            case ViewerKeyAction.Previous: NavigateRelative(-1); break;
            case ViewerKeyAction.Next: NavigateRelative(1); break;
            case ViewerKeyAction.First: NavigateFirst(); break;
            case ViewerKeyAction.Last: NavigateLast(); break;
            case ViewerKeyAction.ZoomIn: ZoomAt(1.25, new Point(ActualWidth / 2, ActualHeight / 2)); break;
            case ViewerKeyAction.ZoomOut: ZoomAt(0.8, new Point(ActualWidth / 2, ActualHeight / 2)); break;
            case ViewerKeyAction.FitWindow: FitToWindow(); break;
            case ViewerKeyAction.ActualSize: SetZoom100(); break;
            case ViewerKeyAction.FlipHorizontal: FlipHorizontal(); break;
            case ViewerKeyAction.RotateRight: RotateRight(); break;
            case ViewerKeyAction.CycleBackground: CycleBackground(); break;
            default: return false;
        }
        return true;
    }

    /// <summary>ファンクションキーの操作を実行する。</summary>
    public void ExecuteFunctionKey(int keyNumber)
    {
        // 番号ごとに、ヘルプ・フィット切替・前後の画像・Photoshopで開く・左右反転・回転・背景・再読込・外部アプリで開く・閉じるを実行する。
        switch (keyNumber)
        {
            case 1:
                MessageBox.Show(
                    "グラフィックビューアー操作一覧:\n\n" +
                    "  [Enter / Esc / Q / V / F12] 閉じて一覧へ戻る\n" +
                    "  [← / → / ↑ / ↓ / Space / BackSpace / F3 / F4] 前の画像 / 次の画像\n" +
                    "  AIはアートボード順に移動し、最後まで進むと次のファイルへ\n" +
                    "  [Shift + ↑ / ↓ / ← / →] 画像のスクロール移動\n" +
                    "  [PageUp / PageDn / + / -] 拡大 / 縮小ズーム\n" +
                    "  [Home / End] 最初の画像 / 最後の画像\n" +
                    "  [マウスホイール] 拡大・縮小ズーム\n" +
                    "  [左ドラッグ] 画像のパン移動\n" +
                    "  [ダブルクリック / 0 / F2] ウィンドウにフィット / 等倍トグル\n" +
                    "  [F5] Photoshop で開く\n" +
                    "  [F6 / H] 左右反転\n" +
                    "  [F7 / R] 90度時計回り回転\n" +
                    "  [F8 / B] 背景切替 (黒 / 市松 / 白)\n" +
                    "  [F9] 再読込\n" +
                    "  [F10] 既定の外部アプリで開く\n",
                    "グラフィックビューアー ヘルプ",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                break;
            case 2:
                if (_viewport.FitMode == ViewerFitMode.Window) SetZoom100();
                else FitToWindow();
                break;
            case 3:
                NavigateRelative(-1);
                break;
            case 4:
                NavigateRelative(1);
                break;
            case 5:
                if (!PhotoshopLauncher.Send(_currentFilePath))
                {
                    MessageBox.Show("Photoshop が見つかりませんでした。", "Photoshop 連携", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                break;
            case 6:
                FlipHorizontal();
                break;
            case 7:
                RotateRight();
                break;
            case 8:
                CycleBackground();
                break;
            case 9:
                LoadInternal(_currentFilePath);
                break;
            case 10:
                ViewerVisuals.OpenWithDefaultApp(_currentFilePath);
                break;

            case 12:
                CloseRequested?.Invoke(this, EventArgs.Empty);
                break;
        }
    }
}
