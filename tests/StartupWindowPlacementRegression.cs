using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using MirrorFlux.Services;

internal static class StartupWindowPlacementRegression
{
    public static void Run(Action<bool, string> check)
    {
        Rect work = new(0, 0, 1920, 1040);
        Rect inside = new(100, 100, 1288, 832);
        check(StartupWindowPlacement.CenteredIfOutside(inside, work) == inside, "Startup keeps a fully visible position");
        check(StartupWindowPlacement.CenteredIfOutside(work, work) == work, "Startup accepts exact work-area edges");
        foreach (var bounds in new[] { new Rect(-1, 80, 1288, 832), new Rect(700, 80, 1288, 832), new Rect(80, -1, 1288, 832), new Rect(80, 250, 1288, 832) })
            check(StartupWindowPlacement.CenteredIfOutside(bounds, work) == new Rect(316, 104, 1288, 832), "Startup centers a window crossing a work-area edge: " + bounds.Location);
        foreach (double scale in new[] { 1.0, 1.25, 1.5, 2.0 })
        {
            var narrow = new Rect(-1366, 30, 1366, 698);
            var bounds = new Rect(-1300, 80, 1288 * scale, 832 * scale);
            var result = StartupWindowPlacement.CenteredIfOutside(bounds, narrow);
            check(Math.Abs(result.Left + result.Width / 2 - (narrow.Left + narrow.Width / 2)) < .01 &&
                Math.Abs(result.Top + result.Height / 2 - (narrow.Top + narrow.Height / 2)) < .01 && result.Size == bounds.Size,
                $"Startup centers oversized windows on a negative-origin monitor at {scale:P0} without resizing");
        }
        var taskbar = new Rect(48, 30, 1872, 1010);
        check(StartupWindowPlacement.CenteredIfOutside(new Rect(0, 0, 1288, 832), taskbar) == new Rect(340, 119, 1288, 832),
            "Startup center excludes top and left taskbars");
        // %fileRows: ファイル一覧の行数からウィンドウの高さを決める。標準は高さ832で一覧が560（28行）とした例。
        check(StartupWindowPlacement.HeightForRows(832, 560, 28, 640, 1040) == 832 &&
            StartupWindowPlacement.HeightForRows(832, 560, 35, 640, 1040) == 972 &&
            StartupWindowPlacement.HeightForRows(832, 560, 20, 640, 1040) == 672,
            "File rows change the window height by one row height each");
        check(StartupWindowPlacement.HeightForRows(832, 560, 60, 640, 1040) == 1032 &&
            StartupWindowPlacement.HeightForRows(832, 560, 5, 640, 1040) == 652,
            "File rows beyond the screen or below the minimum height are adjusted by whole rows");
        check(StartupWindowPlacement.HeightForRows(972, 700, 20, 640, 1040) == 672,
            "File rows are computed from the current list height, so applying them twice is stable");
        check(StartupWindowPlacement.TopInside(new Rect(100, 100, 1288, 832), work) == 100 &&
            StartupWindowPlacement.TopInside(new Rect(100, 300, 1288, 972), work) == 68 &&
            StartupWindowPlacement.TopInside(new Rect(100, 300, 1288, 1200), work) == 0,
            "A taller window is moved up only as far as needed to keep its bottom on screen");
        // サウンドビューア: 曲リスト（18の整数行）の行数が標準の高さと同じなら、ウィンドウの高さを変えない。
        check(StartupWindowPlacement.SameWholeRows(172, 829, 832, 18) && StartupWindowPlacement.SameWholeRows(175, 832, 832, 18),
            "The player keeps a slightly shorter window when its track list shows the same whole rows");
        check(!StartupWindowPlacement.SameWholeRows(161, 829, 832, 18) && !StartupWindowPlacement.SameWholeRows(100, 689, 832, 18) &&
            !StartupWindowPlacement.SameWholeRows(0, 829, 832, 18),
            "The player returns to the standard height when a track row would be lost or the layout is not ready");
        VerifyNativeStartup(check);
    }

    private static void VerifyNativeStartup(Action<bool, string> check)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            Window? window = null;
            try
            {
                window = new Window { Width = 300, Height = 200, Left = -100000, Top = -100000, WindowStartupLocation = WindowStartupLocation.Manual, ShowInTaskbar = false };
                bool initialized = false;
                window.SourceInitialized += (_, _) => { initialized = true; StartupWindowPlacement.Apply(window); };
                IntPtr hwnd = new WindowInteropHelper(window).EnsureHandle(); // Hidden: no user settings or visible window.
                var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
                bool read = GetWindowRect(hwnd, out var rect) && GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref info);
                check(initialized && read && Math.Abs((rect.Left + rect.Right) / 2.0 - (info.Work.Left + info.Work.Right) / 2.0) <= 1 &&
                    Math.Abs((rect.Top + rect.Bottom) / 2.0 - (info.Work.Top + info.Work.Bottom) / 2.0) <= 1,
                    "SourceInitialized centers the actual HWND before it is shown");
                var before = rect;
                StartupWindowPlacement.Apply(window);
                GetWindowRect(hwnd, out rect);
                check(rect.Equals(before), "Native startup correction does not move an already centered window");
            }
            catch (Exception ex) { failure = ex; }
            finally { window?.Close(); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure != null) throw failure;
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}
