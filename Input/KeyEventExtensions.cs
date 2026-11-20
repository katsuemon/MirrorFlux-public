using System.Windows.Input;

namespace MirrorFlux.Input;

/// <summary>
/// キーイベントの補助。
/// </summary>
public static class KeyEventExtensions
{
    /// <summary>
    /// IME変換中やAlt併用時にも、実際に押されたキーを返す。
    /// </summary>
    public static Key NormalizedKey(this KeyEventArgs e) =>
        e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key == Key.System ? e.SystemKey : e.Key;
}
