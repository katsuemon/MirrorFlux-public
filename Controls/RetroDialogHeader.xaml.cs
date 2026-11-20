using System.Windows;
using System.Windows.Controls;

namespace MirrorFlux.Controls;

/// <summary>ダイアログ上端の見出し帯。横縞の間に TitleText の題名を表示する。</summary>
public partial class RetroDialogHeader : UserControl
{
    public static readonly DependencyProperty TitleTextProperty =
        DependencyProperty.Register(
            nameof(TitleText),
            typeof(string),
            typeof(RetroDialogHeader),
            new PropertyMetadata(string.Empty));

    public string TitleText
    {
        get => (string)GetValue(TitleTextProperty);
        set => SetValue(TitleTextProperty, value);
    }

    public RetroDialogHeader()
    {
        InitializeComponent();
    }
}
