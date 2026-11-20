using System.Windows;
using System.Windows.Media;

using MirrorFlux.Services;

namespace MirrorFlux.Controls;

/// <summary>
/// 音源チップのレジスタ1ページ分（16×16）を16進で表示する部品。
/// ビューアの8×14ピクセルの文字グリッドに合わせて並べる。
/// </summary>
public sealed class MusicRegisterDisplay : FrameworkElement
{
    private byte[]? _values;

    /// <summary>表示するレジスタ値を差し替えて描き直す。</summary>
    public void SetValues(byte[]? values)
    {
        _values = values;
        InvalidateVisual();
    }

    /// <summary>列見出し、行見出し、各アドレスの値を描く。値がないアドレスは「--」とする。</summary>
    protected override void OnRender(DrawingContext dc)
    {
        var text = ThemeBrushes.Get("PrimaryText", this);
        var muted = ThemeBrushes.Get("MutedText", this);
        // 上端に +0〜+F の列見出しを描く。
        for (int column = 0; column < 16; column++)
            MusicDisplayDrawing.TextAt(dc, $"+{column:X1}", 16 + column * 16, 0, 14, muted);
        // 各行の先頭に行見出しを描き、続けて16個の値を描く。
        for (int row = 0; row < 16; row++)
        {
            double y = (row + 1) * 14;
            MusicDisplayDrawing.TextAt(dc, $"{row:X1}0", 0, y, 14, muted);
            for (int column = 0; column < 16; column++)
            {
                int address = row * 16 + column;
                string value = _values != null && address < _values.Length ? $"{_values[address]:X2}" : "--";
                MusicDisplayDrawing.TextAt(dc, value, 16 + column * 16, y, 14, text);
            }
        }
    }
}
