using System.Diagnostics;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageMagick;
using MirrorFlux.Services;

internal static class WindowsImageCodecRegression
{
    internal static void Run(Action<bool, string> check) => OnSta(() =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "mf-wic-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            byte[] pixels = [0,0,255,255,255,0,0,255];
            var source = BitmapSource.Create(2, 1, 96, 96, PixelFormats.Bgra32, null, pixels, 8);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(source));
            string disguised = Path.Combine(directory, "sample.HEIC");
            using (var file = File.Create(disguised)) encoder.Save(file);
            bool loadedBefore = NativeImageLoaded();
            var bitmap = ImageFileDecoder.Decode(disguised);
            check(bitmap.PixelWidth == 2 && bitmap.PixelHeight == 1 && bitmap.IsFrozen, "Windows codec route recognizes content and returns a frozen HEIC-named bitmap");
            byte[] actual = new byte[8]; new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0).CopyPixels(actual, 8, 0);
            check(actual.SequenceEqual(pixels), "Windows image route retains exact generated pixels");
            File.Delete(disguised);
            check(!File.Exists(disguised) && bitmap.PixelWidth == 2, "Windows image route releases its file before returning");
            string invalid = Path.Combine(directory, "invalid.heif"); File.WriteAllText(invalid, "not an image");
            bool explained = false;
            try { ImageFileDecoder.Decode(invalid); }
            catch (NotSupportedException ex) { explained = ex.Message == ImageFileDecoder.WindowsCodecMessage && ex.InnerException != null; }
            check(explained, "Unavailable or invalid HEIC decode explains the Windows codec requirement");
            check(loadedBefore == NativeImageLoaded(), "HEIC routes never fall back to the bundled native image decoder");
            bool missing = false;
            try { ImageFileDecoder.Decode(Path.Combine(directory, "missing.heic")); }
            catch (FileNotFoundException) { missing = true; }
            check(missing, "Missing HEIC keeps its file-not-found error");
        }
        finally { Directory.Delete(directory, true); }
    });

    internal static void Audit(string path, string reportPath) => OnSta(() =>
    {
        bool nativeRejected = false; string nativeError = "";
        try { using var image = new MagickImage(path); }
        catch (MagickException ex) { nativeRejected = true; nativeError = ex.Message; }
        bool windowsSucceeded; string windowsError = ""; int width = 0, height = 0;
        try { var image = ImageFileDecoder.Decode(path); windowsSucceeded = true; width = image.PixelWidth; height = image.PixelHeight; }
        catch (NotSupportedException ex) { windowsSucceeded = false; windowsError = ex.Message; }
        File.WriteAllText(reportPath, JsonSerializer.Serialize(new { nativeRejected, nativeError, windowsSucceeded, windowsError, width, height },
            new JsonSerializerOptions { WriteIndented = true }));
        if (!nativeRejected) throw new Exception("The bundled native DLL must not decode the HEVC fixture.");
        Console.WriteLine(windowsSucceeded ? "HEIC decoded through Windows codec." : "HEIC reports the Windows codec requirement on this host.");
    });

    private static bool NativeImageLoaded() => Process.GetCurrentProcess().Modules.Cast<ProcessModule>()
        .Any(m => string.Equals(m.ModuleName, "Magick.Native-Q8-x64.dll", StringComparison.OrdinalIgnoreCase));

    private static void OnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { error = ex; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}
