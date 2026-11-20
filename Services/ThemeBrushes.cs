using System.Windows;
using System.Windows.Media;
using MirrorFlux.Models;

namespace MirrorFlux.Services;

/// <summary>
/// 描画コードがテーマ色のブラシを取るための共通の入口。リソースにないとき（テストなど App.xaml がない場合）は、
/// 各所でばらばらに決めた代わりの色ではなく、INI の既定値（AppSettings の既定のテーマ色）を使う。
/// </summary>
internal static class ThemeBrushes
{
    private static readonly Lazy<Dictionary<string, Brush>> Defaults = new(BuildDefaults);

    /// <summary>
    /// key のブラシを返す。element が指定されればその要素から（親やアプリのリソースも含めて）探し、
    /// なければアプリのリソース、それもなければ既定のテーマ色を使う。既定にもないキーは透明にする。
    /// </summary>
    public static Brush Get(string key, FrameworkElement? element = null) =>
        (element?.TryFindResource(key) ?? Application.Current?.TryFindResource(key)) as Brush
        ?? (Defaults.Value.TryGetValue(key, out var brush) ? brush : Brushes.Transparent);

    /// <summary>
    /// AppSettings の既定のテーマ色から、フリーズ済みのブラシの表を作る。読めない色は飛ばす。
    /// </summary>
    private static Dictionary<string, Brush> BuildDefaults()
    {
        var settings = new AppSettings();
        var result = new Dictionary<string, Brush>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in settings.ThemeColors) Add(pair.Key, pair.Value);
        foreach (var item in ThemeKeyCatalog.Core) Add(item.Key, item.Get(settings));
        Add("UrlForeground", settings.TVColorUrl);
        return result;

        void Add(string key, string hex)
        {
            try
            {
                if (ColorConverter.ConvertFromString(hex) is Color color)
                {
                    var brush = new SolidColorBrush(color);
                    brush.Freeze();
                    result[key] = brush;
                }
            }
            catch (FormatException) { }
        }
    }
}
