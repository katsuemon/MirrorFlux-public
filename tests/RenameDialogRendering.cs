using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MirrorFlux;

internal static class RenameDialogRendering
{
    public static void Run()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "mf-rename-render-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var file = Path.Combine(directory, "CHANGELOG.txt");
            var folder = Path.Combine(directory, "folder");
            try
            {
                File.WriteAllText(file, "preview");
                Directory.CreateDirectory(folder);
                File.SetLastWriteTime(file, new DateTime(2024, 4, 1, 16, 26, 6));
                var app = new App();
                app.InitializeComponent();
                var dialog = new FileRenameDialog(file);
                var content = (FrameworkElement)dialog.Content;
                double logicalWidth = dialog.Width;
                content.Measure(new Size(logicalWidth, double.PositiveInfinity));
                VisualTreeHelper.SetRootDpi(content, new DpiScale(1.5, 1.5));
                content.InvalidateMeasure();
                content.Measure(new Size(logicalWidth, double.PositiveInfinity));
                var size = content.DesiredSize;
                content.Arrange(new Rect(0, 0, logicalWidth, size.Height));
                content.UpdateLayout();
                var nameBox = (System.Windows.Controls.TextBox)dialog.FindName("NameBox");
                var dateBox = (System.Windows.Controls.TextBox)dialog.FindName("DateBox");
                if (Math.Abs(nameBox.ActualWidth - 288) > .01) throw new Exception("Rename field does not extend left across the available edit area");
                double nameRight = nameBox.TranslatePoint(new Point(nameBox.ActualWidth, 0), content).X;
                double dateRight = dateBox.TranslatePoint(new Point(dateBox.ActualWidth, 0), content).X;
                if (Math.Abs(nameRight - dateRight) > 1.0) throw new Exception($"Rename and date field right edges are not aligned: {nameRight:F2} vs {dateRight:F2}");
                if (Math.Abs(dialog.Width - 460) > .01) throw new Exception("Rename dialog width is not compact");
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(logicalWidth * 1.5), (int)Math.Ceiling(size.Height * 1.5), 144, 144, PixelFormats.Pbgra32);
                bitmap.Render(content);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                var path = Path.Combine(Path.GetTempPath(), "mf-rename-dialog.png");
                using var stream = File.Create(path);
                encoder.Save(stream);
                var folderDialog = new FileRenameDialog(folder);
                if (folderDialog.Title != "フォルダー情報の編集") throw new Exception("Folder rename dialog title missing");
                if (((FrameworkElement)folderDialog.FindName("ExtensionButtonsPanel")).Visibility != Visibility.Collapsed)
                    throw new Exception("Folder rename dialog still shows extension controls");
                Console.WriteLine(path);
            }
            catch (Exception ex) { failure = ex; }
            finally { File.Delete(file); if (Directory.Exists(folder)) Directory.Delete(folder); Directory.Delete(directory); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null) throw new Exception("Rename dialog render failed", failure);
    }
}
