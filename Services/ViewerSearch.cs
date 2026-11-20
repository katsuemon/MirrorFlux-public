using System.Globalization;
using System.Text.RegularExpressions;
namespace MirrorFlux.Services;

/// <summary>
/// 検索で見つかった位置と長さ。
/// </summary>
public readonly record struct ViewerMatch(int Offset, int Length);
/// <summary>
/// ビューアーの検索。テキストの文字列・正規表現検索、バイト列の検索、アドレス指定の解釈を行う。
/// </summary>
public static class ViewerSearch
{
    /// <summary>
    /// テキストから一致する箇所をすべて探す。正規表現は 200 ミリ秒で打ち切り、長さ0の一致は除く。
    /// </summary>
    public static List<ViewerMatch> Text(string text, string query, bool regex, bool matchCase, CancellationToken token = default)
    {
        var results = new List<ViewerMatch>();
        if (query.Length == 0) return results;
        // 正規表現で探す。
        if (regex)
        {
            var pattern = new Regex(query, RegexOptions.CultureInvariant | (matchCase ? RegexOptions.None : RegexOptions.IgnoreCase), TimeSpan.FromMilliseconds(200));
            foreach (Match match in pattern.Matches(text))
            {
                token.ThrowIfCancellationRequested();
                if (match.Length > 0) results.Add(new(match.Index, match.Length));
            }
        }
        // 文字列として、重ならないように前から順に探す。
        else
        {
            int position = 0;
            while (position <= text.Length - query.Length)
            {
                token.ThrowIfCancellationRequested();
                int next = text.IndexOf(query, position, matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
                if (next < 0) break;
                results.Add(new(next, query.Length)); position = next + query.Length;
            }
        }
        return results;
    }
    /// <summary>
    /// 「89 50 ?? 47」のような16進のバイト列を読む。「??」は任意の1バイトを表す null にする。
    /// </summary>
    public static byte?[] ParseBytes(string query)
    {
        string value = query.Replace("0x", "", StringComparison.OrdinalIgnoreCase).Replace(" ", "").Replace("\t", "").Replace("-", "");
        if (value.Length == 0) return [];
        if (value.Length % 2 != 0) throw new FormatException("バイト列は2桁ずつ指定してください (例: 89 50 ?? 47)。");
        var bytes = new byte?[value.Length / 2];
        for (int i = 0; i < bytes.Length; i++) bytes[i] = value.Substring(i * 2, 2) == "??" ? null : byte.Parse(value.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return bytes;
    }
    /// <summary>
    /// データから、任意バイトを含むパターンに一致する箇所をすべて探す。
    /// </summary>
    public static List<ViewerMatch> Bytes(byte[] data, byte?[] pattern, CancellationToken token = default)
    {
        var results = new List<ViewerMatch>();
        if (pattern.Length == 0) return results;
        // 4096 バイトごとに中止を確かめながら、1バイトずつずらして比べる。
        for (int i = 0; i <= data.Length - pattern.Length; i++)
        {
            if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
            bool found = true;
            for (int j = 0; j < pattern.Length; j++) if (pattern[j] is byte value && data[i + j] != value) { found = false; break; }
            if (found) results.Add(new(i, pattern.Length));
        }
        return results;
    }
    /// <summary>
    /// アドレスの入力を読む。既定は16進で、「0x」「$」を付けてもよい。「d:」を付けると10進。ファイルの範囲外は受け付けない。
    /// </summary>
    public static int Address(string input, int length)
    {
        string value = input.Trim(); bool dec = value.StartsWith("d:", StringComparison.OrdinalIgnoreCase);
        if (dec) value = value[2..];
        else if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) value = value[2..];
        else if (value.StartsWith('$')) value = value[1..];
        long address = long.Parse(value, dec ? NumberStyles.Integer : NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        if (address < 0 || address >= Math.Max(1, length)) throw new FormatException("ファイルの範囲内のアドレスを指定してください。");
        return (int)address;
    }
}
