using System.IO;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using ImageMagick;

internal static class ImageNativeRoundtripRegression
{
    internal static void Run(string reportPath)
    {
        const int width = 32, height = 24;
        byte[] rgb = new byte[width * height * 3];
        byte[][] colors = [[255, 0, 0], [0, 255, 0], [0, 0, 255], [255, 255, 255], [0, 0, 0], [255, 255, 0]];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
            colors[(x / 8 + y / 8) % colors.Length].CopyTo(rgb, (y * width + x) * 3);
        byte[] ppm = [.. Encoding.ASCII.GetBytes($"P6\n{width} {height}\n255\n"), .. rgb];
        var results = new List<object>();
        foreach (var format in new[] { MagickFormat.Png, MagickFormat.Bmp, MagickFormat.Tiff, MagickFormat.Gif,
            MagickFormat.Jpeg, MagickFormat.WebP, MagickFormat.Jp2, MagickFormat.Avif, MagickFormat.Jxl })
        {
            try
            {
                using var source = new MagickImage(ppm);
                source.Quality = 100;
                byte[] encoded = source.ToByteArray(format);
                using var decoded = new MagickImage(encoded);
                decoded.Depth = 8;
                byte[] actual = decoded.ToByteArray(MagickFormat.Rgb);
                if (decoded.Width != width || decoded.Height != height || actual.Length != rgb.Length)
                    throw new InvalidDataException($"{format}: roundtrip {decoded.Width}x{decoded.Height}, RGB bytes {actual.Length}");
                double error = actual.Zip(rgb, (a, b) => Math.Abs((int)a - b)).Average();
                if (format is MagickFormat.Png or MagickFormat.Bmp or MagickFormat.Tiff or MagickFormat.Gif && error != 0)
                    throw new InvalidDataException("Lossless roundtrip changed pixels");
                results.Add(new { format = format.ToString(), supported = true, meanAbsoluteError = error,
                    pixelSha256 = Convert.ToHexString(SHA256.HashData(actual)), encodedBytes = encoded.Length });
            }
            catch (MagickException ex)
            {
                results.Add(new { format = format.ToString(), supported = false, error = ex.Message });
            }
        }
        byte[] svg = Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"32\" height=\"24\"><rect width=\"32\" height=\"24\" fill=\"red\"/><text x=\"1\" y=\"18\" font-size=\"12\">MF</text></svg>");
        using var vector = new MagickImage(svg);
        if (vector.Width != width || vector.Height != height) throw new InvalidDataException("SVG dimensions changed");
        results.Add(new { format = "Svg", supported = true, pixelSha256 = Convert.ToHexString(SHA256.HashData(vector.ToByteArray(MagickFormat.Rgb))) });
        var report = new { managedVersion = MagickNET.Version, delegates = MagickNET.Delegates,
            features = MagickNET.Features, results };
        File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("Image roundtrip probe recorded 9 raster formats and SVG: " + reportPath);
    }
}
