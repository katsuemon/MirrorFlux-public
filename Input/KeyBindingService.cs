using System.Windows.Input;

namespace MirrorFlux.Input;

/// <summary>
/// ファイル一覧画面のキーとコマンドの対応表。既定の割り当ての上にINIの割り当てを重ねる。
/// </summary>
public sealed class KeyBindingService
{
    private readonly Dictionary<(Key Key, ModifierKeys Modifiers), ResolvedCommand> _bindings = new();

    /// <summary>
    /// 既定のキー割り当てとファンクションキーの既定を登録する。
    /// </summary>
    public KeyBindingService()
    {
        // カーソル移動とパネル切り替え。
        Add(Key.Up, AppCommand.MoveUp);
        Add(Key.Down, AppCommand.MoveDown);
        Add(Key.PageUp, AppCommand.MovePageUp);
        Add(Key.PageDown, AppCommand.MovePageDown);
        Add(Key.Home, AppCommand.MoveHome);
        Add(Key.End, AppCommand.MoveEnd);
        Add(Key.Left, AppCommand.ActivateLeftPane);
        Add(Key.Right, AppCommand.ActivateRightPane);
        Add(Key.Tab, AppCommand.SwitchPane);
        Add(Key.Tab, AppCommand.SwitchPane, ModifierKeys.Shift);
        // 実行、親フォルダーへの移動、マーク。
        Add(Key.Enter, AppCommand.ExtExec);
        Add(Key.Return, AppCommand.ExtExec);
        Add(Key.Enter, AppCommand.ExtExecWindows, ModifierKeys.Shift);
        Add(Key.Return, AppCommand.ExtExecWindows, ModifierKeys.Shift);
        Add(Key.Back, AppCommand.GoParent);
        Add(Key.Space, AppCommand.ToggleMark);
        // ファイル操作、絞り込み、並び順、パス移動などの文字キー。
        Add(Key.R, AppCommand.Refresh, ModifierKeys.Control);
        Add(Key.R, AppCommand.FileRename);
        Add(Key.K, AppCommand.MakeDir);
        Add(Key.A, AppCommand.MarkAll);
        Add(Key.A, AppCommand.MarkClear, ModifierKeys.Shift);
        Add(Key.A, AppCommand.Archive, ModifierKeys.Control);
        Add(Key.I, AppCommand.IncrementalSearch);
        Add(Key.Delete, AppCommand.Delete);
        Add(Key.D, AppCommand.Delete);
        Add(Key.C, AppCommand.FileCopyCheck);
        Add(Key.M, AppCommand.FileMoveCheck);
        Add(Key.M, AppCommand.FileMove, ModifierKeys.Shift);
        Add(Key.S, AppCommand.Sort);
        Add(Key.OemMinus, AppCommand.SyncPanePath);
        Add(Key.Subtract, AppCommand.SyncPanePath);
        Add(Key.OemBackslash, AppCommand.ChangeDirectory);
        Add(Key.Oem5, AppCommand.ChangeDirectory);
        Add(Key.OemPipe, AppCommand.ChangeDirectory);
        Add(Key.J, AppCommand.ChangeDirectory);
        Add(Key.J, AppCommand.JumpList, ModifierKeys.Shift);
        // 実行中のコピー・移動・削除などの中止。
        Add(Key.Escape, AppCommand.OperationCancel);

        ApplyBindings(FunctionKeyBindings.CreateDefaults());
    }

    /// <summary>
    /// キーと修飾キーから実行するコマンドを探す。見つかったら押されていた修飾キーを添えて返す。
    /// </summary>
    public bool TryResolve(Key key, ModifierKeys modifiers, out ResolvedCommand command)
    {
        if (_bindings.TryGetValue((key, modifiers), out command))
        {
            command = command with { Modifiers = modifiers };
            return true;
        }
        // 修飾キー付きのEnterは、別のコマンドを明示的に割り当てていなければ拡張子に応じた実行として扱う。
        if (key == Key.Return && _bindings.TryGetValue((key, ModifierKeys.None), out var enter)
            && enter.Command == AppCommand.ExtExec)
        {
            command = enter with { Modifiers = modifiers };
            return true;
        }
        return false;
    }

    /// <summary>
    /// INIの割り当てを上書き登録する。EnterとReturn、メインのマイナスとテンキーのマイナスは対にして同じコマンドにする。
    /// </summary>
    public void ApplyBindings(IEnumerable<MirrorFlux.Models.KeyBindingItem> customBindings)
    {
        foreach (var item in customBindings)
        {
            _bindings[(item.Key, item.Modifiers)] = new ResolvedCommand(item.Command, 0, ExternalCommand: item.ExternalCommand);
            if (item.Key == Key.Return)
            {
                _bindings[(Key.Enter, item.Modifiers)] = new ResolvedCommand(item.Command, 0, ExternalCommand: item.ExternalCommand);
            }
            else if (item.Key == Key.Enter)
            {
                _bindings[(Key.Return, item.Modifiers)] = new ResolvedCommand(item.Command, 0, ExternalCommand: item.ExternalCommand);
            }
            else if (item.Key == Key.OemMinus)
            {
                _bindings[(Key.Subtract, item.Modifiers)] = new ResolvedCommand(item.Command, 0, ExternalCommand: item.ExternalCommand);
            }
            else if (item.Key == Key.Subtract)
            {
                _bindings[(Key.OemMinus, item.Modifiers)] = new ResolvedCommand(item.Command, 0, ExternalCommand: item.ExternalCommand);
            }
        }
    }

    private void Add(Key key, AppCommand command, ModifierKeys modifiers = ModifierKeys.None, int argument = 0) =>
        _bindings[(key, modifiers)] = new ResolvedCommand(command, argument);
}
