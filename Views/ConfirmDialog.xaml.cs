using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MirrorFlux.Controls;
using MirrorFlux.Input;
using MirrorFlux.Models;

namespace MirrorFlux;

/// <summary>
/// コピー・移動・削除・終了の確認ダイアログ。Y/Enterで実行、N/Escで中止、←→↑↓/Tabで選択を切り替える。
/// 誤操作で困る削除と終了は「いいえ」を初期選択にする。
/// </summary>
public partial class ConfirmDialog : RetroDialogWindow
{
    private static readonly Brush SelectedBg = new SolidColorBrush(Color.FromRgb(0x20, 0x2A, 0x48));

    /// <summary>
    /// 詳細表の1行。Emphasisは値を強調色に、Boldは太字にする。
    /// </summary>
    private sealed record Row(string Label, string Value, bool Emphasis = false, bool Bold = false);

    private bool _yesSelected;

    /// <summary>
    /// 見出し・問いかけ・詳細行・選択肢の文言を設定する。IMEは無効にしてY/Nキーを直接受ける。
    /// </summary>
    private ConfirmDialog(string title, string prompt, IEnumerable<Row> rows, string yesText, string noText,
        string keyHelp, bool yesByDefault)
    {
        InitializeComponent();
        InputMethod.SetIsInputMethodEnabled(this, false);
        Title = title;
        Header.TitleText = title;
        PromptText.Text = prompt;
        foreach (var row in rows) DetailRows.Children.Add(CreateRow(row));
        YesText.Text = yesText;
        NoText.Text = noText;
        KeyHelp.Text = keyHelp;
        _yesSelected = yesByDefault;
    }

    /// <summary>
    /// 反対側のフォルダーへのコピー・移動の確認を作る。「はい」が初期選択。
    /// </summary>
    public static ConfirmDialog ForTransfer(IReadOnlyList<FileEntry> items, string destinationDir, bool isMove)
    {
        string verb = isMove ? "移動" : "コピー";
        var rows = TargetRows(items).ToList();
        rows.Insert(1, new Row($"{verb}先", $"[ {destinationDir} ]", Emphasis: true));
        return new ConfirmDialog($"{verb}確認", $"以下の項目を反対側のフォルダーへ{verb}しますか？", rows,
            $"はい ({verb}実行)", "いいえ (中止)", "[Y/Enter] 実行  [N/Esc] 中止  [←/→/Tab] カーソル移動", yesByDefault: true);
    }

    /// <summary>
    /// ごみ箱への移動、またはアーカイブ内の項目の削除の確認を作る。「いいえ」が初期選択。
    /// </summary>
    public static ConfirmDialog ForDelete(IReadOnlyList<FileEntry> items, bool isInsideArchive = false) =>
        new("削除確認",
            isInsideArchive ? "以下の項目をアーカイブから削除しますか？" : "以下の項目をごみ箱に移動しますか？",
            TargetRows(items),
            isInsideArchive ? "はい (削除実行)" : "はい (ごみ箱へ移動)", "いいえ (中止)",
            "[Y/Enter] 実行  [N/Esc] 中止  [←/→/Tab] カーソル移動", yesByDefault: false);

    /// <summary>
    /// 実行中のファイル操作の中止の確認を作る。続けたいときに誤って止めないよう「いいえ」が初期選択。
    /// </summary>
    public static ConfirmDialog ForCancelOperation(string kind) =>
        new($"{kind}中止確認", $"実行中の{kind}を中止しますか？",
            [new Row("処理中のファイル", "[ 書きかけは消し、元のファイルは残す ]"), new Row("終わった分", "[ そのまま残す ]"),
             new Row("待ちの作業", "[ 中止後に順に実行 ]")],
            "はい (中止)", "いいえ (続ける)", "[Y/Enter] 中止  [N/Esc] 続ける  [←/→/Tab] カーソル移動", yesByDefault: false);

    /// <summary>
    /// アプリケーション終了の確認を作る。誤終了を防ぐため「いいえ」が初期選択。
    /// </summary>
    public static ConfirmDialog ForExit() =>
        new("終了確認", "アプリケーションを終了しますか？",
            [new Row("設定保存", "[ mf.ini へ自動保存 ]"), new Row("終了時動作", "[ 左右パネルパスを保持 ]")],
            "はい (終了)", "いいえ (継続)", "[Y/Enter] 終了  [N/Esc] 継続  [←/→/Tab] カーソル移動", yesByDefault: false);

    /// <summary>
    /// 対象・更新日時・属性の行を作る。1項目ならその内容、複数なら件数と合計サイズを出す。
    /// </summary>
    private static IEnumerable<Row> TargetRows(IReadOnlyList<FileEntry> items)
    {
        // 1項目のときは名前・サイズ・日時・種類を出す。
        if (items.Count == 1)
        {
            var item = items[0];
            return
            [
                new Row(item.IsDirectory ? "対象フォルダー" : "対象ファイル", $"[ {item.Name} ({item.SizeText}) ]", Emphasis: true, Bold: true),
                new Row("更新日時", $"[ {item.LastWriteTime:yyyy-MM-dd  HH:mm:ss} ]"),
                new Row("属性", $"[ {(item.IsDirectory ? "ディレクトリ" : "通常ファイル")} ]"),
            ];
        }
        // 複数のときは件数と合計サイズを出す。
        long totalSize = items.Sum(i => i.Length);
        return
        [
            new Row("対象項目", $"[ {items.Count} 個の項目 (合計 {FileEntry.FormatFileSize(totalSize)}) ]", Emphasis: true, Bold: true),
            new Row("更新日時", "[ 複数項目 ]"),
            new Row("属性", "[ マーク項目 ]"),
        ];
    }

    /// <summary>
    /// 詳細表の1行を「見出し : 値」の3列のGridとして作る。
    /// </summary>
    private static Grid CreateRow(Row row)
    {
        // 見出し・区切り・値の列を用意する。
        var grid = new Grid { Height = 22 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        TextBlock Cell(int column, string text, string brush)
        {
            var cell = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
            cell.SetResourceReference(TextBlock.ForegroundProperty, brush);
            Grid.SetColumn(cell, column);
            grid.Children.Add(cell);
            return cell;
        }
        // 各セルを置き、値は長ければ省略表示にする。
        Cell(0, row.Label, "MutedText");
        Cell(1, ":", "MutedText");
        var value = Cell(2, row.Value, row.Emphasis ? "HighlightText" : "PaneText");
        value.TextTrimming = TextTrimming.CharacterEllipsis;
        if (row.Bold) value.FontWeight = FontWeights.Bold;
        return grid;
    }

    /// <summary>
    /// 表示時に初期選択を描き、キー入力を受けられるようにする。
    /// </summary>
    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateSelection();
        Activate();
        Focus();
    }

    /// <summary>
    /// 選択中の選択肢に背景と下線を付け、もう一方を消す。
    /// </summary>
    private void UpdateSelection()
    {
        var highlightBrush = (Brush)FindResource("HighlightText");
        var (selected, other) = _yesSelected ? (OptionYes, OptionNo) : (OptionNo, OptionYes);
        selected.Background = SelectedBg;
        selected.BorderBrush = highlightBrush;
        other.Background = Brushes.Transparent;
        other.BorderBrush = Brushes.Transparent;
    }

    private void Finish(bool result)
    {
        DialogResult = result;
        Close();
    }

    /// <summary>
    /// Y/N/Esc/Enterで結果を決め、矢印キーとTabで選択を切り替える。
    /// </summary>
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.NormalizedKey())
        {
            case Key.Y:
                e.Handled = true;
                Finish(true);
                break;
            case Key.N:
            case Key.Escape:
                e.Handled = true;
                Finish(false);
                break;
            case Key.Left:
            case Key.Right:
            case Key.Up:
            case Key.Down:
            case Key.Tab:
                e.Handled = true;
                _yesSelected = !_yesSelected;
                UpdateSelection();
                break;
            case Key.Enter:
                e.Handled = true;
                Finish(_yesSelected);
                break;
        }
    }

    private void OptionYes_Click(object sender, MouseButtonEventArgs e) => Finish(true);
    private void OptionNo_Click(object sender, MouseButtonEventArgs e) => Finish(false);

    private void OptionYes_MouseEnter(object sender, MouseEventArgs e)
    {
        _yesSelected = true;
        UpdateSelection();
    }

    private void OptionNo_MouseEnter(object sender, MouseEventArgs e)
    {
        _yesSelected = false;
        UpdateSelection();
    }
}
