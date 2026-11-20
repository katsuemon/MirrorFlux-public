using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MirrorFlux.Services;

namespace MirrorFlux.Controls;

/// <summary>Windows標準のPDFレンダラーでページを画像にして表示するPDFビューア。ページ送り・ズーム・回転・背景切替を持つ。</summary>
public partial class PdfViewerControl : UserControl, IViewerControl
{
    /// <summary>PDFビューアで開ける拡張子かを返す。</summary>
    public static bool IsSupportedExtension(string extension) =>
        string.Equals(extension, ".pdf", StringComparison.OrdinalIgnoreCase);

    private string _currentFilePath = string.Empty;
    private long _fileSize;
    private WindowsPdfDocument? _document;
    private CancellationTokenSource? _renderCts;
    private CancellationTokenSource? _hiResCts;
    private readonly SemaphoreSlim _renderGate = new(1, 1);
    private readonly ConcurrentDictionary<int, (BitmapSource Bitmap, double PageWidth, double PageHeight)> _pageCache = new();

    private int _currentPageIndex;
    private int _pageCount;
    private BitmapSource? _currentBitmap;
    private double _pageWidth = 612;
    private double _pageHeight = 792;

    private readonly ViewerViewport _viewport = new(0.1, 16, limitWindowFitToActualSize: false);

    // 背景の表示モード（黒・市松・白）。
    private int _backgroundMode;

    public string FilePath => _currentFilePath;
    public string StatusText { get; private set; } = string.Empty;
    public string LocationText { get; private set; } = string.Empty;
    public bool IsPromptActive => false;

    public event EventHandler? StatusChanged;
    public event EventHandler? CloseRequested;

    /// <summary>サイズ変更時の再フィットとマウス操作を設定する。</summary>
    public PdfViewerControl()
    {
        InitializeComponent();
        // サイズが変わったら、現在のフィット方法で合わせ直す。
        SizeChanged += (_, _) =>
        {
            if (_currentBitmap is null) return;
            if (_viewport.FitMode == ViewerFitMode.Window) FitToWindow();
            else if (_viewport.FitMode == ViewerFitMode.Width) FitToWidth();
            else UpdateTransform();
        };
        Loaded += (_, _) => Focus();
        // ドラッグで移動し、ダブルクリックで全体表示と横幅合わせを切り替える。
        _viewport.AttachMouse(RootGrid, this, UpdateTransform, () =>
        {
            if (_viewport.FitMode == ViewerFitMode.Window) FitToWidth();
            else FitToWindow();
        });
    }

    /// <summary>PDFを開く。読み込み中の表示を出し、文書と最初のページの読み込みは非同期で進める。</summary>
    public bool Load(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return false;
        }

        // パスとサイズを記録し、ページと表示倍率を初期化する。
        _currentFilePath = Path.GetFullPath(filePath);
        try
        {
            _fileSize = new FileInfo(_currentFilePath).Length;
        }
        catch
        {
            _fileSize = 0;
        }

        _currentPageIndex = 0;
        _viewport.Reset();

        // ページの代わりに読み込み中の表示を出す。
        OverlayMessageText.Text = "PDF 読み込み中...";
        OverlayMessageText.Visibility = Visibility.Visible;
        PageBorder.Visibility = Visibility.Hidden;

        _ = LoadDocumentAndFirstPageAsync(_currentFilePath);
        return true;
    }

    /// <summary>前の文書と描画を破棄してPDFを開き、最初のページを描画する。</summary>
    private async Task LoadDocumentAndFirstPageAsync(string filePath)
    {
        // 進行中の描画を止め、前の文書とページキャッシュを破棄する。
        _renderCts?.Cancel();
        _renderCts?.Dispose();
        _hiResCts?.Cancel();
        _hiResCts?.Dispose();
        var cts = new CancellationTokenSource();
        _renderCts = cts;

        _pageCache.Clear();
        _document?.Dispose();
        _document = null;

        try
        {
            // 文書を開く。開いている間に別の読み込みが始まったら捨てる。
            var doc = await WindowsPdfDocument.OpenAsync(filePath, cts.Token);
            if (cts.IsCancellationRequested)
            {
                doc?.Dispose();
                return;
            }

            // 開けないときやページがないときは、パスワード保護かどうかを含めてエラーを表示する。
            if (doc is null || doc.PageCount == 0)
            {
                doc?.Dispose();
                OverlayMessageText.Text = doc?.IsPasswordProtected == true
                    ? "パスワード保護された PDF は開けません"
                    : "PDF を読み込めませんでした";
                OverlayMessageText.Visibility = Visibility.Visible;
                StatusText = $"{Path.GetFileName(filePath)}  [PDF 読込エラー]";
                LocationText = "Page 0/0";
                StatusChanged?.Invoke(this, EventArgs.Empty);
                return;
            }

            // 文書を採用して最初のページを描画する。
            _document = doc;
            _pageCount = (int)doc.PageCount;
            _currentPageIndex = 0;

            await RenderCurrentPageAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            OverlayMessageText.Text = $"PDF エラー: {ex.Message}";
            OverlayMessageText.Visibility = Visibility.Visible;
        }
    }

    /// <summary>通常描画の高さ（ピクセル）。画面の高さの1.2倍を720から1440の範囲に収める。</summary>
    private uint GetBaseRenderHeight() =>
        (uint)Math.Clamp(Math.Max(ActualHeight * 1.2, 960.0), 720.0, 1440.0);

    /// <summary>現在のページを描画して表示する。キャッシュがあればそれを使い、表示後に前後のページを先読みする。</summary>
    private async Task RenderCurrentPageAsync(CancellationToken cancellationToken)
    {
        var doc = _document;
        int pageIndex = _currentPageIndex;
        if (doc is null || pageIndex < 0 || pageIndex >= _pageCount)
        {
            return;
        }

        // キャッシュにあればすぐに表示して先読みへ進む。
        if (_pageCache.TryGetValue(pageIndex, out var cached))
        {
            PresentPageBitmap(cached.Bitmap, cached.PageWidth, cached.PageHeight, resetFit: true);
            _ = PrefetchAdjacentPagesAsync(doc, pageIndex, cancellationToken);
            return;
        }

        // 描画は1件ずつ行う。待っている間に先読みで描画済みになっていればそれを使う。
        uint renderHeight = GetBaseRenderHeight();
        (BitmapSource? Bitmap, WindowsPdfDocument.PdfPageSize PageSize) result;

        await _renderGate.WaitAsync(cancellationToken);
        try
        {
            if (_pageCache.TryGetValue(pageIndex, out cached))
            {
                result = (cached.Bitmap, new WindowsPdfDocument.PdfPageSize { Width = (float)cached.PageWidth, Height = (float)cached.PageHeight });
            }
            else
            {
                result = await doc.RenderPageAsync((uint)pageIndex, 0, renderHeight, cancellationToken);
                if (result.Bitmap is not null)
                {
                    // ページサイズが取れなければ画像のピクセル数を使う。キャッシュは16ページを超えたら空にする。
                    double pw = result.PageSize.Width > 0 ? result.PageSize.Width : result.Bitmap.PixelWidth;
                    double ph = result.PageSize.Height > 0 ? result.PageSize.Height : result.Bitmap.PixelHeight;
                    if (_pageCache.Count > 16)
                    {
                        _pageCache.Clear();
                    }
                    _pageCache[pageIndex] = (result.Bitmap, pw, ph);
                }
            }
        }
        finally
        {
            _renderGate.Release();
        }

        // 待っている間に取り消されたり、別のページへ移ったりしていれば表示しない。
        if (cancellationToken.IsCancellationRequested || result.Bitmap is null || _currentPageIndex != pageIndex)
        {
            return;
        }

        double pageW = result.PageSize.Width > 0 ? result.PageSize.Width : result.Bitmap.PixelWidth;
        double pageH = result.PageSize.Height > 0 ? result.PageSize.Height : result.Bitmap.PixelHeight;
        PresentPageBitmap(result.Bitmap, pageW, pageH, resetFit: true);

        _ = PrefetchAdjacentPagesAsync(doc, pageIndex, cancellationToken);
    }

    /// <summary>描画したページ画像を表示し、必要ならフィット方法に合わせて倍率を決め直す。</summary>
    private void PresentPageBitmap(BitmapSource bmp, double pageWidth, double pageHeight, bool resetFit)
    {
        // ページサイズを記録して画像を表示する。
        _currentBitmap = bmp;
        _pageWidth = pageWidth;
        _pageHeight = pageHeight;
        _viewport.SetSourceSize(pageWidth, pageHeight);

        ApplyBitmapSource();
        OverlayMessageText.Visibility = Visibility.Collapsed;
        PageBorder.Visibility = Visibility.Visible;
        PageBadgeBorder.Visibility = Visibility.Visible;

        // フィット表示中は合わせ直す。フィット処理がステータスも更新する。
        if (resetFit)
        {
            if (_viewport.FitMode == ViewerFitMode.Window)
            {
                FitToWindow();
                return;
            }
            if (_viewport.FitMode == ViewerFitMode.Width)
            {
                FitToWidth();
                return;
            }
        }

        UpdateTransform();
        UpdateStatus();
    }

    /// <summary>前後のページを順に描画してキャッシュへ入れる。文書が替わったら止める。</summary>
    private async Task PrefetchAdjacentPagesAsync(WindowsPdfDocument doc, int centerPageIndex, CancellationToken cancellationToken)
    {
        uint renderHeight = GetBaseRenderHeight();
        int[] candidates = [centerPageIndex + 1, centerPageIndex - 1];

        foreach (int idx in candidates)
        {
            if (cancellationToken.IsCancellationRequested || !ReferenceEquals(_document, doc))
            {
                return;
            }
            if (idx < 0 || idx >= _pageCount || _pageCache.ContainsKey(idx))
            {
                continue;
            }

            try
            {
                await _renderGate.WaitAsync(cancellationToken);
                try
                {
                    if (!_pageCache.ContainsKey(idx) && ReferenceEquals(_document, doc))
                    {
                        var (bmp, pageSize) = await doc.RenderPageAsync((uint)idx, 0, renderHeight, cancellationToken);
                        if (bmp is not null)
                        {
                            double pw = pageSize.Width > 0 ? pageSize.Width : bmp.PixelWidth;
                            double ph = pageSize.Height > 0 ? pageSize.Height : bmp.PixelHeight;
                            _pageCache[idx] = (bmp, pw, ph);
                        }
                    }
                }
                finally
                {
                    _renderGate.Release();
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
            }
        }
    }

    /// <summary>拡大で表示画像の解像度が足りなくなったら、少し待ってから高解像度で描き直す。</summary>
    private void ScheduleHighResRenderIfNeeded()
    {
        var doc = _document;
        var bmp = _currentBitmap;
        if (doc is null || bmp is null) return;

        // 必要な高さが今の画像の1.25倍以下なら描き直さない。上限は2800ピクセル。
        uint desiredHeight = (uint)Math.Clamp(_pageHeight * _viewport.Zoom, GetBaseRenderHeight(), 2800.0);
        if (desiredHeight <= bmp.PixelHeight * 1.25)
        {
            return;
        }

        // 前の高解像度描画を止めて新しく始める。
        _hiResCts?.Cancel();
        _hiResCts?.Dispose();
        var cts = new CancellationTokenSource();
        _hiResCts = cts;
        int pageIndex = _currentPageIndex;

        _ = Task.Run(async () =>
        {
            try
            {
                // ズーム操作が続く間は待ち、描画は他の描画と1件ずつ行う。
                await Task.Delay(100, cts.Token);
                await _renderGate.WaitAsync(cts.Token);
                (BitmapSource? Bitmap, WindowsPdfDocument.PdfPageSize PageSize) rendered;
                try
                {
                    if (cts.IsCancellationRequested || !ReferenceEquals(_document, doc)) return;
                    rendered = await doc.RenderPageAsync((uint)pageIndex, 0, desiredHeight, cts.Token);
                }
                finally
                {
                    _renderGate.Release();
                }

                if (cts.IsCancellationRequested || rendered.Bitmap is null) return;

                // 同じ文書の同じページを表示しているときだけ、UIスレッドで画像を差し替える。
                await Dispatcher.InvokeAsync(() =>
                {
                    if (!cts.IsCancellationRequested && ReferenceEquals(_document, doc) && _currentPageIndex == pageIndex)
                    {
                        _currentBitmap = rendered.Bitmap;
                        ApplyBitmapSource();
                    }
                });
            }
            catch
            {
            }
        }, cts.Token);
    }

    /// <summary>現在の回転を反映した画像をページに設定する。</summary>
    private void ApplyBitmapSource()
    {
        if (_currentBitmap is null) return;

        PageImage.Source = ViewerVisuals.Transform(_currentBitmap, _viewport.RotationDegrees);
    }

    /// <summary>描画を止め、キャッシュと文書を解放する。</summary>
    public void Close()
    {
        _renderCts?.Cancel();
        _renderCts?.Dispose();
        _renderCts = null;
        _hiResCts?.Cancel();
        _hiResCts?.Dispose();
        _hiResCts = null;

        _pageCache.Clear();
        PageImage.Source = null;
        _currentBitmap = null;

        _document?.Dispose();
        _document = null;
    }

    // ここからページ移動の処理。

    /// <summary>現在のページから delta ページ移動する。</summary>
    private void NavigatePage(int delta)
    {
        if (_document is null || _pageCount <= 0) return;
        int next = Math.Clamp(_currentPageIndex + delta, 0, _pageCount - 1);
        if (next == _currentPageIndex) return;

        _currentPageIndex = next;
        TriggerPageRender();
    }

    /// <summary>指定したページへ移動する。</summary>
    private void NavigateToPage(int index)
    {
        if (_document is null || _pageCount <= 0) return;
        int next = Math.Clamp(index, 0, _pageCount - 1);
        if (next == _currentPageIndex) return;

        _currentPageIndex = next;
        TriggerPageRender();
    }

    /// <summary>進行中の描画を止め、現在のページを描画し直す。</summary>
    private void TriggerPageRender()
    {
        _renderCts?.Cancel();
        _renderCts?.Dispose();
        var cts = new CancellationTokenSource();
        _renderCts = cts;
        _ = RenderCurrentPageAsync(cts.Token);
    }

    // ここからズームとフィットの処理。

    /// <summary>ページ全体が画面に収まるように合わせる。</summary>
    private void FitToWindow()
    {
        if (_currentBitmap is null || !_viewport.FitWindow(new Size(ActualWidth, ActualHeight), 16)) return;
        UpdateTransform();
        UpdateStatus();
    }

    /// <summary>ページの横幅を画面に合わせる。</summary>
    private void FitToWidth()
    {
        if (_currentBitmap is null || !_viewport.FitWidth(new Size(ActualWidth, ActualHeight), 24)) return;
        UpdateTransform();
        UpdateStatus();
    }

    /// <summary>等倍表示にする。</summary>
    private void SetZoom100()
    {
        if (_currentBitmap is null) return;

        _viewport.ActualSize(new Size(ActualWidth, ActualHeight));
        UpdateTransform();
        UpdateStatus();
    }

    /// <summary>指定点を中心に拡大縮小し、必要なら高解像度で描き直す。</summary>
    private void ZoomAt(double factor, Point center)
    {
        if (_currentBitmap is null) return;

        if (!_viewport.ZoomAt(factor, center)) return;
        UpdateTransform();
        UpdateStatus();
        ScheduleHighResRenderIfNeeded();
    }

    /// <summary>表示倍率と位置をページへ反映する。</summary>
    private void UpdateTransform()
    {
        if (_currentBitmap is null) return;

        _viewport.ApplyTo(PageBorder);
    }

    // ここから回転と背景の処理。

    /// <summary>右へ90度回転し、フィット表示中は合わせ直す。</summary>
    private void RotateRight()
    {
        _viewport.RotateRight();
        ApplyBitmapSource();
        if (_viewport.FitMode == ViewerFitMode.Window) FitToWindow();
        else if (_viewport.FitMode == ViewerFitMode.Width) FitToWidth();
        else UpdateTransform();
    }

    /// <summary>背景を黒・市松・白の順に切り替える。</summary>
    private void CycleBackground()
    {
        _backgroundMode = ViewerVisuals.CycleBackground(RootGrid, this, _backgroundMode);
    }

    // ここからマウス操作の処理。

    /// <summary>Ctrl+ホイールで拡大縮小し、それ以外は表示状態に応じてページ移動かスクロールをする。</summary>
    private void RootGrid_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        // Ctrl付きはマウス位置を中心に拡大縮小する。
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            var center = e.GetPosition(this);
            ZoomAt(e.Delta > 0 ? 1.25 : 0.8, center);
            return;
        }

        // 全体表示中はホイールで前後のページへ移動し、拡大中は縦にスクロールする。
        if (_viewport.FitMode == ViewerFitMode.Window)
        {
            NavigatePage(e.Delta > 0 ? -1 : 1);
        }
        else
        {
            _viewport.Pan(0, e.Delta > 0 ? 60 : -60, setManual: false);
            UpdateTransform();
        }
    }

    // ここからステータス更新の処理。

    /// <summary>ファイル名・ページサイズ・倍率とページ番号を更新して通知する。</summary>
    private void UpdateStatus()
    {
        string fileName = Path.GetFileName(_currentFilePath);
        string sizeStr = Models.FileEntry.FormatFileSize(_fileSize);
        string fitLabel = _viewport.ZoomLabel;

        StatusText = $"{fileName}  [PDF {_pageWidth:F0}x{_pageHeight:F0}]  {sizeStr}  {fitLabel}";

        if (_pageCount > 0)
        {
            int pageNum = _currentPageIndex + 1;
            LocationText = ViewerVisuals.PositionText("Page", pageNum, _pageCount);
            PageBadgeText.Text = $"Page {pageNum} / {_pageCount}";
        }
        else
        {
            LocationText = "Page 0/0";
            PageBadgeText.Text = "Page 0 / 0";
        }

        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    // ここからキーボード操作の処理。

    /// <summary>ビューアのキー操作を処理する。処理したら true を返す。</summary>
    public bool HandleKeyDown(KeyEventArgs e)
    {
        // キーを操作に変換し、スクロール操作ならページを動かす。
        var action = ViewerInput.Map(ViewerInput.Normalize(e), Keyboard.Modifiers,
            hasWidthFit: true, hasFlip: false);
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
            case ViewerKeyAction.Previous: NavigatePage(-1); break;
            case ViewerKeyAction.Next: NavigatePage(1); break;
            case ViewerKeyAction.First: NavigateToPage(0); break;
            case ViewerKeyAction.Last: NavigateToPage(_pageCount - 1); break;
            case ViewerKeyAction.ZoomIn: ZoomAt(1.25, new Point(ActualWidth / 2, ActualHeight / 2)); break;
            case ViewerKeyAction.ZoomOut: ZoomAt(0.8, new Point(ActualWidth / 2, ActualHeight / 2)); break;
            case ViewerKeyAction.FitWindow: FitToWindow(); break;
            case ViewerKeyAction.FitWidth: FitToWidth(); break;
            case ViewerKeyAction.ActualSize: SetZoom100(); break;
            case ViewerKeyAction.RotateRight: RotateRight(); break;
            case ViewerKeyAction.CycleBackground: CycleBackground(); break;
            default: return false;
        }
        return true;
    }

    /// <summary>ファンクションキーの操作を実行する。</summary>
    public void ExecuteFunctionKey(int keyNumber)
    {
        // 番号ごとに、ヘルプ・フィット切替・ページ移動・幅合わせ・縮小・回転・背景・再読込・外部アプリで開く・閉じるを実行する。
        switch (keyNumber)
        {
            case 1:
                MessageBox.Show(
                    "PDFビューアー操作一覧:\n\n" +
                    "  [Enter / Esc / Q / V / F12] 閉じて一覧へ戻る\n" +
                    "  [← / → / ↑ / ↓ / Space / BackSpace / F3 / F4] 前ページ / 次ページ\n" +
                    "  [Shift + ↑ / ↓ / ← / →] ページのスクロール移動\n" +
                    "  [PageUp / PageDn / + / -] 拡大 / 縮小ズーム\n" +
                    "  [Home / End] 最初のページ / 最後のページ\n" +
                    "  [0 / F2] 全体フィット / 等倍トグル\n" +
                    "  [W / F5] 横幅に合わせる (Fit Width)\n" +
                    "  [F6] 縮小ズーム\n" +
                    "  [R / F7] 90度時計回り回転\n" +
                    "  [B / F8] 背景切替 (黒 / 市松 / 白)\n" +
                    "  [F9] 再読込\n" +
                    "  [F10] 既定の外部アプリで開く\n",
                    "PDFビューアー ヘルプ",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                break;
            case 2:
                if (_viewport.FitMode == ViewerFitMode.Window) SetZoom100();
                else FitToWindow();
                break;
            case 3:
                NavigatePage(-1);
                break;
            case 4:
                NavigatePage(1);
                break;
            case 5:
                FitToWidth();
                break;
            case 6:
                ZoomAt(0.8, new Point(ActualWidth / 2, ActualHeight / 2));
                break;
            case 7:
                RotateRight();
                break;
            case 8:
                CycleBackground();
                break;
            case 9:
                _ = LoadDocumentAndFirstPageAsync(_currentFilePath);
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

