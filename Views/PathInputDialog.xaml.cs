using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using MirrorFlux.Controls;
using MirrorFlux.Services;
using MirrorFlux.Input;

namespace MirrorFlux;

/// <summary>
/// 移動先のフォルダーまたはアーカイブのパスを直接入力するダイアログ。
/// </summary>
public partial class PathInputDialog : RetroDialogWindow
{
    public string TargetPath { get; private set; } = "";

    public PathInputDialog(string defaultPath = "")
    {
        InitializeComponent();
        if (!string.IsNullOrEmpty(defaultPath))
        {
            PathTextBox.Text = defaultPath;
        }
    }

    /// <summary>
    /// 入力欄にフォーカスして全選択する。表示直後にフォーカスが外れることがあるため、入力処理の順番でもう一度合わせる。
    /// </summary>
    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        Activate();
        PathTextBox.Focus();
        Keyboard.Focus(PathTextBox);
        PathTextBox.SelectAll();

        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            Activate();
            PathTextBox.Focus();
            Keyboard.Focus(PathTextBox);
            PathTextBox.SelectAll();
        }));
    }

    /// <summary>
    /// 入力を直し始めたらエラー表示を消す。
    /// </summary>
    private void PathTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (ErrorText is not null && ErrorText.Visibility == Visibility.Visible)
        {
            ErrorText.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// Enterで決定、Escで中止する。
    /// </summary>
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.NormalizedKey();
        if (key == Key.Enter)
        {
            e.Handled = true;
            Confirm();
        }
        else if (key == Key.Escape)
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
        PathTextBox.Focus();
        Keyboard.Focus(PathTextBox);
    }

    /// <summary>
    /// 入力したパスがフォルダー、対応アーカイブ、アーカイブ内のパスのどれかなら結果にして閉じる。
    /// </summary>
    private void Confirm()
    {
        // 空の入力は受け付けない。前後の空白と引用符は外す。
        var raw = PathTextBox.Text;
        if (string.IsNullOrWhiteSpace(raw))
        {
            ShowError("移動先のパスを入力してください。");
            return;
        }

        var target = raw.Trim().Trim('"', '\'');
        if (string.IsNullOrWhiteSpace(target))
        {
            ShowError("移動先のパスを入力してください。");
            return;
        }

        try
        {
            // 実在するフォルダーへ移動する。\\server\share のようなUNCパスや [...] を含むパスもここで扱う。
            if (Directory.Exists(target))
            {
                TargetPath = Path.GetFullPath(target);
                DialogResult = true;
                Close();
                return;
            }

            // 対応形式のアーカイブファイル（.zip、.lha など）なら、その中へ移動する。
            if (File.Exists(target) && ArchiveService.IsSupportedArchive(target))
            {
                TargetPath = Path.GetFullPath(target);
                DialogResult = true;
                Close();
                return;
            }

            // アーカイブ内のパス（例：C:\archive.zip\subfolder）なら、アーカイブ本体があるか確かめて移動する。
            if (ArchiveService.IsInsideArchive(target) &&
                ArchiveService.TrySplitArchivePath(target, out var arcFile, out _) &&
                File.Exists(arcFile))
            {
                TargetPath = target;
                DialogResult = true;
                Close();
                return;
            }

            ShowError($"指定されたフォルダーまたはアーカイブが見つかりません:\n{target}");
        }
        catch (Exception ex)
        {
            ShowError($"パスの検証中にエラーが発生しました: {ex.Message}");
        }
    }
}
