using System.IO;
using System.Text;
using MirrorFlux.Services;

internal static class CharsetDetectorRegression
{
    public static void Run(Action<bool, string> check)
    {
        const int limit = 65536;
        foreach (string character in new[] { "é", "日", "😀" })
        {
            byte[] encoded = Encoding.UTF8.GetBytes(character);
            for (int prefix = 1; prefix < encoded.Length; prefix++)
            {
                byte[] data = Encoding.UTF8.GetBytes(new string('a', limit - prefix) + character + "末尾\r\n");
                var detected = CharsetDetector.Detect(data, limit);
                check(detected.Encoding.CodePage == 65001 && !detected.IsBinary &&
                    detected.Encoding.GetString(data) == Encoding.UTF8.GetString(data),
                    $"UTF8 {encoded.Length}-byte character crossing sample boundary after {prefix} bytes");
            }
        }
        foreach (byte[] invalid in new byte[][] { [0xe3, 0x81], [0xc0, 0xaf], [0xed, 0xa0, 0x80], [0xf4, 0x90, 0x80, 0x80], [0xe0, 0x80, 0x80] })
        {
            byte[] data = [.. Encoding.UTF8.GetBytes("本文"), .. invalid];
            check(CharsetDetector.Detect(data, data.Length).Encoding.CodePage != 65001,
                "Invalid/truncated UTF8 is not accepted by UTF8 validation: " + Convert.ToHexString(invalid));
        }
        foreach (var encoding in new[] { CharsetDetector.ShiftJis, CharsetDetector.EucJp, Encoding.Unicode, Encoding.BigEndianUnicode })
        {
            byte[] data = [.. encoding.GetPreamble(), .. encoding.GetBytes("日本語の文章です。\r\n次の行です。")];
            check(CharsetDetector.Detect(data, data.Length).Encoding.CodePage == encoding.CodePage,
                "Existing Japanese/BOM detection retained: " + encoding.WebName);
        }
        check(CharsetDetector.Detect([0, 1, 2, 255], 4).IsBinary, "NUL binary detection retained");
        check(CharsetDetector.Detect([], 0).Encoding.CodePage == 65001, "Empty text remains UTF8");
    }

    public static void AuditUtf8(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        string expected = new UTF8Encoding(false, true).GetString(data);
        var detected = CharsetDetector.Detect(data, Math.Min(data.Length, 65536));
        if (detected.Encoding.CodePage != 65001 || detected.IsBinary || detected.Encoding.GetString(data) != expected)
            throw new Exception("UTF8 file audit failed: " + detected.EncodingName);
        Console.WriteLine($"PASS external UTF8 file: {data.Length} bytes; entire decoded text matches strict UTF8");
    }
}
