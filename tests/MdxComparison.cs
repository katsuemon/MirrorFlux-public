using System.IO;
using MirrorFlux.Services.Music;
using NAudio.Wave;

internal static class MdxComparison
{
    internal static void Run(string path, string directory)
    {
        const int seconds = 165;
        Directory.CreateDirectory(directory);
        var file = MdxFile.Load(path);
        var core = new MxdrvCore(file.Data, file.PdxData);
        var offThisTick = new bool[8];
        int sameTickRetriggers = 0;
        core.WriteOpmCallback = (reg,value) => {
            if (reg != 8) return;
            int channel = value & 7;
            if ((value & 0x78) == 0) offThisTick[channel] = true;
            else if (offThisTick[channel]) { sameTickRetriggers++; offThisTick[channel] = false; }
        };
        var mixer = new PdxPcmMixer();
        core.PcmStopCallback = mixer.Stop;
        using var log = new StreamWriter(Path.Combine(directory, "pcm.csv"));
        log.WriteLine("seconds,channel,offset,length,mode,pan,volume");
        int frame = 0;
        core.PcmOutCallback = (ch, off, len, mode, pv) => {
            log.WriteLine($"{frame / 44100.0:F6},{ch},{off},{len},{mode},{pv >> 8},{pv & 255}");
            mixer.Start(ch, file.PdxData!, off, len, mode, pv >> 8, pv & 255);
        };
        using var pcm = new WaveFileWriter(Path.Combine(directory, "pcm.wav"), new WaveFormat(44100,16,2));
        double nextTick = 0;
        int[][] output = [new int[1], new int[1]];
        for (; frame < seconds * 44100; frame++)
        {
            if (frame >= nextTick) { Array.Clear(offThisTick); core.Tick(); nextTick += 44100 * 1024.0 * (256-core.Tempo)/4000000; }
            mixer.Mix(output,1);
            pcm.WriteSample(Math.Clamp(output[0][0]/32768f,-1,1));
            pcm.WriteSample(Math.Clamp(output[1][0]/32768f,-1,1));
        }
        using var provider = new MdxWaveProvider(path);
        using var full = new WaveFileWriter(Path.Combine(directory,"full.wav"),provider.WaveFormat);
        var buffer = new byte[44100*4];
        for(int i=0;i<seconds;i++) { provider.Read(buffer,0,buffer.Length); full.Write(buffer,0,buffer.Length); }
        using var fmProvider = new MdxWaveProvider(path);
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var fmCore = (MxdrvCore)typeof(MdxWaveProvider).GetField("_core",flags)!.GetValue(fmProvider)!;
        var fmMixer = (PdxPcmMixer)typeof(MdxWaveProvider).GetField("_pcmMixer",flags)!.GetValue(fmProvider)!;
        fmCore.PcmOutCallback = null;
        for (int ch = 0; ch < 8; ch++) fmMixer.Stop(ch);
        using var fm = new WaveFileWriter(Path.Combine(directory,"fm.wav"),fmProvider.WaveFormat);
        for(int i=0;i<seconds;i++) { fmProvider.Read(buffer,0,buffer.Length); fm.Write(buffer,0,buffer.Length); }
        Console.WriteLine($"Rendered {seconds} seconds; PDX="+file.PdxResolvedPath);
        Console.WriteLine($"Same-tick FM off/on pairs: {sameTickRetriggers}");
    }
}
