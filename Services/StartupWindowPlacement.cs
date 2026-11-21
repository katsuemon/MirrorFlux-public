using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace MirrorFlux.Services;

/// <summary>
/// 起動時と高さ変更時のウィンドウ位置・高さを調整する。ウィンドウがモニターの作業領域からはみ出さないようにする。
/// </summary>
internal static class StartupWindowPlacement
{
    /// <summary>
    /// ウィンドウが作業領域に収まっていなければ、作業領域の中央に置いた位置を返す。収まっていればそのまま返す。
    /// </summary>
    internal static Rect CenteredIfOutside(Rect bounds, Rect workArea)
    {
        if (bounds.IsEmpty || workArea.IsEmpty || workArea.Width <= 0 || workArea.Height <= 0 || workArea.Contains(bounds))
            return bounds;
        return new Rect(workArea.Left + (workArea.Width - bounds.Width) / 2,
            workArea.Top + (workArea.Height - bounds.Height) / 2, bounds.Width, bounds.Height);
    }

    /// <summary>
    /// ファイル一覧が指定の行数になるウィンドウの高さを返す。上限・下限を超えるときは、行の途中で切れないよう1行単位で調整する。
    /// </summary>
    internal static double HeightForRows(double windowHeight, double listHeight, int rows, double min, double max, double rowHeight = 20)
    {
        double height = windowHeight + rows * rowHeight - listHeight;
        while (height > max && height - rowHeight >= min) height -= rowHeight;
        while (height < min) height += rowHeight;
        return height;
    }

    /// <summary>
    /// 高さ area の領域を、ウィンドウの高さを current から target へ変えたときにも同じ整数行数で埋められるか。
    /// area が0以下（まだ配置されていない）なら判断できないので false を返す。
    /// </summary>
    internal static bool SameWholeRows(double area, double current, double target, double rowHeight) =>
        area > 0 && Math.Floor(area / rowHeight) == Math.Floor((area + target - current) / rowHeight);

    /// <summary>通常表示中の位置を画面ピクセルで記録する。最小化中の特殊座標は保存しない。</summary>
    internal static Rect? CapturePosition(Window window)
    {
        if (window.WindowState != WindowState.Normal) return null;
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        return hwnd != IntPtr.Zero && GetWindowRect(hwnd, out var bounds) ? bounds.ToRect() : null;
    }

    internal static Rect PositionInside(Rect bounds, Rect work) => new(
        Math.Clamp(bounds.Left, work.Left, Math.Max(work.Left, work.Right - bounds.Width)),
        Math.Clamp(bounds.Top, work.Top, Math.Max(work.Top, work.Bottom - bounds.Height)), bounds.Width, bounds.Height);

    /// <summary>保存した画面に戻す。画面が撤去された場合は最寄りの画面内へ収める。</summary>
    internal static void RestorePosition(Window window, Rect? saved)
    {
        if (saved is not Rect position || window.WindowState != WindowState.Normal) return;
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var current)) return;
        var bounds = new NativeRect { Left = (int)position.Left, Top = (int)position.Top,
            Right = (int)position.Right, Bottom = (int)position.Bottom };
        IntPtr monitor = MonitorFromRect(ref bounds, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return;
        Rect target = PositionInside(new Rect(position.Location, current.ToRect().Size), info.Work.ToRect());
        SetWindowPos(hwnd, IntPtr.Zero, (int)Math.Round(target.Left), (int)Math.Round(target.Top),
            0, 0, 0x0001 | 0x0004 | 0x0010);
    }

    /// <summary>ウィンドウがあるモニターの作業領域の高さ（ピクセル）。取得できなければ0。</summary>
    internal static double WorkAreaHeight(Window window)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return 0;
        IntPtr monitor = MonitorFromWindow(hwnd, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        return monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info) ? info.Work.Bottom - info.Work.Top : 0;
    }

    /// <summary>
    /// 下端が作業領域からはみ出さない上端の位置を返す。上端は作業領域より上にしない。
    /// </summary>
    internal static double TopInside(Rect bounds, Rect workArea) =>
        bounds.Bottom <= workArea.Bottom ? bounds.Top : Math.Max(workArea.Top, workArea.Bottom - bounds.Height);

    /// <summary>高さを変えた後、下端が作業領域からはみ出していれば上へ寄せる。左右の位置は変えない。</summary>
    internal static void KeepBottomInside(Window window)
    {
        // ウィンドウの位置と、いちばん近いモニターの作業領域を取る。
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var bounds)) return;
        IntPtr monitor = MonitorFromWindow(hwnd, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return;
        // 上端を直す必要があれば、大きさ・重なり順・アクティブ状態を変えずに動かす。
        Rect current = bounds.ToRect();
        double top = TopInside(current, info.Work.ToRect());
        if (top == current.Top) return;
        SetWindowPos(hwnd, IntPtr.Zero, (int)Math.Round(current.Left), (int)Math.Round(top),
            0, 0, 0x0001 | 0x0004 | 0x0010);
    }

    /// <summary>
    /// 起動時、ウィンドウが作業領域からはみ出していれば、いちばん近いモニターの中央へ動かす。
    /// </summary>
    internal static void Apply(Window window)
    {
        // ウィンドウの位置と、いちばん近いモニター（MONITOR_DEFAULTTONEAREST）の作業領域を取る。
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var bounds)) return;
        IntPtr monitor = MonitorFromWindow(hwnd, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return;
        // WPF の DIP とモニターの座標を混ぜず、最後まで画面のピクセル座標で計算する。原点が負のモニターにも対応できる。
        // 動かすときは大きさ・重なり順・アクティブ状態を変えない（NOSIZE | NOZORDER | NOACTIVATE）。
        Rect current = bounds.ToRect();
        Rect target = CenteredIfOutside(current, info.Work.ToRect());
        if (target == current) return;
        SetWindowPos(hwnd, IntPtr.Zero, (int)Math.Round(target.Left), (int)Math.Round(target.Top),
            0, 0, 0x0001 | 0x0004 | 0x0010);
    }

    /// <summary>
    /// Win32 の RECT。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
        public readonly Rect ToRect() => new(Left, Top, Right - Left, Bottom - Top);
    }
    /// <summary>
    /// Win32 の MONITORINFO。モニター全体と作業領域の範囲。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor, Work;
        public uint Flags;
    }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect bounds);
    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromRect(ref NativeRect rect, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
