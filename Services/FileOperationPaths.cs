using System.IO;

namespace MirrorFlux.Services;

/// <summary>
/// コピー・移動・展開で使うパスの検証と名前作り。リンクの拒否、自分の中への転送の防止、展開先の外への書き込み防止を担う。
/// </summary>
public static class FileOperationPaths
{
    /// <summary>
    /// 別名に指定された名前を検証し、同じフォルダー内のパスにする。パスや不正な名前は受け付けない。
    /// </summary>
    public static string RenameTarget(string targetPath, string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            name.EndsWith('.') || name.EndsWith(' '))
            throw new IOException("有効なファイル名を指定してください（パスは指定できません）。");
        return Path.Combine(Path.GetDirectoryName(targetPath)!, name);
    }

    /// <summary>
    /// path が parent と同じか、その下にあるか。大文字・小文字は区別しない。
    /// </summary>
    public static bool IsSameOrDescendant(string path, string parent)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return full.Equals(root, StringComparison.OrdinalIgnoreCase) ||
            full.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>パスが解決できないときは false を返す <see cref="IsSameOrDescendant"/>。</summary>
    public static bool TryIsSameOrDescendant(string path, string parent)
    {
        try { return IsSameOrDescendant(path, parent); }
        catch { return false; }
    }

    /// <summary>追加しようとする項目がアーカイブ自身か、アーカイブを含むフォルダーかを判定する。</summary>
    public static bool ContainsArchive(string itemPath, bool isDirectory, string archivePath)
    {
        try
        {
            string item = Path.GetFullPath(itemPath), archive = Path.GetFullPath(archivePath);
            return item.Equals(archive, StringComparison.OrdinalIgnoreCase) || isDirectory && IsSameOrDescendant(archive, item);
        }
        catch { return false; }
    }

    public static bool IsUncPath(string path) => path.StartsWith(@"\\") || path.StartsWith("//");

    /// <summary>
    /// シンボリックリンク・ジャンクションならコピー・移動・展開を拒否する。
    /// クラウド同期のプレースホルダーも再解析ポイントだが、LinkTarget を持つのは本物のリンクだけなので、通常の同期ファイル・フォルダーは拒否しない。
    /// </summary>
    public static void RejectLinks(string path)
    {
        if (LinkTarget(path) != null)
            throw new IOException($"リンク自体のコピー・移動・展開はできません: {path}");
    }

    /// <summary>
    /// リンクならリンク先を返す。リンクでないか存在しなければ null を返す。
    /// </summary>
    private static string? LinkTarget(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if (!attributes.HasFlag(FileAttributes.ReparsePoint)) return null;
            FileSystemInfo entry = attributes.HasFlag(FileAttributes.Directory)
                ? new DirectoryInfo(path) : new FileInfo(path);
            return entry.LinkTarget;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    /// <summary>
    /// 途中のリンクをすべてたどった実際のパスを返す。64回たどっても終わらなければ循環とみなす。
    /// </summary>
    internal static string ResolvePhysicalPath(string path)
    {
        string full = Path.GetFullPath(path);
        for (int hop = 0; hop < 64; hop++)
        {
            // ルートから1段ずつ進み、最初に見つかったリンクをリンク先に置き換えてやり直す。
            string root = Path.GetPathRoot(full)!;
            string current = root;
            var parts = full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            bool followed = false;
            for (int i = 0; i < parts.Length; i++)
            {
                current = Path.Combine(current, parts[i]);
                string? target = LinkTarget(current);
                if (target == null) continue;
                current = Path.GetFullPath(target, Path.GetDirectoryName(current)!);
                full = Path.GetFullPath(Path.Combine(new[] { current }.Concat(parts.Skip(i + 1)).ToArray()));
                followed = true;
                break;
            }
            if (!followed) return Path.TrimEndingDirectorySeparator(full);
        }
        throw new IOException($"循環リンクを解決できません: {path}");
    }

    /// <summary>
    /// フォルダーの転送前に、リンクでないことと、実体として自分自身やその下へ転送しないことを確かめる。
    /// </summary>
    public static void ValidateDirectoryTransfer(string source, string destination)
    {
        RejectLinks(source);
        RejectLinks(destination);
        if (IsSameOrDescendant(ResolvePhysicalPath(destination), ResolvePhysicalPath(source)))
            throw new IOException("自身または自身の配下へフォルダーをコピー・移動することはできません。");
    }

    /// <summary>
    /// ファイルの転送前に、リンクでないことと、実体が同じファイルへ転送しないことを確かめる。
    /// </summary>
    public static void ValidateFileTransfer(string source, string destination)
    {
        RejectLinks(source);
        RejectLinks(destination);
        if (string.Equals(ResolvePhysicalPath(source), ResolvePhysicalPath(destination), StringComparison.OrdinalIgnoreCase))
            throw new IOException("同じ実体のファイルへコピー・移動することはできません。");
    }

    /// <summary>
    /// フォルダー以下のファイルを再帰的に列挙する。途中にリンクがあれば例外にする。
    /// </summary>
    internal static IEnumerable<FileInfo> EnumerateTransferFiles(string directory)
    {
        RejectLinks(directory);
        var info = new DirectoryInfo(directory);
        foreach (var file in info.EnumerateFiles())
        {
            RejectLinks(file.FullName);
            yield return file;
        }
        foreach (var child in info.EnumerateDirectories())
            foreach (var file in EnumerateTransferFiles(child.FullName)) yield return file;
    }

    /// <summary>
    /// 「名前_1.拡張子」のように連番を付け、ファイルともフォルダーとも重ならないパスを作る。
    /// </summary>
    public static string GenerateAutoNumberedPath(string originalPath)
    {
        string full = Path.GetFullPath(originalPath);
        string directory = Path.GetDirectoryName(full)!;
        string name = Path.GetFileNameWithoutExtension(full);
        string extension = Path.GetExtension(full);
        for (int number = 1; ; number++)
        {
            string candidate = Path.Combine(directory, $"{name}_{number}{extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
    }

    /// <summary>
    /// アーカイブ内のパスから展開先のパスを作る。絶対パス・「..」・不正な名前・展開先の外を指すパス・途中のリンクを拒否する。
    /// </summary>
    public static string ExtractionTarget(string root, params string[] parts)
    {
        // 各部分の名前を検証する。
        foreach (var part in parts)
        {
            if (Path.IsPathRooted(part) || part.Replace('/', '\\').Split('\\').Any(segment =>
                segment is "." or ".." || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                segment.EndsWith('.') || segment.EndsWith(' ')))
                throw new IOException($"安全でないアーカイブ内パスです: {part}");
        }
        // 組み立てたパスが展開先フォルダーの下にあるか確かめる。
        var target = Path.GetFullPath(Path.Combine(new[] { root }.Concat(parts).ToArray()));
        if (!IsSameOrDescendant(target, root) || target.Equals(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
            throw new IOException("展開先フォルダーの外には書き込めません。");
        // 選んだ展開先がジャンクションの下にあるのは認めるが、その下でリンクをたどることは認めない。
        string current = Path.GetFullPath(root);
        foreach (string part in Path.GetRelativePath(root, target).Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, part);
            RejectLinks(current);
        }
        // 実体のパスでも展開先の外を指していないか確かめる。
        if (!IsSameOrDescendant(ResolvePhysicalPath(target), ResolvePhysicalPath(root)))
            throw new IOException("展開先フォルダーの外には書き込めません。");
        return target;
    }
}
