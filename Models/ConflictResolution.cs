using System.IO;

namespace MirrorFlux.Models;

/// <summary>
/// コピー・移動先に同名ファイルがあるときの処理方法。各値の後ろは上書き確認ダイアログでのキー。
/// </summary>
public enum ConflictAction
{
    Overwrite,      // O：強制上書き。
    Update,         // U：コピー元が新しいときだけ上書きする。
    Rename,         // R：名前を変えてコピーする。
    Skip,           // N：コピーしない。
    AutoRename,     // A：自動で連番（名前_1 など）を付けてコピーする。
    Cancel          // C：処理全体を中止する。
}

/// <summary>
/// 上書き確認ダイアログで選ばれた処理。以降すべてに適用するか、名前変更後の名前も持つ。
/// </summary>
public sealed record ConflictResolution(
    ConflictAction Action,
    bool ApplyToAll,
    string? NewName = null
);

/// <summary>
/// 上書き確認ダイアログに出す競合の情報。アーカイブ内の項目など、コピー元の名前とサイズを別に指定できる。
/// </summary>
public sealed record ConflictInfo(
    FileInfo SourceFile,
    FileInfo DestinationFile,
    string StatusMessage,
    string SizeMessage,
    string TimestampMessage,
    bool IsContentIdentical,
    string? CustomSourceName = null,
    long? CustomSourceLength = null
)
{
    public string DisplaySourceName => CustomSourceName ?? SourceFile.Name;
    public long DisplaySourceLength => CustomSourceLength ?? (SourceFile.Exists ? SourceFile.Length : 0);
}
