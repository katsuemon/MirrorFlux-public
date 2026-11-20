using System.Windows;
using System.Windows.Input;
using MirrorFlux.Controls;

internal static class ViewerViewportRegression
{
    public static void Run(Action<bool, string> check)
    {
        var image = new ViewerViewport(0.05, 32, limitWindowFitToActualSize: true);
        image.Reset(200, 100);
        check(image.FitWindow(new Size(1000, 650)) && image.Zoom == 1 &&
              image.PanX == 400 && image.PanY == 275 && image.FitMode == ViewerFitMode.Window,
            "image fit keeps small images at actual size and centers them");

        image.RotateRight();
        check(image.RotatedSize == new Size(100, 200) && image.FitWindow(new Size(1000, 650)) &&
              image.PanX == 450 && image.PanY == 225,
            "image rotation swaps viewport dimensions and preserves fit");

        check(image.ZoomAt(2, new Point(500, 325)) && image.Zoom == 2 &&
              image.PanX == 400 && image.PanY == 125 && image.FitMode == ViewerFitMode.Manual,
            "zoom keeps the cursor anchor fixed");
        image.BeginDrag(new Point(10, 20));
        check(image.DragTo(new Point(25, 45)) && image.PanX == 415 && image.PanY == 150,
            "drag moves the image by the cursor delta");
        image.EndDrag();
        check(!image.DragTo(new Point(50, 50)), "drag ends without further movement");

        var pdf = new ViewerViewport(0.1, 16, limitWindowFitToActualSize: false);
        pdf.Reset(200, 100);
        check(pdf.FitWindow(new Size(1000, 650), 16) && Math.Abs(pdf.Zoom - 4.92) < 0.0001 &&
              pdf.FitMode == ViewerFitMode.Window,
            "PDF fit uses its inset and may enlarge small pages");
        check(pdf.FitWidth(new Size(1000, 650), 24) && Math.Abs(pdf.Zoom - 4.88) < 0.0001 &&
              pdf.PanY == 8 && pdf.FitMode == ViewerFitMode.Width,
            "PDF width fit keeps its top margin");
        pdf.Pan(0, 60, setManual: false);
        check(pdf.PanY == 68 && pdf.FitMode == ViewerFitMode.Width,
            "PDF wheel scrolling retains width fit mode");

        check(ViewerInput.Map(Key.Left, ModifierKeys.None, false, true) == ViewerKeyAction.Previous &&
              ViewerInput.Map(Key.Left, ModifierKeys.Shift, true, false) == ViewerKeyAction.PanRight &&
              ViewerInput.PanDelta(ViewerKeyAction.PanRight) == new Point(64, 0),
            "shared arrow mapping preserves navigation and shift panning");
        check(ViewerInput.Map(Key.W, ModifierKeys.None, true, false) == ViewerKeyAction.FitWidth &&
              ViewerInput.Map(Key.W, ModifierKeys.None, false, true) == ViewerKeyAction.None &&
              ViewerInput.Map(Key.H, ModifierKeys.None, false, true) == ViewerKeyAction.FlipHorizontal &&
              ViewerInput.Map(Key.H, ModifierKeys.None, true, false) == ViewerKeyAction.None,
            "shared key mapping keeps PDF width fit and image flip distinct");
        check(ViewerInput.Map(Key.R, ModifierKeys.Shift, true, false) == ViewerKeyAction.None &&
              ViewerInput.Map(Key.R, ModifierKeys.None, true, false) == ViewerKeyAction.RotateRight,
            "rotation shortcut still requires no modifiers");
    }
}
