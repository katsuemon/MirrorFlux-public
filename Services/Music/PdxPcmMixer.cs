using System.Buffers.Binary;
namespace MirrorFlux.Services.Music;

/// <summary>
/// X68000 の PCM8 を8チャンネル分ミックスする。
/// OKI ADPCM（下位ニブルから）、ビッグエンディアンの16ビット PCM、符号付き8ビット PCM を扱う。
/// </summary>
internal sealed class PdxPcmMixer
{
    // 出力のサンプルレート。通常の再生は 44.1 kHz、NanoDrive 8 へ送るときは OKIM6258 の 15.625 kHz。
    private readonly double _outputRate;
    internal PdxPcmMixer(double outputRate = 44100) { _outputRate = outputRate; }
    /// <summary>1チャンネル分の発音状態（サンプル・位置・進み幅・左右・音量・ピーク）。</summary>
    private sealed class Voice
    {
        public short[] Samples = []; public double Position, Step; public int Pan; public double Gain, Peak;
        // 元の PDX のデータ（ADPCM をそのまま送るときに使う）。
        public byte[] Bank = []; public int Offset, Mode;
    }
    private readonly Voice[] _voices = Enumerable.Range(0, 8).Select(_ => new Voice()).ToArray();
    private readonly Dictionary<(int, int, int), short[]> _samples = [];
    private static readonly int[] DeltaTable = [16, 17, 19, 21, 23, 25, 28, 31, 34, 37, 41, 45, 50, 55, 60, 66,
        73, 80, 88, 97, 107, 118, 130, 143, 157, 173, 190, 209, 230, 253, 279, 307,
        337, 371, 408, 449, 494, 544, 598, 658, 724, 796, 876, 963, 1060, 1166, 1282, 1411, 1552];
    private static readonly int[] ScaleChange = [-1, -1, -1, -1, 2, 4, 6, 8];
    private static readonly int[] Volume = [2, 3, 4, 5, 6, 8, 10, 12, 16, 20, 24, 32, 40, 48, 64, 80];
    /// <summary>
    /// 周波数モードからサンプルレートを返す。
    /// MDX の表記では MXDRV の F0〜F4 は遅い順に並ぶが、PCM8 のハードウェア分周表は逆順になっている。
    /// F4 は普通の PCM8 の曲で使う 15.625 kHz の既定値。
    /// 5（16ビット PCM）と6（8ビット PCM）も 15.625 kHz で鳴らす。
    /// </summary>
    internal static double Frequency(int mode) => mode switch
    {
        0 => 3906.25,
        1 => 5208.333333333333,
        2 => 7812.5,
        3 => 10416.666666666667,
        4 => 15625.0,
        5 or 6 => 15625.0,
        _ => 0
    };
    /// <summary>
    /// サンプルを16ビット PCM に変換する。
    /// モード0〜4は OKI ADPCM、5はビッグエンディアン16ビット、6は符号付き8ビット。
    /// </summary>
    internal static short[] Decode(ReadOnlySpan<byte> bytes, int mode)
    {
        int count = mode <= 4 ? bytes.Length * 2 : mode == 5 ? bytes.Length / 2 : mode == 6 ? bytes.Length : 0;
        var pcm = new short[count]; int signal = 0, scale = 0;
        for (int i = 0; i < count; i++)
        {
            // 16ビットと8ビットの PCM はそのまま広げる。
            if (mode == 5) { pcm[i] = BinaryPrimitives.ReadInt16BigEndian(bytes.Slice(i * 2, 2)); continue; }
            if (mode == 6) { pcm[i] = (short)((sbyte)bytes[i] << 8); continue; }
            // ADPCM は下位ニブルから順に復号する。
            int nibble = (bytes[i / 2] >> ((i & 1) * 4)) & 15;
            int step = DeltaTable[scale];
            int difference = (step >> 3) + ((nibble & 1) != 0 ? step >> 2 : 0) + ((nibble & 2) != 0 ? step >> 1 : 0) + ((nibble & 4) != 0 ? step : 0);
            signal = Math.Clamp(signal + ((nibble & 8) != 0 ? -difference : difference), -2047, 2047);
            scale = Math.Clamp(scale + ScaleChange[nibble & 7], 0, 48);
            pcm[i] = (short)((signal & ~3) << 4);
        }
        return pcm;
    }
    /// <summary>
    /// チャンネルで発音を始める。
    /// 同じサンプルの復号結果は使い回す。
    /// </summary>
    internal void Start(int channel, byte[] bank, int offset, int length, int mode, int pan, int volume)
    {
        Stop(channel);
        if ((uint)channel >= 8 || offset < 0 || length <= 0 || offset > bank.Length - length) return;
        var v = _voices[channel];
        if (!_samples.TryGetValue((offset, length, mode), out var pcm)) _samples[(offset, length, mode)] = pcm = Decode(bank.AsSpan(offset, length), mode);
        v.Samples = pcm; v.Position = 0; v.Step = Frequency(mode) / _outputRate; v.Pan = pan; v.Gain = Volume[Math.Clamp(volume, 0, 15)] / 16.0;
        v.Bank = bank; v.Offset = offset; v.Mode = mode;
    }

    /// <summary>
    /// 鳴っているのが1音だけで、その音の ADPCM を作り直さずにそのまま出せるときに、その音の情報を返す。
    /// 条件は、ADPCM（モード0〜4）で出力と同じサンプルレート、音量が等倍、左右の両方に出すこと。
    /// position は次に出すサンプルの番号（整数）、count はサンプル数。
    /// </summary>
    internal bool TryGetSoloAdpcm(out int channel, out int position, out int count)
    {
        channel = -1; position = 0; count = 0;
        for (int ch = 0; ch < 8; ch++)
        {
            var v = _voices[ch];
            if (v.Step <= 0 || v.Pan == 0 || v.Position >= v.Samples.Length || (MutedMask & (1 << ch)) != 0) continue;
            if (channel >= 0) return false;
            channel = ch;
        }
        if (channel < 0) return false;
        var solo = _voices[channel];
        if (solo.Mode > 4 || solo.Step != 1.0 || solo.Gain != 1.0 || solo.Pan != 3 || solo.Position != Math.Floor(solo.Position)) return false;
        position = (int)solo.Position; count = solo.Samples.Length;
        return true;
    }

    /// <summary>TryGetSoloAdpcm で選んだ音の、position 番目のサンプルの元の ADPCM 符号（下位ニブルから）。</summary>
    internal int AdpcmNibble(int channel, int position)
    {
        var v = _voices[channel];
        return (v.Bank[v.Offset + position / 2] >> ((position & 1) * 4)) & 15;
    }
    /// <summary>チャンネルの発音を止める。</summary>
    internal void Stop(int channel) { if ((uint)channel < 8) { _voices[channel].Samples = []; _voices[channel].Peak = 0; } }
    internal bool IsActive(int channel) => _voices[channel].Step > 0 && _voices[channel].Pan != 0 && _voices[channel].Position < _voices[channel].Samples.Length;
    internal double Level(int channel) => _voices[channel].Peak;
    internal int MutedMask { get; set; }
    /// <summary>
    /// 全チャンネルを左右の出力にミックスする。
    /// サンプル間は直線で補間し、レベルメーター用のピークも求める。
    /// 消音中のチャンネルもピークだけは測る。
    /// </summary>
    internal void Mix(int[][] output, int frames)
    {
        Array.Clear(output[0], 0, frames); Array.Clear(output[1], 0, frames);
        for (int ch = 0; ch < 8; ch++)
        {
            var v = _voices[ch]; v.Peak = 0;
            if (v.Step <= 0 || v.Pan == 0) continue;
            for (int i = 0; i < frames && v.Position < v.Samples.Length; i++, v.Position += v.Step)
            {
                int at = (int)v.Position; double fraction = v.Position - at;
                double sample = v.Samples[at] + (v.Samples[Math.Min(at + 1, v.Samples.Length - 1)] - v.Samples[at]) * fraction;
                int value = (int)Math.Round(sample * v.Gain); v.Peak = Math.Max(v.Peak, Math.Abs(value) / 32768.0);
                if ((MutedMask & (1 << ch)) != 0) continue;
                if ((v.Pan & 1) != 0) output[0][i] += value;
                if ((v.Pan & 2) != 0) output[1][i] += value;
            }
        }
    }
}
