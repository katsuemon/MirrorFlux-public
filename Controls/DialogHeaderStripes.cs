using System.Windows;
using System.Windows.Media;

using MirrorFlux.Services;

namespace MirrorFlux.Controls;

/// <summary>
/// ダイアログ見出しの横縞を描く部品。
/// モニターの拡大率に関係なく、物理ピクセル単位で同じ太さと間隔の縞にする。
/// </summary>
public sealed class DialogHeaderStripes : FrameworkElement
{
    /// <summary>
    /// 縞の輪郭をぼかさない設定にし、表示後に描き直す。
    /// </summary>
    public DialogHeaderStripes()
    {
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
        Loaded += (_, _) => InvalidateVisual();
    }

    /// <summary>
    /// DPIが変わったら物理ピクセルの位置を計算し直すため描き直す。
    /// </summary>
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        InvalidateVisual();
    }

    /// <summary>
    /// 2ピクセルの線と2ピクセルの隙間を、物理ピクセルに合わせて上下中央に並べて描く。
    /// </summary>
    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        // ウィンドウ内の位置を物理ピクセルで求め、描ける範囲の上端と下端を整数に丸める。
        var dpi = VisualTreeHelper.GetDpi(this);
        var origin = new Point();
        if (PresentationSource.FromVisual(this)?.RootVisual is Visual root)
            origin = TransformToAncestor(root).Transform(new Point());

        double originY = origin.Y * dpi.DpiScaleY;
        int top = (int)Math.Ceiling(originY);
        int bottom = (int)Math.Floor(originY + ActualHeight * dpi.DpiScaleY);
        // 上下の余白を残して入る線の本数を決める。
        const int thickness = 2;
        const int gap = 2;
        const int minimumInset = 2;
        int count = (bottom - top - 2 * minimumInset + gap) / (thickness + gap);
        if (count <= 0 || ActualWidth <= 0) return;

        int patternHeight = count * (thickness + gap) - gap;
        // 縞全体を上下中央に置く。高さが奇数ピクセルのときは1物理ピクセルずれるのは避けられない。
        int start = top + (bottom - top - patternHeight) / 2;
        // 物理ピクセルの位置を論理座標に戻して1本ずつ描く。
        for (int index = 0; index < count; index++)
        {
            double y = (start + index * (thickness + gap) - originY) / dpi.DpiScaleY;
            drawingContext.DrawRectangle(ThemeBrushes.Get("ViewerBlack", this), null,
                new Rect(0, y, ActualWidth, thickness / dpi.DpiScaleY));
        }
    }
}
