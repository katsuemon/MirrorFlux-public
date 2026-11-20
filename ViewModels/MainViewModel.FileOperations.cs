using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using MirrorFlux.Controls;
using MirrorFlux.Input;
using MirrorFlux.Models;
using MirrorFlux.Services;
using MirrorFlux.Services.Music;

namespace MirrorFlux.ViewModels;

/// <summary>
/// メイン画面のビューモデルのうち、リネーム・コピー・移動・フォルダー作成・削除のファイル操作を受け持つ部分。
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>
    /// 選択中のファイルまたはフォルダーの名前などの項目情報をダイアログで変更する。
    /// </summary>
    private async Task RenameSelectedFileAsync()
    {
        // 親フォルダーの項目とアーカイブ内は対象外。
        var pane = ActivePane;
        var selected = pane.SelectedItem;
        if (selected is null || selected.IsParent || ArchiveService.IsInsideArchive(pane.CurrentPath))
        {
            StatusMessage = "リネームは通常の単一ファイルまたはフォルダーが対象です（アーカイブ内部は対象外）。";
            AddLog(StatusMessage, "WARN");
            return;
        }
        try
        {
            // ダイアログを開く。取り消したら中止をログへ残す。
            var dialog = new FileRenameDialog(selected.FullPath);
            if (Application.Current.MainWindow is MainWindow window) dialog.PositionCenter(window);
            if (dialog.ShowDialog() != true || dialog.ResultPath is null)
            {
                AddLog($"項目情報の変更を中止しました: {selected.FullPath}", "CANCEL");
                (Application.Current?.MainWindow as MainWindow)?.FocusActiveList();
                return;
            }
            // 変更をログへ残し、ファイル一覧を読み直して変更後の項目を選ぶ。
            AddLog($"{selected.FullPath} -> {dialog.ResultPath}", "RENAME");
            await LoadPaneAsync(pane, pane.CurrentPath);
            pane.SelectedItem = pane.Entries.FirstOrDefault(entry =>
                string.Equals(entry.FullPath, dialog.ResultPath, StringComparison.OrdinalIgnoreCase)) ?? pane.SelectedItem;
            // 反対側のパネルが名前を変えたフォルダーの中にいれば、新しいパスへ付け替えて読み直す。
            // 同じフォルダーを開いているだけなら、そのまま読み直す。
            var other = ReferenceEquals(pane, LeftPane) ? RightPane : LeftPane;
            if (selected.IsDirectory && FileOperationPaths.IsSameOrDescendant(other.CurrentPath, selected.FullPath))
            {
                string relative = Path.GetRelativePath(selected.FullPath, other.CurrentPath);
                string rebasedPath = relative == "." ? dialog.ResultPath : Path.Combine(dialog.ResultPath, relative);
                await LoadPaneAsync(other, rebasedPath);
            }
            else if (string.Equals(other.CurrentPath, pane.CurrentPath, StringComparison.OrdinalIgnoreCase))
                await LoadPaneAsync(other, other.CurrentPath);
            StatusMessage = $"項目情報変更完了: {dialog.ResultPath}";
            AddLog(StatusMessage, "DONE");
        }
        catch (Exception ex)
        {
            // 失敗は対象のフルパスと理由をログへ残す。
            string reason = FileCopyService.FormatFailureReason(ex);
            StatusMessage = $"項目情報の変更失敗: {reason}";
            AddLog($"項目情報変更失敗: {selected.FullPath}\n  [原因: {reason}]", "ERR");
        }
        (Application.Current?.MainWindow as MainWindow)?.FocusActiveList();
    }

    /// <summary>
    /// コピー・移動の対象を集める。マークがあればマークした項目、なければ選択中の項目を使う。
    /// </summary>
    private static List<FileEntry> GetTransferItems(FilePaneViewModel pane)
    {
        var marked = pane.Entries.Where(entry => entry.IsMarked && !entry.IsParent).ToList();
        if (marked.Count == 0 && pane.SelectedItem is { IsParent: false } selected)
            marked.Add(selected);
        return marked;
    }

    /// <summary>
    /// コピー・移動の確認ダイアログを開き、実行してよいかを返す。
    /// </summary>
    private static bool ConfirmTransfer(IReadOnlyList<FileEntry> items, string destination, bool isMove)
    {
        RetroDialogWindow dialog = ConfirmDialog.ForTransfer(items, destination, isMove);
        if (Application.Current.MainWindow is MainWindow window) dialog.PositionCenter(window);
        return dialog.ShowDialog() == true;
    }

    /// <summary>
    /// 同名の項目があったときの競合ダイアログを開き、選ばれた対応を返す。
    /// ファイル操作は別スレッドで動くため、ダイアログはUIスレッドで開く。
    /// </summary>
    private static async Task<ConflictResolution> ResolveTransferConflictAsync(ConflictInfo info, bool isMove)
    {
        ConflictResolution? resolution = null;
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var dialog = new CopyConflictDialog(info, isMove);
            if (Application.Current.MainWindow is MainWindow window) dialog.PositionCenter(window);
            dialog.ShowDialog();
            resolution = dialog.Resolution;
        });
        return resolution!;
    }

    /// <summary>
    /// コピー・移動の後に両方のパネルを読み直す。
    /// 成功した項目だけマークを外し、失敗やスキップした項目はマークを残して再操作できるようにする。
    /// </summary>
    private async Task ReloadTransferPanesAsync(FilePaneViewModel sourcePane, FilePaneViewModel destPane,
        IReadOnlyCollection<string>? successfulPaths = null)
    {
        if (successfulPaths is { Count: > 0 }) sourcePane.Unmark(successfulPaths);
        await LoadPaneAsync(destPane, destPane.CurrentPath);
        await LoadPaneAsync(sourcePane, sourcePane.CurrentPath, preserveMarks: true);
    }

    /// <summary>
    /// コピー・移動の結果を、件数のサマリーとして状態表示とログに出す。
    /// </summary>
    private void FinishTransfer(string operation, int completed, int skipped, int errors, string? lastError)
    {
        // 1件も成功しなかったときは最後のエラーを表示する。
        if (errors > 0 && completed == 0)
        {
            StatusMessage = lastError ?? $"{operation}失敗 ({errors}件エラー)";
            // 全件失敗でも件数のサマリーをログへ残す。
            AddLog($"{operation}失敗 (0件{operation}, {skipped}件スキップ, {errors}件エラー)", "WARN");
            return;
        }
        // 成功があれば完了の件数を出す。エラーがあれば警告にする。
        StatusMessage = errors > 0
            ? $"{operation}完了 ({completed}件{operation}, {skipped}件スキップ, {errors}件エラー)"
            : $"{operation}完了 ({completed}件{operation}, {skipped}件スキップ)";
        AddLog(StatusMessage, errors > 0 ? "WARN" : "DONE");
    }

    /// <summary>
    /// マークした項目、なければ選択中の項目を反対側のパネルのフォルダーへコピーする。
    /// コピー先がアーカイブ内なら追加し、コピー元がアーカイブ内なら展開する。
    /// </summary>
    private async Task CopySelectedOrMarkedAsync(bool confirmBeforeCopy = false)
    {
        // 操作中のパネルをコピー元、反対側のパネルをコピー先にする。
        var sourcePane = ActivePane;
        var destPane = ReferenceEquals(ActivePane, LeftPane) ? RightPane : LeftPane;

        var items = GetTransferItems(sourcePane);

        if (items.Count == 0)
        {
            AddLog("[コピー] コピー対象のファイルまたはフォルダーがありません", "WARN");
            StatusMessage = "コピー対象が選択されていません";
            return;
        }

        // コピー先がアーカイブ内なら、アーカイブへの追加として扱う。
        var destDir = destPane.CurrentPath;
        if (ArchiveService.IsInsideArchive(destDir))
        {
            // 書き込めない形式のアーカイブなら理由を出して終わる。
            if (!ArchiveService.CanModifyArchive(destDir, out var reason))
            {
                AddLog($"[コピー] {reason}", "WARN");
                StatusMessage = reason ?? "アーカイブへの書き込みに対応していません";
                return;
            }

            // 追加先のアーカイブ自身（またはそれを含むフォルダー）を追加しようとしていないか調べる。
            ArchiveService.TrySplitArchivePath(destDir, out var targetArcPath, out var targetInternalPath);
            var selfItem = items.FirstOrDefault(item => FileOperationPaths.ContainsArchive(item.FullPath, item.IsDirectory, targetArcPath));

            // 自分自身への再帰的な追加になるので、エラーダイアログを出して止める。
            if (selfItem != null)
            {
                AddLog($"[エラー] アーカイブファイル自身を追加することはできません: {selfItem.Name} -> {Path.GetFileName(targetArcPath)}", "ERR");
                StatusMessage = "エラー: アーカイブ自身への追加はできません";

                var details = new List<DialogDetailRow>
                {
                    new("対象項目", selfItem.Name, IsHighlight: true),
                    new("追加先", Path.GetFileName(targetArcPath), IsHighlight: false),
                    new("エラー理由", "自分自身への再帰追加は抑制されています", IsHighlight: false)
                };

                var errDialog = new ErrorDialog(
                    "エラー",
                    "アーカイブファイル自身をアーカイブ内に追加することはできません。",
                    details);

                if (Application.Current.MainWindow is MainWindow errMw)
                {
                    errDialog.PositionCenter(errMw);
                }
                errDialog.ShowDialog();
                return;
            }

            // アーカイブの原本を書き換えるので、安全のため設定に関係なく必ず確認ダイアログを出す。
            if (!ConfirmTransfer(items, destDir, isMove: false))
            {
                StatusMessage = "コピーが中止されました";
                AddLog("コピーが中止されました", "CANCEL");
                return;
            }

            StatusMessage = $"{items.Count}項目のアーカイブ追加中...";

            // 同名の項目があったときは競合ダイアログで対応を選ばせる。
            Func<ConflictInfo, Task<ConflictResolution>> archiveConflictResolver =
                info => ResolveTransferConflictAsync(info, isMove: false);

            var addJob = new FileOperationJob("アーカイブ追加", sourcePane.CurrentPath, targetArcPath, items.Count);
            try
            {
                // アーカイブへ追加し、成功した項目だけマークを外して両方のパネルを読み直す。
                // アーカイブは一時ファイルに書いてから置き換えるので、中止すると何も追加されず原本はそのまま。
                var (copied, skipped, successfulPaths) = await RunFileOperationAsync(() => ArchiveService.AddEntriesToArchiveAsync(
                    targetArcPath,
                    targetInternalPath,
                    items,
                    archiveConflictResolver,
                    (msg, tag) => AddLog(msg, tag),
                    addJob.Token), addJob);

                await ReloadTransferPanesAsync(sourcePane, destPane, successfulPaths);

                StatusMessage = $"アーカイブへコピー完了 ({copied}件追加, {skipped}件スキップ)";
                AddLog($"アーカイブへコピー完了 ({copied}件追加, {skipped}件スキップ)", "DONE");
                (Application.Current?.MainWindow as MainWindow)?.FocusActiveList();
                return;
            }
            catch (OperationCanceledException)
            {
                StatusMessage = "アーカイブへの追加を中止しました (アーカイブは変更していません)";
                AddLog(StatusMessage, "CANCEL");
                return;
            }
            catch (Exception ex)
            {
                AddLog($"アーカイブへのコピー失敗: {ex.Message}", "ERR");
                StatusMessage = $"コピーエラー: {ex.Message}";
                return;
            }
        }

        // 通常フォルダーへのコピー。コピー先が消えていれば止める。
        if (!Directory.Exists(destDir))
        {
            AddLog($"[コピー] コピー先フォルダーが存在しません: {destDir}", "ERR");
            StatusMessage = "コピー先フォルダーが存在しません";
            return;
        }

        // 同じフォルダーへのコピーは許可し、重複した名前は競合ダイアログで連番やリネームにする。
        if (string.Equals(Path.GetFullPath(sourcePane.CurrentPath), Path.GetFullPath(destDir), StringComparison.OrdinalIgnoreCase))
        {
            AddLog("[コピー] 同一フォルダー内コピー（重複時は連番またはリネームで対応）", "INFO");
        }

        // 確認付きのコピーなら確認ダイアログを出す。
        if (confirmBeforeCopy)
        {
            if (!ConfirmTransfer(items, destDir, isMove: false))
            {
                StatusMessage = "コピーが中止されました";
                AddLog("コピーが中止されました", "CANCEL");
                return;
            }
        }

        StatusMessage = $"{items.Count}項目のコピーを開始中...";

        // 同名の項目があったときは競合ダイアログで対応を選ばせる。
        Func<ConflictInfo, Task<ConflictResolution>> conflictResolver =
            info => ResolveTransferConflictAsync(info, isMove: false);

        // エラーの件数と最後のエラーを数えながらログへ流す。
        int errorCount = 0;
        string? lastError = null;
        Action<string, string> copyLogAction = (msg, tag) =>
        {
            if (tag == "ERR") { Interlocked.Increment(ref errorCount); lastError = msg; }
            AddLog(msg, tag);
        };
        bool fromArchive = ArchiveService.TrySplitArchivePath(sourcePane.CurrentPath, out var archiveFilePath, out _);
        var job = new FileOperationJob(fromArchive ? "展開" : "コピー", fromArchive ? archiveFilePath : sourcePane.CurrentPath, destDir, items.Count);
        try
        {
            int copied, skipped;
            List<string> successfulPaths;

            if (fromArchive)
            {
                // コピー元がアーカイブ内なら、コピー先へ展開する。展開するファイルの一覧は展開の処理が決める。
                (copied, skipped, successfulPaths) = await RunFileOperationAsync(() => ArchiveService.ExtractEntriesAsync(
                    archiveFilePath,
                    items,
                    destDir,
                    conflictResolver,
                    copyLogAction,
                    job.Token), job);
            }
            else
            {
                // 通常のフォルダー間コピー。始める前に件数と量を数える。
                var result = await RunFileOperationAsync(() => FileCopyService.CopyEntriesAsync(
                    items,
                    destDir,
                    conflictResolver,
                    copyLogAction,
                    job.Token), job, items);
                (copied, skipped, successfulPaths) = result;
                errorCount = Math.Max(errorCount, result.ErrorCount);
            }

            // 成功した項目だけマークを外して読み直し、サマリーを出す。
            await ReloadTransferPanesAsync(sourcePane, destPane, successfulPaths);
            FinishTransfer("コピー", copied, skipped, errorCount, lastError);
            (Application.Current?.MainWindow as MainWindow)?.FocusActiveList();
        }
        catch (OperationCanceledException)
        {
            // 中止しても途中までの結果を表示するため読み直す。終わった項目だけマークを外す。
            await ReloadTransferPanesAsync(sourcePane, destPane, job.CompletedItems);
            FinishCanceled(job, errorCount);
            (Application.Current?.MainWindow as MainWindow)?.FocusActiveList();
        }
        catch (Exception ex)
        {
            AddLog($"コピー失敗: {ex.Message}", "ERR");
            StatusMessage = $"コピーエラー: {ex.Message}";
            (Application.Current?.MainWindow as MainWindow)?.FocusActiveList();
        }
    }

    /// <summary>
    /// マークした項目、なければ選択中の項目を反対側のパネルのフォルダーへ移動する。
    /// </summary>
    private async Task MoveSelectedOrMarkedAsync(bool confirmBeforeMove = false)
    {
        // 操作中のパネルを移動元、反対側のパネルを移動先にする。
        var sourcePane = ActivePane;
        var destPane = ReferenceEquals(ActivePane, LeftPane) ? RightPane : LeftPane;

        var items = GetTransferItems(sourcePane);

        if (items.Count == 0)
        {
            AddLog("[移動] 移動対象のファイルまたはフォルダーがありません", "WARN");
            StatusMessage = "移動対象が選択されていません";
            return;
        }

        // アーカイブ内が関わる移動は扱わない。展開やコピーを使ってもらう。
        if (ArchiveService.IsInsideArchive(sourcePane.CurrentPath) || ArchiveService.IsInsideArchive(destPane.CurrentPath))
        {
            AddLog("[移動] アーカイブ内の直接移動はサポートされていません (解凍・コピーをご利用ください)", "WARN");
            StatusMessage = "アーカイブ内の移動はサポートされていません";
            return;
        }

        // 移動先が消えていれば止める。
        var destDir = destPane.CurrentPath;
        if (!Directory.Exists(destDir))
        {
            AddLog($"[移動] 移動先フォルダーが存在しません: {destDir}", "ERR");
            StatusMessage = "移動先フォルダーが存在しません";
            return;
        }

        // 同じフォルダーへの移動は意味がないので止める。
        if (string.Equals(Path.GetFullPath(sourcePane.CurrentPath), Path.GetFullPath(destDir), StringComparison.OrdinalIgnoreCase))
        {
            AddLog("[移動] 同一フォルダーへの移動はできません", "WARN");
            StatusMessage = "同一フォルダーへの移動はできません";
            return;
        }

        // 確認付きの移動なら確認ダイアログを出す。
        if (confirmBeforeMove)
        {
            if (!ConfirmTransfer(items, destDir, isMove: true))
            {
                StatusMessage = "移動が中止されました";
                AddLog("移動が中止されました", "CANCEL");
                return;
            }
        }

        StatusMessage = $"{items.Count}項目の移動を開始中...";

        // 同名の項目があったときは競合ダイアログで対応を選ばせる。
        Func<ConflictInfo, Task<ConflictResolution>> conflictResolver =
            info => ResolveTransferConflictAsync(info, isMove: true);

        // エラーの件数と最後のエラーを数えながらログへ流す。
        int moveErrorCount = 0;
        string? lastMoveError = null;
        Action<string, string> moveLogAction = (msg, tag) =>
        {
            if (tag == "ERR") { Interlocked.Increment(ref moveErrorCount); lastMoveError = msg; }
            AddLog(msg, tag);
        };

        var job = new FileOperationJob("移動", sourcePane.CurrentPath, destDir, items.Count);
        try
        {
            // 移動し、成功した項目だけマークを外して読み直し、サマリーを出す。始める前に件数と量を数える。
            var (moved, skipped, successfulPaths) = await RunFileOperationAsync(() => FileMoveService.MoveEntriesAsync(
                items,
                destDir,
                conflictResolver,
                moveLogAction,
                job.Token), job, items);

            await ReloadTransferPanesAsync(sourcePane, destPane, successfulPaths);
            FinishTransfer("移動", moved, skipped, moveErrorCount, lastMoveError);
            (Application.Current?.MainWindow as MainWindow)?.FocusActiveList();
        }
        catch (OperationCanceledException)
        {
            // 中止や失敗でも途中まで動いた項目を表示するため読み直す。
            await ReloadTransferPanesAsync(sourcePane, destPane, job.CompletedItems);
            FinishCanceled(job, moveErrorCount);
            (Application.Current?.MainWindow as MainWindow)?.FocusActiveList();
        }
        catch (Exception ex)
        {
            await ReloadTransferPanesAsync(sourcePane, destPane);
            AddLog($"移動失敗: {ex.Message}", "ERR");
            StatusMessage = $"移動エラー: {ex.Message}";
            (Application.Current?.MainWindow as MainWindow)?.FocusActiveList();
        }
    }

    /// <summary>
    /// 操作中のパネルのフォルダーに新しいフォルダーを作る。アーカイブ内にも作れる。
    /// </summary>
    public async Task CreateDirectoryAsync()
    {
        if (Application.Current.MainWindow is not MainWindow mw) return;

        // 書き込めない形式のアーカイブ内なら理由を出して終わる。
        bool isInsideArchive = ArchiveService.IsInsideArchive(ActivePane.CurrentPath);
        if (isInsideArchive && !ArchiveService.CanModifyArchive(ActivePane.CurrentPath, out var reason))
        {
            AddLog($"[フォルダ作成] {reason}", "WARN");
            StatusMessage = reason ?? "アーカイブを変更できません";
            return;
        }

        // フォルダー名を入力するダイアログを開く。
        bool isLeft = ReferenceEquals(ActivePane, LeftPane);
        var dialog = new MakeDirDialog();
        dialog.PositionOverPane(mw, isLeft, topOffset: 24);

        bool? result = dialog.ShowDialog();
        if (result == true && !string.IsNullOrWhiteSpace(dialog.DirectoryName))
        {
            var dirName = dialog.DirectoryName.Trim();

            // アーカイブ内ではアーカイブにフォルダーの項目を追加する。
            if (isInsideArchive)
            {
                if (ArchiveService.TrySplitArchivePath(ActivePane.CurrentPath, out var archiveFilePath, out var internalPath))
                {
                    try
                    {
                        await RunFileOperationAsync(() => ArchiveService.CreateArchiveDirectoryAsync(
                            archiveFilePath,
                            internalPath,
                            dirName,
                            (msg, tag) => AddLog(msg, tag)), new FileOperationJob("アーカイブ内のフォルダー作成", archiveFilePath, null, 1));

                        StatusMessage = $"ディレクトリ作成: {dirName}";
                        AddLog($"ディレクトリ作成完了: {dirName}", "DONE");

                        // ファイル一覧を読み直し、作ったフォルダーを選ぶ。
                        await LoadPaneAsync(ActivePane, ActivePane.CurrentPath);
                        var createdEntry = ActivePane.Entries.FirstOrDefault(i => string.Equals(i.Name, dirName, StringComparison.OrdinalIgnoreCase));
                        if (createdEntry is not null)
                        {
                            ActivePane.SelectedItem = createdEntry;
                        }
                        StatusMessage = $"ディレクトリ作成: {dirName}";

                        // 反対側のパネルが同じ場所を開いていれば、そちらも読み直す。
                        var otherPane = isLeft ? RightPane : LeftPane;
                        if (string.Equals(otherPane.CurrentPath, ActivePane.CurrentPath, StringComparison.OrdinalIgnoreCase))
                        {
                            await LoadPaneAsync(otherPane, otherPane.CurrentPath);
                        }
                    }
                    catch (Exception ex)
                    {
                        string failReason = FileCopyService.FormatFailureReason(ex);
                        AddLog($"ディレクトリ作成失敗: {dirName}\n  [原因: {failReason}]", "ERR");
                        StatusMessage = $"ディレクトリ作成失敗: {failReason}";
                    }
                }

                mw.FocusActiveList();
                return;
            }

            // 通常フォルダーでは実際にフォルダーを作る。
            var targetPath = Path.Combine(ActivePane.CurrentPath, dirName);

            try
            {
                // 同名のファイルやフォルダーがあれば作らない。
                if (Directory.Exists(targetPath) || File.Exists(targetPath))
                {
                    AddLog($"既に同名の項目が存在します: {targetPath}", "WARN");
                    StatusMessage = $"同名のファイルまたはフォルダーが既に存在します: {dirName}";
                    mw.FocusActiveList();
                    return;
                }

                Directory.CreateDirectory(targetPath);
                AddLog(targetPath, "MKDIR");
                StatusMessage = $"ディレクトリ作成: {targetPath}";

                // ファイル一覧を読み直し、作ったフォルダーを選ぶ。
                await LoadPaneAsync(ActivePane, ActivePane.CurrentPath);
                var createdEntry = ActivePane.Entries.FirstOrDefault(i => string.Equals(i.Name, dirName, StringComparison.OrdinalIgnoreCase));
                if (createdEntry is not null)
                {
                    ActivePane.SelectedItem = createdEntry;
                }
                StatusMessage = $"ディレクトリ作成: {targetPath}";

                // 反対側のパネルが同じフォルダーを開いていれば、そちらも読み直す。
                var otherPane = isLeft ? RightPane : LeftPane;
                if (string.Equals(otherPane.CurrentPath, ActivePane.CurrentPath, StringComparison.OrdinalIgnoreCase))
                {
                    await LoadPaneAsync(otherPane, otherPane.CurrentPath);
                }
            }
            catch (Exception ex)
            {
                string failReason = FileCopyService.FormatFailureReason(ex);
                AddLog($"ディレクトリ作成失敗: {targetPath}\n  [原因: {failReason}]", "ERR");
                StatusMessage = $"ディレクトリ作成失敗: {failReason}";
            }
        }

        mw.FocusActiveList();
    }

    /// <summary>
    /// マークした項目、なければ選択中の項目を削除する。
    /// 通常フォルダーではごみ箱へ送り、アーカイブ内ではアーカイブから項目を取り除く。
    /// </summary>
    public async Task DeleteSelectedOrMarkedAsync()
    {
        if (Application.Current.MainWindow is not MainWindow mw) return;

        // 書き込めない形式のアーカイブ内なら理由を出して終わる。
        bool isInsideArchive = ArchiveService.IsInsideArchive(ActivePane.CurrentPath);
        if (isInsideArchive && !ArchiveService.CanModifyArchive(ActivePane.CurrentPath, out var reason))
        {
            AddLog($"[削除] {reason}", "WARN");
            StatusMessage = reason ?? "アーカイブ内の削除に対応していません";
            return;
        }

        // マークした項目、なければ選択中の項目を対象にする。
        var markedItems = ActivePane.Entries.Where(i => i.IsMarked).ToList();
        var targetItems = markedItems.Count > 0
            ? markedItems
            : (ActivePane.SelectedItem is not null ? new List<FileEntry> { ActivePane.SelectedItem } : new List<FileEntry>());

        // 親フォルダーの項目（..）は削除対象から外す。
        targetItems = targetItems.Where(i => !string.Equals(i.Name, "..", StringComparison.Ordinal)).ToList();

        if (targetItems.Count == 0)
        {
            AddLog("削除対象のファイルまたはフォルダーがありません", "WARN");
            StatusMessage = "削除対象がありません";
            return;
        }

        // 一覧が変わる前に、削除後に選ぶ項目を求めておく。反対側のパネルが同じフォルダーならそちらも求める。
        string? selectionAfterDelete = ActivePane.SelectionPathAfterRemoving(targetItems);
        var peerPane = ReferenceEquals(ActivePane, LeftPane) ? RightPane : LeftPane;
        bool refreshPeerPane = string.Equals(peerPane.CurrentPath, ActivePane.CurrentPath, StringComparison.OrdinalIgnoreCase);
        string? peerSelectionAfterDelete = refreshPeerPane ? peerPane.SelectionPathAfterRemoving(targetItems) : null;

        // 削除の確認ダイアログを開く。
        var dialog = ConfirmDialog.ForDelete(targetItems, isInsideArchive);
        dialog.PositionCenter(mw);

        bool? result = dialog.ShowDialog();
        if (result == true)
        {
            // アーカイブ内ではアーカイブから項目を取り除く。
            if (isInsideArchive)
            {
                if (ArchiveService.TrySplitArchivePath(ActivePane.CurrentPath, out var archiveFilePath, out _))
                {
                    try
                    {
                        int arcDeletedCount = await RunFileOperationAsync(() => ArchiveService.DeleteArchiveEntriesAsync(
                            archiveFilePath,
                            targetItems,
                            (msg, tag) => AddLog(msg, tag)), new FileOperationJob("アーカイブ内の削除", archiveFilePath, null, targetItems.Count));

                        // 削除できたらマークを外し、求めておいた選択で両方のパネルを読み直す。
                        if (arcDeletedCount > 0)
                        {
                            StatusMessage = $"アーカイブから削除完了 ({arcDeletedCount}件)";
                            AddLog($"削除完了 ({arcDeletedCount}件)", "DONE");

                            ActivePane.Unmark(targetItems.Select(i => i.FullPath));

                            await LoadPaneAsync(ActivePane, ActivePane.CurrentPath, selectionAfterDelete);
                            if (refreshPeerPane)
                                await LoadPaneAsync(peerPane, peerPane.CurrentPath, peerSelectionAfterDelete);
                        }
                        else
                        {
                            StatusMessage = "削除完了 (0件)";
                            AddLog("削除完了 (0件)", "DONE");
                        }
                    }
                    catch (Exception ex)
                    {
                        AddLog($"アーカイブ削除失敗: {ex.Message}", "ERR");
                        StatusMessage = $"削除失敗: {ex.Message}";
                    }
                }

                mw.FocusActiveList();
                return;
            }

            int deletedCount = 0;
            int errorCount = 0;
            var deletedItems = new List<FileEntry>();
            var deleteJob = new FileOperationJob("削除", ActivePane.CurrentPath, null, targetItems.Count);
            var deletePane = ActivePane;
            bool deleteCanceled = false;

            // 通常フォルダーでは1件ずつごみ箱へ送り、対象ごとにフルパスと結果をログへ残す。
            // 中止は項目と項目の間で効く（フォルダーは中身ごと1回で送るので、その途中では止まらない）。
            try
            {
                await RunFileOperationAsync(async () =>
                {
                    foreach (var item in targetItems)
                    {
                        deleteJob.Token.ThrowIfCancellationRequested();
                        deleteJob.BeginFile(item.FullPath, item.IsDirectory ? 0 : item.Length);
                        try
                        {
                            var (delSuccess, delReason) = RecycleBinService.DeleteToRecycleBinWithResult(item.FullPath);
                            if (delSuccess)
                            {
                                deletedCount++;
                                deletedItems.Add(item);
                                AddLog($"[ごみ箱] {item.FullPath}", "DEL");
                            }
                            else
                            {
                                errorCount++;
                                string reasonText = string.IsNullOrEmpty(delReason) ? "" : $"\n  [原因: {delReason}]";
                                AddLog($"削除失敗: {item.FullPath}{reasonText}", "ERR");
                            }
                            await Task.Delay(10, deleteJob.Token);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            errorCount++;
                            string reason = FileCopyService.FormatFailureReason(ex);
                            AddLog($"削除失敗: {item.FullPath}\n  [原因: {reason}]", "ERR");
                        }
                        finally { deleteJob.EndFile(); }
                    }
                }, deleteJob, targetItems, recursePlan: false);
            }
            catch (OperationCanceledException) { deleteCanceled = true; }

            // 結果のサマリーを出す。中止したときは、削除できた分だけ一覧に反映する。
            if (deleteCanceled)
            {
                FinishCanceled(deleteJob, errorCount);
                if (deletedCount > 0)
                {
                    await LoadPaneAsync(deletePane, deletePane.CurrentPath, deletePane.SelectionPathAfterRemoving(deletedItems));
                    if (refreshPeerPane)
                        await LoadPaneAsync(peerPane, peerPane.CurrentPath, peerPane.SelectionPathAfterRemoving(deletedItems));
                }
            }
            else if (deletedCount > 0)
            {
                StatusMessage = $"削除完了 ({deletedCount}件)";
                AddLog($"削除完了 ({deletedCount}件)", "DONE");

                // 実際に削除できた項目だけを基準に選択を求め直してから読み直す。
                string? actualSelectionAfterDelete = deletePane.SelectionPathAfterRemoving(deletedItems);
                await LoadPaneAsync(deletePane, deletePane.CurrentPath, actualSelectionAfterDelete);
                if (refreshPeerPane)
                    await LoadPaneAsync(peerPane, peerPane.CurrentPath, peerPane.SelectionPathAfterRemoving(deletedItems));
            }
            else if (errorCount > 0)
            {
                StatusMessage = $"削除に失敗しました ({errorCount} 件)";
                AddLog($"削除に失敗しました ({errorCount} 件)", "ERR");
            }
            else
            {
                StatusMessage = "削除完了 (0件)";
                AddLog("削除完了 (0件)", "DONE");
            }
        }
        // 取り消したら中止をログへ残す。
        else
        {
            StatusMessage = "削除を中止しました";
            AddLog("削除を中止しました", "CANCEL");
        }

        mw.FocusActiveList();
    }
}
