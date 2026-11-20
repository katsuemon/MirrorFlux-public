using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using MirrorFlux.Controls;
using MirrorFlux.Services;

internal static class ArchiveViewerRegression
{
    public static void Run(string root, Action<bool, string> check)
    {
        string archive = Path.Combine(root, "viewer-doc.lzh");
        var data = System.Text.Encoding.GetEncoding(932).GetBytes("日本語の説明書\r\nLZHから表示するテキストです。\r\n");
        var files = new Dictionary<string, byte[]> { ["readme.doc"] = data, ["program.x"] = new byte[] { 1, 2, 3 } };
        LhaArchiveWriter.Write(archive, files);
        Exercise(ArchiveService.ReadArchiveFilePaths(archive).Single(p => p.EndsWith(".doc")), check);
    }
    public static void Audit(string archivePath)
    {
        var files = ArchiveService.ReadArchiveFilePaths(archivePath);
        var documents = files.Where(path => Path.GetExtension(path).Equals(".doc", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (documents.Length == 0) throw new Exception("No DOC entries in archive");
        foreach (string path in documents) Exercise(path, null);
        Console.WriteLine($"PASS archive DOC viewer: entries={documents.Length}, extraction and text render complete on UI dispatcher");
    }
    public static void Exercise(string virtualPath, Action<bool, string>? check)
    {
        Exception? failure = null; string stage = "starting";
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            async Task Run()
            {
                stage = "extracting";
                string? path = await ArchiveService.ExtractEntryToTempFileAsync(virtualPath);
                if (path == null) throw new Exception("Archive entry extraction failed");
                stage = "loading";
                var viewer = new TextViewerControl { Width = 1000, Height = 650, FontSize = 14 };
                if (!viewer.Load(path, false)) throw new Exception("DOC viewer load failed");
                await viewer.PendingLoad;
                stage = "rendering";
                viewer.Measure(new Size(1000, 650)); viewer.Arrange(new Rect(0, 0, 1000, 650)); viewer.UpdateLayout();
                await Dispatcher.Yield(DispatcherPriority.Render);
                if (!viewer.StatusText.Contains(Path.GetFileName(path))) throw new Exception("DOC viewer did not show file status");
                viewer.Close(); stage = "complete";
            }
            var work = Run();
            work.ContinueWith(_ => dispatcher.BeginInvokeShutdown(DispatcherPriority.Background), TaskScheduler.Default);
            Dispatcher.Run();
            try { work.GetAwaiter().GetResult(); } catch (Exception ex) { failure = ex; }
        }) { IsBackground = true };
        var watch = Stopwatch.StartNew(); thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(8))) throw new TimeoutException("UI archive viewer stalled while " + stage);
        if (failure != null) throw failure;
        check?.Invoke(true, "LHA DOC extraction and rendering complete without UI deadlock");
        Console.WriteLine($"PASS DOC entry elapsed={watch.ElapsedMilliseconds}ms");
    }
}
