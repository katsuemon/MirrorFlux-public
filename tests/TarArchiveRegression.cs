using System;
using System.Formats.Tar;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MirrorFlux.Models;
using MirrorFlux.Services;

internal static class TarArchiveRegression
{
    public static async Task Run(string root, Action<bool, string> check)
    {
        string source = Path.Combine(root, "tar-source");
        string folder = Path.Combine(source, "素材");
        Directory.CreateDirectory(Path.Combine(folder, "空"));
        Directory.CreateDirectory(Path.Combine(folder, "曲"));
        File.WriteAllText(Path.Combine(folder, "曲", "01 音.txt"), "tar content", Encoding.UTF8);
        string single = Path.Combine(source, "single.txt");
        File.WriteAllText(single, "replacement", Encoding.UTF8);
        string archive = Path.Combine(root, "roundtrip.tar");
        using (var output = File.Create(archive))
        using (var writer = new TarWriter(output, TarEntryFormat.Pax))
        {
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "single.txt")
            {
                DataStream = new MemoryStream(Encoding.UTF8.GetBytes("original"))
            });
        }

        static FileEntry Entry(string path) => new(path, Path.GetFileName(path),
            Directory.Exists(path), false, File.Exists(path) ? new FileInfo(path).Length : 0,
            File.GetLastWriteTime(path));
        static Task<ConflictResolution> Overwrite(ConflictInfo _) =>
            Task.FromResult(new ConflictResolution(ConflictAction.Overwrite, false));
        static void Log(string _, string __) { }

        check(ArchiveService.CanModifyArchive(archive, out _), "TAR accepts archive edits");
        check(!ArchiveService.CanModifyArchive(Path.Combine(root, "readonly.tgz"), out _),
            "TGZ remains read-only");
        var result = await ArchiveService.AddEntriesToArchiveAsync(
            archive, "", [Entry(folder), Entry(single)], Overwrite, Log);
        check(result.CopiedCount >= 3 && result.SuccessfulPaths.Contains(folder),
            "TAR adds marked folder and file");
        string replacement = Encoding.UTF8.GetString(await ArchiveService.ExtractArchiveEntryBytesAsync(
            archive, "single.txt"));
        check(replacement.TrimStart('\uFEFF') == "replacement", "TAR overwrite replaces existing file");
        check(Encoding.UTF8.GetString(await ArchiveService.ExtractArchiveEntryBytesAsync(
            archive, "素材/曲/01 音.txt")).Contains("tar content"), "TAR preserves Japanese nested file");

        using (var input = File.OpenRead(archive))
        using (var reader = new TarReader(input))
        {
            var names = new System.Collections.Generic.List<string>();
            TarEntry? entry;
            while ((entry = reader.GetNextEntry()) != null) names.Add(entry.Name.TrimEnd('/'));
            check(names.Contains("素材/空") && names.Contains("素材/曲"),
                "TAR preserves empty and nested directories");
            check(names.Count(name => name == "single.txt") == 1,
                "TAR overwrite has one entry per name");
        }

        await ArchiveService.CreateArchiveDirectoryAsync(archive, "", "新規", Log);
        check(ArchiveService.ReadArchiveEntries(archive, "").Any(e => e.Name == "新規"),
            "TAR creates an empty directory");
        var target = new FileEntry(archive + "\\素材\\曲", "曲", true, false, 0, DateTime.Now);
        int removed = await ArchiveService.DeleteArchiveEntriesAsync(archive, [target], Log);
        check(removed >= 2 && !ArchiveService.ReadArchiveFilePaths(archive)
            .Any(path => path.Contains("01 音.txt")), "TAR deletes a directory recursively");

        string before = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            File.ReadAllBytes(archive)));
        string failureSource = Path.Combine(source, "single.txt");
        bool failed = false;
        try
        {
            await ArchiveService.AddEntriesToArchiveAsync(archive, "", [Entry(failureSource)],
                _ =>
                {
                    File.Delete(failureSource);
                    return Overwrite(_);
                }, Log);
        }
        catch (FileNotFoundException) { failed = true; }
        check(failed && before == Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            File.ReadAllBytes(archive))) && !Directory.EnumerateFiles(root, "roundtrip.tar.tmp.*").Any(),
            "TAR write failure keeps the original and removes the temporary file");
    }
}
