using System.Globalization;
using System.IO;

namespace MirrorFlux.Services;

/// <summary>
/// 名前の変更ダイアログで指定された、新しい名前・属性・更新日時。
/// </summary>
public sealed record FileRenameOptions(string Name, bool ReadOnly, bool Hidden, bool System, DateTime? LastWriteTime);

/// <summary>
/// ファイル・フォルダーの名前・属性・更新日時を変更する。途中で失敗したら元に戻す。
/// </summary>
public static class FileRenameService
{
    /// <summary>
    /// 名前を大文字・小文字・先頭大文字に変換する。extensionOnly なら拡張子だけを変換する。
    /// </summary>
    public static string ConvertCase(string name, bool extensionOnly, string mode)
    {
        var extension = Path.GetExtension(name);
        string text = extensionOnly ? extension.TrimStart('.') : name;
        string converted = mode switch
        {
            "upper" => text.ToUpperInvariant(),
            "lower" => text.ToLowerInvariant(),
            "capital" => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(text.ToLowerInvariant()),
            _ => text
        };
        return extensionOnly ? Path.GetFileNameWithoutExtension(name) + (extension.Length > 0 ? "." + converted : "") : converted;
    }

    /// <summary>
    /// 新しい名前を検証し、変更後のパスを返す。Windows の予約名と 255 文字を超える名前は受け付けない。
    /// </summary>
    public static string ValidateName(string sourcePath, string name)
    {
        var target = FileOperationPaths.RenameTarget(sourcePath, name);
        var stem = name.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
            (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && "123456789¹²³".Contains(stem[3])))
            throw new IOException("Windowsで予約されている名前は使用できません。");
        if (name.Length > 255) throw new IOException("ファイル名は255文字以内で指定してください。");
        return target;
    }

    /// <summary>
    /// 名前・属性・更新日時をまとめて変更し、変更後のパスを返す。失敗したら名前・日時・属性を元に戻す。
    /// </summary>
    public static string Apply(string sourcePath, FileRenameOptions options)
    {
        // 対象と新しい名前を確かめる。リンクと、ほかの項目と重なる名前は受け付けない。
        sourcePath = Path.GetFullPath(sourcePath);
        bool isDirectory = Directory.Exists(sourcePath);
        if (!isDirectory && !File.Exists(sourcePath))
            throw new IOException("通常のファイルまたはフォルダーを選択してください。");
        FileOperationPaths.RejectLinks(sourcePath);
        string target = ValidateName(sourcePath, options.Name);
        bool samePath = string.Equals(sourcePath, target, StringComparison.OrdinalIgnoreCase);
        if (!samePath && (Directory.Exists(target) || File.Exists(target)))
            throw new IOException("同じ名前のファイルまたはフォルダーが既に存在します。");

        // 元に戻すため元の属性と日時を覚え、新しい属性を組み立てる。
        var attributes = File.GetAttributes(sourcePath);
        var time = isDirectory ? Directory.GetLastWriteTimeUtc(sourcePath) : File.GetLastWriteTimeUtc(sourcePath);
        const FileAttributes editable = FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System;
        var updated = attributes & ~(editable | FileAttributes.Normal);
        if (options.ReadOnly) updated |= FileAttributes.ReadOnly;
        if (options.Hidden) updated |= FileAttributes.Hidden;
        if (options.System) updated |= FileAttributes.System;
        if (updated == 0) updated = FileAttributes.Normal;
        string current = sourcePath;
        try
        {
            // 読み取り専用を外してから日時を変え、名前を変え、最後に属性を設定する。
            if (attributes.HasFlag(FileAttributes.ReadOnly))
                File.SetAttributes(sourcePath, attributes & ~FileAttributes.ReadOnly);
            if (options.LastWriteTime is DateTime modified)
            {
                if (isDirectory) Directory.SetLastWriteTime(sourcePath, modified);
                else File.SetLastWriteTime(sourcePath, modified);
            }
            if (!string.Equals(sourcePath, target, StringComparison.Ordinal))
            {
                // 上書きはしない。検証の後に同名の項目が作られた場合も同じ。
                if (isDirectory) Directory.Move(sourcePath, target);
                else File.Move(sourcePath, target, overwrite: false);
                current = target;
            }
            File.SetAttributes(current, updated);
            return current;
        }
        catch (Exception error)
        {
            // 名前・日時・属性を元に戻す。戻せなければその旨を含めて報告する。
            try
            {
                File.SetAttributes(current, File.GetAttributes(current) & ~FileAttributes.ReadOnly);
                if (current != sourcePath)
                {
                    if (isDirectory) Directory.Move(current, sourcePath);
                    else File.Move(current, sourcePath, overwrite: false);
                }
                current = sourcePath;
                if (isDirectory) Directory.SetLastWriteTimeUtc(current, time);
                else File.SetLastWriteTimeUtc(current, time);
                File.SetAttributes(current, attributes);
            }
            catch (Exception rollback)
            {
                throw new IOException($"変更に失敗しました。復元も完了できませんでした。対象: {current}\n{error.Message}\n{rollback.Message}", error);
            }
            throw new IOException($"変更できませんでした: {error.Message}", error);
        }
    }
}
