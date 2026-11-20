using System.Buffers.Binary;
using MirrorFlux.Services.Music;
using MirrorFlux.Services.Vgm;

/// <summary>
/// DACを多用するVGMでも、鍵盤・レベル（時間軸）とパン・レジスタ（スナップショット）が、
/// 音声出力のバッファの分だけ先に進んだ合成位置ではなく、聞こえている位置の状態を出すことの検査。
/// </summary>
internal static class VgmDisplayTimingRegression
{
    private const int Rate = 44100;

    private static byte[] DacHeavyVgm()
    {
        // YM2612のch0をキーオンし、1サンプルごとにDACへ書きながら1秒進め、そこでパンを左だけにしてキーオフする。
        var commands = new List<byte>();
        int samples = Rate + Rate / 2;
        commands.AddRange([0x67, 0x66, 0x00]);
        commands.AddRange(BitConverter.GetBytes(samples));
        for (int i = 0; i < samples; i++) commands.Add((byte)(128 + (i & 31)));
        commands.AddRange([0x52, 0x2b, 0x80, 0x52, 0xb4, 0xc0, 0x52, 0xa4, 0x22, 0x52, 0xa0, 0x69, 0x52, 0x40, 0x00, 0x52, 0x28, 0xf0]);
        for (int i = 0; i < Rate; i++) commands.Add(0x81);
        commands.AddRange([0x52, 0xb4, 0x40, 0x52, 0x28, 0x00]);
        for (int i = 0; i < Rate / 2; i++) commands.Add(0x81);
        byte[] data = new byte[256];
        "Vgm "u8.CopyTo(data);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), 0x171);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x34), 256 - 0x34);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x2c), 7670453);
        data = [.. data, .. commands, 0x66];
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), (uint)data.Length - 4);
        return data;
    }

    public static void Run(Action<bool, string> check)
    {
        using var provider = new IndependentVgmWaveProvider(VgmFile.Load(DacHeavyVgm(), "dac-heavy.vgm")!);
        // WaveOutの既定（300ms・2バッファ）と同じ150msずつ合成し、1.35秒まで進める。
        byte[] buffer = new byte[6615 * 4];
        for (int i = 0; i < 9; i++) provider.Read(buffer, 0, buffer.Length);

        double before = Rate * 0.95, after = Rate * 1.05;
        var early = provider.GetSnapshot(before);
        var late = provider.GetSnapshot(after);
        int index = Array.FindIndex(early.MusicChannels, c => c.Id.EndsWith(":FM:0", StringComparison.Ordinal) && c.ChipName.StartsWith("YM2612", StringComparison.Ordinal));
        check(index >= 0, "DAC-heavy VGM exposes the YM2612 FM channel 0");
        if (index < 0) return;
        check(Math.Abs(early.CurrentTime.TotalSeconds - 0.95) < 0.02 && Math.Abs(late.CurrentTime.TotalSeconds - 1.05) < 0.02,
            "VGM snapshot is taken at the audible position, not the rendered position");
        check(early.MusicChannels[index].Pan != late.MusicChannels[index].Pan,
            "VGM pan display changes at the audible position of the pan write");

        var states = new ChannelRealtimeState[PitchTimeline.MaxChannels];
        check(provider.Timeline.SampleAt(before, states) > index && states[index].IsKeyOn,
            "VGM keyboard still shows the note 300ms behind the rendered position of a DAC-heavy song");
        check(provider.Timeline.SampleAt(after, states) > index && !states[index].IsKeyOn,
            "VGM keyboard shows the key-off at its audible position");
    }
}