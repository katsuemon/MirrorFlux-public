using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using MirrorFlux.Controls;
using MirrorFlux.Services;

namespace MirrorFlux;

/// <summary>
/// カーソル位置の1項目の名前・属性・更新日時を編集するダイアログ。
/// </summary>
public partial class FileRenameDialog : RetroDialogWindow
{
    private readonly string _sourcePath;
    private readonly bool _isDirectory;
    private readonly Control[][] _navigationRows;
    public string? ResultPath { get; private set; }

    /// <summary>
    /// 対象の現在の名前・属性・更新日時を読み込み、矢印キーで移動する行の並びを作る。
    /// </summary>
    public FileRenameDialog(string sourcePath)
    {
        InitializeComponent();
        _sourcePath = sourcePath;
        _isDirectory = Directory.Exists(sourcePath);
        FileSystemInfo item = _isDirectory ? new DirectoryInfo(sourcePath) : new FileInfo(sourcePath);
        // 上下キーで移動する行を並べる。フォルダーには拡張子の行がない。
        var navigationRows = new List<Control[]>
        {
            new[] { NameBox },
            new[] { NameUpperButton, NameLowerButton, NameCapitalButton }
        };
        if (!_isDirectory) navigationRows.Add([ExtensionUpperButton, ExtensionLowerButton, ExtensionCapitalButton]);
        navigationRows.AddRange([[ReadOnlyToggle], [HiddenToggle], [SystemToggle], [DateBox], [TimeBox], [ApplyButton, CancelButton]]);
        _navigationRows = navigationRows.ToArray();
        // フォルダーのときは見出しを変え、拡張子の行を隠す。
        if (_isDirectory)
        {
            Title = "フォルダー情報の編集";
            DialogHeader.TitleText = "フォルダー情報の編集";
            NameLabel.Text = "フォルダー名";
            ExtensionRow.Height = new GridLength(0);
            ExtensionLabel.Visibility = Visibility.Collapsed;
            ExtensionButtonsPanel.Visibility = Visibility.Collapsed;
        }
        // 現在の値を「現在」欄と編集欄の両方に入れる。
        OriginalName.Text = NameBox.Text = item.Name;
        ReadOnlyToggle.IsChecked = item.Attributes.HasFlag(FileAttributes.ReadOnly);
        HiddenToggle.IsChecked = item.Attributes.HasFlag(FileAttributes.Hidden);
        SystemToggle.IsChecked = item.Attributes.HasFlag(FileAttributes.System);
        CurrentReadOnly.Text = ReadOnlyToggle.IsChecked == true ? "あり" : "なし";
        CurrentHidden.Text = HiddenToggle.IsChecked == true ? "あり" : "なし";
        CurrentSystem.Text = SystemToggle.IsChecked == true ? "あり" : "なし";
        CurrentDate.Text = DateBox.Text = item.LastWriteTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        CurrentTime.Text = TimeBox.Text = item.LastWriteTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 名前欄にフォーカスし、ファイルなら拡張子を除いた部分を選択する。
    /// </summary>
    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        NameBox.Focus();
        NameBox.Select(0, _isDirectory ? NameBox.Text.Length : Path.GetFileNameWithoutExtension(NameBox.Text).Length);
    }

    /// <summary>
    /// ボタンのTag（対象:変換方法）に従って、名前全体または拡張子の大文字・小文字を変える。
    /// </summary>
    private void Case_Click(object sender, RoutedEventArgs e)
    {
        var parts = ((string)((Button)sender).Tag).Split(':');
        NameBox.Text = FileRenameService.ConvertCase(NameBox.Text, parts[0] == "ext", parts[1]);
    }

    /// <summary>
    /// 矢印キーで項目を移動し、Enterで属性の切り替えまたは変更の適用を行う。
    /// </summary>
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // 修飾キーなしの矢印キーは項目移動として処理する。
        if (Keyboard.Modifiers != ModifierKeys.None) return;
        var focused = Keyboard.FocusedElement as Control;
        if (focused is not null && HandleArrowKey(e.Key, focused))
        {
            e.Handled = true;
            return;
        }
        // 属性ボタンでのEnterは「あり／なし」を切り替える。
        if (e.Key == Key.Enter && focused is ToggleButton toggle)
        {
            toggle.IsChecked = toggle.IsChecked != true;
            e.Handled = true;
        }
        // ボタンでのEnterは通常どおり押下として扱い、入力欄でのEnterは変更を適用する。
        else if (e.Key == Key.Enter && focused is not Button)
        {
            e.Handled = true;
            Apply();
        }
    }

    /// <summary>
    /// 上下キーで行を移動し、左右キーで同じ行のボタン移動や属性の切り替えを行う。処理したらtrueを返す。
    /// </summary>
    private bool HandleArrowKey(Key key, Control focused)
    {
        // フォーカスのある行と列を調べる。
        int row = Array.FindIndex(_navigationRows, controls => controls.Contains(focused));
        if (row < 0) return false;
        int column = Array.IndexOf(_navigationRows[row], focused);
        // 上下キーで行を循環して移動する。列はなるべく保つ。
        if (key is Key.Up or Key.Down)
        {
            int nextRow = (row + (key == Key.Down ? 1 : -1) + _navigationRows.Length) % _navigationRows.Length;
            var controls = _navigationRows[nextRow];
            controls[Math.Min(column, controls.Length - 1)].Focus();
            return true;
        }
        if (key is not (Key.Left or Key.Right)) return false;
        // 名前・日付・時刻の入力欄では、左右キーを通常のカーソル移動に残す。
        if (focused is TextBox) return false;
        // 属性ボタンは右で「あり」、左で「なし」にする。
        if (focused is ToggleButton toggle)
        {
            toggle.IsChecked = key == Key.Right;
            return true;
        }
        // ボタンの行では左右に循環して移動する。
        var buttons = _navigationRows[row];
        int nextColumn = (column + (key == Key.Right ? 1 : -1) + buttons.Length) % buttons.Length;
        buttons[nextColumn].Focus();
        return true;
    }

    private void Apply_Click(object sender, RoutedEventArgs e) => Apply();

    /// <summary>
    /// 入力内容を検査して名前・属性・更新日時を変更する。失敗したら理由を表示してダイアログを残す。
    /// </summary>
    private void Apply()
    {
        try
        {
            // 更新日時を読み取る。Windowsのファイル時刻は1601年より前を扱えない。
            if (!DateTime.TryParseExact(DateBox.Text + " " + TimeBox.Text, "yyyy-MM-dd HH:mm:ss",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var modified) || modified.Year < 1601)
                throw new IOException("更新日時を yyyy-MM-dd HH:mm:ss の形式で入力してください（1601年以降）。");
            // 日時を変えていなければ更新日時には触れない。
            DateTime? changedTime = DateBox.Text == CurrentDate.Text && TimeBox.Text == CurrentTime.Text
                ? null : DateTime.SpecifyKind(modified, DateTimeKind.Local);
            // 変更を適用して、新しいパスを結果にする。
            ResultPath = FileRenameService.Apply(_sourcePath, new FileRenameOptions(NameBox.Text,
                ReadOnlyToggle.IsChecked == true, HiddenToggle.IsChecked == true,
                SystemToggle.IsChecked == true, changedTime));
            DialogResult = true;
            Close();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ErrorText.Text = ex.Message;
            ErrorText.Visibility = Visibility.Visible;
        }
    }
}
