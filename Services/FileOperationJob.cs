using System.Diagnostics;
using System.IO;
using MirrorFlux.Models;

namespace MirrorFlux.Services;

/// <summary>
/// コピー・移動・削除・展開などのファイル操作1回分。中止の受付、対象の数え上げ、処理中のファイルと進み具合を持つ。
/// 操作の処理（各サービス）は <see cref="Current"/> を通して処理中のファイルと書いたバイト数を知らせる。
/// 画面は <see cref="GetSnapshot"/> で一定間隔ごとに状態を読む。
/// </summary>
public sealed class FileOperationJob
{
    private static readonly AsyncLocal<FileOperationJob?> CurrentJob = new();

    /// <summary>この非同期の流れで実行中の操作。操作の外ではnull。</summary>
    public static FileOperationJob? Current => CurrentJob.Value;

    private readonly object _sync = new();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Stopwatch _elapsed = new();
    private readonly List<(string Path, long Size)> _plan = [];
    private readonly List<string> _completedItems = [];
    private int _nextPlanIndex;
    private bool _hasPlan, _counting, _running;
    private int _countedFiles, _doneFiles;
    private long _totalBytes, _completedBytes, _currentDone, _currentSize;
    private string? _currentPath;

    /// <summary>操作の名前（コピー・移動・削除・展開など）。</summary>
    public string Kind { get; }
    /// <summary>操作元のフォルダー（またはアーカイブ）。</summary>
    public string Source { get; }
    /// <summary>操作先のフォルダー。削除ではnull。</summary>
    public string? Destination { get; }
    /// <summary>対象の項目数（マークした項目の数）。</summary>
    public int ItemCount { get; }

    public FileOperationJob(string kind, string source, string? destination, int itemCount)
    {
        Kind = kind;
        Source = source;
        Destination = destination;
        ItemCount = itemCount;
    }

    public CancellationToken Token => _cancellation.Token;
    public bool IsCancellationRequested => _cancellation.IsCancellationRequested;
    public bool IsRunning { get { lock (_sync) return _running; } }

    /// <summary>中止を求める。処理は次の区切り（ファイルの途中を含む）で止まる。</summary>
    public void Cancel() => _cancellation.Cancel();

    /// <summary>この非同期の流れの中で、この操作を <see cref="Current"/> にする。</summary>
    public IDisposable Enter()
    {
        var previous = CurrentJob.Value;
        CurrentJob.Value = this;
        return new Restore(previous);
    }

    private sealed class Restore(FileOperationJob? previous) : IDisposable
    {
        public void Dispose() => CurrentJob.Value = previous;
    }

    /// <summary>順番が来て実行を始めたことを記録する。</summary>
    public void MarkRunning()
    {
        lock (_sync) { _running = true; _elapsed.Start(); }
    }

    /// <summary>
    /// 実ファイル・実フォルダーの項目を、処理と同じ順（フォルダーの中はファイル、次に下のフォルダー）で数え上げる。
    /// 数えている間も中止できる。読めないフォルダーは飛ばす。
    /// </summary>
    public void PlanFileSystemItems(IEnumerable<FileEntry> items, bool recurse = true)
    {
        var plan = new List<(string, long)>();
        lock (_sync) { _counting = true; _countedFiles = 0; }
        try
        {
            foreach (var item in items)
            {
                Token.ThrowIfCancellationRequested();
                if (item.IsParent) continue;
                if (item.IsDirectory && recurse) AddDirectory(plan, item.FullPath);
                else AddPlanned(plan, item.FullPath, item.IsDirectory ? 0 : item.Length);
            }
        }
        finally
        {
            lock (_sync) _counting = false;
        }
        SetPlan(plan);
    }

    private void AddDirectory(List<(string, long)> plan, string directory)
    {
        Token.ThrowIfCancellationRequested();
        var info = new DirectoryInfo(directory);
        try { foreach (var file in info.GetFiles()) AddPlanned(plan, file.FullName, file.Length); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        DirectoryInfo[] children;
        try { children = info.GetDirectories(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
        foreach (var child in children) AddDirectory(plan, child.FullName);
    }

    private void AddPlanned(List<(string, long)> plan, string path, long size)
    {
        plan.Add((path, size));
        lock (_sync) _countedFiles = plan.Count;
    }

    /// <summary>処理するファイルの一覧（処理の順）を決める。</summary>
    public void SetPlan(IReadOnlyList<(string Path, long Size)> plan)
    {
        lock (_sync)
        {
            _plan.Clear();
            _plan.AddRange(plan);
            _hasPlan = true;
            _nextPlanIndex = 0;
            _totalBytes = plan.Sum(p => Math.Max(0, p.Size));
        }
    }

    /// <summary>1ファイル（または1項目）の処理を始める。</summary>
    public void BeginFile(string path, long size)
    {
        lock (_sync)
        {
            _currentPath = path;
            _currentSize = Math.Max(0, size);
            _currentDone = 0;
            // 一覧の中の位置を進める。予定と違う順に来たときは、近くを探して合わせる。
            int limit = Math.Min(_plan.Count, _nextPlanIndex + 256);
            for (int i = _nextPlanIndex; i < limit; i++)
            {
                if (string.Equals(_plan[i].Path, path, StringComparison.OrdinalIgnoreCase)) { _nextPlanIndex = i + 1; break; }
            }
        }
    }

    /// <summary>処理中のファイルに書いたバイト数を足す。</summary>
    public void AddBytes(long bytes)
    {
        if (bytes <= 0) return;
        lock (_sync) _currentDone = Math.Min(_currentSize, _currentDone + bytes);
    }

    /// <summary>1ファイルの処理を終える（コピー・スキップ・失敗のどれでも）。</summary>
    public void EndFile()
    {
        lock (_sync)
        {
            if (_currentPath == null) return;
            _doneFiles++;
            _completedBytes += _currentSize;
            _currentPath = null;
            _currentDone = _currentSize = 0;
        }
    }

    /// <summary>マークした項目のうち、中身まで処理し終えたもの（中止後にマークを外すのに使う）。</summary>
    public void ItemCompleted(string path)
    {
        lock (_sync) _completedItems.Add(path);
    }

    public IReadOnlyList<string> CompletedItems { get { lock (_sync) return [.. _completedItems]; } }

    /// <summary>処理し終えたファイルの数と、予定の数（数えていなければ -1）。</summary>
    public (int Done, int Total) Counts { get { lock (_sync) return (_doneFiles, _hasPlan ? _plan.Count : -1); } }

    /// <summary>画面に出す今の状態。</summary>
    public FileOperationSnapshot GetSnapshot(int upcomingCount)
    {
        lock (_sync)
        {
            // 処理中のファイルの次から並べる。
            int start = _nextPlanIndex;
            var upcoming = _plan.Skip(start).Take(Math.Max(0, upcomingCount)).Select(p => p.Path).ToArray();
            int remaining = Math.Max(0, _plan.Count - start - upcoming.Length);
            return new FileOperationSnapshot(Kind, Source, Destination, ItemCount, _running, _counting, _countedFiles,
                _doneFiles, _hasPlan ? _plan.Count : -1, _completedBytes + _currentDone, _hasPlan ? _totalBytes : -1,
                _currentPath, _currentSize, _currentDone, upcoming, remaining, _elapsed.Elapsed, _cancellation.IsCancellationRequested);
        }
    }
}

/// <summary>ファイル操作1回分の、ある時点の状態。</summary>
public sealed record FileOperationSnapshot(
    string Kind, string Source, string? Destination, int ItemCount, bool IsRunning, bool IsCounting, int CountedFiles,
    int DoneFiles, int TotalFiles, long DoneBytes, long TotalBytes,
    string? CurrentPath, long CurrentSize, long CurrentDone,
    IReadOnlyList<string> Upcoming, int RemainingAfterUpcoming, TimeSpan Elapsed, bool IsCancelling)
{
    /// <summary>処理中のファイルの進み具合（0〜1）。大きさが0なら0。</summary>
    public double CurrentFraction => CurrentSize > 0 ? Math.Clamp(CurrentDone / (double)CurrentSize, 0, 1) : 0;

    /// <summary>1秒あたりのバイト数。経過が短いときは0。</summary>
    public double BytesPerSecond => Elapsed.TotalSeconds >= 0.5 ? DoneBytes / Elapsed.TotalSeconds : 0;

    /// <summary>残り時間の見込み。量が分からないか速度が出ていなければnull。</summary>
    public TimeSpan? Remaining => TotalBytes > 0 && BytesPerSecond > 0
        ? TimeSpan.FromSeconds(Math.Max(0, (TotalBytes - DoneBytes) / BytesPerSecond))
        : null;
}

/// <summary>書いたバイト数を実行中のファイル操作へ知らせる書込み用ストリーム（展開の進み具合に使う）。</summary>
internal sealed class ProgressWriteStream(Stream inner, FileOperationJob job) : Stream
{
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) { inner.Write(buffer, offset, count); job.AddBytes(count); }
    public override void Write(ReadOnlySpan<byte> buffer) { inner.Write(buffer); job.AddBytes(buffer.Length); }
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await inner.WriteAsync(buffer, cancellationToken);
        job.AddBytes(buffer.Length);
    }
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
}