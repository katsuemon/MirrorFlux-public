using System.Buffers;
using System.Text;

namespace MirrorFlux.Services;

/// <summary>
/// ファイル先頭のバイト列から、文字コード・改行形式・バイナリかどうかを推定する。
/// </summary>
public static class CharsetDetector
{
    public static readonly Encoding ShiftJis;
    public static readonly Encoding EucJp;

    /// <summary>
    /// コードページを使えるようにし、Shift_JIS（CP932）と EUC-JP を用意する。
    /// </summary>
    static CharsetDetector()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }
        catch
        {
        }

        ShiftJis = GetEncodingSafe(932, "shift_jis");
        EucJp = GetEncodingSafe(51932, "euc-jp");
    }

    /// <summary>
    /// コードページ番号、名前の順に文字コードを取得し、どちらも使えなければ UTF-8 を返す。
    /// </summary>
    private static Encoding GetEncodingSafe(int codepage, string fallbackName)
    {
        try
        {
            return Encoding.GetEncoding(codepage);
        }
        catch
        {
            try
            {
                return Encoding.GetEncoding(fallbackName);
            }
            catch
            {
                return Encoding.UTF8;
            }
        }
    }

    /// <summary>
    /// 判定結果。文字コード、表示名、改行形式、バイナリかどうか。
    /// </summary>
    public sealed record DetectResult(Encoding Encoding, string EncodingName, string NewLineName, bool IsBinary);

    /// <summary>
    /// ファイル先頭のバイト列から文字コード・改行形式・バイナリかどうかを判定する。
    /// </summary>
    public static DetectResult Detect(byte[] buffer, int length)
    {
        if (length == 0)
        {
            return new DetectResult(Encoding.UTF8, "UTF-8", "CRLF", false);
        }

        // BOM があればそれに従う。
        if (length >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF)
        {
            var nl = DetectNewLine(buffer, 3, length);
            return new DetectResult(new UTF8Encoding(true), "UTF-8 BOM", nl, false);
        }
        if (length >= 2 && buffer[0] == 0xFF && buffer[1] == 0xFE)
        {
            var nl = DetectNewLine(buffer, 2, length);
            return new DetectResult(Encoding.Unicode, "UTF-16 LE", nl, false);
        }
        if (length >= 2 && buffer[0] == 0xFE && buffer[1] == 0xFF)
        {
            var nl = DetectNewLine(buffer, 2, length);
            return new DetectResult(Encoding.BigEndianUnicode, "UTF-16 BE", nl, false);
        }

        // 先頭 4096 バイトに NULL（0x00）があればバイナリとみなす。
        int nullCount = 0;
        int checkLen = Math.Min(length, 4096);
        for (int i = 0; i < checkLen; i++)
        {
            if (buffer[i] == 0x00)
            {
                nullCount++;
            }
        }
        bool isBinary = nullCount > 0;

        // 改行形式を調べる。
        var newLine = DetectNewLine(buffer, 0, length);

        if (isBinary)
        {
            return new DetectResult(Encoding.Latin1, "BINARY", newLine, true);
        }

        // 正しい UTF-8 で、マルチバイト文字を含むか全部 ASCII なら UTF-8 とする。
        if (IsValidUtf8(buffer, length, out bool hasMultibyteUtf8))
        {
            if (hasMultibyteUtf8 || IsAllAscii(buffer, length))
            {
                return new DetectResult(new UTF8Encoding(false), "UTF-8", newLine, false);
            }
        }

        // Shift_JIS と EUC-JP の2バイト文字らしさを数え、多い方を選ぶ。
        int sjisScore = CheckShiftJis(buffer, length);
        int eucScore = CheckEucJp(buffer, length);

        if (eucScore > sjisScore && eucScore > 0)
        {
            return new DetectResult(EucJp, "EUC-JP", newLine, false);
        }

        if (sjisScore > 0)
        {
            return new DetectResult(ShiftJis, "Shift_JIS", newLine, false);
        }

        // どれにも当てはまらなければ UTF-8 とする。
        return new DetectResult(new UTF8Encoding(false), "UTF-8", newLine, false);
    }

    /// <summary>
    /// 最初に見つかった改行から改行形式を返す。改行がなければ CRLF とする。
    /// </summary>
    private static string DetectNewLine(byte[] buf, int offset, int len)
    {
        for (int i = offset; i < len - 1; i++)
        {
            if (buf[i] == 0x0D && buf[i + 1] == 0x0A) return "CRLF";
            if (buf[i] == 0x0A) return "LF";
            if (buf[i] == 0x0D) return "CR";
        }
        if (len > offset && buf[len - 1] == 0x0A) return "LF";
        if (len > offset && buf[len - 1] == 0x0D) return "CR";
        return "CRLF";
    }

    /// <summary>
    /// すべて ASCII の範囲か。
    /// </summary>
    private static bool IsAllAscii(byte[] buf, int len)
    {
        for (int i = 0; i < len; i++)
        {
            if (buf[i] >= 0x80) return false;
        }
        return true;
    }

    /// <summary>
    /// 正しい UTF-8 か。マルチバイト文字を含むかも返す。
    /// </summary>
    private static bool IsValidUtf8(byte[] buf, int len, out bool hasMultibyte)
    {
        hasMultibyte = false;
        int i = 0;
        while (i < len)
        {
            if (buf[i] <= 0x7F) { i++; continue; }
            // 調べる範囲が文字の途中で終わることがある。残りのバイトは元のバッファーから（範囲の外は最大3バイトまで）確かめる。
            // 本当のファイル末尾で途切れた文字や、Unicode として不正な値は受け入れない。
            var status = Rune.DecodeFromUtf8(buf.AsSpan(i, Math.Min(4, buf.Length - i)),
                out _, out int consumed);
            if (status != OperationStatus.Done) return false;
            hasMultibyte = true;
            i += consumed;
        }
        return true;
    }

    /// <summary>
    /// Shift_JIS の2バイト文字として読める組の数を数える。
    /// </summary>
    private static int CheckShiftJis(byte[] buf, int len)
    {
        int score = 0;
        for (int i = 0; i < len - 1; i++)
        {
            byte b1 = buf[i];
            byte b2 = buf[i + 1];
            if (((b1 >= 0x81 && b1 <= 0x9F) || (b1 >= 0xE0 && b1 <= 0xFC)) &&
                ((b2 >= 0x40 && b2 <= 0x7E) || (b2 >= 0x80 && b2 <= 0xFC)))
            {
                score++;
                i++;
            }
        }
        return score;
    }

    /// <summary>
    /// EUC-JP の2バイト文字として読める組の数を数える。
    /// </summary>
    private static int CheckEucJp(byte[] buf, int len)
    {
        int score = 0;
        for (int i = 0; i < len - 1; i++)
        {
            byte b1 = buf[i];
            byte b2 = buf[i + 1];
            if (b1 >= 0xA1 && b1 <= 0xFE && b2 >= 0xA1 && b2 <= 0xFE)
            {
                score++;
                i++;
            }
        }
        return score;
    }
}
