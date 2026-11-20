using MirrorFlux.Services.Music;
using System.IO;
using System.IO.Compression;
using System.Buffers.Binary;
using MirrorFlux.Services.Vgm;

internal static class VgmChipRegression
{
    private static void Put(byte[] bytes,int offset,uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset),value);
    private static byte[] File(List<byte> commands,params (int Offset,uint Clock)[] chips)
    {
        byte[] bytes=new byte[256+commands.Count]; "Vgm "u8.CopyTo(bytes); Put(bytes,4,(uint)bytes.Length-4); Put(bytes,8,0x171); Put(bytes,0x34,256-0x34);
        foreach(var chip in chips) Put(bytes,chip.Offset,chip.Clock); commands.CopyTo(bytes,256); return bytes;
    }
    private static void Write(List<byte> bytes,byte command,byte reg,byte value) => bytes.AddRange([command,reg,value]);
    private static byte[] Render(byte[] bytes,int frames=8192,int chunk=1024)
    {
        var file=VgmFile.Load(bytes,"standalone VGM regression") ?? throw new Exception("Fixture did not load");
        using var provider=new IndependentVgmWaveProvider(file);
        byte[] audio=new byte[frames*4];
        for(int pos=0;pos<frames;pos+=chunk) provider.Read(audio,pos*4,Math.Min(chunk,frames-pos)*4);
        return audio;
    }
    public static void Run()
    {
        using var syntheticRhythmRom = new SyntheticRhythmRom();

        int passed=0;
        void Check(bool value,string label) { if(!value) throw new Exception(label); Console.WriteLine("PASS "+label); passed++; }
        Check(IndependentVgmWaveProvider.SupportsDevice(0x25), "Compiled ES550x core is available");
        Es550xRegression.Run(Check);
        NativeVoiceDisplayRegression.Run(Check);
        // The Darius board clocks one four-bit MSM5205 voice at 8 kHz.
        var adpcm = new List<byte> { 0x32, 0x00 };
        for (int i = 0; i < 512; i++)
            adpcm.AddRange([0x32, (byte)(i % 16 < 8 ? 0x14 : 0x1c), (byte)(i % 2 == 0 ? 0x74 : 0x75)]);
        adpcm.AddRange([0x32, 0x01, 0x61, 0xff, 0x7f, 0x66]);
        var msmFile = File(adpcm, (0x44, 0x40000000 | 4000000), (0xf0, 384000));
        Put(msmFile, 8, 0x172);
        msmFile[0xd7] = 6; // S48, four-bit input
        var msmAudio = Render(msmFile);
        int Peak(int firstFrame, int frames) => Enumerable.Range(firstFrame, frames)
            .Max(frame => Math.Abs((int)BinaryPrimitives.ReadInt16LittleEndian(msmAudio.AsSpan(frame * 4))));
        Check(Peak(128, 2400) > 256 && Peak(4096, 2048) <= 4,
            "MSM5205 produces sampled audio alongside two idle YM2203s and goes quiet on reset");
        using (var player = new IndependentVgmWaveProvider(VgmFile.Load(msmFile)!))
        {
            var buffer = new byte[256 * 4];
            MusicChannelSnapshot Voice() => VgmPlaybackBackend.MapSnapshot(player.GetSnapshot()).Channels.Single(c => c.ChipName == "MSM5205");
            Check(!Voice().IsKeyOn && !Voice().IsInUse, "Unused MSM5205 starts with its keyboard off");
            player.Read(buffer, 0, buffer.Length);
            var hit = Voice();
            Check(player.GetSnapshot().MusicChannels[12].Id == hit.Id, "Dual YM2203 plus MSM5205 places ADPCM on channel 13");
            Check(hit.Kind == MusicChannelKind.Adpcm && hit.KeyboardNotes.Contains(60) && hit.IsKeyOn && hit.Level > 0,
                "MSM5205 decoded nibbles light the ADPCM keyboard and meter");
            Check(player.SetChannelMuted(hit, true), "MSM5205 accepts the displayed channel mask");
            player.Read(buffer, 0, buffer.Length);
            Check(player.SetChannelMuted(hit, false), "MSM5205 accepts unmute");
            for (int i = 0; i < 20; i++) player.Read(buffer, 0, buffer.Length);
            Check(Voice() is { IsKeyOn: false, IsInUse: true, Level: 0 }, "MSM5205 reset releases its ADPCM lane");
        }
        var pwm = new List<byte> { 0xb2,0x41,0x00, 0xb2,0x12,0x00 };
        for (int n = 0; n < 8192; n++)
        {
            pwm.AddRange([0xb2,0x21,(byte)(n%32<16 ? 0x40 : 0xc0),
                          0xb2,0x31,(byte)(n%24<12 ? 0xc0 : 0x40),0x70]);
        }
        pwm.Add(0x66);
        using (var player = new IndependentVgmWaveProvider(VgmFile.Load(File(pwm,(0x70,23011361)))!))
        {
            var buffer = new byte[256 * 4];
            for (int i = 0; i < 4; i++) player.Read(buffer,0,buffer.Length);
            var lanes = VgmPlaybackBackend.MapSnapshot(player.GetSnapshot()).Channels.Where(c => c.ChipName == "PWM").ToArray();
            Check(lanes.Length == 2 && lanes.All(c => c.IsKeyOn && c.Level > 0 && c.KeyboardNotes.Length > 0),
                "32X PWM exposes active left and right PCM lanes");
            Check(lanes[0].Pan == 2 && lanes[1].Pan == 1,
                "32X PWM left and right lanes retain their output sides");
            Check(player.SetChannelMuted(lanes[0],true), "32X PWM left lane can be masked");
            for (int i = 0; i < 8; i++) player.Read(buffer,0,buffer.Length);
            Check(System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(buffer.AsSpan(0,2)) == 0 &&
                buffer.Where((_,i) => i%4>=2).Any(b=>b!=0), "32X PWM left mask keeps right audio");
        }
        // Exercise the native-output path, including writes that make the old SCC display core disappear.
        var msx = new List<byte>();
        msx.AddRange([0xd2,0x84,128,0x7f, 0xd2,0x81,8,253, 0xd2,0x81,9,0,
            0xd2,0x82,4,15, 0xd2,0x83,0,16, 0x51,0x10,0x22, 0x51,0x20,0x19, 0x51,0x30,0x10,
            0x61,0xff,0x7f,0x66]);
        using (var p = new IndependentVgmWaveProvider(VgmFile.Load(File(msx,(0x9c,0xc0000000u|1789773),(0x10,3579545)))!))
        {
            byte[] audio = new byte[4096]; p.Read(audio,0,audio.Length);
            var channels = p.GetSnapshot().MusicChannels;
            var plus = channels.Single(c => c.Id == "K051649 #2:4");
            Check(plus.IsKeyOn && plus.NoteNumber == 69 && plus.Waveform![0] == 255 &&
                channels.Single(c => c.Id == "K051649 #2:3").Waveform![0] == 128 &&
                channels.Single(c => c.Id == "K051649:4").Waveform![0] == 128,
                "SCC+ exposes independent fifth waveform, correct A4 and second-chip state");
            var opll = channels.Single(c => c.Id == "YM2413:FM:0");
            Check(opll.IsKeyOn && opll.NoteNumber == 69 && opll.KeyboardNotes.Contains(69) &&
                channels.Count(c=>c.ChipName == "YM2413") == 9 && !channels.Any(c=>c.Label == "MIX"),
                "YM2413 and SCC/SCC+ share the native playback path without duplicate MIX rows");
        }
        var vrc7 = new List<byte> { 0x51,0x10,0x22, 0x51,0x20,0x19, 0x51,0x30,0x10,
            0x61,0xff,0x7f,0x66 };
        using (var p = new IndependentVgmWaveProvider(VgmFile.Load(File(vrc7,(0x10,0x80000000u|3579545)))!))
        {
            p.Read(new byte[4096],0,4096);
            var lanes = p.GetSnapshot().MusicChannels;
            Check(lanes.Length == 6 && lanes.All(c => c.ChipName == "VRC7" && c.Kind == MusicChannelKind.Fm) &&
                  lanes[0].IsKeyOn && !lanes.Any(c => c.Label == "MIX"),
                "VGM VRC7 flag exposes six FM channels without an extra MIX row");
            Check(p.SetChannelMuted(lanes[0], true), "VGM VRC7 FM channel can be masked");
        }
        // OPL2 melody and percussion use the same physical paths in the full player.
        var opl2 = new List<byte> { 0x5a,0xa0,0x44, 0x5a,0xb0,0x32, 0x5a,0xbd,0x30, 0x61,0xff,0x7f,0x66 };
        using (var p = new IndependentVgmWaveProvider(VgmFile.Load(File(opl2,(0x50,3579545)))!))
        {
            p.Read(new byte[4096],0,4096);
            var lanes = p.GetSnapshot().MusicChannels;
            Check(lanes.Length == 9 && lanes[0].NoteNumber == 69 && lanes[0].IsKeyOn && lanes[6].Label == "BD" && lanes[6].IsKeyOn,
                "OPL2 reports the melody note and bass drum independently");
        }
        var opl3 = new List<byte> {
            0x5f,0x05,0x01, // OPL3 new mode
            0x5e,0x43,0x00, 0x5e,0xc0,0x10, 0x5e,0xa0,0x44, 0x5e,0xb0,0x32,
            0x5f,0x43,0x00, 0x5f,0xc0,0x20, 0x5f,0xa0,0x44, 0x5f,0xb0,0x32,
            0x5f,0x04,0x01, 0x5e,0xc6,0x30, 0x5e,0xbd,0x30, 0x61,0xff,0x7f,0x66
        };
        using (var p = new IndependentVgmWaveProvider(VgmFile.Load(File(opl3,(0x5c,14318180)))!))
        {
            p.Read(new byte[4096],0,4096);
            var lanes = p.GetSnapshot().MusicChannels;
            Check(lanes.Length == 18 && lanes.All(c => c.ChipName == "YMF262" && c.Label != "MIX") &&
                lanes.Select(c => c.Id).Distinct().Count() == 18, "YMF262 has eighteen distinct FM/rhythm lanes");
            Check(lanes[0].IsKeyOn && lanes[0].NoteNumber == 69 && lanes[0].Pan == 2 &&
                lanes[9].IsKeyOn && lanes[9].NoteNumber == 69 && lanes[9].Pan == 1 &&
                lanes[0].Level > 0 && lanes[9].Level > 0,
                "YMF262 tracks both OPL3 ports, notes, stereo routing and meters");
            Check(!lanes[3].IsKeyOn && lanes[3].Level == 0 && lanes[6].Label == "BD" && lanes[6].IsKeyOn &&
                p.SetChannelMuted(lanes[9],true), "YMF262 four-operator partner and rhythm lane remain distinct and maskable");
            Check(p.GetSnapshot().RegisterBanks.Count(b=>b.ChipName=="YMF262") == 2,
                "YMF262 exposes two register ports without a MIX row");
        }
        var secondOpl3 = new List<byte> { 0xaf,0x05,0x01, 0xaf,0x43,0x00, 0xaf,0xc0,0x30,
            0xaf,0xa0,0x44, 0xaf,0xb0,0x32, 0x61,0xff,0x7f,0x66 };
        using (var p = new IndependentVgmWaveProvider(VgmFile.Load(File(secondOpl3,(0x5c,0x40000000u|14318180)))!))
        {
            p.Read(new byte[4096],0,4096);
            var lanes = p.GetSnapshot().MusicChannels;
            Check(lanes.Length == 36 && lanes.Single(c=>c.Id=="YMF262 #2:FM:9").IsKeyOn &&
                !lanes.Single(c=>c.Id=="YMF262:FM:9").IsKeyOn,
                "Second YMF262 port-one commands reach the second chip's keyboard and meter");
        }
        var es = new List<byte>();
        byte[] esWave = Enumerable.Range(0,512).Select(i => (byte)Math.Clamp(128 +
            (int)Math.Round(100*Math.Sin(i*Math.PI/128)),1,255)).ToArray();
        es.AddRange([0x67,0x66,0xe1,0x04,0x02,0x00,0x00,0,0,0,0]);
        es.AddRange(esWave);
        foreach (var (address,value) in new (byte,byte)[] { (0xe1,2),(0x20,1),(0x21,1),
            (0x40,0x80),(0x41,0x80),(0x80,0),(0x81,1),(0xa0,0),(0xa1,0x10) })
            es.AddRange([0xd5,0,(byte)address,value]);
        es.AddRange([0x61,0xff,0x7f,0x66]);
        using (var p = new IndependentVgmWaveProvider(VgmFile.Load(File(es,(0xcc,7159090)))!))
        {
            byte[] audio = new byte[4096]; p.Read(audio,0,audio.Length);
            var lanes = p.GetSnapshot().MusicChannels;
            Check(lanes.Length == 32 && lanes.All(c => c.ChipName == "ES5503" && c.Label != "MIX") &&
                lanes.Select(c => c.Id).Distinct().Count() == 32,
                "ES5503 exposes thirty-two independent PCM oscillator lanes");
            Check(lanes[0].IsKeyOn && lanes[1].IsKeyOn && lanes[0].NoteNumber == 69 &&
                lanes[0].Level > 0 && lanes[1].Level > 0 &&
                lanes[0].Pan == 2 && lanes[1].Pan == 1 && lanes[0].Waveform![0] == esWave[0] &&
                !lanes[2].IsInUse && p.SetChannelMuted(lanes[0],true),
                "ES5503 RAM, keyboard, meter, stereo route and per-voice mask follow the core");
            p.Read(new byte[256],0,256); // drain the previous resampler frame
            byte[] masked = new byte[4096]; p.Read(masked,0,masked.Length);
            Check(Enumerable.Range(0,masked.Length/4).All(i=>BinaryPrimitives.ReadInt16LittleEndian(masked.AsSpan(i*4))==0) &&
                Enumerable.Range(0,masked.Length/4).Any(i=>BinaryPrimitives.ReadInt16LittleEndian(masked.AsSpan(i*4+2))!=0),
                "ES5503 voice mask silences only its own stereo side");
        }
        byte[] oneShotEs = File(es,(0xcc,7159090));
        oneShotEs[256+11+16] = 0; // first oscillator reaches a zero terminator
        using (var p = new IndependentVgmWaveProvider(VgmFile.Load(oneShotEs)!))
        {
            p.Read(new byte[16],0,16);
            Check(p.GetSnapshot().MusicChannels[0].IsKeyOn,
                "ES5503 one-shot oscillator initially lights its keyboard");
            p.Read(new byte[1024],0,1024);
            var lanes = p.GetSnapshot().MusicChannels;
            Check(!lanes[0].IsKeyOn && lanes[0].Level == 0 && lanes[1].IsKeyOn,
                "ES5503 zero-terminated sample releases only its own key and meter");
        }
        var dualOpna = new List<byte> { 0x67,0x66,0x81, 9,0,0,0x80, 0,0,0x20,0, 0,0,0,0, 0x42,
            0xa6,0,0xfe, 0xa6,1,0, 0xa6,7,0x3e, 0xa6,8,15, 0x61,0xff,0x7f,0x66 };
        using (var p = new IndependentVgmWaveProvider(VgmFile.Load(File(dualOpna,(0x48,0x40000000u|7987200)))!))
        {
            byte[] audio = new byte[4096]; p.Read(audio,0,audio.Length);
            var channels = p.GetSnapshot().MusicChannels;
            Check(channels.Length == 32 && channels[9].Id == "YM2608:ADPCM-B" && channels[10].Id == "YM2608:RHYTHM:0" &&
                channels[25].Id == "YM2608 #2:ADPCM-B" && channels.Select(c=>c.Id).Distinct().Count()==32 &&
                channels.Single(c=>c.Id=="YM2608 #2:SSG:0").IsKeyOn && !channels.Single(c=>c.Id=="YM2608:SSG:0").IsKeyOn,
                "Large ADPCM image preserves both YM2608s and routes second-chip writes independently");
        }
        Console.WriteLine("Compiled VGM device IDs: " + string.Join(",",Enumerable.Range(0,48).Where(type=>IndependentVgmWaveProvider.SupportsDevice((byte)type)).Select(type=>type.ToString("X2"))));
        var c140Commands = new List<byte> { 0xd4, 0, 0, 0xff, 0xd4, 0, 1, 0xff, 0xd4, 0, 2, 0x80, 0xd4, 0, 3, 0, 0xd4, 0, 5, 0x80, 0x61, 0xff, 0x7f, 0x66 };
        byte[] c140Vgm = File(c140Commands, (0xa8, 8000000u));
        using (var c140p = new IndependentVgmWaveProvider(VgmFile.Load(c140Vgm)!))
        {
            var initialSnap = c140p.GetSnapshot();
            Check(initialSnap.MusicChannels.Length == 24 && initialSnap.MusicChannels.All(c => c.ChipName == "C140" && c.Label != "MIX" && c.Kind == MusicChannelKind.Pcm),
                "Namco C140 initializes with 24 PCM voice channels instead of a single MIX channel");
            var mappedInitial = VgmPlaybackBackend.MapSnapshot(initialSnap);
            Check(mappedInitial.ResolvedSoundSourceDisplay.Contains("C140"),
                "Namco C140 resolves sound source display without falling back to MIX");
            byte[] buf = new byte[4096];
            c140p.Read(buf, 0, buf.Length);
            var activeSnap = c140p.GetSnapshot();
            var voice0 = activeSnap.MusicChannels.Single(c => c.Id == "C140:0");
            Check(voice0.IsKeyOn && voice0.Label == "PCM01" && voice0.Kind == MusicChannelKind.Pcm,
                "Namco C140 triggered voice activates as PCM01");
        }
        // Legacy VGM 1.51 file with C140 clock logged as 21390 Hz sample rate (like Starblade / early arcade logs)
        var legacyC140Commands = new List<byte> { 0xd4, 0, 0, 0xff, 0xd4, 0, 1, 0xff, 0xd4, 0, 2, 0x80, 0xd4, 0, 3, 0, 0xd4, 0, 5, 0x80, 0x61, 0xff, 0x7f, 0x66 };
        byte[] legacyC140Vgm = File(legacyC140Commands, (0xa8, 21390u));
        // Force header version to 1.51 (0x151) and set type at 0x96 to 1 (System 21)
        BitConverter.GetBytes(0x151u).CopyTo(legacyC140Vgm, 0x08);
        legacyC140Vgm[0x96] = 1;
        var parsedLegacy = VgmFile.Load(legacyC140Vgm);
        Check(parsedLegacy != null && parsedLegacy.C140Clock == 12288000 && parsedLegacy.C140Type == 1 && parsedLegacy.SoundSourceDisplay.Contains("Namco C140"),
            "Legacy VGM 1.51 header with 21390 sample rate normalizes to 12.288 MHz C140 clock and detects chip");
        using (var legacyPlayer = new IndependentVgmWaveProvider(parsedLegacy!))
        {
            byte[] buf = new byte[4096];
            legacyPlayer.Read(buf, 0, buf.Length);
            var snap = legacyPlayer.GetSnapshot();
            var v0 = snap.MusicChannels.Single(c => c.Id == "C140:0");
            Check(v0.IsKeyOn && v0.Level > 0 && v0.NoteNumber != null,
                "Legacy VGM 1.51 C140 voice drives keyboard note and level meter during playback");
        }
        // 実曲は同梱しない。手元のC140曲で確かめるときだけ、環境変数にVGZのパスを指定する。
        string? starbladePath = Environment.GetEnvironmentVariable("MF_TEST_C140_VGZ");
        if (!string.IsNullOrEmpty(starbladePath) && System.IO.File.Exists(starbladePath))
        {
            var vgm = VgmFile.Load(starbladePath);
            Check(vgm != null && vgm.C140Clock == 12288000 && vgm.C140Type == 1 && vgm.SoundSourceDisplay.Contains("Namco C140"),
                "Starblade VGM file normalizes C140 clock and identifies Namco C140");
            using var player = new IndependentVgmWaveProvider(vgm!);
            byte[] buffer = new byte[8192];
            int activeSnapshots = 0;
            for (int frame = 0; frame < 60; frame++)
            {
                player.Read(buffer, 0, buffer.Length);
                var snap = player.GetSnapshot();
                if (snap.MusicChannels.Any(c => c.ChipName == "C140" && c.IsKeyOn && c.Level > 0)) activeSnapshots++;
            }
            Check(activeSnapshots > 0, "Starblade real track drives active C140 key-on and meter levels");
        }
        var opn=new List<byte>();
        Write(opn,0x55,7,0x3e); Write(opn,0x55,0,254); Write(opn,0x55,1,0); Write(opn,0x55,8,15); opn.AddRange([0x61,0xff,0x7f,0x66]);
        byte[] file=File(opn,(0x44,4000000));
        Check(Render(file).Any(b=>b!=0),"standalone VGM x64 DLL loads and renders OPN PSG");
        Check(Render(file).SequenceEqual(Render(file,8192,127)),"Native renderer preserves output across callback buffer sizes");
        var opna=new List<byte>(); Write(opna,0x56,0x11,63); Write(opna,0x56,0x18,0xdf); Write(opna,0x56,0x10,1); opna.AddRange([0x61,0xff,0x7f,0x66]);
        Check(Render(File(opna,(0x48,7987200))).Any(b=>b!=0),"standalone VGM YM2608 receives the external synthetic rhythm ROM");
        byte[] compressed=CompressedDac(true), plain=CompressedDac(false);
        Check(Render(plain).Any(b=>b!=0) && Render(plain).SequenceEqual(Render(compressed)),"Compressed n-bit PCM bank renders identically to uncompressed PCM");
        var pokey=new List<byte>(); Write(pokey,0xbb,0,100); Write(pokey,0xbb,1,0xaf); pokey.AddRange([0x61,0xff,0x7f,0x66]);
        Check(Render(File(pokey,(0xb0,1789773))).Any(b=>b!=0),"Previously unconnected Pokey header and commands produce audio");
        byte[] shortFile=File([..opn.Take(opn.Count-4),0x61,0x10,0,0x66],(0x44,4000000));
        using(var provider=new IndependentVgmWaveProvider(VgmFile.Load(shortFile)!))
        {
            var buffer=new byte[4096]; provider.IsPaused=true; Check(provider.Read(buffer,0,buffer.Length)==buffer.Length && buffer.All(b=>b==0) && provider.GetSnapshot().CurrentTime==TimeSpan.Zero,"Native pause retains playback position");
            provider.IsPaused=false; for(int i=0;i<10;i++) provider.Read(buffer,0,buffer.Length);
            Check(!provider.IsPlaying,"Native non-looping stream terminates");
        }
        var dual=new List<byte>(); Write(dual,0xa5,7,0x3e); Write(dual,0xa5,0,254); Write(dual,0xa5,1,0); Write(dual,0xa5,8,15); dual.AddRange([0x61,0xff,0x7f,0x66]);
        Check(Render(File(dual,(0x44,0x40000000|4000000))).Any(b=>b!=0),"Second YM2203 remains audible through compatibility engine");
        byte[] Oki(bool second = false)
        {
            byte[] rom = new byte[8 + 4096]; Put(rom, 0, 4096);
            rom[8 + 8 + 1] = 1; // sample 1: 0x100 .. 0x1ff
            rom[8 + 8 + 4] = 1; rom[8 + 8 + 5] = 0xff;
            Array.Fill(rom, (byte)0x17, 8 + 256, 256);
            var cmd = new List<byte> { 0x67, 0x66, 0x8b };
            cmd.AddRange(BitConverter.GetBytes((uint)rom.Length | (second ? 0x80000000u : 0))); cmd.AddRange(rom);
            byte reg = second ? (byte)0x80 : (byte)0;
            Write(cmd, 0xb8, reg, 0x81); Write(cmd, 0xb8, reg, 0x90);
            cmd.AddRange([0x61, 0xff, 0x7f, 0x66]);
            return File(cmd, (0x98, 0x80000000u | (second ? 0x40000000u : 0) | 1056000u));
        }
        foreach (bool second in new[] { false, true })
        using (var provider = new IndependentVgmWaveProvider(VgmFile.Load(Oki(second))!))
        {
            byte[] buffer = new byte[256 * 4]; provider.Read(buffer, 0, buffer.Length);
            string chip = second ? "OKIM6295 #2" : "OKIM6295";
            var voices = provider.GetSnapshot().MusicChannels.Where(c => c.ChipName == chip).ToArray();
            Check(voices.Length == 4 && voices[0].IsKeyOn && voices[3].IsKeyOn, "OKIM6295 native voices 1 and 4 display independently" + (second ? " on second chip" : ""));
            Check(voices[0].Level > 0 && voices[3].Level > 0 && voices[0].KeyboardNotes.Length > 0 && !voices[1].IsInUse,
                "OKIM6295 feeds keyboard and level meters while unused voices remain dim" + (second ? " on second chip" : ""));
            for (int n = 0; n < 40; n++) provider.Read(buffer, 0, buffer.Length);
            Check(!provider.GetSnapshot().MusicChannels.Any(c => c.ChipName == chip && c.IsKeyOn), "OKIM6295 natural sample end clears voice activity" + (second ? " on second chip" : ""));
        }
        var oki6258 = new List<byte>(); Write(oki6258, 0xb7, 0, 2); Write(oki6258, 0xb7, 2, 0);
        for (int n = 0; n < 8192; n++) { Write(oki6258, 0xb7, 1, 0x17); oki6258.Add(0x75); }
        oki6258.AddRange([0x61, 0xff, 0x7f, 0x66]);
        using (var provider = new IndependentVgmWaveProvider(VgmFile.Load(File(oki6258, (0x90, 8000000)))!))
        {
            byte[] buffer = new byte[256 * 4]; provider.Read(buffer, 0, buffer.Length);
            var voice = provider.GetSnapshot().MusicChannels.Single(c => c.ChipName == "OKIM6258");
            Check(voice.IsKeyOn && voice.Level > 0 && voice.KeyboardNotes.Length > 0, "OKIM6258 native ADPCM activity feeds keyboard and meter");
        }
        void Wide(List<byte> commands,int address,int value) => commands.AddRange([0xe1,(byte)(address>>8),(byte)address,(byte)(value>>8),(byte)value]);
        byte[] C352(bool loop)
        {
            var commands=new List<byte>(); byte[] rom=new byte[8+1024]; Put(rom,0,1024); Array.Fill(rom,(byte)0x40,8,1024);
            commands.AddRange([0x67,0x66,0x92]); commands.AddRange(BitConverter.GetBytes(rom.Length)); commands.AddRange(rom);
            foreach(int channel in new[]{0,31})
            {
                int address=channel*8;
                Wide(commands,address,channel==0?0xff00:0x00ff);
                Wide(commands,address+2,channel==0?0x8000:0x4000);
                Wide(commands,address+5,0); Wide(commands,address+6,1023); Wide(commands,address+7,0);
                Wide(commands,address+3,loop?0x4002:0x4000);
            }
            Wide(commands,0x202,0); commands.AddRange([0x61,0xff,0x7f,0x66]);
            byte[] file=File(commands,(0xdc,24576000)); file[0xd6]=72; return file;
        }
        using(var provider=new IndependentVgmWaveProvider(VgmFile.Load(C352(true))!))
        {
            var buffer=new byte[256*4]; provider.Read(buffer,0,buffer.Length);
            var channels=provider.GetSnapshot().MusicChannels.Where(c=>c.ChipName=="C352").ToArray();
            Check(channels.Length==32 && channels[0].IsKeyOn && channels[31].IsKeyOn,"C352 exposes all 32 native voices, including voice 32");
            Check(channels[0].NoteNumber-channels[31].NoteNumber==12 && channels[0].Pan==2 && channels[31].Pan==1,
                "C352 voices retain independent sample rates, notes and opposing pan");
            Check(channels[0].KeyboardNotes.Length>0 && channels[31].KeyboardNotes.Length>0 && !channels[1].IsInUse,
                "C352 native voices feed both keyboards and unused voices remain dim");
        }
        using(var provider=new IndependentVgmWaveProvider(VgmFile.Load(C352(false))!))
        {
            var buffer=new byte[256*4]; provider.Read(buffer,0,buffer.Length);
            Check(provider.GetSnapshot().MusicChannels.Any(c=>c.ChipName=="C352"&&c.IsKeyOn),"C352 sample starts on its native busy flag");
            for(int i=0;i<40;i++) provider.Read(buffer,0,buffer.Length);
            Check(!provider.GetSnapshot().MusicChannels.Any(c=>c.ChipName=="C352"&&c.IsKeyOn),"C352 native sample end clears keys without a key-off register write");
        }
        var ymzCommands=new List<byte>(); byte[] ymzRom=new byte[8+256]; Put(ymzRom,0,256); Array.Fill(ymzRom,(byte)0x40,8,256);
        ymzCommands.AddRange([0x67,0x66,0x86]); ymzCommands.AddRange(BitConverter.GetBytes(ymzRom.Length)); ymzCommands.AddRange(ymzRom);
        Write(ymzCommands,0x5d,0xff,0x80);
        foreach(int channel in new[]{0,7})
        {
            Write(ymzCommands,0x5d,(byte)(channel*4),channel==0?(byte)0xff:(byte)0x7f);
            Write(ymzCommands,0x5d,(byte)(channel*4+2),0xff);
            Write(ymzCommands,0x5d,(byte)(channel*4+3),channel==0?(byte)1:(byte)15);
            Write(ymzCommands,0x5d,(byte)(0x62+channel*4),64);
            Write(ymzCommands,0x5d,(byte)(0x63+channel*4),128);
            Write(ymzCommands,0x5d,(byte)(channel*4+1),0xd0);
        }
        ymzCommands.AddRange([0x61,0xff,0x7f,0x66]);
        using(var provider=new IndependentVgmWaveProvider(VgmFile.Load(File(ymzCommands,(0x68,16934400)))!))
        {
            var buffer=new byte[256*4]; provider.Read(buffer,0,buffer.Length);
            var channels=provider.GetSnapshot().MusicChannels.Where(c=>c.ChipName=="YMZ280B").ToArray();
            Check(channels.Length==8 && channels[0].IsKeyOn && channels[7].IsKeyOn,"YMZ280B exposes voice 1 and 8 independently");
            Check(channels[0].NoteNumber-channels[7].NoteNumber==12 && channels[0].Pan==2 && channels[7].Pan==1,
                "YMZ280B voices retain independent pitch and hard-left/right pan");
        }
        var qsoundCommands=new List<byte>(); byte[] qsoundRom=new byte[8+256]; Put(qsoundRom,0,256); Array.Fill(qsoundRom,(byte)0x40,8,256);
        qsoundCommands.AddRange([0x67,0x66,0x8f]); qsoundCommands.AddRange(BitConverter.GetBytes(qsoundRom.Length)); qsoundCommands.AddRange(qsoundRom);
        qsoundCommands.AddRange([0x61,0,0x10]);
        void Qs(int address,int value)=>qsoundCommands.AddRange([0xc4,(byte)(value>>8),(byte)value,(byte)address]);
        foreach(int channel in new[]{0,15})
        {
            Qs(channel*8,0x8000); Qs(channel*8+2,channel==0?0x1000:0x800);
            Qs(channel*8+4,128); Qs(channel*8+5,128); Qs(channel*8+6,0x3fff); Qs(0x80+channel,channel==0?0x110:0x130);
        }
        qsoundCommands.AddRange([0x61,0xff,0x7f,0x66]);
        using(var provider=new IndependentVgmWaveProvider(VgmFile.Load(File(qsoundCommands,(0xb4,4000000)))!))
        {
            var buffer=new byte[4096*4]; provider.Read(buffer,0,buffer.Length); provider.Read(buffer,0,buffer.Length);
            var channels=provider.GetSnapshot().MusicChannels.Where(c=>c.ChipName=="QSound").ToArray();
            Check(channels.Length==19 && channels[0].IsKeyOn && channels[15].IsKeyOn,"QSound splits sixteen PCM and three ADPCM lanes");
            Check(channels[0].NoteNumber-channels[15].NoteNumber==12 && channels[0].Pan==2 && channels[15].Pan==1,
                "QSound retains independent PCM pitch and pan");
        }
        Console.WriteLine($"Passed {passed} standalone VGM compatibility checks.");
    }
    private static byte[] CompressedDac(bool compressed)
    {
        byte[] sample=Enumerable.Range(0,256).Select(i=>(byte)(i%16*16)).ToArray();
        byte[] block;
        if(compressed)
        {
            block=new byte[10+128]; block[0]=0; Put(block,1,256); block[5]=8; block[6]=4; block[7]=1;
            for(int i=0;i<128;i++) block[10+i]=(byte)((i*2%16)<<4 | ((i*2+1)%16));
        }
        else block=sample;
        var commands=new List<byte>{0x67,0x66,compressed?(byte)0x40:(byte)0}; commands.AddRange(BitConverter.GetBytes(block.Length)); commands.AddRange(block);
        Write(commands,0x52,0x2b,0x80); Write(commands,0x53,0xb6,0xc0); commands.AddRange([0xe0,0,0,0,0]);
        for(int i=0;i<256;i++) commands.Add(0x8f);
        commands.AddRange([0x61,0xff,0x7f,0x66]); return File(commands,(0x2c,7670454));
    }
    public static void AuditRoute(int chip)
    {
        int[] fields = [0x0c,0x10,0x2c,0x30,0x38,0x40,0x44,0x48,0x4c,0x50,0x54,0x58,0x5c,0x60,0x64,0x68,
            0x6c,0x70,0x74,0x80,0x84,0x88,0x8c,0x90,0x98,0x9c,0xa0,0xa4,0xa8,0xac,0xb0,0xb4,
            0xb8,0xc0,0xc4,0xc8,0xcc,0xd0,0xd8,0xdc,0xe0,0xe4,0xe8,0xec,0xf0,0xf4,0xf8,0xfc];
        var bytes = File([0x61,0xff,0x7f,0x66], (fields[chip],3579545));
        Put(bytes,8,0x172); bytes[0xd6] = 16;
        using var player = new IndependentVgmWaveProvider(VgmFile.Load(bytes)!);
        byte[] output = new byte[1024];
        player.Read(output,0,output.Length);
        var snapshot = VgmPlaybackBackend.MapSnapshot(player.GetSnapshot());
        var channels = snapshot.Channels;
        Console.WriteLine($"{chip:X2} {string.Join(',',channels.Select(c=>c.ChipName).Distinct())} {channels.Length} lanes {string.Join(',',channels.Select(c=>c.KindLabel).Distinct())} IDs={string.Join(',',channels.Select(c=>c.Id))}");
        if (channels.Length == 0 || channels.Select(c=>c.Id).Distinct().Count()!=channels.Length || channels.Select(c=>c.ChipName).Distinct().Count()!=1)
            throw new Exception("Missing or duplicate channel IDs");
    }

    public static void AuditDisplay(string archive, string name)
    {
        using var zip = ZipFile.OpenRead(archive);
        var entries = zip.Entries.Where(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase) ||
            (name == "*" && e.Name.EndsWith(".vgz", StringComparison.OrdinalIgnoreCase))).ToArray();
        if (entries.Length == 0) throw new InvalidDataException("No matching VGM entry.");
        foreach (var entry in entries)
        {
            using var input = entry.Open(); using var data = new MemoryStream(); input.CopyTo(data);
            var file = VgmFile.Load(data.ToArray(), entry.Name)!;
            string chip = (file.Ym2413Clock & 0x80000000) != 0 ? "VRC7" : file.NesApuClock != 0 ? "NES APU" : file.K051649Clock != 0 ? "K051649" : file.Ym2413Clock != 0 ? "YM2413" : file.Ym3812Clock != 0 ? "YM3812" : file.Ymf262Clock != 0 ? "YMF262" : file.Es5503Clock != 0 ? "ES5503" : file.Ymz280BClock != 0 ? "YMZ280B" : file.Ymf271Clock != 0 ? "YMF271" : "YM2608";
            using var player = new IndependentVgmWaveProvider(file);
            bool pwm = player.GetSnapshot().MusicChannels.Any(c => c.ChipName == "PWM");
            if (pwm) chip = "PWM";
            bool vsu = player.GetSnapshot().MusicChannels.Any(c => c.ChipName == "VSU");
            if (vsu) chip = "VSU";
            bool msm = player.GetSnapshot().MusicChannels.Any(c => c.ChipName == "MSM5205");
            if (msm) chip = "MSM5205";
            bool msmIdle = false, msmHit = false;
            var samples = new byte[1024 * 4];
            var levels = new HashSet<int>(); var pitches = new SortedSet<int>(); var lit = new HashSet<string>(); var waves = new HashSet<string>(); var lanes = new HashSet<string>();
            for (int i = 0; i < (msm ? 2000 : 650) && player.Read(samples, 0, samples.Length) > 0; i++)
            {
                if (chip == "NES APU" && player.GetSnapshot().MusicChannels.Any(c => c.ChipName == "NES FDS"))
                    chip = "NES FDS";
                foreach (var channel in VgmPlaybackBackend.MapSnapshot(player.GetSnapshot()).Channels.Where(c=>c.ChipName == chip))
                {
                    lanes.Add(channel.Id);
                    if (msm)
                    {
                        msmHit |= channel.KeyboardNotes.Contains(60) && channel.Level > 0;
                        msmIdle |= !channel.IsKeyOn && channel.Level == 0;
                    }
                    if (channel.Label == "MIX") throw new Exception($"Unexpected MIX in {entry.Name} {chip}");
                    if ((channel.IsKeyOn && channel.NoteNumber.HasValue) || channel.KeyboardNotes.Length > 0)
                    {
                        lit.Add(channel.Id); levels.Add((int)(channel.Level * 100));
                        if (channel.NoteNumber.HasValue) pitches.Add(channel.NoteNumber.Value);
                    }
                    if (channel.Waveform is { Length: 32 } wave && wave.Distinct().Count() > 1) waves.Add(channel.Id);
                }
            }
            Console.WriteLine($"{entry.Name} {chip} lanes={lanes.Count}, lit={string.Join(',',lit)}, waves={string.Join(',',waves)}, pitches={string.Join(',',pitches)}, levels={levels.Count}");
            if (msm)
                Console.WriteLine($"  ADPCM keyboard and meter active={msmHit}, idle={msmIdle}");
            if (chip == "VRC7" && player.GetSnapshot().MusicChannels.Any(c => c.Label == "MIX" &&
                    (c.ChipName is "YM2413" or "VRC7")))
                throw new Exception("Duplicate VRC7 MIX row.");
            int expected = msm ? 1 : pwm ? 2 : vsu ? 6 : chip == "NES FDS" ? 1 : chip == "NES APU" ? 5 : chip == "VRC7" ? 6 : chip == "K051649" ? 5 : chip is "YM2413" or "YM3812" ? 9 : chip == "YMF262" ? 18 : chip == "ES5503" ? 32 : chip == "YMZ280B" ? 8 : 16;
            if ((chip == "YMF271" ? lanes.Count < 12 || lanes.Count > 48 : lanes.Count != expected) || (chip == "K051649" && !lit.IsSubsetOf(waves))) throw new Exception("Missing keyboard lanes or waveform");
        }
    }
    public static void Audit(string path)
    {
        using var zip=ZipFile.OpenRead(path); int count=0;
        foreach(var entry in zip.Entries.Where(e=>e.FullName.EndsWith(".vgm",StringComparison.OrdinalIgnoreCase)||e.FullName.EndsWith(".vgz",StringComparison.OrdinalIgnoreCase)).Take(8))
        {
            using var stream=entry.Open(); using var memory=new MemoryStream(); stream.CopyTo(memory);
            using var provider=new IndependentVgmWaveProvider(VgmFile.Load(memory.ToArray(),entry.Name)!);
            var buffer=new byte[4096]; long energy=0; var activeVoices=new HashSet<string>();
            for(int i=0;i<350;i++) { int read=provider.Read(buffer,0,buffer.Length); for(int j=0;j<read;j++) energy+=buffer[j]; foreach(var channel in provider.GetSnapshot().MusicChannels) if(channel.IsKeyOn || channel.KeyboardNotes.Length>0) activeVoices.Add(channel.Id); }
            if(energy==0) throw new Exception("Silent native playback: "+entry.Name);
            Console.WriteLine($"PASS {entry.Name}: {provider.GetSnapshot().MusicChannels.Length} display lanes, {activeVoices.Count} active voices, nonzero output"); count++;
        }
        if(count==0) throw new Exception("Archive contains no VGM tracks");
    }
}
