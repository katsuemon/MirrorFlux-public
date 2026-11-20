using MirrorFlux.Input;

namespace MirrorFlux.Models;

/// <summary>
/// 拡張子関連付けの1件（.txt ---- &amp;textviewer; など）。拡張子、コマンド、INIでのコマンド名、修飾キーを持つ。
/// </summary>
public sealed record ExtBindingItem(string Extension, AppCommand Command, string RawCommandName,
    System.Windows.Input.ModifierKeys Modifiers = System.Windows.Input.ModifierKeys.None);
