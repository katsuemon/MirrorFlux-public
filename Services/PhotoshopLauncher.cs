using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace MirrorFlux.Services;

/// <summary>
/// インストールされた Photoshop を探し、画像ファイルを渡して開く。
/// </summary>
public static class PhotoshopLauncher
{
    /// <summary>
    /// レジストリと Adobe のインストールフォルダーから Photoshop.exe を探す。見つからなければ null を返す。
    /// </summary>
    public static string? FindPhotoshop()
    {
        // App Paths、Adobe のバージョン別の登録、Program Files の順に候補を集める。
        var candidates = new List<string?>();

        candidates.Add(ReadAppPath(Registry.LocalMachine));
        candidates.Add(ReadAppPath(Registry.CurrentUser));
        candidates.AddRange(ReadAdobeVersions(Registry.LocalMachine));
        candidates.AddRange(ReadAdobeVersions(Registry.CurrentUser));
        candidates.AddRange(GlobInstallDirs(@"C:\Program Files\Adobe"));
        candidates.AddRange(GlobInstallDirs(@"C:\Program Files (x86)\Adobe"));

        // 実在する最初の候補を使う。
        foreach (var c in candidates)
        {
            if (string.IsNullOrWhiteSpace(c)) continue;
            if (File.Exists(c)) return Path.GetFullPath(c);
        }
        return null;
    }

    /// <summary>
    /// ファイルを Photoshop で開く。ファイルか Photoshop が見つからない、または起動に失敗したら false を返す。
    /// </summary>
    public static bool Send(string file)
    {
        if (string.IsNullOrWhiteSpace(file) || !File.Exists(file)) return false;

        var ps = FindPhotoshop();
        if (ps is null) return false;

        try
        {
            Process.Start(new ProcessStartInfo(ps, $"\"{file}\"") { UseShellExecute = false });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? ReadAppPath(RegistryKey root)
        => ReadPath(root, @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\Photoshop.exe", null);

    /// <summary>
    /// Adobe\Photoshop の下のバージョンごとの登録から、実行ファイルのパスを集める。
    /// </summary>
    private static IEnumerable<string?> ReadAdobeVersions(RegistryKey root)
    {
        var list = new List<string?>();
        try
        {
            using var ps = root.OpenSubKey(@"SOFTWARE\Adobe\Photoshop");
            if (ps is null) return list;

            // ApplicationPath を優先し、なければ InstallPath に Photoshop.exe を付ける。
            foreach (var name in ps.GetSubKeyNames())
            {
                using var ver = ps.OpenSubKey(name);
                if (ver is null) continue;

                var app = ver.GetValue("ApplicationPath") as string;
                var install = ver.GetValue("InstallPath") as string;
                if (!string.IsNullOrWhiteSpace(app)) list.Add(app);
                else if (!string.IsNullOrWhiteSpace(install)) list.Add(Path.Combine(install, "Photoshop.exe"));
            }
        }
        catch
        {
            // レジストリを読めなければ無視する。
        }
        return list;
    }

    /// <summary>
    /// 「Adobe Photoshop*」フォルダーの中にある Photoshop.exe を集める。
    /// </summary>
    private static IEnumerable<string?> GlobInstallDirs(string dir)
    {
        var list = new List<string?>();
        try
        {
            if (!Directory.Exists(dir)) return list;
            foreach (var d in Directory.GetDirectories(dir, "Adobe Photoshop*"))
            {
                var exe = Path.Combine(d, "Photoshop.exe");
                if (File.Exists(exe)) list.Add(exe);
            }
        }
        catch
        {
            // フォルダーを列挙できなければ無視する。
        }
        return list;
    }

    /// <summary>
    /// レジストリの値を文字列で読む。読めなければ null を返す。
    /// </summary>
    private static string? ReadPath(RegistryKey root, string subKey, string? valueName)
    {
        try
        {
            using var key = root.OpenSubKey(subKey);
            return key?.GetValue(valueName ?? "") as string;
        }
        catch
        {
            return null;
        }
    }
}
