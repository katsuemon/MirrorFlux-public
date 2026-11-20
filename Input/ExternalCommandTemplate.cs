using System.Text;

namespace MirrorFlux.Input;

/// <summary>
/// INIに書いた外部プログラムのコマンド行を、実行ファイルと引数に分けたもの。%markfiles でマークしたファイルを渡せる。
/// </summary>
internal sealed record ExternalCommandTemplate(string Text, string Executable, IReadOnlyList<string> Arguments)
{
    internal static bool IsMarkedFiles(string value) => value.Equals("%markfiles", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// コマンド行を引数に分解する。引用符と「\」の扱いはWindowsのコマンドライン規則に合わせ、行末の「;」「#」以降はコメントとして無視する。
    /// </summary>
    internal static bool TryParse(string text, out ExternalCommandTemplate? command)
    {
        command = null;
        var tokens = new List<string>();
        var token = new StringBuilder();
        bool quoted = false, started = false;
        int end = text.Length;
        for (int i = 0; i < text.Length;)
        {
            // 制御文字や改行を含む行は受け付けない。
            char c = text[i];
            if (c is '\0' or '\r' or '\n') return false;
            // 引用符の外の「;」と、行頭か空白の直後の「#」からはコメントとする。「;」の後にコメント以外が続く行は受け付けない。
            if (!quoted && (c == ';' || c == '#' && (i == 0 || char.IsWhiteSpace(text[i - 1]))))
            {
                if (c == ';')
                {
                    string rest = text[(i + 1)..].TrimStart();
                    if (rest.Length > 0 && !rest.StartsWith('#')) return false;
                }
                end = i;
                break;
            }
            // 引用符の外の空白で引数を区切る。
            if (!quoted && char.IsWhiteSpace(c))
            {
                if (started) { tokens.Add(token.ToString()); token.Clear(); started = false; }
                i++; continue;
            }
            started = true;
            // 引用符の直前の「\」は2個で1個になり、奇数個なら引用符そのものを表す。それ以外の「\」はそのまま。
            if (c == '\\')
            {
                int start = i;
                while (i < text.Length && text[i] == '\\') i++;
                int slashes = i - start;
                if (i < text.Length && text[i] == '"')
                {
                    token.Append('\\', slashes / 2);
                    if (slashes % 2 != 0) token.Append('"');
                    else quoted = !quoted;
                    i++;
                }
                else token.Append('\\', slashes);
                continue;
            }
            // 引用符は引用の開始・終了として扱い、他の文字は引数に加える。
            if (c == '"') quoted = !quoted;
            else token.Append(c);
            i++;
        }
        // 引用符が閉じていない行や、実行ファイルが空・「&」始まり・%markfiles の行は受け付けない。
        if (quoted) return false;
        if (started) tokens.Add(token.ToString());
        if (tokens.Count == 0 || string.IsNullOrWhiteSpace(tokens[0]) || tokens[0].StartsWith('&') || IsMarkedFiles(tokens[0])) return false;
        // %markfiles は引数1個分をまるごと占める場合だけ認める。パスを文字列へ埋め込むと、もう一度コマンドとして解釈し直す必要が出るため。
        if (tokens.Skip(1).Any(t => t.Contains("%markfiles", StringComparison.OrdinalIgnoreCase) && !IsMarkedFiles(t))) return false;
        command = new(text[..end].Trim(), tokens[0], tokens.Skip(1).ToArray());
        return true;
    }
}
