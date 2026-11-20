using System.IO;
using System.Windows.Input;
using MirrorFlux.Controls;
using MirrorFlux.Input;
using MirrorFlux.Services;

internal static class TextViewerRegression
{
    public static void Run(Action<bool, string> check)
    {
        check(IniKeyParser.TryParseLine(">KEY_V -s-- &hexviewer;", out var binding) &&
            binding?.Key == Key.V && binding.Modifiers == ModifierKeys.Shift && binding.Command == AppCommand.HexViewer,
            "Shift V hexviewer binding parses");
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".png");
            try
            {
                File.WriteAllBytes(path, [0, 1, 2, 65, 66, 67]);
                var host = new ViewerHost();
                check(host.Open(path, forceHexViewer: true) && host.CurrentViewer is TextViewerControl &&
                    host.CurrentViewer.StatusText.Contains("[HEX]"), "Hex command bypasses image selection and starts in hex mode");
                host.CloseViewer();
                check(host.Open(path, forceTextViewer: true) && host.CurrentViewer is TextViewerControl &&
                    !host.CurrentViewer.StatusText.Contains("[HEX]"), "Text command overrides binary auto-detection");
                int direction = 0;
                host.TextFileNavigationRequested += value => direction = value;
                var textViewer = host.CurrentViewer as TextViewerControl ?? throw new Exception("Text viewer was not created");
                textViewer.ExecuteShiftFunctionKey(6);
                check(direction == 1, "Viewer host forwards adjacent text-file navigation");
                host.CloseViewer();
            }
            catch (Exception ex) { failure = ex; }
            finally { File.Delete(path); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure != null) throw failure;
    }
}
