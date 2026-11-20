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
/// メイン画面のビューモデルのうち、ビューア・拡張子実行・関連付け実行・サウンドバーを受け持つ部分。
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>
    /// 選択中のファイルをビューアで開く。引数で使うビューアを指定できる。
    /// アーカイブ内のファイルは一時ファイルへ展開してから開く。
    /// </summary>
    public async Task OpenViewerAsync(
        bool forceGraphicViewer = false,
        bool forceTextViewer = false,
        bool forceVgmPlayer = false,
        bool forcePdfViewer = false,
        bool forceHexViewer = false,
        bool forceVideoViewer = false)
    {
        // 再生中の曲を確認する。
        var musicService = MusicPlaybackService.Instance;
        bool isMusicActive = musicService.IsPlaying || musicService.IsPaused;
        string? activeMusicPath = musicService.CurrentFilePath;

        // 音楽プレイヤー指定で再生中の曲があれば、選択に関係なくその曲の画面を開く。
        if (forceVgmPlayer && isMusicActive && !string.IsNullOrEmpty(activeMusicPath) && musicService.CanOpenFullViewer(activeMusicPath))
        {
            string origPath = ArchiveService.GetOriginalViewPath(activeMusicPath);
            AddLog($"[ビューアー起動] {Path.GetFileName(origPath)}", "VIEW");
            OpenViewerRequested?.Invoke(activeMusicPath, false, false, true, false, false, false);
            return;
        }

        // フォルダーなどファイル以外を選んでいるときは、再生中の曲を開くか案内を出す。
        var selected = ActivePane.SelectedItem;
        if (selected is null || selected.IsDirectory || selected.IsParent)
        {
            if (forceVgmPlayer && isMusicActive && activeMusicPath is string currentPath)
            {
                OpenViewerRequested?.Invoke(currentPath, false, false, true, false, false, false);
                return;
            }
            AddLog("ビューアー: 表示対象のファイルを選択してください", "VIEW");
            StatusMessage = "ビューアー: 通常ファイルを選択してください";
            return;
        }

        string targetPath = selected.FullPath;
        string displayName = selected.Name;

        // 選択中のファイルが再生中の曲なら、展開し直さず再生中のパスで開く。
        string? selectedOriginal = ArchiveService.GetOriginalViewPath(selected.FullPath);
        string? activeOriginal = activeMusicPath != null ? ArchiveService.GetOriginalViewPath(activeMusicPath) : null;
        bool isCurrentlyPlayingSelected = isMusicActive &&
            !string.IsNullOrEmpty(activeMusicPath) &&
            string.Equals(selectedOriginal, activeOriginal, StringComparison.OrdinalIgnoreCase);

        if (isCurrentlyPlayingSelected)
        {
            targetPath = activeMusicPath!;
            AddLog($"[ビューアー起動] {displayName}", "VIEW");
            OpenViewerRequested?.Invoke(targetPath, forceGraphicViewer, forceTextViewer, forceVgmPlayer, forcePdfViewer, forceHexViewer, forceVideoViewer);
            return;
        }

        await RunWithLoadingAsync($"Loading: {displayName}", async () =>
        {
            // アーカイブ内のファイルは一時ファイルへ展開する。通常ファイルは存在を確かめる。
            bool isArchiveEntry = await Task.Run(() => ArchiveService.IsArchiveEntry(selected.FullPath));
            if (isArchiveEntry)
            {
                AddLog($"[アーカイブ展開中...] {selected.Name}", "VIEW");
                StatusMessage = $"アーカイブ内ファイルを展開中: {selected.Name}";
                var tempPath = await ArchiveService.ExtractEntryToTempFileAsync(selected.FullPath);
                if (string.IsNullOrEmpty(tempPath) || !File.Exists(tempPath))
                {
                    AddLog($"ビューアー: アーカイブ内ファイルの展開に失敗しました: {selected.Name}", "ERR");
                    StatusMessage = $"展開できませんでした: {selected.Name}";
                    return;
                }
                targetPath = tempPath;
            }
            else if (!await Task.Run(() => File.Exists(selected.FullPath)))
            {
                AddLog($"ビューアー: ファイルが見つかりません: {selected.FullPath}", "WARN");
                StatusMessage = $"ファイルが存在しません: {selected.Name}";
                return;
            }

            // ビューアを開くよう画面へ依頼する。
            AddLog($"[ビューアー起動] {displayName}", "VIEW");
            OpenViewerRequested?.Invoke(targetPath, forceGraphicViewer, forceTextViewer, forceVgmPlayer, forcePdfViewer, forceHexViewer, forceVideoViewer);
        });
    }

    /// <summary>
    /// テキストビューアから、操作中のパネルで前または次のファイルを選んで開き直す。
    /// </summary>
    public async Task OpenAdjacentTextFileAsync(string currentFilePath, int direction)
    {
        string originalPath = ArchiveService.GetOriginalViewPath(currentFilePath);
        var adjacent = ActivePane.FindAdjacentFile(originalPath, direction);
        if (adjacent is null)
        {
            StatusMessage = direction < 0 ? "前のファイルはありません" : "次のファイルはありません";
            return;
        }
        ActivePane.SelectedItem = adjacent;
        await OpenViewerAsync(forceTextViewer: true);
    }

    /// <summary>
    /// ビューアで表示したファイルを、操作中のパネルのファイル一覧で選ぶ。
    /// 一時ファイルで開いていても、元のアーカイブ内のパスや名前で探す。
    /// </summary>
    public bool SelectViewedFile(string viewedFilePath)
    {
        string originalPath = ArchiveService.GetOriginalViewPath(viewedFilePath);
        var match = ActivePane.Entries.FirstOrDefault(entry =>
            string.Equals(entry.FullPath, originalPath, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(entry.FullPath, viewedFilePath, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(entry.Name, Path.GetFileName(originalPath), StringComparison.OrdinalIgnoreCase));
        if (match is null) return false;
        ActivePane.SelectedItem = match;
        return true;
    }

    internal Action<System.Diagnostics.ProcessStartInfo> ExplorerLauncher { get; set; } = request => System.Diagnostics.Process.Start(request);

    /// <summary>
    /// 選択中のフォルダーをエクスプローラーで開く。
    /// </summary>
    private void OpenFolderInExplorer(FileEntry selected)
    {
        try
        {
            // アーカイブ内のフォルダーなど、実在しないフォルダーは開けない。
            if (!Directory.Exists(selected.FullPath))
                throw new IOException("エクスプローラーで開ける実フォルダーが見つかりません。");
            // Windowsフォルダーの explorer.exe にフルパスを渡して起動する。
            var request = new System.Diagnostics.ProcessStartInfo(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"))
                { UseShellExecute = false };
            request.ArgumentList.Add(Path.GetFullPath(selected.FullPath));
            ExplorerLauncher(request);
            AddLog($"エクスプローラーで開く: {selected.FullPath}", "ACT");
            StatusMessage = $"エクスプローラー: {selected.FullPath}";
        }
        catch (Exception ex)
        {
            AddLog($"エクスプローラー起動失敗: {selected.FullPath} ({ex.Message})", "ERR");
            StatusMessage = $"開けませんでした: {selected.FullPath}";
        }
    }

    /// <summary>
    /// 選択中の項目をEnterで実行する。フォルダーは移動、アーカイブは中を表示し、
    /// ファイルはINIの拡張子設定に従う。設定がなければ指定によりWindowsの関連付けで開く。
    /// </summary>
    private async Task ExecuteExtExecAsync(System.Windows.Input.ModifierKeys modifiers, bool launchWithWindowsIfUnmapped)
    {
        var selected = ActivePane.SelectedItem;
        if (selected is null) return;

        // 関連付け実行ではフォルダーをエクスプローラーで開く。
        if (launchWithWindowsIfUnmapped && (selected.IsDirectory || selected.IsParent))
        {
            OpenFolderInExplorer(selected);
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

        // アーカイブファイル（.zip、.lha、.7z など）はその中を表示する。読み込み前にログとLoading表示を出す。
        if (ArchiveService.IsSupportedArchiveExtension(Path.GetExtension(selected.FullPath)))
        {
            AddLog($"アーカイブ内表示: {selected.FullPath}", "ACT");
            await LoadPaneAsync(ActivePane, selected.FullPath);
            return;
        }

        // 拡張子と修飾キーに合う拡張子設定を探し、あればそのコマンドを実行する。
        string ext = Path.GetExtension(selected.FullPath).ToLowerInvariant();
        var binding = _settings.ExtBindings.FirstOrDefault(b =>
            string.Equals(b.Extension, ext, StringComparison.OrdinalIgnoreCase) && b.Modifiers == modifiers);

        if (binding is not null)
        {
            await ExecuteAsync(new ResolvedCommand(binding.Command, 0, modifiers));
            return;
        }

        // 設定がなく関連付け実行でもなければ、Shift+Enter を案内する。
        if (!launchWithWindowsIfUnmapped)
        {
            string extensionLabel = string.IsNullOrEmpty(ext) ? "(拡張子なし)" : ext;
            AddLog($"拡張子実行設定がありません: {extensionLabel} ({selected.Name})", "WARN");
            StatusMessage = $"未登録の拡張子です: {extensionLabel} (Shift+Enter: Windows関連付け)";
            return;
        }

        await ExecuteWindowsAssociationAsync(selected);
    }

    /// <summary>
    /// 選択中のファイルをWindowsの関連付けで開く。アーカイブ内のファイルは一時ファイルへ展開してから開く。
    /// </summary>
    private async Task ExecuteWindowsAssociationAsync(FileEntry selected)
    {
        await RunWithLoadingAsync($"Loading: {selected.Name}", async () =>
        {
            try
            {
                // アーカイブ内のファイルなら一時ファイルへ展開する。
                string launchPath = selected.FullPath;
                bool isArchiveEntry = await Task.Run(() => ArchiveService.IsArchiveEntry(selected.FullPath));
                if (isArchiveEntry)
                {
                    AddLog($"[アーカイブ展開中...] {selected.Name}", "ACT");
                    var tempPath = await ArchiveService.ExtractEntryToTempFileAsync(selected.FullPath);
                    if (string.IsNullOrEmpty(tempPath) || !File.Exists(tempPath))
                    {
                        AddLog($"アーカイブ内ファイルの展開に失敗しました: {selected.Name}", "ERR");
                        StatusMessage = $"展開できませんでした: {selected.Name}";
                        return;
                    }
                    launchPath = tempPath;
                }

                // シェル経由で関連付けられたアプリを起動する。
                AddLog($"関連付け実行: {selected.Name}", "ACT");
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(launchPath) { UseShellExecute = true });
                StatusMessage = $"実行: {selected.Name}";
            }
            catch (Exception ex)
            {
                AddLog($"実行失敗: {selected.Name} ({ex.Message})", "ERR");
                StatusMessage = $"実行できませんでした: {selected.Name}";
            }
        });
    }

    /// <summary>
    /// 選択中のファイルをサウンドバーで再生する。ファイル以外を選んでいれば再生中の曲を一時停止する。
    /// </summary>
    private async Task PlayMiniPlayerAsync()
    {
        // ファイル以外を選んでいるときは、再生中なら一時停止だけ行う。
        var selected = ActivePane.SelectedItem;
        if (selected is null || selected.IsDirectory || selected.IsParent)
        {
            if (MusicPlaybackService.Instance.IsPlaying)
            {
                MusicPlaybackService.Instance.TogglePause();
            }
            return;
        }

        string playPath = selected.FullPath;
        string displayName = selected.Name;

        await RunWithLoadingAsync($"Loading: {displayName}", async () =>
        {
            // アーカイブ内のファイルなら一時ファイルへ展開する。
            bool isArchiveEntry = await Task.Run(() => ArchiveService.IsArchiveEntry(selected.FullPath));
            if (isArchiveEntry)
            {
                AddLog($"[アーカイブ展開中...] {selected.Name}", "ACT");
                StatusMessage = $"アーカイブ内ファイルを展開中: {selected.Name}";
                var tempPath = await ArchiveService.ExtractEntryToTempFileAsync(selected.FullPath);
                if (string.IsNullOrEmpty(tempPath) || !File.Exists(tempPath))
                {
                    AddLog($"アーカイブ内ファイルの展開に失敗しました: {selected.Name}", "ERR");
                    StatusMessage = $"展開できませんでした: {selected.Name}";
                    return;
                }
                playPath = tempPath;
            }

            // 再生を始め、結果をログへ残す。音声出力デバイスがなく実機だけで鳴らす場合もその旨を残す。
            AddLog($"[サウンドバー再生] {displayName}", "ACT");
            if (MusicPlaybackService.Instance.Play(playPath))
            {
                StatusMessage = $"再生: {displayName}";
                if (Services.Vgm.VgmPlayerService.Instance.IsHardwareOnly)
                    AddLog($"音声出力デバイスがないため、実機（NDSIF）だけで再生します: {displayName}", "INFO");
            }
            else if (MusicPlaybackService.Instance.LastError is { } reason)
            {
                AddLog($"{reason}: {displayName}", "ERR");
                StatusMessage = reason;
            }
            else
            {
                AddLog($"再生失敗: {displayName}", "ERR");
                StatusMessage = $"再生できませんでした: {displayName}";
            }
        });
    }

    public Task RestoreMusicBrowsePathAsync(string path, string? selectedPath) => LoadPaneAsync(ActivePane, path, selectedPath);

    /// <summary>
    /// ビューアを表示中なら閉じるよう画面へ依頼する。
    /// </summary>
    public void CloseViewer()
    {
        if (IsViewerActive)
        {
            IsViewerActive = false;
            CloseViewerRequested?.Invoke();
        }
    }}
