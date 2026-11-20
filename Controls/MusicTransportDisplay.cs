using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

using MirrorFlux.Services;

namespace MirrorFlux.Controls;

/// <summary>
/// サウンドビューアのトランスポート表示。回転するリール、シークバー、再生操作の行、再生モードの行を描く。
/// リールは直径約47ピクセルで、上端をシークバーと同じ Y=7 にそろえる。
/// シークバーは幅2・高さ8ピクセルの棒を並べ、右端を7セグメント表示の右端にそろえる。
/// 操作の行(PLAY/STOP/PAUSE/REW/FF)とモードの行(PLAYALL/REPEAT/SHUFFLE/LOOP)は13ピクセル太字で、シークバーの幅に均等に配置する。
/// </summary>
public sealed class MusicTransportDisplay : FrameworkElement
{
    private static readonly Brush FallbackHighlight = ThemeBrushes.Get("HighlightText");
    private static readonly Brush FallbackMuted = ThemeBrushes.Get("MutedText");
    private static readonly Brush FallbackPanOff = ThemeBrushes.Get("MusicInactiveWhiteKey");
    private static readonly Brush FallbackDisabled = ThemeBrushes.Get("MusicInactiveWhiteBorder");
    private static readonly Brush HoverHighlight = ThemeBrushes.Get("MusicTransportHover");
    private static readonly Brush HoverMuted = ThemeBrushes.Get("MusicTransportHoverMuted");
    private static readonly Brush Trailing1 = ThemeBrushes.Get("MusicTransportTrail1");
    private static readonly Brush Trailing2 = ThemeBrushes.Get("MusicTransportTrail2");
    private static readonly Brush Trailing3 = ThemeBrushes.Get("MusicTransportTrail3");
    private static readonly Typeface Font = new(new FontFamily("Consolas, Segoe UI Symbol, Lucida Sans Unicode, Meiryo"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    private const double ReelCenterX = 23.5;
    private const double ReelCenterY = 30.5;
    private const double ReelOuterRadius = 23.5;
    private const double ReelInnerRadius = 11.5;
    private const double BarStartX = 54.0;

    private bool _isPlaying;
    private bool _isPaused;
    private TimeSpan _currentTime;
    private TimeSpan _totalTime;
    private TimeSpan _loopDuration;
    private TimeSpan _loopStartCurrentTime = TimeSpan.Zero;
    private TimeSpan _currentLoopDuration = TimeSpan.Zero;
    private int _loopCount;
    private bool _isPlayAllEnabled;
    private bool _isRepeatEnabled;
    private bool _isShuffleEnabled;
    // 目標ループ回数。0 は無限ループ、1 と 2 はその回数で次へ進む。
    private int _targetLoops;
    private int _spinPhase;
    private bool _isDraggingSeekBar;
    private ControlElement _hoveredElement = ControlElement.None;

    /// <summary>
    /// マウスで指せる表示上の部品の種類。
    /// </summary>
    public enum ControlElement
    {
        None,
        Reel,
        SeekBar,
        Play,
        Stop,
        Pause,
        Rewind,
        FastForward,
        PlayAll,
        Repeat,
        Shuffle,
        Loop
    }

    public event Action? PlayClicked;
    public event Action? StopClicked;
    public event Action? PauseClicked;
    public event Action? FastForwardClicked;
    public event Action? RewindClicked;
    public event Action? PlayAllClicked;
    public event Action? RepeatClicked;
    public event Action? ShuffleClicked;
    public event Action? LoopToggleClicked;
    public event Action<TimeSpan>? SeekRequested;

    private bool _canSeek;
    /// <summary>
    /// シークできる曲かどうか。無効にするとドラッグ中のシークとシーク系のホバーを解除する。
    /// </summary>
    public bool CanSeek
    {
        get => _canSeek;
        set
        {
            if (_canSeek == value) return;
            _canSeek = value;
            // シーク不可になったら、ドラッグとシーク系部品のホバーを解除する。
            if (!value)
            {
                if (_isDraggingSeekBar) { _isDraggingSeekBar = false; ReleaseMouseCapture(); }
                if (_hoveredElement is ControlElement.SeekBar or ControlElement.Rewind or ControlElement.FastForward)
                {
                    _hoveredElement = ControlElement.None;
                    Cursor = Cursors.Arrow;
                }
            }
            InvalidateVisual();
        }
    }

    public bool IsPlaying => _isPlaying;
    public bool IsPaused => _isPaused;
    public TimeSpan CurrentTime => _currentTime;
    public TimeSpan TotalTime => _totalTime;
    public int LoopCount => _loopCount;
    /// <summary>
    /// シークバーの進み具合(0～1)。ループ後はそのループの開始位置からの割合で示す。
    /// </summary>
    public double SeekBarRatio
    {
        get
        {
            double loopDur = _currentLoopDuration > TimeSpan.Zero ? _currentLoopDuration.TotalSeconds : _totalTime.TotalSeconds;
            double elapsed = Math.Max(0.0, (_currentTime - _loopStartCurrentTime).TotalSeconds);
            return loopDur > 0.0 ? Math.Clamp(elapsed / loopDur, 0.0, 1.0) : 0.0;
        }
    }

    /// <summary>
    /// 全曲再生の状態。無限ループのまま有効にすると、次へ進めるように2ループへ切り替える。
    /// </summary>
    public bool IsPlayAllEnabled
    {
        get => _isPlayAllEnabled;
        set
        {
            if (value && !_isPlayAllEnabled && _targetLoops == 0) _targetLoops = 2;
            _isPlayAllEnabled = value;
            InvalidateVisual();
        }
    }

    public bool IsRepeatEnabled
    {
        get => _isRepeatEnabled;
        set { _isRepeatEnabled = value; InvalidateVisual(); }
    }

    public bool IsShuffleEnabled
    {
        get => _isShuffleEnabled;
        set { _isShuffleEnabled = value; InvalidateVisual(); }
    }

    /// <summary>
    /// 目標ループ回数。1 と 2 以外は無限ループ(0)として扱う。
    /// </summary>
    public int TargetLoops
    {
        get => _targetLoops;
        set
        {
            _targetLoops = value switch
            {
                1 => 1,
                2 => 2,
                _ => 0
            };
            InvalidateVisual();
        }
    }

    public MusicTransportDisplay()
    {
        Height = 60;
        SnapsToDevicePixels = true;
        ClipToBounds = true;
        Focusable = false;
    }

    /// <summary>
    /// 高さは60に固定し、幅は使える幅いっぱい(不明なら320)にする。
    /// </summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsFinite(availableSize.Width) && availableSize.Width > 0
            ? availableSize.Width
            : 320;
        return new Size(width, 60);
    }

    /// <summary>
    /// テーマのブラシを探し、見つからなければ既定のブラシを返す。
    /// </summary>
    private Brush GetBrush(string resourceKey, Brush fallback)
    {
        if (TryFindResource(resourceKey) is Brush b) return b;
        if (Application.Current?.TryFindResource(resourceKey) is Brush ab) return ab;
        return fallback;
    }

    /// <summary>
    /// 再生状態と時間を受け取り、変化があったときだけ再描画する。
    /// </summary>
    public void UpdateState(bool isPlaying, bool isPaused, TimeSpan currentTime, TimeSpan totalTime, int loopCount, TimeSpan loopDuration = default)
    {
        // 再生状態と時間の変化を記録する。
        bool changed = false;
        if (_isPlaying != isPlaying) { _isPlaying = isPlaying; changed = true; }
        if (_isPaused != isPaused) { _isPaused = isPaused; changed = true; }
        if (_currentTime != currentTime) { _currentTime = currentTime; changed = true; }
        if (_totalTime != totalTime)
        {
            _totalTime = totalTime;
            changed = true;
            if (_loopCount == 0) _currentLoopDuration = totalTime;
        }
        if (_loopDuration != loopDuration) { _loopDuration = loopDuration; changed = true; }

        // シークバーの基準となるループ開始位置と長さを更新する。
        if (loopCount != _loopCount)
        {
            if (loopCount > _loopCount)
            {
                // ループしたので、シークバーを先頭へ戻す。
                _loopStartCurrentTime = currentTime;
                _currentLoopDuration = loopDuration > TimeSpan.Zero ? loopDuration : totalTime;
            }
            else
            {
                // 曲が変わった、または先頭へ戻ったのでリセットする。
                _loopStartCurrentTime = TimeSpan.Zero;
                _currentLoopDuration = loopDuration > TimeSpan.Zero ? loopDuration : totalTime;
            }
            _loopCount = loopCount;
            changed = true;
        }
        else if (currentTime < _loopStartCurrentTime)
        {
            _loopStartCurrentTime = currentTime;
            _currentLoopDuration = loopDuration > TimeSpan.Zero ? loopDuration : totalTime;
            changed = true;
        }
        else if (_currentLoopDuration <= TimeSpan.Zero)
        {
            _currentLoopDuration = loopDuration > TimeSpan.Zero ? loopDuration : totalTime;
            if (_currentLoopDuration > TimeSpan.Zero) changed = true;
        }

        if (_isPlaying && !_isPaused)
        {
            // 再生位置に合わせてリールを回す。1秒あたり約16段進める。
            int phase = (int)(currentTime.TotalMilliseconds / 62.5) % 24;
            if (phase != _spinPhase) { _spinPhase = phase; changed = true; }
        }

        if (changed) InvalidateVisual();
    }

    /// <summary>
    /// シークバーの範囲を求める。右端を整数位置にそろえ、4ピクセル間隔の棒が右端で終わるように最初の棒の位置を決める。
    /// </summary>
    private static void ComputeLayoutBounds(double barStartX, double barEndX, out double firstBarX, out double seekRight)
    {
        seekRight = Math.Floor(barEndX);
        const double barW = 2.0;
        const double pitch = 4.0;
        double lastBarX = seekRight - barW;
        int barCount = Math.Max(1, (int)Math.Floor((lastBarX - barStartX) / pitch) + 1);
        firstBarX = lastBarX - (barCount - 1) * pitch;
    }

    /// <summary>
    /// 指定位置にある部品を返す。シークできない曲では REW と FF を反応させない。
    /// </summary>
    private ControlElement HitTest(Point pt)
    {
        // 範囲外、リール、シークバーを順に判定する。
        if (pt.X < 0 || pt.X > ActualWidth || pt.Y < 0 || pt.Y > 60) return ControlElement.None;
        if (pt.X <= 49.0 && pt.Y >= 2 && pt.Y <= 56) return ControlElement.Reel;

        ComputeLayoutBounds(BarStartX, ActualWidth, out double firstBarX, out double seekRight);
        if (CanSeek && pt.X >= firstBarX && pt.X <= seekRight && pt.Y >= 2 && pt.Y <= 17) return ControlElement.SeekBar;

        // 操作の行(Y=20～39)を判定する。
        if (pt.Y >= 20 && pt.Y <= 39)
        {
            var row1 = GetRow1Layout(firstBarX, seekRight);
            foreach (var item in row1)
            {
                if (pt.X >= item.HitLeft && pt.X <= item.HitRight)
                    return !CanSeek && (item.Element is ControlElement.Rewind or ControlElement.FastForward)
                        ? ControlElement.None : item.Element;
            }
        }

        // モードの行(Y=40～59)を判定する。
        if (pt.Y >= 40 && pt.Y <= 59)
        {
            var row2 = GetRow2Layout(firstBarX, seekRight);
            foreach (var item in row2)
            {
                if (pt.X >= item.HitLeft && pt.X <= item.HitRight)
                    return item.Element;
            }
        }

        return ControlElement.None;
    }

    /// <summary>
    /// ドラッグ中はシークし、それ以外はホバー中の部品とカーソルを更新する。
    /// </summary>
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Point pt = e.GetPosition(this);
        // シークバーをドラッグ中なら位置に合わせてシークする。
        if (_isDraggingSeekBar)
        {
            SeekFromPoint(pt);
            return;
        }

        // ホバー中の部品が変わったら強調表示を切り替える。
        var hit = HitTest(pt);
        if (hit != _hoveredElement)
        {
            _hoveredElement = hit;
            Cursor = hit != ControlElement.None ? Cursors.Hand : Cursors.Arrow;
            InvalidateVisual();
        }
    }

    /// <summary>
    /// マウスが離れたらホバー表示を消す。
    /// </summary>
    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoveredElement != ControlElement.None)
        {
            _hoveredElement = ControlElement.None;
            Cursor = Cursors.Arrow;
            InvalidateVisual();
        }
    }

    /// <summary>
    /// クリックした部品に応じて、シーク開始、操作イベントの通知、モードの切り替えを行う。
    /// </summary>
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Point pt = e.GetPosition(this);
        var hit = HitTest(pt);
        switch (hit)
        {
            case ControlElement.SeekBar:
                _isDraggingSeekBar = true;
                CaptureMouse();
                SeekFromPoint(pt);
                break;
            case ControlElement.Play:
                PlayClicked?.Invoke();
                break;
            case ControlElement.Stop:
                StopClicked?.Invoke();
                break;
            case ControlElement.Pause:
                PauseClicked?.Invoke();
                break;
            case ControlElement.Rewind:
                RewindClicked?.Invoke();
                break;
            case ControlElement.FastForward:
                FastForwardClicked?.Invoke();
                break;
            case ControlElement.PlayAll:
                IsPlayAllEnabled = !IsPlayAllEnabled;
                PlayAllClicked?.Invoke();
                break;
            case ControlElement.Repeat:
                _isRepeatEnabled = !_isRepeatEnabled;
                InvalidateVisual();
                RepeatClicked?.Invoke();
                break;
            case ControlElement.Shuffle:
                _isShuffleEnabled = !_isShuffleEnabled;
                InvalidateVisual();
                ShuffleClicked?.Invoke();
                break;
            case ControlElement.Loop:
                TargetLoops = TargetLoops switch
                {
                    0 => 1,
                    1 => 2,
                    _ => 0
                };
                LoopToggleClicked?.Invoke();
                break;
        }
        e.Handled = true;
    }

    /// <summary>
    /// シークバーのドラッグを終える。
    /// </summary>
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_isDraggingSeekBar)
        {
            _isDraggingSeekBar = false;
            ReleaseMouseCapture();
            e.Handled = true;
        }
    }

    /// <summary>
    /// シークバー上の位置を、現在のループ内の再生位置に換算してシークを要求する。
    /// </summary>
    private void SeekFromPoint(Point pt)
    {
        if (!CanSeek) return;
        double loopDur = _currentLoopDuration > TimeSpan.Zero ? _currentLoopDuration.TotalSeconds : _totalTime.TotalSeconds;
        if (loopDur <= 0) return;
        ComputeLayoutBounds(BarStartX, ActualWidth, out double firstBarX, out double seekRight);
        double ratio = Math.Clamp((pt.X - firstBarX) / (seekRight - firstBarX), 0.0, 1.0);
        TimeSpan target = _loopStartCurrentTime + TimeSpan.FromSeconds(loopDur * ratio);
        SeekRequested?.Invoke(target);
    }

    /// <summary>
    /// リール、シークバー、操作の行、モードの行を描く。
    /// </summary>
    protected override void OnRender(DrawingContext dc)
    {
        // 全面を透明で塗り、どこでもマウスに反応させる。テーマのブラシを集める。
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        var dpi = VisualTreeHelper.GetDpi(this);

        var highlight = GetBrush("HighlightText", FallbackHighlight);
        var muted = GetBrush("MutedText", FallbackMuted);
        var panOff = GetBrush("MusicInactiveWhiteKey", FallbackPanOff);
        var disabled = GetBrush("MusicInactiveWhiteBorder", FallbackDisabled);

        ComputeLayoutBounds(BarStartX, ActualWidth, out double firstBarX, out double seekRight);

        // リールを描く。直径47ピクセルで、左端は曲情報の枠と同じ X=0、上端はシークバーと同じ Y=7。
        DrawReel(dc, new Point(ReelCenterX, ReelCenterY), ReelOuterRadius, ReelInnerRadius, highlight, muted);

        // 幅2・高さ8ピクセルの棒でシークバーを描く。
        DrawSeekBar(dc, firstBarX, seekRight, highlight, muted);

        // 操作の行を、シークバーと同じ左右の範囲に並べる。
        DrawTransportRow(dc, dpi, firstBarX, seekRight, highlight, muted, panOff, disabled);

        // モードの行を、シークバーと同じ左右の範囲に並べる。
        DrawModeRow(dc, dpi, firstBarX, seekRight, highlight, muted);
    }

    /// <summary>
    /// 24本のスポークでリールを描く。再生中は現在のスポークを明るくし、後ろの3本を尾を引くように段階的に暗くする。
    /// </summary>
    private void DrawReel(DrawingContext dc, Point center, double outerR, double innerR, Brush highlight, Brush muted)
    {
        // スポークごとに色を決めて線を引く。
        const int spokes = 24;
        int activeSpoke = _isPlaying && !_isPaused ? _spinPhase : -1;

        for (int i = 0; i < spokes; i++)
        {
            double angle = i * (2 * Math.PI / spokes);
            double cos = Math.Cos(angle);
            double sin = Math.Sin(angle);

            Point pInner = new(center.X + cos * innerR, center.Y + sin * innerR);
            Point pOuter = new(center.X + cos * outerR, center.Y + sin * outerR);

            Brush brush;
            if (activeSpoke >= 0)
            {
                int diff = (activeSpoke - i + spokes) % spokes;
                brush = diff switch
                {
                    0 => highlight,
                    1 => Trailing1,
                    2 => Trailing2,
                    3 => Trailing3,
                    _ => muted
                };
            }
            else
            {
                brush = muted;
            }

            var pen = new Pen(brush, 1.4);
            pen.Freeze();
            dc.DrawLine(pen, pInner, pOuter);
        }

        // 内側の円を描く。
        var innerPen = new Pen(muted, 1.0);
        innerPen.Freeze();
        dc.DrawEllipse(null, innerPen, center, innerR, innerR);
    }

    /// <summary>
    /// シークバーを描く。再生済みの位置までの棒を明るくする。
    /// </summary>
    private void DrawSeekBar(DrawingContext dc, double firstBarX, double seekRight, Brush highlight, Brush muted)
    {
        // 上端はリールの上端(30.5 - 23.5 = 7.0)にそろえ、高さ8ピクセル、棒2ピクセルと隙間2ピクセルの間隔にする。
        double barY = 7.0;
        double barW = 2.0;
        double barH = 8.0;
        double pitch = 4.0;

        // 進み具合とホバー状態から色を決める。
        double ratio = SeekBarRatio;

        double activeThreshold = firstBarX + (seekRight - firstBarX) * ratio;

        Brush activeBrush = _hoveredElement == ControlElement.SeekBar ? HoverHighlight : highlight;
        Brush inactiveBrush = _hoveredElement == ControlElement.SeekBar ? HoverMuted : muted;

        // 棒を並べる。少しでも進んでいれば、途中にかかる棒も明るくする。
        for (double x = firstBarX; x + barW <= seekRight + 0.01; x += pitch)
        {
            Brush barBrush = (x + barW <= activeThreshold || (x < activeThreshold && ratio > 0))
                ? activeBrush
                : inactiveBrush;

            dc.DrawRectangle(barBrush, null, new Rect(x, barY, barW, barH));
        }
    }

    /// <summary>
    /// 行に並べる部品1つの描画位置・幅と、クリックに反応する左右の範囲。
    /// </summary>
    private readonly record struct ItemLayout(ControlElement Element, double X, double Width, double HitLeft, double HitRight);

    /// <summary>
    /// 操作の行の配置を求める。両端をそろえ、残りの幅を部品の間に均等に割り振る。反応範囲は隣との中間で区切る。
    /// </summary>
    private static ItemLayout[] GetRow1Layout(double startX, double endX)
    {
        // アイコンと文字を含めた見た目の幅。PLAY、STOP、PAUSE、REW、FF の順。
        double[] widths = { 41.52, 41.06, 46.76, 34.74, 26.72 };
        ControlElement[] elements = { ControlElement.Play, ControlElement.Stop, ControlElement.Pause, ControlElement.Rewind, ControlElement.FastForward };

        // 部品の間隔を求める。幅が足りないときは固定の間隔にする。
        double totalW = 0;
        foreach (var w in widths) totalW += w;

        double span = Math.Max(0, endX - startX);
        double gap = (widths.Length > 1 && span > totalW)
            ? (span - totalW) / (widths.Length - 1)
            : 10.0;

        // 左から並べ、最後の部品だけ右端にそろえる。
        var items = new ItemLayout[widths.Length];
        double currentX = startX;
        for (int i = 0; i < widths.Length; i++)
        {
            if (i == widths.Length - 1)
            {
                currentX = endX - widths[i];
            }
            double hitLeft = i == 0 ? startX : (items[i - 1].X + widths[i - 1] + currentX) / 2.0;
            double hitRight = i == widths.Length - 1 ? endX : currentX + widths[i] + gap / 2.0;
            items[i] = new ItemLayout(elements[i], currentX, widths[i], hitLeft, hitRight);
            currentX += widths[i] + gap;
        }
        return items;
    }

    /// <summary>
    /// モードの行の配置を求める。並べ方は操作の行と同じ。
    /// </summary>
    private static ItemLayout[] GetRow2Layout(double startX, double endX)
    {
        // アイコンと文字を含めた見た目の幅。PLAYALL、REPEAT、SHUFFLE、LOOP の順。
        double[] widths = { 65.24, 56.90, 64.56, 58.00 };
        ControlElement[] elements = { ControlElement.PlayAll, ControlElement.Repeat, ControlElement.Shuffle, ControlElement.Loop };

        // 部品の間隔を求める。幅が足りないときは固定の間隔にする。
        double totalW = 0;
        foreach (var w in widths) totalW += w;

        double span = Math.Max(0, endX - startX);
        double gap = (widths.Length > 1 && span > totalW)
            ? (span - totalW) / (widths.Length - 1)
            : 12.0;

        // 左から並べ、最後の部品だけ右端にそろえる。
        var items = new ItemLayout[widths.Length];
        double currentX = startX;
        for (int i = 0; i < widths.Length; i++)
        {
            if (i == widths.Length - 1)
            {
                currentX = endX - widths[i];
            }
            double hitLeft = i == 0 ? startX : (items[i - 1].X + widths[i - 1] + currentX) / 2.0;
            double hitRight = i == widths.Length - 1 ? endX : currentX + widths[i] + gap / 2.0;
            items[i] = new ItemLayout(elements[i], currentX, widths[i], hitLeft, hitRight);
            currentX += widths[i] + gap;
        }
        return items;
    }

    /// <summary>
    /// 操作の行を描く。現在の再生状態に当たる部品を明るくし、シークできない曲では REW と FF を無効色にする。
    /// </summary>
    private void DrawTransportRow(DrawingContext dc, DpiScale dpi, double startX, double endX, Brush highlight, Brush muted, Brush panOff, Brush disabled)
    {
        // centerY は13ピクセル太字の大文字の見た目上の中心(上端 y+3.66、ベースライン y+11.96)。アイコンをここに合わせる。
        double y = 22.5;
        double centerY = y + 7.8;
        const double fontSize = 13.0;
        var layout = GetRow1Layout(startX, endX);

        foreach (var item in layout)
        {
            double x = item.X;
            switch (item.Element)
            {
                case ControlElement.Play:
                {
                    bool active = _isPlaying && !_isPaused;
                    Brush brush = active
                        ? (_hoveredElement == ControlElement.Play ? HoverHighlight : highlight)
                        : (_hoveredElement == ControlElement.Play ? HoverMuted : muted);

                    var playGeo = new StreamGeometry();
                    using (var ctx = playGeo.Open())
                    {
                        ctx.BeginFigure(new Point(x, centerY - 4.0), true, true);
                        ctx.LineTo(new Point(x + 8.0, centerY), true, false);
                        ctx.LineTo(new Point(x, centerY + 4.0), true, false);
                    }
                    playGeo.Freeze();
                    dc.DrawGeometry(brush, null, playGeo);
                    DrawText(dc, "PLAY", new Point(x + 13.0, y), brush, dpi, fontSize);
                    break;
                }
                case ControlElement.Stop:
                {
                    bool active = !_isPlaying;
                    Brush brush = active
                        ? (_hoveredElement == ControlElement.Stop ? HoverHighlight : highlight)
                        : (_hoveredElement == ControlElement.Stop ? HoverMuted : muted);

                    dc.DrawRectangle(brush, null, new Rect(x, centerY - 4.0, 8.0, 8.0));
                    DrawText(dc, "STOP", new Point(x + 13.0, y), brush, dpi, fontSize);
                    break;
                }
                case ControlElement.Pause:
                {
                    bool active = _isPaused;
                    Brush brush = active
                        ? (_hoveredElement == ControlElement.Pause ? HoverHighlight : highlight)
                        : (_hoveredElement == ControlElement.Pause ? HoverMuted : muted);

                    dc.DrawRectangle(brush, null, new Rect(x, centerY - 4.0, 2.5, 8.0));
                    dc.DrawRectangle(brush, null, new Rect(x + 4.5, centerY - 4.0, 2.5, 8.0));
                    DrawText(dc, "PAUSE", new Point(x + 12.0, y), brush, dpi, fontSize);
                    break;
                }
                case ControlElement.Rewind:
                {
                    Brush brush = !CanSeek ? disabled : _hoveredElement == ControlElement.Rewind ? HoverHighlight : panOff;
                    DrawDoubleArrowLeft(dc, x, centerY, brush);
                    DrawText(dc, "REW", new Point(x + 13.5, y), brush, dpi, fontSize);
                    break;
                }
                case ControlElement.FastForward:
                {
                    Brush brush = !CanSeek ? disabled : _hoveredElement == ControlElement.FastForward ? HoverHighlight : panOff;
                    DrawDoubleArrowRight(dc, x, centerY, brush);
                    DrawText(dc, "FF", new Point(x + 13.5, y), brush, dpi, fontSize);
                    break;
                }
            }
        }
    }

    /// <summary>
    /// モードの行を描く。有効なモードを明るくし、LOOP は目標ループ回数を表示する。
    /// </summary>
    private void DrawModeRow(DrawingContext dc, DpiScale dpi, double startX, double endX, Brush highlight, Brush muted)
    {
        // centerY は13ピクセル太字の大文字の見た目上の中心。アイコンをここに合わせる。
        double y = 40.5;
        double centerY = y + 7.8;
        const double fontSize = 13.0;
        var layout = GetRow2Layout(startX, endX);

        foreach (var item in layout)
        {
            double x = item.X;
            switch (item.Element)
            {
                case ControlElement.PlayAll:
                {
                    bool hovered = _hoveredElement == ControlElement.PlayAll;
                    Brush brush = _isPlayAllEnabled
                        ? (hovered ? HoverHighlight : highlight)
                        : (hovered ? HoverMuted : muted);

                    DrawPlayAllIcon(dc, x, centerY, brush);
                    DrawText(dc, "PLAYALL", new Point(x + 16.0, y), brush, dpi, fontSize);
                    break;
                }
                case ControlElement.Repeat:
                {
                    bool hovered = _hoveredElement == ControlElement.Repeat;
                    Brush brush = _isRepeatEnabled
                        ? (hovered ? HoverHighlight : highlight)
                        : (hovered ? HoverMuted : muted);

                    DrawRepeatIcon(dc, x, centerY, brush);
                    DrawText(dc, "REPEAT", new Point(x + 14.5, y), brush, dpi, fontSize);
                    break;
                }
                case ControlElement.Shuffle:
                {
                    bool hovered = _hoveredElement == ControlElement.Shuffle;
                    Brush brush = _isShuffleEnabled
                        ? (hovered ? HoverHighlight : highlight)
                        : (hovered ? HoverMuted : muted);

                    DrawShuffleIcon(dc, x, centerY, brush);
                    DrawText(dc, "SHUFFLE", new Point(x + 15.5, y), brush, dpi, fontSize);
                    break;
                }
                case ControlElement.Loop:
                {
                    bool hovered = _hoveredElement == ControlElement.Loop;
                    Brush brush = hovered ? HoverHighlight : highlight;

                    // 目標ループ回数に応じて ∞ LOOP、1 LOOP、2 LOOP を表示する。
                    string loopLabel = _targetLoops switch
                    {
                        1 => "1 LOOP",
                        2 => "2 LOOP",
                        _ => "∞ LOOP"
                    };

                    DrawLoopIcon(dc, x, centerY, brush);
                    DrawText(dc, loopLabel, new Point(x + 14.5, y), brush, dpi, fontSize);
                    break;
                }
            }
        }
    }

    /// <summary>
    /// 全曲再生のアイコン(リストの線と再生の三角)を描く。
    /// </summary>
    private static void DrawPlayAllIcon(DrawingContext dc, double x, double centerY, Brush brush)
    {
        var pen = new Pen(brush, 1.2);
        pen.Freeze();
        // 左にリストを表す3本の線を引く。
        dc.DrawLine(pen, new Point(x, centerY - 3.2), new Point(x + 5.0, centerY - 3.2));
        dc.DrawLine(pen, new Point(x, centerY), new Point(x + 4.0, centerY));
        dc.DrawLine(pen, new Point(x, centerY + 3.2), new Point(x + 5.0, centerY + 3.2));

        // 右に高さ8ピクセルの再生の三角を centerY 中心で描く。
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(new Point(x + 6.5, centerY - 4.0), true, true);
            ctx.LineTo(new Point(x + 11.0, centerY), true, false);
            ctx.LineTo(new Point(x + 6.5, centerY + 4.0), true, false);
        }
        geo.Freeze();
        dc.DrawGeometry(brush, null, geo);
    }

    /// <summary>
    /// リピートのアイコン(上下で逆向きの矢印)を描く。
    /// </summary>
    private static void DrawRepeatIcon(DrawingContext dc, double x, double centerY, Brush brush)
    {
        var pen = new Pen(brush, 1.2);
        pen.Freeze();
        // 上側。右向きの線、右上の矢じり、右の縦線を引く。
        dc.DrawLine(pen, new Point(x + 1.5, centerY - 3.0), new Point(x + 9.0, centerY - 3.0));
        dc.DrawLine(pen, new Point(x + 7.0, centerY - 4.5), new Point(x + 9.5, centerY - 3.0));
        dc.DrawLine(pen, new Point(x + 7.0, centerY - 1.5), new Point(x + 9.5, centerY - 3.0));
        dc.DrawLine(pen, new Point(x + 9.5, centerY - 3.0), new Point(x + 9.5, centerY - 0.5));

        // 下側。左向きの線、左下の矢じり、左の縦線を引く。
        dc.DrawLine(pen, new Point(x + 8.5, centerY + 3.0), new Point(x + 1.0, centerY + 3.0));
        dc.DrawLine(pen, new Point(x + 3.0, centerY + 1.5), new Point(x + 0.5, centerY + 3.0));
        dc.DrawLine(pen, new Point(x + 3.0, centerY + 4.5), new Point(x + 0.5, centerY + 3.0));
        dc.DrawLine(pen, new Point(x + 0.5, centerY + 3.0), new Point(x + 0.5, centerY + 0.5));
    }

    /// <summary>
    /// シャッフルのアイコン(交差する2本の矢印)を描く。
    /// </summary>
    private static void DrawShuffleIcon(DrawingContext dc, double x, double centerY, Brush brush)
    {
        var pen = new Pen(brush, 1.2);
        pen.Freeze();
        // 左上から右下へ向かう線と、右下の矢じりを引く。
        dc.DrawLine(pen, new Point(x, centerY - 3.0), new Point(x + 3.0, centerY - 3.0));
        dc.DrawLine(pen, new Point(x + 3.0, centerY - 3.0), new Point(x + 7.0, centerY + 3.0));
        dc.DrawLine(pen, new Point(x + 7.0, centerY + 3.0), new Point(x + 10.0, centerY + 3.0));
        dc.DrawLine(pen, new Point(x + 7.5, centerY + 1.5), new Point(x + 10.5, centerY + 3.0));
        dc.DrawLine(pen, new Point(x + 7.5, centerY + 4.5), new Point(x + 10.5, centerY + 3.0));

        // 左下から右上へ向かう線と、右上の矢じりを引く。
        dc.DrawLine(pen, new Point(x, centerY + 3.0), new Point(x + 3.0, centerY + 3.0));
        dc.DrawLine(pen, new Point(x + 3.0, centerY + 3.0), new Point(x + 7.0, centerY - 3.0));
        dc.DrawLine(pen, new Point(x + 7.0, centerY - 3.0), new Point(x + 10.0, centerY - 3.0));
        dc.DrawLine(pen, new Point(x + 7.5, centerY - 4.5), new Point(x + 10.5, centerY - 3.0));
        dc.DrawLine(pen, new Point(x + 7.5, centerY - 1.5), new Point(x + 10.5, centerY - 3.0));
    }

    /// <summary>
    /// ループのアイコン(時計回りの円弧と矢じり)を描く。
    /// </summary>
    private static void DrawLoopIcon(DrawingContext dc, double x, double centerY, Brush brush)
    {
        double cx = x + 4.5;
        double cy = centerY;
        double r = 4.0;

        var pen = new Pen(brush, 1.3);
        pen.Freeze();

        // 右上から下と左を回って左上まで、時計回りの円弧を描く。
        var arcGeo = new StreamGeometry();
        using (var ctx = arcGeo.Open())
        {
            ctx.BeginFigure(new Point(cx + 3.5, cy - 1.5), false, false);
            ctx.ArcTo(new Point(cx - 2.0, cy - 3.2), new Size(r, r), 0, true, SweepDirection.Clockwise, true, false);
        }
        arcGeo.Freeze();
        dc.DrawGeometry(null, pen, arcGeo);

        // 円弧の端に、時計回りの向きの矢じりを描く。
        var arrowGeo = new StreamGeometry();
        using (var ctx = arrowGeo.Open())
        {
            ctx.BeginFigure(new Point(cx + 1.2, cy - 3.2), true, true);
            ctx.LineTo(new Point(cx + 4.8, cy + 0.8), true, false);
            ctx.LineTo(new Point(cx + 5.2, cy - 2.6), true, false);
        }
        arrowGeo.Freeze();
        dc.DrawGeometry(brush, null, arrowGeo);
    }

    /// <summary>
    /// 早送りの右向き二重三角を描く。
    /// </summary>
    private static void DrawDoubleArrowRight(DrawingContext dc, double x, double centerY, Brush brush)
    {
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(new Point(x, centerY - 4.0), true, true);
            ctx.LineTo(new Point(x + 4.0, centerY), true, false);
            ctx.LineTo(new Point(x, centerY + 4.0), true, false);

            ctx.BeginFigure(new Point(x + 4.5, centerY - 4.0), true, true);
            ctx.LineTo(new Point(x + 8.5, centerY), true, false);
            ctx.LineTo(new Point(x + 4.5, centerY + 4.0), true, false);
        }
        geo.Freeze();
        dc.DrawGeometry(brush, null, geo);
    }

    /// <summary>
    /// 巻き戻しの左向き二重三角を描く。
    /// </summary>
    private static void DrawDoubleArrowLeft(DrawingContext dc, double x, double centerY, Brush brush)
    {
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(new Point(x + 4.0, centerY - 4.0), true, true);
            ctx.LineTo(new Point(x, centerY), true, false);
            ctx.LineTo(new Point(x + 4.0, centerY + 4.0), true, false);

            ctx.BeginFigure(new Point(x + 8.5, centerY - 4.0), true, true);
            ctx.LineTo(new Point(x + 4.5, centerY), true, false);
            ctx.LineTo(new Point(x + 8.5, centerY + 4.0), true, false);
        }
        geo.Freeze();
        dc.DrawGeometry(brush, null, geo);
    }

    /// <summary>
    /// 現在のDPIに合わせて文字列を描く。
    /// </summary>
    private static void DrawText(DrawingContext dc, string text, Point origin, Brush brush, DpiScale dpi, double fontSize)
    {
        var ft = new FormattedText(
            text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            Font,
            fontSize,
            brush,
            dpi.PixelsPerDip);
        dc.DrawText(ft, origin);
    }
}
