using System.IO;

namespace MirrorFlux.Services.Music;

/// <summary>YM2608 のリズム音源用 ROM（YM2608_ADPCM.ROM、8192バイト）を、EXE のフォルダーかその下の roms フォルダーから読み込む。</summary>
internal static class Ym2608RhythmRom
{
    internal const string FileName = "YM2608_ADPCM.ROM";
    /// <summary>
    /// ROM を探すフォルダーを決める。
    /// dotnet から起動したときは BaseDirectory、それ以外は EXE のあるフォルダー。
    /// </summary>
    internal static string ResolveDirectory(string? processPath, string baseDirectory) =>
        string.IsNullOrEmpty(processPath) || Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? baseDirectory
            : Path.GetDirectoryName(processPath) ?? baseDirectory;
    /// <summary>
    /// ROM のファイルを、EXE のフォルダー、その下の roms フォルダーの順に探す（OPL4 の yrw801.rom と同じ置き場所）。
    /// 大文字・小文字が違う名前でも見つける。見つからなければ null。
    /// </summary>
    internal static string? FindPath(string exeDirectory)
    {
        foreach (string directory in new[] { exeDirectory, Path.Combine(exeDirectory, "roms") })
        {
            string path = Path.Combine(directory, FileName);
            if (File.Exists(path)) return path;
            if (!Directory.Exists(directory)) continue;
            string? match = Directory.EnumerateFiles(directory)
                .FirstOrDefault(p => Path.GetFileName(p).Equals(FileName, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;
        }
        return null;
    }

    /// <summary>
    /// ROM を読み込む。
    /// 見つからないか大きさが違えば null を返す。
    /// </summary>
    internal static byte[]? Load()
    {
        try
        {
            // IncludeAllContentForSelfExtract を使うと BaseDirectory は展開先になる。外部 ROM は元の EXE の隣に置くので、EXE の場所から探す。
            string? path = FindPath(ResolveDirectory(Environment.ProcessPath, AppContext.BaseDirectory));
            // 大きさが 8192 バイトのときだけ使う。
            if (path == null || new FileInfo(path).Length != 8192) return null;
            byte[] data = File.ReadAllBytes(path);
            return data.Length == 8192 ? data : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
