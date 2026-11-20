namespace MirrorFlux.Services;

public static partial class ArchiveService
{
    /// <summary>
    /// すべての中身を処理し終えた選択元のパスを返す。マークは選択した元の項目を表し、フォルダー内の個々のファイルではないため、元の項目単位で判定する。
    /// </summary>
    private static List<string> GetCompletedArchiveRoots(IEnumerable<FileItemToAdd> required,
        IEnumerable<FileItemToAdd> copied, IEnumerable<string>? directoryRoots = null)
    {
        // 1件でも未完了の中身がある選択元を除き、残りを完了として返す。
        var completed = copied.ToHashSet();
        var groups = required.GroupBy(item => item.SourceEntryFullPath, StringComparer.OrdinalIgnoreCase).ToList();
        var incomplete = groups.Where(group => group.Any(item => !completed.Contains(item)))
            .Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return groups.Select(group => group.Key).Concat(directoryRoots ?? [])
            .Where(path => !incomplete.Contains(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
