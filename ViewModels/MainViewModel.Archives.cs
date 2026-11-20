using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using MirrorFlux.Models;
using MirrorFlux.Services;

namespace MirrorFlux.ViewModels;

/// <summary>
/// メイン画面のビューモデルのうち、全マーク・絞り込み・アーカイブ作成を受け持つ部分。
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>
    /// 操作中のパネルで表示中の項目をすべてマークし、件数を表示する。
    /// </summary>
    private void MarkAllVisible()
    {
        ActivePane.MarkAllVisible();
        int count = ActivePane.VisibleEntries.Cast<FileEntry>().Count(entry => entry.IsMarked && !entry.IsParent);
        StatusMessage = $"{count}項目をマークしました";
    }

    /// <summary>
    /// インクリメンタル検索のダイアログを開き、入力に合わせてファイル一覧を絞り込む。
    /// </summary>
    private void OpenIncrementalSearch()
    {
        // 取り消し時に戻せるよう、元の絞り込みと選択を覚えておく。
        if (Application.Current.MainWindow is not MainWindow window) return;
        var pane = ActivePane;
        string previous = pane.SearchText;
        var previousSelection = pane.SelectedItem;
        var dialog = new IncrementalSearchDialog(previous, pane.ApplySearch);
        dialog.PositionOverPane(window, ReferenceEquals(pane, LeftPane), topOffset: 24);
        // 取り消したら元の絞り込みと選択へ戻す。
        if (dialog.ShowDialog() != true)
        {
            pane.ApplySearch(previous);
            if (previousSelection != null && pane.VisibleEntries.Cast<FileEntry>().Contains(previousSelection))
                pane.SelectedItem = previousSelection;
        }
        // 結果を表示してファイル一覧へフォーカスを戻す。
        StatusMessage = pane.SearchText.Length == 0
            ? "絞り込みを解除しました"
            : $"絞り込み: {pane.SearchText}";
        window.FocusActiveList();
    }

    /// <summary>
    /// マークした項目から新しいアーカイブを作成する。
    /// </summary>
    private async Task CreateArchiveFromMarksAsync()
    {
        // アーカイブ内には作れないので、通常フォルダーでの操作に限る。
        if (Application.Current.MainWindow is not MainWindow window) return;
        var pane = ActivePane;
        if (ArchiveService.IsInsideArchive(pane.CurrentPath))
        {
            StatusMessage = "新規アーカイブは通常フォルダー内で作成してください";
            AddLog(StatusMessage, "WARN");
            return;
        }
        // マークがなければ案内して終わる。
        var items = pane.Entries.Where(entry => entry.IsMarked && !entry.IsParent).ToList();
        if (items.Count == 0)
        {
            StatusMessage = "圧縮するファイル・フォルダーをマークしてください";
            AddLog(StatusMessage, "WARN");
            return;
        }
        // 1件ならその名前、複数なら archive を既定のファイル名にしてダイアログを開く。
        string stem = items.Count == 1
            ? (items[0].IsDirectory ? items[0].Name : Path.GetFileNameWithoutExtension(items[0].Name))
            : "archive";
        var dialog = new ArchiveCreateDialog(stem + ".zip", items.Count);
        dialog.PositionOverPane(window, ReferenceEquals(pane, LeftPane), topOffset: 24);
        if (dialog.ShowDialog() != true)
        {
            window.FocusActiveList();
            return;
        }
        string target = Path.Combine(pane.CurrentPath, dialog.ArchiveFileName);
        try
        {
            // 圧縮を実行する。
            int added = 0;
            await RunWithLoadingAsync($"圧縮中: {dialog.ArchiveFileName}", async () =>
                added = await RunFileOperationAsync(() => ArchiveService.CreateArchiveFromEntriesAsync(
                    target, items, dialog.ZipLevel, (message, tag) => AddLog(message, tag)),
                    new FileOperationJob("アーカイブ作成", ActivePane.CurrentPath, target, items.Count)),
                loadingTitle: "アーカイブ作成中...");
            // マークを外し、作ったアーカイブを選んだ状態でファイル一覧を読み直す。
            pane.Unmark(items.Select(item => item.FullPath));
            await LoadPaneAsync(pane, pane.CurrentPath, target);
            // 反対側のパネルが同じフォルダーなら、そちらも読み直す。
            var other = ReferenceEquals(pane, LeftPane) ? RightPane : LeftPane;
            if (string.Equals(other.CurrentPath, pane.CurrentPath, StringComparison.OrdinalIgnoreCase))
                await LoadPaneAsync(other, other.CurrentPath);
            StatusMessage = $"アーカイブ作成完了: {target} ({added}件)";
            AddLog(StatusMessage, "DONE");
        }
        catch (Exception ex)
        {
            // 失敗は理由を付けてログへ残す。
            string reason = FileCopyService.FormatFailureReason(ex);
            StatusMessage = $"アーカイブ作成失敗: {reason}";
            AddLog($"{target}\n  [原因: {reason}]", "ERR");
        }
        window.FocusActiveList();
    }
}
