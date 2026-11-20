using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MirrorFlux;
using MirrorFlux.Controls;
using MirrorFlux.Models;
using MirrorFlux.ViewModels;

internal static class DialogFooterRendering
{
    public static void Run()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            string root = Path.Combine(Path.GetTempPath(), "MF-footer-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            try
            {
                var app = new App(); app.InitializeComponent();
                string file = Path.Combine(root, "sample.txt"), other = Path.Combine(root, "other.txt"); File.WriteAllText(file, "sample"); File.WriteAllText(other, "sample");
                var entry = new FileEntry(file, "sample.txt", false, false, 6, DateTime.Now);
                var archive = new ArchiveCreateDialog("sample.zip", 1);
                if (archive.FindName("LhaButton") != null) throw new Exception("LHA option is still visible");
                foreach (var (name, label, level) in new[] {
                    ("LevelNone", "無圧縮", 0), ("LevelFast", "高速", 1),
                    ("LevelStandard", "標準", 6), ("LevelHigh", "高圧縮", 9)
                })
                {
                    var button = (Button)archive.FindName(name);
                    if ((string)button.Content != label) throw new Exception("ZIP level label mismatch: " + name);
                    button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    if (archive.ZipLevel != level) throw new Exception("ZIP level choice mismatch: " + name);
                }
                ((Button)archive.FindName("LzhButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                var notice = (TextBlock)archive.FindName("FormatNoticeText");
                if ((string)((Button)archive.FindName("FixedLevelButton")).Content != "固定" ||
                    notice.Text != "LZHの名前はCP932対応文字のみです。" ||
                    notice.Visibility != Visibility.Visible ||
                    !((TextBox)archive.FindName("NameBox")).Text.EndsWith(".lzh", StringComparison.OrdinalIgnoreCase))
                    throw new Exception("LZH warning or extension mismatch");
                var lzhArchive = new ArchiveCreateDialog("sample.zip", 1);
                ((Button)lzhArchive.FindName("LzhButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                ((Button)archive.FindName("ZipButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                if (notice.Visibility != Visibility.Collapsed)
                    throw new Exception("LZH warning remains visible for ZIP");
                var windows = new Window[] {
                    new MakeDirDialog(), new PathInputDialog(root), ConfirmDialog.ForTransfer([entry], root, false), ConfirmDialog.ForTransfer([entry], root, true),
                    ConfirmDialog.ForDelete([entry]), new CopyConflictDialog(new(new FileInfo(file), new FileInfo(other), "同一内容", "同一サイズ", "同一日時", true)),
                    ConfirmDialog.ForExit(), new ErrorDialog("エラー", "確認用メッセージ"), new SortDialog(PaneSortMode.Name, true, true),
                    new FileRenameDialog(file), new DriveSelectDialog([new DriveItem('C', "FIXED", "TEST", "C:\\", true, true)], 'C'),
                    archive, lzhArchive
                };
                int rendered = 0;
                foreach (var dialog in windows)
                {
                    foreach (double scale in new[] { 1.0, 1.25 })
                    {
                        var content = (FrameworkElement)dialog.Content; VisualTreeHelper.SetRootDpi(content, new DpiScale(scale, scale));
                        content.Measure(new Size(dialog.Width, double.PositiveInfinity)); content.Arrange(new Rect(0, 0, dialog.Width, content.DesiredSize.Height)); content.UpdateLayout();
                        var footer = Find<RetroDialogKeyHelp>(content) ?? throw new Exception("Missing common footer: " + dialog.GetType().Name);
                        var label = footer.Children.OfType<TextBlock>().Single();
                        if (footer.ActualHeight <= 0 || label.ActualWidth <= 0 || label.ActualWidth > footer.ActualWidth + 1) throw new Exception("Footer text clips: " + dialog.GetType().Name);
                        string directory = Path.Combine(Environment.CurrentDirectory, ".dotnet", "dialog-footers"); Directory.CreateDirectory(directory);
                        string suffix = dialog == lzhArchive ? "-lzh" : dialog is ConfirmDialog ? "-" + dialog.Title : "";
                        using var stream = File.Create(Path.Combine(directory, dialog.GetType().Name + suffix + "-" + (int)(96 * scale) + ".png"));
                        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(dialog.Width * scale), (int)Math.Ceiling(content.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                        bitmap.Render(content); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(stream); rendered++;
                    }
                }
                Console.WriteLine($"PASS dialog footers: {windows.Length} dialogs; {rendered} renderings at 100/125 percent");
            }
            catch (Exception ex) { failure = ex; }
            finally { Directory.Delete(root, true); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure != null) throw failure;
    }
    private static T? Find<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T result) return result;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) if (Find<T>(VisualTreeHelper.GetChild(root, i)) is T child) return child;
        return null;
    }
}
