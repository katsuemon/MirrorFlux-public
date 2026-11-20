using System.IO;
using System.Windows;
using System.Windows.Media;

namespace MirrorFlux.Services;

/// <summary>
/// ファイル一覧の線画アイコンを提供する。VS Code 風の落ち着いた線画で、外部画像やフォントに頼らず、
/// フリーズ済みのジオメトリで描くため高 DPI でもにじまず速い。色はテーマ色から取る。
/// </summary>
public static class FileIconService
{
    private static readonly Geometry FolderGeom;
    private static readonly Geometry ParentGeom;
    private static readonly Geometry FileGeom;
    private static readonly Geometry CSharpGeom;
    private static readonly Geometry ScriptGeom;
    private static readonly Geometry MarkdownGeom;
    private static readonly Geometry ProjectGeom;
    private static readonly Geometry GitGeom;
    private static readonly Geometry ExeGeom;
    private static readonly Geometry ArchiveGeom;
    private static readonly Geometry ImageGeom;
    private static readonly Geometry MusicGeom;


    /// <summary>
    /// 16x16 を基準にした線画アイコンの形を用意する。
    /// </summary>
    static FileIconService()
    {
        // フォルダー（上にタブの付いた角丸の枠）。
        FolderGeom = CreateGeom("M 1.5,3.5 C 1.5,2.7 2.2,2 3,2 L 5.5,2 C 6.2,2 6.8,2.4 7.2,3 L 8.2,4.5 L 13,4.5 C 13.8,4.5 14.5,5.2 14.5,6 L 14.5,12.5 C 14.5,13.3 13.8,14 13,14 L 3,14 C 2.2,14 1.5,13.3 1.5,12.5 Z");

        // 親フォルダー「..」（フォルダーに上向き矢印）。
        ParentGeom = CreateGeom("M 1.5,3.5 C 1.5,2.7 2.2,2 3,2 L 5.5,2 C 6.2,2 6.8,2.4 7.2,3 L 8.2,4.5 L 13,4.5 C 13.8,4.5 14.5,5.2 14.5,6 L 14.5,12.5 C 14.5,13.3 13.8,14 13,14 L 3,14 C 2.2,14 1.5,13.3 1.5,12.5 Z M 8,11.5 L 8,6.5 M 5.5,9 L 8,6.5 L 10.5,9");

        // 一般のファイル（右上の角を折った紙）。
        FileGeom = CreateGeom("M 2.5,2 C 2.5,1.5 2.9,1 3.5,1 L 9.5,1 L 13.5,5 L 13.5,13.5 C 13.5,14.1 13.1,14.5 12.5,14.5 L 3.5,14.5 C 2.9,14.5 2.5,14.1 2.5,13.5 Z M 9,1 L 9,5.5 L 13.5,5.5");

        // C#（C と #）。
        CSharpGeom = CreateGeom("M 6.5,4.5 C 3.5,4.5 2,6 2,8 C 2,10 3.5,11.5 6.5,11.5 M 8,6 L 14,6 M 7.5,10 L 13.5,10 M 9.5,4 L 8.5,12 M 12.5,4 L 11.5,12");

        // スクリプト・バッチ（プロンプト「>_」）。
        ScriptGeom = CreateGeom("M 2.5,4 L 7,8 L 2.5,12 M 8.5,12 L 13.5,12");

        // Markdown（M と下向き矢印）。
        MarkdownGeom = CreateGeom("M 1.5,5 L 1.5,12 M 1.5,5 L 4.5,8.5 L 7.5,5 M 7.5,5 L 7.5,12 M 11.5,4 L 11.5,12 M 9.5,10 L 11.5,12 L 13.5,10");

        // Visual Studio のプロジェクト・ソリューション。
        ProjectGeom = CreateGeom("M 3.5,5.5 L 9,2 L 12.5,4.5 L 8,8 L 12.5,11.5 L 9,14 L 3.5,10.5 L 6.5,8 Z");

        // Git の設定ファイル。
        GitGeom = CreateGeom("M 8,1.5 L 14.5,8 L 8,14.5 L 1.5,8 Z M 5.5,8 L 8,5.5 M 8,5.5 L 8,10.5 M 8,8 L 10.5,10.5");

        // 実行ファイル・DLL。
        ExeGeom = CreateGeom("M 2,2 C 1.5,2 1,2.5 1,3 L 1,13 C 1,13.5 1.5,14 2,14 L 14,14 C 14.5,14 15,13.5 15,13 L 15,3 C 15,2.5 14.5,2 14,2 Z M 5.5,5.5 L 10.5,5.5 L 10.5,10.5 L 5.5,10.5 Z M 8,4 L 8,5.5 M 8,10.5 L 8,12 M 4,8 L 5.5,8 M 10.5,8 L 12,8");

        // アーカイブ（ファスナー付きの箱）。
        ArchiveGeom = CreateGeom("M 2,3 L 14,3 L 14,13.5 L 2,13.5 Z M 8,3 L 8,13.5 M 6.5,5 L 8,5 M 8,7 L 9.5,7 M 6.5,9 L 8,9 M 8,11 L 9.5,11");

        // 画像（太陽と山）。
        ImageGeom = CreateGeom("M 2,3 L 14,3 L 14,13 L 2,13 Z M 5,6.5 A 1,1 0 1 0 5,6.49 M 2,11 L 6,7.5 L 9.5,11 L 11.5,9.5 L 14,11.5");

        // 音楽（音符）。
        MusicGeom = CreateGeom("M 4,11.5 A 2,1.5 0 1 0 4,8.5 L 4,2 L 13,2 L 13,9.5 A 2,1.5 0 1 0 13,6.5 L 13,4.5 L 4,4.5");
    }

    /// <summary>
    /// パス記法の文字列からジオメトリを作り、フリーズする。
    /// </summary>
    private static Geometry CreateGeom(string path)
    {
        var g = Geometry.Parse(path);
        g.Freeze();
        return g;
    }

    /// <summary>
    /// 項目の名前と種類に合うアイコンの形とテーマ色を返す。
    /// </summary>
    public static (Geometry Geometry, Brush Brush) GetIcon(string name, bool isDirectory, bool isParent)
    {
        // 親フォルダーとフォルダー。
        if (isParent) return (ParentGeom, ThemeBrushes.Get("IconDirectory"));
        if (isDirectory) return (FolderGeom, ThemeBrushes.Get("IconDirectory"));

        // Git の設定ファイルは名前で見分ける。
        var lowerName = name.ToLowerInvariant();
        if (lowerName is ".gitignore" or ".gitattributes" or ".gitmodules")
        {
            return (GitGeom, ThemeBrushes.Get("IconGit"));
        }

        // それ以外は拡張子で決める。
        var ext = Path.GetExtension(lowerName);
        return ext switch
        {
            ".cs" => (CSharpGeom, ThemeBrushes.Get("IconScript")),
            ".bat" or ".cmd" or ".ps1" or ".sh" => (ScriptGeom, ThemeBrushes.Get("IconScript")),
            ".md" or ".markdown" => (MarkdownGeom, ThemeBrushes.Get("IconMarkdown")),
            ".csproj" or ".sln" or ".vbproj" => (ProjectGeom, ThemeBrushes.Get("IconProject")),
            ".exe" or ".dll" => (ExeGeom, ThemeBrushes.Get("IconDefault")),
            ".zip" or ".7z" or ".rar" or ".tar" or ".gz" or ".lzh" => (ArchiveGeom, ThemeBrushes.Get("IconArchive")),
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".ico" or ".svg" or ".webp" or ".ai" => (ImageGeom, ThemeBrushes.Get("IconImage")),
            var music when MirrorFlux.Services.Music.MusicFormats.IsChiptune(music) => (MusicGeom, ThemeBrushes.Get("IconMusic")),
            _ => (FileGeom, ThemeBrushes.Get("IconDefault"))
        };
    }
}
