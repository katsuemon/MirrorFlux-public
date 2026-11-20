using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MirrorFlux.Input;

namespace MirrorFlux.Controls;

/// <summary>表示の合わせ方。ウィンドウに合わせる、幅に合わせる、手動（拡大率と位置を利用者が決める）。</summary>
internal enum ViewerFitMode { Window, Width, Manual }

/// <summary>
/// 画像ビューアと PDF ビューアで共通の表示位置・拡大率・回転の計算。
/// 合わせるときの余白と拡大率の上限・下限は各ビューアが渡す。
/// </summary>
internal sealed class ViewerViewport(double minimumZoom, double maximumZoom, bool limitWindowFitToActualSize)
{
    private Point _lastDragPosition;

    public double SourceWidth { get; private set; }
    public double SourceHeight { get; private set; }
    public double Zoom { get; private set; } = 1;
    public double PanX { get; private set; }
    public double PanY { get; private set; }
    public int RotationDegrees { get; private set; }
    public ViewerFitMode FitMode { get; private set; } = ViewerFitMode.Window;
    public bool IsDragging { get; private set; }

    public Size RotatedSize => RotationDegrees % 180 == 0
        ? new Size(SourceWidth, SourceHeight)
        : new Size(SourceHeight, SourceWidth);

    /// <summary>元の大きさを設定し、回転・拡大率・位置をウィンドウに合わせる状態へ戻す。</summary>
    public void Reset(double width = 0, double height = 0)
    {
        SourceWidth = width;
        SourceHeight = height;
        RotationDegrees = 0;
        Zoom = 1;
        PanX = PanY = 0;
        FitMode = ViewerFitMode.Window;
        IsDragging = false;
    }

    public void SetSourceSize(double width, double height)
    {
        SourceWidth = width;
        SourceHeight = height;
    }

    /// <summary>
    /// 全体が表示領域に収まる拡大率にして中央に置く。inset があれば最低40を残して余白を引く。
    /// 原寸を上限とする設定なら、小さいものは拡大しない。
    /// </summary>
    public bool FitWindow(Size view, double inset = 0)
    {
        Size source = RotatedSize;
        if (view.Width <= 0 || view.Height <= 0 || source.Width <= 0 || source.Height <= 0) return false;
        double availableWidth = inset == 0 ? view.Width : Math.Max(40, view.Width - inset);
        double availableHeight = inset == 0 ? view.Height : Math.Max(40, view.Height - inset);
        Zoom = Math.Min(availableWidth / source.Width, availableHeight / source.Height);
        if (limitWindowFitToActualSize) Zoom = Math.Min(Zoom, 1);
        Center(view, source);
        FitMode = ViewerFitMode.Window;
        return true;
    }

    /// <summary>
    /// 幅を表示領域に合わせ、横は中央に置く。縦は高さが収まらなければ上端から8の位置に置く。
    /// </summary>
    public bool FitWidth(Size view, double inset)
    {
        Size source = RotatedSize;
        if (view.Width <= 0 || view.Height <= 0 || source.Width <= 0 || source.Height <= 0) return false;
        Zoom = Math.Max(40, view.Width - inset) / source.Width;
        PanX = (view.Width - source.Width * Zoom) / 2;
        PanY = Math.Min(8, (view.Height - source.Height * Zoom) / 2);
        if (source.Height * Zoom > view.Height) PanY = 8;
        FitMode = ViewerFitMode.Width;
        return true;
    }

    /// <summary>原寸（100%）にして中央に置く。</summary>
    public void ActualSize(Size view)
    {
        Zoom = 1;
        Center(view, RotatedSize);
        FitMode = ViewerFitMode.Manual;
    }

    /// <summary>
    /// 指定した点を中心に拡大率を factor 倍する。上限・下限で変わらなければ false を返す。
    /// </summary>
    public bool ZoomAt(double factor, Point center)
    {
        double next = Math.Clamp(Zoom * factor, minimumZoom, maximumZoom);
        if (Math.Abs(next - Zoom) < 0.0001) return false;
        // 中心点の下にある元画像上の位置が、拡大後も同じ画面位置に来るよう表示位置を直す。
        double relativeX = (center.X - PanX) / Zoom;
        double relativeY = (center.Y - PanY) / Zoom;
        Zoom = next;
        PanX = center.X - relativeX * Zoom;
        PanY = center.Y - relativeY * Zoom;
        FitMode = ViewerFitMode.Manual;
        return true;
    }

    /// <summary>表示位置をずらす。通常は手動の合わせ方に切り替える。</summary>
    public void Pan(double x, double y, bool setManual = true)
    {
        PanX += x;
        PanY += y;
        if (setManual) FitMode = ViewerFitMode.Manual;
    }

    public void BeginDrag(Point position)
    {
        _lastDragPosition = position;
        IsDragging = true;
    }

    /// <summary>ドラッグ中なら、前回の位置からの移動量だけ表示位置をずらす。</summary>
    public bool DragTo(Point position)
    {
        if (!IsDragging) return false;
        Pan(position.X - _lastDragPosition.X, position.Y - _lastDragPosition.Y);
        _lastDragPosition = position;
        return true;
    }

    public void EndDrag() => IsDragging = false;

    /// <summary>ステータス行の拡大率表記（[Fit: 50%] / [Width: 80%] / [100%]）。</summary>
    public string ZoomLabel => FitMode switch
    {
        ViewerFitMode.Window => $"[Fit: {Zoom * 100:F0}%]",
        ViewerFitMode.Width => $"[Width: {Zoom * 100:F0}%]",
        _ => $"[{Zoom * 100:F0}%]"
    };

    /// <summary>
    /// surface 上の左ボタンのドラッグで表示位置を動かし（位置は relativeTo 基準、動いたら onMoved）、
    /// ダブルクリックで onDoubleClick を呼ぶ。
    /// </summary>
    public void AttachMouse(FrameworkElement surface, IInputElement relativeTo, Action onMoved, Action onDoubleClick)
    {
        // 左ボタンを押したら、ダブルクリックならその処理を呼び、そうでなければドラッグを始める。
        surface.MouseDown += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Left) return;
            if (e.ClickCount == 2)
            {
                onDoubleClick();
                return;
            }
            BeginDrag(e.GetPosition(relativeTo));
            surface.CaptureMouse();
        };
        // 左ボタンを押したまま動かしたら表示位置をずらし、離したらドラッグを終える。
        surface.MouseMove += (_, e) =>
        {
            if (e.LeftButton == MouseButtonState.Pressed && DragTo(e.GetPosition(relativeTo))) onMoved();
        };
        surface.MouseUp += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Left || !IsDragging) return;
            EndDrag();
            surface.ReleaseMouseCapture();
        };
    }

    public void RotateRight() => RotationDegrees = (RotationDegrees + 90) % 360;

    /// <summary>計算した大きさと位置を、表示用の枠に反映する。</summary>
    public void ApplyTo(Border border)
    {
        Size source = RotatedSize;
        border.Width = source.Width * Zoom;
        border.Height = source.Height * Zoom;
        Canvas.SetLeft(border, PanX);
        Canvas.SetTop(border, PanY);
    }

    /// <summary>現在の拡大率で表示領域の中央に置く。</summary>
    private void Center(Size view, Size source)
    {
        PanX = (view.Width - source.Width * Zoom) / 2;
        PanY = (view.Height - source.Height * Zoom) / 2;
    }
}

/// <summary>画像ビューアと PDF ビューアで共通の表示用の補助。</summary>
internal static class ViewerVisuals
{
    /// <summary>位置表記（例 "Img 3/10  [30%]"、"Page 2/8  [25%]"）。number は1から。</summary>
    public static string PositionText(string unit, int number, int count) =>
        $"{unit} {number}/{count}  [{(double)number / count * 100.0:F0}%]";

    /// <summary>ファイルを Windows の既定のアプリで開く。開けなくても何もしない。</summary>
    public static void OpenWithDefaultApp(string path)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); }
        catch { }
    }

    /// <summary>
    /// 画像を左右反転してから回転した新しい画像を返す。どちらもなければ元の画像をそのまま返す。
    /// </summary>
    public static BitmapSource Transform(BitmapSource original, int rotationDegrees, bool flipHorizontally = false)
    {
        if (rotationDegrees == 0 && !flipHorizontally) return original;
        // 反転、回転の順に、画像の中心を基準とした変換を重ねる。
        var transforms = new TransformGroup();
        if (flipHorizontally)
            transforms.Children.Add(new ScaleTransform(-1, 1, original.PixelWidth / 2.0, original.PixelHeight / 2.0));
        if (rotationDegrees != 0)
            transforms.Children.Add(new RotateTransform(rotationDegrees, original.PixelWidth / 2.0, original.PixelHeight / 2.0));
        // 変換済みの画像を作り、別スレッドからも使えるよう固定する。
        var transformed = new TransformedBitmap();
        transformed.BeginInit();
        transformed.Source = original;
        transformed.Transform = transforms;
        transformed.EndInit();
        transformed.Freeze();
        return transformed;
    }

    /// <summary>背景を 画面の背景色 → 市松模様 → 白 の順に切り替え、新しい番号を返す。</summary>
    public static int CycleBackground(Grid grid, FrameworkElement resourceOwner, int currentMode)
    {
        int next = (currentMode + 1) % 3;
        string resource = next switch
        {
            1 => "CheckerboardBrush",
            2 => "ViewerWhite",
            _ => "ScreenBackground"
        };
        grid.Background = (Brush)resourceOwner.FindResource(resource);
        return next;
    }
}

/// <summary>画像ビューアと PDF ビューアの固定キーに対応する操作。</summary>
internal enum ViewerKeyAction
{
    None, Close, Previous, Next, First, Last,
    ZoomIn, ZoomOut, PanUp, PanDown, PanLeft, PanRight,
    FitWindow, FitWidth, ActualSize, FlipHorizontal, RotateRight, CycleBackground
}

/// <summary>画像ビューアと PDF ビューアのキーを操作に変換する。</summary>
internal static class ViewerInput
{
    public static Key Normalize(KeyEventArgs e) => e.NormalizedKey();

    /// <summary>
    /// キーと修飾キーを操作に変換する。Shift+矢印は表示位置の移動、矢印だけなら前後のページや画像へ移る。
    /// 幅合わせと左右反転は、そのビューアが対応しているときだけ有効にする。
    /// </summary>
    public static ViewerKeyAction Map(Key key, ModifierKeys modifiers, bool hasWidthFit, bool hasFlip)
    {
        bool shift = (modifiers & ModifierKeys.Shift) != 0;
        return key switch
        {
            Key.Return or Key.Enter or Key.Escape or Key.Q or Key.V => ViewerKeyAction.Close,
            Key.PageUp or Key.OemPlus or Key.Add => ViewerKeyAction.ZoomIn,
            Key.PageDown or Key.OemMinus or Key.Subtract => ViewerKeyAction.ZoomOut,
            Key.Up => shift ? ViewerKeyAction.PanDown : ViewerKeyAction.Previous,
            Key.Down => shift ? ViewerKeyAction.PanUp : ViewerKeyAction.Next,
            Key.Left => shift ? ViewerKeyAction.PanRight : ViewerKeyAction.Previous,
            Key.Right => shift ? ViewerKeyAction.PanLeft : ViewerKeyAction.Next,
            Key.Back or Key.P => ViewerKeyAction.Previous,
            Key.Space or Key.N => ViewerKeyAction.Next,
            Key.Home => ViewerKeyAction.First,
            Key.End => ViewerKeyAction.Last,
            Key.D0 or Key.NumPad0 => ViewerKeyAction.FitWindow,
            Key.D1 or Key.NumPad1 => ViewerKeyAction.ActualSize,
            Key.W when hasWidthFit => ViewerKeyAction.FitWidth,
            Key.H when hasFlip => ViewerKeyAction.FlipHorizontal,
            Key.R when modifiers == ModifierKeys.None => ViewerKeyAction.RotateRight,
            Key.B => ViewerKeyAction.CycleBackground,
            _ => ViewerKeyAction.None
        };
    }

    /// <summary>表示位置移動の操作に対応する移動量（64単位）を返す。</summary>
    public static Point PanDelta(ViewerKeyAction action) => action switch
    {
        ViewerKeyAction.PanUp => new Point(0, -64),
        ViewerKeyAction.PanDown => new Point(0, 64),
        ViewerKeyAction.PanLeft => new Point(-64, 0),
        ViewerKeyAction.PanRight => new Point(64, 0),
        _ => new Point(0, 0)
    };
}
