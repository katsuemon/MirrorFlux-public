using System.Globalization;
using MirrorFlux.Models;
using MirrorFlux.Input;

namespace MirrorFlux.Services;

/// <summary>
/// INI の文字列を読み、設定に変換する。古い形式の名前や、後から増えた既定のキー・拡張子の割り当ても補う。
/// </summary>
internal static class SettingsIniReader
{
    /// <summary>
    /// INI を1行ずつ読み、変数・キー割り当て・拡張子の割り当てを設定に取り込む。
    /// </summary>
    public static AppSettings Parse(string text)
    {
        var settings = new AppSettings();
        var lines = text.Split('\n');
        var loadedKeyBindings = new List<KeyBindingItem>();
        var loadedExtBindings = new List<ExtBindingItem>();
        string section = "";
        string? keyContext = "file";
        foreach (var rawLine in lines)
        {
            // 空行とコメント行を飛ばす。
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';'))
            {
                continue;
            }

            // セクション見出し。「keys.」のセクションならキー割り当ての対象を切り替える。
            if (IniDocument.TrySection(line, out var header))
            {
                section = header;
                keyContext = section.StartsWith("keys.") ? section[5..] : null;
                continue;
            }
            // 「>」はキー割り当て。ファイル一覧用とビューアー用で読み方を分ける。
            if (line.StartsWith('>'))
            {
                if (keyContext == null) continue;
                if (keyContext != "file")
                {
                    if (ViewerFunctionBindings.TryParse(keyContext, line, out var viewerBinding) && viewerBinding != null)
                        settings.ViewerKeyBindings.Add(viewerBinding);
                    continue;
                }
                if (IniKeyParser.TryParseLine(line, out var keyItem) && keyItem is not null)
                {
                    loadedKeyBindings.Add(keyItem);
                }
                continue;
            }

            // 「.」は拡張子の割り当て。全体設定のセクションでだけ受け付ける。
            if (line.StartsWith('.'))
            {
                if (section is not ("" or "extensions" or "settings" or "general")) continue;
                if (IniKeyParser.TryParseExtBinding(line, out var extItem) && extItem is not null)
                {
                    loadedExtBindings.Add(extItem);
                }
                continue;
            }

            // 「%名前 値」の変数。値のない %jumpPath は登録の削除として扱う。それ以外は「名前=値」の形として読む。
            if (line.StartsWith('%'))
            {
                var sepIndex = line.IndexOfAny(['\t', ' ', '=']);
                if (sepIndex > 1)
                {
                    var key = line[1..sepIndex].Trim();
                    var value = line[(sepIndex + 1)..].Trim();
                    if (IniDocument.AllowsVariable(section, key)) ApplyKey(settings, key, value);
                }
                else if (line.StartsWith("%jumpPath", StringComparison.OrdinalIgnoreCase))
                {
                    if (IniDocument.AllowsVariable(section, line[1..])) ApplyKey(settings, line[1..], "");
                }
            }
            else
            {
                var eqIndex = line.IndexOf('=');
                if (eqIndex > 0)
                {
                    var key = line[..eqIndex].Trim();
                    var value = line[(eqIndex + 1)..].Trim();
                    if (IniDocument.AllowsVariable(section, key)) ApplyKey(settings, key, value);
                }
            }
        }

        // キー割り当てがあれば置き換え、書かれていないファンクションキーの既定を補う。
        if (loadedKeyBindings.Count > 0)
        {
            settings.KeyBindings = loadedKeyBindings;
            foreach (var binding in FunctionKeyBindings.CreateDefaults())
                if (!loadedKeyBindings.Any(b => b.Key == binding.Key && b.Modifiers == binding.Modifiers))
                    loadedKeyBindings.Add(binding);
        }

        if (loadedExtBindings.Count > 0)
        {
            settings.ExtBindings = loadedExtBindings;
        }

        // 動画の拡張子の既定と、音楽などの後から増えた既定の割り当てを補う。
        foreach (var binding in new AppSettings().ExtBindings.Where(b => b.Command == AppCommand.VideoViewer))
            if (!settings.ExtBindings.Any(b => b.Extension.Equals(binding.Extension, StringComparison.OrdinalIgnoreCase) && b.Modifiers == binding.Modifiers))
                settings.ExtBindings.Add(binding);
        UpgradeLegacyMusicBindings(settings);
        return settings;
    }

    /// <summary>
    /// 古い INI に足りないキー・拡張子の割り当てを補う。利用者が書いた割り当ては上書きしない。
    /// </summary>
    private static void UpgradeLegacyMusicBindings(AppSettings settings)
    {
        // チップチューンの拡張子は、Enter でサウンドバー、Shift+Enter でフル画面のプレイヤーにする。
        // VGM/VGZ がフル画面のプレイヤー1件だけの古い既定なら、新しい既定に置き換える。
        foreach (string extension in MirrorFlux.Services.Music.MusicFormats.Chiptune)
        {
            var matches = settings.ExtBindings.Where(binding =>
                string.Equals(binding.Extension, extension, StringComparison.OrdinalIgnoreCase)).ToArray();
            bool legacyFullViewerDefault = (extension is ".vgm" or ".vgz") && matches.Length == 1 &&
                matches[0].Modifiers == System.Windows.Input.ModifierKeys.None &&
                matches[0].Command == AppCommand.VgmPlayer;

            if (legacyFullViewerDefault)
            {
                settings.ExtBindings.Remove(matches[0]);
                matches = [];
            }

            // 割り当てがなければ、MP3 の前に2件を加える。
            if (matches.Length == 0)
            {
                int audioIdx = settings.ExtBindings.FindIndex(b => string.Equals(b.Extension, ".mp3", StringComparison.OrdinalIgnoreCase));
                var miniItem = new ExtBindingItem(extension, AppCommand.MiniPlayer, "miniplayer");
                var fullItem = new ExtBindingItem(extension, AppCommand.VgmPlayer, "vgmplayer", System.Windows.Input.ModifierKeys.Shift);
                if (audioIdx >= 0)
                {
                    settings.ExtBindings.Insert(audioIdx, miniItem);
                    settings.ExtBindings.Insert(audioIdx + 1, fullItem);
                }
                else
                {
                    settings.ExtBindings.Add(miniItem);
                    settings.ExtBindings.Add(fullItem);
                }
                continue;
            }

            // サウンドバーだけで Shift の割り当てがなければ、フル画面のプレイヤーを加える。
            bool hasPlainMini = matches.Any(binding => binding.Modifiers == System.Windows.Input.ModifierKeys.None && binding.Command == AppCommand.MiniPlayer);
            bool hasShiftBinding = matches.Any(binding => binding.Modifiers == System.Windows.Input.ModifierKeys.Shift);
            if (hasPlainMini && !hasShiftBinding)
                settings.ExtBindings.Add(new ExtBindingItem(extension, AppCommand.VgmPlayer, "vgmplayer", System.Windows.Input.ModifierKeys.Shift));
        }

        // 一般の音声ファイルはサウンドバーで開く。
        foreach (string extension in Services.Music.MusicFormats.Audio)
        {
            bool hasBinding = settings.ExtBindings.Any(binding =>
                string.Equals(binding.Extension, extension, StringComparison.OrdinalIgnoreCase));
            if (!hasBinding)
            {
                settings.ExtBindings.Add(new ExtBindingItem(extension, AppCommand.MiniPlayer, "miniplayer"));
            }
        }

        // 後から増えたキーの既定（Shift+Enter、マーク、アーカイブ、インクリメンタルサーチ、並べ替え、ジャンプリスト、ビューアー）を、空いていれば加える。
        bool hasShiftEnter = settings.KeyBindings.Any(binding =>
            binding.Key is System.Windows.Input.Key.Return or System.Windows.Input.Key.Enter &&
            binding.Modifiers == System.Windows.Input.ModifierKeys.Shift);
        if (!hasShiftEnter)
            settings.KeyBindings.Add(new KeyBindingItem(System.Windows.Input.Key.Return, System.Windows.Input.ModifierKeys.Shift,
                AppCommand.ExtExecWindows, "extexecwin"));
        foreach (var (key, modifiers, command, name) in new[] {
            (System.Windows.Input.Key.A, System.Windows.Input.ModifierKeys.None, AppCommand.MarkAll, "markall"),
            (System.Windows.Input.Key.A, System.Windows.Input.ModifierKeys.Shift, AppCommand.MarkClear, "markclear"),
            (System.Windows.Input.Key.A, System.Windows.Input.ModifierKeys.Control, AppCommand.Archive, "archive"),
            (System.Windows.Input.Key.I, System.Windows.Input.ModifierKeys.None, AppCommand.IncrementalSearch, "incrimentalsearch") })
            if (!settings.KeyBindings.Any(b => b.Key == key && b.Modifiers == modifiers))
                settings.KeyBindings.Add(new KeyBindingItem(key, modifiers, command, name));
        if (!settings.KeyBindings.Any(b => b.Key == System.Windows.Input.Key.S && b.Modifiers == System.Windows.Input.ModifierKeys.None))
            settings.KeyBindings.Add(new KeyBindingItem(System.Windows.Input.Key.S, System.Windows.Input.ModifierKeys.None, AppCommand.Sort, "sort"));
        // ファイル操作の中止は、どのキーにも割り当てていなければ Esc に割り当てる。
        if (!settings.KeyBindings.Any(b => b.Command == AppCommand.OperationCancel) &&
            !settings.KeyBindings.Any(b => b.Key == System.Windows.Input.Key.Escape && b.Modifiers == System.Windows.Input.ModifierKeys.None))
            settings.KeyBindings.Add(new KeyBindingItem(System.Windows.Input.Key.Escape, System.Windows.Input.ModifierKeys.None, AppCommand.OperationCancel, "opcancel"));
        if (!settings.KeyBindings.Any(b => b.Key == System.Windows.Input.Key.F10 && b.Modifiers == System.Windows.Input.ModifierKeys.None))
            settings.KeyBindings.Add(new KeyBindingItem(System.Windows.Input.Key.F10, System.Windows.Input.ModifierKeys.None, AppCommand.JumpList, "jumplist"));
        foreach (var (modifiers, command, name) in new[] {
            (System.Windows.Input.ModifierKeys.None, AppCommand.TextViewer, "textviewer"),
            (System.Windows.Input.ModifierKeys.Shift, AppCommand.HexViewer, "hexviewer") })
            if (!settings.KeyBindings.Any(b => b.Key == System.Windows.Input.Key.V && b.Modifiers == modifiers))
                settings.KeyBindings.Add(new KeyBindingItem(System.Windows.Input.Key.V, modifiers, command, name));

        // 後から増えた拡張子の既定（ソース・文書はテキストビューアー、PDF は PDF ビューアー、AI は PNG の前に画像ビューアー）を加える。
        foreach (string extension in new[] { ".c", ".cpp", ".h", ".cc", ".cxx", ".hpp", ".hh", ".hxx", ".doc", ".man", ".text" })
            if (!settings.ExtBindings.Any(binding => string.Equals(binding.Extension, extension, StringComparison.OrdinalIgnoreCase)))
                settings.ExtBindings.Add(new ExtBindingItem(extension, AppCommand.TextViewer, "textviewer"));

        if (!settings.ExtBindings.Any(b => string.Equals(b.Extension, ".cfg", StringComparison.OrdinalIgnoreCase)))
            settings.ExtBindings.Add(new ExtBindingItem(".cfg", AppCommand.TextViewer, "textviewer"));

        if (!settings.ExtBindings.Any(b => string.Equals(b.Extension, ".pdf", StringComparison.OrdinalIgnoreCase)))
            settings.ExtBindings.Add(new ExtBindingItem(".pdf", AppCommand.PdfViewer, "pdfviewer"));

        if (!settings.ExtBindings.Any(b => string.Equals(b.Extension, ".ai", StringComparison.OrdinalIgnoreCase)))
        {
            int pngIdx = settings.ExtBindings.FindIndex(b => string.Equals(b.Extension, ".png", StringComparison.OrdinalIgnoreCase));
            var aiItem = new ExtBindingItem(".ai", AppCommand.GraphicViewer, "graphicviewer");
            if (pngIdx >= 0)
                settings.ExtBindings.Insert(pngIdx, aiItem);
            else
                settings.ExtBindings.Add(aiItem);
        }
    }

    /// <summary>
    /// 変数1つを設定に反映する。古い別名も受け付け、範囲外の数値は無視する。
    /// </summary>
    private static void ApplyKey(AppSettings s, string key, string value)
    {
        // テーマ色。値の最初の語だけを使い、古い名前は今の名前に直す。
        if (key.StartsWith("theme_", StringComparison.OrdinalIgnoreCase) && key.Length > 6)
        {
            string themeKey = ThemeKeyCatalog.CurrentName(key[6..].Trim());
            string themeValue = value.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            if (themeValue.Length > 0 && !ThemeKeyCatalog.TrySetCore(s, themeKey, themeValue))
                s.ThemeColors[themeKey] = themeValue;
            return;
        }
        // ジャンプ先。空の値は登録の削除とする。
        if (key.StartsWith("jumppath", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(key[8..], out int jumpSlot) && jumpSlot is >= 1 and <= 10)
        {
            if (string.IsNullOrWhiteSpace(value)) s.JumpPaths.Remove(jumpSlot);
            else s.JumpPaths[jumpSlot] = JumpListService.NormalizeIniValue(value);
            return;
        }
        if (value.Length == 0) return;

        // 拡張子ごとの構文の割り当てと、ファンクションキーの表示名。
        if (key.StartsWith("tvsyntax_", StringComparison.OrdinalIgnoreCase))
        {
            s.TVSyntaxMappings[key[9..].TrimStart('.')] = value;
            return;
        }
        if (AppSettings.TryGetFunctionLabelSlot(s, key.ToLowerInvariant(), out string[] labels, out int labelIndex))
        {
            labels[labelIndex] = value;
            return;
        }
        switch (key.ToLowerInvariant())
        {
            // テキストビューアーの構文ハイライトと音楽再生の設定。
            case "tvhighlight": s.TVHighlight = value is "1" or "true" or "on"; break;
            case "tvsyntaxpath": s.TVSyntaxPath = value; break;
            case "tvcolorcomment": s.TVColorComment = value; break;
            case "tvcolorkeyword": s.TVColorKeyword = value; break;
            case "tvcolorstring": s.TVColorString = value; break;
            case "tvcolornumber": s.TVColorNumber = value; break;
            case "tvcolorkey": s.TVColorKey = value; break;
            case "tvsyntaxprefix":
                if (TextSyntaxRule.TryParse(value, out var prefixRule) && prefixRule != null) s.TVSyntaxPrefixes[prefixRule.Pattern] = prefixRule.Syntax;
                break;
            case "tvsyntaxrule":
                if (TextSyntaxRule.TryParse(value, out var syntaxRule) && syntaxRule != null) s.TVSyntaxRules.Add(syntaxRule);
                break;
            case "tvcolorscenariovoice": s.TVColorScenarioVoice = value; break;
            case "tvcolorurl": s.TVColorUrl = value; break;
            case "tvcolorsection": s.TVColorSection = value; break;
            case "pdxsearchpaths": s.PdxSearchPaths = value; break;
            case "nd6-ndsif" or "nd6_ndsif" or "nd6ndsif":
                s.Nd6Ndsif = ParseBool(value, false); break;
            case "nd7-ndsif" or "nd7_ndsif" or "nd7ndsif":
                s.Nd7Ndsif = ParseBool(value, false); break;
            case "nd8-ndsif" or "nd8_ndsif" or "nd8ndsif":
                s.Nd8Ndsif = ParseBool(value, false); break;
            // パネルのパスと保存設定。
            case "leftpath" or "pathl":
                s.LeftPath = value;
                break;
            case "rightpath" or "pathr":
                s.RightPath = value;
                break;
            case "savestate" or "save_state":
                s.SaveState = ParseBool(value, true);
                break;
            case "filerows" or "file_rows":
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var fileRows))
                    s.FileRows = fileRows;
                break;

            // ウィンドウ・表示の設定。
            case "fontfamily" or "font":
                s.FontFamily = value;
                break;
            case "fontsize" or "font_size":
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var fs) && fs >= 8 && fs <= 32)
                    s.FontSize = fs;
                break;
            case "logheight" or "log_h":
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var lh) && lh >= 40 && lh <= 400)
                    s.LogHeight = lh;
                break;
            case "logrows" or "log_rows":
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rows) && rows is >= 1 and <= 20)
                    s.LogRows = rows;
                break;
            case "loglevel" or "log_level":
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var lvl))
                    s.LogLevel = Math.Clamp(lvl, 0, 3);
                break;
            case "mastervolume":
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var masterVolume))
                    s.MasterVolumePercent = masterVolume;
                break;

            // 動作・ファイル一覧の設定。
            case "showicons" or "icons" or "show_icons":
                s.ShowIcons = ParseBool(value, true);
                break;
            case "showhidden" or "hidden" or "show_hidden":
                s.ShowHidden = ParseBool(value, false);
                break;
            case "sortmode" or "sort" or "sort_mode":
                s.SortMode = value.Trim().ToLowerInvariant();
                break;
            case "leftsort" or "leftsortmode" or "sortl" or "left_sort":
                s.LeftSortMode = value.Trim().ToLowerInvariant();
                break;
            case "rightsort" or "rightsortmode" or "sortr" or "right_sort":
                s.RightSortMode = value.Trim().ToLowerInvariant();
                break;

            // 古い形式のテーマ色の名前。
            case "col_screen" or "col0" or "screenbackground":
                s.ColScreen = value;
                break;
            case "col_panel" or "panelbackground":
                s.ColPanel = value;
                break;
            case "col_text" or "primarytext":
                s.ColText = value;
                break;
            case "col_panetext" or "col_pane_text" or "panetext":
                s.ColPaneText = value;
                break;
            case "col_muted" or "mutedtext":
                s.ColMuted = value;
                break;
            case "col_accent" or "accenttext":
                s.ColAccent = value;
                break;
            case "col_highlight" or "col_emerald" or "highlighttext" or "emeraldtext":
                s.ColHighlight = value;
                break;
            case "col_darkpurple" or "col_dark_purple" or "darkpurple":
                s.ColDarkPurple = value;
                break;
            case "col_darkpurplehighlight" or "col_dark_purple_highlight" or "darkpurplehighlight":
                s.ColDarkPurpleHighlight = value;
                break;
            case "col_darkpurpleshadow" or "col_dark_purple_shadow" or "darkpurpleshadow":
                s.ColDarkPurpleShadow = value;
                break;
            case "col_bordi" or "col_border_inact" or "inactiveborder":
                s.ColInactiveBorder = value;
                break;
            case "col_search_bg": s.ColSearchBg = value; break;
            case "col_search_fg": s.ColSearchFg = value; break;
            case "col_search_current_bg": s.ColSearchCurrentBg = value; break;
            case "col_search_current_fg": s.ColSearchCurrentFg = value; break;
            case "col_mark" or "markcolor":
                s.ColMark = value;
                break;
            case "col_mark_fg" or "col_markfg" or "markforeground":
                s.ColMarkFg = value;
                break;
            case "col_log_bg" or "logbackground":
                s.ColLogBg = value;
                break;
            case "col_fn_bg" or "functionbarbackground":
                s.ColFunctionBg = value;
                break;
            case "col_fn_btn_bg" or "fnbtnbackground":
                s.ColFnBtnBg = value;
                break;
            case "col_fn_btn_fg" or "fnbtnforeground":
                s.ColFnBtnFg = value;
                break;

            // ファンクションキーの表示名は、この switch の前で AppSettings.TryGetFunctionLabelSlot が扱う。
        }
    }

    /// <summary>
    /// 1/true/yes/on と 0/false/no/off を真偽値にする。どれでもなければ既定値を返す。
    /// </summary>
    private static bool ParseBool(string value, bool defaultValue)
    {
        var v = value.Trim().ToLowerInvariant();
        return v switch
        {
            "1" or "true" or "yes" or "on" => true,
            "0" or "false" or "no" or "off" => false,
            _ => defaultValue
        };
    }

}
