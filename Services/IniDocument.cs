using System.Globalization;
using System.Text;
using MirrorFlux.Models;

namespace MirrorFlux.Services;

/// <summary>
/// 利用者が手で編集した INI を、セクションを見分けながら書き換える。アプリが管理する値だけを更新し、ほかの行はそのまま残す。
/// </summary>
internal static class IniDocument
{
    /// <summary>
    /// 行が「[名前]」のセクション見出しなら、小文字にした名前を返す。
    /// </summary>
    public static bool TrySection(string line, out string section)
    {
        line = line.Trim();
        section = "";
        if (!line.StartsWith('[') || !line.EndsWith(']')) return false;
        section = line[1..^1].Trim().ToLowerInvariant();
        return true;
    }

    /// <summary>
    /// そのセクションに書かれた変数を設定として受け付けるか。古い INI ではキー割り当てのセクションの後に変数が置かれていることがあるため、「keys.」のセクションでも受け付ける。
    /// </summary>
    public static bool AllowsVariable(string section, string key) => section switch
    {
        "theme" => key.StartsWith("theme_", StringComparison.OrdinalIgnoreCase),
        "gme" => key.ToLowerInvariant() is "mastervolume" or "pdxsearchpaths" or "nd6-ndsif" or "nd6_ndsif" or "nd6ndsif" or "nd7-ndsif" or "nd7_ndsif" or "nd7ndsif" or "nd8-ndsif" or "nd8_ndsif" or "nd8ndsif",
        "ndsif" => key.ToLowerInvariant() is "nd6-ndsif" or "nd6_ndsif" or "nd6ndsif" or "nd7-ndsif" or "nd7_ndsif" or "nd7ndsif" or "nd8-ndsif" or "nd8_ndsif" or "nd8ndsif",
        "state" => RuntimeKeys.Contains(CanonicalRuntimeKey(key)),
        "" or "settings" or "general" => true,
        _ => section.StartsWith("keys.")
    };

    private static readonly HashSet<string> RuntimeKeys = new(StringComparer.OrdinalIgnoreCase)
    { "leftPath", "rightPath", "logLevel", "sortMode", "leftSort", "rightSort" };

    /// <summary>
    /// 実行中に変わる値の別名を、正式な名前にそろえる。
    /// </summary>
    private static string CanonicalRuntimeKey(string key) => key.ToLowerInvariant() switch
    {
        "pathl" => "leftPath", "pathr" => "rightPath",
        "log_level" => "logLevel", "sort" or "sort_mode" => "sortMode",
        "leftsortmode" or "sortl" or "left_sort" => "leftSort",
        "rightsortmode" or "sortr" or "right_sort" => "rightSort", _ => key
    };

    /// <summary>
    /// 終了時に保存する、実行中に変わる値（パネルのパス、ログの詳しさ、並び順）の一覧。
    /// </summary>
    public static Dictionary<string, string> RuntimeValues(AppSettings settings) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["leftPath"] = settings.LeftPath, ["rightPath"] = settings.RightPath,
        ["logLevel"] = settings.LogLevel.ToString(CultureInfo.InvariantCulture),
        ["sortMode"] = settings.SortMode, ["leftSort"] = settings.LeftSortMode, ["rightSort"] = settings.RightSortMode
    };

    /// <summary>
    /// 実行中に変わる値が書かれた行の番号を、正式な名前ごとに返す。同じ名前が複数あれば最後の行を使う。
    /// </summary>
    private static Dictionary<string, int> Assignments(IReadOnlyList<string> lines)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        string section = "";
        for (int i = 0; i < lines.Count; i++)
        {
            // セクション見出し・空行・コメント行を除き、「%名前 値」「名前=値」の名前を取り出す。
            string line = lines[i].Trim();
            if (TrySection(line, out var header)) { section = header; continue; }
            if (line.Length == 0 || line[0] is '#' or ';' or '>' or '.') continue;
            line = line.TrimStart('%');
            int split = line.IndexOfAny([' ', '\t', '=']);
            string key = split < 0 ? line : line[..split];
            if (RuntimeKeys.Contains(CanonicalRuntimeKey(key)) && AllowsVariable(section, key)) result[CanonicalRuntimeKey(key)] = i;
        }
        return result;
    }

    /// <summary>
    /// 実行中に変わった値を現在の INI の内容へ反映した文字列を返す。起動後に利用者が書き換えた値は上書きしない。
    /// </summary>
    public static string MergeRuntime(string original, string current,
        IReadOnlyDictionary<string, string> baseline, AppSettings settings)
    {
        // 改行コードを保ち、使われなくなった設定行を除く。
        string newline = current.Contains("\r\n") ? "\r\n" : "\n";
        var originalLines = original.Replace("\r\n", "\n").Split('\n');
        var lines = current.Replace("\r\n", "\n").Split('\n').ToList();
        lines.RemoveAll(IsObsoleteSetting);
        var before = Assignments(originalLines);
        var now = Assignments(lines);
        var additions = new List<string>();
        // 起動時から変わった値だけを書き換える。起動時の行と今の行が違えば、利用者が編集・削除したので触らない。
        foreach (var (key, value) in RuntimeValues(settings))
        {
            if (baseline.TryGetValue(key, out var initial) && initial == value) continue;
            string? oldLine = before.TryGetValue(key, out int previousIndex) ? originalLines[previousIndex] : null;
            string? newLine = now.TryGetValue(key, out int currentIndex) ? lines[currentIndex] : null;
            if (oldLine != newLine) continue;
            if (newLine != null) lines[currentIndex] = ReplaceValue(newLine, key, value);
            else additions.Add($"%{key}  {value}");
        }
        // 末尾に残った使われない空の「[keys.file]」だけを除く。中身のあるキー割り当てのセクションには触らない。
        int last = lines.FindLastIndex(line => !string.IsNullOrWhiteSpace(line));
        if (last >= 0 && lines[last].Trim().Equals("[keys.file]", StringComparison.OrdinalIgnoreCase)) lines.RemoveAt(last);
        // まだ行のない値は末尾の [state] セクションに加える。
        if (additions.Count > 0)
        {
            lines.Add("[state]");
            lines.AddRange(additions);
            lines.Add("");
        }
        return string.Join(newline, lines);
    }

    /// <summary>
    /// 手でそろえた桁を保つ。同じ名前の行なら、名前と区切りの空白を残して値だけを置き換える。
    /// </summary>
    private static string ReplaceValue(string line, string key, string value)
    {
        var match = System.Text.RegularExpressions.Regex.Match(line, @"^\s*%([^\s=]+)[ \t]+");
        return match.Success && match.Groups[1].Value.Equals(key, StringComparison.OrdinalIgnoreCase)
            ? match.Value + value
            : $"%{key}  {value}";
    }

    /// <summary>
    /// 使われなくなった設定（fmcore やウィンドウの大きさ）の行か。
    /// </summary>
    private static bool IsObsoleteSetting(string line)
    {
        // 「名前=値」の形は fmcore だけを対象にする。
        string text = line.TrimStart();
        if (!text.StartsWith('%'))
        {
            int equals = text.IndexOf('=');
            return equals > 0 && text[..equals].Trim().Equals("fmcore", StringComparison.OrdinalIgnoreCase);
        }
        // 「%名前 値」の形は名前で判定する。
        int end = text.IndexOfAny([' ', '\t', '=']);
        string key = (end < 0 ? text[1..] : text[1..end]).ToLowerInvariant();
        return key is "fmcore" or "winwidth" or "winheight" or "win_w" or "win_h" or "width" or "height";
    }
}
