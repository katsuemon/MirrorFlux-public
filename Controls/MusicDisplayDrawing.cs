using System.Globalization;
using System.Windows;
using System.Windows.Media;
using MirrorFlux.Services.Music;

using MirrorFlux.Services;

namespace MirrorFlux.Controls;

/// <summary>音楽プレイヤーの表示で共通に使う、画素にそろえた描画部品。デコーダーや音源チップのレジスタには依存しない。</summary>
internal static class MusicDisplayDrawing
{
    /// <summary>色の文字列から凍結済みのブラシを作る。</summary>
    internal static SolidColorBrush Brush(string color)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(color)!;
        brush.Freeze();
        return brush;
    }
    internal static readonly Brush Black = ThemeBrushes.Get("ViewerBlack");
    internal static readonly Brush Border = ThemeBrushes.Get("MusicBorder");
    internal static readonly Brush Text = ThemeBrushes.Get("MusicText");
    internal static readonly Brush Muted = ThemeBrushes.Get("MusicMuted");
    internal static readonly Brush KeyboardWhiteKey = ThemeBrushes.Get("MusicKeyboardWhiteKey");
    internal static readonly Brush KeyboardWhiteBorder = ThemeBrushes.Get("MusicKeyboardWhiteBorder");
    internal static readonly Brush KeyboardBlackKey = ThemeBrushes.Get("MusicKeyboardBlackKey");
    internal static readonly Brush KeyboardActiveWhiteKey = ThemeBrushes.Get("MusicKeyLamp");
    internal static readonly Brush KeyboardActiveBlackKey = KeyboardActiveWhiteKey;
    internal static readonly Brush KeyboardActiveNdWhiteKey = ThemeBrushes.Get("MusicKeyLampNd");
    internal static readonly Brush KeyboardActiveNdBlackKey = KeyboardActiveNdWhiteKey;
    internal static readonly Brush KeyboardInactiveWhiteKey = ThemeBrushes.Get("MusicInactiveWhiteKey");
    internal static readonly Brush KeyboardInactiveWhiteBorder = ThemeBrushes.Get("MusicInactiveWhiteBorder");
    internal static readonly Brush KeyboardInactiveBlackKey = ThemeBrushes.Get("MusicInactiveBlackKey");
    internal static readonly Brush Cyan = ThemeBrushes.Get("MusicCyan");
    internal static readonly Brush Amber = ThemeBrushes.Get("MusicAmber");
    internal static readonly Brush MeterOff = ThemeBrushes.Get("MusicMeterOff");
    internal static readonly Brush MeterLit = ThemeBrushes.Get("MusicMeterLit");
    internal static readonly Brush MeterPeak = ThemeBrushes.Get("MusicMeterPeak");
    internal static readonly Brush MeterEndpoint = ThemeBrushes.Get("MusicMeterEndpoint");
    internal static readonly Brush PanRing = ThemeBrushes.Get("MusicPanRing");
    internal static readonly Brush PanDot = ThemeBrushes.Get("MusicPanDot");
    internal static readonly Typeface Face = new("Consolas");
    private static readonly string[] MmlNoteNames = { "c", "c+", "d", "d+", "e", "f", "f+", "g", "g+", "a", "a+", "b" };
    private static readonly bool[] BlackKeys = { false, true, false, true, false, false, true, false, true, false, true, false };

    internal static string FormatTime(TimeSpan time) => time.TotalHours >= 1 ? $"{(int)time.TotalHours}:{time.Minutes:D2}:{time.Seconds:D2}" : $"{(int)time.TotalMinutes:D2}:{time.Seconds:D2}";
    /// <summary>MIDIノート番号をMML形式の音名（例: o4c+）にする。</summary>
    internal static string FormatMmlNote(int? note)
    {
        if (note is not int n || n < 0 || n > 127) return "";
        // 鍵盤（o0c〜o8b）より低い音も「o0」にまとめず、本当のオクターブ（o-1）で出す。
        int octave = n / 12 - 1;
        return $"o{octave}{MmlNoteNames[n % 12]}";
    }
    /// <summary>「Note :」欄の文字数。音名は最長の「~o-1c+」（6文字）まで同じ幅に収め、セントの位置を動かさない。</summary>
    internal const int NoteDisplayLength = 18;
    /// <summary>音名とセントずれを「Note :」欄の表示文字列にする。推定値には「~」を付ける。</summary>
    internal static string FormatNoteDisplay(int? note, bool estimated = false, int cents = 0)
    {
        string noteName = note.HasValue ? (estimated ? "~" : "") + FormatMmlNote(note.Value) : "";
        return $"Note : {noteName,-6} {MusicPitch.FormatCents(cents)}";
    }
    internal static double Level(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;
    /// <summary>幅いっぱいに並べるバーのうち、index番目の左右端を物理画素にそろえて求める。</summary>
    internal static (double Left, double Right) EdgeAlignedBar(double width, int index, int count, double dpi, int gap)
    {
        // 描画した鍵盤に合わせる。鍵盤は最後の鍵の右に物理画素1つ分を残すため、ここでも1画素引く。
        int total = Math.Max(0, (int)Math.Round(width * dpi) - 1);
        int ink = Math.Max(0, total - gap * (count - 1));
        return ((Math.Floor(ink * index / (double)count) + gap * index) / dpi,
            (Math.Floor(ink * (index + 1) / (double)count) + gap * index) / dpi);
    }
    /// <summary>チャンネルごとの表示枠の左右端を、全チャンネル同じ物理画素ピッチで求める。</summary>
    internal static (double Left, double Right) ChannelSlot(double width, int index, int count, double dpiScaleX)
    {
        // 幅がチャンネル数の画素に満たないときは、単純な等分で返す。
        if (count <= 0 || index < 0 || index >= count || width <= 0 || dpiScaleX <= 0) return (0, 0);
        int physicalWidth = Math.Max(0, (int)Math.Floor(width * dpiScaleX + .001));
        if (physicalWidth < count)
        {
            double fallbackLeft = Math.Floor(index * width * dpiScaleX / count) / dpiScaleX;
            double fallbackRight = Math.Floor((index + 1) * width * dpiScaleX / count) / dpiScaleX;
            return (fallbackLeft, fallbackRight);
        }

        // 全チャンネルを同じ物理画素ピッチにする。割り切れない余りの画素は左右の余白に振り分け、
        // 一部のバー（とそのすきま）だけが太く見えないようにする。
        int pitch = physicalWidth / count;
        int padding = (physicalWidth - pitch * count) / 2;
        double left = (padding + index * pitch) / dpiScaleX;
        double right = (padding + (index + 1) * pitch) / dpiScaleX;
        return (left, right);
    }
    /// <summary>Consolasで文字列を整数座標に描く。最大幅があれば1行に収めて末尾を省略する。</summary>
    internal static void TextAt(DrawingContext dc, string text, double x, double y, double size, Brush? brush = null, double maxWidth = double.PositiveInfinity)
    {
        var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, size, brush ?? Text, 1);
        if (double.IsFinite(maxWidth) && maxWidth > 0) { formatted.MaxTextWidth = maxWidth; formatted.MaxLineCount = 1; formatted.Trimming = TextTrimming.CharacterEllipsis; }
        dc.DrawText(formatted, new Point(Math.Floor(x), Math.Floor(y)));
    }
    internal static double TextWidth(string text, double size) =>
        new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, size, Text, 1).WidthIncludingTrailingWhitespace;

    /// <summary>鍵盤を文字グリッドに合わせた固定の鍵幅で描く。光る鍵、範囲外の印、細かい音程の棒も描く。</summary>
    internal static void FixedKeyboard(DrawingContext dc, Rect bounds, IReadOnlySet<int> active, double keyWidth = 8, bool dimmed = false,
        DpiScale? compactDpi = null, IReadOnlySet<int>? ndActive = null, bool isNdPart = false,
        IReadOnlyCollection<double>? pitches = null, Brush? marker = null, DpiScale? dpi = null)
    {
        // 表示範囲はC1〜B9の9オクターブ（白鍵63個）。
        const int startNote = 24;
        const int endNote = startNote + 108;
        if (bounds.Width < keyWidth || bounds.Height < 2) return;
        // 鍵の下の左右の角は1ドット落とす。縁取りのある白鍵は、縁の内側の面に1ドット分の欠けが残るよう、その分だけ大きく落とす。
        double devicePixel = 1 / (compactDpi?.DpiScaleX ?? dpi?.DpiScaleX ?? 1);
        double devicePixelY = 1 / (compactDpi?.DpiScaleY ?? dpi?.DpiScaleY ?? 1);
        double blackTop = bounds.Y + devicePixelY;
        // 縁取りは斜めの辺にも幅1でかかるため、縁の内側の面に1ドット分の欠けが残る大きさにする。黒鍵は縁取りなし。
        double chamfer = compactDpi.HasValue ? devicePixel : .5 + Math.Sqrt(.5) + devicePixel;
        // 黒鍵は1.5画素落とすと、角の画素の中心が斜めの辺より外になり、ちょうど1画素だけ欠ける。
        double blackChamfer = 1.5 * devicePixel;

        // 光る鍵 > ハイライト色の音程の棒 > 光っていない鍵、の順に重ねるため、形と塗りを先に集める。
        // まず白鍵の形と塗りを集める。コンパクト表示では画素にそろえ、縁取りの代わりに1画素のすきまを空ける。
        var whites = new List<(bool Active, Geometry Shape, Brush Fill, Pen? Outline)>();
        var blacks = new List<(bool Active, Geometry Shape, Brush Fill, Pen? Outline)>();
        int whiteIndex = 0;
        for (int note = startNote; note < endNote; note++)
        {
            int semitone = note % 12;
            if (semitone is 1 or 3 or 6 or 8 or 10) continue;
            double x = bounds.X + whiteIndex++ * keyWidth;
            if (x >= bounds.Right) break;
            bool isActive = active.Contains(note);
            bool isNd = isNdPart || (ndActive != null && ndActive.Contains(note));
            Brush fill = isActive ? (isNd ? KeyboardActiveNdWhiteKey : KeyboardActiveWhiteKey) : dimmed ? KeyboardInactiveWhiteKey : KeyboardWhiteKey;
            var keyRect = new Rect(x, bounds.Y, Math.Min(keyWidth, bounds.Right - x), bounds.Height);
            if (compactDpi is DpiScale whiteDpi)
            {
                double left = Math.Round(x * whiteDpi.DpiScaleX) / whiteDpi.DpiScaleX;
                double right = Math.Round(keyRect.Right * whiteDpi.DpiScaleX) / whiteDpi.DpiScaleX;
                keyRect = new Rect(left, bounds.Y, Math.Max(0, right - left - 1 / whiteDpi.DpiScaleX), bounds.Height);
                whites.Add((isActive, BottomChamfer(keyRect, chamfer), fill, null));
            }
            else whites.Add((isActive, BottomChamfer(keyRect, chamfer), fill, new Pen(dimmed ? KeyboardInactiveWhiteBorder : KeyboardWhiteBorder, 1)));
        }

        // 次に黒鍵の形と塗りを集める。黒鍵は白鍵の境目をまたぐ位置に置く。
        whiteIndex = 0;
        for (int note = startNote; note < endNote; note++)
        {
            int semitone = note % 12;
            if (semitone is not (1 or 3 or 6 or 8 or 10)) { whiteIndex++; continue; }
            double x = bounds.X + whiteIndex * keyWidth - Math.Floor(keyWidth * .35);
            if (x >= bounds.Right || x + keyWidth <= bounds.X) continue;
            bool isActive = active.Contains(note);
            bool isNd = isNdPart || (ndActive != null && ndActive.Contains(note));
            var keyRect = new Rect(x, blackTop, Math.Min(Math.Max(2, Math.Floor(keyWidth * .7)), bounds.Right - x), Math.Max(0, Math.Floor(bounds.Height * .62) - devicePixelY));
            if (compactDpi is DpiScale blackDpi)
            {
                // コンパクト表示では、黒鍵の幅を奇数画素にして白鍵の境目の中央に置く。
                double blackWidthPixels = Math.Round(2 * blackDpi.DpiScaleX, MidpointRounding.AwayFromZero) + 1;
                double boundaryPixels = Math.Round((bounds.X + whiteIndex * keyWidth) * blackDpi.DpiScaleX);
                double left = Math.Floor(boundaryPixels - blackWidthPixels / 2) / blackDpi.DpiScaleX;
                double right = left + blackWidthPixels / blackDpi.DpiScaleX;
                double blackH = Math.Round(Math.Floor(bounds.Height * .62) * blackDpi.DpiScaleY) / blackDpi.DpiScaleY;
                keyRect = new Rect(left, blackTop, right - left, Math.Max(0, blackH - devicePixelY));
            }
            else if (dpi is DpiScale snapDpi)
            {
                // 縁取りのない黒鍵は画素の境目にそろえる（半端な画素で角の1ドットがぼやけないように）。
                double Snap(double value, double scale) => Math.Round(value * scale) / scale;
                double left = Snap(keyRect.Left, snapDpi.DpiScaleX), right = Snap(keyRect.Right, snapDpi.DpiScaleX);
                double bottom = Snap(bounds.Y + Math.Floor(bounds.Height * .62), snapDpi.DpiScaleY);
                keyRect = new Rect(left, blackTop, right - left, Math.Max(0, bottom - blackTop));
            }
            // 黒鍵には縁取りを付けない（黒い縁が白鍵の上で影のように見えるため）。
            blacks.Add((isActive, BottomChamfer(keyRect, blackChamfer),
                isActive ? (isNd ? KeyboardActiveNdBlackKey : KeyboardActiveBlackKey) : dimmed ? KeyboardInactiveBlackKey : KeyboardBlackKey,
                null));
        }

        // 白鍵の上に黒鍵を重ねて描く。
        foreach (var key in whites) dc.DrawGeometry(key.Fill, key.Outline, key.Shape);
        foreach (var key in blacks) dc.DrawGeometry(key.Fill, key.Outline, key.Shape);

        if (marker == null) return;
        // 鍵盤の範囲外で鳴っている音は、その側の端の縦の真ん中に、キーONのランプの色（%theme_MusicKeyLamp）で三角の印を出す。
        double markWidth = Math.Max(3, Math.Floor(keyWidth * .75));
        double markHeight = Math.Max(4, Math.Floor(bounds.Height * .4));
        double markTop = bounds.Y + Math.Floor((bounds.Height - markHeight) / 2);
        if (dpi is DpiScale markDpi) markTop = Math.Round(markTop * markDpi.DpiScaleY) / markDpi.DpiScaleY;
        if (active.Any(note => note < startNote)) dc.DrawGeometry(KeyboardActiveWhiteKey, null, Triangle(bounds.X, markTop, markWidth, markHeight, pointsLeft: true));
        if (active.Any(note => note >= endNote))
        {
            double right = Math.Min(bounds.Right, bounds.X + 63 * keyWidth);
            dc.DrawGeometry(KeyboardActiveWhiteKey, null, Triangle(right - markWidth, markTop, markWidth, markHeight, pointsLeft: false));
        }

        // 半音より細かい本当の音程の位置に、ハイライト色の棒を置く。いちばん近い鍵の形に合わせ、
        // 白鍵なら黒鍵より下の段に白鍵の幅で、黒鍵なら黒鍵の段に黒鍵の幅で描く（もう一方の段にはみ出さない）。
        if (pitches == null || pitches.Count == 0) return;
        double scale = compactDpi?.DpiScaleX ?? 1;
        // 幅は鍵の面の幅。鍵と鍵のすきま（縁取り1ピクセル）の分は細くする。
        double whiteBarWidth = Math.Max(1 / scale, keyWidth - 1 / scale);
        double blackBarWidth = compactDpi is DpiScale barDpi
            ? (Math.Round(2 * barDpi.DpiScaleX, MidpointRounding.AwayFromZero) + 1) / barDpi.DpiScaleX
            : Math.Max(1, Math.Max(2, Math.Floor(keyWidth * .7)) - 1);
        double blackHeight = Math.Floor(bounds.Height * .62);
        if (compactDpi is DpiScale heightDpi) blackHeight = Math.Round(blackHeight * heightDpi.DpiScaleY) / heightDpi.DpiScaleY;
        else if (dpi is DpiScale rowDpi) blackHeight = Math.Round((bounds.Y + blackHeight) * rowDpi.DpiScaleY) / rowDpi.DpiScaleY - bounds.Y;
        double keyboardRight = Math.Min(bounds.Right, bounds.X + 63 * keyWidth);
        // 縁取りがある表示では、描き直す光る鍵は鍵の面（縁の内側）だけにする。棒は縁やすきまの上に重なってよい。
        Geometry Face(Geometry shape, Pen? outline)
        {
            if (outline == null) return shape;
            var inner = shape.GetWidenedPathGeometry(new Pen(Brushes.Black, outline.Thickness));
            return new CombinedGeometry(GeometryCombineMode.Exclude, shape, inner);
        }
        // 音程ごとに、いちばん近い鍵の種類に合わせた棒を描く。
        bool drewBar = false;
        foreach (double pitch in pitches)
        {
            if (KeyCenter(pitch, startNote, endNote, keyWidth) is not double center) continue;
            bool black = BlackKeys[(int)Math.Round(pitch) % 12];
            double width = black ? blackBarWidth : whiteBarWidth;
            double x = Math.Clamp(bounds.X + center - width / 2, bounds.X, keyboardRight - width);
            if (compactDpi.HasValue) x = Math.Round(x * scale) / scale;
            // 黒鍵の棒は黒鍵と同じく下の角を1ドット落とす（光る黒鍵の角の欠けから棒がのぞかないように）。上端も1ドット削る。
            if (black) dc.DrawGeometry(marker, null, BottomChamfer(new Rect(x, blackTop, width, Math.Max(0, blackHeight - devicePixelY)), blackChamfer));
            else dc.DrawRectangle(marker, null, new Rect(x, bounds.Y + blackHeight, width, Math.Max(0, bounds.Height - blackHeight)));
            drewBar = true;
        }
        if (!drewBar) return;
        // 光っている鍵は棒より上に、面だけを描き直す（白鍵は黒鍵に隠れる部分を除く）。
        if (whites.Any(key => key.Active))
        {
            var blackArea = new GeometryGroup();
            foreach (var key in blacks) blackArea.Children.Add(key.Shape);
            blackArea.Freeze();
            foreach (var key in whites.Where(key => key.Active))
                dc.DrawGeometry(key.Fill, null, new CombinedGeometry(GeometryCombineMode.Exclude, Face(key.Shape, key.Outline), blackArea));
        }
        foreach (var key in blacks.Where(key => key.Active)) dc.DrawGeometry(key.Fill, null, Face(key.Shape, key.Outline));
    }

    /// <summary>下の左右の角を1ドット（size）落とした鍵の形。</summary>
    private static Geometry BottomChamfer(Rect rect, double size)
    {
        size = Math.Min(Math.Max(0, size), Math.Min(rect.Width / 2, rect.Height));
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(rect.TopLeft, isFilled: true, isClosed: true);
            context.LineTo(rect.TopRight, isStroked: true, isSmoothJoin: false);
            context.LineTo(new Point(rect.Right, rect.Bottom - size), isStroked: true, isSmoothJoin: false);
            context.LineTo(new Point(rect.Right - size, rect.Bottom), isStroked: true, isSmoothJoin: false);
            context.LineTo(new Point(rect.Left + size, rect.Bottom), isStroked: true, isSmoothJoin: false);
            context.LineTo(new Point(rect.Left, rect.Bottom - size), isStroked: true, isSmoothJoin: false);
        }
        geometry.Freeze();
        return geometry;
    }
    private static readonly double[] FixedKeyCenterUnits = BuildKeyCenterUnits(24, 24 + 9 * 12);

    /// <summary>各ノートの中心位置を白鍵幅単位で表に作る。白鍵は鍵の中央、黒鍵は白鍵の境目とする。</summary>
    private static double[] BuildKeyCenterUnits(int startNote, int endNote)
    {
        var table = new double[endNote - startNote];
        int whites = 0;
        for (int n = startNote; n < endNote; n++)
        {
            bool black = BlackKeys[n % 12];
            table[n - startNote] = black ? whites : whites + 0.5;
            if (!black) whites++;
        }
        return table;
    }

    /// <summary>
    /// 鍵盤の左端から、音程pitch（ノート番号の小数）の位置までの距離を求める。白鍵は鍵の中央、黒鍵は白鍵の境目を半音の中心とし、
    /// 前後の半音の間を端数で線形補間する。範囲外ならnull。
    /// </summary>
    internal static double? KeyCenter(double pitch, int startNote, int endNote, double keyWidth)
    {
        if (!double.IsFinite(pitch) || pitch < startNote || pitch > endNote - 1) return null;
        int low = (int)Math.Floor(pitch);
        double fraction = pitch - low;
        // 標準の9オクターブ鍵盤なら、事前計算した表を使う。
        if (startNote == 24 && endNote == 24 + 9 * 12)
        {
            int idx = low - 24;
            double leftUnit = FixedKeyCenterUnits[idx];
            double unit = fraction <= 0 || low + 1 >= endNote
                ? leftUnit
                : leftUnit + (FixedKeyCenterUnits[idx + 1] - leftUnit) * fraction;
            return unit * keyWidth;
        }
        // それ以外の範囲は、白鍵を数えて中心を求める。
        double Center(int note)
        {
            int whites = 0;
            for (int n = startNote; n < note; n++) if (!BlackKeys[n % 12]) whites++;
            return BlackKeys[note % 12] ? whites * keyWidth : whites * keyWidth + keyWidth / 2;
        }
        double left = Center(low);
        return fraction <= 0 || low + 1 >= endNote ? left : left + (Center(low + 1) - left) * fraction;
    }

    /// <summary>左向きまたは右向きの三角の印の形を作る。</summary>
    private static Geometry Triangle(double x, double y, double width, double height, bool pointsLeft)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(new Point(pointsLeft ? x + width : x, y), isFilled: true, isClosed: true);
            context.LineTo(new Point(pointsLeft ? x + width : x, y + height), false, false);
            context.LineTo(new Point(pointsLeft ? x : x + width, y + height / 2), false, false);
        }
        geometry.Freeze();
        return geometry;
    }

    private static readonly Brush WaveformBg = ThemeBrushes.Get("MusicWaveformBackground");
    private static readonly Brush WaveformIdle = ThemeBrushes.Get("MusicWaveformIdle");

    /// <summary>8ビットの波形サンプルを、物理画素1列ごとに1点の点線で描く。</summary>
    internal static void DrawWaveform(DrawingContext dc, byte[] samples, double x, double y, double width, double height, bool active, Brush? activeBrush = null, DpiScale? dpiScale = null)
    {
        if (samples == null || samples.Length == 0 || width <= 0 || height <= 0) return;

        // 描画範囲を物理画素の整数座標に直す。
        double dpiX = dpiScale?.DpiScaleX ?? 1.0;
        double dpiY = dpiScale?.DpiScaleY ?? 1.0;

        int originX = (int)Math.Round(x * dpiX);
        int originY = (int)Math.Round(y * dpiY);
        int physW = (int)Math.Round(width * dpiX);
        int physH = (int)Math.Round(height * dpiY);

        // 背景の箱を物理画素の境目にぴったりそろえて塗る。
        dc.DrawRectangle(WaveformBg, null, new Rect((double)originX / dpiX, (double)originY / dpiY, (double)physW / dpiX, (double)physH / dpiY));

        Brush waveBrush = active ? (activeBrush ?? Cyan) : WaveformIdle;
        int count = samples.Length;
        int maxRow = Math.Max(1, physH - 1);

        // 各列に対応するサンプルの値から行を決め、1画素の点を置く。
        for (int px = 0; px < physW; px++)
        {
            int sIdx = px * count / physW;
            byte v = samples[Math.Clamp(sIdx, 0, count - 1)];
            int sy = Math.Clamp((int)Math.Round((255 - v) * (double)maxRow / 255.0), 0, maxRow);

            double colLeft = (originX + px) / dpiX;
            double colRight = (originX + px + 1) / dpiX;
            double colW = colRight - colLeft;

            double rowTop = (originY + sy) / dpiY;
            double rowBottom = (originY + sy + 1) / dpiY;
            double rowH = rowBottom - rowTop;

            dc.DrawRectangle(waveBrush, null, new Rect(colLeft, rowTop, colW, rowH));
        }
    }

}

/// <summary>スペクトラムの下に、対数目盛りの周波数ラベル（10Hz〜22k）を並べる目盛り表示。</summary>
public sealed class MusicFrequencyRulerDisplay : FrameworkElement
{
    public double MinimumFrequency { get; set; } = AudioSpectrumAnalyzer.MinFrequency;
    public double MaximumFrequency { get; set; } = AudioSpectrumAnalyzer.MaxFrequency;
    public int ReferenceChannels { get; set; } = 16;
    public int ReferenceGapPixels { get; set; } = 3;
    public bool FillWidth { get; set; }
    public double FontSize { get; set; } = 11;

    public MusicFrequencyRulerDisplay()
    {
        SnapsToDevicePixels = true;
        ClipToBounds = true;
    }

    /// <summary>目盛りの左端。基準にするバー表示の最初のバーの左端に合わせる。</summary>
    public double GetLeftEdge()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        if (FillWidth)
        {
            return MusicDisplayDrawing.EdgeAlignedBar(ActualWidth, 0, Math.Max(1, ReferenceChannels), dpi.DpiScaleX, Math.Max(0, ReferenceGapPixels)).Left;
        }
        return MusicDisplayDrawing.ChannelSlot(ActualWidth, 0, ReferenceChannels, dpi.DpiScaleX).Left;
    }

    /// <summary>目盛りの右端。基準にするバー表示の最後のバーの右端（すきまを除く）に合わせる。</summary>
    public double GetRightEdge()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        if (FillWidth)
        {
            return MusicDisplayDrawing.EdgeAlignedBar(ActualWidth, Math.Max(0, ReferenceChannels - 1), Math.Max(1, ReferenceChannels), dpi.DpiScaleX, Math.Max(0, ReferenceGapPixels)).Right;
        }
        var last = MusicDisplayDrawing.ChannelSlot(ActualWidth, ReferenceChannels - 1, ReferenceChannels, dpi.DpiScaleX);
        return last.Right - Math.Max(0, ReferenceGapPixels) / dpi.DpiScaleX;
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        InvalidateVisual();
    }

    /// <summary>目盛りの破線と周波数ラベルを描く。</summary>
    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(MusicDisplayDrawing.Black, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (ActualWidth <= 0 || MinimumFrequency <= 0 || MaximumFrequency <= MinimumFrequency) return;
        // バーの範囲いっぱいに「-」を並べて目盛りの線にする。
        double left = GetLeftEdge(), right = GetRightEdge(), span = right - left;
        if (span <= 0) return;
        double fontSize = Math.Max(6, FontSize);
        Brush ink = ThemeBrushes.Get("MutedText", this);
        double dashWidth = Math.Max(1, MusicDisplayDrawing.TextWidth("-", fontSize));
        int dashCount = Math.Max(1, (int)Math.Ceiling(span / dashWidth));
        dc.PushClip(new RectangleGeometry(new Rect(left, 0, span, ActualHeight)));
        MusicDisplayDrawing.TextAt(dc, new string('-', dashCount), left, 0, fontSize, ink);

        (double Frequency, string Label)[] marks =
        [
            (10, "10Hz"), (20, "20"), (100, "100"), (1000, "1k"),
            (5000, "5k"), (10000, "10k"), (22050, "22k")
        ];
        // 各ラベルを対数位置に置き、下の破線を黒で消してから文字を描く。両端は内側に寄せる。
        double logRange = Math.Log(MaximumFrequency / MinimumFrequency);
        foreach (var mark in marks)
        {
            double position = Math.Log(Math.Clamp(mark.Frequency, MinimumFrequency, MaximumFrequency) / MinimumFrequency) / logRange;
            double textWidth = MusicDisplayDrawing.TextWidth(mark.Label, fontSize);
            double x = left + position * span - textWidth / 2;
            if (position <= 0) x = left;
            else if (position >= .999) x = right - textWidth;
            dc.DrawRectangle(MusicDisplayDrawing.Black, null, new Rect(x - 1, 0, textWidth + 2, ActualHeight));
            MusicDisplayDrawing.TextAt(dc, mark.Label, x, 0, fontSize, ink);
        }
        dc.Pop();
    }
}

/// <summary>サウンドビューアのグラフ横の固定幅のすきまに、右寄せの小さな縦目盛りとラベルを描く表示。</summary>
public sealed class MusicVerticalScaleDisplay : FrameworkElement
{
    public string Labels { get; set; } = "";
    public int MinorIntervals { get; set; }
    public int BottomInsetPixels { get; set; }
    /// <summary>0より大きいとき、同じ高さと倍率の <see cref="MusicMeterDisplay"/> のブロックに目盛りを合わせる。</summary>
    public double DisplayScale { get; set; }
    public bool ShowPeakDockSegment { get; set; } = true;
    public bool FillPlotHeight { get; set; } = true;
    public const double TextSize = 9;
    public const int LabelLeftShiftPixels = 2;
    public const int MajorTickPixels = 8;
    public const int SectionTickPixels = 5;
    public const int StrokePixels = 2;
    public string[] GetLabels() => Labels.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public MusicVerticalScaleDisplay()
    {
        SnapsToDevicePixels = true;
        ClipToBounds = false;
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        InvalidateVisual();
    }

    /// <summary>縦軸、目盛り、ラベルを描く。メーターのブロックに合わせる指定があればそちらで描く。</summary>
    protected override void OnRender(DrawingContext dc)
    {
        string[] labels = GetLabels();
        if (labels.Length == 0 || ActualWidth <= 0 || ActualHeight <= 0) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        if (DisplayScale > 0) { RenderBlockAligned(dc, labels, dpi); return; }
        // 右端に縦軸を引く。
        double bottom = Math.Max(0, ActualHeight - Math.Max(0, BottomInsetPixels) / dpi.DpiScaleY);
        Brush ink = ThemeBrushes.Get("MutedText", this);
        double pixelX = 1 / dpi.DpiScaleX;
        double pixelY = 1 / dpi.DpiScaleY;
        double axisStroke = StrokePixels * pixelX;
        double tickStroke = StrokePixels * pixelY;
        dc.DrawRectangle(ink, null, new Rect(Math.Max(0, ActualWidth - axisStroke), 0, axisStroke, bottom));
        // 等間隔に目盛りを引く。ラベルの位置に当たる目盛りは長くする。
        int intervals = Math.Max(labels.Length - 1, MinorIntervals);
        for (int tick = 0; tick <= intervals; tick++)
        {
            double position = intervals == 0 ? 0 : tick / (double)intervals;
            bool major = labels.Length <= 1 || Math.Abs(position * (labels.Length - 1) - Math.Round(position * (labels.Length - 1))) < .001;
            double length = (major ? MajorTickPixels : SectionTickPixels) * pixelX;
            double y = Math.Round(position * bottom * dpi.DpiScaleY) * pixelY;
            y = Math.Clamp(y, tickStroke / 2, Math.Max(tickStroke / 2, bottom - tickStroke / 2));
            dc.DrawRectangle(ink, null, new Rect(Math.Max(0, ActualWidth - length), y - tickStroke / 2, length, tickStroke));
        }
        // ラベルを長い目盛りの左に右寄せで置く。
        for (int index = 0; index < labels.Length; index++)
        {
            double position = labels.Length == 1 ? 0 : index / (double)(labels.Length - 1);
            double width = MusicDisplayDrawing.TextWidth(labels[index], TextSize);
            double x = ActualWidth - (MajorTickPixels + 1 + LabelLeftShiftPixels) * pixelX - width;
            double y = Math.Clamp(position * bottom - TextSize / 2, 0, Math.Max(0, ActualHeight - TextSize - 1));
            MusicDisplayDrawing.TextAt(dc, labels[index], x, y, TextSize, ink, Math.Max(pixelX, width));
        }
    }

    /// <summary>メーターの各ブロックの上端（DIP、上から順）、ブロックの高さ、目盛りの下端を求める。</summary>
    public (double[] BlockTops, double BlockHeight, double Bottom) GetBlockLayout()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var layout = MusicMeterDisplay.ComputeSegmentLayout(ActualHeight, dpi.DpiScaleY, DisplayScale, ShowPeakDockSegment, false, 0, FillPlotHeight);
        int blocks = layout.Segments + (ShowPeakDockSegment ? 1 : 0);
        double top = layout.PlotHeight - layout.Segments * layout.Step;
        var tops = new double[blocks];
        for (int block = 0; block < blocks; block++) tops[block] = top + block * layout.Step;
        double bottom = ShowPeakDockSegment ? layout.PlotHeight + layout.SegmentHeight : layout.PlotHeight - layout.Step + layout.SegmentHeight;
        return (tops, layout.SegmentHeight, bottom);
    }

    /// <summary>メーターのブロック位置に合わせて縦軸、目盛り、ラベルを描く。</summary>
    private void RenderBlockAligned(DrawingContext dc, string[] labels, DpiScale dpi)
    {
        var (tops, blockHeight, bottom) = GetBlockLayout();
        if (tops.Length == 0) return;
        Brush ink = ThemeBrushes.Get("MutedText", this);
        double pixelX = 1 / dpi.DpiScaleX;
        double pixelY = 1 / dpi.DpiScaleY;
        // 縦軸と目盛りは物理画素1つの太さ。目盛りは各ブロックの上下中央に置く。
        double axisStroke = pixelX;
        double tickStroke = pixelY;
        double tickOffset = Math.Max(0, Math.Floor((blockHeight - tickStroke) * dpi.DpiScaleY / 2)) * pixelY;
        int last = tops.Length - 1;
        // 縦軸は最初の目盛りの上端から、最後の目盛りの下端までちょうどに引く。
        double axisTop = tops[0] + tickOffset;
        double axisBottom = tops[last] + tickOffset + tickStroke;
        dc.DrawRectangle(ink, null, new Rect(Math.Max(0, ActualWidth - axisStroke), axisTop, axisStroke, Math.Max(0, axisBottom - axisTop)));
        // ラベルは正確に等間隔に置き、長い目盛りはラベルがちょうど重なるブロックだけに付ける。
        var labelBlocks = new double[labels.Length];
        for (int index = 0; index < labels.Length; index++)
            labelBlocks[index] = labels.Length == 1 ? 0 : index * last / (double)(labels.Length - 1);
        for (int block = 0; block <= last; block++)
        {
            bool major = labelBlocks.Any(position => Math.Abs(position - block) < .001);
            double length = (major ? MajorTickPixels : SectionTickPixels) * pixelX;
            dc.DrawRectangle(ink, null, new Rect(Math.Max(0, ActualWidth - length), tops[block] + tickOffset, length, tickStroke));
        }
        // ラベルを最初と最後の目盛りの間に配分し、目盛りの左に右寄せで描く。
        double firstCenter = tops[0] + tickOffset + tickStroke / 2;
        double lastCenter = tops[last] + tickOffset + tickStroke / 2;
        for (int index = 0; index < labels.Length; index++)
        {
            double center = last == 0 ? firstCenter : firstCenter + (lastCenter - firstCenter) * labelBlocks[index] / last;
            double width = MusicDisplayDrawing.TextWidth(labels[index], TextSize);
            double x = ActualWidth - (MajorTickPixels + 1 + LabelLeftShiftPixels) * pixelX - width;
            double y = Math.Round(Math.Clamp(center - TextSize / 2, 0, Math.Max(0, ActualHeight - TextSize - 1)) * dpi.DpiScaleY) * pixelY;
            // 一番上のラベル（0dBや127）は、字形の見た目の上端を最初の目盛りの上端にぴったり合わせる。
            if (index == 0)
            {
                var formatted = new FormattedText(labels[0], CultureInfo.InvariantCulture, FlowDirection.LeftToRight, MusicDisplayDrawing.Face, TextSize, ink, 1);
                double glyphTopOffset = formatted.BuildGeometry(new Point(0, 0)).Bounds.Top;
                y = tops[0] + tickOffset - glyphTopOffset;
            }
            MusicDisplayDrawing.TextAt(dc, labels[index], x, y, TextSize, ink, Math.Max(pixelX, width));
        }
    }
}

/// <summary>チャンネルごとのレベルメーター。ブロックを積んだバーとピークホールドを描く。</summary>
public sealed class MusicMeterDisplay : FrameworkElement
{
    private double[] _levels = Array.Empty<double>();
    private string[] _labels = Array.Empty<string>();
    private double[] _peaks = Array.Empty<double>();
    private int[] _holds = Array.Empty<int>();
    /// <summary>ピークが最大に達してから下がり始めるまでに保持するフレーム数（約270ms）。</summary>
    internal const int PeakHoldFrames = 16;
    public bool ShowPeakDockSegment { get; set; }
    public int HiddenTopSegments { get; set; }
    public double DisplayScale { get; set; } = 1;
    public bool FillPlotHeight { get; set; }
    public bool FillWidth { get; set; }
    /// <summary>チャンネルのバーどうしの横のすきま（物理画素）。</summary>
    public int ChannelGapPixels { get; set; } = 1;
    /// <summary>0より大きいとき、このチャンネル数のメーターと最初と最後のバーの端をそろえる。</summary>
    public int OuterEdgeReferenceChannels { get; set; }
    public int OuterEdgeReferenceGapPixels { get; set; }
    internal int MaskedChannels { get; set; }
    public int ChannelCount => _levels.Length;
    public double GetDisplayedLevel(int channel) => channel >= 0 && channel < _levels.Length ? _levels[channel] : 0;
    internal double GetPeak(int channel) => channel >= 0 && channel < _peaks.Length ? _peaks[channel] : 0;
    public double GetChannelWidth(int channel)
    {
        if (channel < 0 || channel >= _levels.Length || _levels.Length == 0) return double.NaN;
        var slot = MusicDisplayDrawing.ChannelSlot(ActualWidth, channel, _levels.Length, VisualTreeHelper.GetDpi(this).DpiScaleX);
        return slot.Right - slot.Left;
    }
    /// <summary>チャンネルのバーの中心のX座標。</summary>
    public double GetChannelCenter(int channel)
    {
        if (channel < 0 || channel >= _levels.Length || _levels.Length == 0) return double.NaN;
        if (FillWidth) { var bar = BarBounds(channel); return (bar.Left + bar.Right) / 2; }
        var slot = MusicDisplayDrawing.ChannelSlot(ActualWidth, channel, _levels.Length, VisualTreeHelper.GetDpi(this).DpiScaleX);
        return (slot.Left + slot.Right) / 2;
    }
    public double GetBarLeft(int channel) => BarBounds(channel).Left;
    public double GetBarRight(int channel) => BarBounds(channel).Right;
    public double GetBarWidth(int channel) => BarBounds(channel).Width;
    /// <summary>チャンネルのバーの左端と幅を求める（高さは使わない）。</summary>
    private Rect BarBounds(int channel)
    {
        if (channel < 0 || channel >= _levels.Length || _levels.Length == 0) return Rect.Empty;
        var dpi = VisualTreeHelper.GetDpi(this);
        double pixelX = 1 / dpi.DpiScaleX;
        double gap = Math.Max(0, ChannelGapPixels) * pixelX;
        // 幅いっぱいに並べる指定では、鍵盤の右端にそろえて配分する。
        if (FillWidth)
        {
            var edge = MusicDisplayDrawing.EdgeAlignedBar(ActualWidth, channel, _levels.Length, dpi.DpiScaleX, Math.Max(0, ChannelGapPixels));
            return new Rect(edge.Left, 0, edge.Right - edge.Left, 0);
        }
        // 基準のメーターがあれば、その外側の端の間にバーを等分に並べる。
        if (OuterEdgeReferenceChannels > 0)
        {
            var first = MusicDisplayDrawing.ChannelSlot(ActualWidth, 0, OuterEdgeReferenceChannels, dpi.DpiScaleX);
            var last = MusicDisplayDrawing.ChannelSlot(ActualWidth, OuterEdgeReferenceChannels - 1, OuterEdgeReferenceChannels, dpi.DpiScaleX);
            double leftEdge = first.Left;
            double rightEdge = last.Right - Math.Max(0, OuterEdgeReferenceGapPixels) * pixelX;
            double barWidth = Math.Max(pixelX, (rightEdge - leftEdge - gap * (_levels.Length - 1)) / _levels.Length);
            double left = leftEdge + channel * (barWidth + gap);
            return new Rect(left, 0, barWidth, 0);
        }
        // 通常はチャンネル枠からすきまを除いた幅にする。
        var slot = MusicDisplayDrawing.ChannelSlot(ActualWidth, channel, _levels.Length, dpi.DpiScaleX);
        return new Rect(slot.Left, 0, Math.Max(pixelX, slot.Right - slot.Left - gap), 0);
    }
    public MusicMeterDisplay() { SnapsToDevicePixels = true; RenderOptions.SetEdgeMode(this, EdgeMode.Aliased); }
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        InvalidateMeasure();
        InvalidateVisual();
    }
    /// <summary>
    /// 新しいレベルを受け取り、表示値とピークを更新する。上がるときはすぐ追従し、下がるときはゆっくり落とす。
    /// エンベロープの時間で動くチャンネルは値をそのまま使う。一時停止中は表示を止める。
    /// </summary>
    public void SetLevels(double[] levels, string[] labels, bool paused = false, bool[]? envelopeTimed = null)
    {
        // チャンネル数が変わったら、表示値とピークを作り直す。
        bool reset = _peaks.Length != levels.Length;
        if (reset)
        {
            _levels = new double[levels.Length];
            _peaks = new double[levels.Length];
            _holds = new int[levels.Length];
        }
        _labels = labels;
        for (int i = 0; i < levels.Length; i++)
        {
            double target = MusicDisplayDrawing.Level(levels[i]);
            if (reset)
            {
                _levels[i] = target;
                _peaks[i] = target;
                _holds[i] = target > .01 ? PeakHoldFrames : 0;
            }
            else if (!paused)
            {
                if (envelopeTimed != null && i < envelopeTimed.Length && envelopeTimed[i])
                    _levels[i] = target;
                else
                    _levels[i] = target >= _levels[i] ? target : Math.Max(target, _levels[i] * .96);
                if (_levels[i] < .002) _levels[i] = 0;
                // ピークは一定フレーム保持してから少しずつ下げる。
                double level = _levels[i];
                if (level >= _peaks[i]) { _peaks[i] = level; _holds[i] = PeakHoldFrames; }
                else if (_holds[i] > 0) _holds[i]--;
                else _peaks[i] = Math.Max(level, _peaks[i] - .025);
            }
        }
        InvalidateVisual();
    }
    public readonly record struct SegmentLayout(int Segments, double Step, double SegmentHeight, double PlotHeight);

    /// <summary>メーターのブロックの配置を求める。横の目盛りもこれを使い、ブロックとぴったりそろえる。</summary>
    public static SegmentLayout ComputeSegmentLayout(double height, double dpiScaleY, double displayScale, bool showPeakDockSegment,
        bool hasLabels, int hiddenTopSegments, bool fillPlotHeight)
    {
        // ブロックのピッチと高さを整数の物理画素で決め、入るブロック数を数える。
        double pixelY = 1 / dpiScaleY;
        double scale = Math.Max(1, displayScale);
        int stepPixels = Math.Max(3, (int)Math.Round(3 * scale, MidpointRounding.AwayFromZero));
        int heightPixels = Math.Max(2, (int)Math.Round(2 * scale, MidpointRounding.AwayFromZero));
        double segmentStep = stepPixels * pixelY;
        double segmentHeight = heightPixels * pixelY;
        bool showPeakDock = showPeakDockSegment && !hasLabels;
        double plotHeight = height - (hasLabels ? 15 : showPeakDock ? segmentHeight : 2);
        plotHeight = Math.Floor(plotHeight * dpiScaleY) / dpiScaleY;
        int availableSegments = Math.Max(1, (int)(plotHeight / segmentStep));
        int segments = Math.Max(1, availableSegments - Math.Max(0, hiddenTopSegments));
        // 高さいっぱいに広げるときも、ブロックとすきまは整数画素のままにする。余りは下に回し、
        // 最初のブロックが見出しのすぐ下に来るようにする。
        if (fillPlotHeight)
        {
            segmentStep = Math.Max(heightPixels + 1, (int)Math.Floor(plotHeight * dpiScaleY / segments)) * pixelY;
            plotHeight = segments * segmentStep;
        }
        return new SegmentLayout(segments, segmentStep, segmentHeight, plotHeight);
    }

    /// <summary>レベルメーターを描く。</summary>
    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth, height = ActualHeight;
        if (width <= 0 || height <= 0) return;
        dc.DrawRectangle(MusicDisplayDrawing.Black, null, new Rect(0, 0, width, height));
        int count = _levels.Length;
        if (count == 0) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        double pixelX = 1 / dpi.DpiScaleX;
        double pixelY = 1 / dpi.DpiScaleY;
        bool showPeakDock = ShowPeakDockSegment && _labels.Length == 0;
        var layout = ComputeSegmentLayout(height, dpi.DpiScaleY, DisplayScale, ShowPeakDockSegment, _labels.Length > 0, HiddenTopSegments, FillPlotHeight);
        int segments = layout.Segments;
        double segmentStep = layout.Step;
        double segmentHeight = layout.SegmentHeight;
        double plotHeight = layout.PlotHeight;
        // チャンネルごとに、点灯ブロック、ピーク、下端のブロック、ラベルを描く。ミュート中のチャンネルは灰色にする。
        for (int index = 0; index < count; index++)
        {
            int lit = (int)Math.Round(MusicDisplayDrawing.Level(_levels[index]) * segments);
            Rect bar = BarBounds(index);
            double x = bar.Left;
            double barWidth = bar.Width;
            bool masked = index < 32 && (MaskedChannels & (1 << index)) != 0;
            for (int segment = 0; segment < segments; segment++)
            {
                Brush brush = masked ? MusicDisplayDrawing.KeyboardInactiveWhiteBorder : segment >= lit ? MusicDisplayDrawing.MeterOff : MusicDisplayDrawing.MeterLit;
                dc.DrawRectangle(brush, null, new Rect(x, plotHeight - (segment + 1) * segmentStep, barWidth, segmentHeight));
            }
            int peakSegment = _peaks[index] > .01
                ? Math.Clamp((int)Math.Round(_peaks[index] * segments), 1, segments)
                : 0;
            if (!masked && peakSegment > 0)
                dc.DrawRectangle(MusicDisplayDrawing.MeterPeak, null,
                    new Rect(x, Math.Max(0, plotHeight - peakSegment * segmentStep), barWidth, segmentHeight));
            if (showPeakDock)
            {
                dc.DrawRectangle(masked ? MusicDisplayDrawing.KeyboardInactiveWhiteBorder : lit > 0 ? MusicDisplayDrawing.MeterLit : MusicDisplayDrawing.MeterEndpoint, null,
                    new Rect(x, plotHeight, barWidth, segmentHeight));
            }
            if (index < _labels.Length && barWidth >= 15) MusicDisplayDrawing.TextAt(dc, _labels[index], x, height - 12, 9, MusicDisplayDrawing.Muted, barWidth);
        }
    }
}

/// <summary>チャンネルごとに、鳴っているノート番号を3桁の数字で並べる表示。</summary>
public sealed class MusicNoteNumberDisplay : FrameworkElement
{
    public const double TextSize = 14;
    private int?[] _notes = new int?[16];
    private bool[] _sounding = new bool[16];
    public int ChannelCount => _notes.Length;
    public bool IsNoteActive(int channel) => channel >= 0 && channel < _notes.Length && _sounding[channel] && _notes[channel].HasValue;
    public string GetNoteText(int channel) => IsNoteActive(channel) && _notes[channel] is int note
        ? Math.Clamp(note, 0, 999).ToString("D3", CultureInfo.InvariantCulture)
        : "000";

    public MusicNoteNumberDisplay()
    {
        SnapsToDevicePixels = true;
        ClipToBounds = true;
    }

    /// <summary>ノート番号と発音中かどうかを写し取り、再描画する。</summary>
    public void SetNotes(int?[] notes, bool[] sounding)
    {
        int count = Math.Max(notes.Length, sounding.Length);
        _notes = new int?[count];
        _sounding = new bool[count];
        Array.Copy(notes, _notes, Math.Min(notes.Length, count));
        Array.Copy(sounding, _sounding, Math.Min(sounding.Length, count));
        InvalidateVisual();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(MusicDisplayDrawing.Black, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (_notes.Length == 0 || ActualWidth <= 0 || ActualHeight <= 0) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        Brush ink = ThemeBrushes.Get("MutedText", this);
        Brush inactiveInk = MusicDisplayDrawing.KeyboardInactiveWhiteKey;
        // 各チャンネル枠の中央に番号を描く。鳴っていないチャンネルは暗い色にする。
        for (int channel = 0; channel < _notes.Length; channel++)
        {
            string text = GetNoteText(channel);
            var slot = MusicDisplayDrawing.ChannelSlot(ActualWidth, channel, _notes.Length, dpi.DpiScaleX);
            double textWidth = MusicDisplayDrawing.TextWidth(text, TextSize);
            MusicDisplayDrawing.TextAt(dc, text, (slot.Left + slot.Right - textWidth) / 2, 0, TextSize,
                IsNoteActive(channel) ? ink : inactiveInk, slot.Right - slot.Left);
        }
    }
}

/// <summary>チャンネルごとの左右の定位を、リングと点の向きで示すパン表示。</summary>
public sealed class MusicPanDisplay : FrameworkElement
{
    public bool FillWidth { get; set; }
    public int ChannelGapPixels { get; set; } = 2;
    public const double RingDiameterPixels = 16;
    public const double RingStrokePixels = 1;
    public const double DotDiameterPixels = 4;
    private double[] _leftLevels = new double[16];
    private double[] _rightLevels = new double[16];
    private bool[] _active = Enumerable.Repeat(true, 16).ToArray();
    public double DisplayScale { get; set; } = 1;
    public int ChannelCount => _leftLevels.Length;
    public bool IsChannelActive(int channel) => channel >= 0 && channel < _active.Length && _active[channel];
    public (double Left, double Right) GetPanLevels(int channel) => channel >= 0 && channel < _leftLevels.Length
        ? (_leftLevels[channel], _rightLevels[channel]) : (0, 0);
    /// <summary>左右のレベルから、-1（左）〜1（右）の定位を求める。</summary>
    public double GetIndicatorOffset(int channel)
    {
        if (channel < 0 || channel >= _leftLevels.Length) return double.NaN;
        double total = _leftLevels[channel] + _rightLevels[channel];
        return total > .001 ? (_rightLevels[channel] - _leftLevels[channel]) / total : 0;
    }
    /// <summary>定位を、真上から左右に最大135度振った向きの単位ベクトルにする。</summary>
    public Point GetIndicatorDirection(int channel)
    {
        double balance = GetIndicatorOffset(channel);
        if (!double.IsFinite(balance)) return new Point(double.NaN, double.NaN);
        double angle = (-90 + balance * 135) * Math.PI / 180;
        return new Point(Math.Cos(angle), Math.Sin(angle));
    }
    /// <summary>チャンネルの表示枠の中心のX座標。</summary>
    public double GetChannelCenter(int channel)
    {
        if (channel < 0 || channel >= _leftLevels.Length || _leftLevels.Length == 0) return double.NaN;
        var slot = FillWidth
            ? MusicDisplayDrawing.EdgeAlignedBar(ActualWidth, channel, _leftLevels.Length, VisualTreeHelper.GetDpi(this).DpiScaleX, ChannelGapPixels)
            : MusicDisplayDrawing.ChannelSlot(ActualWidth, channel, _leftLevels.Length, VisualTreeHelper.GetDpi(this).DpiScaleX);
        return (slot.Left + slot.Right) / 2;
    }

    public MusicPanDisplay()
    {
        SnapsToDevicePixels = true;
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        InvalidateMeasure();
        InvalidateVisual();
    }

    /// <summary>左右のレベルとチャンネルの有効状態を写し取り、再描画する。</summary>
    public void SetPanLevels(double[] leftLevels, double[] rightLevels, bool[]? active = null)
    {
        int count = Math.Max(leftLevels.Length, rightLevels.Length);
        _leftLevels = new double[count];
        _rightLevels = new double[count];
        _active = new bool[count];
        for (int index = 0; index < count; index++)
        {
            if (index < leftLevels.Length) _leftLevels[index] = MusicDisplayDrawing.Level(leftLevels[index]);
            if (index < rightLevels.Length) _rightLevels[index] = MusicDisplayDrawing.Level(rightLevels[index]);
            _active[index] = active == null || index >= active.Length || active[index];
        }
        InvalidateVisual();
    }

    /// <summary>チャンネルごとにリングを描き、鳴っていれば定位の向きに点を置く。</summary>
    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth;
        double height = ActualHeight;
        dc.DrawRectangle(MusicDisplayDrawing.Black, null, new Rect(0, 0, width, height));
        if (_leftLevels.Length == 0 || width <= 0 || height <= 0) return;

        // リングと点の大きさを物理画素単位で決める。
        var dpi = VisualTreeHelper.GetDpi(this);
        double pixelX = 1 / dpi.DpiScaleX;
        double pixelY = 1 / dpi.DpiScaleY;
        double scale = Math.Max(1, DisplayScale);
        double ringDiameter = RingDiameterPixels * scale * pixelX;
        double ringRadius = ringDiameter / 2;
        double dotRadius = DotDiameterPixels * scale * pixelX / 2;
        double centerY = Math.Floor(height * dpi.DpiScaleY / 2) * pixelY;
        var ringPen = new Pen(MusicDisplayDrawing.PanRing, RingStrokePixels * scale * pixelX);
        ringPen.Freeze();
        var inactiveRingPen = new Pen(MusicDisplayDrawing.KeyboardInactiveWhiteKey, RingStrokePixels * scale * pixelX);
        inactiveRingPen.Freeze();
        // 無効なチャンネルはリングだけを暗い色で描く。点はリングの内側に収まる範囲で動かす。
        for (int index = 0; index < _leftLevels.Length; index++)
        {
            double center = GetChannelCenter(index);
            bool active = IsChannelActive(index);
            dc.DrawEllipse(null, active ? ringPen : inactiveRingPen, new Point(center, centerY), ringRadius, ringRadius);
            if (!active) continue;
            double total = _leftLevels[index] + _rightLevels[index];
            if (total <= .001) continue;
            Point direction = GetIndicatorDirection(index);
            double travel = Math.Max(0, ringRadius - dotRadius - RingStrokePixels * scale * pixelX / 2);
            dc.DrawEllipse(MusicDisplayDrawing.PanDot, null,
                new Point(center + direction.X * travel, centerY + direction.Y * travel), dotRadius, dotRadius);
        }
    }
}

/// <summary>全チャンネルの発音を1段の鍵盤にまとめて光らせる、コンパクトな鍵盤表示。NanoDriveで鳴っている音は別の色にする。</summary>
public sealed class MusicKeyboardDisplay : FrameworkElement
{
    /// <summary>白鍵1つの幅。4DIPを物理画素に丸め、すきま用に物理画素1つを足す。</summary>
    public static double KeyPitch(double dpiScale) => (Math.Round(4 * dpiScale, MidpointRounding.AwayFromZero) + 1) / dpiScale;
    private IReadOnlySet<int> _notes = new HashSet<int>();
    private IReadOnlySet<int> _ndNotes = new HashSet<int>();
    private readonly List<double> _pitches = [];
    private readonly double[] _smoothedPitches = new double[PitchTimeline.MaxChannels];
    private readonly bool[] _wasKeyOn = new bool[PitchTimeline.MaxChannels];
    private readonly int[] _lastKeyGen = new int[PitchTimeline.MaxChannels];
    private readonly int?[] _heldNote = new int?[PitchTimeline.MaxChannels];
    private readonly int?[] _lastNote = new int?[PitchTimeline.MaxChannels];
    private long _lastRenderTicks;

    public MusicKeyboardDisplay()
    {
        SnapsToDevicePixels = true;
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
        Array.Fill(_smoothedPitches, double.NaN);
    }

    /// <summary>
    /// チャンネルの状態から光らせる鍵と音程の棒を決める。キーオン中の音名は押した時点の音を保ち、
    /// 音程の棒は前回からの経過時間に応じてなめらかに追従させる。
    /// </summary>
    public void SetChannels(MusicChannelSnapshot[] channels)
    {
        // 経過時間から、音程を追従させる割合を求める。
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        double dt = _lastRenderTicks > 0 ? Math.Clamp((double)(now - _lastRenderTicks) / System.Diagnostics.Stopwatch.Frequency, 0.0, 0.1) : 0.0;
        _lastRenderTicks = now;
        double alpha = !IsLoaded || dt <= 0 ? 1.0 : Math.Clamp(1.0 - Math.Exp(-75.0 * dt), 0.15, 1.0);

        var notes = new HashSet<int>();
        var ndNotes = new HashSet<int>();
        _pitches.Clear();
        int count = Math.Min(channels.Length, PitchTimeline.MaxChannels);
        for (int i = 0; i < count; i++)
        {
            // 新しいキーオン（キー世代かノート番号の変化）なら、押した音を記録し直す。
            var channel = channels[i];
            bool isNewKeyOn = channel.IsKeyOn && (!_wasKeyOn[i] ||
                (channel.KeyGeneration > 0 ? channel.KeyGeneration != _lastKeyGen[i] : (channel.NoteNumber.HasValue && channel.NoteNumber != _heldNote[i])));
            if (isNewKeyOn || (channel.IsKeyOn && !_heldNote[i].HasValue))
            {
                _heldNote[i] = channel.NoteNumber;
            }
            // 光らせる鍵を決める。鍵盤の表示範囲に合わせて1オクターブ上げる。
            int? soundingNote = (channel.IsKeyOn && _heldNote[i].HasValue)
                ? _heldNote[i]
                : (channel.IsKeyOn && channel.NoteNumber.HasValue ? channel.NoteNumber.Value : (channel.KeyboardNotes.Length > 0 ? channel.KeyboardNotes[0] : null));
            if (soundingNote is int note)
            {
                int mapped = note + 12;
                notes.Add(mapped);
                if (channel.IsHardwareOutput) ndNotes.Add(mapped);
            }
            // 音程の棒の位置を更新する。新しいキーオンや大きな跳躍、半音ちょうどの間の移動はすぐ合わせる。
            if (channel.IsKeyOn && MusicPitch.Display(channel) is double rawPitch)
            {
                static bool IsIntegerSemitone(double p) => Math.Abs(p - Math.Round(p)) < 0.005;
                bool snap = isNewKeyOn ||
                    !IsLoaded ||
                    double.IsNaN(_smoothedPitches[i]) ||
                    Math.Abs(rawPitch - _smoothedPitches[i]) >= 0.85 ||
                    (IsIntegerSemitone(rawPitch) && IsIntegerSemitone(_smoothedPitches[i]));
                _smoothedPitches[i] = snap ? rawPitch : _smoothedPitches[i] + (rawPitch - _smoothedPitches[i]) * alpha;
                _pitches.Add(_smoothedPitches[i] + 12.0);
                _wasKeyOn[i] = true;
                _lastKeyGen[i] = channel.KeyGeneration;
                _lastNote[i] = channel.NoteNumber;
            }
            else
            {
                _wasKeyOn[i] = false;
                _smoothedPitches[i] = double.NaN;
                _lastNote[i] = null;
                _heldNote[i] = null;
            }
        }
        // 来なかったチャンネルの状態は消しておく。
        for (int i = count; i < PitchTimeline.MaxChannels; i++)
        {
            _wasKeyOn[i] = false;
            _smoothedPitches[i] = double.NaN;
            _lastNote[i] = null;
            _heldNote[i] = null;
        }
        _notes = notes;
        _ndNotes = ndNotes;
        InvalidateVisual();
    }
    /// <summary>コンパクト表示の鍵盤を描く。</summary>
    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(MusicDisplayDrawing.Black, null, new Rect(0, 0, ActualWidth, ActualHeight));
        // 白鍵ごとにちょうど物理画素1つのすきまを足す。小数のDPI倍率でも同じにする。
        MusicDisplayDrawing.FixedKeyboard(dc, new Rect(0, 0, ActualWidth, ActualHeight), _notes, KeyPitch(VisualTreeHelper.GetDpi(this).DpiScaleX),
            compactDpi: VisualTreeHelper.GetDpi(this), ndActive: _ndNotes,
            pitches: _pitches,
            marker: ThemeBrushes.Get("HighlightText", this));
    }
}

/// <summary>サウンドビューアで、チャンネルごとの鍵盤を固定の高さで12段並べる表示。各鍵盤の上の1行にチャンネル名、波形、音名を出す。</summary>
public sealed class MusicKeyboardRowsDisplay : FrameworkElement
{
    private const int MaxTrackedChannels = 36;
    private MusicChannelSnapshot[] _channels = Array.Empty<MusicChannelSnapshot>();
    private readonly double[] _smoothedPitches = new double[MaxTrackedChannels];
    private readonly bool[] _wasKeyOn = new bool[MaxTrackedChannels];
    private readonly int[] _lastKeyGen = new int[MaxTrackedChannels];
    private readonly int?[] _heldNote = new int?[MaxTrackedChannels];
    private readonly int?[] _lastNote = new int?[MaxTrackedChannels];
    private readonly string?[] _cachedHeaderText = new string?[MaxTrackedChannels];
    private readonly Brush?[] _cachedHeaderBrush = new Brush?[MaxTrackedChannels];
    private readonly FormattedText?[] _cachedHeaderFormatted = new FormattedText?[MaxTrackedChannels];
    private readonly string?[] _cachedNoteText = new string?[MaxTrackedChannels];
    private readonly Brush?[] _cachedNoteBrush = new Brush?[MaxTrackedChannels];
    private readonly FormattedText?[] _cachedNoteFormatted = new FormattedText?[MaxTrackedChannels];
    private long _lastRenderTicks;

    public int FirstChannel { get; private set; }
    public int MaximumFirstChannel => Math.Max(0, _channels.Length - 12);
    public event Action<int>? FirstChannelChanged;

    private int _visibleMask;
    /// <summary>鍵盤を消灯させる段（ビットが立った段）を設定する。</summary>
    internal void SetVisibleMask(int mask)
    {
        if (_visibleMask == mask) return;
        _visibleMask = mask;
        InvalidateVisual();
    }

    /// <summary>表示する先頭チャンネルをずらし、変わったら通知する。</summary>
    public void ScrollChannels(int delta)
    {
        int next = Math.Clamp(FirstChannel + delta, 0, MaximumFirstChannel);
        if (next == FirstChannel) return;
        FirstChannel = next;
        InvalidateVisual();
        FirstChannelChanged?.Invoke(FirstChannel);
    }

    public MusicKeyboardRowsDisplay()
    {
        SnapsToDevicePixels = true;
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
        Array.Fill(_smoothedPitches, double.NaN);
    }

    /// <summary>
    /// チャンネルの状態を受け取り、押した音と音程の棒の位置を更新する。
    /// 追従のしかたはコンパクト表示の鍵盤と同じ。
    /// </summary>
    public void SetChannels(MusicChannelSnapshot[] channels)
    {
        // 経過時間から、音程を追従させる割合を求める。
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        double dt = _lastRenderTicks > 0 ? Math.Clamp((double)(now - _lastRenderTicks) / System.Diagnostics.Stopwatch.Frequency, 0.0, 0.1) : 0.0;
        _lastRenderTicks = now;
        double alpha = !IsLoaded || dt <= 0 ? 1.0 : Math.Clamp(1.0 - Math.Exp(-75.0 * dt), 0.15, 1.0);

        _channels = channels.Take(MaxTrackedChannels).ToArray();
        for (int i = 0; i < _channels.Length; i++)
        {
            // 新しいキーオンなら、押した音を記録し直す。
            var ch = _channels[i];
            bool isNewKeyOn = ch.IsKeyOn && (!_wasKeyOn[i] ||
                (ch.KeyGeneration > 0 ? ch.KeyGeneration != _lastKeyGen[i] : (ch.NoteNumber.HasValue && ch.NoteNumber != _heldNote[i])));
            if (isNewKeyOn || (ch.IsKeyOn && !_heldNote[i].HasValue))
            {
                _heldNote[i] = ch.NoteNumber;
            }

            // 音程の棒の位置を更新する。新しいキーオンや大きな跳躍、半音ちょうどの間の移動はすぐ合わせる。
            if (ch.IsKeyOn && MusicPitch.Display(ch) is double rawPitch)
            {
                static bool IsIntegerSemitone(double p) => Math.Abs(p - Math.Round(p)) < 0.005;
                bool snap = isNewKeyOn ||
                    !IsLoaded ||
                    double.IsNaN(_smoothedPitches[i]) ||
                    Math.Abs(rawPitch - _smoothedPitches[i]) >= 0.85 ||
                    (IsIntegerSemitone(rawPitch) && IsIntegerSemitone(_smoothedPitches[i]));
                _smoothedPitches[i] = snap ? rawPitch : _smoothedPitches[i] + (rawPitch - _smoothedPitches[i]) * alpha;
                _wasKeyOn[i] = true;
                _lastKeyGen[i] = ch.KeyGeneration;
                _lastNote[i] = ch.NoteNumber;
            }
            else
            {
                _wasKeyOn[i] = false;
                _smoothedPitches[i] = double.NaN;
                _lastNote[i] = null;
                _heldNote[i] = null;
            }
        }
        // 来なかったチャンネルの状態は消しておく。
        for (int i = _channels.Length; i < MaxTrackedChannels; i++)
        {
            _wasKeyOn[i] = false;
            _smoothedPitches[i] = double.NaN;
            _lastNote[i] = null;
            _heldNote[i] = null;
        }
        // チャンネル数が減ったときは、先頭チャンネルを範囲内に戻して通知する。
        int next = Math.Clamp(FirstChannel, 0, MaximumFirstChannel);
        bool moved = next != FirstChannel;
        FirstChannel = next;
        InvalidateVisual();
        if (moved) FirstChannelChanged?.Invoke(FirstChannel);
    }

    /// <summary>段ごとに作った文字の描画結果を使い回して描く。文字と色が前回と同じなら作り直さない。</summary>
    private static void DrawCachedText(
        DrawingContext dc,
        int slot,
        string text,
        double x,
        double y,
        double size,
        Brush brush,
        string?[] textCache,
        Brush?[] brushCache,
        FormattedText?[] formattedCache)
    {
        if ((uint)slot < (uint)formattedCache.Length)
        {
            if (formattedCache[slot] is FormattedText cached &&
                string.Equals(textCache[slot], text, StringComparison.Ordinal) &&
                ReferenceEquals(brushCache[slot], brush))
            {
                dc.DrawText(cached, new Point(Math.Floor(x), Math.Floor(y)));
                return;
            }
            var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, MusicDisplayDrawing.Face, size, brush, 1);
            textCache[slot] = text;
            brushCache[slot] = brush;
            formattedCache[slot] = formatted;
            dc.DrawText(formatted, new Point(Math.Floor(x), Math.Floor(y)));
            return;
        }
        MusicDisplayDrawing.TextAt(dc, text, x, y, size, brush);
    }

    /// <summary>12段の見出し行と鍵盤を描く。</summary>
    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(MusicDisplayDrawing.Black, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (ActualWidth < 8 || ActualHeight < 16) return;

        // 文字グリッド（8×14）に合わせて、各段の高さと鍵盤の幅、音名欄の位置を決める。
        const int channelCount = 12;
        const double characterWidth = 8;
        const double characterHeight = 14;
        const int whiteKeys = 63;
        double keyboardHeight = characterHeight * 2;
        double blockHeight = characterHeight + keyboardHeight;
        Brush channelText = ThemeBrushes.Get("PrimaryText", this);
        Brush accentText = ThemeBrushes.Get("AccentText", this);
        Brush highlight = ThemeBrushes.Get("HighlightText", this);
        double keyboardWidth = Math.Min(ActualWidth, whiteKeys * characterWidth);
        double noteBlockWidth = MusicDisplayDrawing.NoteDisplayLength * characterWidth;
        double noteX = Math.Max(0, keyboardWidth - noteBlockWidth);
        var dpi = VisualTreeHelper.GetDpi(this);

        for (int channel = 0; channel < channelCount; channel++)
        {
            double y = Math.Floor(channel * blockHeight + characterHeight);
            var active = new HashSet<int>();
            var pitches = new List<double>();
            string chipName = "CHANNEL";
            string kind = "";
            bool inUse = false;
            bool isNd = false;
            int? currentNote = null;
            bool estimated = false;
            byte[]? waveform = null;
            string waveformLabel = "Wavetable : ";
            int index = FirstChannel + channel;
            double? displayPitch = null;
            bool isKeyOn = false;
            // チャンネルがあれば、光らせる鍵、音程の棒、表示する音名を決める。ノイズとリズムには音名を出さない。
            if (index < _channels.Length)
            {
                var state = _channels[index];
                if (!string.IsNullOrWhiteSpace(state.ChipName)) chipName = state.ChipName;
                kind = state.KindLabel;
                inUse = state.IsInUse;
                isNd = state.IsHardwareOutput;
                waveform = state.Waveform;
                waveformLabel = state.WaveformLabel;
                isKeyOn = state.IsKeyOn;
                int? soundingNote = (isKeyOn && (uint)index < MaxTrackedChannels && _heldNote[index].HasValue)
                    ? _heldNote[index]
                    : (isKeyOn && state.NoteNumber.HasValue ? state.NoteNumber : (state.KeyboardNotes.Length > 0 ? state.KeyboardNotes[0] : null));

                if (isKeyOn && soundingNote is int note)
                {
                    active.Add(note + 12);
                }
                else if (!isKeyOn && state.KeyboardNotes.Length > 0)
                {
                    active.Add(state.KeyboardNotes[0] + 12);
                }
                double smoothed = (uint)index < (uint)_smoothedPitches.Length ? _smoothedPitches[index] : double.NaN;
                displayPitch = double.IsFinite(smoothed) ? smoothed : (state.IsKeyOn ? MusicPitch.Display(state) : null);
                if (state.IsKeyOn && displayPitch is double pitch) pitches.Add(pitch + 12);
                if (soundingNote is int displayNote &&
                    state.Kind is not (MusicChannelKind.Noise or MusicChannelKind.Rhythm) &&
                    (state.IsKeyOn || active.Count > 0))
                {
                    currentNote = displayNote;
                    estimated = state.IsNoteEstimated;
                }
            }
            // 見出し行にチャンネル番号、音源名、種類を描く。
            Brush rowBrush = inUse ? channelText : MusicDisplayDrawing.Muted;
            DrawCachedText(dc, index, $"CH {index + 1:D2} {chipName} {kind}", 0, y - characterHeight, characterHeight, rowBrush,
                _cachedHeaderText, _cachedHeaderBrush, _cachedHeaderFormatted);

            // 波形メモリーのある音源は、音名欄の左に波形を描く。左の見出しと重なるときは描かない。
            if (waveform is { Length: > 0 })
            {
                double labelWidth = waveformLabel.Length * characterWidth;
                double graphWidth = 32;
                double waveSectionWidth = labelWidth + graphWidth;
                double waveSectionRight = noteX - 16;
                double waveSectionX = waveSectionRight - waveSectionWidth;
                double leftTextWidth = 17 * characterWidth + 8;

                if (waveSectionX >= leftTextWidth)
                {
                    double graphX = waveSectionX + labelWidth;
                    double graphTop = y - characterHeight + 3;
                    double graphHeight = 8;

                    MusicDisplayDrawing.TextAt(dc, waveformLabel, waveSectionX, y - characterHeight, characterHeight, rowBrush);
                    MusicDisplayDrawing.DrawWaveform(dc, waveform, graphX, graphTop, graphWidth, graphHeight, inUse && (active.Count > 0), accentText, dpi);
                }
            }

            // 消灯指定の段は、鍵も音程も出さない。
            bool masked = (_visibleMask & (1 << channel)) != 0;
            if (masked) { active.Clear(); pitches.Clear(); displayPitch = null; isKeyOn = false; }

            // 音名とセントずれを描き、その下に鍵盤を描く。
            int cents = (isKeyOn && (uint)index < MaxTrackedChannels) ? MusicPitch.Cents(displayPitch, currentNote) : 0;
            string noteDisplay = MusicDisplayDrawing.FormatNoteDisplay(currentNote, estimated, cents);
            DrawCachedText(dc, index, noteDisplay, noteX, y - characterHeight, characterHeight, rowBrush,
                _cachedNoteText, _cachedNoteBrush, _cachedNoteFormatted);

            MusicDisplayDrawing.FixedKeyboard(dc, new Rect(0, y, keyboardWidth, keyboardHeight), active, characterWidth, masked || !inUse, isNdPart: isNd,
                pitches: pitches, marker: highlight, dpi: dpi);
        }
    }
}

/// <summary>サウンドビューアのシークバー。マウスのクリックとドラッグで再生位置を指定する。</summary>
public sealed class MusicSeekBarDisplay : FrameworkElement
{
    public int ReferenceChannels { get; set; } = 24;
    public int ReferenceGapPixels { get; set; } = 2;
    public double Progress { get; private set; }
    public bool IsDragging { get; private set; }
    public event Action<double>? SeekRequested;

    public MusicSeekBarDisplay()
    {
        SnapsToDevicePixels = true;
        ClipToBounds = true;
        Focusable = false;
        Cursor = System.Windows.Input.Cursors.Hand;
    }

    /// <summary>バーの左端。基準のメーターの最初のバーの左端に合わせる。</summary>
    public double GetLeftEdge()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        return MusicDisplayDrawing.EdgeAlignedBar(ActualWidth, 0, Math.Max(1, ReferenceChannels), dpi.DpiScaleX, Math.Max(0, ReferenceGapPixels)).Left;
    }

    /// <summary>バーの右端。基準のメーターの最後のバーの右端に合わせる。</summary>
    public double GetRightEdge()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        return MusicDisplayDrawing.EdgeAlignedBar(ActualWidth, Math.Max(0, ReferenceChannels - 1), Math.Max(1, ReferenceChannels), dpi.DpiScaleX, Math.Max(0, ReferenceGapPixels)).Right;
    }

    /// <summary>再生位置を表示に反映する。ドラッグ中は利用者の操作を優先して無視する。</summary>
    public void SetProgress(double ratio)
    {
        if (IsDragging) return;
        double clamped = double.IsFinite(ratio) ? Math.Clamp(ratio, 0, 1) : 0;
        if (Math.Abs(clamped - Progress) < .0005) return;
        Progress = clamped;
        InvalidateVisual();
    }

    /// <summary>指定の割合へシークするよう要求する。</summary>
    public void SeekToRatio(double ratio)
    {
        Progress = double.IsFinite(ratio) ? Math.Clamp(ratio, 0, 1) : 0;
        InvalidateVisual();
        SeekRequested?.Invoke(Progress);
    }

    /// <summary>マウス位置をバー上の割合に直してシークする。</summary>
    private void UpdateFromPoint(Point pt)
    {
        double left = GetLeftEdge();
        double right = GetRightEdge();
        double span = right - left;
        if (span <= 0) return;
        SeekToRatio((pt.X - left) / span);
    }

    /// <summary>クリックした位置へシークし、マウスを捕まえてドラッグを始める。</summary>
    protected override void OnMouseLeftButtonDown(System.Windows.Input.MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        IsDragging = true;
        CaptureMouse();
        UpdateFromPoint(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!IsDragging || !IsMouseCaptured) return;
        UpdateFromPoint(e.GetPosition(this));
        e.Handled = true;
    }

    /// <summary>離した位置へシークし、ドラッグを終えてマウスを放す。</summary>
    protected override void OnMouseLeftButtonUp(System.Windows.Input.MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!IsDragging) return;
        UpdateFromPoint(e.GetPosition(this));
        IsDragging = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(System.Windows.Input.MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        IsDragging = false;
        InvalidateVisual();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        InvalidateVisual();
    }

    /// <summary>溝、再生済みの部分、つまみを物理画素にそろえて描く。</summary>
    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(MusicDisplayDrawing.Black, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        double pixelX = 1 / dpi.DpiScaleX;
        double pixelY = 1 / dpi.DpiScaleY;
        double left = GetLeftEdge();
        double right = GetRightEdge();
        double span = right - left;
        if (span <= 0) return;

        // 角の丸い溝を上下中央に描く。
        double trackHeight = Math.Max(4 * pixelY, Math.Round(4 * dpi.DpiScaleY) * pixelY);
        double trackTop = Math.Floor((ActualHeight - trackHeight) / 2 * dpi.DpiScaleY) * pixelY;
        double trackRadius = trackHeight / 2;
        var borderPen = new Pen(MusicDisplayDrawing.Border, pixelX);
        borderPen.Freeze();
        dc.DrawRoundedRectangle(MusicDisplayDrawing.MeterOff, borderPen, new Rect(left, trackTop, span, trackHeight), trackRadius, trackRadius);

        // 再生済みの部分を溝の内側に塗る。
        double fillWidth = Math.Round(span * Progress * dpi.DpiScaleX) * pixelX;
        if (fillWidth > 0)
        {
            double innerHeight = Math.Max(pixelY, trackHeight - 2 * pixelY);
            double innerRadius = innerHeight / 2;
            dc.DrawRoundedRectangle(MusicDisplayDrawing.MeterLit, null,
                new Rect(left, trackTop + pixelY, Math.Min(span, fillWidth), innerHeight), innerRadius, innerRadius);
        }

        // つまみを再生位置に描く。ドラッグ中は色を変える。
        double thumbWidth = Math.Max(4 * pixelX, Math.Round(5 * dpi.DpiScaleX) * pixelX);
        double thumbHeight = Math.Max(trackHeight + 2 * pixelY, Math.Round(Math.Min(ActualHeight - 2 * pixelY, 10) * dpi.DpiScaleY) * pixelY);
        double thumbTop = Math.Floor((ActualHeight - thumbHeight) / 2 * dpi.DpiScaleY) * pixelY;
        double thumbLeft = Math.Clamp(left + span * Progress - thumbWidth / 2, left, Math.Max(left, right - thumbWidth));
        thumbLeft = Math.Round(thumbLeft * dpi.DpiScaleX) * pixelX;
        double thumbRadius = thumbWidth / 2;
        dc.DrawRoundedRectangle(IsDragging ? MusicDisplayDrawing.Amber : MusicDisplayDrawing.Cyan, null,
            new Rect(thumbLeft, thumbTop, thumbWidth, thumbHeight), thumbRadius, thumbRadius);
    }
}

/// <summary>経過時間などを、少し斜めにした7セグメント風の数字で出す表示。左に目印の箱と2行のラベルを付けられる。</summary>
public sealed class MusicSevenSegmentDisplay : FrameworkElement
{
    private string _text = "00:00";
    public double DigitHeight { get; set; } = 18.67;
    public double DigitWidth { get; set; } = 10;
    public double SegmentThickness { get; set; } = 1.9;
    public double DigitSpacing { get; set; } = 3;
    public double ColonWidth { get; set; } = 3.4;
    public double SlantDegrees { get; set; } = 8;
    public bool ShowIndicator { get; set; } = true;
    public double IndicatorWidth { get; set; } = 4;
    public double IndicatorSpacing { get; set; } = 5;
    public double FixedWidth { get; set; } = 114;
    public double LabelSize { get; set; } = 8.5;
    public HorizontalAlignment DigitsAlignment { get; set; } = HorizontalAlignment.Right;
    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(
        nameof(Foreground), typeof(Brush), typeof(MusicSevenSegmentDisplay),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public Brush? Foreground
    {
        get => (Brush?)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    private string? _labelLine1;
    public string? LabelLine1
    {
        get => _labelLine1;
        set
        {
            if (_labelLine1 != value)
            {
                _labelLine1 = value;
                InvalidateVisual();
            }
        }
    }

    private string? _labelLine2;
    public string? LabelLine2
    {
        get => _labelLine2;
        set
        {
            if (_labelLine2 != value)
            {
                _labelLine2 = value;
                InvalidateVisual();
            }
        }
    }

    private static readonly Brush DefaultDimBrush = ThemeBrushes.Get("MusicDim");

    public string Text
    {
        get => _text;
        set
        {
            string next = value ?? "";
            if (_text == next) return;
            _text = next;
            InvalidateMeasure();
            InvalidateVisual();
        }
    }

    public MusicSevenSegmentDisplay()
    {
        SnapsToDevicePixels = true;
        Focusable = false;
    }

    private double SlantOffset => DigitHeight * Math.Tan(SlantDegrees * Math.PI / 180.0);

    /// <summary>数字部分の幅。コロン、スラッシュ、空白は数字より狭く数える。</summary>
    private double DigitsWidth
    {
        get
        {
            double width = SlantOffset;
            for (int i = 0; i < _text.Length; i++)
            {
                char c = _text[i];
                if (c == ':') width += ColonWidth;
                else if (c == '/') width += DigitWidth * 0.7;
                else if (c == ' ') width += DigitWidth * 0.35;
                else width += DigitWidth;
                if (i + 1 < _text.Length) width += DigitSpacing;
            }
            return width;
        }
    }

    /// <summary>全体の幅。目印やラベルがあるときは、その分を含めて固定幅以上にする。</summary>
    private double ContentWidth
    {
        get
        {
            double digitsW = DigitsWidth;
            if (ShowIndicator || !string.IsNullOrEmpty(LabelLine1) || !string.IsNullOrEmpty(LabelLine2))
            {
                return Math.Max(FixedWidth, digitsW + 52.0);
            }
            return digitsW;
        }
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(Math.Ceiling(Math.Max(1, ContentWidth)), Math.Ceiling(DigitHeight));

    /// <summary>目印の箱、ラベル、7セグメントの数字を描く。</summary>
    protected override void OnRender(DrawingContext dc)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0 || string.IsNullOrEmpty(_text)) return;
        Brush litBrush = Foreground
            ?? ThemeBrushes.Get("AccentText", this);
        Brush dimBrush = DefaultDimBrush;
        Brush highlightBrush = ThemeBrushes.Get("HighlightText", this);
        Brush labelBrush = ThemeBrushes.Get("MutedText", this);

        double h = DigitHeight;
        double w = DigitWidth;
        double t = SegmentThickness;
        double slant = Math.Tan(SlantDegrees * Math.PI / 180.0);
        double x = ActualWidth - ContentWidth;
        double yOffset = ActualHeight - h;

        dc.PushTransform(new TranslateTransform(0, yOffset));

        // 目印の箱を内容の左端に描く。並んだ表示どうしで箱の位置がそろうよう、同じ基準で置く。
        if (ShowIndicator)
        {
            dc.DrawRectangle(highlightBrush, null, new Rect(x, 0, IndicatorWidth, h));
        }

        // 箱の右に2行のラベル（DIGITAL CLOCK、ELAPSED TIME、LOOP COUNTSなど）を描く。
        if (!string.IsNullOrEmpty(LabelLine1) || !string.IsNullOrEmpty(LabelLine2))
        {
            double labelX = x + (ShowIndicator ? IndicatorWidth + IndicatorSpacing : 0);
            if (!string.IsNullOrEmpty(LabelLine1))
            {
                MusicDisplayDrawing.TextAt(dc, LabelLine1, labelX, 0.5, LabelSize, labelBrush);
            }
            if (!string.IsNullOrEmpty(LabelLine2))
            {
                MusicDisplayDrawing.TextAt(dc, LabelLine2, labelX, 9.5, LabelSize, labelBrush);
            }
        }

        // 7セグメントの数字を右端か左端にそろえ、1文字ずつ描く。
        double digitX = DigitsAlignment == HorizontalAlignment.Left
            ? 0
            : (ActualWidth - DigitsWidth);
        for (int i = 0; i < _text.Length; i++)
        {
            char c = _text[i];
            if (c == ':')
            {
                DrawColon(dc, digitX, h, t, ColonWidth, slant, litBrush);
                digitX += ColonWidth + DigitSpacing;
            }
            else if (c == '/')
            {
                DrawSlash(dc, digitX, w * 0.7, h, t, slant, litBrush);
                digitX += w * 0.7 + DigitSpacing;
            }
            else if (c == ' ')
            {
                digitX += w * 0.35 + DigitSpacing;
            }
            else
            {
                int mask = GetSegmentMask(c);
                DrawDigit(dc, digitX, w, h, t, slant, mask, litBrush, dimBrush);
                digitX += w + DigitSpacing;
            }
        }
        dc.Pop();
    }

    /// <summary>斜めの棒でスラッシュを描く。</summary>
    private static void DrawSlash(DrawingContext dc, double originX, double slashW, double h, double t, double slant, Brush litBrush)
    {
        double marginY = h * 0.12;
        double x1 = 0;
        double y1 = h - marginY;
        double x2 = slashW;
        double y2 = marginY;
        double dx = t * 0.7;
        DrawPolygon(dc, litBrush, originX, h, slant,
        [
            new Point(x1, y1),
            new Point(x1 + dx, y1),
            new Point(x2 + dx, y2),
            new Point(x2, y2)
        ]);
    }

    /// <summary>文字ごとの点灯セグメントのビット（bit0=A〜bit6=G）。対応しない文字は全消灯。</summary>
    private static int GetSegmentMask(char c) => c switch
    {
        '0' => 0x3F,
        '1' => 0x06,
        '2' => 0x5B,
        '3' => 0x4F,
        '4' => 0x66,
        '5' => 0x6D,
        '6' => 0x7D,
        '7' => 0x07,
        '8' => 0x7F,
        '9' => 0x6F,
        'A' or 'a' => 0x77,
        'B' or 'b' => 0x7C,
        'C' or 'c' => 0x39,
        'D' or 'd' => 0x5E,
        'E' or 'e' => 0x79,
        'F' or 'f' => 0x71,
        '-' => 0x40,
        _ => 0x00
    };

    /// <summary>上下2つの四角い点でコロンを描く。</summary>
    private static void DrawColon(DrawingContext dc, double originX, double h, double t, double colonW, double slant, Brush litBrush)
    {
        double dotSize = t * 1.05;
        double cx = (colonW - dotSize) / 2.0;
        foreach (double cy in new[] { h * 0.30 - dotSize / 2.0, h * 0.70 - dotSize / 2.0 })
        {
            DrawPolygon(dc, litBrush, originX, h, slant,
            [
                new Point(cx, cy),
                new Point(cx + dotSize, cy),
                new Point(cx + dotSize, cy + dotSize),
                new Point(cx, cy + dotSize)
            ]);
        }
    }

    /// <summary>1桁分の7つのセグメントを、点灯と消灯の色で描き分ける。</summary>
    private static void DrawDigit(DrawingContext dc, double originX, double w, double h, double t, double slant, int mask, Brush litBrush, Brush dimBrush)
    {
        double halfH = h / 2.0;
        double r = t / 2.0;
        const double gap = 0.6;

        // セグメントの並びは0:A（上）、1:B（右上）、2:C（右下）、3:D（下）、4:E（左下）、5:F（左上）、6:G（中央）。
        Point[][] segments =
        [
            HorizontalSegment(r + gap, w - r - gap, r, r),
            VerticalSegment(w - r, r + gap, halfH - gap, r),
            VerticalSegment(w - r, halfH + gap, h - r - gap, r),
            HorizontalSegment(r + gap, w - r - gap, h - r, r),
            VerticalSegment(r, halfH + gap, h - r - gap, r),
            VerticalSegment(r, r + gap, halfH - gap, r),
            HorizontalSegment(r + gap, w - r - gap, halfH, r)
        ];

        for (int s = 0; s < segments.Length; s++)
        {
            bool lit = (mask & (1 << s)) != 0;
            DrawPolygon(dc, lit ? litBrush : dimBrush, originX, h, slant, segments[s]);
        }
    }

    /// <summary>両端がとがった横向きの六角形のセグメント。</summary>
    private static Point[] HorizontalSegment(double x1, double x2, double yc, double r) =>
    [
        new Point(x1, yc),
        new Point(x1 + r, yc - r),
        new Point(x2 - r, yc - r),
        new Point(x2, yc),
        new Point(x2 - r, yc + r),
        new Point(x1 + r, yc + r)
    ];

    /// <summary>両端がとがった縦向きの六角形のセグメント。</summary>
    private static Point[] VerticalSegment(double xc, double y1, double y2, double r) =>
    [
        new Point(xc, y1),
        new Point(xc + r, y1 + r),
        new Point(xc + r, y2 - r),
        new Point(xc, y2),
        new Point(xc - r, y2 - r),
        new Point(xc - r, y1 + r)
    ];

    /// <summary>多角形を下端を基準に斜めに傾けて塗る。</summary>
    private static void DrawPolygon(DrawingContext dc, Brush brush, double originX, double h, double slant, ReadOnlySpan<Point> pts)
    {
        if (pts.Length < 3) return;
        Point Sheared(Point p) => new(originX + p.X + (h - p.Y) * slant, p.Y);
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(Sheared(pts[0]), isFilled: true, isClosed: true);
            for (int i = 1; i < pts.Length; i++)
            {
                ctx.LineTo(Sheared(pts[i]), isStroked: false, isSmoothJoin: false);
            }
        }
        geometry.Freeze();
        dc.DrawGeometry(brush, null, geometry);
    }
}
