namespace MirrorFlux.Services.Music;

/// <summary>
/// 音楽ファイルの拡張子の一覧。
/// 再生バックエンド・ファイルアイコン・INIの移行処理はここを参照する。
/// 形式を追加するときは、ここと再生バックエンド、mf.default.ini / AppSettings の拡張子関連付けを合わせる。
/// </summary>
internal static class MusicFormats
{
    /// <summary>VGM エンジンで鳴らす形式。</summary>
    public static readonly string[] Vgm = [".vgm", ".vgz", ".s98"];

    /// <summary>Game Music Emu で鳴らす形式。</summary>
    public static readonly string[] Gme = [".nsf", ".nsfe", ".hes", ".spc", ".gbs", ".ay", ".gym", ".kss", ".sap"];

    /// <summary>MXDRV 互換エンジンで鳴らす形式。</summary>
    public static readonly string[] Mdx = [".mdx"];

    /// <summary>NAudio で鳴らす一般の音声ファイル。</summary>
    public static readonly string[] Audio = [".mp3", ".wav", ".flac", ".m4a", ".wma", ".aac"];

    /// <summary>鍵盤・チャンネル表示のあるチップチューン形式（VGM・GME・MDX）。</summary>
    public static readonly string[] Chiptune = [.. Vgm, .. Gme, .. Mdx];

    private static readonly HashSet<string> ChiptuneSet = new(Chiptune, StringComparer.OrdinalIgnoreCase);

    /// <summary>拡張子（"." 付き）がチップチューン形式かを判定する。</summary>
    public static bool IsChiptune(string extension) => ChiptuneSet.Contains(extension);
}
