using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MirrorFlux.Controls;
using MirrorFlux.Services;
using MirrorFlux.Input;

namespace MirrorFlux;

/// <summary>
/// INIの %jumpPath01～10 に登録した移動先から選んでパネルを移動するダイアログ。
/// </summary>
public partial class JumpListDialog : RetroDialogWindow
{
    /// <summary>
    /// ジャンプリストの1項目。番号は2桁で表示する。
    /// </summary>
    private sealed record Entry(int Slot, string Path, string DisplayName) { public string Number => Slot.ToString("00"); }
    private readonly Entry[] _entries;
    public string? TargetPath { get; private set; }
    /// <summary>
    /// 登録済みの1～10番を番号順に並べる。1件もなければ登録方法を案内する。
    /// </summary>
    public JumpListDialog(IReadOnlyDictionary<int, string> paths)
    {
        InitializeComponent();
        InputMethod.SetIsInputMethodEnabled(this, false);
        _entries = paths.Where(p => p.Key is >= 1 and <= 10 && !string.IsNullOrWhiteSpace(p.Value))
            .OrderBy(p => p.Key).Select(p => new Entry(p.Key, p.Value, JumpListService.GetDisplayName(p.Value))).ToArray();
        Paths.ItemsSource = _entries;
        if (_entries.Length > 0) Paths.SelectedIndex = 0;
        else ErrorText.Text = "INIの %jumpPath01〜10 に移動先を登録してください。";
    }
    private void Window_Loaded(object sender, RoutedEventArgs e) { Activate(); FocusSelectedEntry(); }
    /// <summary>
    /// 選択中の項目を見える位置へ送り、その項目にフォーカスを置く。
    /// </summary>
    private void FocusSelectedEntry()
    {
        if (Paths.SelectedItem is { } selected)
        {
            Paths.ScrollIntoView(selected);
            Paths.UpdateLayout();
            if (Paths.ItemContainerGenerator.ContainerFromItem(selected) is ListBoxItem item)
            {
                item.Focus();
                return;
            }
        }
        Paths.Focus();
    }
    /// <summary>
    /// 選択中の移動先を確かめて決定する。ネットワーク先などで画面が止まらないよう、確認は別スレッドで行う。
    /// </summary>
    private async void Confirm()
    {
        // 確認中は操作を受け付けない。
        if (!IsEnabled || Paths.SelectedItem is not Entry entry) return;
        IsEnabled = false;
        var result = await Task.Run(() =>
        {
            bool ok = JumpListService.TryResolve(entry.Path, out var path, out var error);
            return (ok, path, error);
        });
        IsEnabled = true;
        // 確認中に閉じられていれば何もしない。成功なら決定し、失敗なら理由を表示する。
        if (!IsVisible) return;
        if (result.ok) { TargetPath = result.path; DialogResult = true; }
        else { ErrorText.Text = result.error; FocusSelectedEntry(); }
    }
    private void Paths_DoubleClick(object sender, MouseButtonEventArgs e) => Confirm();
    /// <summary>
    /// Enterで決定し、数字キーでその番号の項目を選ぶ。0は10番を表す。
    /// </summary>
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        Key key = e.NormalizedKey();
        if (key == Key.Enter) { e.Handled = true; Confirm(); }
        else if (key is >= Key.D0 and <= Key.D9 || key is >= Key.NumPad0 and <= Key.NumPad9)
        {
            int number = key >= Key.NumPad0 ? key - Key.NumPad0 : key - Key.D0;
            int index = Array.FindIndex(_entries, x => x.Slot == (number == 0 ? 10 : number));
            if (index >= 0) { Paths.SelectedIndex = index; FocusSelectedEntry(); }
            e.Handled = true;
        }
    }
}
