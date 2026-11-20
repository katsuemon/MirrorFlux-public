using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MirrorFlux.Controls;
using MirrorFlux.Models;
using MirrorFlux.Input;

namespace MirrorFlux;

/// <summary>
/// ドライブを選ぶダイアログ。ドライブ文字キーで即決し、「\」でパスの直接入力へ進む。
/// </summary>
public partial class DriveSelectDialog : RetroDialogWindow
{
    private readonly IReadOnlyList<DriveItem> _drives;
    private readonly char _currentLetter;

    public DriveItem? SelectedDrive { get; private set; }
    public bool RequestPathInput { get; private set; }

    /// <summary>
    /// ドライブ一覧を表示し、現在のドライブを選択しておく。IMEは無効にして文字キーを直接受ける。
    /// </summary>
    public DriveSelectDialog(IReadOnlyList<DriveItem> drives, char currentLetter)
    {
        _drives = drives;
        _currentLetter = char.ToUpperInvariant(currentLetter);

        InitializeComponent();
        InputMethod.SetIsInputMethodEnabled(this, false);

        DriveListBox.ItemsSource = _drives;

        // 生成時点で現在のドライブを選択しておく。
        int initialIndex = FindCurrentIndex();
        if (initialIndex >= 0)
        {
            DriveListBox.SelectedIndex = initialIndex;
            SelectedDrive = _drives[initialIndex];
        }
    }

    /// <summary>
    /// 現在のドライブの位置を返す。見つからなければ先頭、一覧が空なら-1。
    /// </summary>
    private int FindCurrentIndex()
    {
        for (int i = 0; i < _drives.Count; i++)
        {
            if (_drives[i].Letter == _currentLetter)
            {
                return i;
            }
        }
        return _drives.Count > 0 ? 0 : -1;
    }

    /// <summary>
    /// 表示時に現在のドライブへフォーカスを合わせる。一覧の項目が作られた後にもう一度合わせ直す。
    /// </summary>
    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        SelectAndFocusCurrentDrive();
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(SelectAndFocusCurrentDrive));
    }

    /// <summary>
    /// 現在のドライブを選択して見える位置へ送り、その項目にキーボードフォーカスを置く。
    /// </summary>
    private void SelectAndFocusCurrentDrive()
    {
        int targetIndex = FindCurrentIndex();
        if (targetIndex >= 0)
        {
            // 選択して見える位置へスクロールする。
            DriveListBox.SelectedIndex = targetIndex;
            var targetItem = _drives[targetIndex];
            SelectedDrive = targetItem;
            DriveListBox.ScrollIntoView(targetItem);

            // 項目のコンテナができていればそこへ、なければ一覧自体へフォーカスする。
            DriveListBox.UpdateLayout();
            if (DriveListBox.ItemContainerGenerator.ContainerFromIndex(targetIndex) is ListBoxItem container)
            {
                container.Focus();
                Keyboard.Focus(container);
            }
            else
            {
                DriveListBox.Focus();
            }
        }
        else
        {
            DriveListBox.Focus();
        }
    }

    /// <summary>
    /// Escで中止、「\」でパス入力、Enterで決定、上下で移動、ドライブ文字キーで即決する。
    /// </summary>
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Escで中止する。
        var key = e.NormalizedKey();
        if (key == Key.Escape)
        {
            e.Handled = true;
            DialogResult = false;
            Close();
            return;
        }

        // 「\」系のキーでパスの直接入力へ進む。Jドライブがなければ J でも同じ。
        if (key == Key.OemBackslash || key == Key.Oem5 || key == Key.OemPipe || key == Key.Divide ||
            (key == Key.J && !_drives.Any(d => d.Letter == 'J')))
        {
            e.Handled = true;
            RequestPathInput = true;
            DialogResult = true;
            Close();
            return;
        }

        // Enterで選択中のドライブに決める。
        if (key == Key.Enter)
        {
            e.Handled = true;
            ConfirmSelection();
            return;
        }

        // 上下キーで選択を循環して移動する。
        if (key == Key.Down && _drives.Count > 0)
        {
            int next = (DriveListBox.SelectedIndex + 1) % _drives.Count;
            DriveListBox.SelectedIndex = next;
            DriveListBox.ScrollIntoView(_drives[next]);
            if (DriveListBox.ItemContainerGenerator.ContainerFromIndex(next) is ListBoxItem c)
            {
                c.Focus();
            }
            e.Handled = true;
            return;
        }

        if (key == Key.Up && _drives.Count > 0)
        {
            int prev = (DriveListBox.SelectedIndex - 1 + _drives.Count) % _drives.Count;
            DriveListBox.SelectedIndex = prev;
            DriveListBox.ScrollIntoView(_drives[prev]);
            if (DriveListBox.ItemContainerGenerator.ContainerFromIndex(prev) is ListBoxItem c)
            {
                c.Focus();
            }
            e.Handled = true;
            return;
        }

        // A～Zキーで、そのドライブがあれば即座に決める。
        if (key >= Key.A && key <= Key.Z)
        {
            char pressedLetter = (char)('A' + (key - Key.A));
            var matched = _drives.FirstOrDefault(d => d.Letter == pressedLetter);
            if (matched is not null)
            {
                e.Handled = true;
                SelectedDrive = matched;
                DialogResult = true;
                Close();
                return;
            }
        }
    }

    /// <summary>
    /// キーヘルプのクリックでパスの直接入力へ進む。
    /// </summary>
    private void DirectPathBadge_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        RequestPathInput = true;
        DialogResult = true;
        Close();
    }

    private void DriveListBox_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        ConfirmSelection();
    }

    /// <summary>
    /// 選択中のドライブを結果にしてダイアログを閉じる。
    /// </summary>
    private void ConfirmSelection()
    {
        if (DriveListBox.SelectedItem is DriveItem item)
        {
            SelectedDrive = item;
            DialogResult = true;
            Close();
        }
        else if (DriveListBox.SelectedIndex >= 0 && DriveListBox.SelectedIndex < _drives.Count)
        {
            SelectedDrive = _drives[DriveListBox.SelectedIndex];
            DialogResult = true;
            Close();
        }
    }
}
