using System.IO;
using MirrorFlux.Services.Hardware;
using MirrorFlux.Services.Music;

/// <summary>
/// 実曲の MDX で、ND8 へ送る PCM8 のミックスを OKIM6258 の ADPCM にしたときの誤差（SN比）と、
/// 信号値が12ビットの範囲を越える（実機が値を止めるか折り返すかで結果が分かれる）回数を数える（調査用）。
/// 使い方: --mdx-nd8-stats &lt;MDXファイル&gt;...
/// </summary>
internal static class MdxNd8Stats
{
    /// <summary>
    /// ND8 へ送る PCM8 を WAV（15.625 kHz・モノラル）に書き出す。
    /// *-mix.wav は ADPCM にする前のミックス、*-nd8.wav は送るストリームを OKIM6258 と同じ手順で復号したもの。
    /// </summary>
    public static void WriteWaves(string path, string outputFolder, double seconds)
    {
        var file = MdxFile.Load(path);
        var renderer = new MdxNd8Renderer(file.Data, file.PdxData);
        var mix = new List<short>();
        renderer.EncodeTrace = (a, b) => { mix.Add((short)(a << 4)); mix.Add((short)(b << 4)); };
        var decoded = new List<short>();
        var decoder = new OkiAdpcmCodec(); decoder.Reset();
        ulong limit = (ulong)(seconds * 1_000_000);
        while (!renderer.Finished && renderer.TimeUs < limit)
            foreach (byte value in renderer.Next().Data)
            {
                decoded.Add((short)(decoder.Decode(value & 15) << 4));
                decoded.Add((short)(decoder.Decode(value >> 4) << 4));
            }
        Directory.CreateDirectory(outputFolder);
        string name = Path.GetFileNameWithoutExtension(path);
        WriteWave(Path.Combine(outputFolder, name + "-mix.wav"), mix);
        WriteWave(Path.Combine(outputFolder, name + "-nd8.wav"), decoded);
        Console.WriteLine($"{name}: {mix.Count / 15625.0:0.0} s -> {outputFolder}");
    }

    private static void WriteWave(string path, List<short> samples)
    {
        using var w = new BinaryWriter(File.Create(path));
        int bytes = samples.Count * 2;
        w.Write("RIFF"u8); w.Write(36 + bytes); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(15625); w.Write(15625 * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(bytes);
        foreach (short s in samples) w.Write(s);
    }

    public static void Run(IEnumerable<string> paths, double seconds)
    {
        foreach (string path in paths)
        {
            MdxFile file;
            try { file = MdxFile.Load(path); } catch (Exception ex) { Console.WriteLine($"SKIP {Path.GetFileName(path)}: {ex.Message}"); continue; }
            if (file.PdxData == null) continue;
            var renderer = new MdxNd8Renderer(file.Data, file.PdxData);
            var targets = new List<int>();
            renderer.EncodeTrace = (a, b) => { targets.Add(a); targets.Add(b); };
            ulong limit = (ulong)(seconds * 1_000_000);
            var stream = new List<int>();
            while (!renderer.Finished && renderer.TimeUs < limit)
                foreach (byte value in renderer.Next().Data) { stream.Add(value & 15); stream.Add(value >> 4); }

            // 送るストリームを復号して、ミックスとの誤差と、12ビットの範囲を越えた回数を数える。
            var codec = new OkiAdpcmCodec(); codec.Reset();
            double signal = 0, error = 0, plainError = 0; long overflow = 0, clipped = 0;
            var plain = new OkiAdpcmCodec(); plain.Reset();
            for (int i = 0; i < targets.Count && i < stream.Count; i++)
            {
                int target = targets[i];
                if (codec.Unclamped(stream[i]) is < -2048 or > 2047) overflow++;
                codec.Decode(stream[i]);
                plain.Encode(target);
                if (target is >= 2047 or <= -2048) clipped++;
                signal += (double)target * target;
                error += (double)(target - codec.Signal) * (target - codec.Signal);
                plainError += (double)(target - plain.Signal) * (target - plain.Signal);
            }
            Console.WriteLine($"{Path.GetFileName(path),-16} SNR {10 * Math.Log10(signal / Math.Max(1, error)),5:0.0} dB (all re-encoded {10 * Math.Log10(signal / Math.Max(1, plainError)),5:0.0})  passed {100.0 * renderer.PassedSamples / Math.Max(1, stream.Count),5:0.0}%  overflow {overflow}  mix clipped {clipped:N0}");
        }
    }
}
