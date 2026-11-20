namespace MirrorFlux.Models;

/// <summary>
/// コピー・移動の結果の分類。すべて完了、一部完了、すべてスキップ、失敗。
/// </summary>
public enum FileTransferStatus { Complete, Partial, Skipped, Failed }

/// <summary>
/// コピー・移動の結果。成功・スキップ・エラーの件数と、すべて完了した最上位の項目のパスを持つ。
/// </summary>
public sealed record FileCopyResult(int CopiedCount, int SkippedCount, int ErrorCount, List<string> SuccessfulPaths)
{
    public FileTransferStatus Status => ErrorCount == 0 && SkippedCount == 0 ? FileTransferStatus.Complete
        : CopiedCount > 0 ? FileTransferStatus.Partial : ErrorCount > 0 ? FileTransferStatus.Failed : FileTransferStatus.Skipped;

    /// <summary>
    /// 成功件数・スキップ件数・完了した項目のパスの3つに分解する。エラー件数を使わない呼び出し側のために残している。
    /// </summary>
    public void Deconstruct(out int copied, out int skipped, out List<string> successfulPaths)
        => (copied, skipped, successfulPaths) = (CopiedCount, SkippedCount, SuccessfulPaths);
}
