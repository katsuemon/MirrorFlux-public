using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using MirrorFlux.Controls;

namespace MirrorFlux;

/// <summary>
/// 入力した文字でファイル一覧をその場で絞り込むダイアログ。Escで元の絞り込みに戻す。
/// </summary>
public partial class IncrementalSearchDialog : RetroDialogWindow
{
    private readonly string _initial;
    private readonly Func<string, (int Count, string? FirstName)> _search;
    public string Query => SearchBox.Text;

    /// <summary>
    /// 初期文字列と、絞り込みを実行して件数と先頭の名前を返す処理を受け取る。
    /// </summary>
    public IncrementalSearchDialog(string initial, Func<string, (int Count, string? FirstName)> search)
    {
        _initial = initial;
        _search = search;
        InitializeComponent();
        SearchBox.Text = initial;
        UpdateResult(initial);
    }

    /// <summary>
    /// 表示が落ち着いてから入力欄にフォーカスし、全選択する。
    /// </summary>
    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            Activate();
            SearchBox.Focus();
            SearchBox.SelectAll();
        }));
    }

    /// <summary>
    /// 入力が変わるたびに絞り込み直す。初期化中に呼ばれた場合は何もしない。
    /// </summary>
    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_search != null) UpdateResult(SearchBox.Text);
    }

    /// <summary>
    /// 絞り込みを実行し、件数と先頭の名前を表示する。
    /// </summary>
    private void UpdateResult(string query)
    {
        var result = _search(query);
        ResultText.Text = result.Count == 0 ? "一致する項目はありません"
            : $"{result.Count} 件  {result.FirstName}";
    }

    /// <summary>
    /// Enterで決定し、Escでは開いたときの文字列で絞り込み直してから閉じる。
    /// </summary>
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            DialogResult = true;
            Close();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            _search(_initial);
            DialogResult = false;
            Close();
        }
    }
}
