using System.Numerics;

namespace MirrorFlux.Services.Music;

/// <summary>10 Hz から 44.1 kHz 出力のナイキスト周波数までを、対数で24帯域に分けたスペクトラム。</summary>
public sealed class AudioSpectrumAnalyzer
{
    public const int WindowSampleCount = 8192;
    private const int Size = WindowSampleCount;
    public const int BandCount = 24;
    public const double MinFrequency = 10;
    public const double MaxFrequency = 22050;
    private const double DisplayFloorDb = -60;
    private static readonly double[] HannWindow = CreateHannWindow();
    private readonly float[] _left = new float[Size], _right = new float[Size];
    private readonly object _sync = new();
    private readonly object _captureSync = new();
    private readonly Complex[] _fftLeft = new Complex[Size], _fftRight = new Complex[Size];
    private readonly float[] _result = new float[BandCount];
    private int _cursor;
    private long _totalSamplesAdded;
    private long _lastCapturedSamples = -1;
    private int _lastCapturedRate = -1;

    /// <summary>FFT 用のハン窓を作る。</summary>
    private static double[] CreateHannWindow()
    {
        var w = new double[Size];
        for (int i = 0; i < Size; i++)
            w[i] = .5 - .5 * Math.Cos(2 * Math.PI * i / (Size - 1));
        return w;
    }

    /// <summary>交互に並んだステレオのサンプルを、最新8192サンプル分の輪状バッファに足す。</summary>
    public void AddSamples(ReadOnlySpan<int> interleavedStereo)
    {
        lock (_sync)
        {
            for (int i = 0; i + 1 < interleavedStereo.Length; i += 2)
            {
                _left[_cursor] = Math.Clamp(interleavedStereo[i], short.MinValue, short.MaxValue) / 32768f;
                _right[_cursor] = Math.Clamp(interleavedStereo[i + 1], short.MinValue, short.MaxValue) / 32768f;
                _cursor = (_cursor + 1) % Size;
                _totalSamplesAdded++;
            }
        }
    }

    /// <summary>
    /// いまのバッファから24帯域のレベル（0〜1）を求める。
    /// 前回からサンプルが増えていなければ前回の結果を返す。
    /// </summary>
    public float[] Capture(int sampleRate = 44100)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        lock (_captureSync)
        {
            lock (_sync)
            {
                if (_lastCapturedRate == sampleRate && _lastCapturedSamples == _totalSamplesAdded)
                    return _result.ToArray();
                _lastCapturedRate = sampleRate;
                _lastCapturedSamples = _totalSamplesAdded;

                // 直流分を除いてハン窓を掛け、古い順に並べて FFT の入力にする。
                double leftMean = 0, rightMean = 0;
                for (int i = 0; i < Size; i++)
                {
                    leftMean += _left[i];
                    rightMean += _right[i];
                }
                leftMean /= Size;
                rightMean /= Size;
                for (int i = 0; i < Size; i++)
                {
                    double window = HannWindow[i];
                    int index = (_cursor + i) % Size;
                    _fftLeft[i] = new((_left[index] - leftMean) * window, 0);
                    _fftRight[i] = new((_right[index] - rightMean) * window, 0);
                }
            }
            // 左右を FFT し、帯域ごとに左右の大きいほうの振幅を集める。
            Transform(_fftLeft);
            Transform(_fftRight);
            Array.Clear(_result);
            double maxFrequency = Math.Min(MaxFrequency, sampleRate / 2.0);
            for (int band = 0; band < _result.Length; band++)
            {
                double low = MinFrequency * Math.Pow(maxFrequency / MinFrequency, band / (double)_result.Length);
                double high = MinFrequency * Math.Pow(maxFrequency / MinFrequency, (band + 1.0) / _result.Length);
                int first = Math.Clamp((int)Math.Ceiling(low * Size / sampleRate), 1, Size / 2 - 1);
                int last = Math.Clamp((int)Math.Ceiling(high * Size / sampleRate) - 1, first, Size / 2 - 1);
                double peak = 0, power = 0;
                for (int bin = first; bin <= last; bin++)
                {
                    double magnitude = Math.Max(_fftLeft[bin].Magnitude, _fftRight[bin].Magnitude) * 4 / Size;
                    peak = Math.Max(peak, magnitude);
                    power += magnitude * magnitude;
                }
                // 帯域が広いほど雑音のピークが高く出るので、RMS との差を使ってピークを補正する。
                int binCount = last - first + 1;
                double rms = Math.Sqrt(power / binCount);
                double noisePeakRatio = Math.Sqrt(Math.Max(1, Math.Log(binCount)));
                double correctedPeak = Math.Max(rms, peak - rms * (noisePeakRatio - 1));
                // dB に直し、-60 dB を0として0〜1に収める。
                double db = 20 * Math.Log10(Math.Max(correctedPeak, 1e-9));
                _result[band] = (float)Math.Clamp((db - DisplayFloorDb) / -DisplayFloorDb, 0, 1);
            }
            return _result.ToArray();
        }
    }

    /// <summary>その場で行う基数2の FFT。</summary>
    private static void Transform(Complex[] data)
    {
        // ビット反転の順に並べ替える。
        for (int i = 1, j = 0; i < Size; i++)
        {
            int bit = Size >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) (data[i], data[j]) = (data[j], data[i]);
        }
        // 長さを倍にしながらバタフライ演算を重ねる。
        for (int length = 2; length <= Size; length <<= 1)
        {
            var step = Complex.FromPolarCoordinates(1, -2 * Math.PI / length);
            for (int start = 0; start < Size; start += length)
            {
                var phase = Complex.One;
                for (int offset = 0; offset < length / 2; offset++)
                {
                    var even = data[start + offset];
                    var odd = data[start + offset + length / 2] * phase;
                    data[start + offset] = even + odd;
                    data[start + offset + length / 2] = even - odd;
                    phase *= step;
                }
            }
        }
    }
}
