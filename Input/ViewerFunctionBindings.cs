using System.Text.RegularExpressions;
using System.Windows.Input;

namespace MirrorFlux.Input;

/// <summary>
/// ビューアのキー割り当て1件。ファンクションキーは番号で、動画ビューアの他のキーはInputKeyで表す。
/// </summary>
public sealed record ViewerFunctionBinding(string Viewer, int Number, ModifierKeys Modifiers, string Command, Key? InputKey = null);
/// <summary>
/// ビューアの操作1件。INIでの名前、既定の番号、Shift付きか、ファンクションキー欄の表示名を持つ。
/// </summary>
public sealed record ViewerFunctionAction(string Command, int Number, bool Shift, string Label);

/// <summary>
/// ビューアごとの操作一覧と、INIのキー割り当ての読み書き・解決。操作は名前で識別し、どのファンクションキーに割り当てるかとは切り離す。
/// </summary>
public static class ViewerFunctionBindings
{
    public static IReadOnlyList<string> Viewers { get; } = Array.AsReadOnly(new[] { "text", "hex", "graphic", "pdf", "music", "video" });
    /// <summary>
    /// ビューアごとの操作と既定の番号。番号13以上はファンクションキーではなく操作の識別に使う。
    /// </summary>
    private static readonly Dictionary<string, ViewerFunctionAction[]> Actions = new()
    {
        ["text"] =
        [
            new("help", 1, false, "ヘルプ"),
            new("search", 2, false, "検索"),
            new("findnext", 3, false, "次検索"),
            new("findprevious", 4, false, "前検索"),
            new("encoding", 5, false, "文字コード"),
            new("hex", 6, false, "BIN表示"),
            new("jump", 7, false, "行移動"),
            new("linenumbers", 8, false, "行番号"),
            new("reload", 9, false, "再読込"),
            new("wrap", 10, false, "折り返し"),
            new("minimap", 11, false, "ミニマップ"),
            new("close", 12, false, "終了"),
            new("follow", 2, true, "末尾追従"),
            new("regex", 3, true, "正規表現"),
            new("matchcase", 4, true, "大小区別"),
            new("previousfile", 5, true, "前ファイル"),
            new("nextfile", 6, true, "次ファイル"),
            new("copy", 8, true, "コピー"),
            new("syntax", 10, true, "Syntax"),
        ],
        ["hex"] =
        [
            new("help", 1, false, "ヘルプ"),
            new("search", 2, false, "検索"),
            new("findnext", 3, false, "次検索"),
            new("findprevious", 4, false, "前検索"),
            new("encoding", 5, false, "文字コード"),
            new("text", 6, false, "TEXT表示"),
            new("jump", 7, false, "アドレス"),
            new("reload", 9, false, "再読込"),
            new("minimap", 11, false, "ミニマップ"),
            new("close", 12, false, "終了"),
            new("follow", 2, true, "末尾追従"),
            new("bytesearch", 5, true, "バイト検索"),
            new("rowwidth", 6, true, "行幅"),
            new("endian", 7, true, "Endian"),
            new("copybytes", 8, true, "バイトコピー"),
            new("copytext", 9, true, "文字列コピー"),
        ],
        ["graphic"] =
        [
            new("help", 1, false, "ヘルプ"),
            new("fit", 2, false, "フィット"),
            new("previous", 3, false, "前画像"),
            new("next", 4, false, "次画像"),
            new("photoshop", 5, false, "Photoshop"),
            new("flip", 6, false, "反転"),
            new("rotate", 7, false, "回転"),
            new("background", 8, false, "背景"),
            new("reload", 9, false, "再読込"),
            new("associated", 10, false, "関連"),
            new("close", 12, false, "終了"),
        ],
        ["pdf"] =
        [
            new("help", 1, false, "ヘルプ"),
            new("fit", 2, false, "フィット"),
            new("previous", 3, false, "前頁"),
            new("next", 4, false, "次頁"),
            new("fitwidth", 5, false, "幅合せ"),
            new("zoomout", 6, false, "縮小"),
            new("rotate", 7, false, "回転"),
            new("background", 8, false, "背景"),
            new("reload", 9, false, "再読込"),
            new("associated", 10, false, "関連"),
            new("close", 12, false, "終了"),
        ],
        ["video"] =
        [
            new("help", 1, false, "ヘルプ"),
            new("playpause", 2, false, "再生/休止"),
            new("backward1", 13, false, "1秒戻る"),
            new("forward1", 14, false, "1秒進む"),
            new("backward", 3, false, "10秒戻る"),
            new("forward", 4, false, "10秒進む"),
            new("volumedown", 5, false, "音量－"),
            new("volumeup", 6, false, "音量＋"),
            new("mute", 7, false, "消音"),
            new("stop", 8, false, "停止"),
            new("beginning", 9, false, "先頭"),
            new("close", 12, false, "終了"),
        ],
        ["music"] =
        [
            new("help", 1, false, "ヘルプ"),
            new("playpause", 2, false, "再生/停止"),
            new("previous", 3, false, "前曲"),
            new("next", 4, false, "次曲"),
            new("playall", 5, false, "連続再生"),
            new("repeat", 6, false, "リピート"),
            new("shuffle", 7, false, "シャッフル"),
            new("stop", 8, false, "停止"),
            new("loops", 9, false, "ループ"),
            new("forward", 10, false, "早送り"),
            new("registers", 11, false, "レジスタ"),
            new("close", 12, false, "終了"),
            new("titles", 13, false, "曲名/ファイル名"),
        ],
    };
    /// <summary>
    /// 「>KEY_F1 -s-- &amp;command;」形式の行。修飾キーの欄は s/a/c/t と「-」「_」で書く。
    /// </summary>
    private static readonly Regex BindingPattern = new(
        @"^>KEY_F([1-9]|1[0-2])\s+([sSaAcCtT_-]{1,8})\s+&([a-zA-Z0-9_]+);?\s*(?:#.*)?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// 動画ビューア用の行。ファンクションキー以外の任意のキー名も書ける。
    /// </summary>
    private static readonly Regex VideoBindingPattern = new(
        @"^>(KEY_[A-Za-z0-9_\\-]+)\s+([sSaAcCtT_-]{1,8})\s+&([a-zA-Z0-9_]+);?\s*(?:#.*)?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// ビューアの操作を名前で探す。「none」は何もしない操作として返す。
    /// </summary>
    public static ViewerFunctionAction? FindAction(string viewer, string command) =>
        command == "none" ? new("none", 0, false, "") :
        Actions.TryGetValue(viewer, out var actions) ? actions.FirstOrDefault(a => a.Command == command) : null;

    /// <summary>
    /// Shiftなしの操作を番号で探す。番号は操作の識別用なので、F12を超える値もある。
    /// </summary>
    public static ViewerFunctionAction? FindActionByNumber(string viewer, int number) =>
        Actions.TryGetValue(viewer, out var actions) ? actions.FirstOrDefault(a => a.Number == number && !a.Shift) : null;

    /// <summary>
    /// 全ビューアの既定の割り当てを作る。Shift付きの既定はテキストとバイナリのビューアだけに持たせる。
    /// </summary>
    public static IEnumerable<ViewerFunctionBinding> CreateDefaults()
    {
        // 各ビューアのF1～F12を、Shiftなし・Shift付きの両方について並べる。
        foreach (var viewer in Viewers)
        foreach (var modifiers in new[] { ModifierKeys.None, ModifierKeys.Shift })
        for (int number = 1; number <= 12; number++)
        {
            bool shift = modifiers == ModifierKeys.Shift && viewer is "text" or "hex";
            var action = Actions[viewer].FirstOrDefault(a => a.Number == number && a.Shift == shift);
            yield return new(viewer, number, modifiers, action?.Command ?? "none");
        }
        // 動画ビューアのファンクションキー以外の既定キー。
        foreach (var (key, command) in new (Key, string)[] {
            (Key.Space, "playpause"), (Key.Left, "backward1"), (Key.Right, "forward1"),
            (Key.Down, "volumedown"), (Key.Up, "volumeup"), (Key.M, "mute"),
            (Key.Home, "beginning"), (Key.Escape, "close"), (Key.Return, "close") })
            yield return new("video", 0, ModifierKeys.None, command, key);
        yield return new("video", 0, ModifierKeys.Shift, "backward", Key.Left);
        yield return new("video", 0, ModifierKeys.Shift, "forward", Key.Right);
        // サウンドビューアのファンクションキー以外の既定キー（他のキーは固定）。
        yield return new("music", 0, ModifierKeys.None, "titles", Key.T);
    }

    /// <summary>
    /// INIの1行をビューアのキー割り当てとして読む。知らない操作名の行は受け付けない。
    /// </summary>
    public static bool TryParse(string viewer, string line, out ViewerFunctionBinding? binding)
    {
        binding = null;
        if (!Actions.ContainsKey(viewer)) return false;
        // 動画とサウンドのビューアは任意のキーを受け付け、ファンクションキーなら番号、それ以外はキーとして記録する。
        if (viewer is "video" or "music")
        {
            var video = VideoBindingPattern.Match(line.Trim());
            if (!video.Success || !IniKeyParser.TryParseKey(video.Groups[1].Value, out var key) || key == Key.None) return false;
            string action = video.Groups[3].Value.ToLowerInvariant();
            if (FindAction(viewer, action) == null) return false;
            bool function = key is >= Key.F1 and <= Key.F12;
            binding = new(viewer, function ? key - Key.F1 + 1 : 0,
                IniKeyParser.ParseModifiers(video.Groups[2].Value), action, function ? null : key);
            return true;
        }
        // 他のビューアはF1～F12だけを受け付ける。
        var match = BindingPattern.Match(line.Trim());
        if (!match.Success) return false;
        string command = match.Groups[3].Value.ToLowerInvariant();
        if (FindAction(viewer, command) == null) return false;
        binding = new(viewer, int.Parse(match.Groups[1].Value), IniKeyParser.ParseModifiers(match.Groups[2].Value), command);
        return true;
    }

    /// <summary>
    /// キー割り当てをINIの1行に書き出す。ファンクションキーは「>KEY_Fn -sac &amp;command;」の形にする。
    /// </summary>
    public static string Format(ViewerFunctionBinding binding)
    {
        if (binding.InputKey is { } key)
            return IniKeyParser.FormatLine(new Models.KeyBindingItem(key, binding.Modifiers, AppCommand.None, binding.Command));
        var modifiers = binding.Modifiers;
        return $">KEY_F{binding.Number} -{(modifiers.HasFlag(ModifierKeys.Shift) ? 's' : '-')}{(modifiers.HasFlag(ModifierKeys.Alt) ? 'a' : '-')}{(modifiers.HasFlag(ModifierKeys.Control) ? 'c' : '-')} &{binding.Command};";
    }

    /// <summary>
    /// 押されたファンクションキーと修飾キーから、ビューアの操作を決める。後に書かれた割り当てを優先する。
    /// </summary>
    public static ViewerFunctionAction? Resolve(IEnumerable<ViewerFunctionBinding> bindings, string viewer, int number, ModifierKeys modifiers)
    {
        var binding = bindings.LastOrDefault(b => b.Viewer == viewer && b.Number == number && b.Modifiers == modifiers);
        return binding == null ? null : FindAction(viewer, binding.Command);
    }
}
