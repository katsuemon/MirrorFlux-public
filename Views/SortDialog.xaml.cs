using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MirrorFlux.Controls;
using MirrorFlux.ViewModels;
using MirrorFlux.Input;

namespace MirrorFlux;

/// <summary>
/// パネルごとのファイル一覧の並び順（名前・拡張子・サイズ・日時と昇順・降順）と、フォルダーを上に集めるかを選ぶダイアログ。
/// </summary>
public partial class SortDialog : RetroDialogWindow
{
    private static readonly Brush SelectedBg = new SolidColorBrush(Color.FromRgb(0x20, 0x2A, 0x48));
    private readonly List<Border> _options = [];
    private int _selectedIndex;

    public PaneSortMode SelectedSortMode { get; private set; }
    public bool SelectedAscending { get; private set; }
    public bool SelectedDirectoriesFirst { get; private set; }

    /// <summary>
    /// 現在の並び順を初期選択にして、対象のパネル名を見出しに出す。
    /// </summary>
    public SortDialog(PaneSortMode currentMode, bool currentAscending, bool isLeftPane, bool currentDirectoriesFirst = true)
    {
        InitializeComponent();

        // 現在の設定を結果の初期値にする。
        SelectedSortMode = currentMode;
        SelectedAscending = currentAscending;
        SelectedDirectoriesFirst = currentDirectoriesFirst;

        // 対象のパネル名を見出しと案内文に入れる。
        var paneLabel = isLeftPane ? "左ウインドウ" : "右ウインドウ";
        Title = $"ソート設定 ({paneLabel})";
        DialogHeader.TitleText = $"ソート設定 ({paneLabel})";
        TargetPaneText.Text = $"{paneLabel}の表示ソート順を選択してください:";

        // 4行×2列の選択肢を登録する。偶数番目が▲昇順、奇数番目が▼降順。
        _options.Add(OptionNameAsc);
        _options.Add(OptionNameDesc);
        _options.Add(OptionTypeAsc);
        _options.Add(OptionTypeDesc);
        _options.Add(OptionSizeAsc);
        _options.Add(OptionSizeDesc);
        _options.Add(OptionDateAsc);
        _options.Add(OptionDateDesc);

        _selectedIndex = FindOptionIndex(currentMode, currentAscending);
    }

    /// <summary>
    /// 並び順と昇降に当たる選択肢の位置をTag（「種類:昇順か」）から探す。見つからなければ先頭。
    /// </summary>
    private int FindOptionIndex(PaneSortMode mode, bool ascending)
    {
        var expectedTag = $"{mode}:{ascending}";
        for (int i = 0; i < _options.Count; i++)
        {
            if (string.Equals(_options[i].Tag?.ToString(), expectedTag, System.StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }
        return 0;
    }

    /// <summary>
    /// 表示時に選択中の項目とフォルダー優先の状態を描き、キー入力を受けられるようにする。
    /// </summary>
    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateSelectionHighlight();
        UpdateDirsFirstDisplay();
        Activate();
        Focus();
    }

    /// <summary>
    /// 「フォルダを上に集める」のチェック表示を更新する。
    /// </summary>
    private void UpdateDirsFirstDisplay()
    {
        DirsFirstCheckText.Text = SelectedDirectoriesFirst ? "[✓]" : "[ ]";
        DirsFirstCheckText.Foreground = (Brush)FindResource(SelectedDirectoriesFirst ? "HighlightText" : "MutedText");
    }

    private void ToggleDirectoriesFirst()
    {
        SelectedDirectoriesFirst = !SelectedDirectoriesFirst;
        UpdateDirsFirstDisplay();
    }

    private void DirsFirst_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        ToggleDirectoriesFirst();
    }

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
    /// 矢印キーとTabで選択を移し、Enterか各選択肢の頭文字キーで決定する。F/Spaceでフォルダー優先を切り替える。
    /// </summary>
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // 選択中の位置を行と列に分ける。
        var key = e.NormalizedKey();
        int rowCount = _options.Count / 2;
        int row = _selectedIndex / 2;
        int col = _selectedIndex % 2;

        switch (key)
        {
            case Key.Escape:
                e.Handled = true;
                DialogResult = false;
                Close();
                break;

            case Key.Enter:
                e.Handled = true;
                ConfirmByIndex(_selectedIndex);
                break;

            // 上下キーは同じ列のまま行を循環して移動する。
            case Key.Up:
                e.Handled = true;
                row = (row - 1 + rowCount) % rowCount;
                _selectedIndex = row * 2 + col;
                UpdateSelectionHighlight();
                break;

            case Key.Down:
                e.Handled = true;
                row = (row + 1) % rowCount;
                _selectedIndex = row * 2 + col;
                UpdateSelectionHighlight();
                break;

            // 左右キーは昇順と降順を入れ替える。
            case Key.Left:
            case Key.Right:
                e.Handled = true;
                _selectedIndex ^= 1;
                UpdateSelectionHighlight();
                break;

            // Tabは全選択肢を順に移動し、Shift+Tabは逆順に移動する。
            case Key.Tab:
                e.Handled = true;
                if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
                {
                    _selectedIndex = (_selectedIndex - 1 + _options.Count) % _options.Count;
                }
                else
                {
                    _selectedIndex = (_selectedIndex + 1) % _options.Count;
                }
                UpdateSelectionHighlight();
                break;

            // 頭文字キーでその並び順に即決する。
            case Key.N:
                e.Handled = true;
                ConfirmChoice(PaneSortMode.Name, true);
                break;
            case Key.R:
                e.Handled = true;
                ConfirmChoice(PaneSortMode.Name, false);
                break;
            case Key.E:
                e.Handled = true;
                ConfirmChoice(PaneSortMode.Type, true);
                break;
            case Key.X:
                e.Handled = true;
                ConfirmChoice(PaneSortMode.Type, false);
                break;
            case Key.Z:
                e.Handled = true;
                ConfirmChoice(PaneSortMode.Size, true);
                break;
            case Key.S:
                e.Handled = true;
                ConfirmChoice(PaneSortMode.Size, false);
                break;
            case Key.T:
                e.Handled = true;
                ConfirmChoice(PaneSortMode.Date, true);
                break;
            case Key.D:
                e.Handled = true;
                ConfirmChoice(PaneSortMode.Date, false);
                break;
            case Key.F:
            case Key.Space:
                e.Handled = true;
                ToggleDirectoriesFirst();
                break;
        }
    }

    /// <summary>
    /// クリックした選択肢で決定する。
    /// </summary>
    private void Option_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border border)
        {
            var idx = _options.IndexOf(border);
            if (idx >= 0)
            {
                ConfirmByIndex(idx);
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
    /// 指定位置の選択肢のTagを並び順と昇降に分解して決定する。
    /// </summary>
    private void ConfirmByIndex(int index)
    {
        if (index < 0 || index >= _options.Count)
        {
            return;
        }

        var tag = _options[index].Tag?.ToString() ?? "";
        var parts = tag.Split(':');
        if (parts.Length == 2 &&
            System.Enum.TryParse<PaneSortMode>(parts[0], ignoreCase: true, out var mode) &&
            bool.TryParse(parts[1], out var asc))
        {
            ConfirmChoice(mode, asc);
        }
    }

    /// <summary>
    /// 選んだ並び順を結果にしてダイアログを閉じる。
    /// </summary>
    private void ConfirmChoice(PaneSortMode mode, bool ascending)
    {
        SelectedSortMode = mode;
        SelectedAscending = ascending;
        DialogResult = true;
        Close();
    }
}
