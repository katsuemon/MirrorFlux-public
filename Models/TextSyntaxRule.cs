using System.IO;
using System.Text.RegularExpressions;
namespace MirrorFlux.Models;

/// <summary>
/// ファイル名またはパスのワイルドカードで色分けの種類を指定する規則。書いた順に評価し、拡張子ごとの指定より優先する。
/// </summary>
public sealed record TextSyntaxRule(string Pattern, string Syntax)
{
    /// <summary>
    /// INIの値「パターン 種類」を読む。パターンは空白を含むなら引用符で囲む。
    /// </summary>
    public static bool TryParse(string value, out TextSyntaxRule? rule)
    {
        rule = null;
        var match = Regex.Match(value, """^\s*(?:"(?<path>[^"\r\n]+)"|(?<path>[^\s"]+))\s+(?<syntax>[a-zA-Z0-9_-]+)\s*$""", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        if (!match.Success) return false;
        rule = new(match.Groups["path"].Value, match.Groups["syntax"].Value.ToLowerInvariant()); return true;
    }
    public string ToIniValue() => $"\"{Pattern}\" {Syntax}";
    /// <summary>
    /// パスがパターンに合うか調べる。「\」を含まないパターンはファイル名だけと比べ、相対パスはEXEのフォルダーを基準にする。
    /// 「*」「?」はフォルダーの区切りをまたがない。
    /// </summary>
    public bool Matches(string path)
    {
        // 環境変数を展開し、区切り文字を「\」にそろえる。
        string pattern = Environment.ExpandEnvironmentVariables(Pattern).Replace('/', '\\');
        string candidate = path.Replace('/', '\\');
        if (!pattern.Contains('\\')) candidate = Path.GetFileName(candidate);
        else if (!Path.IsPathRooted(pattern)) pattern = Path.Combine(MirrorFlux.Services.SettingsStore.AppDirectory, pattern);
        // ワイルドカードを正規表現に置き換えて比べる。
        string expression = "^" + Regex.Escape(pattern).Replace(@"\*", @"[^\\]*").Replace(@"\?", @"[^\\]") + "$";
        return Regex.IsMatch(candidate, expression, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    }
}
