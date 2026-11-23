namespace MirrorFlux.Models;

/// <summary>
/// mf.iniから読み込むアプリ全体の設定と、その既定値。
/// </summary>
public sealed class AppSettings
{
    // 起動時の左右のパネルのフォルダー。
    public string LeftPath { get; set; } = @"C:\";
    public string RightPath { get; set; } = @"C:\";
    // 終了時に左右のフォルダー・ソート順・ログレベルをINIへ書き戻すか。falseなら終了時にINIを書き換えない。
    public bool SaveState { get; set; } = true;

    /// <summary>
    /// ファイル一覧の表示行数。0なら標準のウィンドウの高さのまま。1以上なら5～100行に収め、その行数が入るようウィンドウの高さを決める。
    /// </summary>
    private int _fileRows;
    public int FileRows
    {
        get => _fileRows;
        set => _fileRows = value <= 0 ? 0 : Math.Clamp(value, 5, 100);
    }

    // フォントとログ欄の行数。ログ欄は1～20行に収める。
    public string FontFamily { get; set; } = "Consolas, BIZ UDGothic, MS Gothic";
    public double FontSize { get; set; } = 14;
    private int _logRows = 6;
    public int LogRows
    {
        get => _logRows;
        set => _logRows = Math.Clamp(value, 1, 20);
    }
    /// <summary>
    /// ログ欄の高さ（px）。%logHeight の指定と画面の行の高さに使う。1行は20px。
    /// </summary>
    public double LogHeight
    {
        get => LogRows * 20.0;
        set => LogRows = (int)Math.Round(value / 20.0, MidpointRounding.AwayFromZero);
    }
    // ログの表示レベル（0～3）と、音楽の主音量（0～100%）。
    private int _logLevel = 2;
    public int LogLevel
    {
        get => _logLevel;
        set => _logLevel = Math.Clamp(value, 0, 3);
    }
    private int _masterVolumePercent = 50;
    public int MasterVolumePercent
    {
        get => _masterVolumePercent;
        set => _masterVolumePercent = Math.Clamp(value, 0, 100);
    }

    // ファイル一覧の表示と並び順。
    public bool ShowIcons { get; set; } = true;
    public bool ShowHidden { get; set; } = false;
    public string SortMode { get; set; } = "name";
    public string LeftSortMode { get; set; } = string.Empty;
    public string RightSortMode { get; set; } = string.Empty;

    // 基本の配色。マーク項目の文字色を auto にすると、マークの背景の明るさから自動で決める。
    public string ColScreen { get; set; } = "#000000";
    public string ColPanel { get; set; } = "#000000";
    public string ColText { get; set; } = "#8494FC";
    public string ColPaneText { get; set; } = "#D8D8D8";
    public string ColMuted { get; set; } = "#605898";
    public string ColAccent { get; set; } = "#8494FC";
    public string ColHighlight { get; set; } = "#8494FC";
    public string ColDarkPurple { get; set; } = "#19112E";
    public string ColDarkPurpleHighlight { get; set; } = "#382860";
    public string ColDarkPurpleShadow { get; set; } = "#0C0818";
    public string ColInactiveBorder { get; set; } = "#302858";
    public string ColMark { get; set; } = "#4A4D4E";
    public string ColSearchBg { get; set; } = "#FFD54F";
    public string ColSearchFg { get; set; } = "#101018";
    public string ColSearchCurrentBg { get; set; } = "#FF8C42";
    public string ColSearchCurrentFg { get; set; } = "#101018";
    public string ColMarkFg { get; set; } = "auto";
    public string ColLogBg { get; set; } = "#000000";
    public string ColFunctionBg { get; set; } = "#000000";
    public string ColFnBtnBg { get; set; } = "#B0B0B0";
    public string ColFnBtnFg { get; set; } = "#000000";
    // %theme_<リソース名> で上書きできるその他の表示色。キーはApp.xamlのリソース名。
    public Dictionary<string, string> ThemeColors { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["InteractionHoverBackground"] = "#121828",
        ["InteractionSelectionBackground"] = "#202A48",
        ["DialogBackground"] = "#000000",
        ["DialogInsetBackground"] = "#181831",
        ["ErrorText"] = "#FF8888",
        ["ViewerBlack"] = "#000000",
        ["ViewerWhite"] = "#FFFFFF",
        ["ViewerChromeBackground"] = "#2A2A2A",
        ["ViewerChromeBorder"] = "#383838",
        ["ViewerBadgeBackground"] = "#CC19112E",
        ["TextViewerBackground"] = "#0D101A",
        ["TextMinimapBackground"] = "#090C14",
        ["DirectoryText"] = "#C8D4FF",
        ["IconDirectory"] = "#AAB6FF",
        ["IconDefault"] = "#758496",
        ["IconScript"] = "#20C287",
        ["IconMarkdown"] = "#3F9CE8",
        ["IconProject"] = "#9F5FD8",
        ["IconGit"] = "#E25A45",
        ["IconArchive"] = "#D89830",
        ["IconImage"] = "#D85888",
        ["IconMusic"] = "#00FF66",
        ["MusicBorder"] = "#404064",
        ["MusicText"] = "#C8C8E8",
        ["MusicMuted"] = "#605898",
        ["MusicKeyboardWhiteKey"] = "#888894",
        ["MusicKeyboardWhiteBorder"] = "#3C3C48",
        ["MusicKeyboardBlackKey"] = "#202028",
        ["MusicKeyLamp"] = "#00E840",
        ["MusicKeyLampNd"] = "#00E5FF",
        ["MusicInactiveWhiteKey"] = "#303038",
        ["MusicInactiveWhiteBorder"] = "#202028",
        ["MusicInactiveBlackKey"] = "#101018",
        ["MusicCyan"] = "#60C8F0",
        ["MusicAmber"] = "#E0B860",
        ["MusicMeterOff"] = "#101032",
        ["MusicMeterLit"] = "#8282FA",
        ["MusicMeterPeak"] = "#6060C4",
        ["MusicMeterEndpoint"] = "#5757B4",
        ["MusicPanRing"] = "#686872",
        ["MusicPanDot"] = "#B0B0BC",
        ["MusicWaveformBackground"] = "#0A0A16",
        ["MusicWaveformIdle"] = "#466482",
        ["MusicDim"] = "#14142B",
        ["MusicTransportHover"] = "#AAB6FF",
        ["MusicTransportHoverMuted"] = "#857DBA",
        ["MusicTransportTrail1"] = "#7282E0",
        ["MusicTransportTrail2"] = "#5C6CB8",
        ["MusicTransportTrail3"] = "#485594",
        ["MiniPlayerBorder"] = "#606080",
        ["MiniPlayerButtonBackground"] = "#101024",
        ["MiniPlayerButtonForeground"] = "#A8A8FF",
        ["MiniPlayerButtonBorder"] = "#444478",
        ["MiniPlayerButtonHover"] = "#282858",
        ["MusicSevenSegment"] = "#A4A4CE",
    };

    // ファンクションキー欄の表示名（添字0がF1、12個）。INIでの名前は FunctionLabelSections を参照。
    public string[] FileFunctionLabels { get; set; } =
        ["ヘルプ", "マーク", "表示", "名前変更", "コピー", "移動", "作成", "削除", "ソート", "パス選択", "更新", "終了"];
    public string[] ViewerFunctionLabels { get; set; } =
        ["ヘルプ", "検索", "次検索", "前検索", "コード", "BIN表示", "行指定", "行番号", "再読込", "折り返し", "マップ", "閉じる"];
    /// <summary>
    /// 画像ビューアの表示名。F11には機能がないため常に空で、INIにも書かない。
    /// </summary>
    public string[] GraphicViewerFunctionLabels { get; set; } =
        ["ヘルプ", "フィット", "前画像", "次画像", "PhotoShop", "反転", "回転", "背景", "再読込", "関連", "", "終了"];
    public string[] MusicFunctionLabels { get; set; } =
        ["ヘルプ", "再生/休止", "前曲", "次曲", "全曲", "リピート", "シャッフル", "停止", "ループ", "早送り", "レジスタ", "終了"];

    /// <summary>
    /// ファンクションキー表示名の組ごとの、INIの接頭辞（%fn01～%fn12 など）、書き出し時の見出し、表示名の配列、INIで扱わないキー番号。
    /// </summary>
    internal static readonly (string Prefix, string Title, Func<AppSettings, string[]> Labels, int SkippedKey)[] FunctionLabelSections =
    [
        ("fn", "ファンクションキー表示テキスト (F1〜F12)", s => s.FileFunctionLabels, 0),
        ("vfn", "テキストビューアー ファンクションキー表示テキスト (F1〜F12)", s => s.ViewerFunctionLabels, 0),
        ("gvfn", "グラフィックビューアー ファンクションキー表示テキスト (F1〜F12)", s => s.GraphicViewerFunctionLabels, 11),
        ("mfn", "サウンドプレイヤー ファンクションキー表示テキスト (F1〜F12)", s => s.MusicFunctionLabels, 0),
    ];

    /// <summary>
    /// INIのキー（小文字。例："vfn02"、"vfn2"）がファンクションキーの表示名なら、その配列と添字を返す。
    /// </summary>
    internal static bool TryGetFunctionLabelSlot(AppSettings settings, string key, out string[] labels, out int index)
    {
        foreach (var section in FunctionLabelSections)
        {
            if (!key.StartsWith(section.Prefix, StringComparison.Ordinal)) continue;
            string number = key[section.Prefix.Length..];
            if (number.Length is 1 or 2 && number.All(char.IsAsciiDigit) &&
                int.Parse(number) is >= 1 and <= 12 and var n && n != section.SkippedKey)
            {
                labels = section.Labels(settings);
                index = n - 1;
                return true;
            }
        }
        labels = [];
        index = -1;
        return false;
    }

    // テキストビューアの色分け表示の設定。
    public bool TVHighlight { get; set; } = true;
    public string TVSyntaxPath { get; set; } = "syntax";
    public string TVColorComment { get; set; } = "#808CA8";
    public string TVColorKeyword { get; set; } = "#B3A2FF";
    public string TVColorString { get; set; } = "#E8B887";
    public string TVColorNumber { get; set; } = "#99C8F0";
    public string TVColorKey { get; set; } = "#85CDE0";
    public string TVColorScenarioVoice { get; set; } = "#EBCB68";
    public List<TextSyntaxRule> TVSyntaxRules { get; set; } = [];
    public Dictionary<string, string> TVSyntaxPrefixes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string TVColorUrl { get; set; } = "#69C9FF";
    public string TVColorSection { get; set; } = "#D0B2F5";
    // ジャンプリストの移動先（1～10番）。
    public SortedDictionary<int, string> JumpPaths { get; set; } = new()
    {
        [1] = "shell:Desktop",
        [2] = "shell:Downloads",
        [3] = "shell:Personal"
    };
    // MDXのPDXファイルを探すフォルダーと、NanoDrive 6/7/8 の実機（NDSIF）で鳴らすか。
    public string PdxSearchPaths { get; set; } = "";
    public bool Nd6Ndsif { get; set; } = false;
    public bool Nd7Ndsif { get; set; } = false;
    public bool Nd8Ndsif { get; set; } = false;
    // 拡張子ごとに使う色分けの種類。
    public Dictionary<string, string> TVSyntaxMappings { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    { ["ini"] = "ini", ["cfg"] = "auto", ["json"] = "json", ["xml"] = "xml", ["xaml"] = "xml", ["cs"] = "csharp", ["txt"] = "none", ["c"] = "cpp", ["cpp"] = "cpp", ["h"] = "cpp", ["cc"] = "cpp", ["cxx"] = "cpp", ["hpp"] = "cpp", ["hh"] = "cpp", ["hxx"] = "cpp" };

    // 拡張子関連付けの既定（.txt ---- &textviewer; など）。音楽ファイルは修飾キーなしならサウンドバー、Shift付きならサウンドビューアで開く。
    public List<ExtBindingItem> ExtBindings { get; set; } = new()
    {
        new ExtBindingItem(".txt", MirrorFlux.Input.AppCommand.TextViewer, "textviewer"),
        new ExtBindingItem(".md", MirrorFlux.Input.AppCommand.TextViewer, "textviewer"),
        new ExtBindingItem(".log", MirrorFlux.Input.AppCommand.TextViewer, "textviewer"),
        new ExtBindingItem(".ini", MirrorFlux.Input.AppCommand.TextViewer, "textviewer"),
        new ExtBindingItem(".cfg", MirrorFlux.Input.AppCommand.TextViewer, "textviewer"),
        new ExtBindingItem(".c", MirrorFlux.Input.AppCommand.TextViewer, "textviewer"),
        new ExtBindingItem(".cpp", MirrorFlux.Input.AppCommand.TextViewer, "textviewer"),
        new ExtBindingItem(".h", MirrorFlux.Input.AppCommand.TextViewer, "textviewer"),
        new ExtBindingItem(".cc", MirrorFlux.Input.AppCommand.TextViewer, "textviewer"),
        new ExtBindingItem(".cxx", MirrorFlux.Input.AppCommand.TextViewer, "textviewer"),
        new ExtBindingItem(".hpp", MirrorFlux.Input.AppCommand.TextViewer, "textviewer"),
        new ExtBindingItem(".hh", MirrorFlux.Input.AppCommand.TextViewer, "textviewer"),
        new ExtBindingItem(".hxx", MirrorFlux.Input.AppCommand.TextViewer, "textviewer"),
        new ExtBindingItem(".doc", MirrorFlux.Input.AppCommand.TextViewer, "textviewer"),
        new ExtBindingItem(".man", MirrorFlux.Input.AppCommand.TextViewer, "textviewer"),
        new ExtBindingItem(".text", MirrorFlux.Input.AppCommand.TextViewer, "textviewer"),
        new ExtBindingItem(".cs", MirrorFlux.Input.AppCommand.TextViewer, "textviewer"),
        new ExtBindingItem(".xaml", MirrorFlux.Input.AppCommand.TextViewer, "textviewer"),
        new ExtBindingItem(".xml", MirrorFlux.Input.AppCommand.TextViewer, "textviewer"),
        new ExtBindingItem(".json", MirrorFlux.Input.AppCommand.TextViewer, "textviewer"),
        new ExtBindingItem(".bat", MirrorFlux.Input.AppCommand.TextViewer, "textviewer"),
        new ExtBindingItem(".ps1", MirrorFlux.Input.AppCommand.TextViewer, "textviewer"),
        new ExtBindingItem(".pdf", MirrorFlux.Input.AppCommand.PdfViewer, "pdfviewer"),
        new ExtBindingItem(".ai", MirrorFlux.Input.AppCommand.GraphicViewer, "graphicviewer"),
        new ExtBindingItem(".png", MirrorFlux.Input.AppCommand.GraphicViewer, "graphicviewer"),
        new ExtBindingItem(".jpg", MirrorFlux.Input.AppCommand.GraphicViewer, "graphicviewer"),
        new ExtBindingItem(".jpeg", MirrorFlux.Input.AppCommand.GraphicViewer, "graphicviewer"),
        new ExtBindingItem(".bmp", MirrorFlux.Input.AppCommand.GraphicViewer, "graphicviewer"),
        new ExtBindingItem(".gif", MirrorFlux.Input.AppCommand.GraphicViewer, "graphicviewer"),
        new ExtBindingItem(".ico", MirrorFlux.Input.AppCommand.GraphicViewer, "graphicviewer"),
        new ExtBindingItem(".webp", MirrorFlux.Input.AppCommand.GraphicViewer, "graphicviewer"),
        new ExtBindingItem(".heic", MirrorFlux.Input.AppCommand.GraphicViewer, "graphicviewer"),
        new ExtBindingItem(".heif", MirrorFlux.Input.AppCommand.GraphicViewer, "graphicviewer"),
        new ExtBindingItem(".psd", MirrorFlux.Input.AppCommand.GraphicViewer, "graphicviewer"),
        new ExtBindingItem(".tif", MirrorFlux.Input.AppCommand.GraphicViewer, "graphicviewer"),
        new ExtBindingItem(".tiff", MirrorFlux.Input.AppCommand.GraphicViewer, "graphicviewer"),
        new ExtBindingItem(".mp4", MirrorFlux.Input.AppCommand.VideoViewer, "videoviewer"),
        new ExtBindingItem(".m4v", MirrorFlux.Input.AppCommand.VideoViewer, "videoviewer"),
        new ExtBindingItem(".wmv", MirrorFlux.Input.AppCommand.VideoViewer, "videoviewer"),
        new ExtBindingItem(".avi", MirrorFlux.Input.AppCommand.VideoViewer, "videoviewer"),
        new ExtBindingItem(".mov", MirrorFlux.Input.AppCommand.VideoViewer, "videoviewer"),
        new ExtBindingItem(".mpg", MirrorFlux.Input.AppCommand.VideoViewer, "videoviewer"),
        new ExtBindingItem(".mpeg", MirrorFlux.Input.AppCommand.VideoViewer, "videoviewer"),
        new ExtBindingItem(".mkv", MirrorFlux.Input.AppCommand.VideoViewer, "videoviewer"),
        new ExtBindingItem(".webm", MirrorFlux.Input.AppCommand.VideoViewer, "videoviewer"),
        new ExtBindingItem(".ay", MirrorFlux.Input.AppCommand.MiniPlayer, "miniplayer"),
        new ExtBindingItem(".ay", MirrorFlux.Input.AppCommand.VgmPlayer, "vgmplayer", System.Windows.Input.ModifierKeys.Shift),
        new ExtBindingItem(".gbs", MirrorFlux.Input.AppCommand.MiniPlayer, "miniplayer"),
        new ExtBindingItem(".gbs", MirrorFlux.Input.AppCommand.VgmPlayer, "vgmplayer", System.Windows.Input.ModifierKeys.Shift),
        new ExtBindingItem(".gym", MirrorFlux.Input.AppCommand.MiniPlayer, "miniplayer"),
        new ExtBindingItem(".gym", MirrorFlux.Input.AppCommand.VgmPlayer, "vgmplayer", System.Windows.Input.ModifierKeys.Shift),
        new ExtBindingItem(".s98", MirrorFlux.Input.AppCommand.MiniPlayer, "miniplayer"),
        new ExtBindingItem(".s98", MirrorFlux.Input.AppCommand.VgmPlayer, "vgmplayer", System.Windows.Input.ModifierKeys.Shift),
        new ExtBindingItem(".mdx", MirrorFlux.Input.AppCommand.MiniPlayer, "miniplayer"),
        new ExtBindingItem(".mdx", MirrorFlux.Input.AppCommand.VgmPlayer, "vgmplayer", System.Windows.Input.ModifierKeys.Shift),
        new ExtBindingItem(".nsf", MirrorFlux.Input.AppCommand.MiniPlayer, "miniplayer"),
        new ExtBindingItem(".nsf", MirrorFlux.Input.AppCommand.VgmPlayer, "vgmplayer", System.Windows.Input.ModifierKeys.Shift),
        new ExtBindingItem(".nsfe", MirrorFlux.Input.AppCommand.MiniPlayer, "miniplayer"),
        new ExtBindingItem(".nsfe", MirrorFlux.Input.AppCommand.VgmPlayer, "vgmplayer", System.Windows.Input.ModifierKeys.Shift),
        new ExtBindingItem(".hes", MirrorFlux.Input.AppCommand.MiniPlayer, "miniplayer"),
        new ExtBindingItem(".hes", MirrorFlux.Input.AppCommand.VgmPlayer, "vgmplayer", System.Windows.Input.ModifierKeys.Shift),
        new ExtBindingItem(".kss", MirrorFlux.Input.AppCommand.MiniPlayer, "miniplayer"),
        new ExtBindingItem(".kss", MirrorFlux.Input.AppCommand.VgmPlayer, "vgmplayer", System.Windows.Input.ModifierKeys.Shift),
        new ExtBindingItem(".sap", MirrorFlux.Input.AppCommand.MiniPlayer, "miniplayer"),
        new ExtBindingItem(".sap", MirrorFlux.Input.AppCommand.VgmPlayer, "vgmplayer", System.Windows.Input.ModifierKeys.Shift),
        new ExtBindingItem(".spc", MirrorFlux.Input.AppCommand.MiniPlayer, "miniplayer"),
        new ExtBindingItem(".spc", MirrorFlux.Input.AppCommand.VgmPlayer, "vgmplayer", System.Windows.Input.ModifierKeys.Shift),
        new ExtBindingItem(".vgm", MirrorFlux.Input.AppCommand.MiniPlayer, "miniplayer"),
        new ExtBindingItem(".vgm", MirrorFlux.Input.AppCommand.VgmPlayer, "vgmplayer", System.Windows.Input.ModifierKeys.Shift),
        new ExtBindingItem(".vgz", MirrorFlux.Input.AppCommand.MiniPlayer, "miniplayer"),
        new ExtBindingItem(".vgz", MirrorFlux.Input.AppCommand.VgmPlayer, "vgmplayer", System.Windows.Input.ModifierKeys.Shift),
        new ExtBindingItem(".mp3", MirrorFlux.Input.AppCommand.MiniPlayer, "miniplayer"),
        new ExtBindingItem(".wav", MirrorFlux.Input.AppCommand.MiniPlayer, "miniplayer"),
        new ExtBindingItem(".flac", MirrorFlux.Input.AppCommand.MiniPlayer, "miniplayer"),
        new ExtBindingItem(".m4a", MirrorFlux.Input.AppCommand.MiniPlayer, "miniplayer"),
        new ExtBindingItem(".wma", MirrorFlux.Input.AppCommand.MiniPlayer, "miniplayer"),
        new ExtBindingItem(".aac", MirrorFlux.Input.AppCommand.MiniPlayer, "miniplayer"),
    };

    // ビューアのキー割り当て。
    public List<MirrorFlux.Input.ViewerFunctionBinding> ViewerKeyBindings { get; set; } = MirrorFlux.Input.ViewerFunctionBindings.CreateDefaults().ToList();

    // ファイル一覧画面のキー割り当ての既定。
    public List<KeyBindingItem> KeyBindings { get; set; } =
    [
        .. MirrorFlux.Input.FunctionKeyBindings.CreateDefaults(),
        new KeyBindingItem(System.Windows.Input.Key.Tab, System.Windows.Input.ModifierKeys.None, MirrorFlux.Input.AppCommand.SwitchPane, "switchpane"),
        new KeyBindingItem(System.Windows.Input.Key.Tab, System.Windows.Input.ModifierKeys.Shift, MirrorFlux.Input.AppCommand.SwitchPane, "switchpane"),
        new KeyBindingItem(System.Windows.Input.Key.Back, System.Windows.Input.ModifierKeys.None, MirrorFlux.Input.AppCommand.GoParent, "parent"),
        new KeyBindingItem(System.Windows.Input.Key.Left, System.Windows.Input.ModifierKeys.None, MirrorFlux.Input.AppCommand.ParentLeft, "parentleft",
            FollowingCommands: [new(System.Windows.Input.Key.Left, System.Windows.Input.ModifierKeys.None, MirrorFlux.Input.AppCommand.ActivateLeftPane, "paneleft")]),
        new KeyBindingItem(System.Windows.Input.Key.Right, System.Windows.Input.ModifierKeys.None, MirrorFlux.Input.AppCommand.ParentRight, "parentright",
            FollowingCommands: [new(System.Windows.Input.Key.Right, System.Windows.Input.ModifierKeys.None, MirrorFlux.Input.AppCommand.ActivateRightPane, "paneright")]),
        new KeyBindingItem(System.Windows.Input.Key.Return, System.Windows.Input.ModifierKeys.None, MirrorFlux.Input.AppCommand.ExtExec, "extexec"),
        new KeyBindingItem(System.Windows.Input.Key.Return, System.Windows.Input.ModifierKeys.Shift, MirrorFlux.Input.AppCommand.ExtExecWindows, "extexecwin"),
        new KeyBindingItem(System.Windows.Input.Key.V, System.Windows.Input.ModifierKeys.None, MirrorFlux.Input.AppCommand.TextViewer, "textviewer"),
        new KeyBindingItem(System.Windows.Input.Key.V, System.Windows.Input.ModifierKeys.Shift, MirrorFlux.Input.AppCommand.HexViewer, "hexviewer"),
        new KeyBindingItem(System.Windows.Input.Key.G, System.Windows.Input.ModifierKeys.None, MirrorFlux.Input.AppCommand.GraphicViewer, "graphicviewer"),
        new KeyBindingItem(System.Windows.Input.Key.R, System.Windows.Input.ModifierKeys.None, MirrorFlux.Input.AppCommand.FileRename, "filerename"),
        new KeyBindingItem(System.Windows.Input.Key.C, System.Windows.Input.ModifierKeys.None, MirrorFlux.Input.AppCommand.FileCopyCheck, "filecopycheck"),
        new KeyBindingItem(System.Windows.Input.Key.C, System.Windows.Input.ModifierKeys.Shift, MirrorFlux.Input.AppCommand.FileCopy, "filecopy"),
        new KeyBindingItem(System.Windows.Input.Key.M, System.Windows.Input.ModifierKeys.None, MirrorFlux.Input.AppCommand.FileMoveCheck, "filemovecheck"),
        new KeyBindingItem(System.Windows.Input.Key.M, System.Windows.Input.ModifierKeys.Shift, MirrorFlux.Input.AppCommand.FileMove, "filemove"),
        new KeyBindingItem(System.Windows.Input.Key.S, System.Windows.Input.ModifierKeys.None, MirrorFlux.Input.AppCommand.Sort, "sort"),
        new KeyBindingItem(System.Windows.Input.Key.Q, System.Windows.Input.ModifierKeys.Control, MirrorFlux.Input.AppCommand.Exit, "exit"),
        new KeyBindingItem(System.Windows.Input.Key.L, System.Windows.Input.ModifierKeys.None, MirrorFlux.Input.AppCommand.DriveMenu, "drive"),
        new KeyBindingItem(System.Windows.Input.Key.K, System.Windows.Input.ModifierKeys.None, MirrorFlux.Input.AppCommand.MakeDir, "mkdir"),
        new KeyBindingItem(System.Windows.Input.Key.A, System.Windows.Input.ModifierKeys.None, MirrorFlux.Input.AppCommand.MarkAll, "markall"),
        new KeyBindingItem(System.Windows.Input.Key.A, System.Windows.Input.ModifierKeys.Shift, MirrorFlux.Input.AppCommand.MarkClear, "markclear"),
        new KeyBindingItem(System.Windows.Input.Key.A, System.Windows.Input.ModifierKeys.Control, MirrorFlux.Input.AppCommand.Archive, "archive"),
        new KeyBindingItem(System.Windows.Input.Key.I, System.Windows.Input.ModifierKeys.None, MirrorFlux.Input.AppCommand.IncrementalSearch, "incrimentalsearch"),
        new KeyBindingItem(System.Windows.Input.Key.Delete, System.Windows.Input.ModifierKeys.None, MirrorFlux.Input.AppCommand.Delete, "filedelete"),
        new KeyBindingItem(System.Windows.Input.Key.D, System.Windows.Input.ModifierKeys.None, MirrorFlux.Input.AppCommand.Delete, "filedelete"),
        new KeyBindingItem(System.Windows.Input.Key.OemMinus, System.Windows.Input.ModifierKeys.None, MirrorFlux.Input.AppCommand.SyncPanePath, "samepath"),
        new KeyBindingItem(System.Windows.Input.Key.OemBackslash, System.Windows.Input.ModifierKeys.None, MirrorFlux.Input.AppCommand.ChangeDirectory, "chdir"),
        new KeyBindingItem(System.Windows.Input.Key.J, System.Windows.Input.ModifierKeys.None, MirrorFlux.Input.AppCommand.ChangeDirectory, "chdir"),
        new KeyBindingItem(System.Windows.Input.Key.J, System.Windows.Input.ModifierKeys.Shift, MirrorFlux.Input.AppCommand.JumpList, "jumplist"),
        new KeyBindingItem(System.Windows.Input.Key.Escape, System.Windows.Input.ModifierKeys.None, MirrorFlux.Input.AppCommand.OperationCancel, "opcancel"),
    ];
}
