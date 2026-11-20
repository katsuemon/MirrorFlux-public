using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MirrorFlux.Controls;

namespace MirrorFlux;

/// <summary>
/// ダイアログの詳細表の1行。IsHighlightなら値を強調色の太字にする。
/// </summary>
public sealed record DialogDetailRow(string Label, string Value, bool IsHighlight = false);

/// <summary>
/// エラー内容と詳細の表を表示するダイアログ。O/Enter/Esc/Spaceで閉じる。
/// </summary>
public partial class ErrorDialog : RetroDialogWindow
{
    /// <summary>
    /// 見出し・説明文・詳細行を設定する。
    /// </summary>
    public ErrorDialog(
        string title,
        string message,
        IEnumerable<DialogDetailRow>? details = null)
    {
        InitializeComponent();

        // 見出しと説明文を設定する。
        DialogHeader.TitleText = title;
        Title = title;
        PromptText.Text = message;

        // 詳細を「見出し : [ 値 ]」の行として並べる。
        if (details != null)
        {
            var highlightBrush = (Brush)FindResource("HighlightText");
            var paneTextBrush = (Brush)FindResource("PaneText");
            var mutedBrush = (Brush)FindResource("MutedText");

            foreach (var detail in details)
            {
                var grid = new Grid { Height = 22 };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var lbl = new TextBlock
                {
                    Text = detail.Label,
                    Foreground = mutedBrush,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(lbl, 0);

                var colon = new TextBlock
                {
                    Text = ":",
                    Foreground = mutedBrush,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(colon, 1);

                var val = new TextBlock
                {
                    Text = $"[ {detail.Value} ]",
                    Foreground = detail.IsHighlight ? highlightBrush : paneTextBrush,
                    FontWeight = detail.IsHighlight ? FontWeights.Bold : FontWeights.Normal,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(val, 2);

                grid.Children.Add(lbl);
                grid.Children.Add(colon);
                grid.Children.Add(val);

                DetailsPanel.Children.Add(grid);
            }
        }
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        Focus();
    }

    /// <summary>
    /// O/Enter/Esc/Spaceのどれかで閉じる。
    /// </summary>
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.O:
            case Key.Enter:
            case Key.Escape:
            case Key.Space:
                e.Handled = true;
                DialogResult = true;
                Close();
                break;
        }
    }

    private void OptionOk_Click(object sender, MouseButtonEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
