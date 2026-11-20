using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using MirrorFlux.Services.Music;

/// <summary>スリープ直前の通知で再生停止が1回だけ呼ばれ、復帰後は次のスリープでまた呼ばれることを確かめる。</summary>
internal static class SuspendPlaybackRegression
{
    public static void Run(Action<bool, string> check)
    {
        check(SuspendPlaybackGuard.IsSuspendMessage(0x0218, (IntPtr)4) &&
              !SuspendPlaybackGuard.IsSuspendMessage(0x0218, (IntPtr)0x12) &&
              !SuspendPlaybackGuard.IsSuspendMessage(0x0010, (IntPtr)4),
            "Only WM_POWERBROADCAST/PBT_APMSUSPEND is treated as entering sleep");

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            Window? window = null;
            try
            {
                window = new Window { Width = 100, Height = 100, Left = -10000, Top = -10000, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
                int stops = 0;
                using var guard = new SuspendPlaybackGuard(window, () => stops++);
                IntPtr hwnd = new WindowInteropHelper(window).Handle;
                SendMessage(hwnd, 0x0218, (IntPtr)4, IntPtr.Zero);
                SendMessage(hwnd, 0x0218, (IntPtr)4, IntPtr.Zero);
                check(stops == 1, "Playback is stopped once when the PC enters sleep");
                SendMessage(hwnd, 0x0218, (IntPtr)0x12, IntPtr.Zero); // PBT_APMRESUMEAUTOMATIC
                SendMessage(hwnd, 0x0218, (IntPtr)4, IntPtr.Zero);
                check(stops == 2, "After resuming, the next sleep stops playback again");
            }
            catch (Exception ex) { failure = ex; }
            finally { window?.Close(); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure != null) throw failure;
    }

    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);
}
