using System.Collections.ObjectModel;
using System.IO;
using MirrorFlux.Models;
using MirrorFlux.Services;

namespace MirrorFlux.ViewModels;

/// <summary>
/// ファイル一覧の並べ替えの種類。
/// </summary>
public enum PaneSortMode
{
    Name,
    Size,
    Date,
    Type
}

/// <summary>
/// 左右それぞれのパネルの状態を持つビューモデル。
/// ファイル一覧、選択、マーク、絞り込み、並べ替え、ドライブ情報の表示を受け持つ。
/// </summary>
public sealed class FilePaneViewModel : ObservableObject
{
    private string _currentPath = @"C:\";
    private FileEntry? _selectedItem;
    private bool _isActive;
    private PaneSortMode _sortMode = PaneSortMode.Name;
    private bool _sortAscending = true;
    private bool _sortDirectoriesFirst = true;
    private string _searchText = "";

    private readonly Dictionary<string, (string Path, string? SelectedEntry)> _driveLocations = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// パネルの名前と最初に開くフォルダーを受け取り、そのドライブの位置を覚えておく。
    /// </summary>
    public FilePaneViewModel(string title, string initialPath = @"C:\")
    {
        Title = title;
        _currentPath = initialPath;
        var key = GetDriveKey(initialPath);
        if (!string.IsNullOrEmpty(key))
        {
            _driveLocations[key] = (initialPath, null);
        }
    }

    /// <summary>
    /// パスからドライブごとの位置を覚えるためのキー（例: C:、UNCの共有名）を作る。
    /// </summary>
    public static string GetDriveKey(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        try
        {
            // ルートの末尾の区切り文字を外し、大文字にそろえる。
            var root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root)) return string.Empty;
            return root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToUpperInvariant();
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// フォルダー、またはアーカイブ内のパスなら元のアーカイブファイルが存在するかを調べる。
    /// </summary>
    public static bool PathExists(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            if (ArchiveService.TrySplitArchivePath(path, out var archiveFilePath, out _))
                return File.Exists(archiveFilePath);
            return Directory.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 現在のフォルダーと選択中の項目を、そのドライブの最後の位置として覚える。
    /// </summary>
    public void RememberCurrentDriveState()
    {
        if (string.IsNullOrWhiteSpace(_currentPath)) return;
        var key = GetDriveKey(_currentPath);
        if (!string.IsNullOrEmpty(key))
        {
            _driveLocations[key] = (_currentPath, SelectedItem?.FullPath ?? SelectedItem?.Name);
        }
    }

    /// <summary>
    /// ドライブごとに覚えたフォルダーを返す。すでに存在しなければ null を返す。
    /// </summary>
    public string? GetRememberedPathForDrive(string? driveRootOrPath)
    {
        var key = GetDriveKey(driveRootOrPath);
        if (!string.IsNullOrEmpty(key) && _driveLocations.TryGetValue(key, out var loc))
        {
            if (PathExists(loc.Path))
            {
                return loc.Path;
            }
        }
        return null;
    }

    /// <summary>
    /// ドライブごとに覚えた選択中の項目を返す。
    /// </summary>
    public string? GetRememberedSelectionForDrive(string? driveRootOrPath)
    {
        var key = GetDriveKey(driveRootOrPath);
        if (!string.IsNullOrEmpty(key) && _driveLocations.TryGetValue(key, out var loc))
        {
            return loc.SelectedEntry;
        }
        return null;
    }

    public string Title { get; }
    public ObservableCollection<FileEntry> Entries { get; } = [];
    public ObservableCollection<FileEntry> VisibleEntries { get; } = [];
    public string SearchText => _searchText;
    public bool HasSearchText => _searchText.Length != 0;
    public string SearchBadgeText => HasSearchText ? $"[{_searchText}]" : "";

    /// <summary>
    /// 名前に文字列を含む項目だけを表示する絞り込みを適用し、件数と選択中の名前を返す。
    /// </summary>
    public (int Count, string? FirstName) ApplySearch(string text)
    {
        // 絞り込み中は親フォルダーの項目を表示しない。
        _searchText = text;
        var visible = Entries.Where(entry => _searchText.Length == 0 ||
            (!entry.IsParent && entry.Name.Contains(_searchText, StringComparison.CurrentCultureIgnoreCase))).ToList();
        VisibleEntries.Clear();
        foreach (var entry in visible) VisibleEntries.Add(entry);
        // 選択中の項目が見えなくなったら先頭を選ぶ。
        if (SelectedItem == null || !visible.Contains(SelectedItem))
            SelectedItem = visible.FirstOrDefault();
        // 表示に関わるプロパティをまとめて通知する。
        OnPropertyChanged(nameof(SearchText));
        OnPropertyChanged(nameof(HasSearchText));
        OnPropertyChanged(nameof(SearchBadgeText));
        OnPropertyChanged(nameof(EntrySummary));
        OnPropertyChanged(nameof(SelectedIndexDisplay));
        return (visible.Count, SelectedItem?.Name);
    }

    /// <summary>
    /// 表示中の項目を、親フォルダーを除いてすべてマークする。
    /// </summary>
    public void MarkAllVisible()
    {
        foreach (var entry in VisibleEntries.Cast<FileEntry>().Where(e => !e.IsParent))
            entry.IsMarked = true;
        NotifyMarkedChanged();
    }

    /// <summary>
    /// 表示中のフォルダー。変わったら空き容量の表示も更新する。
    /// </summary>
    public string CurrentPath
    {
        get => _currentPath;
        private set
        {
            if (SetProperty(ref _currentPath, value))
            {
                OnPropertyChanged(nameof(DriveFreeSpace));
            }
        }
    }

    public event Action? MarkedChanged;

    /// <summary>
    /// 選択中の項目。変わったらドライブごとに覚えた選択も更新する。
    /// </summary>
    public FileEntry? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (SetProperty(ref _selectedItem, value))
            {
                OnPropertyChanged(nameof(SelectedIndexDisplay));
                // 覚えている位置が今のフォルダーのときだけ、選択を書き換える。
                if (!string.IsNullOrWhiteSpace(_currentPath))
                {
                    var key = GetDriveKey(_currentPath);
                    if (!string.IsNullOrEmpty(key) && _driveLocations.TryGetValue(key, out var loc) && string.Equals(loc.Path, _currentPath, StringComparison.OrdinalIgnoreCase))
                    {
                        _driveLocations[key] = (_currentPath, value?.FullPath ?? value?.Name);
                    }
                }
            }
        }
    }

    public bool IsActive
    {
        get => _isActive;
        set => SetProperty(ref _isActive, value);
    }

    /// <summary>
    /// 並べ替えの種類。変わったら並び順の表示を更新する。
    /// </summary>
    public PaneSortMode SortMode
    {
        get => _sortMode;
        set
        {
            if (SetProperty(ref _sortMode, value))
            {
                OnPropertyChanged(nameof(SortDescription));
                OnPropertyChanged(nameof(SortBadgeText));
            }
        }
    }

    /// <summary>
    /// 昇順かどうか。変わったら並び順の表示を更新する。
    /// </summary>
    public bool SortAscending
    {
        get => _sortAscending;
        set
        {
            if (SetProperty(ref _sortAscending, value))
            {
                OnPropertyChanged(nameof(SortDescription));
                OnPropertyChanged(nameof(SortBadgeText));
            }
        }
    }

    /// <summary>
    /// フォルダーを先に並べるかどうか。変わったら並び順の表示を更新する。
    /// </summary>
    public bool SortDirectoriesFirst
    {
        get => _sortDirectoriesFirst;
        set
        {
            if (SetProperty(ref _sortDirectoriesFirst, value))
            {
                OnPropertyChanged(nameof(SortDescription));
                OnPropertyChanged(nameof(SortBadgeText));
            }
        }
    }

    /// <summary>
    /// 一覧の下に出す並び順の説明（例: 名前順 (昇順)）。
    /// </summary>
    public string SortDescription
    {
        get
        {
            var modeName = SortMode switch
            {
                PaneSortMode.Name => "名前順",
                PaneSortMode.Size => "サイズ順",
                PaneSortMode.Date => "日時順",
                PaneSortMode.Type => "拡張子順",
                _ => "名前順"
            };
            var dirName = SortAscending ? "昇順" : "降順";
            var folderSuffix = SortDirectoriesFirst ? "" : "・フォルダ混在";
            return $"{modeName} ({dirName}{folderSuffix})";
        }
    }

    /// <summary>
    /// パネルの見出しに出す短い並び順の表示（例: [名前▲]）。
    /// </summary>
    public string SortBadgeText
    {
        get
        {
            var shortName = SortMode switch
            {
                PaneSortMode.Name => "名前",
                PaneSortMode.Size => "サイズ",
                PaneSortMode.Date => "日時",
                PaneSortMode.Type => "拡張子",
                _ => "名前"
            };
            var arrow = SortAscending ? "▲" : "▼";
            return $"[{shortName}{arrow}]";
        }
    }

    /// <summary>
    /// 並べ替えの種類ごとの既定の向き。サイズと日時は大きい・新しい順にする。
    /// </summary>
    public static bool DefaultAscendingFor(PaneSortMode mode) => mode switch
    {
        PaneSortMode.Size => false,
        PaneSortMode.Date => false,
        _ => true
    };

    /// <summary>
    /// INIの並べ替え設定（例: size_desc_mix）を種類・向き・フォルダー優先に分解する。
    /// </summary>
    public static (PaneSortMode Mode, bool Ascending, bool DirectoriesFirst) ParseSortSetting(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return (PaneSortMode.Name, true, true);
        }

        // 末尾のフォルダー混在・フォルダー優先の指定を読み取って取り除く。
        var normalized = raw.Trim().ToLowerInvariant();
        bool dirsFirst = true;
        if (normalized.EndsWith("_nodir", StringComparison.Ordinal) ||
            normalized.EndsWith(":nodir", StringComparison.Ordinal) ||
            normalized.EndsWith("_mix", StringComparison.Ordinal) ||
            normalized.EndsWith(":mix", StringComparison.Ordinal))
        {
            dirsFirst = false;
            normalized = normalized
                .Replace("_nodir", "")
                .Replace(":nodir", "")
                .Replace("_mix", "")
                .Replace(":mix", "");
        }
        else if (normalized.EndsWith("_dir", StringComparison.Ordinal) ||
                 normalized.EndsWith(":dir", StringComparison.Ordinal))
        {
            dirsFirst = true;
            normalized = normalized
                .Replace("_dir", "")
                .Replace(":dir", "");
        }

        // 昇順・降順の明示があれば読み取って取り除く。
        bool? explicitAsc = null;
        if (normalized.EndsWith("_asc", StringComparison.Ordinal) || normalized.EndsWith(":asc", StringComparison.Ordinal) || normalized.EndsWith("+"))
        {
            explicitAsc = true;
            normalized = normalized.TrimEnd('+').Replace("_asc", "").Replace(":asc", "");
        }
        else if (normalized.EndsWith("_desc", StringComparison.Ordinal) || normalized.EndsWith(":desc", StringComparison.Ordinal) || normalized.EndsWith("-"))
        {
            explicitAsc = false;
            normalized = normalized.TrimEnd('-').Replace("_desc", "").Replace(":desc", "");
        }

        // 残りを種類として解釈する。向きの指定がなければ種類ごとの既定を使う。
        var mode = normalized switch
        {
            "size" => PaneSortMode.Size,
            "date" or "time" => PaneSortMode.Date,
            "type" or "ext" or "extension" => PaneSortMode.Type,
            _ => PaneSortMode.Name
        };

        return (mode, explicitAsc ?? DefaultAscendingFor(mode), dirsFirst);
    }

    /// <summary>
    /// 現在の並べ替えをINIへ保存する形式の文字列にする。
    /// </summary>
    public string FormatSortSetting()
    {
        var modeStr = SortMode switch
        {
            PaneSortMode.Size => "size",
            PaneSortMode.Date => "date",
            PaneSortMode.Type => "type",
            _ => "name"
        };
        var dirStr = SortAscending ? "asc" : "desc";
        var folderStr = SortDirectoriesFirst ? "" : "_mix";
        return $"{modeStr}_{dirStr}{folderStr}";
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Auto)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceEx(
        string lpDirectoryName,
        out ulong lpFreeBytesAvailable,
        out ulong lpTotalNumberOfBytes,
        out ulong lpTotalNumberOfFreeBytes);

    /// <summary>
    /// パスのあるドライブの空き容量と全体の容量を取得する。
    /// </summary>
    private static bool TryGetDiskSpace(string path, out long freeBytes, out long totalBytes)
    {
        freeBytes = 0;
        totalBytes = 0;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }
        // アーカイブ内では、そのアーカイブファイルがあるドライブの空き容量を示す。
        if (ArchiveService.IsInsideArchive(path))
        {
            if (!ArchiveService.TrySplitArchivePath(path, out var archiveFile, out _)) return false;
            string? parent = Path.GetDirectoryName(archiveFile);
            if (string.IsNullOrEmpty(parent) || ArchiveService.IsInsideArchive(parent)) return false;
            path = parent;
        }

        try
        {
            // まずAPIでフォルダー単位の空き容量を取る。UNCパスもここで取れる。
            if (OperatingSystem.IsWindows() && GetDiskFreeSpaceEx(path, out ulong free, out ulong total, out _))
            {
                freeBytes = (long)free;
                totalBytes = (long)total;
                return true;
            }

            // 取れなければドライブ情報から取る。UNCパスは対象外。
            var root = Path.GetPathRoot(path);
            if (!string.IsNullOrEmpty(root) && !FileOperationPaths.IsUncPath(root))
            {
                var drive = new DriveInfo(root);
                if (drive.IsReady)
                {
                    freeBytes = drive.AvailableFreeSpace;
                    totalBytes = drive.TotalSize;
                    return true;
                }
            }
        }
        catch
        {
        }
        return false;
    }

    /// <summary>
    /// ドライブの空き容量の表示。アーカイブ内ではアーカイブの情報を表示する。
    /// </summary>
    public string DriveFreeSpace
    {
        get
        {
            if (ArchiveService.IsInsideArchive(CurrentPath))
            {
                return ArchiveService.GetArchiveInfoText(CurrentPath);
            }

            // ドライブ名かUNCの共有名に、空き容量と全体の容量をGB単位で添える。
            try
            {
                if (TryGetDiskSpace(CurrentPath, out var freeBytes, out var totalBytes))
                {
                    var freeGb = freeBytes / (1024.0 * 1024 * 1024);
                    var totalGb = totalBytes / (1024.0 * 1024 * 1024);
                    var root = Path.GetPathRoot(CurrentPath) ?? "";
                    string label = FileOperationPaths.IsUncPath(root)
                        ? root.TrimEnd('\\', '/')
                        : (root.Length > 0 ? $"{root[0]}:" : "");
                    return $"{label} {freeGb:F1}GB 空き / {totalGb:F1}GB";
                }
            }
            catch
            {
            }
            return string.Empty;
        }
    }

    /// <summary>
    /// ボリューム名の表示。アーカイブ内ではアーカイブの種類、UNCパスでは共有名を返す。
    /// </summary>
    public string VolumeLabel
    {
        get
        {
            if (ArchiveService.IsInsideArchive(CurrentPath))
            {
                return ArchiveService.GetArchiveTag(CurrentPath);
            }

            try
            {
                var root = Path.GetPathRoot(CurrentPath);
                if (!string.IsNullOrEmpty(root))
                {
                    // UNCパスはサーバー名を除いた共有名を返す。
                    if (FileOperationPaths.IsUncPath(root))
                    {
                        var trimmed = root.Trim('\\', '/');
                        var slashIdx = trimmed.IndexOfAny(['\\', '/']);
                        if (slashIdx >= 0 && slashIdx < trimmed.Length - 1)
                        {
                            return trimmed.Substring(slashIdx + 1);
                        }
                        return trimmed;
                    }

                    // ローカルドライブはボリュームラベルを返す。
                    var drive = new DriveInfo(root);
                    if (drive.IsReady && !string.IsNullOrWhiteSpace(drive.VolumeLabel))
                    {
                        return drive.VolumeLabel;
                    }
                }
            }
            catch
            {
            }
            return "System";
        }
    }

    /// <summary>
    /// 見出しに出すパスの表示（例: C:\Work\*）。
    /// </summary>
    public string DisplayPathPattern
    {
        get
        {
            var p = CurrentPath.TrimEnd('\\');
            return string.IsNullOrEmpty(p) ? @"C:\*" : $"{p}\\*";
        }
    }

    public int FilesCount => Entries.Count(e => !e.IsDirectory);

    public long TotalUsedBytes => Entries.Where(e => !e.IsDirectory).Sum(e => e.Length);

    public static (string Number, string Unit) FormatCapacityParts(long bytes) => FileEntry.FormatSizeParts(bytes);

    public string UsedCapacityValue => FormatCapacityParts(TotalUsedBytes).Number;
    public string UsedCapacityUnit => FormatCapacityParts(TotalUsedBytes).Unit;

    /// <summary>
    /// 空き容量の数値と、単位に残りの割合を添えた表示。
    /// </summary>
    public (string Value, string Unit) FreeCapacityInfo
    {
        get
        {
            try
            {
                if (TryGetDiskSpace(CurrentPath, out var freeBytes, out var totalBytes) && totalBytes > 0)
                {
                    var (num, unit) = FormatCapacityParts(freeBytes);
                    var pct = (int)((freeBytes * 100) / totalBytes);
                    return (num, $"{unit}({pct}%)");
                }
            }
            catch
            {
            }
            // 取得できないときは実在しない数値を出さない。
            return ("-", "");
        }
    }

    public string FreeCapacityValue => FreeCapacityInfo.Value;
    public string FreeCapacityUnit => FreeCapacityInfo.Unit;

    /// <summary>
    /// ドライブの種類を示すタグ（[SCSI]、[USB]、[NET]、[CD]、アーカイブ内は [ARC]）。
    /// </summary>
    public string DeviceTypeTag
    {
        get
        {
            if (ArchiveService.IsInsideArchive(CurrentPath))
            {
                return "[ARC]";
            }

            try
            {
                var root = Path.GetPathRoot(CurrentPath);
                if (!string.IsNullOrEmpty(root))
                {
                    // UNCパスはネットワークとして扱う。
                    if (FileOperationPaths.IsUncPath(root))
                    {
                        return "[NET]";
                    }

                    // それ以外はドライブの種類で決める。
                    var drive = new DriveInfo(root);
                    return drive.DriveType switch
                    {
                        DriveType.Fixed => "[SCSI]",
                        DriveType.Removable => "[USB]",
                        DriveType.Network => "[NET]",
                        DriveType.CDRom => "[CD]",
                        _ => "[SCSI]"
                    };
                }
            }
            catch
            {
            }
            return "[SCSI]";
        }
    }

    /// <summary>
    /// ファイル一覧の下に出す件数・マーク・並び順・絞り込みのまとめ。
    /// </summary>
    public string EntrySummary
    {
        get
        {
            // 件数とマークしたファイルの合計サイズを数える。
            var dirs = Entries.Count(e => e.IsDirectory && !e.IsParent);
            var files = Entries.Count(e => !e.IsDirectory);
            var marked = Entries.Count(e => e.IsMarked);
            var markedBytes = Entries.Where(e => e.IsMarked && !e.IsDirectory).Sum(e => e.Length);
            var markedSize = markedBytes > 1024 * 1024
                ? $"{(markedBytes / (1024.0 * 1024)):F1}MB"
                : $"{(markedBytes / 1024.0):F1}KB";

            // 絞り込み中ならその文字列も添える。
            string filter = _searchText.Length == 0 ? "" : $" | 絞り込み: {_searchText}";
            return $"{files} Files, {dirs} Dirs | {marked} Marked ({markedSize}) | [{SortDescription}]{filter}";
        }
    }

    public bool ShowHidden { get; set; }

    /// <summary>
    /// フォルダーまたはアーカイブ内を読み込んでファイル一覧を作り直す。
    /// 同じフォルダーの再読み込みでは、指定によりマークと絞り込みを引き継ぐ。
    /// </summary>
    public async Task LoadAsync(string path, string? restoreSelection = null, CancellationToken cancellationToken = default, bool preserveMarks = false)
    {
        // 同じフォルダーを読み直すときだけ、マークとフォルダーのサイズを控えておく。
        var fullPath = Path.GetFullPath(path);
        var currentPathSnapshot = CurrentPath;
        var marks = preserveMarks && string.Equals(fullPath, CurrentPath, StringComparison.OrdinalIgnoreCase)
            ? Entries.Where(e => e.IsMarked).ToDictionary(e => e.FullPath, e => e.Length, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var selectedPathSnapshot = SelectedItem?.FullPath;
        var showHiddenSnapshot = ShowHidden;
        // 読み込みは別スレッドで行う。親フォルダーへ戻るときは元いたフォルダーを選ぶ。
        var (entries, previousSelection) = await Task.Run(() =>
        {
            var prevSel = restoreSelection ?? selectedPathSnapshot;
            if (restoreSelection == null
                && !string.IsNullOrEmpty(currentPathSnapshot)
                && !string.Equals(fullPath, currentPathSnapshot, StringComparison.OrdinalIgnoreCase)
                && string.Equals(fullPath.TrimEnd(Path.DirectorySeparatorChar),
                    ArchiveService.GetParentPath(currentPathSnapshot).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            {
                prevSel = currentPathSnapshot.TrimEnd(Path.DirectorySeparatorChar);
            }
            var readEntries = DirectoryReader.Read(fullPath, showHiddenSnapshot);
            return (readEntries, prevSel);
        }, cancellationToken);
        // 控えたマークとフォルダーのサイズを新しい項目へ戻し、並べ替える。
        foreach (var entry in entries)
            if (marks.TryGetValue(entry.FullPath, out long length))
            {
                entry.IsMarked = true;
                if (entry.IsDirectory) entry.Length = length;
            }
        var sortedEntries = SortEntriesList(entries);

        // ファイル一覧を入れ替える。
        Entries.Clear();
        foreach (var entry in sortedEntries)
        {
            Entries.Add(entry);
        }

        // 別のフォルダーへ移ったら絞り込みを解除する。
        CurrentPath = fullPath;
        ApplySearch(string.Equals(currentPathSnapshot, fullPath, StringComparison.OrdinalIgnoreCase)
            ? _searchText : "");

        // 前の選択を復元する。見つからないか絞り込みで見えなければ先頭を選ぶ。
        SelectedItem = Entries.FirstOrDefault(entry =>
            string.Equals(entry.FullPath, previousSelection, StringComparison.OrdinalIgnoreCase)
            || string.Equals(entry.Name, previousSelection, StringComparison.OrdinalIgnoreCase))
            ?? VisibleEntries.Cast<FileEntry>().FirstOrDefault();
        if (_searchText.Length != 0 && SelectedItem != null &&
            !VisibleEntries.Cast<FileEntry>().Contains(SelectedItem))
            SelectedItem = VisibleEntries.Cast<FileEntry>().FirstOrDefault();

        // このドライブの最後の位置として覚える。
        var driveKey = GetDriveKey(fullPath);
        if (!string.IsNullOrEmpty(driveKey))
        {
            _driveLocations[driveKey] = (fullPath, SelectedItem?.FullPath ?? SelectedItem?.Name);
        }

        // 一覧の周りの表示をまとめて更新する。
        OnPropertyChanged(nameof(EntrySummary));
        OnPropertyChanged(nameof(DriveFreeSpace));
        OnPropertyChanged(nameof(VolumeLabel));
        OnPropertyChanged(nameof(DisplayPathPattern));
        OnPropertyChanged(nameof(FilesCount));
        OnPropertyChanged(nameof(UsedCapacityValue));
        OnPropertyChanged(nameof(UsedCapacityUnit));
        OnPropertyChanged(nameof(FreeCapacityValue));
        OnPropertyChanged(nameof(FreeCapacityUnit));
        OnPropertyChanged(nameof(DeviceTypeTag));
        NotifyMarkedChanged();
    }

    /// <summary>
    /// 同じフォルダーを読み直し、サイズや日時の変化だけを反映する。
    /// 項目の並びが同じなら変わった項目だけを差し替え、マークと選択を保つ。
    /// </summary>
    public async Task RefreshMetadataAsync()
    {
        // アーカイブ内は対象外。読み込み中にフォルダーを移っていたら捨てる。
        string path = CurrentPath;
        if (ArchiveService.IsInsideArchive(path)) return;
        var entries = await Task.Run(() => DirectoryReader.Read(path, ShowHidden));
        if (!string.Equals(path, CurrentPath, StringComparison.OrdinalIgnoreCase)) return;
        // マークとフォルダーのサイズを新しい項目へ引き継ぐ。
        string? selected = SelectedItem?.FullPath;
        var marked = Entries.Where(e => e.IsMarked).ToDictionary(e => e.FullPath, StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
            if (marked.TryGetValue(entry.FullPath, out var previous))
            {
                entry.IsMarked = true;
                if (entry.IsDirectory) entry.Length = previous.Length;
            }
        // 並びが同じなら変わった項目だけ差し替え、違えば一覧ごと入れ替える。
        var sorted = SortEntriesList(entries);
        if (Entries.Count == sorted.Count && Entries.Select(e => e.FullPath).SequenceEqual(sorted.Select(e => e.FullPath), StringComparer.OrdinalIgnoreCase))
        {
            for (int i = 0; i < sorted.Count; i++)
                if (Entries[i].LastWriteTime != sorted[i].LastWriteTime || Entries[i].Length != sorted[i].Length) Entries[i] = sorted[i];
        }
        else { Entries.Clear(); foreach (var entry in sorted) Entries.Add(entry); }
        // 絞り込みと選択を戻して表示を更新する。
        ApplySearch(_searchText);
        SelectedItem = VisibleEntries.FirstOrDefault(e => string.Equals(e.FullPath, selected, StringComparison.OrdinalIgnoreCase))
            ?? VisibleEntries.FirstOrDefault();
        OnPropertyChanged(nameof(EntrySummary));
        OnPropertyChanged(nameof(FilesCount));
        NotifyMarkedChanged();
    }

    /// <summary>
    /// 表示中の項目の中で選択を指定数だけ動かす。wrap なら端で反対側へ回り込む。
    /// </summary>
    public void MoveSelection(int offset, bool wrap = false)
    {
        var visible = VisibleEntries.Cast<FileEntry>().ToList();
        if (visible.Count == 0) return;
        int index = SelectedItem == null ? 0 : visible.IndexOf(SelectedItem);
        if (index < 0) index = 0;
        index = wrap
            ? ((index + offset) % visible.Count + visible.Count) % visible.Count
            : Math.Clamp(index + offset, 0, visible.Count - 1);
        SelectedItem = visible[index];
    }

    /// <summary>
    /// 表示中の先頭または末尾の項目を選ぶ。
    /// </summary>
    public void SelectBoundary(bool last)
    {
        var visible = VisibleEntries.Cast<FileEntry>().ToList();
        if (visible.Count > 0) SelectedItem = last ? visible[^1] : visible[0];
    }

    /// <summary>
    /// 項目を取り除いた後に選ぶべき項目のパスを返す。
    /// 選択中の項目が残るならそれを、消えるなら手前、なければ後ろの残る項目を選ぶ。
    /// </summary>
    public string? SelectionPathAfterRemoving(IEnumerable<FileEntry> removedEntries)
    {
        // 選択中の項目が残るならそのまま。
        if (SelectedItem is null) return null;
        var removed = removedEntries.Select(entry => entry.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!removed.Contains(SelectedItem.FullPath)) return SelectedItem.FullPath;

        // 手前、次に後ろの順で残る項目を探す。
        int selectedIndex = Entries.IndexOf(SelectedItem);
        for (int index = selectedIndex - 1; index >= 0; index--)
            if (!removed.Contains(Entries[index].FullPath)) return Entries[index].FullPath;
        for (int index = selectedIndex + 1; index < Entries.Count; index++)
            if (!removed.Contains(Entries[index].FullPath)) return Entries[index].FullPath;
        return null;
    }

    /// <summary>
    /// ビューアで前後のファイルへ移るため、指定方向にある次のファイルを探す。端では反対側へ回り込む。
    /// </summary>
    public FileEntry? FindAdjacentFile(string currentPath, int direction)
    {
        // 今のファイルの位置を探す。見つからなければ選択中の項目を起点にする。
        if (direction == 0 || Entries.Count < 2) return null;
        int currentIndex = Entries.ToList().FindIndex(entry =>
            string.Equals(entry.FullPath, currentPath, StringComparison.OrdinalIgnoreCase));
        if (currentIndex < 0 && SelectedItem is not null) currentIndex = Entries.IndexOf(SelectedItem);
        if (currentIndex < 0) return null;

        // フォルダーを飛ばして次のファイルを探す。
        for (int step = 1; step < Entries.Count; step++)
        {
            int index = ((currentIndex + Math.Sign(direction) * step) % Entries.Count + Entries.Count) % Entries.Count;
            var entry = Entries[index];
            if (!entry.IsDirectory && !entry.IsParent) return entry;
        }
        return null;
    }

    public int MarkedDirsCount => Entries.Count(e => e.IsMarked && e.IsDirectory && !e.IsParent);
    public int MarkedFilesCount => Entries.Count(e => e.IsMarked && !e.IsDirectory);
    public long MarkedBytes => Entries.Where(e => e.IsMarked && !e.IsParent).Sum(e => e.Length);
    public string MarkedCapacityValue => FormatCapacityParts(MarkedBytes).Number;
    public string MarkedCapacityUnit => FormatCapacityParts(MarkedBytes).Unit;
    public int MarkedSectors => (int)(MarkedBytes / 256);
    public bool HasMarkedItems => Entries.Any(e => e.IsMarked);

    /// <summary>
    /// 選択中の項目が表示中の何番目かを3桁右寄せで表示する。
    /// </summary>
    public string SelectedIndexDisplay
    {
        get
        {
            var idx = SelectedItem is null ? 0 : VisibleEntries.Cast<FileEntry>().ToList().IndexOf(SelectedItem) + 1;
            return $"{idx,3}";
        }
    }

    /// <summary>
    /// 選択中の項目のマークを切り替え、カーソルを次の項目へ進める。
    /// </summary>
    public void ToggleSelectedMark()
    {
        if (SelectedItem is null)
        {
            return;
        }

        // 親フォルダーの項目はマークしない。
        if (!SelectedItem.IsParent)
        {
            SelectedItem.IsMarked = !SelectedItem.IsMarked;
            // サイズ未計算のフォルダーをマークしたら、別スレッドで合計サイズを数えてUIスレッドで表示を更新する。
            if (SelectedItem.IsDirectory && SelectedItem.IsMarked && SelectedItem.Length == 0)
            {
                var dirEntry = SelectedItem;
                _ = Task.Run(() =>
                {
                    try
                    {
                        var size = DirectoryReader.CalculateDirectorySize(dirEntry.FullPath);
                        dirEntry.Length = size;
                        System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
                        {
                            NotifyMarkedChanged();
                        });
                    }
                    catch
                    {
                    }
                });
            }
            NotifyMarkedChanged();
        }

        // 往年のファイラーと同じく、マークしたらカーソルを次の項目へ進める。
        MoveSelection(1);
    }

    /// <summary>
    /// マークに関わる表示をまとめて更新し、変更を知らせる。
    /// </summary>
    public void NotifyMarkedChanged()
    {
        OnPropertyChanged(nameof(MarkedDirsCount));
        OnPropertyChanged(nameof(MarkedFilesCount));
        OnPropertyChanged(nameof(MarkedBytes));
        OnPropertyChanged(nameof(MarkedCapacityValue));
        OnPropertyChanged(nameof(MarkedCapacityUnit));
        OnPropertyChanged(nameof(MarkedSectors));
        OnPropertyChanged(nameof(HasMarkedItems));
        OnPropertyChanged(nameof(EntrySummary));
        OnPropertyChanged(nameof(SelectedIndexDisplay));
        MarkedChanged?.Invoke();
    }

    /// <summary>
    /// 指定したパスの項目だけマークを外す。
    /// </summary>
    public void Unmark(IEnumerable<string> paths)
    {
        var set = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        foreach (var entry in Entries)
        {
            if (set.Contains(entry.FullPath))
            {
                entry.IsMarked = false;
            }
        }
        NotifyMarkedChanged();
    }

    /// <summary>
    /// すべての項目のマークを外す。
    /// </summary>
    public void UnmarkAll()
    {
        foreach (var entry in Entries)
        {
            entry.IsMarked = false;
        }
        NotifyMarkedChanged();
    }

    /// <summary>
    /// 並べ替えの種類と向きを設定し、ファイル一覧を並べ直す。
    /// </summary>
    public void SetSort(PaneSortMode mode, bool ascending, bool? directoriesFirst = null)
    {
        SortMode = mode;
        SortAscending = ascending;
        if (directoriesFirst.HasValue)
        {
            SortDirectoriesFirst = directoriesFirst.Value;
        }
        ApplySort();
        OnPropertyChanged(nameof(EntrySummary));
    }

    /// <summary>
    /// 並べ替えの種類を 名前→拡張子→サイズ→日時 の順に切り替える。
    /// </summary>
    public void CycleSort()
    {
        var nextMode = SortMode switch
        {
            PaneSortMode.Name => PaneSortMode.Type,
            PaneSortMode.Type => PaneSortMode.Size,
            PaneSortMode.Size => PaneSortMode.Date,
            PaneSortMode.Date => PaneSortMode.Name,
            _ => PaneSortMode.Name
        };
        SetSort(nextMode, DefaultAscendingFor(nextMode));
    }

    private List<FileEntry> SortEntriesList(IReadOnlyList<FileEntry> source) =>
        SortEntriesList(source, SortMode, SortAscending, SortDirectoriesFirst);

    /// <summary>
    /// 指定の並べ替えで項目を並べた新しいリストを返す。親フォルダーの項目は常に先頭に置く。
    /// </summary>
    internal static List<FileEntry> SortEntriesList(IReadOnlyList<FileEntry> source,
        PaneSortMode mode, bool ascending, bool directoriesFirst)
    {
        // 親フォルダーの項目を分け、フォルダー優先の順位と拡張子のキーを用意する。
        var parent = source.FirstOrDefault(e => e.IsParent);
        var others = source.Where(e => !e.IsParent).ToList();
        int DirRank(FileEntry e) => directoriesFirst ? (e.IsDirectory ? 0 : 1) : 0;
        string ExtKey(FileEntry e) => e.IsDirectory ? string.Empty : e.TypeText;

        // 種類と向きごとに並べる。同じ値どうしは名前の自然順で並べる。
        IEnumerable<FileEntry> sorted = (mode, ascending) switch
        {
            (PaneSortMode.Size, true) => others
                .OrderBy(DirRank)
                .ThenBy(e => e.Length)
                .ThenBy(e => e.Name, NaturalStringComparer.Instance),
            (PaneSortMode.Size, false) => others
                .OrderBy(DirRank)
                .ThenByDescending(e => e.Length)
                .ThenBy(e => e.Name, NaturalStringComparer.Instance),
            (PaneSortMode.Date, true) => others
                .OrderBy(DirRank)
                .ThenBy(e => e.LastWriteTime)
                .ThenBy(e => e.Name, NaturalStringComparer.Instance),
            (PaneSortMode.Date, false) => others
                .OrderBy(DirRank)
                .ThenByDescending(e => e.LastWriteTime)
                .ThenBy(e => e.Name, NaturalStringComparer.Instance),
            (PaneSortMode.Type, true) => others
                .OrderBy(DirRank)
                .ThenBy(ExtKey, StringComparer.OrdinalIgnoreCase)
                .ThenBy(e => e.Name, NaturalStringComparer.Instance),
            (PaneSortMode.Type, false) => others
                .OrderBy(DirRank)
                .ThenByDescending(ExtKey, StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(e => e.Name, NaturalStringComparer.Instance),
            (_, false) => others
                .OrderBy(DirRank)
                .ThenByDescending(e => e.Name, NaturalStringComparer.Instance),
            _ => others
                .OrderBy(DirRank)
                .ThenBy(e => e.Name, NaturalStringComparer.Instance)
        };

        // 親フォルダーの項目を先頭に戻す。
        var result = new List<FileEntry>(source.Count);
        if (parent is not null)
        {
            result.Add(parent);
        }
        result.AddRange(sorted);
        return result;
    }

    /// <summary>
    /// 現在の並べ替えでファイル一覧を並べ直し、絞り込みと選択を保つ。
    /// </summary>
    public void ApplySort()
    {
        if (Entries.Count <= 1)
        {
            return;
        }

        // 並べ直した項目で一覧を入れ替える。
        var selected = SelectedItem;
        var sorted = SortEntriesList(Entries);
        Entries.Clear();
        foreach (var item in sorted)
        {
            Entries.Add(item);
        }

        // 絞り込みをかけ直し、元の選択が見えていればそれを選ぶ。
        ApplySearch(_searchText);
        SelectedItem = selected is not null && VisibleEntries.Contains(selected)
            ? selected : VisibleEntries.FirstOrDefault();
    }
}
