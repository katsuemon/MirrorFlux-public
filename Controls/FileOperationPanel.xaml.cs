using System.Windows.Controls;

namespace MirrorFlux.Controls;

/// <summary>
/// ファイル操作の進行欄。実行中の操作の件数・量・処理中のファイル・次のファイルと、順番待ちの操作を出す。
/// 表示する文字列は MainViewModel が用意する。
/// </summary>
public partial class FileOperationPanel : UserControl
{
    public FileOperationPanel()
    {
        InitializeComponent();
    }
}