using System.IO;
using System.IO.Compression;
using System.Text;
using MirrorFlux.Models;
using MirrorFlux.Services;

internal static class ArchiveEncodingRegression
{
    public static async Task Run(string root, Action<bool, string> check)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        foreach (var (encoding, clearFlag) in new[] { (Encoding.GetEncoding(932), false), (Encoding.UTF8, false), (Encoding.UTF8, true) })
        {
            string path = Path.Combine(root, $"japanese-{encoding.CodePage}-{clearFlag}.zip");
            const string name = "アクトレイザー/01 神殿.spc";
            using (var zip = new ZipArchive(File.Create(path), ZipArchiveMode.Create, false, encoding))
            {
                using var stream = zip.CreateEntry(name).Open();
                stream.Write(new byte[] { 1, 2, 3, 4 });
            }
            if (clearFlag)
            {
                // Simulate old tools writing UTF-8 names without the language-encoding flag.
                byte[] bytes = File.ReadAllBytes(path);
                for (int i = 0; i < bytes.Length - 12; i++)
                {
                    if (bytes[i] != 0x50 || bytes[i + 1] != 0x4b) continue;
                    if (bytes[i + 2] == 3 && bytes[i + 3] == 4) bytes[i + 7] &= 0xf7;
                    if (bytes[i + 2] == 1 && bytes[i + 3] == 2) bytes[i + 9] &= 0xf7;
                }
                File.WriteAllBytes(path, bytes);
            }
            check(ArchiveService.ReadArchiveEntries(path, "").Any(e => e.Name == "アクトレイザー"), $"ZIP {encoding.CodePage} Japanese directory lists correctly");
            var entries = ArchiveService.ReadArchiveEntries(path, "アクトレイザー").Where(e => !e.IsParent).ToArray();
            check(entries.Length == 1 && entries[0].Name == "01 神殿.spc", $"ZIP {encoding.CodePage} nested filename lists correctly");
            check((await ArchiveService.ExtractArchiveEntryBytesAsync(path, name)).SequenceEqual(new byte[] { 1, 2, 3, 4 }), $"ZIP {encoding.CodePage} Japanese name extracts for playback");
            string destination = Path.Combine(root, $"extracted-{encoding.CodePage}-{clearFlag}");
            var result = await ArchiveService.ExtractEntriesAsync(path, entries, destination,
                _ => Task.FromResult(new ConflictResolution(ConflictAction.Overwrite, false)), (_, _) => { });
            check(result.CopiedCount == 1 && File.ReadAllBytes(Path.Combine(destination, "01 神殿.spc")).Length == 4, $"ZIP {encoding.CodePage} Japanese name extracts through file copy");
            await ArchiveService.CreateArchiveDirectoryAsync(path, "", "追加🎵", (_, _) => { });
            check(ArchiveService.ReadArchiveEntries(path, "").Any(e => e.Name == "追加🎵") &&
                (await ArchiveService.ExtractArchiveEntryBytesAsync(path, name)).Length == 4,
                $"ZIP {encoding.CodePage} update preserves old names and new Unicode names");
            check(await ArchiveService.DeleteArchiveEntriesAsync(path, entries, (_, _) => { }) == 1 &&
                !ArchiveService.ReadArchiveFilePaths(path).Any(), $"ZIP {encoding.CodePage} deletes the selected Japanese entry");
        }
    }

    public static async Task Audit(string path)
    {
        var files = ArchiveService.ReadArchiveFilePaths(path);
        Console.WriteLine(string.Join(Environment.NewLine, files.Take(5)));
        if (files.Length == 0 || files.Any(p => p.Contains('\ufffd'))) throw new Exception("Invalid decoded archive names");
        string first = files[0];
        if (!ArchiveService.TrySplitArchivePath(first, out var archive, out var key) ||
            (await ArchiveService.ExtractArchiveEntryBytesAsync(archive, key)).Length == 0)
            throw new Exception("Cannot extract decoded entry");
        Console.WriteLine($"PASS real ZIP: {files.Length} names and first entry extraction (read-only)");
    }
}
