using System.ComponentModel;
using System.IO;
using System.Windows.Threading;
using MirrorFlux.ViewModels;
namespace MirrorFlux.Services;

/// <summary>
/// パネルが表示中のフォルダーを監視し、変化があればファイル一覧を少し待ってからまとめて更新する。アーカイブ内は監視しない。
/// </summary>
public sealed class PaneDirectoryMonitor : IDisposable
{
    private readonly FilePaneViewModel _pane;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer;
    private FileSystemWatcher? _watcher;
    private bool _disposed, _refreshing, _pending;
    /// <summary>
    /// 250 ミリ秒の待ち合わせ用タイマーを用意し、パスの変化を見ながら監視を始める。
    /// </summary>
    public PaneDirectoryMonitor(FilePaneViewModel pane, Dispatcher dispatcher)
    {
        _pane = pane; _dispatcher = dispatcher;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, async (_, _) => await Refresh(), dispatcher);
        _timer.Stop(); pane.PropertyChanged += PathChanged; Watch();
    }
    private void PathChanged(object? sender, PropertyChangedEventArgs e) { if (e.PropertyName == nameof(FilePaneViewModel.CurrentPath)) Watch(); }
    /// <summary>
    /// 今のフォルダーの監視を作り直す。監視できないフォルダーでは何もしない。
    /// </summary>
    private void Watch()
    {
        _watcher?.Dispose(); _watcher = null;
        if (_disposed || ArchiveService.IsInsideArchive(_pane.CurrentPath)) return;
        try
        {
            _watcher = new FileSystemWatcher(_pane.CurrentPath) { NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite };
            _watcher.Changed += Changed; _watcher.Created += Changed; _watcher.Deleted += Changed;
            _watcher.Renamed += (_, _) => Schedule(); _watcher.Error += (_, _) => Schedule();
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { }
    }
    private void Changed(object sender, FileSystemEventArgs e) => Schedule();
    /// <summary>
    /// 更新を予約する。続けて変化があればタイマーをやり直し、まとめて1回だけ更新する。
    /// </summary>
    public void Schedule()
    {
        if (_disposed || _dispatcher.HasShutdownStarted) return;
        _dispatcher.BeginInvoke(() => { if (!_disposed) { _pending = true; _timer.Stop(); _timer.Start(); } });
    }
    /// <summary>
    /// 予約された更新を行う。更新中に次の変化が来たら、終わった後にもう一度予約する。
    /// </summary>
    private async Task Refresh()
    {
        _timer.Stop();
        if (_disposed || _refreshing || !_pending) return;
        _pending = false; _refreshing = true;
        try { await _pane.RefreshMetadataAsync(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        finally { _refreshing = false; if (_pending && !_disposed) _timer.Start(); }
    }
    public void Dispose() { _disposed = true; _timer.Stop(); _watcher?.Dispose(); _pane.PropertyChanged -= PathChanged; }
}
