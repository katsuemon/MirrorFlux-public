using System.IO;
using System.Text.RegularExpressions;

namespace MirrorFlux.Services;

/// <summary>
/// INI に登録したジャンプ先（%jumpPath01～10）のパスを解決し、表示名と移動できるかを判断する。
/// </summary>
public static class JumpListService
{
    // 「パス,"表示名"」の形。パスは引用符で囲んでもよい。表示名は末尾の引用符で囲んだ部分だけを認め、
    // パスに含まれるカンマ（例 C:\a,b）は表示名の区切りとみなさない。
    private static readonly Regex LabeledValue = new(@"^\s*(?<path>""[^""]*""|[^""]*?)\s*,\s*""(?<label>[^""]*)""\s*$", RegexOptions.CultureInvariant);

    /// <summary>
    /// 登録値を移動先のパスと表示名に分ける。表示名がなければ空文字を返す。
    /// </summary>
    public static (string Path, string Label) Split(string value)
    {
        var match = LabeledValue.Match(value ?? "");
        if (match.Success) return (match.Groups["path"].Value.Trim().Trim('"'), match.Groups["label"].Value.Trim());
        return ((value ?? "").Trim().Trim('"'), "");
    }

    /// <summary>
    /// INIから読んだ値を保存用に整える。表示名があればそのまま、なければパスを囲む引用符を外す。
    /// </summary>
    public static string NormalizeIniValue(string value) =>
        LabeledValue.IsMatch(value) ? value.Trim() : value.Trim().Trim('"');

    /// <summary>
    /// 既知フォルダーの別名や環境変数を展開し、相対パスはアプリのフォルダーを基準に絶対パスへ直す。
    /// </summary>
    public static string ResolvePath(string value)
    {
        string input = Split(value).Path;
        string path = WindowsKnownFolders.ResolveAlias(input) ?? Environment.ExpandEnvironmentVariables(input);
        return Path.GetFullPath(path, SettingsStore.AppDirectory);
    }

    /// <summary>
    /// 表示名を指定していればそれを、既知フォルダーなら Windows の表示名を、それ以外は登録したパスを返す。
    /// </summary>
    public static string GetDisplayName(string value)
    {
        var (input, label) = Split(value);
        if (label.Length > 0) return label;
        try { return WindowsKnownFolders.GetLabel(ResolvePath(input)) ?? input; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return input; }
    }

    /// <summary>
    /// ジャンプ先へ移動できるか確かめる。フォルダー、対応アーカイブ、アーカイブ内のフォルダーを受け付ける。
    /// </summary>
    public static bool TryResolve(string value, out string path, out string error)
    {
        path = ""; error = "";
        try
        {
            // 未登録なら登録方法を伝える。
            if (string.IsNullOrWhiteSpace(Split(value).Path)) { error = "INIの %jumpPath01〜10 に移動先を登録してください。"; return false; }
            // 実フォルダーかアーカイブファイルなら移動できる。
            path = ResolvePath(value);
            if (Directory.Exists(path) || File.Exists(path) && ArchiveService.IsSupportedArchive(path)) return true;
            // アーカイブ内のパスなら、その下にファイルがあればフォルダーとして移動できる。
            if (ArchiveService.TrySplitArchivePath(path, out var archive, out var internalPath) && File.Exists(archive))
            {
                string key = internalPath.Replace('\\', '/').Trim('/');
                bool exists = ArchiveService.ReadArchiveFilePaths(archive).Any(entry =>
                {
                    ArchiveService.TrySplitArchivePath(entry, out _, out var inside);
                    return inside.Replace('\\', '/').StartsWith(key + "/", StringComparison.OrdinalIgnoreCase);
                });
                if (exists) return true;
            }
            error = "移動先のフォルダーまたはアーカイブが見つかりません。";
        }
        // 壊れたアーカイブの読み取りは形式ごとに違う例外を出す。ダイアログ側は非同期の確定処理から
        // 呼ぶため、ここで受けないとアプリ全体の未処理例外になる。
        catch (Exception ex)
        { error = "移動先を確認できません: " + ex.Message; }
        return false;
    }
}
