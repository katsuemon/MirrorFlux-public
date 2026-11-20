using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using MirrorFlux.Input;
using MirrorFlux.Models;
using MirrorFlux.Services;

namespace MirrorFlux.ViewModels;

/// <summary>
/// メイン画面のビューモデルのうち、ファイル操作の順番待ち・中止・進行欄を受け持つ部分。
/// コピー・移動・削除・展開・アーカイブの更新は1つずつ順に実行し、後から始めた操作は待ちに積む。
/// </summary>
public sealed partial class MainViewModel
{
    // 進行欄に出す「次のファイル」の行数と、待ちの作業の行数。
    internal const int OperationUpcomingLines = 3;
    internal const int OperationWaitingLines = 2;

    private readonly SemaphoreSlim _fileOperationGate = new(1, 1);
    private readonly object _fileJobsSync = new();
    private readonly List<FileOperationJob> _fileJobs = [];
    private DispatcherTimer? _operationTimer;

    // 実行中・待ちのファイル操作の数。0でない間はアプリを終了させない（途中のコピー・移動を残さないため）。
    private int _runningFileOperations;
    public bool IsFileOperationRunning => Volatile.Read(ref _runningFileOperations) > 0;

    /// <summary>
    /// ファイル操作を順番待ちに積み、順番が来たら別スレッドで実行する。
    /// planItems を渡すと、実行の前に対象の件数と量を数える（数えている間も中止できる）。
    /// </summary>
    internal async Task<T> RunFileOperationAsync<T>(Func<Task<T>> work, FileOperationJob? job = null,
        IReadOnlyList<FileEntry>? planItems = null, bool recursePlan = true)
    {
        job ??= new FileOperationJob("処理", ActivePane.CurrentPath, null, 0);
        Interlocked.Increment(ref _runningFileOperations);
        lock (_fileJobsSync) _fileJobs.Add(job);
        RefreshOperationPanel();
        try
        {
            await _fileOperationGate.WaitAsync();
            try
            {
                job.MarkRunning();
                RefreshOperationPanel();
                return await Task.Run(async () =>
                {
                    using (job.Enter())
                    {
                        if (planItems != null) job.PlanFileSystemItems(planItems, recursePlan);
                        return await work();
                    }
                });
            }
            finally { _fileOperationGate.Release(); }
        }
        finally
        {
            lock (_fileJobsSync) _fileJobs.Remove(job);
            Interlocked.Decrement(ref _runningFileOperations);
            RefreshOperationPanel();
        }
    }

    internal Task RunFileOperationAsync(Func<Task> work, FileOperationJob? job = null,
        IReadOnlyList<FileEntry>? planItems = null, bool recursePlan = true) =>
        RunFileOperationAsync(async () => { await work(); return true; }, job, planItems, recursePlan);

    /// <summary>実行中のファイル操作（なければnull）。</summary>
    internal FileOperationJob? RunningFileJob
    {
        get { lock (_fileJobsSync) return _fileJobs.FirstOrDefault(job => job.IsRunning); }
    }

    /// <summary>
    /// 実行中のファイル操作を、確認してから中止する（&amp;opcancel;）。待ちの作業はそのあと順に実行する。
    /// </summary>
    internal void RequestCancelFileOperation(Func<FileOperationJob, bool>? confirm = null)
    {
        var job = RunningFileJob;
        if (job == null)
        {
            StatusMessage = "中止するファイル操作はありません";
            return;
        }
        if (job.IsCancellationRequested) return;
        confirm ??= ConfirmCancel;
        if (!confirm(job))
        {
            StatusMessage = $"{job.Kind}を続けます";
            return;
        }
        job.Cancel();
        StatusMessage = $"{job.Kind}を中止しています...";
        AddLog($"{job.Kind}の中止を受け付けました", "CANCEL");
        RefreshOperationPanel();
    }

    private static bool ConfirmCancel(FileOperationJob job)
    {
        if (Application.Current?.MainWindow is not MainWindow mw) return true;
        var dialog = ConfirmDialog.ForCancelOperation(job.Kind);
        dialog.PositionCenter(mw);
        return dialog.ShowDialog() == true;
    }

    /// <summary>中止したときのサマリー（完了・エラー・未処理の件数）をステータス行とログに出す。</summary>
    private void FinishCanceled(FileOperationJob job, int errors)
    {
        var (done, total) = job.Counts;
        string remaining = total >= 0 ? $", 未処理 {Math.Max(0, total - done):N0}件" : "";
        StatusMessage = $"{job.Kind}を中止しました (処理済み {done:N0}件, エラー {errors:N0}件{remaining})";
        AddLog(StatusMessage, "CANCEL");
    }

    // ---- 進行欄 ----

    private bool _isOperationPanelVisible;
    public bool IsOperationPanelVisible { get => _isOperationPanelVisible; private set => SetProperty(ref _isOperationPanelVisible, value); }
    private string _operationHeader = "";
    public string OperationHeader { get => _operationHeader; private set => SetProperty(ref _operationHeader, value); }
    private string _operationKeyHint = "";
    public string OperationKeyHint { get => _operationKeyHint; private set => SetProperty(ref _operationKeyHint, value); }
    private string _operationSummary = "";
    public string OperationSummary { get => _operationSummary; private set => SetProperty(ref _operationSummary, value); }
    private string _operationCurrentPath = "";
    public string OperationCurrentPath { get => _operationCurrentPath; private set => SetProperty(ref _operationCurrentPath, value); }
    private string _operationCurrentSize = "";
    public string OperationCurrentSize { get => _operationCurrentSize; private set => SetProperty(ref _operationCurrentSize, value); }
    private double _operationCurrentPercent;
    public double OperationCurrentPercent { get => _operationCurrentPercent; private set { if (SetProperty(ref _operationCurrentPercent, value)) OnPropertyChanged(nameof(OperationCurrentFraction)); } }
    /// <summary>処理中のファイルの進み具合（0〜1）。進行欄の棒の長さに使う。</summary>
    public double OperationCurrentFraction => OperationCurrentPercent / 100;
    private string _operationCurrentPercentText = "";
    public string OperationCurrentPercentText { get => _operationCurrentPercentText; private set => SetProperty(ref _operationCurrentPercentText, value); }
    private IReadOnlyList<string> _operationUpcoming = [];
    public IReadOnlyList<string> OperationUpcoming { get => _operationUpcoming; private set => SetProperty(ref _operationUpcoming, value); }
    private IReadOnlyList<string> _operationWaiting = [];
    public IReadOnlyList<string> OperationWaiting { get => _operationWaiting; private set => SetProperty(ref _operationWaiting, value); }

    /// <summary>
    /// 進行欄の表示を今の状態に合わせる。操作がある間は0.1秒ごとに読み直し、なくなったら欄を閉じる。
    /// UIスレッド以外から呼ばれたときはUIスレッドへ回す。
    /// </summary>
    internal void RefreshOperationPanel()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess()) { dispatcher.BeginInvoke(RefreshOperationPanel); return; }
        // UIスレッドのない環境（テスト）では複数のスレッドから呼ばれる。後から入った呼び出しが最新の一覧で上書きするよう、1つずつ実行する。
        lock (_operationPanelSync) RefreshOperationPanelCore(dispatcher);
    }

    private readonly object _operationPanelSync = new();

    private void RefreshOperationPanelCore(System.Windows.Threading.Dispatcher? dispatcher)
    {
        FileOperationJob[] jobs;
        lock (_fileJobsSync) jobs = [.. _fileJobs];
        if (jobs.Length == 0)
        {
            IsOperationPanelVisible = false;
            _operationTimer?.Stop();
            return;
        }
        if (dispatcher != null && _operationTimer == null)
        {
            _operationTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(100) };
            _operationTimer.Tick += (_, _) => RefreshOperationPanel();
        }
        if (_operationTimer is { IsEnabled: false }) _operationTimer.Start();

        var lines = BuildOperationPanel(jobs, OperationKeyName());
        OperationHeader = lines.Header;
        OperationKeyHint = lines.KeyHint;
        OperationSummary = lines.Summary;
        OperationCurrentPath = lines.CurrentPath;
        OperationCurrentSize = lines.CurrentSize;
        OperationCurrentPercent = lines.CurrentPercent;
        OperationCurrentPercentText = lines.CurrentPercentText;
        if (!OperationUpcoming.SequenceEqual(lines.Upcoming)) OperationUpcoming = lines.Upcoming;
        if (!OperationWaiting.SequenceEqual(lines.Waiting)) OperationWaiting = lines.Waiting;
        IsOperationPanelVisible = true;
    }

    /// <summary>中止キーの表示名（例：Esc）。割り当てがなければ空。</summary>
    private string OperationKeyName()
    {
        var binding = Settings.KeyBindings.LastOrDefault(b => b.Command == AppCommand.OperationCancel);
        if (binding == null) return "";
        string key = binding.Key switch
        {
            Key.Escape => "Esc",
            Key.Return => "Enter",
            Key.Back => "BS",
            _ => binding.Key.ToString()
        };
        string modifiers = (binding.Modifiers.HasFlag(ModifierKeys.Control) ? "Ctrl+" : "") +
            (binding.Modifiers.HasFlag(ModifierKeys.Alt) ? "Alt+" : "") + (binding.Modifiers.HasFlag(ModifierKeys.Shift) ? "Shift+" : "");
        return modifiers + key;
    }

    /// <summary>進行欄の各行の文字列。</summary>
    internal sealed record OperationPanelLines(string Header, string KeyHint, string Summary, string CurrentPath, string CurrentSize,
        double CurrentPercent, string CurrentPercentText, IReadOnlyList<string> Upcoming, IReadOnlyList<string> Waiting);

    /// <summary>
    /// 操作の種類（ログ・確認で使う日本語名）を、進行欄に出す英語の名前（実行中・名詞）にする。
    /// </summary>
    internal static (string Running, string Noun) OperationKindText(string kind) => kind switch
    {
        "コピー" => ("Copying", "Copy"),
        "移動" => ("Moving", "Move"),
        "削除" => ("Deleting", "Delete"),
        "展開" => ("Extracting", "Extract"),
        "アーカイブ作成" => ("Creating archive", "Create archive"),
        "アーカイブ追加" => ("Adding to archive", "Add to archive"),
        "アーカイブ内の削除" => ("Deleting in archive", "Delete in archive"),
        "アーカイブ内のフォルダー作成" => ("Creating folder in archive", "Create folder in archive"),
        _ => ("Processing", "Process"),
    };

    private static string Plural(long count, string word) => $"{count:N0} {word}{(count == 1 ? "" : "s")}";

    /// <summary>実行中と待ちの操作から、進行欄の各行を作る。jobs は積んだ順。表示は英語。</summary>
    internal static OperationPanelLines BuildOperationPanel(IReadOnlyList<FileOperationJob> jobs, string keyName)
    {
        const string gap = "  ";
        var running = jobs.FirstOrDefault(job => job.IsRunning) ?? jobs[0];
        int position = jobs.ToList().IndexOf(running) + 1;
        var s = running.GetSnapshot(OperationUpcomingLines);
        var kind = OperationKindText(s.Kind);
        string route = s.Destination == null ? s.Source : $"{s.Source} → {s.Destination}";
        string state = !s.IsRunning ? $"Waiting: {kind.Noun}" : s.IsCancelling ? $"Cancelling: {kind.Noun}" : kind.Running;
        string header = $"{state} {position}/{jobs.Count}{gap}{route}";
        string keyHint = s.IsRunning && !s.IsCancelling && keyName.Length > 0 ? $"{keyName}:Cancel" : "";

        string summary;
        if (!s.IsRunning) summary = "Waiting for the previous operation";
        else if (s.IsCounting) summary = $"Counting {Plural(s.CountedFiles, "file")}";
        else if (s.TotalFiles < 0 && s.DoneFiles == 0 && s.CurrentPath == null) summary = $"Working{gap}Elapsed {FormatDuration(s.Elapsed)}";
        else
        {
            var parts = new List<string>
            {
                s.TotalFiles >= 0 ? $"{s.DoneFiles:N0} / {Plural(s.TotalFiles, "file")}" : Plural(s.DoneFiles, "file")
            };
            if (s.TotalBytes > 0) parts.Add($"{FileEntry.FormatFileSize(s.DoneBytes)} / {FileEntry.FormatFileSize(s.TotalBytes)}");
            if (s.BytesPerSecond > 0) parts.Add($"{FileEntry.FormatFileSize((long)s.BytesPerSecond)}/s");
            parts.Add($"Elapsed {FormatDuration(s.Elapsed)}");
            if (s.Remaining is TimeSpan remaining) parts.Add($"Left {FormatDuration(remaining)}");
            summary = string.Join(gap, parts);
        }

        var upcoming = s.Upcoming.ToList();
        if (s.RemainingAfterUpcoming > 0)
        {
            string more = $"… {s.RemainingAfterUpcoming:N0} more";
            if (upcoming.Count > 0) upcoming[^1] += gap + more; else upcoming.Add(more);
        }

        var waiting = jobs.Where(job => !ReferenceEquals(job, running)).Take(OperationWaitingLines).Select(job =>
        {
            int index = jobs.ToList().IndexOf(job) + 1;
            string target = job.Destination == null ? job.Source : $"{job.Source} → {job.Destination}";
            return $"Waiting {index}/{jobs.Count}{gap}{OperationKindText(job.Kind).Noun}{gap}{target} ({Plural(job.ItemCount, "item")})";
        }).ToList();
        int hiddenWaiting = jobs.Count - 1 - waiting.Count;
        if (hiddenWaiting > 0) waiting[^1] += $"{gap}… {hiddenWaiting} more";

        return new OperationPanelLines(header, keyHint, summary,
            s.CurrentPath ?? "", s.CurrentPath != null ? FileEntry.FormatFileSize(s.CurrentSize) : "",
            s.CurrentFraction * 100, s.CurrentPath != null ? $"{s.CurrentFraction * 100:0}%" : "", upcoming, waiting);
    }

    private static string FormatDuration(TimeSpan span) => span.TotalHours >= 1
        ? ((int)span.TotalHours).ToString(CultureInfo.InvariantCulture) + span.ToString(@"\:mm\:ss", CultureInfo.InvariantCulture)
        : span.ToString(@"m\:ss", CultureInfo.InvariantCulture);
}
