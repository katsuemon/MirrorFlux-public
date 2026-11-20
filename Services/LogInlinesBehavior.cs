using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using MirrorFlux.Models;

namespace MirrorFlux.Services;

/// <summary>
/// ログの1行を TextBlock に色分けして表示する添付プロパティ。時刻・種別・「[ ]」で囲んだ部分をテーマ色で塗り分ける。
/// </summary>
public static class LogInlinesBehavior
{
    private static readonly Regex BracketRegex = new(@"(\[[^\]]*\])", RegexOptions.Compiled);

    public static readonly DependencyProperty LogMessageProperty =
        DependencyProperty.RegisterAttached(
            "LogMessage",
            typeof(LogMessage),
            typeof(LogInlinesBehavior),
            new PropertyMetadata(null, OnLogMessageChanged));

    public static LogMessage? GetLogMessage(DependencyObject obj) => (LogMessage?)obj.GetValue(LogMessageProperty);
    public static void SetLogMessage(DependencyObject obj, LogMessage? value) => obj.SetValue(LogMessageProperty, value);

    /// <summary>
    /// ログが設定されたら、TextBlock の中身を色分けした文字列で作り直す。
    /// </summary>
    private static void OnLogMessageChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock tb) return;
        tb.Inlines.Clear();
        if (e.NewValue is not LogMessage log) return;

        var mutedBrush = ThemeBrushes.Get("MutedText", tb);
        var highlightBrush = ThemeBrushes.Get("HighlightText", tb);
        var primaryBrush = ThemeBrushes.Get("PrimaryText", tb);

        // 時刻は控えめな色にする。
        tb.Inlines.Add(new Run(log.Timestamp) { Foreground = mutedBrush });
        tb.Inlines.Add(new Run(" "));

        // [INFO] などの種別は強調色にする。
        tb.Inlines.Add(new Run(log.LevelTag) { Foreground = highlightBrush });
        tb.Inlines.Add(new Run(" "));

        // 本文は「[ ]」で囲んだ部分を強調色、残りを通常の文字色にする。
        var parts = BracketRegex.Split(log.Message);
        foreach (var part in parts)
        {
            if (string.IsNullOrEmpty(part)) continue;
            if (part.StartsWith('[') && part.EndsWith(']'))
            {
                tb.Inlines.Add(new Run(part) { Foreground = highlightBrush });
            }
            else
            {
                tb.Inlines.Add(new Run(part) { Foreground = primaryBrush });
            }
        }
    }
}
