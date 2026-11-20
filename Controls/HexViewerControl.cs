using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using MirrorFlux.Services;
namespace MirrorFlux.Controls;

/// <summary>テキストビューアのHEX表示。オフセット、16進バイト列、文字列を並べて描き、選択と検索結果の強調を行う。</summary>
public sealed class HexViewerControl : Grid
{
    /// <summary>HEX表示の描画面。描画とマウス操作を持ち主のコントロールへ渡す。</summary>
    private sealed class Surface(HexViewerControl owner) : FrameworkElement
    {
        protected override void OnRender(DrawingContext dc) { base.OnRender(dc); owner.Draw(dc, RenderSize); }
        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e) { Focus(); CaptureMouse(); owner.Pick(e.GetPosition(this), (Keyboard.Modifiers & ModifierKeys.Shift) != 0); e.Handled = true; }
        protected override void OnMouseMove(MouseEventArgs e) { if (IsMouseCaptured) owner.Pick(e.GetPosition(this), true); }
        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) { ReleaseMouseCapture(); e.Handled = true; }
        protected override void OnMouseWheel(MouseWheelEventArgs e) { owner.Scroll.Value -= Math.Sign(e.Delta) * 3; e.Handled = true; }
    }
    private readonly Surface _surface;
    private readonly ScrollBar Scroll;
    private readonly ScrollBar _horizontal;
    private byte[] _data = [];
    private int _anchor, _caret, _bytesPerRow = 16;
    private bool _ascii;
    private double _cell = 8, _row = 20;
    public bool BigEndian { get; set; }
    public IReadOnlyList<ViewerMatch> Matches { get; set; } = [];
    public int CurrentMatchIndex { get; set; } = -1;
    public int Caret => _caret;
    public int SelectionStart => Math.Min(_anchor, _caret);
    public int SelectionLength => _data.Length == 0 ? 0 : Math.Abs(_anchor - _caret) + 1;
    public int DataLength => _data.Length;
    public int FirstRow => (int)Scroll.Value;
    public int VisibleRows => Math.Max(1, (int)(_surface.ActualHeight / _row) - 1);
    public int RowCount => Math.Max(1, (_data.Length + _bytesPerRow - 1) / _bytesPerRow);
    public int BytesPerRow { get => _bytesPerRow; set { _bytesPerRow = value is 8 or 16 or 32 ? value : 16; UpdateScroll(); Jump(_caret); } }
    public event EventHandler? Changed;
    /// <summary>描画面と縦横のスクロールバー、コピー用の右クリックメニューを組み立てる。</summary>
    public HexViewerControl()
    {
        // 描画面を左上、縦スクロールバーを右、横スクロールバーを下に置く。
        ClipToBounds = true;
        RowDefinitions.Add(new RowDefinition()); RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition()); ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _surface = new Surface(this) { Focusable = true, Cursor = Cursors.IBeam, ClipToBounds = true }; Children.Add(_surface);
        Scroll = new ScrollBar { Orientation = Orientation.Vertical, Width = 6, MinWidth = 0, ClipToBounds = true, SmallChange = 1 };
        SetColumn(Scroll, 1); Children.Add(Scroll);
        _horizontal = new ScrollBar { Orientation = Orientation.Horizontal, Height = 6, MinHeight = 0, ClipToBounds = true, SmallChange = 24 };
        SetRow(_horizontal, 1); SetColumnSpan(_horizontal, 2); Children.Add(_horizontal);
        // スクロールやサイズ変更で描き直し、表示行が変わったことを知らせる。
        _horizontal.ValueChanged += (_, _) => _surface.InvalidateVisual();
        Scroll.ValueChanged += (_, _) => { _surface.InvalidateVisual(); Changed?.Invoke(this, EventArgs.Empty); };
        SizeChanged += (_, _) => UpdateScroll();
        // 16進バイト列と文字列のどちらでもコピーできる右クリックメニューを付ける。
        var menu = new ContextMenu();
        foreach (var (label, ascii) in new[] { ("16進バイト列をコピー", false), ("文字列をコピー", true) })
        { var item = new MenuItem { Header = label }; item.Click += (_, _) => Copy(ascii); menu.Items.Add(item); }
        _surface.ContextMenu = menu;
    }
    /// <summary>表示するバイト列を差し替え、カーソルと検索結果を初期化する。</summary>
    public void SetData(byte[] data) { _data = data; _caret = _anchor = 0; Matches = []; CurrentMatchIndex = -1; UpdateScroll(); }
    /// <summary>行数と表示行数に合わせて縦スクロールバーの範囲を設定する。</summary>
    private void UpdateScroll()
    {
        Scroll.Maximum = Math.Max(0, RowCount - VisibleRows); Scroll.ViewportSize = VisibleRows; Scroll.LargeChange = VisibleRows;
        _surface.InvalidateVisual();
    }
    /// <summary>カーソルを指定オフセットへ移し、その行が見えるようにスクロールする。extend が真なら選択を広げる。</summary>
    public void Jump(int offset, bool extend = false)
    {
        _caret = Math.Clamp(offset, 0, Math.Max(0, _data.Length - 1)); if (!extend) _anchor = _caret;
        // カーソル行が表示範囲の外なら、上端または下端に来るようにスクロールする。
        int row = _caret / _bytesPerRow;
        if (row < Scroll.Value) Scroll.Value = row;
        else if (row >= Scroll.Value + VisibleRows) Scroll.Value = row - VisibleRows + 1;
        _surface.InvalidateVisual(); Changed?.Invoke(this, EventArgs.Empty);
    }
    /// <summary>指定オフセットから指定長を選択する。長さは最低1バイトとする。</summary>
    public void SelectRange(int offset, int length) { Jump(offset); _anchor = _caret; _caret = Math.Min(Math.Max(0, _data.Length - 1), _caret + Math.Max(1, length) - 1); _surface.InvalidateVisual(); Changed?.Invoke(this, EventArgs.Empty); }
    /// <summary>ミニマップ上の位置（0〜1）に対応するバイトへ移動する。</summary>
    public void Navigate(double position) { Jump((int)(position * Math.Max(0, _data.Length - 1))); }
    /// <summary>ミニマップに描く表示範囲の先頭位置と高さを、ファイル全体に対する割合で返す。</summary>
    public (double, double) Viewport() => ((double)(FirstRow * _bytesPerRow) / Math.Max(1, _data.Length), (double)(VisibleRows * _bytesPerRow) / Math.Max(1, _data.Length));
    /// <summary>ミニマップ用に、指定行のバイト列を16進文字列で返す。</summary>
    public string MiniLine(int row)
    {
        int offset = row * _bytesPerRow; int size = Math.Min(_bytesPerRow, _data.Length - offset);
        return size <= 0 ? "" : Convert.ToHexString(_data.AsSpan(offset, size));
    }
    /// <summary>選択範囲を文字列（Latin1）または空白区切りの16進バイト列にする。</summary>
    public string CopyText(bool ascii) => SelectionLength == 0 ? "" : ascii
        ? Encoding.Latin1.GetString(_data, SelectionStart, SelectionLength)
        : BitConverter.ToString(_data, SelectionStart, SelectionLength).Replace('-', ' ');
    /// <summary>選択範囲をクリップボードへコピーする。クリップボードが使用中で失敗しても無視する。</summary>
    public void Copy(bool ascii) { try { Clipboard.SetText(CopyText(ascii)); } catch (System.Runtime.InteropServices.ExternalException) { } }
    /// <summary>選択位置の値を、設定したバイト順で8・16・32・64ビット整数として読んだ説明文を返す。</summary>
    public string Inspect()
    {
        if (_data.Length == 0) return "空のファイル";
        // 残りのバイト数で読める幅だけを並べる。
        int start = SelectionStart; var span = _data.AsSpan(start); string text = $"0x{start:X8}  {SelectionLength:N0} byte  UInt8:{span[0]} / Int8:{(sbyte)span[0]}";
        if (span.Length >= 2) text += $"  Int16:{(BigEndian ? BinaryPrimitives.ReadInt16BigEndian(span) : BinaryPrimitives.ReadInt16LittleEndian(span))}";
        if (span.Length >= 4) text += $"  Int32:{(BigEndian ? BinaryPrimitives.ReadInt32BigEndian(span) : BinaryPrimitives.ReadInt32LittleEndian(span))}";
        if (span.Length >= 8) text += $"  Int64:{(BigEndian ? BinaryPrimitives.ReadInt64BigEndian(span) : BinaryPrimitives.ReadInt64LittleEndian(span))}";
        return text + (BigEndian ? "  BE" : "  LE");
    }
    /// <summary>HEX表示のキー操作を処理する。処理したら真を返す。</summary>
    public bool HandleKey(Key key, ModifierKeys modifiers)
    {
        bool shift = (modifiers & ModifierKeys.Shift) != 0, ctrl = (modifiers & ModifierKeys.Control) != 0;
        // Ctrl+C は最後にクリックした側の形式でコピーし、Shift付きなら16進バイト列でコピーする。Ctrl+A は全選択。
        if (ctrl && key == Key.C) { Copy(shift ? false : _ascii); return true; }
        if (ctrl && key == Key.A) { _anchor = 0; _caret = Math.Max(0, _data.Length - 1); _surface.InvalidateVisual(); Changed?.Invoke(this, EventArgs.Empty); return true; }
        // カーソル移動キーから移動先を決め、Shift付きなら選択を広げる。
        int next = key switch {
            Key.Left => _caret - 1, Key.Right => _caret + 1, Key.Up => _caret - _bytesPerRow, Key.Down => _caret + _bytesPerRow,
            Key.PageUp => _caret - VisibleRows * _bytesPerRow, Key.PageDown => _caret + VisibleRows * _bytesPerRow,
            Key.Home => ctrl ? 0 : _caret / _bytesPerRow * _bytesPerRow,
            Key.End => ctrl ? _data.Length - 1 : _caret / _bytesPerRow * _bytesPerRow + _bytesPerRow - 1, _ => int.MinValue };
        if (next == int.MinValue) return false; Jump(next, shift); return true;
    }
    /// <summary>クリック位置からバイトを求めてカーソルを移す。16進側と文字列側のどちらを押したかも覚える。</summary>
    private void Pick(Point p, bool extend)
    {
        p.X += _horizontal.Value;
        int row = FirstRow + Math.Max(0, (int)(p.Y / _row) - 1);
        double asciiX = (11 + _bytesPerRow * 3 + 2) * _cell;
        _ascii = p.X >= asciiX;
        int column = _ascii ? (int)((p.X - asciiX) / _cell) : (int)((p.X - 11 * _cell) / (_cell * 3));
        Jump(row * _bytesPerRow + Math.Clamp(column, 0, _bytesPerRow - 1), extend);
    }
    /// <summary>見出し行と、表示範囲の各行のオフセット・16進バイト列・文字列を描く。</summary>
    private void Draw(DrawingContext dc, Size size)
    {
        // 背景を塗り、色とフォントを用意する。
        dc.DrawRectangle(ThemeBrushes.Get("ViewerBlack", this), null, new Rect(size));
        var foreground = ThemeBrushes.Get("PaneText", this);
        var muted = ThemeBrushes.Get("MutedText", this);
        var accent = ThemeBrushes.Get("HighlightText", this);
        double fontSize = Math.Clamp((double)GetValue(TextElement.FontSizeProperty), 10, 28);
        var typeface = new Typeface("Consolas"); double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        FormattedText Text(string value, Brush brush) => new(value, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, fontSize, brush, dpi);
        // 1文字の幅と行の高さを測り、横スクロールの範囲を決めて横位置をずらす。
        _cell = Text("0", foreground).WidthIncludingTrailingWhitespace; _row = Math.Ceiling(fontSize * 1.4);
        double asciiX = (11 + _bytesPerRow * 3 + 2) * _cell;
        _horizontal.Maximum = Math.Max(0, asciiX + (_bytesPerRow + 1) * _cell - size.Width);
        _horizontal.ViewportSize = size.Width;
        dc.PushTransform(new TranslateTransform(-_horizontal.Value, 0));
        // 見出し行を描く。
        string header = "OFFSET     " + string.Join(" ", Enumerable.Range(0, _bytesPerRow).Select(i => i.ToString("X2"))) + "   TEXT";
        dc.DrawText(Text(header, muted), new Point(0, 0));
        // 表示範囲の各行を描く。選択中と検索一致のバイトは16進側と文字列側の両方に背景を塗る。
        for (int row = 0; row < VisibleRows + 1; row++)
        {
            int offset = (FirstRow + row) * _bytesPerRow; if (offset >= _data.Length) break;
            double y = (row + 1) * _row;
            dc.DrawText(Text(offset.ToString("X8"), muted), new Point(0, y));
            for (int column = 0; column < _bytesPerRow && offset + column < _data.Length; column++)
            {
                int index = offset + column; double x = (11 + column * 3) * _cell; bool selected = index >= SelectionStart && index < SelectionStart + SelectionLength;
                int m = MatchAt(index); bool hit = m >= 0, current = hit && m == CurrentMatchIndex;
                if (selected || hit)
                {
                    var brush = ViewerPalette.Get(this, current ? "SearchCurrentBackground" : selected ? "MarkColor" : "SearchBackground");
                    dc.DrawRectangle(brush, null, new Rect(x, y, _cell * 2.5, _row));
                    dc.DrawRectangle(brush, null, new Rect(asciiX + column * _cell, y, _cell, _row));
                }
                Brush ink = ViewerPalette.Get(this, current ? "SearchCurrentForeground" : selected ? "MarkForeground" : hit ? "SearchForeground" : "MarkForeground");
                if (!selected && !hit) ink = foreground;
                dc.DrawText(Text(_data[index].ToString("X2"), ink), new Point(x, y));
                // 印字できないバイトは文字列側に「.」で表す。
                char character = _data[index] >= 32 && _data[index] <= 126 ? (char)_data[index] : '.';
                dc.DrawText(Text(character.ToString(), ink), new Point(asciiX + column * _cell, y));
            }
        }
        dc.Pop();
    }
    /// <summary>オフセットを含む検索結果の番号を二分探索で返す。なければ -1。</summary>
    private int MatchAt(int offset)
    {
        int low = 0, high = Matches.Count - 1;
        while (low <= high) { int mid = (low + high) / 2; var m = Matches[mid]; if (offset < m.Offset) high = mid - 1; else if (offset >= m.Offset + m.Length) low = mid + 1; else return mid; }
        return -1;
    }
    public void Redraw() => _surface.InvalidateVisual();
}
