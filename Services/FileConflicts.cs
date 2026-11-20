using System.IO;
using MirrorFlux.Models;

namespace MirrorFlux.Services;

/// <summary>
/// コピー・移動・展開・アーカイブ追加で共通の、同名ファイルがあるときの表示文と解決の手順。
/// </summary>
internal static class FileConflicts
{
    /// <summary>解決した書き込み先。Overwrite は既存のファイルを置き換えることを承認済みか。</summary>
    internal readonly record struct Target(string Path, bool Overwrite);

    /// <summary>同名ファイルの確認ダイアログに出す、サイズと更新日時の比較文を作る。</summary>
    internal static (string Size, string Time) CompareSizeAndTime(long sourceSize, DateTime sourceTime, long destinationSize, DateTime destinationTime)
    {
        // サイズの大小を文にする。
        string size = sourceSize == destinationSize
            ? $"同一サイズ ({FileEntry.FormatFileSize(sourceSize)})"
            : sourceSize > destinationSize
                ? $"元が大 ({FileEntry.FormatFileSize(sourceSize)} > {FileEntry.FormatFileSize(destinationSize)})"
                : $"元が小 ({FileEntry.FormatFileSize(sourceSize)} < {FileEntry.FormatFileSize(destinationSize)})";

        // 更新日時の新旧を文にする。2秒未満の差は同じ日時とみなす。
        var diff = sourceTime - destinationTime;
        string time = Math.Abs(diff.TotalSeconds) < 2
            ? $"同一日時 ({sourceTime:yyyy-MM-dd HH:mm:ss})"
            : diff.TotalSeconds > 0
                ? $"コピー元が新しい (+{FormatTimeDiff(diff)})"
                : $"コピー先が新しい (+{FormatTimeDiff(-diff)})";
        return (size, time);
    }

    /// <summary>
    /// 内容を読み比べられない同名ファイル（アーカイブ内の項目）の表示情報を作る。サイズ0同士だけを完全一致とする。
    /// </summary>
    internal static ConflictInfo Uncompared(string sourceVirtualPath, long sourceSize, DateTime sourceTime,
        FileInfo destination, long destinationSize, DateTime destinationTime)
    {
        var (size, time) = CompareSizeAndTime(sourceSize, sourceTime, destinationSize, destinationTime);
        bool identical = sourceSize == destinationSize && sourceSize == 0;
        string status = sourceSize != destinationSize ? "サイズ不一致" : identical ? "完全一致 (空ファイル)" : "内容相違の可能性あり";
        return new ConflictInfo(new FileInfo(sourceVirtualPath), destination, status, size, time, identical,
            CustomSourceName: Path.GetFileName(sourceVirtualPath), CustomSourceLength: sourceSize);
    }

    /// <summary>
    /// targetPath に同名ファイルがある間、利用者の選択に従って書き込み先を決める。
    /// 中止は OperationCanceledException、スキップと「更新」で新しくないときはログを出して null を返す。
    /// 別名・自動連番で決めた名前も改めて確認する。
    /// </summary>
    internal static async Task<Target?> ResolveTargetAsync(string sourceDisplayPath, string targetPath, DateTime sourceTime,
        Func<FileInfo, ConflictInfo> describe, ConflictSession context,
        Func<ConflictInfo, Task<ConflictResolution>> conflictResolver, Action<string, string> logAction)
    {
        while (File.Exists(targetPath))
        {
            // 選択に応じて中止・スキップ・名前の付け直しを行う。
            var destination = new FileInfo(targetPath);
            ConflictResolution resolution = await context.ResolveAsync(() => describe(destination), conflictResolver);
            switch (resolution.Action)
            {
                case ConflictAction.Cancel:
                    throw new OperationCanceledException();
                case ConflictAction.Skip:
                    logAction($"スキップ: {sourceDisplayPath} -> {targetPath}", "SKIP");
                    return null;
                case ConflictAction.Update when sourceTime <= destination.LastWriteTime:
                    logAction($"スキップ (更新なし): {sourceDisplayPath} -> {targetPath}", "SKIP");
                    return null;
                case ConflictAction.Rename:
                    targetPath = FileOperationPaths.RenameTarget(targetPath, resolution.NewName);
                    continue;
                case ConflictAction.AutoRename:
                    targetPath = FileOperationPaths.GenerateAutoNumberedPath(targetPath);
                    continue;
            }
            // 上書き、または元の方が新しい「更新」。既存のファイルを置き換える。
            return new Target(targetPath, true);
        }
        return new Target(targetPath, false);
    }

    /// <summary>
    /// 時間差を「3日」「2時間」のように最も大きい単位で表す。
    /// </summary>
    internal static string FormatTimeDiff(TimeSpan span)
    {
        if (span.TotalDays >= 1) return $"{(int)span.TotalDays}日";
        if (span.TotalHours >= 1) return $"{(int)span.TotalHours}時間";
        if (span.TotalMinutes >= 1) return $"{(int)span.TotalMinutes}分";
        return $"{(int)span.TotalSeconds}秒";
    }
}
