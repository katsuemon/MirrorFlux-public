using System.Windows;
using System.Windows.Media;
using MirrorFlux.Models;

namespace MirrorFlux.Services;

/// <summary>
/// 設定のテーマ色をアプリのリソースのブラシへ反映する。
/// </summary>
internal static class SettingsTheme
{
    /// <summary>
    /// 名前付きのテーマ色、マークの文字色、INI で指定された任意のブラシの順に反映する。
    /// </summary>
    public static void Apply(AppSettings settings)
    {
        var defaults = new AppSettings();
        var app = Application.Current;
        if (app is null) return;

        // 名前付きのテーマ色（ThemeKeyCatalog.Core）。値が読めないときは既定値に戻す。MarkForeground は下で別に扱う。
        foreach (var item in ThemeKeyCatalog.Core)
        {
            if (item.Key == "MarkForeground") continue;
            SetBrush(app, item.Key, item.Get(settings), SafeParseColor(item.Get(defaults), Colors.Black));
        }
        SetBrush(app, "UrlForeground", settings.TVColorUrl, SafeParseColor(defaults.TVColorUrl, Colors.LightSkyBlue));

        // マークの文字色をマーク色の明るさで決める。明るいマーク色なら画面の背景色で反転させ、暗いマーク色なら通常の文字色のままにする。
        var screenColor = SafeParseColor(settings.ColScreen, SafeParseColor(defaults.ColScreen, Colors.Black));
        var paneTextColor = SafeParseColor(settings.ColPaneText, SafeParseColor(defaults.ColPaneText, Colors.White));
        var markBgColor = SafeParseColor(settings.ColMark, SafeParseColor(defaults.ColMark, Colors.Gray));

        // 輝度（0～255）が 128 を超えれば明るいとみなす。
        double markLuminance = 0.299 * markBgColor.R + 0.587 * markBgColor.G + 0.114 * markBgColor.B;
        Color defaultMarkFg = markLuminance > 128 ? screenColor : paneTextColor;

        // 文字色の指定が「auto」以外ならそれを使う。
        if (!string.IsNullOrWhiteSpace(settings.ColMarkFg) && !settings.ColMarkFg.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            SetBrush(app, "MarkForeground", settings.ColMarkFg, defaultMarkFg);
        }
        else
        {
            app.Resources["MarkForeground"] = new SolidColorBrush(defaultMarkFg);
        }

        // アプリが定義したブラシは、専用の設定項目がなくても INI で上書きできる。INI を手で書きやすいよう名前は大文字・小文字を区別しない。
        var brushKeys = app.Resources.Keys.Cast<object>()
            .Where(resourceKey => app.Resources[resourceKey] is Brush)
            .ToDictionary(resourceKey => resourceKey.ToString()!, resourceKey => resourceKey, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in settings.ThemeColors)
        {
            if (!brushKeys.TryGetValue(pair.Key, out var actualKey)) continue;
            var current = app.Resources[actualKey] as SolidColorBrush;
            Color fallback = current?.Color ?? Colors.Transparent;
            SetBrush(app, actualKey, pair.Value, fallback);
        }
    }

    /// <summary>
    /// 色の文字列を色に変換する。読めなければ fallback を返す。
    /// </summary>
    private static Color SafeParseColor(string hexColor, Color fallback)
    {
        try
        {
            if (ColorConverter.ConvertFromString(hexColor) is Color c)
            {
                return c;
            }
        }
        catch
        {
        }
        return fallback;
    }

    private static void SetBrush(Application app, object resourceKey, string hexColor, Color fallback)
    {
        app.Resources[resourceKey] = new SolidColorBrush(SafeParseColor(hexColor, fallback));
    }

}
