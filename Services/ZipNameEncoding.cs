using System.Text;

namespace MirrorFlux.Services;

/// <summary>
/// ZIP のファイル名の文字コード。UTF-8 フラグのない名前は、UTF-8 として正しければ UTF-8、そうでなければ日本の古い ZIP の CP932（Shift_JIS）として読む。
/// 新しく書く名前は UTF-8 にする。
/// </summary>
internal sealed class ZipNameEncoding : Encoding
{
    private ZipNameEncoding() : base(65001) { }
    internal static readonly Encoding Instance = new ZipNameEncoding();
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
    private static readonly Encoding ShiftJis = CreateShiftJis();

    /// <summary>
    /// コードページを使えるようにして CP932 を用意する。
    /// </summary>
    private static Encoding CreateShiftJis()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(932);
    }

    public override string GetString(byte[] bytes, int index, int count) => Decode(bytes.AsSpan(index, count));
    public override int GetCharCount(byte[] bytes, int index, int count) => Decode(bytes.AsSpan(index, count)).Length;
    public override int GetChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex)
    {
        string text = Decode(bytes.AsSpan(byteIndex, byteCount));
        text.CopyTo(0, chars, charIndex, text.Length);
        return text.Length;
    }
    public override int GetByteCount(char[] chars, int index, int count) => UTF8.GetByteCount(chars, index, count);
    public override int GetBytes(char[] chars, int charIndex, int charCount, byte[] bytes, int byteIndex) =>
        UTF8.GetBytes(chars, charIndex, charCount, bytes, byteIndex);
    public override int GetMaxByteCount(int charCount) => UTF8.GetMaxByteCount(charCount);
    public override int GetMaxCharCount(int byteCount) => Math.Max(UTF8.GetMaxCharCount(byteCount), ShiftJis.GetMaxCharCount(byteCount));

    /// <summary>
    /// 正しい UTF-8 なら UTF-8、そうでなければ CP932 として名前を読む。
    /// </summary>
    private static string Decode(ReadOnlySpan<byte> bytes)
    {
        if (System.Text.Unicode.Utf8.IsValid(bytes))
            return StrictUtf8.GetString(bytes);
        return ShiftJis.GetString(bytes);
    }
}
