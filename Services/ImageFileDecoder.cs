using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using ImageMagick;

namespace MirrorFlux.Services;

/// <summary>
/// 画像ファイルを WPF の画像に読み込む。Windows のコーデックと ImageMagick を形式に応じて使い分ける。
/// </summary>
internal static class ImageFileDecoder
{
    internal const string WindowsCodecMessage =
        "HEIC/HEIFの表示にはWindowsの対応画像コーデックが必要です。コーデックが未導入、またはファイルをデコードできません。";
    private static readonly HashSet<string> WindowsCodecExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".heic", ".heif", ".heics", ".heifs", ".hif", ".avci", ".avcs" };
    private static readonly HashSet<string> NativePreferredExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".webp", ".avif", ".psd", ".dds", ".exr" };

    /// <summary>
    /// 画像を読み込む。HEIC/HEIF は Windows のコーデックだけで読み、WebP などは ImageMagick を先に使い、
    /// それ以外は Windows のコーデックで読めなければ ImageMagick で読む。
    /// </summary>
    internal static BitmapSource Decode(string filePath)
    {
        // HEIC/HEIF は Windows のコーデックが必要。読めなければその旨を伝える。
        string extension = Path.GetExtension(filePath);
        if (WindowsCodecExtensions.Contains(extension))
        {
            try { return DecodeWindows(filePath); }
            catch (Exception ex) when (IsDecodeFailure(ex))
            { throw new NotSupportedException(WindowsCodecMessage, ex); }
        }
        // ImageMagick を優先する形式。
        if (NativePreferredExtensions.Contains(extension)) return DecodeNative(filePath);
        // まず Windows のコーデックで読み、失敗したら ImageMagick で読む。
        try { return DecodeWindows(filePath); }
        catch (Exception ex) when (IsDecodeFailure(ex)) { return DecodeNative(filePath); }
    }

    private static bool IsDecodeFailure(Exception error) =>
        error is NotSupportedException or FileFormatException or COMException;

    /// <summary>
    /// Windows のコーデックで読み込む。
    /// </summary>
    private static BitmapSource DecodeWindows(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return ReadBitmap(stream);
    }

    /// <summary>
    /// ImageMagick で読み込み、PNG に変換してから WPF の画像にする。
    /// </summary>
    private static BitmapSource DecodeNative(string path)
    {
        using var image = new MagickImage(path);
        using var stream = new MemoryStream(image.ToByteArray(MagickFormat.Png));
        return ReadBitmap(stream);
    }

    /// <summary>
    /// ストリームから画像を読み切り、フリーズして返す。読み終えた後はストリームを閉じてよい。
    /// </summary>
    private static BitmapSource ReadBitmap(Stream stream)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }
}
