using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace MirrorFlux.Services.Music;

/// <summary>
/// 検査済みの MXDRV の MDX 楽譜と、付属の PDX バンクを読み込んだもの。
/// 元のファイルからは切り離して持つ。
/// </summary>
internal sealed class MdxFile
{
    /// <summary>FM 音色1つ分（番号と27バイトのデータ）。</summary>
    internal sealed record Voice(byte Number, byte[] Data);
    /// <summary>PDX の中の1サンプルの位置と長さ。</summary>
    internal sealed record PdxSample(int Offset, int Length);

    private static readonly Encoding ShiftJis = CreateShiftJis();

    public required byte[] Data { get; init; }
    public required string Title { get; init; }
    public required string PdxFileName { get; init; }
    public required int[] TrackOffsets { get; init; }
    public required IReadOnlyDictionary<byte, Voice> Voices { get; init; }
    public string PdxResolvedPath { get; init; } = "";
    public byte[]? PdxData { get; init; }
    public PdxSample[] PdxSamples { get; init; } = [];

    /// <summary>MDX を読み込んで検査し、PDX が指定されていれば探して読み込む。</summary>
    public static MdxFile Load(string path, IEnumerable<string>? pdxSearchPaths = null)
    {
        // タイトルは CR LF EOF（0x0d 0x0a 0x1a）で終わる。
        byte[] data = File.ReadAllBytes(path);
        if (data.Length < 32) throw new InvalidDataException("MDX file is too short.");

        int terminator = FindTitleTerminator(data);
        if (terminator < 0) throw new InvalidDataException("MDX title terminator was not found.");
        string title = ReadTitle(data);

        // タイトルの後ろの0終端の文字列が PDX 名で、その次が基準位置になる。
        int pdxNameStart = terminator + 3;
        int pdxNameEnd = Array.IndexOf(data, (byte)0, pdxNameStart);
        if (pdxNameEnd < 0) throw new InvalidDataException("MDX PDX name is not terminated.");
        string pdxName = ShiftJis.GetString(data, pdxNameStart, pdxNameEnd - pdxNameStart);
        int basePoint = pdxNameEnd + 1;
        EnsureRange(data, basePoint, 4);

        // 基準位置から音色表の位置とトラック表を読む。トラック数は9か16だけ受け付ける。
        int toneOffset = ReadWord(data, basePoint);
        int firstTrackOffset = ReadWord(data, basePoint + 2);
        int trackCount = (firstTrackOffset - 2) / 2;
        if (firstTrackOffset < 20 || (firstTrackOffset & 1) != 0 || trackCount is not (9 or 16))
            throw new InvalidDataException($"Unsupported MDX track table ({trackCount} tracks).");
        EnsureRange(data, basePoint + 2, trackCount * 2);

        // 各トラックの開始位置を求め、ファイル内に収まるか確かめる。
        int[] tracks = new int[trackCount];
        for (int i = 0; i < tracks.Length; i++)
        {
            tracks[i] = checked(basePoint + ReadWord(data, basePoint + 2 + i * 2));
            if ((uint)tracks[i] >= (uint)data.Length)
                throw new InvalidDataException($"MDX track {i + 1} points outside the file.");
        }

        // 音色表を27バイトずつ読む。
        int voiceStart = checked(basePoint + toneOffset);
        if ((uint)voiceStart > (uint)data.Length) throw new InvalidDataException("MDX voice table points outside the file.");
        var voices = new Dictionary<byte, Voice>();
        for (int offset = voiceStart; offset + 27 <= data.Length; offset += 27)
        {
            byte[] voice = data.AsSpan(offset, 27).ToArray();
            voices[voice[0]] = new Voice(voice[0], voice);
        }

        // PDX 名があれば探して読み込む。
        string pdxResolvedPath = "";
        byte[]? pdxData = null;
        PdxSample[] samples = [];
        if (!string.IsNullOrWhiteSpace(pdxName))
        {
            string? pdxPath = PdxResolver.Resolve(path, pdxName, pdxSearchPaths);
            if (pdxPath != null)
            {
                pdxResolvedPath = pdxPath;
                pdxData = File.ReadAllBytes(pdxPath);
                samples = ParsePdx(pdxData);
            }
        }

        return new MdxFile
        {
            Data = data,
            Title = string.IsNullOrWhiteSpace(title) ? Path.GetFileNameWithoutExtension(path) : title,
            PdxFileName = pdxName,
            TrackOffsets = tracks,
            Voices = voices,
            PdxResolvedPath = pdxResolvedPath,
            PdxData = pdxData,
            PdxSamples = samples
        };
    }

    /// <summary>タイトルを Shift_JIS で読み、末尾の0と空白を除く。</summary>
    internal static string ReadTitle(ReadOnlySpan<byte> data)
    {
        int terminator = FindTitleTerminator(data);
        return terminator < 0 ? "" : ShiftJis.GetString(data[..terminator]).TrimEnd('\0', ' ');
    }

    /// <summary>PDX のサンプル表を読む。</summary>
    private static PdxSample[] ParsePdx(byte[] data)
    {
        // 標準の PDX はビッグエンディアンの位置・長さの組が96個並ぶ。EX-PDX は後ろに表を足すことがあるが、最初のバンクはそのまま読める。
        int entries = Math.Min(96, data.Length / 8);
        var samples = new PdxSample[entries];
        for (int i = 0; i < entries; i++)
        {
            int p = i * 8;
            uint offset = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(p, 4));
            uint length = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(p + 4, 4));
            if (offset > data.Length || length > data.Length - offset)
                throw new InvalidDataException($"PDX sample {i} points outside the bank.");
            samples[i] = new PdxSample((int)offset, (int)length);
        }
        return samples;
    }

    /// <summary>タイトルの終わり（CR LF EOF）の位置を探す。</summary>
    private static int FindTitleTerminator(ReadOnlySpan<byte> data)
    {
        for (int i = 0; i + 2 < data.Length; i++)
            if (data[i] == 0x0d && data[i + 1] == 0x0a && data[i + 2] == 0x1a) return i;
        return -1;
    }

    /// <summary>ビッグエンディアンの16ビット値を読む。</summary>
    private static int ReadWord(byte[] data, int offset)
    {
        EnsureRange(data, offset, 2);
        return BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset, 2));
    }

    /// <summary>範囲がファイル内に収まらなければ例外にする。</summary>
    private static void EnsureRange(byte[] data, int offset, int length)
    {
        if (offset < 0 || length < 0 || offset > data.Length - length)
            throw new InvalidDataException("Truncated MDX structure.");
    }

    /// <summary>置き換え文字を使う Shift_JIS（コードページ932）を用意する。</summary>
    private static Encoding CreateShiftJis()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(932, EncoderFallback.ReplacementFallback, DecoderFallback.ReplacementFallback);
    }
}
