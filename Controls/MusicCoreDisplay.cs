using System.Windows;
using System.Windows.Media;
using System.Globalization;

using MirrorFlux.Services;

namespace MirrorFlux.Controls;

/// <summary>
/// サウンドビューアで FM コアや外部機器（NanoDrive など）の名前を短く表示する部品。
/// カウンター類と同じ左端の目印と2行の見出しを使う。
/// </summary>
public sealed class MusicCoreDisplay : FrameworkElement
{
    private string _text = "—";
    public string LabelLine1 { get; set; } = "FM";
    public string LabelLine2 { get; set; } = "CORE";
    public FontFamily FontFamily { get; } = new("Consolas, BIZ UDGothic, MS Gothic");
    public double FontSize => 14;
    public string Text { get => _text; set { _text = value ?? "—"; InvalidateVisual(); } }
    public MusicCoreDisplay() { SnapsToDevicePixels = true; Focusable = false; }
    protected override Size MeasureOverride(Size availableSize) => new(114,19);
    /// <summary>目印と見出しを左に描き、値を右寄せで描く。</summary>
    protected override void OnRender(DrawingContext dc)
    {
        if(ActualWidth<=0 || ActualHeight<=0) return;
        // 左端の目印と2行の見出しを描く。
        var accent=ThemeBrushes.Get("AccentText", this);
        var highlight=ThemeBrushes.Get("HighlightText", this);
        var muted=ThemeBrushes.Get("MutedText", this);
        dc.DrawRectangle(highlight,null,new Rect(0,0,4,18.67));
        MusicDisplayDrawing.TextAt(dc,LabelLine1,9,.5,8.5,muted);
        MusicDisplayDrawing.TextAt(dc,LabelLine2,9,9.5,8.5,muted);
        // 値が入りきらないときは文字を縮め（最小9）、それでも長ければ末尾を省略する。
        const double start=49;
        double available=Math.Max(1,ActualWidth-start);
        double fontSize = FontSize;
        var typeface = new Typeface(FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var text = new FormattedText(_text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            typeface, fontSize, highlight, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        if (text.WidthIncludingTrailingWhitespace > available && available > 10)
        {
            fontSize = Math.Max(9.0, fontSize * (available / text.WidthIncludingTrailingWhitespace));
            text = new FormattedText(_text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                typeface, fontSize, highlight, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        }
        text.MaxTextWidth = available;
        text.MaxLineCount = 1;
        text.Trimming = TextTrimming.CharacterEllipsis;
        // 右寄せ、上下中央に描く。
        dc.DrawText(text,new Point(Math.Max(start,ActualWidth-text.WidthIncludingTrailingWhitespace),
            Math.Max(0,(ActualHeight-text.Height)/2)));
    }
}
