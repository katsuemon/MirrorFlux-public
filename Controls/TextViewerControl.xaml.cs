using System.Globalization;
using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using MirrorFlux.Services;
using MirrorFlux.Input;
namespace MirrorFlux.Controls;

/// <summary>テキスト・HEX・Markdownを切り替えて表示するテキストビューア。検索、ミニマップ、末尾追従、ディスク更新の自動反映を持つ。</summary>
public partial class TextViewerControl : UserControl, IViewerControl
{
    private byte[] _bytes = [];
    private string _filePath = "", _encodingName = "UTF-8", _newLineName = "", _notice = "";
    private Encoding _encoding = Encoding.UTF8;
    private bool _hex, _markdown, _closed, _loading, _ready, _changingDocument;
    private bool? _forcedHex;
    private bool? _syntaxOverride;
    internal Action<ProcessStartInfo> AssociatedFileLauncher { get; set; } = request => Process.Start(request);
    private bool _follow, _showMap = true;
    private DateTime _writeTime;
    private int _bom, _generation;
    private TextDocument? _preparedDocument;
    private readonly DispatcherTimer _refreshTimer, _searchTimer;
    private Window? _hostWindow;
    private CancellationTokenSource _loadCancellation = new(), _searchCancellation = new();
    private readonly MatchRenderer _renderer;
    private readonly BoxCursorLayer _cursorLayer;
    private List<ViewerMatch> _matches = [];
    private int _matchIndex = -1;
    private int _searchSelectionStart = -1, _searchSelectionLength;
    private string _searchError = "";
    public string FilePath => _filePath;
    public bool IsHexMode => _hex;
    public string StatusText { get; private set; } = "";
    public string LocationText { get; private set; } = "";
    public bool IsPromptActive => SearchInput.IsKeyboardFocusWithin || JumpInput.IsKeyboardFocusWithin;
    public Task PendingLoad { get; private set; } = Task.CompletedTask;
    public Task PendingSearch { get; private set; } = Task.CompletedTask;
    public event EventHandler? StatusChanged;
    public event EventHandler? CloseRequested;
    public event Action<int>? FileNavigationRequested;

    /// <summary>エディターの描画部品・イベント・タイマーを組み立てる。</summary>
    public TextViewerControl()
    {
        InitializeComponent();
        // リンクの自動判定を切り、改行記号とリンクの表示部品を登録する。標準キャレットは透明にし、独自のボックスカーソルで描く。
        Editor.Options.EnableHyperlinks = false;
        Editor.TextArea.TextView.ElementGenerators.Add(new LineEndingElementGenerator());
        Editor.TextArea.TextView.SetResourceReference(TextView.NonPrintableCharacterBrushProperty, "InactiveBorder");
        Editor.TextArea.Caret.CaretBrush = Brushes.Transparent;
        Editor.TextArea.TextView.ElementGenerators.Add(new ViewerLinkElementGenerator(Editor));
        Editor.TextChanged += (_, _) => UpdateStatus();
        // 検索一致の背景・文字色と、ボックスカーソルの層を登録する。
        _renderer = new MatchRenderer(Editor);
        _cursorLayer = new BoxCursorLayer(this) { IsHitTestVisible = false };
        Editor.TextArea.TextView.InsertLayer(_cursorLayer, KnownLayer.Caret, LayerInsertionPosition.Above);
        Editor.TextArea.TextView.VisualLinesChanged += (_, _) => _cursorLayer.InvalidateVisual();
        Editor.TextArea.TextView.BackgroundRenderers.Add(_renderer);
        Editor.TextArea.TextView.LineTransformers.Add(new MatchColorizer(this));
        Editor.TextArea.SelectionChanged += (_, _) => UpdateSelectionColors();
        Editor.TextArea.SetResourceReference(ICSharpCode.AvalonEdit.Editing.TextArea.SelectionBrushProperty, "MarkColor");
        Editor.TextArea.SetResourceReference(ICSharpCode.AvalonEdit.Editing.TextArea.SelectionForegroundProperty, "MarkForeground");
        Editor.TextArea.SelectionCornerRadius = 0;
        // キャレット・スクロール・HEX表示の変化で、ステータスとミニマップを更新する。
        Editor.TextArea.Caret.PositionChanged += (_, _) => { _cursorLayer.RestartBlink(); UpdateStatus(); };
        Editor.TextArea.TextView.ScrollOffsetChanged += (_, _) => { _cursorLayer.InvalidateVisual(); Minimap.InvalidateVisual(); UpdateStatus(); };
        HexView.Changed += (_, _) => { Minimap.InvalidateVisual(); UpdateStatus(); };
        // ディスク更新の監視タイマーと、入力後に少し待って検索するタイマーを用意する。
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(750) };
        _refreshTimer.Tick += async (_, _) => await RefreshFromDiskAsync();
        _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); PendingSearch = SearchAsync(); };
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        _ready = true;
    }
    /// <summary>ホストウィンドウのアクティブ状態を追い、アクティブな間だけディスク更新を監視する。</summary>
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _hostWindow = Window.GetWindow(this);
        if (_hostWindow is not null)
        {
            _hostWindow.Activated -= HostWindow_Activated;
            _hostWindow.Deactivated -= HostWindow_Deactivated;
            _hostWindow.Activated += HostWindow_Activated;
            _hostWindow.Deactivated += HostWindow_Deactivated;
        }
        if (!_closed && (_hostWindow?.IsActive ?? true)) _refreshTimer.Start();
        FocusContent();
    }
    /// <summary>ウィンドウのイベントを外し、更新監視を止める。</summary>
    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _refreshTimer.Stop();
        if (_hostWindow is not null)
        {
            _hostWindow.Activated -= HostWindow_Activated;
            _hostWindow.Deactivated -= HostWindow_Deactivated;
            _hostWindow = null;
        }
    }
    private void HostWindow_Deactivated(object? sender, EventArgs e) => _refreshTimer.Stop();
    /// <summary>ウィンドウがアクティブに戻ったら監視を再開し、すぐに更新を確かめる。</summary>
    private void HostWindow_Activated(object? sender, EventArgs e)
    {
        if (_closed) return;
        _refreshTimer.Start();
        _ = RefreshFromDiskAsync();
    }
    public bool Load(string filePath) => Load(filePath, null);
    /// <summary>ファイルを開く。1MB以下は同期で、それより大きいものは非同期で読み込む。hexMode で表示形式を強制できる。</summary>
    public bool Load(string filePath, bool? hexMode)
    {
        if (!File.Exists(filePath)) return false;
        // 前のファイルの状態と読み込みを打ち切る。
        _closed = false; _cursorLayer.RestartBlink(); _loading = false; _preparedDocument = null; PendingLoad = Task.CompletedTask; _filePath = filePath; _forcedHex = hexMode;
        _loadCancellation.Cancel(); _loadCancellation.Dispose(); _loadCancellation = new();
        int generation = ++_generation;
        try
        {
            // メモリーに載らないサイズは開かず、サイズで同期か非同期かを選ぶ。
            long length = new FileInfo(filePath).Length;
            if (length > int.MaxValue - 1024) { _notice = "このファイルはメモリー上に読み込めるサイズを超えています。"; UpdateStatus(); return false; }
            if (length <= 1024 * 1024) ApplyBytes(ReadFile(filePath, _loadCancellation.Token), true, 0);
            else PendingLoad = ReadAsync(generation, true, 0);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OutOfMemoryException) { _notice = ex.Message; UpdateStatus(); return false; }
    }
    /// <summary>他のプロセスの書き込みや削除を妨げない共有モードで、ファイル全体を1MBずつ読む。</summary>
    private static byte[] ReadFile(string path, CancellationToken token)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > int.MaxValue - 1024) throw new IOException("ファイルが大きすぎます。");
        byte[] data = new byte[(int)stream.Length]; int position = 0;
        while (position < data.Length) { token.ThrowIfCancellationRequested(); int read = stream.Read(data, position, Math.Min(1024 * 1024, data.Length - position)); if (read == 0) break; position += read; }
        if (position != data.Length) Array.Resize(ref data, position);
        return data;
    }
    /// <summary>バックグラウンドでファイルを読み、文字コード判定とテキスト文書の作成まで済ませてから画面へ反映する。</summary>
    private async Task ReadAsync(int generation, bool initial, int byteOffset)
    {
        _loading = true; _notice = "読み込み中…"; UpdateStatus();
        var token = _loadCancellation.Token;
        try
        {
            // UIスレッドの状態を先に写し取ってから、別スレッドで読む。
            string path = _filePath; Encoding currentEncoding = _encoding; bool currentHex = _hex; bool? forced = _forcedHex;
            var prepared = await Task.Run(() =>
            {
                byte[] data = ReadFile(path, token);
                Encoding encoding = currentEncoding; bool hex = currentHex;
                if (initial) { var detected = CharsetDetector.Detect(data, Math.Min(data.Length, 65536)); encoding = detected.Encoding; hex = forced ?? detected.IsBinary; }
                TextDocument? document = null;
                // 文書は別スレッドで作るため所有スレッドを外しておき、画面へ渡すときに付け直す。
                if (!hex)
                {
                    int bom = PreambleLength(data);
                    token.ThrowIfCancellationRequested();
                    document = new TextDocument(encoding.GetString(data, bom, data.Length - bom));
                    document.SetOwnerThread(null);
                }
                token.ThrowIfCancellationRequested();
                return (Data: data, Document: document);
            }, token);
            // 閉じた後や、新しい読み込みが始まった後の結果は捨てる。
            if (_closed || generation != _generation) return;
            _preparedDocument = prepared.Document;
            ApplyBytes(prepared.Data, initial, byteOffset);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OutOfMemoryException) { if (!_closed) _notice = "読み込み失敗: " + ex.Message; }
        finally { if (generation == _generation) { _loading = false; UpdateStatus(); } }
    }
    /// <summary>読み込んだバイト列を反映する。初回だけ文字コード・改行・表示形式を判定する。</summary>
    private void ApplyBytes(byte[] data, bool initial, int byteOffset)
    {
        _searchCancellation.Cancel();
        _bytes = data;
        // 初回は文字コードを判定し、.md で2MB未満ならMarkdown表示にする。
        if (initial)
        {
            var detected = CharsetDetector.Detect(data, Math.Min(data.Length, 65536));
            _encoding = detected.Encoding; _encodingName = detected.EncodingName; _newLineName = detected.NewLineName;
            _hex = _forcedHex ?? detected.IsBinary;
            _markdown = Path.GetExtension(_filePath).Equals(".md", StringComparison.OrdinalIgnoreCase) && !_hex && data.Length < 2 * 1024 * 1024;
        }
        // BOM長と更新時刻を記録し、HEXとテキストの表示を作り直す。
        _bom = PreambleLength(data);
        _notice = ""; _writeTime = File.GetLastWriteTimeUtc(_filePath);
        HexView.SetData(data); RebuildText(byteOffset); ShowMode();
        if (_follow) GoEnd();
    }
    /// <summary>先頭のBOM（UTF-8、UTF-16LE/BE）のバイト数を返す。</summary>
    private static int PreambleLength(byte[] data) => data.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? 3 :
        data.AsSpan().StartsWith(new byte[] { 0xff, 0xfe }) || data.AsSpan().StartsWith(new byte[] { 0xfe, 0xff }) ? 2 : 0;
    /// <summary>現在の文字コードで本文を作り直し、指定バイト位置へキャレットを合わせる。検索結果も作り直す。</summary>
    private void RebuildText(int byteOffset)
    {
        _changingDocument = true;
        try
        {
            if (!_hex)
            {
                // 別スレッドで用意した文書があればそれを使い、なければここで文字列に変換する。
                if (_preparedDocument != null)
                {
                    _preparedDocument.SetOwnerThread(Thread.CurrentThread); Editor.Document = _preparedDocument; _preparedDocument = null;
                }
                else Editor.Text = _encoding.GetString(_bytes, Math.Min(_bom, _bytes.Length), Math.Max(0, _bytes.Length - _bom));
                Editor.CaretOffset = ByteToCharacter(byteOffset);
                Editor.ScrollToLine(Editor.TextArea.Caret.Line);
                ApplySyntax();
                if (_markdown) MarkdownViewer.Document = MarkdownFlowDocumentBuilder.Build(Editor.Text, FontFamily, FontSize);
            }
            else { Editor.Document = new TextDocument(); HexView.Jump(byteOffset); }
            // 古い検索結果を消す。
            _searchCancellation.Cancel(); _matches = []; _renderer.Matches = []; _renderer.CurrentIndex = -1; HexView.Matches = []; HexView.CurrentMatchIndex = -1;
        }
        finally { _changingDocument = false; }
        UpdateSelectionColors();
        // 検索語があれば検索し直す。
        if (SearchInput.Text.Length > 0) PendingSearch = SearchAsync();
    }
    /// <summary>バイト位置を文字位置へ変換する。</summary>
    private int ByteToCharacter(int offset)
    {
        offset = Math.Clamp(offset, _bom, Math.Max(_bom, _bytes.Length));
        // 指定バイト位置より前の、完全にデコードできる文字だけを数える。
        return Math.Clamp(_encoding.GetCharCount(_bytes, Math.Min(_bom, _bytes.Length), Math.Max(0, offset - _bom)), 0, Editor.Document.TextLength);
    }
    /// <summary>キャレット位置を、ファイル先頭からのバイト位置で返す。</summary>
    public int CurrentByteOffset => _hex ? HexView.Caret : TryGetCaretSnapshot(out var document, out _, out int offset)
        ? Math.Min(_bytes.Length, _bom + _encoding.GetByteCount(document.GetText(0, offset))) : 0;
    public void JumpToByte(int offset)
    {
        if (_hex) HexView.Jump(offset);
        else { Editor.CaretOffset = ByteToCharacter(offset); Editor.ScrollToLine(Editor.TextArea.Caret.Line); }
    }
    /// <summary>現在のバイト位置を保ったまま、テキスト表示とHEX表示を切り替える。</summary>
    public void ToggleHexMode()
    {
        int offset = CurrentByteOffset; _hex = !_hex; _markdown = false;
        RebuildText(offset); ShowMode(); FocusContent();
    }
    /// <summary>.md ファイルでMarkdown表示とテキスト表示を切り替える。</summary>
    public void ToggleMarkdownMode()
    {
        if (!Path.GetExtension(_filePath).Equals(".md", StringComparison.OrdinalIgnoreCase)) return;
        if (_hex) ToggleHexMode(); _markdown = !_markdown;
        if (_markdown) MarkdownViewer.Document = MarkdownFlowDocumentBuilder.Build(Editor.Text, FontFamily, FontSize);
        ShowMode();
    }
    /// <summary>表示形式に合わせて各表示部品とミニマップの接続先を切り替える。</summary>
    private void ShowMode()
    {
        // 表示形式に合わせて部品の表示とオプションの有効状態を切り替える。
        Editor.Visibility = !_hex && !_markdown ? Visibility.Visible : Visibility.Collapsed;
        HexView.Visibility = _hex ? Visibility.Visible : Visibility.Collapsed;
        MarkdownViewer.Visibility = _markdown ? Visibility.Visible : Visibility.Collapsed;
        Inspector.Visibility = ByteSearchToggle.Visibility = _hex ? Visibility.Visible : Visibility.Collapsed;
        RegexToggle.IsEnabled = CaseToggle.IsEnabled = !_hex;

        // ミニマップの行数・内容・表示範囲・クリック移動を、テキストかHEXにつなぐ。
        Minimap.Count = () => _hex ? HexView.RowCount : Editor.Document.LineCount;
        Minimap.ReadLine = index => _hex ? HexView.MiniLine(index) : MiniLine(index);
        Minimap.Viewport = () => _hex ? HexView.Viewport() : TextViewport();
        Minimap.Navigate = position => { if (_hex) HexView.Navigate(position); else { int line = Math.Clamp(1 + (int)(position * Editor.Document.LineCount), 1, Editor.Document.LineCount); Editor.CaretOffset = Editor.Document.GetLineByNumber(line).Offset; Editor.ScrollToLine(line); } };
        Minimap.RefreshContent(); UpdateStatus();
    }
    /// <summary>ミニマップ用に1行の先頭110文字を返す。</summary>
    private string MiniLine(int index)
    {
        var line = Editor.Document.GetLineByNumber(Math.Clamp(index + 1, 1, Editor.Document.LineCount));
        return Editor.Document.GetText(line.Offset, Math.Min(110, line.Length));
    }
    /// <summary>画面に見えている行の範囲を、文書全体に対する割合で返す。</summary>
    private (double, double) TextViewport()
    {
        var view = Editor.TextArea.TextView;
        if (!view.VisualLinesValid || view.VisualLines.Count == 0) return (0, 1);
        int first = view.VisualLines[0].FirstDocumentLine.LineNumber - 1;
        int last = view.VisualLines[^1].LastDocumentLine.LineNumber;
        return ((double)first / Editor.Document.LineCount, (double)(last - first) / Editor.Document.LineCount);
    }
    /// <summary>ファイルのサイズか更新時刻が変わっていれば、キャレット位置を保って読み直す。</summary>
    public async Task RefreshFromDiskAsync()
    {
        if (_closed || _loading || _filePath.Length == 0) return;
        try
        {
            var info = new FileInfo(_filePath);
            if (!info.Exists || (info.Length == _bytes.Length && info.LastWriteTimeUtc == _writeTime)) return;
            int offset = CurrentByteOffset;
            PendingLoad = ReadAsync(_generation, false, offset); await PendingLoad;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _notice = "更新待ち: " + ex.Message; UpdateStatus(); }
    }
    private void GoStart()
    {
        Editor.Select(0, 0); Editor.ScrollToHome();
    }
    /// <summary>末尾へ移動する。</summary>
    private void GoEnd()
    {
        if (_hex) HexView.Jump(Math.Max(0, _bytes.Length - 1));
        else { Editor.Select(Editor.Document.TextLength, 0); Editor.ScrollToEnd(); }
    }
    /// <summary>ビューアを閉じ、読み込み・検索・タイマーを止めて内容を解放する。</summary>
    public void Close()
    {
        _closed = true; _cursorLayer.RestartBlink(); ++_generation; _loadCancellation.Cancel(); _searchCancellation.Cancel(); _refreshTimer.Stop(); _searchTimer.Stop();
        _bytes = []; _matches = []; Editor.Document = new TextDocument(); HexView.SetData([]); MarkdownViewer.Document = null;
        _renderer.Matches = []; Minimap.Markers = [];
    }
    /// <summary>ステータス行と位置表示を作り直して通知する。</summary>
    private void UpdateStatus()
    {
        if (!_ready || _closed || _changingDocument) return;
        TextDocument document = Editor.Document; TextLocation location = default; int offset = 0;
        if (!_hex && !TryGetCaretSnapshot(out document, out location, out offset)) return;
        // 表示形式・サイズ・状態をステータスにまとめる。
        string mode = _hex ? "HEX" : _markdown ? "Markdown" : _encodingName + "/" + _newLineName;
        string syntaxState = !_hex && !_markdown ? (Editor.SyntaxHighlighting != null ? "  Syntax : ON" : "  Syntax : OFF") : "";
        StatusText = $"{Path.GetFileName(_filePath)}  [{mode}]  {_bytes.Length:N0} B" + syntaxState + (_follow ? "  末尾追従" : "") + (_notice.Length > 0 ? "  " + _notice : "");
        // 位置表示はTEXTなら行・桁・文字、HEXならアドレスにする。
        LocationText = _hex ? $"0x{HexView.Caret:X8} / 0x{_bytes.Length:X8}" : $"{($"Ln {location.Line}/{document.LineCount}"),-24}{($"Col {location.Column}"),-14}";
        if (!_hex && !_markdown) LocationText += "  " + DescribeCaretCharacter(document, offset);
        if (_hex) Inspector.Text = HexView.Inspect();
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }
    /// <summary>キャレットの位置を安全に取得する。文書の切り替え途中なら false を返す。</summary>
    private bool TryGetCaretSnapshot(out TextDocument document, out TextLocation location, out int offset)
    {
        document = Editor.Document; location = Editor.TextArea.Caret.Location; offset = 0;
        // 文書の差し替え時は、AvalonEditが古いキャレット行を直す前にスクロールイベントが来るため、文書と行番号の整合を確かめる。
        if (!_ready || _closed || _changingDocument || document == null ||
            !ReferenceEquals(document, Editor.TextArea.Document) || !ReferenceEquals(document, Editor.TextArea.TextView.Document) ||
            location.Line < 1 || location.Line > document.LineCount) return false;
        var line = document.GetLineByNumber(location.Line);
        offset = line.Offset + Math.Clamp(location.Column - 1, 0, line.Length);
        return true;
    }
    /// <summary>キャレット位置の文字を、表示名・コードポイント・現在の文字コードのバイト列で説明する。</summary>
    private string DescribeCaretCharacter(TextDocument document, int offset)
    {
        if (offset >= document.TextLength) return "文字:[EOF]";
        // サロゲートペアの後半にいるときは前半へ戻す。
        if (offset > 0 && char.IsLowSurrogate(document.GetCharAt(offset)) && char.IsHighSurrogate(document.GetCharAt(offset - 1))) offset--;
        // 文書全体を複製せず、現在のコードポイントだけを読む。
        string sample = document.GetText(offset, Math.Min(2, document.TextLength - offset));
        bool pair = sample == "\r\n";
        bool valid = Rune.TryGetRuneAt(sample, 0, out Rune rune);
        int point = valid ? rune.Value : sample[0];
        string value = pair ? sample : sample[..(valid ? rune.Utf16SequenceLength : 1)];
        // 空白・改行・制御文字は名前で、それ以外は文字そのもので示す。
        string label = pair ? "[CRLF]" : point switch {
            0x20 => "[SPACE]", 0x3000 => "[全角SPACE]", 0x09 => "[TAB]", 0x0D => "[CR]", 0x0A => "[LF]",
            _ => valid && Rune.GetUnicodeCategory(rune) is not (UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
                ? "「" + value + "」" : "[制御文字]"
        };
        // コードポイントと、現在の文字コードでのバイト列を並べる。
        string unicode = pair ? "U+000D U+000A" : $"U+{point:X4}";
        string bytes;
        try { bytes = BitConverter.ToString(_encoding.GetBytes(value)).Replace('-', ' '); }
        catch (EncoderFallbackException) { bytes = "変換不可"; }
        return $"文字:{label} {unicode} {_encoding.WebName.ToUpperInvariant()}:{bytes}";
    }
    private void FocusContent() { if (_hex) HexView.Focus(); else if (_markdown) MarkdownViewer.Focus(); else Editor.Focus(); }
    /// <summary>検索バーを開く。Markdown表示中はテキスト表示に戻す。</summary>
    private void ShowSearch()
    {
        if (_markdown) { _markdown = false; ShowMode(); }
        SearchBar.Visibility = Visibility.Visible; SearchInput.Focus(); SearchInput.SelectAll();
    }
    /// <summary>検索語とオプションを設定して、すぐに検索する。</summary>
    public Task SearchForAsync(string query, bool regex = false, bool matchCase = false, bool bytes = false)
    {
        SearchInput.Text = query; RegexToggle.IsChecked = regex; CaseToggle.IsChecked = matchCase; ByteSearchToggle.IsChecked = bytes;
        _searchTimer.Stop(); return PendingSearch = SearchAsync();
    }
    public int MatchCount => _matches.Count;
    public string SearchError => _searchError;
    /// <summary>別スレッドで検索し、一致の強調表示とミニマップの印を更新する。</summary>
    private async Task SearchAsync()
    {
        // 前の検索を止め、検索条件と対象を写し取る。
        _searchCancellation.Cancel(); _searchCancellation.Dispose(); _searchCancellation = new();
        var token = _searchCancellation.Token; string query = SearchInput.Text;
        bool hex = _hex, byteSearch = hex && ByteSearchToggle.IsChecked == true, regex = RegexToggle.IsChecked == true, matchCase = CaseToggle.IsChecked == true;
        var snapshot = Editor.Document.CreateSnapshot();
        byte[] data = _bytes;
        _searchError = ""; SearchInfo.Text = query.Length == 0 ? "" : "検索中…";
        try
        {
            // バイト列検索・テキスト検索・HEXでの文字列検索を選ぶ。
            var matches = await Task.Run(() =>
            {
                if (byteSearch) return ViewerSearch.Bytes(data, ViewerSearch.ParseBytes(query), token);
                if (!hex) return ViewerSearch.Text(snapshot.Text, query, regex, matchCase, token);
                // HEXでの文字列検索は、選択中の文字コードでバイト列にし、バイト単位の位置で探す。
                return ViewerSearch.Bytes(data, _encoding.GetBytes(query).Select(b => (byte?)b).ToArray(), token);
            }, token);
            if (token.IsCancellationRequested || _closed) return;
            // 結果を各表示部品へ渡す。
            _matches = matches; _matchIndex = -1; _renderer.CurrentIndex = -1; HexView.CurrentMatchIndex = -1; _renderer.Matches = hex ? [] : matches; HexView.Matches = hex ? matches : []; UpdateSelectionColors();
            // ミニマップの印は表示上の1行に1つまでにする。検索結果自体はすべて残る。
            var marks = new List<double>();
            var occupied = new HashSet<int>();
            int markerStep = Math.Max(1, matches.Count / Math.Max(1, (int)Minimap.ActualHeight));
            for (int marker = 0; marker < matches.Count; marker += markerStep)
            {
                var match = matches[marker];
                double fraction = hex ? (double)match.Offset / Math.Max(1, data.Length) : (double)(Editor.Document.GetLineByOffset(match.Offset).LineNumber - 1) / Editor.Document.LineCount;
                if (occupied.Add((int)(fraction * Math.Max(1, Minimap.ActualHeight)))) marks.Add(fraction);
            }
            Minimap.Markers = marks; Minimap.InvalidateVisual(); Editor.TextArea.TextView.Redraw(); HexView.Redraw();
            SearchInfo.Text = $"{matches.Count:N0}件";
        }
        catch (OperationCanceledException) { }
        // 検索式の誤りや時間切れは件数欄に表示し、結果を消す。
        catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException or FormatException)
        { if (!token.IsCancellationRequested) { _searchError = ex is RegexMatchTimeoutException ? "検索に時間がかかりすぎました" : "検索式を確認してください"; SearchInfo.Text = _searchError; _matches = []; _renderer.Matches = []; HexView.Matches = []; Minimap.Markers = []; HexView.CurrentMatchIndex = -1; _renderer.CurrentIndex = -1; UpdateSelectionColors(); HexView.Redraw(); Minimap.InvalidateVisual(); Editor.TextArea.TextView.Redraw(); } }
    }
    /// <summary>次または前の一致へ移動して選択する。最初の移動はキャレット位置から探す。</summary>
    public void NextMatch(bool forward)
    {
        if (SearchInput.Text.Length == 0) { ShowSearch(); return; }
        if (_matches.Count == 0) return;
        // 最初はキャレットから探し、以降は順に巡回する。
        int current = _hex ? HexView.Caret : Editor.CaretOffset;
        if (_matchIndex < 0) { _matchIndex = forward ? _matches.FindIndex(m => m.Offset >= current) : _matches.FindLastIndex(m => m.Offset <= current); if (_matchIndex < 0) _matchIndex = forward ? 0 : _matches.Count - 1; }
        else _matchIndex = (_matchIndex + (forward ? 1 : -1) + _matches.Count) % _matches.Count;
        // 一致を選択して画面に出す。
        var match = _matches[_matchIndex];
        _renderer.CurrentIndex = HexView.CurrentMatchIndex = _matchIndex;
        if (_hex) HexView.SelectRange(match.Offset, match.Length);
        else
        {
            Editor.Select(match.Offset, match.Length);
            // AvalonEditはCRLFやサロゲートペアの前後で選択範囲を補正するので、補正後の範囲を現在の一致として覚える。
            _searchSelectionStart = Editor.SelectionStart; _searchSelectionLength = Editor.SelectionLength;
            Editor.ScrollToLine(Editor.Document.GetLineByOffset(match.Offset).LineNumber);
        }
        UpdateSelectionColors(); Editor.TextArea.TextView.Redraw(); HexView.Redraw();
        SearchInfo.Text = $"{_matchIndex + 1:N0}/{_matches.Count:N0}件";
    }
    private void Search_TextChanged(object sender, TextChangedEventArgs e) { if (!_ready) return; _searchTimer.Stop(); _searchTimer.Start(); }
    private void SearchOptions_Click(object sender, RoutedEventArgs e) { ShowMode(); PendingSearch = SearchAsync(); }
    /// <summary>検索欄でEscなら閉じ、Enterなら検索の完了を待って次の一致へ（Shiftで前へ）移動する。</summary>
    private async void SearchInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { SearchBar.Visibility = Visibility.Collapsed; FocusContent(); e.Handled = true; }
        else if (e.Key == Key.Enter) { if (_searchTimer.IsEnabled) { _searchTimer.Stop(); PendingSearch = SearchAsync(); } await PendingSearch; NextMatch((Keyboard.Modifiers & ModifierKeys.Shift) == 0); e.Handled = true; }
    }
    /// <summary>行番号またはアドレスの移動欄を開く。</summary>
    private void ShowJump() { JumpLabel.Text = _hex ? "アドレス (HEX / d:十進):" : "行番号:"; JumpInput.Text = ""; JumpOverlay.Visibility = Visibility.Visible; JumpInput.Focus(); }
    /// <summary>移動欄の入力を解釈して移動する。HEXは十六進アドレス（d: で十進）。</summary>
    private void JumpInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { JumpOverlay.Visibility = Visibility.Collapsed; FocusContent(); e.Handled = true; }
        if (e.Key != Key.Enter) return;
        try
        {
            if (_hex) HexView.Jump(ViewerSearch.Address(JumpInput.Text, _bytes.Length));
            else { int line = Math.Clamp(int.Parse(JumpInput.Text, CultureInfo.InvariantCulture), 1, Editor.Document.LineCount); Editor.CaretOffset = Editor.Document.GetLineByNumber(line).Offset; Editor.ScrollToLine(line); }
            JumpOverlay.Visibility = Visibility.Collapsed; FocusContent();
        }
        catch (Exception ex) when (ex is FormatException or OverflowException) { JumpLabel.Text = "入力を確認してください:"; }
        e.Handled = true;
    }
    /// <summary>選択中の文字列を、選択がなければキャレット行を返す。</summary>
    public string SelectedTextOrLine() => Editor.SelectionLength > 0 ? Editor.SelectedText : Editor.Document.GetText(Editor.Document.GetLineByNumber(Editor.TextArea.Caret.Line));
    /// <summary>HEXは選択列の形式で、TEXTは選択か現在行をクリップボードへコピーする。</summary>
    private void Copy()
    {
        if (_hex) { HexView.Copy(false); return; }
        try { Clipboard.SetText(SelectedTextOrLine()); } catch (System.Runtime.InteropServices.ExternalException) { }
    }
    /// <summary>キャレット位置を保ったまま折り返しを切り替える。</summary>
    private void ToggleWrap() { if (_hex) return; int position = Editor.CaretOffset; Editor.WordWrap = !Editor.WordWrap; Editor.CaretOffset = position; Editor.ScrollToLine(Editor.TextArea.Caret.Line); }
    public void SetFollow(bool follow) { _follow = follow; if (_follow) GoEnd(); UpdateStatus(); }
    private void ToggleMap() { _showMap = !_showMap; Minimap.Visibility = _showMap ? Visibility.Visible : Visibility.Collapsed; }
    /// <summary>Shift+ファンクションキーの操作を実行する。</summary>
    public void ExecuteShiftFunctionKey(int number)
    {
        switch (number)
        {
            case 2: SetFollow(!_follow); break;
            case 3: if (!_hex) { RegexToggle.IsChecked = !RegexToggle.IsChecked; ShowSearch(); PendingSearch = SearchAsync(); } break;
            case 4: if (!_hex) { CaseToggle.IsChecked = !CaseToggle.IsChecked; ShowSearch(); PendingSearch = SearchAsync(); } break;
            case 5: if (_hex) { ByteSearchToggle.IsChecked = !ByteSearchToggle.IsChecked; ShowSearch(); PendingSearch = SearchAsync(); } else FileNavigationRequested?.Invoke(-1); break;
            case 6: if (_hex) HexView.BytesPerRow = HexView.BytesPerRow == 8 ? 16 : HexView.BytesPerRow == 16 ? 32 : 8; else FileNavigationRequested?.Invoke(1); break;
            case 7: if (_hex) { HexView.BigEndian = !HexView.BigEndian; UpdateStatus(); } break;
            case 8: Copy(); break;
            case 9: if (_hex) HexView.Copy(true); break;
            case 10: if (!_hex) { _syntaxOverride = Editor.SyntaxHighlighting == null; ApplySyntax(); UpdateStatus(); } break;
        }
        Minimap.RefreshContent();
    }
    /// <summary>設定とShift+F10の切り替えに従って、構文の色分けを適用する。</summary>
    private void ApplySyntax()
    {
        var settings = new SettingsStore().Load().Settings;
        if (_syntaxOverride is bool enabled) settings.TVHighlight = enabled;
        ViewerSyntaxService.Apply(Editor, _filePath, settings);
    }
    /// <summary>Windowsの関連付けでファイルを開く。</summary>
    internal void OpenWithWindowsAssociation()
    {
        if (_closed || _filePath.Length == 0) return;
        // 外部エディターは保存時に排他アクセスを求めることがあるため、更新監視の読み込みと競合しないように止める。監視はMirrorFluxが再びアクティブになったときに再開する。
        _refreshTimer.Stop();
        try
        {
            AssociatedFileLauncher(new ProcessStartInfo(_filePath) { UseShellExecute = true });
            _notice = "";
        }
        // 開けなかったときはメッセージを出し、監視を戻す。
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            _notice = "関連付けで開けませんでした: " + ex.Message;
            if (_hostWindow?.IsActive ?? IsLoaded) _refreshTimer.Start();
        }
        UpdateStatus();
    }
    /// <summary>文字コードを UTF-8、Shift_JIS、EUC-JP、UTF-16LE、UTF-16BE、ASCII の順に切り替えてテキスト表示する。</summary>
    private void CycleEncoding()
    {
        int position = CurrentByteOffset;
        Encoding[] encodings = [Encoding.UTF8, CharsetDetector.ShiftJis, CharsetDetector.EucJp, Encoding.Unicode, Encoding.BigEndianUnicode, Encoding.ASCII];
        int index = Array.FindIndex(encodings, e => e.CodePage == _encoding.CodePage);
        _encoding = encodings[(index + 1) % encodings.Length]; _encodingName = _encoding.WebName; _hex = false; _markdown = false;
        RebuildText(position); ShowMode();
    }
    public bool HandleKeyDown(KeyEventArgs e) => HandleKeyDown(e, Keyboard.Modifiers);
    /// <summary>ビューアのキー操作を処理する。処理したら true を返す。</summary>
    internal bool HandleKeyDown(KeyEventArgs e, ModifierKeys modifiers)
    {
        Key key = e.NormalizedKey();
        // Shift+ファンクションキーは専用の操作へ回す。
        if (modifiers == ModifierKeys.Shift && key >= Key.F1 && key <= Key.F12) { ExecuteShiftFunctionKey(key - Key.F1 + 1); return true; }
        // 入力欄にいる間は F3/F4 だけを処理する。
        if (IsPromptActive) { if (key == Key.F3 || key == Key.F4) { NextMatch(key == Key.F3); return true; } return false; }
        if (_hex && HexView.HandleKey(key, modifiers)) return true;
        // Ctrl系は一部だけ処理し、それ以外はエディター標準の選択・コピー・全選択・単語移動に任せる。
        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            if (key == Key.F) { ShowSearch(); return true; }
            if (key == Key.G) { ShowJump(); return true; }
            if (key == Key.C && !_markdown && Editor.SelectionLength == 0) { Copy(); return true; }
            if (key == Key.End && _follow) { GoEnd(); return true; }
            return false;
        }
        if (modifiers != ModifierKeys.None) return false;
        // 修飾キーなしの単キー操作を処理する。
        switch (key)
        {
            case Key.Escape: case Key.Return: case Key.Q: case Key.V: CloseRequested?.Invoke(this, EventArgs.Empty); return true;
            case Key.Home when !_markdown: GoStart(); return true;
            case Key.End when !_markdown: GoEnd(); return true;
            case Key.OemQuestion: ShowSearch(); return true;
            case Key.G: case Key.J: ShowJump(); return true;
            case Key.E: OpenWithWindowsAssociation(); return true;
            case Key.H: ToggleHexMode(); return true;
            case Key.C: CycleEncoding(); return true;
            case Key.W: ToggleWrap(); return true;
            case Key.N: Editor.ShowLineNumbers = !Editor.ShowLineNumbers; return true;
            case Key.M: ToggleMarkdownMode(); return true;
            case Key.T: SetFollow(!_follow); return true;
        }
        return false;
    }
    /// <summary>ファンクションキーの操作を実行する。F1はTEXTとHEXで別のヘルプを出す。</summary>
    public void ExecuteFunctionKey(int number)
    {
        switch (number)
        {
            case 1 when _hex:
                MessageBox.Show("F2 / Ctrl+F / /: 検索\nF3 / F4: 次 / 前の一致\n" +
                    "F5 / C: 文字コードを切替えてTEXT表示\nF6 / H: TEXT表示\n" +
                    "F7 / Ctrl+G / G: アドレス移動（HEX / d:十進）\nF9: 再読込\nF11: ミニマップ表示切替\n" +
                    "Shift+移動・ドラッグ: 範囲選択\nCtrl+C: 選択列の形式でコピー  Ctrl+A: 全選択\n" +
                    "Shift+F2 / T: 末尾追従\nShift+F5: バイト列検索（?? は任意の1byte）\n" +
                    "Shift+F6: 行幅（8 / 16 / 32byte）\nShift+F7: Endian切替\n" +
                    "Shift+F8 / Ctrl+Shift+C: バイト列コピー\nShift+F9: 文字列コピー\n" +
                    "E: Windows関連付けで開く\nEsc / Q / V / F12: 閉じる", "HEXビューア操作");
                break;
            case 1: MessageBox.Show("F2 / Ctrl+F / /: 検索（正規表現・件数）\nF3 / F4: 次 / 前の一致\nCtrl+G / G: 行 / アドレス移動\nShift+移動・ドラッグ: 範囲選択\nCtrl+C: コピー  Ctrl+A: 全選択\nHome / End: 文書先頭 / 末尾\nF10 / W: 折り返し（初期ON）  N: 行番号\nC / F5: 文字コード  H / F6: TEXT/HEX\nT: 末尾追従  F9: 再読込\nF11: ミニマップ表示切替（クリック・ドラッグで移動）\nHEX検索: バイト列の ?? は任意の1byte\nHEX Ctrl+Shift+C: バイト列コピー\nShift+F2: 末尾追従\nShift+F3/F4: 正規表現 / 大文字小文字\nShift+F5/F6: 前 / 次ファイル（TEXT）、バイト検索 / 行幅（HEX）\nShift+F7: HEXエンディアン\nShift+F8/F9: コピー / HEX文字列コピー\nShift+F10: 色あり/色なし（再読込後も保持）\nE: Windows関連付けで開く\nM: Markdown表示切替\nEsc / Q / V / F12: 閉じる", "ビューア操作"); break;
            case 2: ShowSearch(); break;
            case 3: NextMatch(true); break;
            case 4: NextMatch(false); break;
            case 5: CycleEncoding(); break;
            case 6: ToggleHexMode(); break;
            case 7: ShowJump(); break;
            case 8: if (!_hex) Editor.ShowLineNumbers = !Editor.ShowLineNumbers; break;
            case 9: if (!_loading) PendingLoad = ReadAsync(_generation, false, CurrentByteOffset); break;
            case 10: ToggleWrap(); break;
            case 11: ToggleMap(); break;
            case 12: CloseRequested?.Invoke(this, EventArgs.Empty); break;
        }
    }
    /// <summary>選択範囲が現在の検索一致と同じなら、現在一致の色で選択を表示する。</summary>
    private void UpdateSelectionColors()
    {
        if (!TryGetCaretSnapshot(out _, out _, out _)) return;
        bool current = !_hex && _matchIndex >= 0 && _matchIndex < _matches.Count &&
            Editor.SelectionStart == _searchSelectionStart && Editor.SelectionLength == _searchSelectionLength;
        Editor.TextArea.SelectionBrush = ViewerPalette.Get(Editor, current ? "SearchCurrentBackground" : "MarkColor");
        Editor.TextArea.SelectionForeground = ViewerPalette.Get(Editor, current ? "SearchCurrentForeground" : "MarkForeground");
    }
    /// <summary>検索一致の文字色を行ごとに付ける。</summary>
    private sealed class MatchColorizer(TextViewerControl owner) : DocumentColorizingTransformer
    {
        protected override void ColorizeLine(DocumentLine line)
        {
            // 二分探索で、この行にかかる最初の一致を探す。
            var matches = owner._matches; int low = 0, high = matches.Count;
            while (low < high) { int mid = (low + high) / 2; if (matches[mid].Offset + matches[mid].Length <= line.Offset) low = mid + 1; else high = mid; }
            // 行内に収まる部分だけ色を変える。現在の一致は別の色にする。
            for (int i = low; i < matches.Count && matches[i].Offset < line.EndOffset; i++)
            {
                var match = matches[i];
                int start = Math.Max(line.Offset, match.Offset), end = Math.Min(line.EndOffset, match.Offset + match.Length);
                if (end > start) ChangeLinePart(start, end, element => element.TextRunProperties.SetForegroundBrush(
                    ViewerPalette.Get(owner.Editor, i == owner._matchIndex ? "SearchCurrentForeground" : "SearchForeground")));
            }
        }
    }
    /// <summary>Windowsのキャレット点滅間隔を取得する。</summary>
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetCaretBlinkTime();

    /// <summary>キャレットの点滅間隔。点滅しない設定では null。</summary>
    internal static TimeSpan? CursorBlinkInterval
    {
        get
        {
            uint milliseconds = GetCaretBlinkTime();
            return milliseconds is 0 or uint.MaxValue ? null : TimeSpan.FromMilliseconds(milliseconds);
        }
    }

    /// <summary>キャレット位置の1文字分を反転表示する、点滅するボックスカーソルの層。</summary>
    private sealed class BoxCursorLayer : FrameworkElement
    {

        private readonly TextViewerControl _owner;
        private readonly DispatcherTimer _timer = new();
        private bool _lit = true;

        public BoxCursorLayer(TextViewerControl owner)
        {
            _owner = owner;
            _timer.Tick += (_, _) => { _lit = !_lit; InvalidateVisual(); };
            Loaded += (_, _) => RestartBlink();
            IsVisibleChanged += (_, _) => RestartBlink();
            Unloaded += (_, _) => _timer.Stop();
        }

        /// <summary>点灯状態から点滅をやり直す。点滅しない設定では点灯したままにする。</summary>
        public void RestartBlink()
        {
            _timer.Stop();
            _lit = true;
            InvalidateVisual();
            if (!IsLoaded || !IsVisible || _owner._closed) return;
            if (CursorBlinkInterval is not { } interval) return;
            _timer.Interval = interval;
            _timer.Start();
        }

        /// <summary>キャレットの文字セルを文字色で塗り、同じ行の文字形をマスクにして背景色で文字を抜く。</summary>
        protected override void OnRender(DrawingContext dc)
        {
            if (!_lit || _owner._closed) return;
            // キャレットのある表示行を求める。
            var editor = _owner.Editor;
            var view = editor.TextArea.TextView;
            if (!view.VisualLinesValid) return;
            var caret = editor.TextArea.Caret;
            var line = view.GetVisualLine(caret.Line);
            if (line == null) return;

            // 改行記号のラベルは幅0なので、その後ろ側をクリックしても改行位置を指す。
            int column = caret.Offset == line.LastDocumentLine.EndOffset
                ? line.GetVisualColumn(caret.Offset - line.StartOffset) : caret.VisualColumn;
            var row = line.GetTextLine(column);
            double x = line.GetTextLineVisualXPosition(row, column);
            // 文字要素があれば、その文字の位置と幅でセルを決める。
            double width = view.WideSpaceWidth;
            var element = line.Elements.FirstOrDefault(e => e.VisualColumn <= column && column < e.VisualColumn + e.VisualLength);
            if (element != null)
            {
                int next = line.GetNextCaretPosition(column, System.Windows.Documents.LogicalDirection.Forward,
                    CaretPositioningMode.Normal, false);
                var bounds = row.GetTextBounds(column, Math.Max(1, next - column))[0].Rectangle;
                x = bounds.X;
                if (bounds.Width > 0) width = bounds.Width;
            }

            // 物理ピクセルに合わせてセルの矩形を作る。
            double top = line.GetTextLineVisualYPosition(row, VisualYPosition.TextTop) - view.VerticalOffset;
            double bottom = line.GetTextLineVisualYPosition(row, VisualYPosition.TextBottom) - view.VerticalOffset;
            x -= view.HorizontalOffset;
            double scale = VisualTreeHelper.GetDpi(view).DpiScaleX;
            double Snap(double coordinate) => Math.Round(coordinate * scale) / scale;
            var box = new Rect(Snap(x), Snap(top), Math.Max(1 / scale, Snap(x + width) - Snap(x)), Snap(bottom) - Snap(top));

            // 文字色と背景色を決める。選択中や検索一致の上では、その配色を反転する。
            Brush ink = element is LineEndingElementGenerator.LineEndingCell ? view.NonPrintableCharacterBrush :
                element?.TextRunProperties.ForegroundBrush ?? editor.Foreground;
            Brush paper = element?.BackgroundBrush ?? editor.Background;
            if (caret.Offset >= editor.SelectionStart && caret.Offset < editor.SelectionStart + editor.SelectionLength)
            {
                ink = editor.TextArea.SelectionForeground;
                paper = editor.TextArea.SelectionBrush;
            }
            else if (ReferenceEquals(ink, ViewerPalette.Get(editor, "SearchCurrentForeground")))
                paper = ViewerPalette.Get(editor, "SearchCurrentBackground");
            else if (ReferenceEquals(ink, ViewerPalette.Get(editor, "SearchForeground")))
                paper = ViewerPalette.Get(editor, "SearchBackground");

            // 表示行そのものを文字形のマスクにして、反転セルの字形・位置・改行記号を下の文字と完全に一致させる。
            dc.DrawRectangle(ink, null, box);
            if (element == null) return;
            var glyphs = new DrawingGroup();
            using (var glyphContext = glyphs.Open())
            {
                double rowTop = line.GetTextLineVisualYPosition(row, VisualYPosition.LineTop) - view.VerticalOffset;
                row.Draw(glyphContext, new Point(-view.HorizontalOffset, rowTop), System.Windows.Media.TextFormatting.InvertAxes.None);
            }
            var mask = new DrawingBrush(glyphs) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = box };
            dc.PushOpacityMask(mask);
            dc.DrawRectangle(paper, null, box);
            dc.Pop();
        }
    }
    /// <summary>検索一致の背景を、表示範囲の分だけ塗る。</summary>
    private sealed class MatchRenderer(ICSharpCode.AvalonEdit.TextEditor editor) : IBackgroundRenderer
    {
        public IReadOnlyList<ViewerMatch> Matches { get; set; } = [];
        public int CurrentIndex { get; set; } = -1;
        public KnownLayer Layer => KnownLayer.Background;
        public void Draw(TextView view, DrawingContext dc)
        {
            if (!view.VisualLinesValid || view.VisualLines.Count == 0 || Matches.Count == 0) return;
            int start = view.VisualLines[0].FirstDocumentLine.Offset, end = view.VisualLines[^1].LastDocumentLine.EndOffset;
            // 二分探索で、表示範囲にかかる最初の一致から塗る。
            int low = 0, high = Matches.Count;
            while (low < high) { int mid = (low + high) / 2; if (Matches[mid].Offset + Matches[mid].Length < start) low = mid + 1; else high = mid; }
            for (int i = low; i < Matches.Count && Matches[i].Offset <= end; i++)
            {
                var m = Matches[i];
                if (m.Offset + m.Length > editor.Document.TextLength) continue;
                foreach (Rect rect in BackgroundGeometryBuilder.GetRectsForSegment(view, new TextSegment { StartOffset = m.Offset, Length = m.Length }))
                    dc.DrawRectangle(ViewerPalette.Get(editor, i == CurrentIndex ? "SearchCurrentBackground" : "SearchBackground"), null, rect);
            }
        }
    }
}
