using MirrorFlux.Models;

namespace MirrorFlux.Services;

/// <summary>
/// AppSettings に専用の項目を持つテーマ色。INI での名前、用途の説明、値の読み書きの方法。
/// </summary>
internal sealed record CoreThemeSetting(
    string Key, string Purpose, Func<AppSettings, string> Get, Action<AppSettings, string> Set);

/// <summary>
/// テーマ色の名前の一覧。INI に書く順番と、各色の用途の説明を持つ。
/// </summary>
internal static class ThemeKeyCatalog
{
    public static IReadOnlyList<CoreThemeSetting> Core { get; } =
    [
        new("ScreenBackground", "アプリ全体の画面背景", s => s.ColScreen, (s, value) => s.ColScreen = value),
        new("PanelBackground", "ファイルパネルの背景", s => s.ColPanel, (s, value) => s.ColPanel = value),
        new("PrimaryText", "主要な文字", s => s.ColText, (s, value) => s.ColText = value),
        new("PaneText", "ファイル一覧の文字", s => s.ColPaneText, (s, value) => s.ColPaneText = value),
        new("MutedText", "補助・非強調文字", s => s.ColMuted, (s, value) => s.ColMuted = value),
        new("AccentText", "アクセント文字", s => s.ColAccent, (s, value) => s.ColAccent = value),
        new("HighlightText", "強調表示と操作カーソル", s => s.ColHighlight, (s, value) => s.ColHighlight = value),
        new("DarkPurpleBackground", "ヘッダーの濃紫背景", s => s.ColDarkPurple, (s, value) => s.ColDarkPurple = value),
        new("DarkPurpleHighlight", "濃紫パネルの明部", s => s.ColDarkPurpleHighlight, (s, value) => s.ColDarkPurpleHighlight = value),
        new("DarkPurpleShadow", "濃紫パネルの暗部", s => s.ColDarkPurpleShadow, (s, value) => s.ColDarkPurpleShadow = value),
        new("InactiveBorder", "非アクティブ項目と境界線", s => s.ColInactiveBorder, (s, value) => s.ColInactiveBorder = value),
        new("MarkColor", "マーク項目の背景", s => s.ColMark, (s, value) => s.ColMark = value),
        new("SearchBackground", "検索一致の背景", s => s.ColSearchBg, (s, value) => s.ColSearchBg = value),
        new("SearchForeground", "検索一致の文字", s => s.ColSearchFg, (s, value) => s.ColSearchFg = value),
        new("SearchCurrentBackground", "現在の検索一致背景", s => s.ColSearchCurrentBg, (s, value) => s.ColSearchCurrentBg = value),
        new("SearchCurrentForeground", "現在の検索一致文字", s => s.ColSearchCurrentFg, (s, value) => s.ColSearchCurrentFg = value),
        new("MarkForeground", "マーク項目の文字（auto対応）", s => s.ColMarkFg, (s, value) => s.ColMarkFg = value),
        new("LogBackground", "ログ表示の背景", s => s.ColLogBg, (s, value) => s.ColLogBg = value),
        new("FunctionBarBackground", "ファンクションキーバーの背景", s => s.ColFunctionBg, (s, value) => s.ColFunctionBg = value),
        new("FunctionButtonBackground", "ファンクションボタンの背景", s => s.ColFnBtnBg, (s, value) => s.ColFnBtnBg = value),
        new("FunctionButtonForeground", "ファンクションボタンの文字", s => s.ColFnBtnFg, (s, value) => s.ColFnBtnFg = value),
    ];

    private static readonly Dictionary<string, CoreThemeSetting> CoreByKey =
        Core.ToDictionary(item => item.Key, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyDictionary<string, string> Purposes { get; } = BuildPurposes();

    public static bool IsCoreKey(string key) => CoreByKey.ContainsKey(key);

    // 名前を変える前のテーマ色の名前で書かれた INI も読めるよう、古い名前から今の名前を引く。
    private static readonly Dictionary<string, string> RenamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["MusicMmdspWhiteKey"] = "MusicKeyboardWhiteKey",
        ["MusicMmdspWhiteBorder"] = "MusicKeyboardWhiteBorder",
        ["MusicMmdspBlackKey"] = "MusicKeyboardBlackKey",
    };

    public static string CurrentName(string key) => RenamedKeys.TryGetValue(key, out string? current) ? current : key;

    /// <summary>
    /// 専用の項目を持つテーマ色なら設定に書き込んで true を返す。
    /// </summary>
    public static bool TrySetCore(AppSettings settings, string key, string value)
    {
        if (!CoreByKey.TryGetValue(key, out var item)) return false;
        item.Set(settings, value);
        return true;
    }

    /// <summary>
    /// INI のテーマ色の行に添える用途の説明を、書き出す順に並べた表を作る。
    /// </summary>
    private static IReadOnlyDictionary<string, string> BuildPurposes()
    {
        // 専用の項目を持つ色を先に、その後でブラシのリソースだけで使う色を加える。
        var purposes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in Core) purposes.Add(item.Key, item.Purpose);
        purposes.Add("InteractionHoverBackground", "マウスオーバー項目の背景");
        purposes.Add("InteractionSelectionBackground", "選択項目の背景");
        purposes.Add("DialogBackground", "ダイアログ全体の背景");
        purposes.Add("DialogInsetBackground", "ダイアログ内の入れ子エリア背景");
        purposes.Add("ErrorText", "エラー文字");
        purposes.Add("ViewerBlack", "ビューアの黒背景");
        purposes.Add("ViewerWhite", "ビューアの白背景");
        purposes.Add("ViewerChromeBackground", "ビューア装飾の背景");
        purposes.Add("ViewerChromeBorder", "ビューア装飾の境界線");
        purposes.Add("ViewerBadgeBackground", "PDFページバッジの背景");
        purposes.Add("TextViewerBackground", "テキストビューアの情報欄背景");
        purposes.Add("TextMinimapBackground", "テキストミニマップの背景");
        purposes.Add("DirectoryText", "ディレクトリ名の文字");
        purposes.Add("IconDirectory", "ディレクトリアイコン");
        purposes.Add("IconDefault", "標準ファイルアイコン");
        purposes.Add("IconScript", "スクリプトアイコン");
        purposes.Add("IconMarkdown", "Markdownアイコン");
        purposes.Add("IconProject", "プロジェクトアイコン");
        purposes.Add("IconGit", "Git関連アイコン");
        purposes.Add("IconArchive", "圧縮ファイルアイコン");
        purposes.Add("IconImage", "画像ファイルアイコン");
        purposes.Add("IconMusic", "音楽ファイルアイコン");
        purposes.Add("MusicBorder", "音楽表示の境界線");
        purposes.Add("MusicText", "音楽表示の主文字");
        purposes.Add("MusicMuted", "音楽表示の補助文字");
        purposes.Add("MusicKeyboardWhiteKey", "鍵盤の白鍵");
        purposes.Add("MusicKeyboardWhiteBorder", "鍵盤の白鍵の境界線");
        purposes.Add("MusicKeyboardBlackKey", "鍵盤の黒鍵");
        purposes.Add("MusicKeyLamp", "キーON時の鍵盤ランプと範囲外の三角");
        purposes.Add("MusicKeyLampNd", "NanoDriveで鳴らすパートの鍵盤ランプ");
        purposes.Add("MusicInactiveWhiteKey", "未使用チャンネルの白鍵");
        purposes.Add("MusicInactiveWhiteBorder", "未使用白鍵の境界線");
        purposes.Add("MusicInactiveBlackKey", "未使用チャンネルの黒鍵");
        purposes.Add("MusicCyan", "サウンドバーのPLAY表示とシーク位置");
        purposes.Add("MusicAmber", "音楽表示のアンバー");
        purposes.Add("MusicMeterOff", "レベルメーターの消灯セグメント");
        purposes.Add("MusicMeterLit", "レベルメーターの点灯セグメント");
        purposes.Add("MusicMeterPeak", "レベルメーターのピークホールド");
        purposes.Add("MusicMeterEndpoint", "メーター上下端のセグメント");
        purposes.Add("MusicPanRing", "PANダイヤルの円");
        purposes.Add("MusicPanDot", "PAN方向ドット");
        purposes.Add("MusicWaveformBackground", "波形表示の背景");
        purposes.Add("MusicWaveformIdle", "未使用波形");
        purposes.Add("MusicDim", "音楽表示の減光色");
        purposes.Add("MusicTransportHover", "再生操作のホバー色");
        purposes.Add("MusicTransportHoverMuted", "再生操作の減光ホバー色");
        purposes.Add("MusicTransportTrail1", "再生操作残像1");
        purposes.Add("MusicTransportTrail2", "再生操作残像2");
        purposes.Add("MusicTransportTrail3", "再生操作残像3");
        purposes.Add("MiniPlayerBorder", "サウンドバーの境界線");
        purposes.Add("MiniPlayerButtonBackground", "サウンドバーボタンの背景");
        purposes.Add("MiniPlayerButtonForeground", "サウンドバーボタンの文字");
        purposes.Add("MiniPlayerButtonBorder", "サウンドバーボタンの境界線");
        purposes.Add("MiniPlayerButtonHover", "サウンドバーボタンのホバー色");
        purposes.Add("MusicSevenSegment", "サウンドバーの7セグ表示");
        return purposes;
    }
}
