using System.Buffers.Binary;
using System.IO;
using MirrorFlux.Services.Music;
using MirrorFlux.Services.Vgm;

internal static class FmCoreRegression
{
    public static void Audit(string path)
    {
        if (Path.GetExtension(path).Equals(".zip",StringComparison.OrdinalIgnoreCase)) { VgmChipRegression.Audit(path); return; }
        using var p = new MdxWaveProvider(path);
        byte[] buffer = new byte[4096]; long energy = 0; int pcm = 0, fm = 0;
        for (int i=0;i<1292;i++) {
            p.Read(buffer,0,buffer.Length); energy += buffer.Sum(b=>(long)b);
            var s = p.Snapshot(); pcm += s.Channels.Count(c=>c.Kind==MusicChannelKind.Pcm && c.IsKeyOn); fm += s.Channels.Count(c=>c.Kind==MusicChannelKind.Fm && c.IsKeyOn);
        }
        if (energy == 0 || fm == 0 || pcm == 0) throw new Exception("MDX FM/PDX activity missing");
        Console.WriteLine($"PASS ymfm MDX 30 seconds: FM activity {fm}, PCM activity {pcm}, nonzero audio");
        MdxComparison.Run(path,Path.Combine(Environment.CurrentDirectory,".dotnet","ymfm-comparison"));
    }
    private static void Put(byte[] b, int p, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(p), v);
    private static byte[] Vgm(int field, uint clock, IEnumerable<byte> commands)
    {
        byte[] data = new byte[256]; "Vgm "u8.CopyTo(data); Put(data, 8, 0x171); Put(data, 0x34, 256 - 0x34); Put(data, field, clock);
        data = [..data, ..commands, 0x61, 0xff, 0x7f, 0x66]; Put(data, 4, (uint)data.Length - 4); return data;
    }
    public static void Run()
    {
        using var syntheticRhythm = new SyntheticRhythmRom();
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS ymfm: " + name); }
        VerifyRetrigger(Check);
        OpnFmRegression.Run(Check);
        OpnMixRegression.Run(Check);
        FmCoreDisplayRegression.Run();
        var resolver = typeof(MdxWaveProvider).Assembly.GetType("MirrorFlux.Services.Music.Ym2608RhythmRom")!
            .GetMethod("ResolveDirectory",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic)!;
        Check((string)resolver.Invoke(null,[@"C:\Music\mf.exe",@"C:\Temp\.net\mf\hash\"])! == @"C:\Music",
            "self-extracting player locates ROM beside original EXE");
        Check((string)resolver.Invoke(null,[@"C:\Program Files\dotnet\dotnet.exe",@"C:\Tests\"])! == @"C:\Tests\",
            "dotnet-hosted tests locate ROM beside application assembly");
        byte[] Render(byte[] data, int chunk = 1024)
        {
            using var p = new IndependentVgmWaveProvider(VgmFile.Load(data)!);
            Check(p.GetSnapshot().FmCoreName == "ymfm", "snapshot reports actual selected core");
            byte[] audio = new byte[8192 * 4];
            for (int i = 0; i < 8192; i += chunk) p.Read(audio, i * 4, Math.Min(chunk, 8192 - i) * 4);
            return audio;
        }
        foreach (var (field, clock) in new (int,uint)[] {(0x30,4000000), (0x44,4000000), (0x48,8000000), (0x4c,8000000), (0x4c,0x80000000u|8000000), (0x2c,7670454), (0x10,3579545), (0x54,3579545), (0x58,3579545), (0x50,3579545), (0x5c,14318180)})
            Render(Vgm(field, clock, []));
        byte[] opn = Vgm(0x44, 4000000, [0x55,7,0x3e,0x55,0,254,0x55,1,0,0x55,8,15]);
        Check(Render(opn).Any(x => x != 0), "YM2203 SSG audio");
        // FM CORE欄はソフトで鳴らしているYamahaのFM音源だけ。実機はEXT DEVICE欄に短い名前で出す。
        Check(IndependentVgmWaveProvider.FmCoreName([(0, null), (2, null)]) == "ymfm" &&
            IndependentVgmWaveProvider.FmCoreName([(0, null), (4, null), (18, null)]) == "—" &&
            IndependentVgmWaveProvider.FmCoreName([(3, null), (23, "NanoDrive 8")]) == "ymfm" &&
            IndependentVgmWaveProvider.FmCoreName([(0, "NanoDrive6"), (2, "NanoDrive6"), (3, "NanoDrive 8")]) == "—" &&
            IndependentVgmWaveProvider.FmCoreName([(2, "NanoDrive6"), (2, null), (14, null)]) == "ymfm + mf_chips",
            "FM core lists only Yamaha FM chips synthesized in software");
        Check(IndependentVgmWaveProvider.ExtDeviceName([null, null]) == "—" &&
            IndependentVgmWaveProvider.ExtDeviceName(["NanoDrive 8", null, "NanoDrive6", "NanoDrive6"]) == "ND6 ND8" &&
            IndependentVgmWaveProvider.ExtDeviceName(["NanoDrive 7", "NanoDrive 8", "NanoDrive6"]) == "ND6 ND7 ND8",
            "External device lists each board in use once, by number, with short names");
        using (var psgOnly = new IndependentVgmWaveProvider(VgmFile.Load(Vgm(0x0c, 3579545, []))!))
            Check(psgOnly.GetSnapshot().FmCoreName == "—" && psgOnly.GetSnapshot().ExtDeviceName == "—",
                "PSG-only VGM in software shows no FM core and no external device");
        void VerifyMask(byte[] data, string id)
        {
            using var player = new IndependentVgmWaveProvider(VgmFile.Load(data)!);
            byte[] samples = new byte[2048 * 4];
            int Range()
            {
                player.Read(samples, 0, samples.Length);
                var mono = Enumerable.Range(128, 1920).Select(n => (int)BinaryPrimitives.ReadInt16LittleEndian(samples.AsSpan(n * 4))).ToArray();
                return mono.Max() - mono.Min();
            }
            int before = Range();
            var voice = player.GetSnapshot().MusicChannels.Single(c => c.Id == id);
            Check(player.SetChannelMuted(voice, true), $"ymfm accepts {id} mask");
            int muted = Range();
            Check(player.SetChannelMuted(voice, false), $"ymfm releases {id} mask");
            int after = Range();
            Check(before > 30 && muted <= 2 && after > 30, $"{id} audio mask {before}/{muted}/{after}");
        }
        VerifyMask(opn, "YM2203:SSG:0");
        Check(Render(opn).SequenceEqual(Render(opn, chunk:127)), "callback size independent");
        var opm = new List<byte>();
        var score = new MxdrvCore(MdxRegression.CreateTestMdx(),null);
        score.WriteOpmCallback = (r,v) => opm.AddRange([0x54,(byte)r,v]); score.Tick();
        Check(Render(Vgm(0x30,4000000,opm)).Any(x=>x!=0),"VGM YM2151 FM audio");
        var opnFm = new List<byte>();
        void F(byte r,byte v) => opnFm.AddRange([0x52,r,v]);
        F(0xb0,7); F(0xb4,0xc0);
        foreach(byte slot in new byte[]{0,4,8,12}) { F((byte)(0x30+slot),1); F((byte)(0x40+slot),0x18); F((byte)(0x50+slot),0x1f); F((byte)(0x80+slot),0x0f); }
        F(0xa4,0x22); F(0xa0,0x69); F(0x28,0xf0);
        VerifyMask(Vgm(0x2c,7670454,opnFm), "YM2612:FM:0");
        Check(Render(Vgm(0x2c,7670454,opnFm)).Any(x=>x!=0),"VGM YM2612 FM audio");
        var dac = new List<byte>{0x52,0xb6,0xc0,0x52,0x2b,0x80};
        for (int i=0;i<1024;i++) dac.AddRange([0x52,0x2a,(byte)((i&8)==0?32:224),0x7f]);
        Check(Render(Vgm(0x2c,7670454,dac)).Any(x=>x!=0),"YM2612 DAC audio");
        foreach(var chip in new[]{(0x50,(byte)0x5a,3579545u),(0x54,(byte)0x5b,3579545u),(0x58,(byte)0x5c,3579545u),(0x5c,(byte)0x5e,14318180u)}) {
            var tone = new List<byte>();
            foreach(var rv in new (byte,byte)[]{(0x20,1),(0x23,1),(0x40,0x20),(0x43,0),(0x60,0xf0),(0x63,0xf0),(0x80,0x0f),(0x83,0x0f),(0xc0,0x31),(0xa0,0x80),(0xb0,0x31)}) tone.AddRange([chip.Item2,rv.Item1,rv.Item2]);
            Check(Render(Vgm(chip.Item1,chip.Item3,tone)).Any(x=>x!=0),"OPL FM audio " + chip.Item1.ToString("X"));
        }
        Check(Render(Vgm(0x44, 0x40000000 | 4000000, [0xa5,7,0x3e,0xa5,0,254,0xa5,1,0,0xa5,8,15])).Any(x=>x!=0), "second YM2203 audio");
        foreach(bool second in new[]{false,true})
        for(byte drum=0;drum<6;drum++) {
            byte command=second?(byte)0xa6:(byte)0x56;
            byte[] output=Render(Vgm(0x48,8000000u|(second?0x40000000u:0),
                [command,0x11,63,command,(byte)(0x18+drum),0xdf,command,0x10,(byte)(1<<drum)]));
            int peak=0;
            for(int i=0;i<output.Length;i+=2) peak=Math.Max(peak,Math.Abs((int)BinaryPrimitives.ReadInt16LittleEndian(output.AsSpan(i))));
            Check(peak>1000,$"ymfm YM2608 #{(second?2:1)} external ROM rhythm {drum} has audible amplitude");
        }
        // Exercise both dotnet-hosted and published/self-extracting test executables.
        string directory = (string)resolver.Invoke(null,[Environment.ProcessPath,AppContext.BaseDirectory])!;
        string romPath = Path.Combine(directory,"YM2608_ADPCM.ROM");
        byte[] rhythmRom = File.ReadAllBytes(romPath);
        try
        {
            {
                File.Delete(romPath);
                byte[] rhythmOnly = Vgm(0x48,8000000,[0x56,0x11,63,0x56,0x18,0xdf,0x56,0x10,1]);
                Check(Render(rhythmOnly).All(x=>x==0),"ymfm"+" missing ROM silences rhythm");
                File.WriteAllBytes(romPath,[1,2,3]);
                Check(Render(rhythmOnly).All(x=>x==0),"ymfm"+" invalid ROM silences rhythm");
                File.WriteAllBytes(romPath,rhythmRom);
                Check(Render(rhythmOnly).Any(x=>x!=0),"ymfm"+" external ROM restores rhythm");
            }
        }
        finally { File.WriteAllBytes(romPath,rhythmRom); }
        foreach (bool upload in new[]{false,true})
        {
            var commands = new List<byte>();
            void W(byte reg, byte value) => commands.AddRange([0x57,reg,value]);
            W(0,1); W(1,0x82); W(2,0); W(3,0); W(4,31); W(5,0); W(0x0c,0xff); W(0x0d,0xff);
            if (upload) { W(0,0x60); for(int i=0;i<1024;i++) W(8,0x17); W(0,1); }
            else {
                byte[] rom = new byte[8+1024]; Put(rom,0,262144); Array.Fill(rom,(byte)0x17,8,1024);
                commands.AddRange([0x67,0x66,0x81]); commands.AddRange(BitConverter.GetBytes(rom.Length)); commands.AddRange(rom);
            }
            W(9,0xff); W(10,0xff); W(11,0xff); W(0,0xa0);
            Check(Render(Vgm(0x48,8000000,commands)).Any(x=>x!=0), "YM2608 ADPCM " + (upload?"register upload":"ROM block"));
        }
        // Both independent Neo Geo ADPCM buses must receive their VGM ROM blocks.
        foreach (bool b in new[]{false,true})
        {
            var commands = new List<byte>();
            byte[] rom = new byte[8+4096]; Put(rom,0,4096); Array.Fill(rom,(byte)0x17,8,4096);
            commands.AddRange([0x67,0x66,(byte)(b?0x83:0x82)]); commands.AddRange(BitConverter.GetBytes(rom.Length)); commands.AddRange(rom);
            void W(byte reg, byte value) => commands.AddRange([(byte)(b?0x58:0x59),reg,value]);
            if (b) { W(0x11,0xc0); W(0x12,0); W(0x13,0); W(0x14,0xff); W(0x15,0); W(0x19,0xff); W(0x1a,0xff); W(0x1b,0xff); W(0x10,0x80); }
            else { W(1,0x3f); W(8,0xdf); W(0x10,0); W(0x18,0); W(0x20,0xf); W(0x28,0); W(0,1); }
            Check(Render(Vgm(0x4c,8000000,commands)).Any(x=>x!=0), "YM2610 ADPCM " + (b?"B":"A"));
        }
        byte[] s98 = new byte[48]; "S983"u8.CopyTo(s98); Put(s98,4,1); Put(s98,8,100); Put(s98,20,48); Put(s98,28,1); Put(s98,32,2); Put(s98,36,4000000);
        s98 = [..s98,0,7,0x3e,0,0,254,0,1,0,0,8,15,..Enumerable.Repeat((byte)0xff,30),0xfd];
        Check(Render(S98Decoder.Convert(s98,"test")).Any(x=>x!=0), "S98 converted stream uses ymfm");
        string path = Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".mdx");
        try {
            File.WriteAllBytes(path,MdxRegression.CreateTestMdx());
            byte[] a = new byte[44100*4], b = new byte[a.Length];
            using var p = new MdxWaveProvider(path);
            using var q = new MdxWaveProvider(path);
            Check(p.Snapshot().FmCoreName == "ymfm", "MDX reports selected core");
            Check(p.Snapshot().Channels[0].IsInUse,"MDX keyboard telemetry retained");
            p.Read(a,0,a.Length);
            for (int i=0;i<b.Length;i+=508) q.Read(b,i,Math.Min(508,b.Length-i));
            Check(a.Any(x=>x!=0) && a.SequenceEqual(b), "MDX FM audio and resampler buffer independence");
            // MDXの細かい音程はOPMのKC/KFから求め、光る鍵（ドライバーのノート）と半音以内で一致する。
            var keyed = new List<MusicChannelSnapshot>();
            using (var r = new MdxWaveProvider(path))
            {
                byte[] chunk = new byte[1024 * 4];
                for (int i = 0; i < 43; i++)
                {
                    r.Read(chunk, 0, chunk.Length);
                    keyed.AddRange(r.Snapshot().Channels.Take(8).Where(c => c.IsKeyOn && c.NoteNumber.HasValue));
                }
            }
            Check(keyed.Count > 0 && keyed.All(c => c.Pitch is double v && Math.Abs(v - c.NoteNumber!.Value) <= .5 &&
                MirrorFlux.Services.Music.MusicPitch.Display(c) == v),
                "MDX fine pitch from OPM key code/fraction stays within a semitone of the lit key");
        } finally { File.Delete(path); }
        string ini = Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".ini");
        try {
            foreach (string value in new[] { "default", "ymfm", "invalid" })
            {
                File.WriteAllText(ini, $"[GME]\n%fmCore {value}\n");
                var store = new MirrorFlux.Services.SettingsStore(ini);
                using var player = new IndependentVgmWaveProvider(VgmFile.Load(opn)!, settings: store.Load().Settings);
                Check(player.GetSnapshot().FmCoreName == "ymfm",
                    $"obsolete INI value {value} cannot switch normal playback away from ymfm");
            }
        } finally { File.Delete(ini); }
    }

    private static void VerifyRetrigger(Action<bool,string> check)
    {
        var commands = new List<byte>();
        void W(byte r,byte v) => commands.AddRange([0x54,r,v]);
        W(0x20,0xc7); W(0x28,0x4a);
        foreach(byte s in new byte[]{0,8,16,24}) {
            W((byte)(0x40+s),1); W((byte)(0x60+s),24); W((byte)(0x80+s),31);
            W((byte)(0xa0+s),20); W((byte)(0xc0+s),20); W((byte)(0xe0+s),0xff);
        }
        for(int n=0;n<100;n++) { W(8,0); W(8,0x78); commands.AddRange([0x61,0x3a,0x11]); }
        using var p = new IndependentVgmWaveProvider(VgmFile.Load(Vgm(0x30,4000000,commands))!);
        byte[] buffer=new byte[4410*4]; var rms=new List<double>();
        for(int n=0;n<100;n++) {
            p.Read(buffer,0,buffer.Length); double power=0;
            for(int i=0;i<buffer.Length;i+=2) { double v=BinaryPrimitives.ReadInt16LittleEndian(buffer.AsSpan(i)); power+=v*v; }
            rms.Add(Math.Sqrt(power/(buffer.Length/2)));
        }
        Console.WriteLine($"Retrigger RMS first={rms[1]:F2}, last={rms[^1]:F2}");
        check(rms[1]>100 && rms[^1]>rms[1]*.8,"consecutive key-off/on retriggers attack for 100 notes without fading");

        // OPM hardware LFO PM (LFRQ=0xE0, PMD=0x80|64, PMS=5) modulates exported phase_step / channel.Pitch while KC/KF remain fixed.
        var lfoCmds = new List<byte>();
        void WL(byte r, byte v) => lfoCmds.AddRange([0x54, r, v]);
        WL(0x18, 0xe0); // LFRQ fast
        WL(0x19, 0x80 | 64); // PMD = 64
        WL(0x1b, 0x02); // Triangle LFO wave
        WL(0x20, 0xc7);
        WL(0x28, 0x4a); // KC = A4
        WL(0x30, 0x00); // KF = 0
        WL(0x38, 0x50); // PMS = 5 on CH0
        WL(0x08, 0x78); // Key on CH0
        lfoCmds.AddRange([0x61, 0x44, 0xac]); // Wait 44100 samples
        using var lfoPlayer = new IndependentVgmWaveProvider(VgmFile.Load(Vgm(0x30, 3579545, lfoCmds))!);
        byte[] lfoBuf = new byte[512 * 4];
        var lfoPitches = new List<double>();
        for (int step = 0; step < 40; step++)
        {
            lfoPlayer.Read(lfoBuf, 0, lfoBuf.Length);
            if (lfoPlayer.GetSnapshot().MusicChannels.FirstOrDefault()?.Pitch is double lp)
                lfoPitches.Add(lp);
        }
        check(lfoPitches.Count > 10 && lfoPitches.Max() - lfoPitches.Min() > 0.05,
            "ymfm OPM hardware LFO PM modulates exported channel continuous pitch");
    }
}
