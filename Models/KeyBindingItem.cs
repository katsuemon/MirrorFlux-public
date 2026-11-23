using System.Windows.Input;
using MirrorFlux.Input;

namespace MirrorFlux.Models;

/// <summary>
/// ファイル一覧画面のキー割り当て1件（例：>KEY_C -s-- &amp;filecopy;）。外部プログラムの場合はそのコマンド行も持つ。
/// </summary>
public sealed record KeyBindingItem(
    Key Key,
    ModifierKeys Modifiers,
    AppCommand Command,
    string RawCommandName,
    string? ExternalCommand = null,
    KeyBindingItem[]? FollowingCommands = null
);
