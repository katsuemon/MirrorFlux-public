using System.Text;
using MirrorFlux.Models;
using MirrorFlux.Input;

namespace MirrorFlux.Services;

/// <summary>
/// 設定を新しい INI（mf.ini）の文字列に書き出す。
/// </summary>
internal static class SettingsIniWriter
{
    /// <summary>
    /// すべての設定を、見出しコメント付きの INI の文字列にする。
    /// </summary>
    public static string Serialize(AppSettings settings)
    {
        // 冒頭の説明とパス設定。
        var sb = new StringBuilder();
        sb.AppendLine("# mf.ini - 設定ファイル");
        sb.AppendLine("# 定義ファイル形式 (%変数名 値)");
        sb.AppendLine();
        sb.AppendLine("# [ パス設定 ]");
        sb.AppendLine($"%leftPath\t{settings.LeftPath}");
        sb.AppendLine($"%rightPath\t{settings.RightPath}");
        sb.AppendLine($"%saveState\t{(settings.SaveState ? "1" : "0")}");
        sb.AppendLine();
        // ウィンドウ・表示設定。
        sb.AppendLine("# [ ウィンドウ・表示設定 ]");
        sb.AppendLine($"%fontFamily\t{settings.FontFamily}");
        sb.AppendLine($"%fontSize\t{settings.FontSize:F0}");
        sb.AppendLine($"%fileRows\t{settings.FileRows}");
        sb.AppendLine($"%logRows\t{settings.LogRows}");
        sb.AppendLine($"%logLevel\t{settings.LogLevel}");
        sb.AppendLine($"%masterVolume\t{settings.MasterVolumePercent}");
        sb.AppendLine();
        // 動作・ファイル一覧の設定。パネルごとの並び順は指定があるときだけ書く。
        sb.AppendLine("# [ 動作・一覧設定 ]");
        sb.AppendLine($"%showIcons\t{(settings.ShowIcons ? "1" : "0")}");
        sb.AppendLine($"%showHidden\t{(settings.ShowHidden ? "1" : "0")}");
        sb.AppendLine($"%sortMode\t{settings.SortMode}");
        if (!string.IsNullOrWhiteSpace(settings.LeftSortMode))
        {
            sb.AppendLine($"%leftSort\t{settings.LeftSortMode}");
        }
        if (!string.IsNullOrWhiteSpace(settings.RightSortMode))
        {
            sb.AppendLine($"%rightSort\t{settings.RightSortMode}");
        }
        sb.AppendLine();
        // テーマ色。基本の色、用途の決まった色、それ以外の色の順に書く。
        sb.AppendLine("# [ テーマ設定 ]");
        foreach (var item in ThemeKeyCatalog.Core)
            AppendThemeLine(sb, item.Key, item.Get(settings));
        foreach (string key in ThemeKeyCatalog.Purposes.Keys.Where(k => !ThemeKeyCatalog.IsCoreKey(k)))
        {
            if (settings.ThemeColors.TryGetValue(key, out string? val) && !string.IsNullOrEmpty(val))
                AppendThemeLine(sb, key, val);
        }
        foreach (var pair in settings.ThemeColors.Where(pair => !ThemeKeyCatalog.IsCoreKey(pair.Key) && !ThemeKeyCatalog.Purposes.ContainsKey(pair.Key)).OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            AppendThemeLine(sb, pair.Key, pair.Value);
        sb.AppendLine();
        // ファンクションキーの表示名。セクションごとに省くキーは書かない。
        foreach (var section in AppSettings.FunctionLabelSections)
        {
            sb.AppendLine($"# [ {section.Title} ]");
            string[] labels = section.Labels(settings);
            for (int number = 1; number <= 12; number++)
                if (number != section.SkippedKey) sb.AppendLine($"%{section.Prefix}{number:00}\t{labels[number - 1]}");
            sb.AppendLine();
        }
        // テキストビューアーの構文ハイライト。
        sb.AppendLine("# [ テキストビューア 構文ハイライト ]");
        sb.AppendLine($"%tvHighlight\t{(settings.TVHighlight ? 1 : 0)}");
        sb.AppendLine($"%tvSyntaxPath\t{settings.TVSyntaxPath}");
        sb.AppendLine($"%tvColorComment\t{settings.TVColorComment}");
        sb.AppendLine($"%tvColorKeyword\t{settings.TVColorKeyword}");
        sb.AppendLine($"%tvColorString\t{settings.TVColorString}");
        sb.AppendLine($"%tvColorNumber\t{settings.TVColorNumber}");
        sb.AppendLine($"%tvColorKey\t{settings.TVColorKey}");
        sb.AppendLine($"%tvColorUrl\t{settings.TVColorUrl}");
        sb.AppendLine($"%tvColorSection\t{settings.TVColorSection}");
        sb.AppendLine($"%tvColorScenarioVoice\t{settings.TVColorScenarioVoice}");
        foreach (var rule in settings.TVSyntaxRules) sb.AppendLine($"%tvSyntaxRule\t{rule.ToIniValue()}");
        foreach (var prefix in settings.TVSyntaxPrefixes) sb.AppendLine($"%tvSyntaxPrefix\t{new TextSyntaxRule(prefix.Key, prefix.Value).ToIniValue()}");
        foreach (var syntax in settings.TVSyntaxMappings.OrderBy(pair => pair.Key))
            sb.AppendLine($"%tvSyntax_{syntax.Key}\t{syntax.Value}");
        sb.AppendLine();
        // ジャンプリストの10か所。
        sb.AppendLine("# [ ジャンプリスト (Shift+J / &jumplist;、01〜10、環境変数・EXE基準の相対パス可) ]");
        for (int slot = 1; slot <= 10; slot++)
            sb.AppendLine($"%jumpPath{slot:00}\t{settings.JumpPaths.GetValueOrDefault(slot, "")}");
        sb.AppendLine();
        // 拡張子ごとの実行設定。
        sb.AppendLine("# [ 拡張子実行設定 (.拡張子 ---- &コマンド;) ]");
        foreach (var extBinding in settings.ExtBindings)
        {
            sb.AppendLine(IniKeyParser.FormatExtBinding(extBinding));
        }
        sb.AppendLine();
        // ファイル一覧のキー設定。英字キーをアルファベット順に書く。
        sb.AppendLine("[keys.file]");
        sb.AppendLine("# [ キー設定 (>KEY_キー名 フラグ &コマンド;) ]");
        sb.AppendLine("# フラグ: s=Shift, a=Alt, c=Ctrl, -=なし (順不同)");
        sb.AppendLine("# 英字キー（アルファベット順）");
        foreach (var binding in settings.KeyBindings.Where(binding => IsLetterKey(binding.Key))
                     .OrderBy(binding => binding.Key).ThenBy(binding => binding.Modifiers))
        {
            sb.AppendLine(IniKeyParser.FormatLine(binding));
        }
        sb.AppendLine();
        // 英字以外のキーを決まった順に書く。
        sb.AppendLine("# 特殊キー / F1〜F12: 後の定義を優先、&none; で無効化");
        sb.AppendLine("# help, mark, view, none, filecopy, filemove, mkdir, filedelete, sort, jumplist, refresh, exit");
        foreach (var binding in settings.KeyBindings.Where(binding => !IsLetterKey(binding.Key))
                     .OrderBy(binding => SpecialKeyOrder(binding.Key)).ThenBy(binding => binding.Key).ThenBy(binding => binding.Modifiers))
        {
            sb.AppendLine(IniKeyParser.FormatLine(binding));
        }
        sb.AppendLine();

        // ビューアーごとのキー設定。同じキーは最後の定義だけを書く。
        foreach (var viewer in ViewerFunctionBindings.Viewers)
        {
            sb.AppendLine($"[keys.{viewer}]");
            sb.AppendLine("# 通常: ---- / Shift: -s-- / 無効: &none; / 同じキーは最後の定義を優先");
            foreach (var binding in settings.ViewerKeyBindings.Where(b => b.Viewer == viewer)
                .GroupBy(b => (b.Number, b.InputKey, b.Modifiers)).Select(g => g.Last())
                .OrderBy(b => b.Modifiers).ThenBy(b => b.Number).ThenBy(b => b.InputKey))
                sb.AppendLine(ViewerFunctionBindings.Format(binding));
            sb.AppendLine();
        }
        // 音楽再生の設定（PDX の検索先と NanoDrive の実機出力）。
        sb.AppendLine("[GME]");
        sb.AppendLine("# MDXのPDX検索先 (同じフォルダー優先、複数は ; 区切り、相対パスはEXE基準)");
        sb.AppendLine($"%pdxSearchPaths\t{settings.PdxSearchPaths}");
        sb.AppendLine();
        sb.AppendLine("# NanoDrive 6 実機出力: true=有効 / false=無効");
        sb.AppendLine($"%ND6-NDSIF\t{(settings.Nd6Ndsif ? "true" : "false")}");
        sb.AppendLine($"%ND7-NDSIF\t{(settings.Nd7Ndsif ? "true" : "false")}");
        sb.AppendLine($"%ND8-NDSIF\t{(settings.Nd8Ndsif ? "true" : "false")}");
        sb.AppendLine();
        return sb.ToString();
    }

    /// <summary>
    /// テーマ色の1行を書く。用途の説明があれば行末にコメントで添える。
    /// </summary>
    private static void AppendThemeLine(StringBuilder builder, string key, string value)
    {
        string comment = ThemeKeyCatalog.Purposes.TryGetValue(key, out string? purpose) ? $"\t# {purpose}" : "";
        builder.AppendLine($"%theme_{key}\t{value}{comment}");
    }

    private static bool IsLetterKey(System.Windows.Input.Key key) =>
        key >= System.Windows.Input.Key.A && key <= System.Windows.Input.Key.Z;

    /// <summary>
    /// 英字以外のキーを書く順番。記号・Delete・Enter を先に、ファンクションキーを最後に番号順で並べる。
    /// </summary>
    private static int SpecialKeyOrder(System.Windows.Input.Key key) => key switch
    {
        System.Windows.Input.Key.OemMinus => 0,
        System.Windows.Input.Key.OemBackslash => 1,
        System.Windows.Input.Key.Delete => 2,
        System.Windows.Input.Key.Return => 3,
        >= System.Windows.Input.Key.F1 and <= System.Windows.Input.Key.F24 => 100 + (int)key - (int)System.Windows.Input.Key.F1,
        _ => 50
    };

}
