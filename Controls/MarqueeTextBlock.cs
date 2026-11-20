using System;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

using MirrorFlux.Services;

namespace MirrorFlux.Controls;

/// <summary>
/// 幅に収まらない文字列を左へ流して表示する文字表示部品。
/// 各周回の始めに一時停止し、末尾の後に間隔を空けて先頭をつなげ、切れ目なく繰り返す。
/// </summary>
public sealed class MarqueeTextBlock : FrameworkElement
{
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(
            nameof(Text),
            typeof(string),
            typeof(MarqueeTextBlock),
            new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender, OnVisualAffectingPropertyChanged));

    public static readonly DependencyProperty ForegroundProperty =
        DependencyProperty.Register(
            nameof(Foreground),
            typeof(Brush),
            typeof(MarqueeTextBlock),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnVisualAffectingPropertyChanged));

    public static readonly DependencyProperty FontFamilyProperty =
        DependencyProperty.Register(
            nameof(FontFamily),
            typeof(FontFamily),
            typeof(MarqueeTextBlock),
            new FrameworkPropertyMetadata(new FontFamily("Consolas, BIZ UDGothic, MS Gothic"), FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender, OnVisualAffectingPropertyChanged));

    public static readonly DependencyProperty FontSizeProperty =
        DependencyProperty.Register(
            nameof(FontSize),
            typeof(double),
            typeof(MarqueeTextBlock),
            new FrameworkPropertyMetadata(13.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender, OnVisualAffectingPropertyChanged));

    public static readonly DependencyProperty FontWeightProperty =
        DependencyProperty.Register(
            nameof(FontWeight),
            typeof(FontWeight),
            typeof(MarqueeTextBlock),
            new FrameworkPropertyMetadata(FontWeights.Normal, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender, OnVisualAffectingPropertyChanged));

    public static readonly DependencyProperty FontStyleProperty =
        DependencyProperty.Register(
            nameof(FontStyle),
            typeof(FontStyle),
            typeof(MarqueeTextBlock),
            new FrameworkPropertyMetadata(FontStyles.Normal, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender, OnVisualAffectingPropertyChanged));

    public static readonly DependencyProperty ScrollSpeedProperty =
        DependencyProperty.Register(
            nameof(ScrollSpeed),
            typeof(double),
            typeof(MarqueeTextBlock),
            new FrameworkPropertyMetadata(35.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty InitialPauseSecondsProperty =
        DependencyProperty.Register(
            nameof(InitialPauseSeconds),
            typeof(double),
            typeof(MarqueeTextBlock),
            new FrameworkPropertyMetadata(2.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty GapProperty =
        DependencyProperty.Register(
            nameof(Gap),
            typeof(double),
            typeof(MarqueeTextBlock),
            new FrameworkPropertyMetadata(40.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public Brush? Foreground
    {
        get => (Brush?)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public FontFamily FontFamily
    {
        get => (FontFamily)GetValue(FontFamilyProperty);
        set => SetValue(FontFamilyProperty, value);
    }

    public double FontSize
    {
        get => (double)GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    public FontWeight FontWeight
    {
        get => (FontWeight)GetValue(FontWeightProperty);
        set => SetValue(FontWeightProperty, value);
    }

    public FontStyle FontStyle
    {
        get => (FontStyle)GetValue(FontStyleProperty);
        set => SetValue(FontStyleProperty, value);
    }

    public double ScrollSpeed
    {
        get => (double)GetValue(ScrollSpeedProperty);
        set => SetValue(ScrollSpeedProperty, value);
    }

    public double InitialPauseSeconds
    {
        get => (double)GetValue(InitialPauseSecondsProperty);
        set => SetValue(InitialPauseSecondsProperty, value);
    }

    public double Gap
    {
        get => (double)GetValue(GapProperty);
        set => SetValue(GapProperty, value);
    }

    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private FormattedText? _cachedFormattedText;
    private double _cachedTextWidth;
    private bool _isOverflowing;
    private bool _isSubscribed;

    /// <summary>はみ出しを切り取り、表示状態に応じてアニメーションを開始・停止するよう登録する。</summary>
    public MarqueeTextBlock()
    {
        ClipToBounds = true;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += OnIsVisibleChanged;
    }

    /// <summary>文字列や書式が変わったら、組み直した文字で測り直して描き直す。</summary>
    private static void OnVisualAffectingPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MarqueeTextBlock control)
        {
            // 文字列が変わったときだけ、流れを先頭（一時停止）からやり直す。
            if (e.Property == TextProperty && !Equals(e.OldValue, e.NewValue))
            {
                control._stopwatch.Restart();
            }
            control._cachedFormattedText = null;
            control.InvalidateMeasure();
            control.InvalidateVisual();
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _stopwatch.Restart();
        UpdateSubscription();
    }

    /// <summary>画面から外れたら毎フレームの描画呼び出しを解除する。</summary>
    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_isSubscribed)
        {
            CompositionTarget.Rendering -= OnRendering;
            _isSubscribed = false;
        }
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        UpdateSubscription();
    }

    /// <summary>DPIが変わったら文字を組み直して描き直す。</summary>
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        _cachedFormattedText = null;
        InvalidateVisual();
    }

    /// <summary>組んだ文字とその幅を用意する。組み済みなら何もしない。</summary>
    private void EnsureFormattedText()
    {
        if (_cachedFormattedText != null) return;
        // 空文字なら何も描かない。
        string text = Text;
        if (string.IsNullOrEmpty(text))
        {
            _cachedFormattedText = null;
            _cachedTextWidth = 0;
            return;
        }

        // 現在のDPIと書式で文字を組む。色の指定がなければテーマの強調色を使う。
        var dpi = VisualTreeHelper.GetDpi(this);
        var typeface = new Typeface(FontFamily, FontStyle, FontWeight, FontStretches.Normal);
        Brush brush = Foreground ?? ThemeBrushes.Get("HighlightText", this);
        _cachedFormattedText = new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            typeface,
            FontSize,
            brush,
            dpi.PixelsPerDip);
        _cachedTextWidth = _cachedFormattedText.WidthIncludingTrailingWhitespace;
    }

    /// <summary>文字の幅を希望サイズとし、与えられた幅を超えない範囲で返す。</summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        EnsureFormattedText();
        double h = _cachedFormattedText?.Height ?? (FontSize * 1.3);
        double w = double.IsInfinity(availableSize.Width) ? (_cachedTextWidth > 0 ? _cachedTextWidth : 0) : Math.Min(availableSize.Width, _cachedTextWidth);
        return new Size(w, h);
    }

    /// <summary>確定した幅で、文字がはみ出すかどうかを判定し直す。</summary>
    protected override Size ArrangeOverride(Size finalSize)
    {
        EnsureFormattedText();
        UpdateOverflowState(finalSize.Width);
        return finalSize;
    }

    /// <summary>はみ出し状態が変わったときだけ、アニメーションの開始・停止を切り替える。</summary>
    private void UpdateOverflowState(double availableWidth)
    {
        bool overflowing = _cachedFormattedText != null && availableWidth > 0 && _cachedTextWidth > availableWidth;
        if (overflowing != _isOverflowing)
        {
            _isOverflowing = overflowing;
            UpdateSubscription();
        }
    }

    /// <summary>
    /// はみ出していて、表示中で、読み込み済みのときだけ毎フレームの描画呼び出しを登録する。
    /// 流す必要がないときは登録を外し、無駄な描画をしない。
    /// </summary>
    private void UpdateSubscription()
    {
        bool shouldAnimate = _isOverflowing && IsVisible && IsLoaded;
        if (shouldAnimate && !_isSubscribed)
        {
            CompositionTarget.Rendering += OnRendering;
            _isSubscribed = true;
        }
        else if (!shouldAnimate && _isSubscribed)
        {
            CompositionTarget.Rendering -= OnRendering;
            _isSubscribed = false;
        }
    }

    private double _lastRenderedOffset = -1;

    /// <summary>
    /// 経過時間から現在の左へのずらし量を求める。
    /// 1周は「一時停止」と「文字幅＋間隔を流す時間」で、一時停止中は 0 を返す。
    /// </summary>
    private double ComputeCurrentOffset()
    {
        double cycleDistance = _cachedTextWidth + Gap;
        double speed = Math.Max(1.0, ScrollSpeed);
        double scrollDuration = cycleDistance / speed;
        double pause = Math.Max(0.0, InitialPauseSeconds);
        double totalCycle = pause + scrollDuration;
        double elapsed = _stopwatch.Elapsed.TotalSeconds;
        double cycleTime = totalCycle > 0 ? elapsed % totalCycle : 0;
        return cycleTime < pause ? 0 : (cycleTime - pause) * speed;
    }

    /// <summary>毎フレーム呼ばれ、ずらし量が0.25以上変わったときだけ描き直す。</summary>
    private void OnRendering(object? sender, EventArgs e)
    {
        if (_isOverflowing && IsVisible)
        {
            double offset = ComputeCurrentOffset();
            if (Math.Abs(offset - _lastRenderedOffset) >= 0.25)
            {
                InvalidateVisual();
            }
        }
    }

    /// <summary>文字を上下中央に描く。はみ出すときはずらして描き、後ろに2つ目の複製をつなげる。</summary>
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        EnsureFormattedText();
        if (_cachedFormattedText == null || string.IsNullOrEmpty(Text)) return;

        double width = ActualWidth;
        double height = ActualHeight;
        if (width <= 0 || height <= 0) return;

        // 枠の外へ描かないよう切り取る。
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, width, height)));

        double y = Math.Max(0, (height - _cachedFormattedText.Height) / 2);

        // 収まる文字は左寄せでそのまま描く。
        if (!_isOverflowing)
        {
            _lastRenderedOffset = 0;
            dc.DrawText(_cachedFormattedText, new Point(0, y));
            dc.Pop();
            return;
        }

        // はみ出す文字はずらして描き、間隔の後ろに同じ文字を続けて描いて切れ目をなくす。
        double cycleDistance = _cachedTextWidth + Gap;
        double offset = ComputeCurrentOffset();
        _lastRenderedOffset = offset;

        dc.DrawText(_cachedFormattedText, new Point(-offset, y));

        double secondCopyX = -offset + cycleDistance;
        if (secondCopyX < width)
        {
            dc.DrawText(_cachedFormattedText, new Point(secondCopyX, y));
        }

        dc.Pop();
    }
}
