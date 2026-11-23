using System.Windows.Input;
using MirrorFlux.Models;

namespace MirrorFlux.Input;

/// <summary>
/// ファイル一覧画面のファンクションキーの既定の割り当てと、キー・ボタン共通のコマンド解決、ボタンの表示名。
/// </summary>
public static class FunctionKeyBindings
{
    /// <summary>
    /// F1～F12の既定の割り当てを作る。
    /// </summary>
    public static IEnumerable<KeyBindingItem> CreateDefaults()
    {
        (AppCommand Command, string Name)[] commands =
        [
            (AppCommand.Help, "help"), (AppCommand.ToggleMark, "mark"),
            (AppCommand.View, "view"), (AppCommand.FileRename, "filerename"),
            (AppCommand.FileCopy, "filecopy"), (AppCommand.FileMove, "filemove"),
            (AppCommand.MakeDir, "mkdir"), (AppCommand.Delete, "filedelete"),
            (AppCommand.Sort, "sort"), (AppCommand.JumpList, "jumplist"),
            (AppCommand.Refresh, "refresh"), (AppCommand.Exit, "exit")
        ];
        return commands.Select((item, index) => new KeyBindingItem(
            Key.F1 + index, ModifierKeys.None, item.Command, item.Name));
    }

    /// <summary>
    /// ファンクションキーの番号と修飾キーから実行するコマンドを決める。修飾キーなしで割り当てがなければ既定を使う。
    /// </summary>
    public static bool TryResolve(IEnumerable<KeyBindingItem> bindings, int number,
        ModifierKeys modifiers, out ResolvedCommand command)
    {
        // 後に書かれた割り当てを優先して探す。
        command = default;
        if (number is < 1 or > 12) return false;
        var key = Key.F1 + number - 1;
        var binding = bindings.LastOrDefault(b => b.Key == key && b.Modifiers == modifiers);
        binding ??= modifiers == ModifierKeys.None ? CreateDefaults().ElementAt(number - 1) : null;
        if (binding == null) return false;
        // FunctionKeyコマンドが割り当てられていても、自分自身を呼び直して無限に繰り返さないよう何もしないコマンドにする。
        command = new ResolvedCommand(binding.Command == AppCommand.FunctionKey ? AppCommand.None : binding.Command, 0, modifiers, binding.ExternalCommand,
            binding.FollowingCommands?.Select(c => new ResolvedCommand(c.Command == AppCommand.FunctionKey ? AppCommand.None : c.Command)).ToArray());
        return true;
    }

    /// <summary>
    /// ファンクションキー欄のボタンに出す名前を返す。
    /// </summary>
    public static string Label(AppCommand command) => command switch
    {
        AppCommand.Help => "ヘルプ", AppCommand.ToggleMark => "マーク",
        AppCommand.MarkAll => "全マーク", AppCommand.MarkClear => "マーク解除", AppCommand.Archive => "圧縮", AppCommand.IncrementalSearch => "絞り込み",
        AppCommand.View => "表示",
        AppCommand.FileCopy or AppCommand.FileCopyCheck => "コピー",
        AppCommand.FileMove or AppCommand.FileMoveCheck => "移動", AppCommand.Delete => "削除",
        AppCommand.MakeDir => "作成", AppCommand.FileRename => "名前変更", AppCommand.JumpList => "パス選択",
        AppCommand.VideoViewer => "動画", AppCommand.TextViewer => "TEXT", AppCommand.HexViewer => "BIN表示", AppCommand.ExtExecWindows => "関連付け",
        AppCommand.ExternalProgram => "外部実行", AppCommand.Refresh => "更新", AppCommand.Sort => "ソート", AppCommand.Exit => "終了", AppCommand.OperationCancel => "中止",
        AppCommand.None or AppCommand.FunctionKey => "", _ => "実行"
    };
}
