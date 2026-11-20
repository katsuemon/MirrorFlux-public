using System.Windows.Input;

namespace MirrorFlux.Services;

/// <summary>
/// テキスト・画像・動画などのビューアーに共通のインターフェース。
/// </summary>
public interface IViewerControl
{
    /// <summary>
    /// 表示中のファイルのパス。
    /// </summary>
    string FilePath { get; }

    /// <summary>
    /// ファイルを読み込んで表示する。
    /// </summary>
    bool Load(string filePath);

    /// <summary>
    /// ビューアーを閉じるときの後片付けをする。
    /// </summary>
    void Close();

    /// <summary>
    /// ステータスバーの左側に出す情報（ファイル名、文字コード、サイズなど）。
    /// </summary>
    string StatusText { get; }

    /// <summary>
    /// ステータスバーの右側に出す情報（行・列、進み具合など）。
    /// </summary>
    string LocationText { get; }

    /// <summary>
    /// ステータス情報が変わったときに発生する。
    /// </summary>
    event EventHandler? StatusChanged;

    /// <summary>
    /// 検索や行番号指定などの入力欄が開いているか。
    /// </summary>
    bool IsPromptActive { get; }

    /// <summary>
    /// ビューアーを閉じる要求が出たときに発生する。
    /// </summary>
    event EventHandler? CloseRequested;

    /// <summary>
    /// キー入力を処理する。ビューアー固有のキーを処理したら true を返す。
    /// </summary>
    bool HandleKeyDown(KeyEventArgs e);

    /// <summary>
    /// ファンクションキー（F1～F12）の機能を実行する。
    /// </summary>
    void ExecuteFunctionKey(int keyNumber);
}
