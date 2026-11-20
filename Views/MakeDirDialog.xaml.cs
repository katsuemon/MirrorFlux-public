using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using MirrorFlux.Controls;

namespace MirrorFlux;

/// <summary>
/// 作成するフォルダーの名前を入力するダイアログ。
/// </summary>
public partial class MakeDirDialog : RetroDialogWindow
{
    public string DirectoryName { get; private set; } = "";

    public MakeDirDialog(string defaultName = "")
    {
        InitializeComponent();
        if (!string.IsNullOrEmpty(defaultName))
        {
            DirNameTextBox.Text = defaultName;
        }
    }

    /// <summary>
    /// 入力欄にフォーカスして全選択する。表示直後にフォーカスが外れることがあるため、入力処理の順番でもう一度合わせる。
    /// </summary>
    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        Activate();
        DirNameTextBox.Focus();
        Keyboard.Focus(DirNameTextBox);
        DirNameTextBox.SelectAll();

        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            Activate();
            DirNameTextBox.Focus();
            Keyboard.Focus(DirNameTextBox);
            DirNameTextBox.SelectAll();
        }));
    }

    /// <summary>
    /// 入力を直し始めたらエラー表示を消す。
    /// </summary>
    private void DirNameTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (ErrorText is not null && ErrorText.Visibility == Visibility.Visible)
        {
            ErrorText.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// Enterで決定、Escで中止する。IME変換中のEnterは変換の確定に使われ、ここには届かない。
    /// </summary>
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            Confirm();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            DialogResult = false;
            Close();
        }
    }

    private void ConfirmBadge_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        Confirm();
    }

    private void CancelBadge_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        DialogResult = false;
        Close();
    }

    /// <summary>
    /// エラーを表示して入力欄へ戻す。
    /// </summary>
    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
        DirNameTextBox.Focus();
        Keyboard.Focus(DirNameTextBox);
    }

    /// <summary>
    /// フォルダー名を検査し、使える名前なら結果にしてダイアログを閉じる。
    /// </summary>
    private void Confirm()
    {
        // 空の名前と「.」「..」は受け付けない。
        var raw = DirNameTextBox.Text;
        var name = raw.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            ShowError("フォルダー名を入力してください。");
            return;
        }

        if (name is "." or "..")
        {
            ShowError("'.' または '..' はフォルダー名に使用できません。");
            return;
        }

        // Windowsでは末尾のピリオドと空白が落とされるため、先に除いてから検査する。
        name = name.TrimEnd('.', ' ');
        if (string.IsNullOrWhiteSpace(name))
        {
            ShowError("有効なフォルダー名を入力してください。");
            return;
        }

        // 名前に使えない文字を含むものは受け付けない。
        char[] invalidChars = Path.GetInvalidFileNameChars();
        if (name.IndexOfAny(invalidChars) >= 0)
        {
            ShowError("フォルダー名に使用できない文字が含まれています。");
            return;
        }

        DirectoryName = name;
        DialogResult = true;
        Close();
    }
}
