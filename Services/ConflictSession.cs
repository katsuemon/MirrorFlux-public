using MirrorFlux.Models;

namespace MirrorFlux.Services;

/// <summary>
/// 同名ファイルの確認結果を1回の操作の中で共有する。「すべてに適用」の選択はその回のコピー・移動・展開だけに効く。
/// </summary>
internal sealed class ConflictSession
{
    public ConflictResolution? PersistentResolution { get; set; }

    /// <summary>
    /// 「すべてに適用」の選択があればそれを返し、なければ確認ダイアログで尋ねる。別名は1件ごとに名前が違うため共有しない。
    /// </summary>
    public async Task<ConflictResolution> ResolveAsync(
        Func<ConflictInfo> createInfo,
        Func<ConflictInfo, Task<ConflictResolution>> resolver)
    {
        if (PersistentResolution is not null) return PersistentResolution;
        ConflictResolution resolution = await resolver(createInfo());
        if (resolution.ApplyToAll && resolution.Action != ConflictAction.Rename)
            PersistentResolution = resolution;
        return resolution;
    }
}
