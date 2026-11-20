using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace MirrorFlux.Services.Music;

/// <summary>
/// PCがスリープに入る直前に、再生中の曲を止める。
/// 再生を続けたままスリープすると、復帰時に音声出力やNDSIFの送信が止まったまま戻らず、アプリが応答しなくなることがある。
/// ウィンドウへの WM_POWERBROADCAST（PBT_APMSUSPEND）をUIスレッドで受け、戻る前に停止を終える。
/// Modern Standby の機種でも通知が来るよう RegisterSuspendResumeNotification で登録する。
/// </summary>
public sealed class SuspendPlaybackGuard : IDisposable
{
    internal const int WmPowerBroadcast = 0x0218;
    internal const int PbtApmSuspend = 0x0004;
    private readonly Action _onSuspend;
    private HwndSource? _source;
    private IntPtr _registration;
    // 同じスリープで二重に止めないための印（復帰で0に戻す）。
    private int _stopped;

    /// <summary>ウィンドウの通知を受ける仕組みと、予備の電源イベントを登録する。</summary>
    public SuspendPlaybackGuard(Window window, Action onSuspend)
    {
        _onSuspend = onSuspend;
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        _source = HwndSource.FromHwnd(hwnd);
        _source?.AddHook(Hook);
        try { _registration = RegisterSuspendResumeNotification(hwnd, 0); } catch (Exception) { _registration = IntPtr.Zero; }
        // 予備: ウィンドウへの通知が来ない環境向け。別スレッドから来るのでUIスレッドで同期して止める。
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        _dispatcher = window.Dispatcher;
    }

    private readonly System.Windows.Threading.Dispatcher _dispatcher;

    internal static bool IsSuspendMessage(int message, IntPtr wParam) =>
        message == WmPowerBroadcast && wParam.ToInt64() == PbtApmSuspend;

    /// <summary>
    /// ウィンドウの通知を受ける。
    /// スリープ直前なら止め、復帰などの通知なら印を戻す。
    /// </summary>
    private IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (IsSuspendMessage(message, wParam)) StopOnce();
        else if (message == WmPowerBroadcast) Interlocked.Exchange(ref _stopped, 0);
        return IntPtr.Zero;
    }

    /// <summary>
    /// 予備の電源イベントを受ける。
    /// スリープならUIスレッドで止め（最大5秒待つ）、復帰なら印を戻す。
    /// </summary>
    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend)
        {
            try { _dispatcher.Invoke(StopOnce, TimeSpan.FromSeconds(5)); } catch (Exception) { }
        }
        else if (e.Mode == PowerModes.Resume) Interlocked.Exchange(ref _stopped, 0);
    }

    /// <summary>一度だけ停止の処理を呼ぶ。</summary>
    private void StopOnce()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        try { _onSuspend(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Suspend] stop failed: {ex}"); }
    }

    /// <summary>登録した通知とイベントを外す。</summary>
    public void Dispose()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _source?.RemoveHook(Hook);
        _source = null;
        if (_registration != IntPtr.Zero)
        {
            try { UnregisterSuspendResumeNotification(_registration); } catch (Exception) { }
            _registration = IntPtr.Zero;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr RegisterSuspendResumeNotification(IntPtr recipient, int flags);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterSuspendResumeNotification(IntPtr handle);
}
