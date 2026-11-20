using System.IO;
namespace MirrorFlux.Services.Music;

/// <summary>MDX が使う PDX（ADPCM 音色バンク）の名前を読み、ファイルを探す。</summary>
public static class PdxResolver
{
    /// <summary>MDX のタイトル行（CR LF EOF で終わる）の後ろにある、0で終わる PDX 名を Shift_JIS で読む。</summary>
    public static string ReadBankName(byte[] data)
    {
        int titleEnd = data.AsSpan().IndexOf(new byte[] { 13, 10, 26 });
        if (titleEnd < 0) return "";
        int start = titleEnd + 3, end = Array.IndexOf(data, (byte)0, start);
        return end < 0 ? "" : CharsetDetector.ShiftJis.GetString(data, start, end - start);
    }
    /// <summary>
    /// PDX ファイルを探す。
    /// MDX と同じフォルダー、設定の検索パスの順に、名前そのものと「.pdx」付きの名前を試す。
    /// </summary>
    public static string? Resolve(string mdxPath, string bankName, IEnumerable<string>? searchPaths = null)
    {
        if (string.IsNullOrWhiteSpace(bankName)) return null;
        string name = Path.GetFileName(bankName);
        if (string.IsNullOrWhiteSpace(name)) return null;
        // 探すフォルダーを集める。設定の検索パスは環境変数を展開し、相対パスはアプリのフォルダーから解決する。
        var paths = new List<string> { Path.GetDirectoryName(Path.GetFullPath(mdxPath))! };
        searchPaths ??= new SettingsStore().Load().Settings.PdxSearchPaths.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (string configured in searchPaths)
        {
            try
            {
                string expanded = Environment.ExpandEnvironmentVariables(configured.Trim().Trim('"'));
                if (expanded.Length == 0) continue;
                paths.Add(Path.GetFullPath(expanded, SettingsStore.AppDirectory));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { }
        }
        // 各フォルダーで候補の名前を順に試す。
        foreach (string directory in paths.Distinct(StringComparer.OrdinalIgnoreCase))
            foreach (string candidate in Path.HasExtension(name) ? new[] { name } : new[] { name, name + ".pdx" })
            {
                string path = Path.Combine(directory, candidate);
                if (File.Exists(path)) return Path.GetFullPath(path);
            }
        return null;
    }
}
