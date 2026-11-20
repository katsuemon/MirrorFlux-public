using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MirrorFlux.Controls;

internal static class HeaderRendering
{
    public static void Run()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                foreach (double scale in new[] { 1.0, 1.25, 1.5, 1.75, 2.0 })
                {
                    var header = new RetroDialogHeader { Width = 400, TitleText = "コピー確認" };
                    header.Resources["HighlightText"] = new SolidColorBrush(Color.FromRgb(132, 148, 252));
                    // Realize the UserControl template before propagating the test DPI.
                    header.Measure(new Size(400, 20));
                    VisualTreeHelper.SetRootDpi(header, new DpiScale(scale, scale));
                    header.InvalidateMeasure();
                    header.Measure(new Size(400, 20));
                    header.Arrange(new Rect(0, 0, 400, 20));
                    header.UpdateLayout();
                    int width = (int)(400 * scale), height = (int)(20 * scale);
                    var bitmap = new RenderTargetBitmap(width, height, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                    bitmap.Render(header);
                    var pixels = new byte[width * height * 4];
                    bitmap.CopyPixels(pixels, width * 4, 0);
                    var starts = new List<int>();
                    for (int y = 0; y < height; y++)
                    {
                        bool Black(int row)
                        {
                            int offset = (row * width + 10) * 4;
                            return pixels[offset] == 0 && pixels[offset + 1] == 0 &&
                                pixels[offset + 2] == 0 && pixels[offset + 3] == 255;
                        }
                        if (!Black(y)) continue;
                        int start = y;
                        while (y < height && Black(y)) y++;
                        if (y - start != 2) throw new Exception($"Uneven line at {scale}: {y - start}px");
                        starts.Add(start);
                    }
                    if (starts.Count < 2 || starts.Zip(starts.Skip(1), (a,b) => b - a).Any(gap => gap != 4))
                        throw new Exception($"Uneven spacing at {scale}");
                    int top = starts[0], bottom = height - starts[^1] - 2;
                    if (Math.Abs(top - bottom) > 1) throw new Exception($"Off-center at {scale}");
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    var path = Path.Combine(Path.GetTempPath(), $"mf-header-2px-{scale * 100:0}.png");
                    using var stream = File.Create(path);
                    encoder.Save(stream);
                    Console.WriteLine($"PASS {scale:P0}: all lines 2px, gaps 2px, insets {top}/{bottom}px; {path}");
                }
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null) throw new Exception("Header rendering failed", failure);
    }
}
