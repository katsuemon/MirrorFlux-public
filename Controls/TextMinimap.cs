using System.Globalization;
using MirrorFlux.Services;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
namespace MirrorFlux.Controls;

/// <summary>
/// テキストビューア右端のミニマップ。文書全体の縮小表示、表示中の範囲、検索結果の印を描き、
/// クリックやドラッグでその位置へ移動させる。
/// </summary>
public sealed class TextMinimap : FrameworkElement
{
    private DrawingGroup? _content;
    private Size _contentSize;
    private int _contentCount;
    public void RefreshContent() { _content = null; InvalidateVisual(); }
    public Func<int>? Count { get; set; }
    public Func<int, string>? ReadLine { get; set; }
    public Func<(double Top, double Span)>? Viewport { get; set; }
    public Action<double>? Navigate { get; set; }
    public IReadOnlyList<double> Markers { get; set; } = [];
    public TextMinimap() { Width = 100; ClipToBounds = true; Cursor = Cursors.Hand; }
    /// <summary>縮小表示、表示範囲の枠、検索結果の印の順に描く。</summary>
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        // 背景を塗り、1行を3ピクセルとした地図の高さと描く行数を決める。
        dc.DrawRectangle(ThemeBrushes.Get("TextMinimapBackground", this), null, new Rect(RenderSize));
        int count = Count?.Invoke() ?? 0;
        if (count <= 0 || ActualHeight <= 0) return;
        var ink = ThemeBrushes.Get("MutedText", this);
        var typeface = new Typeface("Consolas");
        double mapHeight = Math.Min(ActualHeight, count * 3.0);
        int rows = Math.Min(count, Math.Max(1, (int)(ActualHeight / 3)));
        // 縮小表示は重いので、大きさか行数が変わったときだけ作り直す。行が多いときは間引き、各行は110文字までにする。
        if (_content == null || _contentSize != RenderSize || _contentCount != count)
        {
          _content = new DrawingGroup(); _contentSize = RenderSize; _contentCount = count;
          using var content = _content.Open();
          for (int y = 0; y < rows; y++)
          {
            int index = Math.Min(count - 1, (int)((long)y * count / rows));
            string line = ReadLine?.Invoke(index) ?? "";
            if (line.Length > 110) line = line[..110];
            var text = new FormattedText(line.Replace('\t', ' '), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                typeface, 2.4, ink, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            content.DrawText(text, new Point(3, y * 3));
          }
        }
        dc.DrawDrawing(_content);
        // 表示中の範囲を半透明の塗りと枠で示す。枠は最低5ピクセルの高さにする。
        var viewport = Viewport?.Invoke() ?? (0.0, 1.0);
        double top = Math.Clamp(viewport.Item1, 0, 1) * mapHeight;
        double height = Math.Clamp(viewport.Item2 * mapHeight, 5, Math.Max(5, mapHeight));
        var accent = ThemeBrushes.Get("HighlightText", this);
        dc.PushOpacity(0.23); dc.DrawRectangle(accent, null, new Rect(0, Math.Min(top, Math.Max(0, mapHeight - height)), ActualWidth, height)); dc.Pop();
        dc.DrawRectangle(null, new Pen(accent, 1), new Rect(0.5, Math.Min(top, Math.Max(0, mapHeight - height)) + 0.5, Math.Max(0, ActualWidth - 1), Math.Max(0, height - 1)));
        // 検索結果の位置を右端に小さな印で描く。
        foreach (double position in Markers)
            dc.DrawRectangle(ViewerPalette.Get(this, "SearchBackground"), null, new Rect(Math.Max(0, ActualWidth - 5), Math.Clamp(position, 0, 1) * mapHeight, 4, 2));
    }
    /// <summary>マウスの縦位置を地図上の割合に直して移動先として知らせる。</summary>
    private void Seek(MouseEventArgs e) { double height = Math.Min(ActualHeight, Math.Max(1, Count?.Invoke() ?? 1) * 3.0); if (height > 0) Navigate?.Invoke(Math.Clamp(e.GetPosition(this).Y / height, 0, 1)); }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e) { CaptureMouse(); Seek(e); e.Handled = true; }
    protected override void OnMouseMove(MouseEventArgs e) { if (IsMouseCaptured) Seek(e); }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) { ReleaseMouseCapture(); e.Handled = true; }
}
