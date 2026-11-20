namespace MirrorFlux.Input;

/// <summary>
/// キーやファンクションキーに割り当てられるファイル一覧画面のコマンドの種類。
/// </summary>
public enum AppCommand
{
    None,
    MoveUp,
    MoveDown,
    MovePageUp,
    MovePageDown,
    MoveHome,
    MoveEnd,
    SwitchPane,
    ActivateLeftPane,
    ActivateRightPane,
    OpenSelected,
    GoParent,
    ToggleMark,
    MarkAll,
    IncrementalSearch,
    Archive,
    Refresh,
    FunctionKey,
    FileCopy,
    FileCopyCheck,
    FileMove,
    FileMoveCheck,
    Exit,
    DriveMenu,
    MakeDir,
    Delete,
    FileRename,
    TextViewer,
    GraphicViewer,
    VgmPlayer,
    ExtExec,
    ExtExecWindows,
    MiniPlayer,
    Sort,
    PdfViewer,
    SyncPanePath,
    HexViewer,
    ChangeDirectory,
    JumpList,
    LogLevel,
    Help,
    View,
    MarkClear,
    ExternalProgram,
    VideoViewer,
    OperationCancel
}

/// <summary>
/// キー入力から決まった実行内容。コマンド、引数、押されていた修飾キー、外部プログラムのコマンド行を持つ。
/// </summary>
public readonly record struct ResolvedCommand(AppCommand Command, int Argument = 0,
    System.Windows.Input.ModifierKeys Modifiers = System.Windows.Input.ModifierKeys.None, string? ExternalCommand = null);
