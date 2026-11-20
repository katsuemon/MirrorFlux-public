using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MirrorFlux.Controls;
using MirrorFlux.Services;
using MirrorFlux.ViewModels;

/// <summary>ファイル操作の進行欄を画面に出さずに描き、行数と高さを確かめて PNG に残す。</summary>
internal static class FileOperationPanelRendering
{
    public static void Run()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                string root = Path.Combine(Path.GetTempPath(), "mf-operation-render-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(root);
                var vm = new MainViewModel(new SettingsStore(Path.Combine(root, "mf.ini")));
                var release = new TaskCompletionSource();
                var running = new FileOperationJob("コピー", @"D:\Photos\2026", @"E:\Backup\Photos", 2);
                var waiting = new FileOperationJob("移動", @"C:\Work\Export", @"E:\Backup\Export", 14);
                var first = vm.RunFileOperationAsync(async () => { await release.Task; return 0; }, running);
                var second = vm.RunFileOperationAsync(() => Task.FromResult(0), waiting);
                running.SetPlan(Enumerable.Range(119, 3400).Select(i => ($@"D:\Photos\2026\02_Feb\IMG_{i:0000}.CR3", 32L << 20)).ToList());
                running.BeginFile(@"D:\Photos\2026\02_Feb\IMG_0119.CR3", 32L << 20); running.AddBytes(32L << 20); running.EndFile();
                running.BeginFile(@"D:\Photos\2026\02_Feb\IMG_0120.CR3", 32L << 20); running.AddBytes(20L << 20);
                vm.RefreshOperationPanel();

                foreach (double scale in new[] { 1.0, 1.5 })
                {
                    var panel = new FileOperationPanel { DataContext = vm, Width = 900 };
                    foreach (var (key, color) in new[] {
                        ("DarkPurpleBackground", "#19112E"), ("LogBackground", "#000000"), ("DarkPurpleHighlight", "#382860"), ("DarkPurpleShadow", "#0C0818"),
                        ("HighlightText", "#8494FC"), ("PaneText", "#D8D8D8"), ("AccentText", "#8494FC"), ("MutedText", "#605898"), ("DirectoryText", "#C8D4FF") })
                        panel.Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
                    panel.Measure(new Size(900, double.PositiveInfinity));
                    VisualTreeHelper.SetRootDpi(panel, new DpiScale(scale, scale));
                    panel.InvalidateMeasure();
                    panel.Measure(new Size(900, double.PositiveInfinity));
                    panel.Arrange(new Rect(panel.DesiredSize));
                    panel.UpdateLayout();
                    // 見出し・集計・処理中・次の3件・待ち1件の7行（各20）と、見出しの上の線1・下の余白1。
                    if (Math.Abs(panel.ActualHeight - 142) > 0.5) throw new Exception($"Panel height {panel.ActualHeight} at {scale:P0}");
                    var bitmap = new RenderTargetBitmap((int)(900 * scale), (int)Math.Ceiling(panel.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                    bitmap.Render(panel);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    string path = Path.Combine(Path.GetTempPath(), $"mf-operation-panel-{scale * 100:0}.png");
                    using (var stream = File.Create(path)) encoder.Save(stream);
                    Console.WriteLine($"PASS operation panel {scale:P0}: 7 lines, height {panel.ActualHeight}; {path}");
                }
                release.SetResult();
                first.GetAwaiter().GetResult();
                second.GetAwaiter().GetResult();
                if (vm.IsOperationPanelVisible) throw new Exception("Panel stays visible after the operations end");
                Console.WriteLine("PASS operation panel closes when no operation is left");
                Directory.Delete(root, true);
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null) throw new Exception("Operation panel rendering failed", failure);
    }
}