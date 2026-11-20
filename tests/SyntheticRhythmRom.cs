using System.IO;
using System.Reflection;
using MirrorFlux.Services.Music;

// Procedurally generated ADPCM test signal; contains no sampled chip/game data.
internal sealed class SyntheticRhythmRom : IDisposable
{
    private readonly string path;
    private readonly byte[]? original;
    public SyntheticRhythmRom()
    {
        var resolver = typeof(MdxWaveProvider).Assembly.GetType("MirrorFlux.Services.Music.Ym2608RhythmRom")!
            .GetMethod("ResolveDirectory", BindingFlags.Static | BindingFlags.NonPublic)!;
        string directory = (string)resolver.Invoke(null, [Environment.ProcessPath, AppContext.BaseDirectory])!;
        path = Path.Combine(directory, "YM2608_ADPCM.ROM");
        original = File.Exists(path) ? File.ReadAllBytes(path) : null;
        byte[] signal = new byte[8192];
        for (int i = 0; i < signal.Length; i++) signal[i] = (byte)((i / 16 % 2 == 0) ? 0x33 : 0xbb);
        File.WriteAllBytes(path, signal);
    }
    public void Dispose()
    {
        if (original is null) File.Delete(path);
        else File.WriteAllBytes(path, original);
    }
}
