using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace MirrorFlux.Controls;

/// <summary>
/// ダイアログ共通の基底ウィンドウ。
/// 枠なし・サイズ変更不可・タスクバー非表示で、ダイアログ用の背景と等幅フォントを使う。
/// Esc または Alt+F4 でキャンセルして閉じ、本体のドラッグで移動できる。
/// パネルの上や親ウィンドウの中央に置く補助を持ち、親ウィンドウが動けば一緒に動く。
/// 親ウィンドウを無効化しないので、表示中も親の移動や終了を妨げない。
/// </summary>
public class RetroDialogWindow : Window
{
    [DllImport("user32.dll")]
    private static extern bool EnableWindow(IntPtr hWnd, bool bEnable);

    private double _lastOwnerLeft;
    private double _lastOwnerTop;

    /// <summary>ダイアログ共通の見た目を設定する。高さは内容に合わせる。</summary>
    public RetroDialogWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "DialogBackground");
        FontFamily = new FontFamily("Consolas, BIZ UDGothic, MS Gothic");
        FontSize = 14;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        SizeToContent = SizeToContent.Height;

        Loaded += RetroDialogWindow_Loaded;
    }

    /// <summary>表示時に親ウィンドウを有効に戻し、親の移動を追いかけるよう登録する。</summary>
    private void RetroDialogWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (Owner != null)
        {
            // ShowDialog が無効化した親ウィンドウを有効に戻し、親の位置を覚えて移動を購読する。
            var ownerHwnd = new WindowInteropHelper(Owner).Handle;
            if (ownerHwnd != IntPtr.Zero)
            {
                EnableWindow(ownerHwnd, true);
            }
            _lastOwnerLeft = Owner.Left;
            _lastOwnerTop = Owner.Top;
            Owner.LocationChanged -= Owner_LocationChanged;
            Owner.LocationChanged += Owner_LocationChanged;

            // WPF の ShowDialog が後から親を無効化することがあるため、アイドル時にもう一度有効に戻す。
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
            {
                if (Owner != null)
                {
                    var h = new WindowInteropHelper(Owner).Handle;
                    if (h != IntPtr.Zero)
                    {
                        EnableWindow(h, true);
                    }
                }
            });
        }
    }

    /// <summary>親ウィンドウが動いた分だけダイアログも動かす。</summary>
    private void Owner_LocationChanged(object? sender, EventArgs e)
    {
        if (Owner != null && !double.IsNaN(Owner.Left) && !double.IsNaN(Owner.Top))
        {
            double dx = Owner.Left - _lastOwnerLeft;
            double dy = Owner.Top - _lastOwnerTop;
            _lastOwnerLeft = Owner.Left;
            _lastOwnerTop = Owner.Top;
            if (!double.IsNaN(Left) && !double.IsNaN(Top))
            {
                Left += dx;
                Top += dy;
            }
        }
    }

    /// <summary>閉じたら親ウィンドウの移動の購読を解除する。</summary>
    protected override void OnClosed(EventArgs e)
    {
        if (Owner != null)
        {
            Owner.LocationChanged -= Owner_LocationChanged;
        }
        base.OnClosed(e);
    }

    /// <summary>入力欄以外を左ボタンで押したら、ドラッグでダイアログを移動する。</summary>
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (e.ButtonState == MouseButtonState.Pressed && e.OriginalSource is not TextBox)
        {
            try { DragMove(); } catch { }
        }
    }

    /// <summary>Esc または Alt+F4 が未処理なら、キャンセル扱いで閉じる。</summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (!e.Handled)
        {
            if (e.Key == Key.Escape || (e.Key == Key.System && e.SystemKey == Key.F4))
            {
                e.Handled = true;
                DialogResult = false;
                Close();
            }
        }
    }

    /// <summary>
    /// 指定したパネル（左または右）の上部中央にダイアログを置く。
    /// </summary>
    public void PositionOverPane(MainWindow mw, bool isLeftPane, double topOffset = 24)
    {
        Owner = mw;
        WindowStartupLocation = WindowStartupLocation.Manual;
        // パネルの位置と幅から、横は中央、縦はパネル上端から topOffset 下に置く。幅が未定なら460とみなす。
        var relPoint = mw.GetPaneTopLeft(isLeftPane);
        double paneWidth = mw.GetPaneActualWidth(isLeftPane);

        double dialogWidth = Width > 0 ? Width : 460;
        Left = mw.Left + relPoint.X + Math.Max(0, (paneWidth - dialogWidth) / 2);
        Top = mw.Top + relPoint.Y + topOffset;
    }

    /// <summary>
    /// 親ウィンドウの中央にダイアログを置く。
    /// </summary>
    public void PositionCenter(MainWindow mw)
    {
        Owner = mw;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
    }
}
