namespace MirrorFlux.Services.Music;

/// <summary>波形から音の高さ（MIDI ノート番号）を推定する。</summary>
internal static class AudioPitchDetector
{
    /// <summary>
    /// 中心をそろえた波形の自己相関で最初の強い山を探し、その周期をノート番号に直す。
    /// 見つからなければ null を返す。
    /// </summary>
    internal static int? Estimate(ReadOnlySpan<double> wave, double sampleRate, double threshold, int minLag = 2, int lastLag = 200)
    {
        // ずらし幅ごとに相関を求め、閾値を超えた山（前後より高い点）が出たらそこを周期とする。
        double previous = -1, before = -1;
        for (int lag = 3; lag <= Math.Min(lastLag, wave.Length - 1); lag++)
        {
            double cross = 0, a = 0, b = 0;
            for (int i = lag; i < wave.Length; i++)
            {
                double x = wave[i], y = wave[i - lag];
                cross += x * y; a += x * x; b += y * y;
            }
            double correlation = a * b > 1e-12 ? cross / Math.Sqrt(a * b) : 0;
            if (lag - 1 >= minLag && previous > threshold && previous > before && previous >= correlation)
                return Math.Clamp((int)Math.Round(69 + 12 * Math.Log2(sampleRate / (lag - 1) / 440)), 0, 127);
            before = previous; previous = correlation;
        }
        return null;
    }
}
