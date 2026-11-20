using System.Windows;
using System.Windows.Controls;
namespace MirrorFlux.Controls;

/// <summary>
/// ダイアログ下部のキーヘルプ。細い区切り線と、中央揃えの小さな説明文を並べる。
/// 上書き確認ダイアログと同じ形式。
/// </summary>
public sealed class RetroDialogKeyHelp : StackPanel
{
    private readonly TextBlock _label;
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(RetroDialogKeyHelp),
        new PropertyMetadata("", (owner, args) => ((RetroDialogKeyHelp)owner)._label.Text = (string)args.NewValue));
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    /// <summary>区切り線と説明文を作る。色はテーマのリソースに追従させる。</summary>
    public RetroDialogKeyHelp()
    {
        SnapsToDevicePixels = true; UseLayoutRounding = true;
        // 1ピクセルの区切り線を置く。
        var divider = new Border { Height = 1, Margin = new Thickness(0, 2, 0, 4), SnapsToDevicePixels = true };
        divider.SetResourceReference(Border.BackgroundProperty, "DarkPurpleHighlight");
        Children.Add(divider);
        // キーの説明文を中央揃えで置く。
        _label = new TextBlock { FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
        _label.SetResourceReference(TextBlock.ForegroundProperty, "MutedText"); Children.Add(_label);
    }
}
