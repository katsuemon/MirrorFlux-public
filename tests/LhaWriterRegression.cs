using System.IO;
using System.Text;
using MirrorFlux.Models;
using MirrorFlux.Services;

internal static class LhaWriterRegression
{
    public static async Task Run(string root, Action<bool, string> check)
    {
        string area = Directory.CreateDirectory(Path.Combine(root, "lha-writer")).FullName;
        string archive = Path.Combine(area, "日本語.lzh");
        var random = new byte[65537]; new Random(42).NextBytes(random);
        var files = new Dictionary<string, byte[]>
        {
            ["日本語/表とソ.txt"] = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("compression日本語\n", 40000))),
            ["random.bin"] = random, ["empty.txt"] = [], ["zeros.bin"] = new byte[40000],
            [new string('a', 222)] = [1, 2, 3],
            ["nested/" + new string('b', 240) + "/long.txt"] = [9, 8, 7]
        };
        LhaArchiveWriter.Write(archive, files);
        // Optional independent interoperability audit; no dependency on 7-Zip for normal tests.
        string? sevenZip = Environment.GetEnvironmentVariable("MF_TEST_7ZIP");
        if (!string.IsNullOrEmpty(sevenZip))
        {
            var start = new System.Diagnostics.ProcessStartInfo(sevenZip) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            start.ArgumentList.Add("t"); start.ArgumentList.Add(archive);
            using var process = System.Diagnostics.Process.Start(start)!;
            string output = process.StandardOutput.ReadToEnd(); string error = process.StandardError.ReadToEnd(); process.WaitForExit();
            check(process.ExitCode == 0, "Independent 7-Zip LHA verification: " + (process.ExitCode == 0 ? "all entries and CRCs valid" : output + error));
        }
        var decoded = ArchiveService.ReadArchiveEntryPrefixes(archive, files.Keys.ToArray(), 2_000_000);
        foreach (var (name, bytes) in files)
            check(decoded.TryGetValue(name.Replace('/', '\\'), out var actual) && actual.SequenceEqual(bytes),
                "BSD LHA encoder interoperates with Hst decoder: " + name);
        check(new FileInfo(archive).Length < files.Sum(f => f.Value.Length) / 2, "LH5 compresses repetitive data rather than merely storing it");
        string source = Path.Combine(area, "追加.txt"); File.WriteAllText(source, "added");
        var entry = new FileEntry(source, Path.GetFileName(source), false, false, new FileInfo(source).Length, DateTime.Now);
        var result = await ArchiveService.AddEntriesToArchiveAsync(archive, "日本語", [entry],
            _ => Task.FromResult(new ConflictResolution(ConflictAction.Overwrite, false)), (_, _) => { });
        check(result.CopiedCount == 1 && ArchiveService.ReadArchiveFilePaths(archive).Any(p => p.EndsWith("日本語\\追加.txt")),
            "LHA update retains existing Japanese names and adds Japanese entries");
        await ArchiveService.CreateArchiveDirectoryAsync(archive, "日本語", "空フォルダー", (_, _) => { });
        check(ArchiveService.ReadArchiveFilePaths(archive).Any(p => p.EndsWith("空フォルダー\\.empty")), "LHA creates Japanese empty folders");
        byte[] before = File.ReadAllBytes(archive);
        string unsupported = Path.Combine(area, "unsupported-😀.txt"); File.WriteAllText(unsupported, "data");
        bool rejected = false;
        var failedLogs = new List<string>();
        try
        {
            await ArchiveService.AddEntriesToArchiveAsync(archive, "", [new(unsupported, Path.GetFileName(unsupported), false, false, 4, DateTime.Now)],
                _ => Task.FromResult(new ConflictResolution(ConflictAction.Overwrite, false)), (_, operation) => failedLogs.Add(operation));
        }
        catch (IOException) { rejected = true; }
        check(!failedLogs.Contains("COPY"), "Failed LHA update emits no successful COPY log");
        check(rejected && before.SequenceEqual(File.ReadAllBytes(archive)) && !Directory.EnumerateFiles(area, "*.tmp.*").Any(),
            "Unrepresentable LHA names fail without replacing the archive or leaving temporary files");
    }
}
