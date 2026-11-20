using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MirrorFlux.Services;

/// <summary>PDF 互換で保存された Illustrator（AI）ファイルの中の PDF を描画して表示する。</summary>
public static class IllustratorImageDecoder
{
    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    public const string CompatibilityMessage =
        "このAI形式は表示できません。Illustratorで「PDF互換ファイルを作成」を有効にして保存してください。";

    /// <summary>
    /// 描画した画像と、表示中のアートボード番号・アートボード数。
    /// </summary>
    public sealed record Preview(BitmapSource Bitmap, int ArtboardIndex, int ArtboardCount);

    /// <summary>
    /// 指定したアートボードを長辺 4096 ピクセル以内で描画する。
    /// </summary>
    public static async Task<Preview> DecodeAsync(string filePath, int artboardIndex = 0,
        CancellationToken cancellationToken = default)
    {
        // 先頭に PDF の印がなければ表示しない。PostScript や Illustrator 独自形式のデータを ImageMagick に回さないのは、
        // 外部の Ghostscript が要るうえ、新しい独自形式の AI は読めないため。
        using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            4096, FileOptions.Asynchronous))
        {
            byte[] header = new byte[1024];
            int count = await stream.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (header.AsSpan(0, count).IndexOf("%PDF-"u8) < 0)
                throw new NotSupportedException(CompatibilityMessage);
        }

        // PDF として開く。
        using var document = await WindowsPdfDocument.OpenAsync(filePath, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (document is null || document.PageCount == 0)
            throw new InvalidDataException("AIの画像データを読み込めませんでした。" + CompatibilityMessage);

        // 描画サイズを決める。Windows.Data.Pdf の出力サイズは物理ピクセルではなく DIP なので、
        // システムの DPI を考えて描画する（125% でも 4096 ピクセルに収める）。
        int pageCount = checked((int)document.PageCount);
        int pageIndex = Math.Clamp(artboardIndex, 0, pageCount - 1);
        var size = document.GetPageSize((uint)pageIndex);
        double maxDips = 4096.0 * 96 / Math.Max(96u, GetDpiForSystem());
        double scale = Math.Min(2.0, maxDips / Math.Max(size.Width, size.Height));
        uint width = (uint)Math.Max(1, Math.Floor(size.Width * scale));
        uint height = (uint)Math.Max(1, Math.Floor(size.Height * scale));
        var rendered = await document.RenderPageAsync((uint)pageIndex, width, height, cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (rendered.Bitmap is null)
            throw new InvalidDataException("AIのアートボードを描画できませんでした。");
        // それでも 4096 ピクセルを超えたら縮小する。
        BitmapSource bitmap = rendered.Bitmap;
        double finalScale = Math.Min(1, 4096.0 / Math.Max(bitmap.PixelWidth, bitmap.PixelHeight));
        if (finalScale < 1)
        {
            bitmap = new TransformedBitmap(bitmap, new ScaleTransform(finalScale, finalScale));
            bitmap.Freeze();
        }
        return new Preview(bitmap, pageIndex, pageCount);
    }
}
