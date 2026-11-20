using System.Windows;
using System.Windows.Media;
using MirrorFlux.Models;
namespace MirrorFlux.Services;

/// <summary>ビューアーの装飾や選択の色に、INI の既存のテーマ色の役割を使う。</summary>
public static class ViewerPalette
{
    private static readonly IReadOnlyDictionary<string, Brush> Defaults = CreateDefaults();
    /// <summary>
    /// リソースにないときに使う既定のブラシを、AppSettings の既定のテーマ色から作る。
    /// </summary>
    private static IReadOnlyDictionary<string, Brush> CreateDefaults()
    {
        var settings = new AppSettings();
        var colors = new Dictionary<string, string> {
            ["UrlForeground"] = settings.TVColorUrl,
            ["MutedText"] = settings.ColMuted,
            ["HighlightText"] = settings.ColHighlight, ["DarkPurpleBackground"] = settings.ColDarkPurple,
            ["DarkPurpleHighlight"] = settings.ColDarkPurpleHighlight, ["MarkColor"] = settings.ColMark,
            ["SearchBackground"] = settings.ColSearchBg,
            ["SearchForeground"] = settings.ColSearchFg,
            ["SearchCurrentBackground"] = settings.ColSearchCurrentBg,
            ["SearchCurrentForeground"] = settings.ColSearchCurrentFg,
            ["MarkForeground"] = settings.ColPaneText
        };
        return colors.ToDictionary(pair => pair.Key, pair => {
            Brush brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(pair.Value)); brush.Freeze(); return brush;
        });
    }
    /// <summary>
    /// 役割名のブラシを要素のリソースから探し、なければ既定のブラシを返す。
    /// </summary>
    public static Brush Get(FrameworkElement element, string role) => element.TryFindResource(role) as Brush ?? Defaults[role];
}
