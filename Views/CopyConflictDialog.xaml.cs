using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MirrorFlux.Controls;
using MirrorFlux.Models;
using MirrorFlux.Input;

namespace MirrorFlux;

/// <summary>
/// コピー・移動先に同名ファイルがあるとき、上書き・更新・名前変更・スキップ・連番・中止を選ぶダイアログ。
/// Shiftを押しながら選ぶと、以降の競合すべてに同じ処理を適用する。
/// </summary>
public partial class CopyConflictDialog : RetroDialogWindow
{
    private readonly ConflictInfo _info;
    private readonly List<Border> _options = [];
    private int _selectedIndex = 0;
    private bool _isRenaming = false;

    public ConflictResolution Resolution { get; private set; } = new(ConflictAction.Cancel, false);

    /// <summary>
    /// 競合情報を表示する。移動のときは文言を「移動」に置き換える。
    /// </summary>
    public CopyConflictDialog(ConflictInfo info, bool isMove = false)
    {
        _info = info;
        InitializeComponent();

        // 移動のときは見出しと選択肢の文言を移動用にする。
        if (isMove)
        {
            LblSource.Text = "移動元";
            LblDest.Text = "移動先";
            TxtOptionO.Text = "強制上書き (上書き移動)";
            TxtOptionU.Text = "更新 (移動元が新しい場合のみ)";
            TxtOptionR.Text = "リネーム移動 (名前変更)";
            TxtOptionN.Text = "移動しない (スキップ)";
            TxtOptionA.Text = "自動連番移動 (名前_1等)";
        }

        // 比較結果と両ファイルの名前・サイズを表示する。
        TxtStatus.Text = info.StatusMessage;
        TxtSize.Text = info.SizeMessage;
        TxtTime.Text = info.TimestampMessage;
        TxtSource.Text = $"{info.DisplaySourceName} ({FileEntry.FormatFileSize(info.DisplaySourceLength)})";
        TxtDest.Text = $"{info.DestinationFile.Name} ({FileEntry.FormatFileSize(info.DestinationFile.Length)})";

        // 内容が同一なら判定結果を強調色にする。
        if (info.IsContentIdentical)
        {
            TxtStatus.Foreground = (Brush)FindResource("HighlightText");
        }
        else
        {
            TxtStatus.Foreground = (Brush)FindResource("PaneText");
        }
    }

    /// <summary>
    /// 選択肢を上から順に登録し、先頭を選択した状態で表示する。
    /// </summary>
    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _options.Add(OptionO);
        _options.Add(OptionU);
        _options.Add(OptionR);
        _options.Add(OptionN);
        _options.Add(OptionA);
        _options.Add(OptionC);

        UpdateSelectionHighlight();
        Focus();
    }

    private static readonly Brush SelectedBg = new SolidColorBrush(Color.FromRgb(0x20, 0x2A, 0x48));

    /// <summary>
    /// 選択中の選択肢だけに背景と下線を付ける。
    /// </summary>
    private void UpdateSelectionHighlight()
    {
        var highlightBrush = (Brush)FindResource("HighlightText");
        for (int i = 0; i < _options.Count; i++)
        {
            var opt = _options[i];
            if (i == _selectedIndex)
            {
                opt.Background = SelectedBg;
                opt.BorderBrush = highlightBrush;
            }
            else
            {
                opt.Background = Brushes.Transparent;
                opt.BorderBrush = Brushes.Transparent;
            }
        }
    }

    /// <summary>
    /// 選択肢の頭文字キーで処理を決め、上下キーで選択を移し、Enterで選択中の処理を実行する。
    /// </summary>
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // 名前変更の入力中はEscで選択肢へ戻る以外、入力欄に任せる。
        if (_isRenaming)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                CancelRename();
            }
            return;
        }

        var isShift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        var key = e.NormalizedKey();

        switch (key)
        {
            // 頭文字キーで処理を決める。Shift付きなら以降すべてに適用する。
            case Key.O:
                ChooseAction(ConflictAction.Overwrite, isShift);
                e.Handled = true;
                break;
            case Key.U:
                ChooseAction(ConflictAction.Update, isShift);
                e.Handled = true;
                break;
            case Key.R:
                StartRename();
                e.Handled = true;
                break;
            case Key.N:
                ChooseAction(ConflictAction.Skip, isShift);
                e.Handled = true;
                break;
            case Key.A:
                ChooseAction(ConflictAction.AutoRename, isShift);
                e.Handled = true;
                break;
            case Key.C:
            case Key.Escape:
                ChooseAction(ConflictAction.Cancel, false);
                e.Handled = true;
                break;

            // 上下キーで選択を循環させる。
            case Key.Up:
                _selectedIndex = (_selectedIndex - 1 + _options.Count) % _options.Count;
                UpdateSelectionHighlight();
                e.Handled = true;
                break;

            case Key.Down:
                _selectedIndex = (_selectedIndex + 1) % _options.Count;
                UpdateSelectionHighlight();
                e.Handled = true;
                break;

            case Key.Enter:
                ExecuteCurrentSelected(isShift);
                e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// 選択中の選択肢のTagに応じた処理を実行する。
    /// </summary>
    private void ExecuteCurrentSelected(bool isShift)
    {
        if (_selectedIndex >= 0 && _selectedIndex < _options.Count)
        {
            var tag = _options[_selectedIndex].Tag?.ToString();
            switch (tag)
            {
                case "Overwrite":
                    ChooseAction(ConflictAction.Overwrite, isShift);
                    break;
                case "Update":
                    ChooseAction(ConflictAction.Update, isShift);
                    break;
                case "Rename":
                    StartRename();
                    break;
                case "Skip":
                    ChooseAction(ConflictAction.Skip, isShift);
                    break;
                case "AutoRename":
                    ChooseAction(ConflictAction.AutoRename, isShift);
                    break;
                case "Cancel":
                    ChooseAction(ConflictAction.Cancel, false);
                    break;
            }
        }
    }

    /// <summary>
    /// クリックした選択肢のTagに応じた処理を実行する。
    /// </summary>
    private void Option_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border border)
        {
            var isShift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            var tag = border.Tag?.ToString();
            switch (tag)
            {
                case "Overwrite":
                    ChooseAction(ConflictAction.Overwrite, isShift);
                    break;
                case "Update":
                    ChooseAction(ConflictAction.Update, isShift);
                    break;
                case "Rename":
                    StartRename();
                    break;
                case "Skip":
                    ChooseAction(ConflictAction.Skip, isShift);
                    break;
                case "AutoRename":
                    ChooseAction(ConflictAction.AutoRename, isShift);
                    break;
                case "Cancel":
                    ChooseAction(ConflictAction.Cancel, false);
                    break;
            }
        }
    }

    /// <summary>
    /// マウスが乗った選択肢を選択中にする。
    /// </summary>
    private void Option_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is Border border)
        {
            var idx = _options.IndexOf(border);
            if (idx >= 0)
            {
                _selectedIndex = idx;
                UpdateSelectionHighlight();
            }
        }
    }

    /// <summary>
    /// 選んだ処理を結果に記録してダイアログを閉じる。
    /// </summary>
    private void ChooseAction(ConflictAction action, bool applyToAll, string? newName = null)
    {
        Resolution = new ConflictResolution(action, applyToAll, newName);
        DialogResult = true;
        Close();
    }

    /// <summary>
    /// 選択肢を隠して名前変更の入力欄を出す。初期値は「元の名前_copy.拡張子」。
    /// </summary>
    private void StartRename()
    {
        _isRenaming = true;
        OptionsPanel.Visibility = Visibility.Collapsed;
        RenamePanel.Visibility = Visibility.Visible;

        // 新しい名前の候補を入れて全選択する。
        var ext = Path.GetExtension(_info.DestinationFile.Name);
        var baseName = Path.GetFileNameWithoutExtension(_info.DestinationFile.Name);
        RenameTextBox.Text = $"{baseName}_copy{ext}";
        RenameTextBox.Focus();
        RenameTextBox.SelectAll();
    }

    /// <summary>
    /// 名前変更の入力欄を閉じて選択肢へ戻る。
    /// </summary>
    private void CancelRename()
    {
        _isRenaming = false;
        RenamePanel.Visibility = Visibility.Collapsed;
        OptionsPanel.Visibility = Visibility.Visible;
        Focus();
    }

    private void RenameConfirm_Click(object sender, RoutedEventArgs e)
    {
        ConfirmRename();
    }

    /// <summary>
    /// 入力欄でEnterなら確定、Escなら選択肢へ戻る。
    /// </summary>
    private void RenameTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            ConfirmRename();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CancelRename();
        }
    }

    /// <summary>
    /// 入力した名前で名前変更コピーを決める。空の名前は受け付けない。
    /// </summary>
    private void ConfirmRename()
    {
        var newName = RenameTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(newName))
        {
            MessageBox.Show("有効なファイル名を入力してください", "リネームコピー", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var isShift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        ChooseAction(ConflictAction.Rename, isShift, newName);
    }
}
