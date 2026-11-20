namespace MirrorFlux.Services.Music;

/// <summary>NES の矩形波・FDS の波形を、波形表示用の32点の波形にする。</summary>
internal static class NesWaveform
{
    /// <summary>デューティ設定の割合（%）を文字列で返す。</summary>
    internal static string DutyPercent(int duty) => duty switch
    {
        0 => "12.5", 1 => "25.0", 2 => "50.0", _ => "75.0"
    };
    internal static string DutyLabel(int duty) => $"Duty {DutyPercent(duty)}% : ";

    /// <summary>デューティ設定から32点の矩形波を作る。</summary>
    internal static byte[] Pulse(int duty)
    {
        int high = duty switch { 0 => 4, 1 => 8, 2 => 16, _ => 24 };
        var samples = new byte[32];
        for (int i = 0; i < high; i++) samples[i] = 255;
        return samples;
    }

    /// <summary>FDS の波形メモリ（64点）を32点に縮める。</summary>
    internal static byte[] Fds(ReadOnlySpan<byte> waveRam)
    {
        var samples = new byte[32];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = (byte)(((waveRam[i * 2] & 63) + (waveRam[i * 2 + 1] & 63)) * 255 / 126);
        return samples;
    }

    /// <summary>
    /// 単独で取り出した音声から波形を推定する。
    /// GME は NSF の各音を単独で出せるが、FDS の RAM や APU のレジスタは見せないので、音声から作る。
    /// </summary>
    internal static byte[]? FromIsolatedAudio(ReadOnlySpan<double> wave, bool pulse)
    {
        if (wave.Length < 64) return null;
        double low = double.MaxValue, high = double.MinValue;
        for (int i = 0; i < wave.Length; i++)
        {
            low = Math.Min(low, wave[i]); high = Math.Max(high, wave[i]);
        }
        // 振れ幅が小さすぎれば表示しない。
        if (high - low < .004) return null;
        // 矩形波は高い側の割合から一番近いデューティを選ぶ。
        if (pulse)
        {
            double threshold = (low + high) / 2;
            int highCount = 0;
            foreach (double sample in wave) if (sample > threshold) highCount++;
            double fraction = (double)highCount / wave.Length;
            double[] dutyFractions = [.125, .25, .5, .75];
            int nearest = Enumerable.Range(0, 4).MinBy(i => Math.Abs(fraction - dutyFractions[i]));
            return Pulse(nearest);
        }

        // それ以外は周期を求め、立ち上がりの位置から1周期を32点に補間する。
        int period = FindPeriod(wave);
        if (period == 0) return null;
        double midpoint = (low + high) / 2;
        int start = wave.Length - period * 2;
        for (int i = Math.Max(1, start); i < wave.Length - period; i++)
            if (wave[i - 1] < midpoint && wave[i] >= midpoint) start = i;
        start = Math.Clamp(start, 0, wave.Length - period - 1);
        var result = new byte[32];
        for (int i = 0; i < result.Length; i++)
        {
            double position = start + i * period / 32.0;
            int index = (int)position;
            double value = wave[index] + (wave[index + 1] - wave[index]) * (position - index);
            result[i] = (byte)Math.Clamp((int)Math.Round((value - low) * 255 / (high - low)), 0, 255);
        }
        return result;
    }

    /// <summary>
    /// 自己相関で波形の周期を求める。
    /// 見つからなければ0を返す。
    /// </summary>
    private static int FindPeriod(ReadOnlySpan<double> wave)
    {
        double previous = -1, before = -1;
        for (int lag = 3; lag <= Math.Min(128, wave.Length / 2); lag++)
        {
            double cross = 0, a = 0, b = 0;
            for (int i = lag; i < wave.Length; i++)
            {
                double x = wave[i], y = wave[i - lag];
                cross += x * y; a += x * x; b += y * y;
            }
            double correlation = a * b > 1e-12 ? cross / Math.Sqrt(a * b) : 0;
            if (lag > 3 && previous > .82 && previous > before && previous >= correlation)
                return lag - 1;
            before = previous; previous = correlation;
        }
        return 0;
    }
}
