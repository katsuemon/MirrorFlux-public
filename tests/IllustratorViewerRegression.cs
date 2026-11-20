using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MirrorFlux.Controls;
using MirrorFlux.Input;
using MirrorFlux.Services;

internal static class IllustratorViewerRegression
{
    public static void Run(string root, Action<bool, string> check)
    {
        string folder = Directory.CreateDirectory(Path.Combine(root, "illustrator")).FullName;
        string ai = Path.Combine(folder, "01-日本語.AI");
        File.WriteAllBytes(ai, CreatePdf());
        string png = Path.Combine(folder, "02-raster.png");
        string legacy = Path.Combine(root, "legacy.ai");
        File.WriteAllText(legacy, "%!PS-Adobe-3.0\n%%Creator: Adobe Illustrator\n");
        string broken = Path.Combine(root, "broken.ai");
        File.WriteAllText(broken, "%PDF-1.7\ntruncated");
        string ini = Path.Combine(root, "ai.ini");
        File.WriteAllText(ini, ".png ---- &graphicviewer;\n");
        var settings = new SettingsStore(ini).Load().Settings;
        check(settings.ExtBindings.Any(b => b.Extension == ".ai" && b.Command == AppCommand.GraphicViewer),
            "existing INI receives AI image association");
        File.WriteAllText(ini, ".AI ---- &textviewer;\n");
        settings = new SettingsStore(ini).Load().Settings;
        check(settings.ExtBindings.Count(b => b.Extension.Equals(".ai", StringComparison.OrdinalIgnoreCase)) == 1 &&
            settings.ExtBindings.Any(b => b.Extension.Equals(".ai", StringComparison.OrdinalIgnoreCase) && b.Command == AppCommand.TextViewer),
            "custom AI association is preserved without duplicate");

        OnDispatcher(async () =>
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(16, 8, 96, 96, PixelFormats.Bgra32,
                null, Enumerable.Repeat((byte)255, 16 * 8 * 4).ToArray(), 16 * 4)));
            using (var stream = File.Create(png)) encoder.Save(stream);

            var host = new ViewerHost();
            check(host.Open(ai) && host.IsGraphicViewer && !host.IsPdfViewer, "AI opens in graphic viewer");
            var viewer = (GraphicViewerControl)host.CurrentViewer!;
            await viewer.PendingLoad;
            check(IsColor(viewer, red: true) && viewer.StatusText.Contains("Artboard 1/2"),
                "AI first artboard pixels and page status render");
            var topRight = Pixel(viewer, .9, .1);
            var bottomLeft = Pixel(viewer, .1, .9);
            check(topRight[0] > 245 && topRight[1] < 10 && bottomLeft[2] > 245 && bottomLeft[1] > 245,
                "AI render includes opposite artboard corners without cropping");
            double originalWidth = Bitmap(viewer).PixelWidth;
            double originalHeight = Bitmap(viewer).PixelHeight;
            viewer.ExecuteFunctionKey(7);
            check(Bitmap(viewer).PixelWidth == originalHeight && Bitmap(viewer).PixelHeight == originalWidth,
                "AI supports image rotation");
            viewer.ExecuteFunctionKey(4);
            await viewer.PendingLoad;
            check(IsColor(viewer, red: false) && viewer.StatusText.Contains("Artboard 2/2"),
                "F4 advances to next AI artboard");
            viewer.ExecuteFunctionKey(3);
            await viewer.PendingLoad;
            check(IsColor(viewer, red: true), "F3 returns to previous AI artboard");
            viewer.ExecuteFunctionKey(4);
            await viewer.PendingLoad;
            viewer.ExecuteFunctionKey(4);
            await viewer.PendingLoad;
            check(viewer.FilePath == png && Bitmap(viewer).PixelWidth == 16 && !viewer.StatusText.Contains("Artboard"),
                "last AI artboard advances to next image without stale page state");
            viewer.ExecuteFunctionKey(3);
            await viewer.PendingLoad;
            check(viewer.FilePath == ai && IsColor(viewer, red: false),
                "previous image opens AI at last artboard");

            viewer.Load(ai);
            Task pending = viewer.PendingLoad;
            viewer.Load(png);
            await pending;
            check(viewer.FilePath == png && Bitmap(viewer).PixelWidth == 16,
                "canceled AI load cannot overwrite newer raster image");
            viewer.Load(ai);
            pending = viewer.PendingLoad;
            viewer.Close();
            await pending;
            check(((Image)viewer.FindName("MainImage")).Source is null, "closing during AI load releases image");

            viewer.Load(legacy);
            await viewer.PendingLoad;
            var overlay = (TextBlock)viewer.FindName("OverlayMessageText");
            check(overlay.Visibility == Visibility.Visible && overlay.Text.Contains("PDF互換") &&
                ((Image)viewer.FindName("MainImage")).Source is null,
                "non-PDF AI shows compatibility guidance without Ghostscript");
            viewer.Load(broken);
            await viewer.PendingLoad;
            check(overlay.Visibility == Visibility.Visible && overlay.Text.Contains("読み込めません"),
                "corrupt PDF-compatible AI reports an error");
            viewer.Load(ai);
            await viewer.PendingLoad;
            check(IsColor(viewer, red: true) && overlay.Visibility == Visibility.Collapsed,
                "valid AI can be opened after decoding failure");
            host.CloseViewer();

            string single = Directory.CreateDirectory(Path.Combine(root, "single-ai")).FullName;
            string only = Path.Combine(single, "only.ai");
            File.Copy(ai, only);
            viewer.Load(only);
            await viewer.PendingLoad;
            viewer.ExecuteFunctionKey(3);
            await viewer.PendingLoad;
            check(IsColor(viewer, red: false), "single AI file wraps backward through artboards");
            viewer.ExecuteFunctionKey(4);
            await viewer.PendingLoad;
            check(IsColor(viewer, red: true), "single AI file wraps forward through artboards");
            viewer.Close();

            string zip = Path.Combine(root, "illustrator.zip");
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
                archive.CreateEntryFromFile(ai, "日本語/画像.ai");
            string entry = ArchiveService.ReadArchiveFilePaths(zip).Single();
            string? extracted = await ArchiveService.ExtractEntryToTempFileAsync(entry);
            check(extracted != null && viewer.Load(extracted), "AI extracts from archive into graphic viewer");
            await viewer.PendingLoad;
            check(IsColor(viewer, red: true), "archived AI renders artwork");
            viewer.Close();

            string large = Path.Combine(root, "large.ai");
            File.WriteAllBytes(large, CreatePdf(20000, 10000));
            var preview = await IllustratorImageDecoder.DecodeAsync(large);
            check(preview.Bitmap.PixelWidth <= 4096 && preview.Bitmap.PixelHeight <= 4096 && preview.Bitmap.IsFrozen,
                "oversized AI preview bounds memory and returns frozen bitmap");
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            bool stopped = false;
            try { await IllustratorImageDecoder.DecodeAsync(ai, cancellationToken: canceled.Token); }
            catch (OperationCanceledException) { stopped = true; }
            check(stopped, "AI decoder honors cancellation");
            using var exclusive = File.Open(ai, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            check(exclusive.Length > 0, "AI renderer releases source file after completion/cancellation");
        });
    }

    public static void Audit(string path)
    {
        OnDispatcher(async () =>
        {
            var viewer = new GraphicViewerControl { Width = 1000, Height = 650 };
            if (!viewer.Load(path)) throw new Exception("AI viewer load failed");
            await viewer.PendingLoad;
            var bitmap = Bitmap(viewer);
            string output = Path.Combine(Environment.CurrentDirectory, ".dotnet", "ai-preview");
            Directory.CreateDirectory(output);
            var rawEncoder = new PngBitmapEncoder(); rawEncoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var raw = File.Create(Path.Combine(output, Path.GetFileNameWithoutExtension(path) + "-raw.png"))) rawEncoder.Save(raw);
            foreach (double scale in new[] { 1.0, 1.25 })
            {
                VisualTreeHelper.SetRootDpi(viewer, new DpiScale(scale, scale));
                viewer.Measure(new Size(1000, 650));
                viewer.Arrange(new Rect(0, 0, 1000, 650)); viewer.UpdateLayout();
                var target = new RenderTargetBitmap((int)(1000 * scale), (int)(650 * scale),
                    96 * scale, 96 * scale, PixelFormats.Pbgra32);
                target.Render(viewer);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(target));
                using var file = File.Create(Path.Combine(output, Path.GetFileNameWithoutExtension(path) + $"-{scale:0.00}.png"));
                encoder.Save(file);
            }
            Console.WriteLine("PASS actual AI " + viewer.StatusText);
            viewer.Close();
        });
    }

    private static BitmapSource Bitmap(GraphicViewerControl viewer) =>
        ((Image)viewer.FindName("MainImage")).Source as BitmapSource ??
        throw new Exception(((TextBlock)viewer.FindName("OverlayMessageText")).Text);

    private static bool IsColor(GraphicViewerControl viewer, bool red)
    {
        byte[] pixel = Pixel(viewer, .5, .5);
        return pixel[0] < 10 && (red ? pixel[2] > 245 && pixel[1] < 10 : pixel[1] > 245 && pixel[2] < 10);
    }

    private static byte[] Pixel(GraphicViewerControl viewer, double x, double y)
    {
        var bitmap = new FormatConvertedBitmap(Bitmap(viewer), PixelFormats.Bgra32, null, 0);
        byte[] pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect((int)(bitmap.PixelWidth * x), (int)(bitmap.PixelHeight * y), 1, 1), pixel, 4, 0);
        return pixel;
    }

    private static void OnDispatcher(Func<Task> action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            async Task Run() { await action(); }
            var work = Run();
            work.ContinueWith(_ => dispatcher.BeginInvokeShutdown(DispatcherPriority.Background), TaskScheduler.Default);
            Dispatcher.Run();
            try { work.GetAwaiter().GetResult(); } catch (Exception ex) { failure = ex; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(60))) throw new TimeoutException("AI viewer timed out");
        if (failure != null) throw failure;
    }

    // A small, original PDF artwork fixture with two artboards; no Adobe assets required.
    private static byte[] CreatePdf(int width = 120, int height = 80)
    {
        string red = $"1 0 0 rg 0 0 {width} {height} re f\n" +
            $"0 0 1 rg {width * 4 / 5} {height * 4 / 5} {width / 5} {height / 5} re f\n" +
            $"1 1 0 rg 0 0 {width / 5} {height / 5} re f\n";
        const string green = "0 1 0 rg 0 0 60 100 re f\n";
        string[] objects = [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {width} {height}] /Resources << >> /Contents 5 0 R >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 60 100] /Resources << >> /Contents 6 0 R >>",
            $"<< /Length {red.Length} >>\nstream\n{red}endstream",
            $"<< /Length {green.Length} >>\nstream\n{green}endstream"
        ];
        var text = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Length; i++)
        {
            offsets.Add(text.Length);
            text.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        int xref = text.Length;
        text.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) text.Append($"{offset:0000000000} 00000 n \n");
        text.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(text.ToString());
    }
}
