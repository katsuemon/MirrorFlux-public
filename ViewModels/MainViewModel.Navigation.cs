using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using MirrorFlux.Input;
using MirrorFlux.Models;
using MirrorFlux.Services;
using MirrorFlux.Services.Music;

namespace MirrorFlux.ViewModels;

/// <summary>
/// メイン画面のビューモデルのうち、フォルダー移動・マーク・パネル切り替え・ドライブ選択・並べ替えを受け持つ部分。
/// </summary>
public sealed partial class MainViewModel
{
    private const int VisibleRows = 18;

    /// <summary>
    /// 選択中の項目を開く。フォルダーとアーカイブは中へ移動し、ファイルは選択したことを表示する。
    /// </summary>
    private async Task OpenSelectedAsync()
    {
        var selected = ActivePane.SelectedItem;
        if (selected is null)
        {
            return;
        }

        // 親フォルダーの項目なら一つ上へ戻る。
        if (selected.IsParent)
        {
            await GoParentAsync();
            return;
        }

        // フォルダーならその中へ移動する。
        if (selected.IsDirectory)
        {
            AddLog($"フォルダ移動: {selected.FullPath}", "ACT");
            await LoadPaneAsync(ActivePane, selected.FullPath);
            return;
        }

        // アーカイブファイルはその中を表示する。拡張子で先に判定し、読み込み前にログとLoading表示を出す。
        if (ArchiveService.IsSupportedArchiveExtension(Path.GetExtension(selected.FullPath)))
        {
            AddLog($"アーカイブ内表示: {selected.FullPath}", "ACT");
            await LoadPaneAsync(ActivePane, selected.FullPath);
            return;
        }

        // 通常ファイルは選択したことだけを表示する。
        AddLog($"ファイル選択: {selected.Name} ({selected.SizeText})", "INFO");
        StatusMessage = $"選択: {selected.Name} ({selected.SizeText})";
    }

    /// <summary>
    /// 操作中のパネルを親フォルダーへ移動する。アーカイブのルートからは元のフォルダーへ戻る。
    /// </summary>
    private async Task GoParentAsync()
    {
        var pane = ActivePane;
        var currentPath = pane.CurrentPath;
        if (string.IsNullOrEmpty(currentPath)) return;

        // 親フォルダーの項目があればそのパスを、なければパスから親を求める。
        var parentEntry = pane.Entries.FirstOrDefault(e => e.IsParent);
        string? parentPath = parentEntry?.FullPath;
        if (string.IsNullOrEmpty(parentPath))
        {
            parentPath = await Task.Run(() => ArchiveService.GetParentPath(currentPath));
        }

        // ルートなどで親が同じパスなら移動しない。
        if (!string.IsNullOrEmpty(parentPath) && !string.Equals(parentPath, currentPath, StringComparison.OrdinalIgnoreCase))
        {
            AddLog($"親ディレクトリへ移動: {parentPath}", "ACT");
            await LoadPaneAsync(pane, parentPath);
        }
    }

    /// <summary>
    /// 選択中の項目のマークを切り替え、結果をログと状態表示に出す。
    /// </summary>
    private void ToggleMark()
    {
        // 親フォルダーの項目はマークできない。
        var item = ActivePane.SelectedItem;
        if (item is null || item.IsParent)
        {
            StatusMessage = "マークする項目がありません";
            return;
        }

        ActivePane.ToggleSelectedMark();
        var state = item.IsMarked ? "ON" : "OFF";
        AddLog($"マーク {state}: {item.Name}", "MARK");
        StatusMessage = $"マーク {state}: {item.Name} ({ActivePane.EntrySummary})";
    }

    /// <summary>
    /// 選択中の項目の名前・サイズ・日時を状態表示に出す。
    /// </summary>
    private void UpdateSelectionStatus()
    {
        var item = ActivePane.SelectedItem;
        if (item is null)
        {
            return;
        }

        var markStr = item.IsMarked ? "[*] " : "";
        var sizeStr = item.IsDirectory ? "<DIR>" : FileEntry.FormatFileSize(item.Length);
        StatusMessage = $"{markStr}{item.DisplayName}  {sizeStr}  {item.ModifiedText}";
    }

    /// <summary>
    /// Loading表示を出しながらパネルのファイル一覧を読み込む。失敗したらログへ残す。
    /// </summary>
    private async Task LoadPaneAsync(FilePaneViewModel pane, string path, string? preferredSelection = null, bool preserveMarks = false)
    {
        await RunWithLoadingAsync($"Loading: {path}", async () =>
        {
            try
            {
                await pane.LoadAsync(path, preferredSelection, preserveMarks: preserveMarks);
                UpdateSelectionStatus();
                OnPropertyChanged(nameof(PromptDriveText));
            }
            catch (Exception exception)
            {
                var msg = $"一覧を読み込めません ({pane.Title}): {exception.Message}";
                StatusMessage = msg;
                AddLog(msg, "ERR");
            }
        });
    }

    /// <summary>
    /// 操作中のパネルを切り替え、関係する表示を更新する。
    /// </summary>
    private void SetActivePane(FilePaneViewModel pane)
    {
        // 左右の強調表示は毎回そろえる。同じパネルならここで終わる。
        LeftPane.IsActive = ReferenceEquals(pane, LeftPane);
        RightPane.IsActive = ReferenceEquals(pane, RightPane);
        if (ReferenceEquals(_activePane, pane))
        {
            return;
        }

        // 操作中のパネルを差し替え、表示を更新する。
        _activePane = pane;
        OnPropertyChanged(nameof(ActivePane));
        OnPropertyChanged(nameof(ActivePaneText));
        OnPropertyChanged(nameof(PromptBadgeText));
        OnPropertyChanged(nameof(PromptDriveText));
        UpdateSelectionStatus();
    }

    /// <summary>
    /// ドライブ選択のダイアログを開き、選んだドライブへ操作中のパネルを移動する。
    /// ドライブごとに最後にいたフォルダーと選択を復元する。
    /// </summary>
    public async Task OpenDriveMenuAsync()
    {
        if (Application.Current.MainWindow is not MainWindow mw) return;

        // ドライブの一覧を取得する。
        DriveInfo[] systemDrives;
        try
        {
            systemDrives = DriveInfo.GetDrives();
        }
        catch (Exception ex)
        {
            AddLog($"[ドライブ切替] ドライブ一覧の取得に失敗しました: {ex.Message}", "ERR");
            return;
        }

        var countsByType = new Dictionary<DriveType, int>();
        var driveItems = new List<DriveItem>();

        // 操作中のパネルが今いるドライブ文字を求める。
        char currentLetter = 'C';
        try
        {
            var rawPath = ActivePane.CurrentPath?.Trim().Trim('"', '\'');
            if (!string.IsNullOrEmpty(rawPath))
            {
                var root = Path.GetPathRoot(rawPath);
                if (!string.IsNullOrEmpty(root) && char.IsLetter(root[0]))
                {
                    currentLetter = char.ToUpperInvariant(root[0]);
                }
                else if (char.IsLetter(rawPath[0]))
                {
                    currentLetter = char.ToUpperInvariant(rawPath[0]);
                }
            }
        }
        catch
        {
            if (!string.IsNullOrEmpty(ActivePane.CurrentPath) && char.IsLetter(ActivePane.CurrentPath[0]))
            {
                currentLetter = char.ToUpperInvariant(ActivePane.CurrentPath[0]);
            }
        }

        // ドライブごとに、種類と種類内の番号、ボリューム名を並べた行を作る。
        foreach (var drive in systemDrives)
        {
            char letter = drive.Name.Length > 0 ? char.ToUpperInvariant(drive.Name[0]) : '?';
            var type = drive.DriveType;
            int index = countsByType.TryGetValue(type, out var count) ? count : 0;
            countsByType[type] = index + 1;

            string typeName = type switch
            {
                DriveType.Fixed => "FIXED",
                DriveType.Removable => "REMOVBL",
                DriveType.CDRom => "CD-ROM",
                DriveType.Network => "NETWORK",
                DriveType.Ram => "RAMDISK",
                _ => "DRIVE"
            };

            string deviceTypeDisplay = $"{typeName,-7} {index}";

            // 準備ができていないドライブはその旨を、使えるドライブはボリューム名を表示する。
            string volumeDisplay;
            if (!drive.IsReady)
            {
                string notReadyType = type switch
                {
                    DriveType.CDRom => "CD-ROM",
                    DriveType.Removable => "Removable",
                    _ => "Drive"
                };
                volumeDisplay = $"── {notReadyType} Not Ready ──";
            }
            else
            {
                string label = string.Empty;
                try
                {
                    label = drive.VolumeLabel;
                }
                catch
                {
                }

                volumeDisplay = string.IsNullOrWhiteSpace(label) ? "no volume name" : label;
            }

            bool isCurrent = letter == currentLetter;
            driveItems.Add(new DriveItem(letter, deviceTypeDisplay, volumeDisplay, drive.RootDirectory.FullName, drive.IsReady, isCurrent));
        }

        // 左右どちらかのパネルで開いているUNCのネットワーク共有も、ドライブとして一覧に加える。
        var uncRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in new[] { LeftPane.CurrentPath, RightPane.CurrentPath })
        {
            if (!string.IsNullOrWhiteSpace(p) && FileOperationPaths.IsUncPath(p))
            {
                var root = Path.GetPathRoot(p);
                if (!string.IsNullOrEmpty(root))
                {
                    uncRoots.Add(root.TrimEnd('\\', '/'));
                }
            }
        }
        int netIdx = 0;
        foreach (var unc in uncRoots)
        {
            var trimmed = unc.Trim('\\', '/');
            var slashIdx = trimmed.IndexOfAny(['\\', '/']);
            string shareName = slashIdx >= 0 ? trimmed.Substring(slashIdx + 1) : trimmed;
            bool isCurrent = string.Equals(Path.GetPathRoot(ActivePane.CurrentPath ?? "")?.TrimEnd('\\', '/'), unc, StringComparison.OrdinalIgnoreCase);
            driveItems.Add(new DriveItem('\\', $"NETWORK {netIdx++}", $"{trimmed} ({shareName})", unc, true, isCurrent));
        }

        // 操作中のパネルの上にダイアログを開く。
        bool isLeft = ReferenceEquals(ActivePane, LeftPane);
        var dialog = new DriveSelectDialog(driveItems, currentLetter);
        dialog.PositionOverPane(mw, isLeft, topOffset: 24);

        bool? result = dialog.ShowDialog();
        if (result == true)
        {
            // パス入力が選ばれたら、パス入力のダイアログへ切り替える。
            if (dialog.RequestPathInput)
            {
                await ChangeDirectoryAsync();
                return;
            }

            if (dialog.SelectedDrive is not null)
            {
                // 準備ができていないドライブへは移動しない。
                var selected = dialog.SelectedDrive;
                if (!selected.IsReady)
                {
                    AddLog($"[ドライブ切替] ドライブ {selected.Letter}: は準備ができていません (Not Ready)", "WARN");
                    StatusMessage = $"ドライブ {selected.Letter}: は準備ができていません";
                    return;
                }

                // 今の位置を覚えてから、移動先のドライブで覚えている位置と選択を復元する。
                ActivePane.RememberCurrentDriveState();
                AddLog($"[ドライブ切替] {ActivePane.Title}パネルを {selected.RootPath} ({selected.VolumeDisplay}) に切り替えました", "DRIVE");
                var targetPath = await Task.Run(() => ActivePane.GetRememberedPathForDrive(selected.RootPath)) ?? selected.RootPath;
                var targetSelection = ActivePane.GetRememberedSelectionForDrive(selected.RootPath);
                await LoadPaneAsync(ActivePane, targetPath, targetSelection);
                StatusMessage = $"{ActivePane.Title}パネル: {targetPath}";
            }
        }

        mw.FocusActiveList();
    }

    /// <summary>
    /// INIに登録したジャンプ先の一覧を開き、選んだフォルダーへ操作中のパネルを移動する。
    /// </summary>
    public async Task OpenJumpListAsync()
    {
        if (Application.Current.MainWindow is not MainWindow mw) return;
        var dialog = new JumpListDialog(_settings.JumpPaths);
        dialog.PositionOverPane(mw, ReferenceEquals(ActivePane, LeftPane), topOffset: 24);
        if (dialog.ShowDialog() == true && dialog.TargetPath is string target)
        {
            try
            {
                AddLog($"[ディレクトリ移動] {ActivePane.Title}パネルを {target} に移動しました", "ACT");
                await LoadPaneAsync(ActivePane, target);
                StatusMessage = $"{ActivePane.Title}パネル: {target}";
            }
            catch (Exception ex) { StatusMessage = $"移動エラー: {ex.Message}"; }
        }
        mw.FocusActiveList();
    }

    /// <summary>
    /// パス入力のダイアログを開き、入力したフォルダーへ操作中のパネルを移動する。
    /// </summary>
    public async Task ChangeDirectoryAsync()
    {
        if (Application.Current.MainWindow is not MainWindow mw) return;

        bool isLeft = ReferenceEquals(ActivePane, LeftPane);
        var dialog = new PathInputDialog(ActivePane.CurrentPath ?? string.Empty);
        dialog.PositionOverPane(mw, isLeft, topOffset: 24);

        bool? result = dialog.ShowDialog();
        if (result == true && !string.IsNullOrWhiteSpace(dialog.TargetPath))
        {
            var targetPath = dialog.TargetPath;
            try
            {
                // 別ドライブを「D:」「D:\」のようにドライブ名だけで指定したときは、そのドライブで最後にいたフォルダーへ移動する。
                ActivePane.RememberCurrentDriveState();
                var driveKey = FilePaneViewModel.GetDriveKey(targetPath);
                var currentKey = FilePaneViewModel.GetDriveKey(ActivePane.CurrentPath);
                if (!string.Equals(driveKey, currentKey, StringComparison.OrdinalIgnoreCase)
                    && (dialog.TargetPath.Length == 2 && dialog.TargetPath[1] == ':'
                        || dialog.TargetPath.Length == 3 && dialog.TargetPath[1] == ':' && (dialog.TargetPath[2] == '\\' || dialog.TargetPath[2] == '/')))
                {
                    targetPath = await Task.Run(() => ActivePane.GetRememberedPathForDrive(targetPath)) ?? targetPath;
                }
                // 覚えている選択を復元して移動する。
                var targetSelection = ActivePane.GetRememberedSelectionForDrive(targetPath);
                AddLog($"[ディレクトリ移動] {ActivePane.Title}パネルを {targetPath} に移動しました", "ACT");
                await LoadPaneAsync(ActivePane, targetPath, targetSelection);
                StatusMessage = $"{ActivePane.Title}パネル: {targetPath}";
            }
            catch (Exception ex)
            {
                AddLog($"[ディレクトリ移動] 移動に失敗しました: {ex.Message}", "ERR");
                StatusMessage = $"移動エラー: {ex.Message}";
            }
        }

        mw.FocusActiveList();
    }

    /// <summary>
    /// 並べ替えのダイアログを開いてパネルの並び順を変える。
    /// メイン画面がないときは並べ替えの種類を順に切り替える。
    /// </summary>
    public Task OpenSortMenuAsync(FilePaneViewModel? targetPane = null)
    {
        // 対象のパネルを操作中のパネルにする。
        var pane = targetPane ?? ActivePane;
        if (!ReferenceEquals(ActivePane, pane))
        {
            SetActivePane(pane);
        }

        // ダイアログで選んだ並び順を適用する。
        if (Application.Current?.MainWindow is MainWindow mw)
        {
            bool isLeft = ReferenceEquals(pane, LeftPane);
            var dialog = new SortDialog(pane.SortMode, pane.SortAscending, isLeft, pane.SortDirectoriesFirst);
            dialog.PositionOverPane(mw, isLeft, topOffset: 24);
            if (dialog.ShowDialog() == true)
            {
                pane.SetSort(dialog.SelectedSortMode, dialog.SelectedAscending, dialog.SelectedDirectoriesFirst);
                AddLog($"[ソート] {pane.Title}パネルを {pane.SortDescription} で整列しました", "SORT");
                StatusMessage = $"{pane.Title}パネル: {pane.SortDescription}";
            }
            mw.FocusActiveList();
        }
        // メイン画面がなければ次の並べ替えの種類へ切り替える。
        else
        {
            pane.CycleSort();
            AddLog($"[ソート] {pane.Title}パネルを {pane.SortDescription} で整列しました", "SORT");
            StatusMessage = $"{pane.Title}パネル: {pane.SortDescription}";
        }

        return Task.CompletedTask;
    }

}
