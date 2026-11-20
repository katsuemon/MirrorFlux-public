using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using NAudio.Wave;

namespace MirrorFlux.Services.Music;

/// <summary>
/// 一般の音声ファイル（.mp3, .wav, .flac, .m4a, .wma, .aac）を16ビットステレオの PCM に変換し、サウンドバーと共通の24帯域 FFT スペクトラムにも流す。
/// </summary>
public sealed class AudioFileWaveProvider : IPlaybackWaveProvider
{
    private readonly object _sync = new();
    private readonly WaveStream _stream;
    private readonly ISampleProvider _sampleProvider;
    private readonly int _sourceChannels;
    private readonly AudioSpectrumAnalyzer _spectrum = new();
    private float[] _floatBuffer = [];
    private int[] _spectrumInput = [];
    private long _renderedFrames;
    private bool _disposed;

    public WaveFormat WaveFormat { get; }
    public string Title { get; }
    public string FormatName { get; }
    public TimeSpan TotalTime { get; }
    public bool IsPaused { get; set; }
    public bool Ended { get; private set; }

    /// <summary>
    /// ファイルを開く。
    /// NAudio の AudioFileReader で開けなければ Media Foundation で開く。
    /// </summary>
    public AudioFileWaveProvider(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Audio file not found.", path);

        Title = Path.GetFileNameWithoutExtension(path);
        FormatName = Path.GetExtension(path).TrimStart('.').ToUpperInvariant();

        try
        {
            var reader = new AudioFileReader(path);
            _stream = reader;
            _sampleProvider = reader;
        }
        catch
        {
            var mfReader = new MediaFoundationReader(path);
            _stream = mfReader;
            _sampleProvider = mfReader.ToSampleProvider();
        }

        // 出力は元のサンプルレートの16ビットステレオにする。
        _sourceChannels = Math.Max(1, _sampleProvider.WaveFormat.Channels);
        int sampleRate = Math.Max(8000, _sampleProvider.WaveFormat.SampleRate);
        WaveFormat = new WaveFormat(sampleRate, 16, 2);
        TotalTime = _stream.TotalTime;
    }

    /// <summary>
    /// 指定バイト数の PCM を返す。
    /// 一時停止中は無音で埋め、ファイルの終わりに達したら残りを無音にして終了にする。
    /// </summary>
    public int Read(byte[] buffer, int offset, int count)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            int frames = count / 4;
            if (frames <= 0 || Ended)
            {
                return 0;
            }

            if (IsPaused)
            {
                Array.Clear(buffer, offset, frames * 4);
                return frames * 4;
            }

            // 作業用バッファを必要な大きさにそろえる。
            int samplesNeeded = frames * _sourceChannels;
            if (_floatBuffer.Length < samplesNeeded)
            {
                _floatBuffer = new float[samplesNeeded];
            }
            if (_spectrumInput.Length < frames * 2)
            {
                _spectrumInput = new int[frames * 2];
            }

            // 浮動小数点のサンプルを必要な数だけ読む。読めなくなったら終了にする。
            int samplesRead = 0;
            try
            {
                while (samplesRead < samplesNeeded)
                {
                    int read = _sampleProvider.Read(_floatBuffer.AsSpan(samplesRead, samplesNeeded - samplesRead));
                    if (read <= 0)
                    {
                        break;
                    }
                    samplesRead += read;
                }
            }
            catch
            {
                Ended = true;
                return 0;
            }

            int framesRead = samplesRead / _sourceChannels;
            if (framesRead <= 0)
            {
                Ended = true;
                return 0;
            }

            // 16ビットのステレオに変換し、スペクトラム用にも控える。モノラルは左右に同じ値を出す。
            for (int i = 0; i < framesRead; i++)
            {
                int baseIndex = i * _sourceChannels;
                float leftFloat = _floatBuffer[baseIndex];
                float rightFloat = _sourceChannels > 1 ? _floatBuffer[baseIndex + 1] : leftFloat;

                int left = (int)Math.Round(Math.Clamp(leftFloat, -1f, 1f) * 32767f);
                int right = (int)Math.Round(Math.Clamp(rightFloat, -1f, 1f) * 32767f);

                BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(offset + i * 4, 2), (short)left);
                BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(offset + i * 4 + 2, 2), (short)right);

                _spectrumInput[i * 2] = left;
                _spectrumInput[i * 2 + 1] = right;
            }

            // スペクトラムに渡して再生位置を進める。
            _spectrum.AddSamples(_spectrumInput.AsSpan(0, framesRead * 2));
            _renderedFrames += framesRead;

            if (framesRead < frames)
            {
                Array.Clear(buffer, offset + framesRead * 4, (frames - framesRead) * 4);
                Ended = true;
            }

            return frames * 4;
        }
    }

    /// <summary>
    /// Span 版の Read。
    /// 借りた配列で読んでから写す。
    /// </summary>
    public int Read(Span<byte> buffer)
    {
        byte[] rented = ArrayPool<byte>.Shared.Rent(buffer.Length);
        try
        {
            int read = Read(rented, 0, buffer.Length);
            rented.AsSpan(0, read).CopyTo(buffer);
            return read;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public bool CanSeek { get { lock (_sync) return !_disposed && _stream.CanSeek; } }

    /// <summary>
    /// 指定位置へ移る。
    /// 範囲外は曲の長さに収める。
    /// </summary>
    public bool Seek(TimeSpan position)
    {
        lock (_sync)
        {
            if (_disposed || !_stream.CanSeek) return false;
            double totalSeconds = Math.Max(0, TotalTime.TotalSeconds);
            double targetSeconds = totalSeconds > 0
                ? Math.Clamp(position.TotalSeconds, 0, totalSeconds)
                : Math.Max(0, position.TotalSeconds);
            try
            {
                _stream.CurrentTime = TimeSpan.FromSeconds(targetSeconds);
                _renderedFrames = (long)Math.Round(_stream.CurrentTime.TotalSeconds * WaveFormat.SampleRate);
                Ended = false;
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// 表示用の状態を返す。
    /// チャンネルの代わりにスペクトラムだけを見せる。
    /// </summary>
    public MusicPlaybackSnapshot Snapshot()
    {
        lock (_sync)
        {
            return new MusicPlaybackSnapshot
            {
                IsPlaying = !Ended,
                IsPaused = IsPaused,
                Title = Title,
                SystemName = "PCM AUDIO",
                FormatName = FormatName,
                FileTypeDisplay = FormatName switch
                {
                    "WAV" => $"WAV (PCM {WaveFormat.SampleRate}Hz {(WaveFormat.Channels == 1 ? "Mono" : "Stereo")})",
                    "MP3" => "MP3 (MPEG Audio)",
                    "FLAC" => "FLAC (Free Lossless Audio)",
                    "OGG" => "OGG (Ogg Vorbis Audio)",
                    "M4A" => "M4A (AAC Audio)",
                    _ => $"{FormatName} Audio"
                },
                SoundSourceDisplay = "PCM (Stream)",
                CurrentTime = TimeSpan.FromSeconds(_renderedFrames / (double)WaveFormat.SampleRate),
                TotalTime = TotalTime,
                Channels = [],
                RegisterBanks = [],
                SpectrumLevels = _spectrum.Capture(WaveFormat.SampleRate),
                SpectrumKind = "AUDIO FFT",
                UseSpectrumOnly = true
            };
        }
    }

    /// <summary>ファイルを閉じて終了にする。</summary>
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            Ended = true;
            _stream.Dispose();
        }
    }
}
