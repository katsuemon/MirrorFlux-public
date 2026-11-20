using System.Buffers.Binary;
using NAudio.Wave;

namespace MirrorFlux.Services.Music;

/// <summary>
/// このプレイヤーの PCM だけ音量を変える。
/// Windows のデバイス音量には触れない。
/// </summary>
internal sealed class OutputGainWaveProvider(IWaveProvider source) : IWaveProvider
{
    private float _gain = 1f;

    public WaveFormat WaveFormat => source.WaveFormat;
    public void SetGain(float gain) => Volatile.Write(ref _gain, Math.Clamp(gain, 0f, 1f));

    public int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    /// <summary>元の音を読み、音量が1未満なら16ビットのサンプルごとに掛ける。</summary>
    public int Read(Span<byte> buffer)
    {
        int read = source.Read(buffer);
        float gain = Volatile.Read(ref _gain);
        if (gain >= 1f) return read;
        for (int i = 0; i + 1 < read; i += 2)
        {
            short sample = BinaryPrimitives.ReadInt16LittleEndian(buffer.Slice(i, 2));
            BinaryPrimitives.WriteInt16LittleEndian(buffer.Slice(i, 2), (short)Math.Round(sample * gain));
        }
        return read;
    }
}
