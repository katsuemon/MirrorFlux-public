using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MirrorFlux.Controls;

namespace MirrorFlux;

/// <summary>
/// マークした項目からアーカイブを作るときに、形式・ファイル名・圧縮率を選ぶダイアログ。
/// </summary>
public partial class ArchiveCreateDialog : RetroDialogWindow
{
    private static readonly (string Extension, string FixedLevel)[] Formats =
    [
        (".zip", ""), (".7z", "標準（固定）"), (".lzh", "固定"),
        (".tar", "圧縮なし")
    ];
    private static readonly int[] ZipLevels = [0, 1, 6, 9];
    private readonly Button[] _formatButtons;
    private readonly Button[] _levelButtons;
    private int _format;
    private int _levelIndex = 2;

    public string ArchiveFileName { get; private set; } = "";
    public int ZipLevel { get; private set; } = 6;

    /// <summary>
    /// 既定のファイル名と対象件数を表示して、選択肢の初期状態を作る。
    /// </summary>
    public ArchiveCreateDialog(string defaultName, int markedCount)
    {
        InitializeComponent();
        CountText.Text = $"マークした {markedCount} 項目を同じフォルダーにまとめます";
        NameBox.Text = defaultName;
        _formatButtons = [ZipButton, SevenZipButton, LzhButton, TarButton];
        _levelButtons = [LevelNone, LevelFast, LevelStandard, LevelHigh];
        UpdateChoices();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e) => _formatButtons[_format].Focus();

    /// <summary>
    /// 選択中の形式と圧縮率をボタンに反映する。圧縮率を選べるのはZIPだけで、他の形式は固定表示にする。
    /// </summary>
    private void UpdateChoices()
    {
        // 選択中のボタンに印を付ける。
        for (int i = 0; i < _formatButtons.Length; i++)
            _formatButtons[i].Tag = i == _format;
        for (int i = 0; i < _levelButtons.Length; i++)
            _levelButtons[i].Tag = i == _levelIndex;
        // ZIP以外は圧縮率の選択肢を隠し、固定の説明を出す。LZHでは名前の文字制限を案内する。
        ZipLevelPanel.Visibility = _format == 0 ? Visibility.Visible : Visibility.Collapsed;
        FixedLevelButton.Visibility = _format == 0 ? Visibility.Collapsed : Visibility.Visible;
        FixedLevelButton.Content = Formats[_format].FixedLevel;
        FixedLevelButton.Tag = true;
        FormatNoticeText.Visibility = Formats[_format].Extension == ".lzh" ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 形式を切り替える。ファイル名が前の形式の拡張子で終わっていれば、新しい拡張子に付け替える。
    /// </summary>
    private void SelectFormat(int index)
    {
        if (index == _format) return;
        string previous = Formats[_format].Extension;
        _format = index;
        string name = NameBox.Text.Trim();
        if (name.EndsWith(previous, StringComparison.OrdinalIgnoreCase))
            NameBox.Text = name[..^previous.Length] + Formats[_format].Extension;
        UpdateChoices();
    }

    /// <summary>
    /// ZIPの圧縮率を選ぶ。
    /// </summary>
    private void SelectLevel(int index)
    {
        _levelIndex = index;
        ZipLevel = ZipLevels[index];
        UpdateChoices();
    }

    private void Format_Click(object sender, RoutedEventArgs e) =>
        SelectFormat(Array.IndexOf(_formatButtons, sender));

    private void Level_Click(object sender, RoutedEventArgs e) =>
        SelectLevel(Array.IndexOf(_levelButtons, sender));

    private void FixedLevel_Click(object sender, RoutedEventArgs e) => FixedLevelButton.Focus();
    private void Apply_Click(object sender, RoutedEventArgs e) => Apply();

    /// <summary>
    /// 矢印キーで行と選択肢を移動し、Enterで作成する。
    /// </summary>
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // フォーカスのある行を調べる。0=形式、1=ファイル名、2=圧縮率、3=ボタン。
        if (Keyboard.Modifiers != ModifierKeys.None) return;
        var focus = Keyboard.FocusedElement as Control;
        int row = Array.IndexOf(_formatButtons, focus);
        row = row >= 0 ? 0 : focus == NameBox ? 1 :
            Array.IndexOf(_levelButtons, focus) >= 0 || focus == FixedLevelButton ? 2 :
            focus == ApplyButton || focus == CancelButton ? 3 : -1;
        if (row < 0) return;

        // 上下キーで行を循環して移動する。
        if (e.Key is Key.Up or Key.Down)
        {
            int next = (row + (e.Key == Key.Down ? 1 : 3)) % 4;
            Control nextControl = next switch
            {
                0 => _formatButtons[_format],
                1 => NameBox,
                2 => _format == 0 ? _levelButtons[_levelIndex] : FixedLevelButton,
                _ => ApplyButton
            };
            nextControl.Focus();
            e.Handled = true;
        }
        // 左右キーで形式を切り替える。
        else if (e.Key is Key.Left or Key.Right && row == 0)
        {
            SelectFormat((_format + (e.Key == Key.Right ? 1 : -1) + Formats.Length) % Formats.Length);
            _formatButtons[_format].Focus();
            e.Handled = true;
        }
        // 左右キーでZIPの圧縮率を切り替える。
        else if (e.Key is Key.Left or Key.Right && row == 2 && _format == 0)
        {
            SelectLevel((_levelIndex + (e.Key == Key.Right ? 1 : -1) + _levelButtons.Length) % _levelButtons.Length);
            _levelButtons[_levelIndex].Focus();
            e.Handled = true;
        }
        // 左右キーで作成と中止を行き来する。
        else if (e.Key is Key.Left or Key.Right && row == 3)
        {
            (focus == ApplyButton ? CancelButton : ApplyButton).Focus();
            e.Handled = true;
        }
        // 中止ボタン以外でのEnterは作成として扱う。
        else if (e.Key == Key.Enter && focus != CancelButton)
        {
            e.Handled = true;
            Apply();
        }
    }

    /// <summary>
    /// ファイル名を検査し、選んだ形式の拡張子にそろえてダイアログを閉じる。
    /// </summary>
    private void Apply()
    {
        // ファイル名として使えない名前は止めて、入力欄へ戻す。
        string name = NameBox.Text.Trim();
        if (name.Length == 0 || name is "." or ".." || name.EndsWith('.') ||
            name.EndsWith(' ') || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            name.Contains('/') || name.Contains('\\'))
        {
            ErrorText.Text = "有効なファイル名を入力してください。";
            ErrorText.Visibility = Visibility.Visible;
            NameBox.Focus();
            return;
        }
        // 別のアーカイブ拡張子が付いていれば外し、選んだ形式の拡張子を付ける。
        string extension = Formats[_format].Extension;
        if (!name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
        {
            string known = Formats.Select(format => format.Extension).Append(".lha")
                .FirstOrDefault(ext => name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) ?? "";
            if (known.Length != 0) name = name[..^known.Length];
            name += extension;
        }
        ArchiveFileName = name;
        DialogResult = true;
        Close();
    }
}
