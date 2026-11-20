using System.IO;
using System.Runtime.InteropServices;

namespace MirrorFlux.Services;

/// <summary>
/// デスクトップ・ダウンロードなど Windows の既知フォルダーの場所と表示名を扱う。
/// </summary>
internal static class WindowsKnownFolders
{
    /// <summary>
    /// 既知フォルダー1つ。shell: の別名、%USERPROFILE% の下の名前、表示名、フォルダー ID。
    /// </summary>
    private sealed record Folder(string Alias, string LegacyName, string Label, Guid Id);
    private static readonly Folder[] Folders =
    [
        new("Desktop", "Desktop", "デスクトップ", new("B4BFCC3A-DB2C-424C-B029-7FE99A87C641")),
        new("Downloads", "Downloads", "ダウンロード", new("374DE290-123F-4565-9164-39C4925E467B")),
        new("Personal", "Documents", "マイドキュメント", new("FDD39AD0-238F-46AF-ADB4-6C85480369C7")),
        new("My Pictures", "Pictures", "ピクチャ", new("33E28130-4E1E-4676-835A-98395C3BC3BB")),
        new("My Music", "Music", "ミュージック", new("4BD8D571-6D19-48D3-BE97-422220080E43")),
        new("My Video", "Videos", "ビデオ", new("18989B1D-99B5-455B-841C-AB7C74E4DDFC"))
    ];

    /// <summary>
    /// 既知フォルダーの今の場所を問い合わせる。OneDrive などへ移されたフォルダーにも対応する。
    /// </summary>
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath(in Guid id, uint flags, IntPtr token, out IntPtr path);

    /// <summary>
    /// 既知フォルダーのパスを取る。KF_FLAG_DONT_VERIFY（0x4000）でドライブがオフラインでも設定上のパスを返させる。
    /// </summary>
    private static string GetPath(Folder folder)
    {
        int result = SHGetKnownFolderPath(folder.Id, 0x4000, IntPtr.Zero, out IntPtr path);
        try
        {
            if (result < 0) throw new IOException("システムフォルダーを取得できません: " + folder.Label, Marshal.GetExceptionForHR(result));
            return Marshal.PtrToStringUni(path) ?? throw new IOException("システムフォルダーのパスが空です。");
        }
        finally { Marshal.FreeCoTaskMem(path); }
    }

    /// <summary>
    /// 「shell:Desktop」や「%USERPROFILE%\Desktop」のような書き方なら、既知フォルダーの実際のパスを返す。当てはまらなければ null を返す。
    /// </summary>
    internal static string? ResolveAlias(string value)
    {
        string normalized = value.Replace('/', '\\').TrimEnd('\\');
        var folder = Folders.FirstOrDefault(f =>
            normalized.Equals("shell:" + f.Alias, StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals(@"%USERPROFILE%\" + f.LegacyName, StringComparison.OrdinalIgnoreCase));
        return folder == null ? null : GetPath(folder);
    }

    /// <summary>
    /// パスが既知フォルダーなら表示名を返す。当てはまらなければ null を返す。
    /// </summary>
    internal static string? GetLabel(string path)
    {
        foreach (var folder in Folders)
        {
            try
            {
                if (Path.TrimEndingDirectorySeparator(path).Equals(Path.TrimEndingDirectorySeparator(GetPath(folder)), StringComparison.OrdinalIgnoreCase))
                    return folder.Label;
            }
            // 取得できない既知フォルダーがあっても、ほかのフォルダーの判定は続ける。
            catch (IOException) { }
        }
        return null;
    }
}
