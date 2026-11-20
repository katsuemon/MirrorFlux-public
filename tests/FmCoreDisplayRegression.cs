using System.Windows;
using System.Windows.Controls;
using MirrorFlux.Controls;
using MirrorFlux.Services.Music;

internal static class FmCoreDisplayRegression
{
    internal static void Run()
    {
        Exception? failure = null;
        var thread = new Thread(() => {
            try {
                var viewer = new MusicPlayerControl();
                foreach (var (core, device) in new[]{("ymfm","—"),("—","ND6 ND8"),("ymfm + mf_chips","ND6 ND7 ND8"),("—","—")}) {
                    viewer.RenderSnapshot(new MusicPlaybackSnapshot { FmCoreName = core, ExtDeviceName = device });
                    viewer.Measure(new Size(1100,720)); viewer.Arrange(new Rect(0,0,1100,720)); viewer.UpdateLayout();
                    var label=(MusicCoreDisplay)viewer.FindName("FmCoreText");
                    var loop=(FrameworkElement)viewer.FindName("LoopCountSevenSegment");
                    var gd3=(MarqueeTextBlock)viewer.FindName("Gd3TrackTitleText");
                    if(label.FontSize!=gd3.FontSize || label.FontFamily.Source!=gd3.FontFamily.Source)
                        throw new Exception("FM core typography must match GD3 content");
                    var labelTop=label.TranslatePoint(new Point(0,0),viewer);
                    var loopBottom=loop.TranslatePoint(new Point(0,loop.ActualHeight),viewer);
                    if(label.Text!=core || labelTop.Y<loopBottom.Y || label.ActualWidth<=0)
                        throw new Exception("FM core label must follow the loop counter without overlap");
                    // EXT DEVICE欄はFM COREの1段下に常に置き、CONTROLLERの見出しと重ならない。
                    var ext=(MusicCoreDisplay)viewer.FindName("ExtDeviceText");
                    var controller=(FrameworkElement)viewer.FindName("ControllerGraphicsText");
                    var extTop=ext.TranslatePoint(new Point(0,0),viewer);
                    var extBottom=ext.TranslatePoint(new Point(0,ext.ActualHeight),viewer);
                    var labelBottom=label.TranslatePoint(new Point(0,label.ActualHeight),viewer);
                    var controllerTop=controller.TranslatePoint(new Point(0,0),viewer);
                    if(ext.Text!=device || ext.LabelLine1!="EXT" || ext.LabelLine2!="DEVICE" || ext.Visibility!=Visibility.Visible ||
                        ext.ActualWidth!=label.ActualWidth || Math.Abs(extTop.Y-labelTop.Y-27)>0.5 ||
                        extTop.Y<labelBottom.Y || extBottom.Y>controllerTop.Y)
                        throw new Exception("EXT DEVICE row must sit one row below FM CORE without overlapping CONTROLLER");
                }
            } catch(Exception e) { failure=e; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if(failure!=null) throw failure;
        Console.WriteLine("PASS FM core display follows loop counter and updates on core changes");
        Console.WriteLine("PASS EXT DEVICE row stays below FM CORE and above CONTROLLER");
    }
}
