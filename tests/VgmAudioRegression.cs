using System.Buffers.Binary;
using MirrorFlux.Services.Vgm;
using MirrorFlux.Services.Music;

internal static class VgmAudioRegression
{
    private const int Rate = 44100;
    private static byte[] File(List<byte> commands, params (int Offset, uint Clock)[] clocks)
    {
        var data = new byte[256 + commands.Count+1];
        "Vgm "u8.CopyTo(data);
        Put(data, 8, 0x171);
        Put(data, 0x34, 256 - 0x34);
        foreach (var c in clocks) Put(data, c.Offset, c.Clock);
        if (clocks.Any(c => c.Offset == 0xdc)) data[0xd6] = 72; // C352 divisor: 72 * 4.
        commands.CopyTo(data, 256);data[^1]=0x66;
        return data;
    }
    private static void Put(byte[] data, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset, 4), value);
    private static void Write(List<byte> data, byte chip, byte reg, byte value) => data.AddRange([chip, reg, value]);
    private static void Wait(List<byte> data, int count) => data.AddRange([0x61, (byte)count, (byte)(count >> 8)]);
    private static void Tone(List<byte> data, byte command, int fnum = 644)
    {
        for (int slot = 0; slot < 4; slot++)
        {
            Write(data, command, (byte)(0x30 + slot * 4), 1);
            Write(data, command, (byte)(0x40 + slot * 4), (byte)(slot == 3 ? 16 : 127));
            Write(data, command, (byte)(0x50 + slot * 4), 31);
            Write(data, command, (byte)(0x80 + slot * 4), 15);
        }
        Write(data, command, 0xb0, 7);
        Write(data, command, 0xb4, 0xc0);
        Write(data, command, 0xa4, (byte)(0x20 | (fnum >> 8)));
        Write(data, command, 0xa0, (byte)fnum);
        Write(data, command, 0x28, 0xf0);
    }
    private static void OplTone(List<byte> data, byte command)
    {
        Write(data, command, 0x20, 0x01); Write(data, command, 0x23, 0x01);
        Write(data, command, 0x40, 0x3f); Write(data, command, 0x43, 0x00);
        Write(data, command, 0x60, 0xf0); Write(data, command, 0x63, 0xf0);
        Write(data, command, 0x80, 0x77); Write(data, command, 0x83, 0x77);
        Write(data, command, 0xc0, 0x01); Write(data, command, 0xa0, 0x98); Write(data, command, 0xb0, 0x31);
    }
    private static short[] Render(byte[] file, int frames, int readFrames = 1024)
    {
        using var provider = new IndependentVgmWaveProvider(VgmFile.Load(file, "audio regression")!);
        var result = new short[frames * 2];
        for (int offset = 0; offset < frames; offset += readFrames)
        {
            int count = Math.Min(readFrames, frames - offset);
            var bytes = new byte[count * 4];
            provider.Read(bytes, 0, bytes.Length);
            for (int i = 0; i < count * 2; i++) result[offset * 2 + i] = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(i * 2, 2));
        }
        return result;
    }
    private static double Frequency(short[] samples, int side = 0)
    {
        var crossings = new List<int>();
        for (int i = 2001; i < samples.Length / 2; i++)
            if (samples[(i - 1) * 2 + side] <= 0 && samples[i * 2 + side] > 0) crossings.Add(i);
        return crossings.Count > 2 ? Rate * (crossings.Count - 1.0) / (crossings[^1] - crossings[0]) : 0;
    }
    private static void Block(List<byte> data, byte type, byte[] payload)
    {
        data.AddRange([0x67, 0x66, type]);
        data.AddRange(BitConverter.GetBytes(payload.Length));
        data.AddRange(payload);
    }
    public static void Run(Action<bool, string> check)
    {
        using var rhythmRom = new SyntheticRhythmRom();
        byte[] invalidDivider = File([], (0xdc, 24576000)); invalidDivider[0xd6] = 0;
        bool rejectedDivider = false;
        try { using var invalid = new IndependentVgmWaveProvider(VgmFile.Load(invalidDivider)!); }
        catch (System.IO.InvalidDataException) { rejectedDivider = true; }
        check(rejectedDivider, "C352 zero divider is rejected before native initialization");
        for (int drum = 0; drum < 6; drum++)
        {
            var rhythm = new List<byte>();
            Write(rhythm, 0x56, 0x11, 63);
            Write(rhythm, 0x56, (byte)(0x18 + drum), 0x9f);
            Write(rhythm, 0x56, 0x10, (byte)(1 << drum));
            Wait(rhythm, Rate); rhythm.Add(0x66);
            var rhythmFile = File(rhythm, (0x48, 7987200));
            var rhythmWave = Render(rhythmFile, 4096);
            check(rhythmWave.Where((_, i) => i % 2 == 0).Any(v => v != 0) && rhythmWave.Where((_, i) => i % 2 != 0).All(v => v == 0),
                $"YM2608 rhythm {drum} sounds on the selected left channel");
            check(rhythmWave.SequenceEqual(Render(rhythmFile, 4096, 127)), $"YM2608 rhythm {drum} is independent of read block size");
            using var provider = new IndependentVgmWaveProvider(VgmFile.Load(rhythmFile, "rhythm")!);
            provider.Read(new byte[256 * 4], 0, 256 * 4);
            check(provider.GetSnapshot().MusicChannels.Single(c => c.Id == $"YM2608:RHYTHM:{drum}").IsKeyOn,
                $"YM2608 rhythm {drum} lights its keyboard lane");
            var tail = new byte[1024 * 4];
            for (int i = 0; i < 40; i++) provider.Read(tail, 0, tail.Length);
            check(!provider.GetSnapshot().MusicChannels.Single(c => c.Id == $"YM2608:RHYTHM:{drum}").IsKeyOn,
                $"YM2608 rhythm {drum} lamp ends with its sample");
        }
        foreach (bool registerUpload in new[] { false, true })
        {
            var opna = new List<byte>();
            byte[] sample = Enumerable.Range(0, 1024).Select(i => (byte)(i % 4 == 0 ? 0x78 : 0x87)).ToArray();
            Write(opna, 0x57, 0, 1);
            Write(opna, 0x57, 1, 0x82); // Left, 8-bit RAM addressing (32 bytes per address unit).
            Write(opna, 0x57, 2, 1); Write(opna, 0x57, 3, 0); // Nonzero start byte 32.
            Write(opna, 0x57, 4, 32); Write(opna, 0x57, 5, 0);
            Write(opna, 0x57, 0x0c, 0xff); Write(opna, 0x57, 0x0d, 0xff);
            if (registerUpload)
            {
                Write(opna, 0x57, 0, 0x60);
                foreach (byte value in sample) Write(opna, 0x57, 8, value);
                Write(opna, 0x57, 0, 1);
            }
            else
            {
                // Two partial images verify offset placement and preservation of previous data.
                for (int part = 0; part < 2; part++)
                {
                    var block = new byte[8 + 512]; Put(block, 0, 262144); Put(block, 4, (uint)(32 + part * 512));
                    sample.AsSpan(part * 512, 512).CopyTo(block.AsSpan(8)); Block(opna, 0x81, block);
                }
            }
            Write(opna, 0x57, 9, 0xff); Write(opna, 0x57, 0x0a, 0xff); Write(opna, 0x57, 0x0b, 0xff);
            Write(opna, 0x57, 0, 0xa0); Wait(opna, Rate); opna.Add(0x66);
            var opnaFile = File(opna, (0x48, 7987200));
            var audio = Render(opnaFile, 10000);
            check(audio.Where((_, i) => i % 2 == 0).Count(v => v != 0) > 500 && audio.Where((_, i) => i % 2 != 0).All(v => v == 0),
                $"YM2608 ADPCM {(registerUpload ? "register" : "image")} upload produces left-only sample audio");
            check(audio.SequenceEqual(Render(opnaFile, 10000, 127)), "YM2608 ADPCM output is independent of read block size");
            using var provider = new IndependentVgmWaveProvider(VgmFile.Load(opnaFile, "OPNA ADPCM")!);
            var bytes = new byte[256 * 4]; provider.Read(bytes, 0, bytes.Length);
            var frame = provider.GetSnapshot();
            check(frame.MusicChannels.Single(c => c.Id == "YM2608:ADPCM-B").IsKeyOn &&
                frame.MusicChannels.Count(c => c.Kind == MirrorFlux.Services.Music.MusicChannelKind.Rhythm) == 6 &&
                frame.MusicChannels.Single(c => c.Id == "YM2608:RHYTHM:0").KindLabel == "RHYTHM BD",
                "YM2608 ADPCM has its own active lane, separate from six RHYTHM lanes");
            for (int i = 0; i < 40; i++) provider.Read(bytes, 0, bytes.Length);
            check(!provider.GetSnapshot().MusicChannels.Single(c => c.Id == "YM2608:ADPCM-B").IsKeyOn,
                "YM2608 ADPCM lamp stops at natural sample end");
        }
        foreach (byte romType in new byte[] { 0x82, 0x83 })
        {
            var neo = new List<byte>();
            var neoRom = new byte[8 + 1024];
            BinaryPrimitives.WriteUInt32LittleEndian(neoRom, 1024);
            for (int i = 8; i < neoRom.Length; i++) neoRom[i] = (byte)(i % 4 == 0 ? 0x78 : 0x87);
            Block(neo, romType, neoRom);
            if (romType == 0x82)
            {
                Write(neo, 0x59, 1, 0x3f); Write(neo, 0x59, 8, 0x9f);
                Write(neo, 0x59, 0x10, 0); Write(neo, 0x59, 0x18, 0);
                Write(neo, 0x59, 0x20, 3); Write(neo, 0x59, 0x28, 0);
                Write(neo, 0x59, 0, 1);
            }
            else
            {
                Write(neo, 0x58, 0x11, 0x80);
                Write(neo, 0x58, 0x12, 0); Write(neo, 0x58, 0x13, 0);
                Write(neo, 0x58, 0x14, 3); Write(neo, 0x58, 0x15, 0);
                Write(neo, 0x58, 0x19, 0xff); Write(neo, 0x58, 0x1a, 0xff);
                Write(neo, 0x58, 0x1b, 0xff); Write(neo, 0x58, 0x10, 0x80);
            }
            Wait(neo, Rate); neo.Add(0x66);
            var neoFile = File(neo, (0x4c, 8000000));
            var neoWave = Render(neoFile, 10000);
            check(neoWave.Where((_, i) => i % 2 == 0).Count(v => v != 0) > 1000 &&
                neoWave.Where((_, i) => i % 2 != 0).All(v => v == 0),
                $"YM2610 ADPCM ROM {romType:X2} produces continuous left-only audio through the correct port");
            check(neoWave.SequenceEqual(Render(neoFile, 10000, 127)), $"YM2610 ADPCM {romType:X2} is independent of output block size");
            using var neoProvider = new IndependentVgmWaveProvider(VgmFile.Load(neoFile, "Neo display")!);
            neoProvider.Read(new byte[512 * 4], 0, 512 * 4);
            var neoFrame = neoProvider.GetSnapshot();
            var voice = neoFrame.MusicChannels.Single(c => c.Id == $"YM2610:ADPCM:{(romType == 0x82 ? 0 : 6)}");
            check(neoFrame.MusicChannels.Length == 14 && voice.Kind == MirrorFlux.Services.Music.MusicChannelKind.Adpcm && voice.IsKeyOn && voice.Level > 0,
                $"YM2610 ADPCM {romType:X2} exposes its own active lane in the fourteen-channel snapshot");
            check(voice.NoteNumber == 60,
                $"YM2610 ADPCM {romType:X2} exposes the keyboard reference note during playback");
            neoProvider.Read(new byte[12000 * 4], 0, 12000 * 4);
            check(!neoProvider.GetSnapshot().MusicChannels.Single(c => c.Id == voice.Id).IsKeyOn,
                $"YM2610 ADPCM {romType:X2} lamp stops when the sample ends without explicit key-off");
        }
        var neoNotes = new List<byte>();
        Tone(neoNotes, 0x58);
        for (int i = 0; i < neoNotes.Count; i += 3)
        {
            if (neoNotes[i + 1] == 0x28) neoNotes[i + 2] = 0xf1;
            else neoNotes[i + 1]++;
        }
        Write(neoNotes, 0x58, 0, 200); Write(neoNotes, 0x58, 1, 0);
        Write(neoNotes, 0x58, 7, 0x3e); Write(neoNotes, 0x58, 8, 15);
        Wait(neoNotes, 4096); Write(neoNotes, 0x58, 0x28, 1); Wait(neoNotes, Rate);
        using (var neoProvider = new IndependentVgmWaveProvider(VgmFile.Load(File(neoNotes, (0x4c, 8000000)), "Neo notes")!))
        {
            neoProvider.Read(new byte[4096], 0, 4096);
            var frame = neoProvider.GetSnapshot();
            var fm = frame.MusicChannels.Single(c => c.Id == "YM2610:FM:1");
            var psg = frame.MusicChannels.Single(c => c.Id == "YM2610:SSG:0");
            check(fm.IsKeyOn && fm.NoteNumber.HasValue && psg.IsKeyOn && psg.NoteNumber.HasValue && Math.Abs(psg.Frequency!.Value - 625) < .01,
                "Neo Geo FM and PSG expose independent pitched keyboard notes");
            check(frame.RegisterBanks.Count(b => b.ChipName == "YM2610") == 2 && frame.MusicChannels.All(c => c.Id != "YM2610:MIX"),
                "Neo Geo replaces mixed telemetry without duplicating register pages");
            neoProvider.Read(new byte[4096 * 4], 0, 4096 * 4);
            check(!neoProvider.GetSnapshot().MusicChannels.Single(c => c.Id == fm.Id).IsKeyOn,
                "Neo Geo FM keyboard releases on key-off");
        }
        var firstOpn = new List<byte>();
        var secondOpn = new List<byte>();
        Tone(firstOpn, 0x55, 644);
        Tone(secondOpn, 0xa5, 966);
        var bothOpn = firstOpn.Concat(secondOpn).ToList();
        Wait(firstOpn, Rate); Wait(secondOpn, Rate); Wait(bothOpn, Rate);
        firstOpn.Add(0x66); secondOpn.Add(0x66); bothOpn.Add(0x66);
        uint dualClock = 4000000u | 0x40000000u;
        var firstWave = Render(File(firstOpn, (0x44, dualClock)), Rate);
        var secondFile = File(secondOpn, (0x44, dualClock));
        var secondWave = Render(secondFile, Rate);
        var bothWave = Render(File(bothOpn, (0x44, dualClock)), Rate);
        check(secondWave.Max(v => Math.Abs((int)v)) > 1000 && Math.Abs(Frequency(secondWave) - 409.44) < 2,
            $"second YM2203 A5 commands synthesize FM at the unflagged 4 MHz clock: peak={secondWave.Max(v => Math.Abs((int)v))}, Hz={Frequency(secondWave):F2}");
        check(bothWave.Select((value, i) => Math.Abs(value - Math.Clamp(firstWave[i] + secondWave[i], short.MinValue, short.MaxValue)) <= 1).All(v => v),
            "dual YM2203 output sums both chips within one PCM quantization step");
        check(Render(secondFile, Rate, 127).SequenceEqual(secondWave), "second YM2203 output is independent of read buffer size");
        check(Render(File(secondOpn, (0x44, 4000000)), Rate).All(v => v == 0), "A5 cannot create a second YM2203 without the dual flag");
        var dualSsg = new List<byte> { 0xa5, 0, 200, 0xa5, 1, 0, 0xa5, 7, 0x3e, 0xa5, 8, 15 };
        Wait(dualSsg, Rate);
        dualSsg.Add(0x66);
        check(Math.Abs(Frequency(Render(File(dualSsg, (0x44, dualClock)), Rate)) - 625) < 2,
            "second YM2203 SSG synthesizes at the correct pitch");
        using (var dualProvider = new IndependentVgmWaveProvider(VgmFile.Load(File(bothOpn, (0x44, dualClock)), "dual OPN")!))
        {
            dualProvider.Read(new byte[4096], 0, 4096);
            var dual = dualProvider.GetSnapshot();
            check(dual.MusicChannels.Length == 12 && dual.MusicChannels.Select(c => c.Id).Distinct().Count() == 12 &&
                dual.MusicChannels.Single(c => c.Id == "YM2203 #2:FM:0").IsKeyOn,
                "dual YM2203 exposes two independent FM3 plus SSG3 groups");
            check(dual.RegisterBanks.Single(b => b.ChipName == "YM2203").Values[0xa0] == (644 & 255) &&
                dual.RegisterBanks.Single(b => b.ChipName == "YM2203 #2").Values[0xa0] == (966 & 255),
                "F12 register pages retain separate images for both YM2203 chips");
        }
        // Short VGM headers must never interpret command bytes as extra chip clocks.
        var shortFile = new byte[512];
        "Vgm "u8.CopyTo(shortFile);
        Put(shortFile, 8, 0x171); Put(shortFile, 0x34, 12);
        Array.Fill(shortFile, (byte)0x55, 64, shortFile.Length - 64);
        var parsed = VgmFile.Load(shortFile)!;
        check(parsed.Ym2203Clock == 0 && parsed.Ym2608Clock == 0 && parsed.OkiM6258Clock == 0 && parsed.HuC6280Clock == 0 &&
              parsed.GameBoyClock == 0 && parsed.NesApuClock == 0 && parsed.K051649Clock == 0 && parsed.C352Clock == 0,
            "short header does not read commands as chip clocks");

        foreach (var chip in new[] { (Cmd: (byte)0x5a, Offset: 0x50, Name: "YM3812"),
            (Cmd: (byte)0x5b, Offset: 0x54, Name: "YM3526"), (Cmd: (byte)0x5e, Offset: 0x5c, Name: "YMF262") })
        {
            var opl = new List<byte>(); OplTone(opl, chip.Cmd); Wait(opl, Rate / 2); opl.Add(0x66);
            var rendered = Render(File(opl, (chip.Offset, chip.Name == "YMF262" ? 14318180u : 3579545u)), Rate / 4);
            int renderedPeak = rendered.Max(sample => Math.Abs((int)sample));
            check(renderedPeak > 32, $"{chip.Name} register stream produces audio: peak={renderedPeak}");
        }

        foreach (var chip in new[] { (Cmd: (byte)0xa0, Offset: 0x74, Clock: 1789750u, Name: "AY8910"),
            (Cmd: (byte)0x58, Offset: 0x4c, Clock: 8000000u, Name: "YM2610 SSG") })
        {
            var psg = new List<byte>();
            Write(psg, chip.Cmd, 0, 100); Write(psg, chip.Cmd, 1, 0);
            Write(psg, chip.Cmd, 7, 0x3e); Write(psg, chip.Cmd, 8, 15);
            Wait(psg, Rate / 2); psg.Add(0x66);
            var rendered = Render(File(psg, (chip.Offset, chip.Clock)), Rate / 4);
            check(rendered.Any(sample => Math.Abs((int)sample) > 32), $"{chip.Name} register stream produces audio");
        }

        var opll = new List<byte>();
        Write(opll, 0x51, 0x30, 0x10); Write(opll, 0x51, 0x10, 0x80); Write(opll, 0x51, 0x20, 0x15);
        Wait(opll, Rate / 2); opll.Add(0x66);
        check(Render(File(opll, (0x10, 3579545)), Rate / 4).Any(sample => Math.Abs((int)sample) > 32),
            "YM2413 register stream produces audio");

        var ayDisplay = new List<byte>();
        Write(ayDisplay, 0xa0, 0, 100); Write(ayDisplay, 0xa0, 1, 0);
        Write(ayDisplay, 0xa0, 7, 0x3e); Write(ayDisplay, 0xa0, 8, 15);
        Write(ayDisplay, 0xa0, 0x80 | 0, 50); Write(ayDisplay, 0xa0, 0x80 | 1, 0);
        Write(ayDisplay, 0xa0, 0x80 | 7, 0x3e); Write(ayDisplay, 0xa0, 0x80 | 8, 15);
        Wait(ayDisplay, Rate); ayDisplay.Add(0x66);
        using (var displayProvider = new IndependentVgmWaveProvider(VgmFile.Load(File(ayDisplay, (0x74, (uint)(1789750 | 0x40000000))), "AY display")!))
        {
            displayProvider.Read(new byte[4096], 0, 4096);
            var snap = displayProvider.GetSnapshot();
            var ch1 = snap.MusicChannels.Single(c => c.Id == "AY8910:0");
            var ch2 = snap.MusicChannels.Single(c => c.Id == "AY8910 #2:0");
            check(ch1.IsKeyOn && ch1.NoteNumber.HasValue && Math.Abs(ch1.Frequency!.Value - 1118.59) < 1,
                "AY8910 exposes three pitched PSG keyboard lanes");
            check(ch2.IsKeyOn && ch2.NoteNumber.HasValue && Math.Abs(ch2.Frequency!.Value - 2237.18) < 2,
                "AY8910 #2 exposes three pitched PSG keyboard lanes for second chip");
            check(snap.RegisterBanks.Any(b => b.ChipName == "AY8910 #2"),
                "AY8910 #2 exposes register bank for second chip");
        }

        int gbPeriod = 0x6d6;
        var gbDisplay = new List<byte>();
        Write(gbDisplay, 0xb3, 0x16, 0x80); Write(gbDisplay, 0xb3, 0x14, 0x77); Write(gbDisplay, 0xb3, 0x15, 0x11);
        Write(gbDisplay, 0xb3, 0x02, 0xf0); Write(gbDisplay, 0xb3, 0x03, (byte)gbPeriod);
        Write(gbDisplay, 0xb3, 0x04, (byte)(0x80 | (gbPeriod >> 8))); Wait(gbDisplay, Rate); gbDisplay.Add(0x66);
        using (var displayProvider = new IndependentVgmWaveProvider(VgmFile.Load(File(gbDisplay, (0x80, 4194304)), "GB display")!))
        {
            displayProvider.Read(new byte[4096], 0, 4096);
            var channel = displayProvider.GetSnapshot().MusicChannels.Single(c => c.Id == "GameBoy:0");
            check(channel.IsKeyOn && channel.NoteNumber is >= 68 and <= 70 && channel.Pan == 3,
                "Game Boy pulse channel exposes clock-correct keyboard and stereo state");
        }

        int nesTimer = 253;
        var nesDisplay = new List<byte>();
        Write(nesDisplay, 0xb4, 0x15, 1); Write(nesDisplay, 0xb4, 0x00, 0x1f);
        Write(nesDisplay, 0xb4, 0x02, (byte)nesTimer); Write(nesDisplay, 0xb4, 0x03, (byte)(nesTimer >> 8));
        Wait(nesDisplay, Rate); nesDisplay.Add(0x66);
        using (var displayProvider = new IndependentVgmWaveProvider(VgmFile.Load(File(nesDisplay, (0x84, 1789773)), "NES display")!))
        {
            displayProvider.Read(new byte[4096], 0, 4096);
            var channel = displayProvider.GetSnapshot().MusicChannels.Single(c => c.Id == "NES APU:0");
            check(channel.IsKeyOn && channel.NoteNumber is >= 68 and <= 70 && Math.Abs(channel.Frequency!.Value - 440.4) < 2,
                "NES APU pulse channel exposes clock-correct keyboard state");
        }
        for (int duty = 0; duty < 4; duty++)
        {
            var dutyDisplay = new List<byte>();
            Write(dutyDisplay, 0xb4, 0x00, (byte)((duty << 6) | 0x1f));
            Wait(dutyDisplay, 64); dutyDisplay.Add(0x66);
            using var displayProvider = new IndependentVgmWaveProvider(VgmFile.Load(File(dutyDisplay, (0x84, 1789773)), "NES duty")!);
            displayProvider.Read(new byte[4096], 0, 4096);
            var channel = displayProvider.GetSnapshot().MusicChannels.Single(c => c.Id == "NES APU:0");
            check(channel.Waveform?.Count(value => value > 127) == new[] { 4, 8, 16, 24 }[duty] &&
                  channel.WaveformLabel == $"Duty {new[] { "12.5", "25.0", "50.0", "75.0" }[duty]}% : ",
                $"VGM NES pulse duty {duty} is drawn with its hardware ratio");
        }

        int fdsPitch = 1031; // about 440 Hz at the NES clock.
        var fdsDisplay = new List<byte>();
        Write(fdsDisplay, 0xb4, 0x3f, 0x02); // $4023 FDS I/O enable
        Write(fdsDisplay, 0xb4, 0x20, 0x9f); // $4080 direct volume
        Write(fdsDisplay, 0xb4, 0x29, 0x80); // $4089 wave write enabled
        for (int i = 0; i < 64; i++) Write(fdsDisplay, 0xb4, (byte)(0x40 + i), (byte)i);
        Write(fdsDisplay, 0xb4, 0x29, 0x00); // $4089 wave write disabled
        Write(fdsDisplay, 0xb4, 0x40, 0x3f); // ignored after write disable
        Write(fdsDisplay, 0xb4, 0x22, (byte)fdsPitch); // $4082/$4083 pitch
        Write(fdsDisplay, 0xb4, 0x23, (byte)(fdsPitch >> 8));
        Wait(fdsDisplay, Rate); fdsDisplay.Add(0x66);
        using (var displayProvider = new IndependentVgmWaveProvider(VgmFile.Load(File(fdsDisplay, (0x84, 1789773)), "FDS display")!))
        {
            displayProvider.Read(new byte[4096], 0, 4096);
            var channel = displayProvider.GetSnapshot().MusicChannels.Single(c => c.Id == "NES APU:5");
            check(channel.ChipName == "NES FDS" && channel.Kind == MusicChannelKind.Wavetable &&
                  channel.IsKeyOn && channel.NoteNumber is >= 68 and <= 70 && channel.Level > 0,
                "VGM FDS exposes its sixth keyboard and meter channel");
            check(channel.Waveform is { Length: 32 } fdsWave && fdsWave[0] < fdsWave[31] && fdsWave.Distinct().Count() == 32,
                "VGM FDS draws the 64-sample hardware wavetable, ignoring disabled writes");
        }

        int sccPeriod = 507;
        var sccDisplay = new List<byte>();
        for (int i = 0; i < 32; i++) sccDisplay.AddRange([0xd2, 0, (byte)i, (byte)(i * 4)]);
        sccDisplay.AddRange([0xd2, 1, 0, (byte)sccPeriod, 0xd2, 1, 1, (byte)(sccPeriod >> 8),
            0xd2, 2, 0, 15, 0xd2, 3, 0, 1]);
        Wait(sccDisplay, Rate); sccDisplay.Add(0x66);
        using (var displayProvider = new IndependentVgmWaveProvider(VgmFile.Load(File(sccDisplay, (0x9c, 3579545)), "SCC display")!))
        {
            displayProvider.Read(new byte[4096], 0, 4096);
            var channel = displayProvider.GetSnapshot().MusicChannels.Single(c => c.Id == "K051649:0");
            check(channel.IsKeyOn && channel.NoteNumber is >= 68 and <= 70,
                "Konami SCC exposes five clock-correct PSG keyboard lanes");
            check(channel.Waveform != null && channel.Waveform.Length == 32 && channel.Waveform[10] == (byte)((sbyte)(10 * 4) + 128),
                "Konami SCC exposes 32-sample wavetable waveform");
        }

        var rfDisplay = new List<byte>();
        Write(rfDisplay, 0xb0, 7, 0xc0); Write(rfDisplay, 0xb0, 0, 0xff); Write(rfDisplay, 0xb0, 1, 0xff);
        Write(rfDisplay, 0xb0, 2, 0); Write(rfDisplay, 0xb0, 3, 8); Write(rfDisplay, 0xb0, 8, 0xfe);
        Wait(rfDisplay, Rate); rfDisplay.Add(0x66);
        using (var displayProvider = new IndependentVgmWaveProvider(VgmFile.Load(File(rfDisplay, (0x40, 8000000)), "RF5C68 display")!))
        {
            displayProvider.Read(new byte[4096], 0, 4096);
            var channel = displayProvider.GetSnapshot().MusicChannels.Single(c => c.Id == "RF5C68:0");
            check(channel.IsKeyOn && channel.NoteNumber == 60 && channel.Frequency > 0 && channel.Pan == 3,
                "RF5C68 exposes relative PCM pitch, playback rate and pan per voice");
        }

        var c140Display = new List<byte>();
        c140Display.AddRange([0xd4, 0, 0, 0xff, 0xd4, 0, 1, 0xff, 0xd4, 0, 2, 0x80,
            0xd4, 0, 3, 0, 0xd4, 0, 5, 0x80]);
        Wait(c140Display, Rate); c140Display.Add(0x66);
        using (var displayProvider = new IndependentVgmWaveProvider(VgmFile.Load(File(c140Display, (0xa8, 8000000)), "C140 display")!))
        {
            displayProvider.Read(new byte[4096], 0, 4096);
            var channel = displayProvider.GetSnapshot().MusicChannels.Single(c => c.Id == "C140:0");
            check(channel.IsKeyOn && channel.NoteNumber == 60 && channel.Frequency > 0 && channel.Pan == 3,
                "Namco C140 exposes relative PCM pitch, playback rate and pan per voice");
        }

        var c140Duration = new List<byte>();
        const int c140SampleLength = 16000;
        var c140Rom = new byte[8 + c140SampleLength];
        Put(c140Rom, 0, c140SampleLength);
        for (int i = 0; i < c140SampleLength; i++)
            c140Rom[8 + i] = (byte)(90 * Math.Sin(2 * Math.PI * i / 64));
        Block(c140Duration, 0x8d, c140Rom);
        void C140Write(int register, byte value) => c140Duration.AddRange([0xd4, (byte)(register >> 8), (byte)register, value]);
        C140Write(0, 0xff); C140Write(1, 0xff); C140Write(2, 0x80); C140Write(3, 0);
        C140Write(4, 0); C140Write(6, 0); C140Write(7, 0);
        C140Write(8, (byte)(c140SampleLength >> 8)); C140Write(9, (byte)(c140SampleLength & 0xff));
        C140Write(10, 0); C140Write(11, 0); C140Write(5, 0x80);
        Wait(c140Duration, Rate); c140Duration.Add(0x66);
        var c140DurationWave = Render(File(c140Duration, (0xa8, 12288000)), 30000);
        check(c140DurationWave.Skip(26000 * 2).Take(2000 * 2).Any(sample => Math.Abs((int)sample) > 32),
            "Namco System 2 C140 sample remains audible at its corrected 21.33 kHz base rate");

        var k053260Display = new List<byte>();
        Write(k053260Display, 0xba, 0x08, 0xc0); Write(k053260Display, 0xba, 0x09, 0x0f);
        Write(k053260Display, 0xba, 0x0f, 0x7f); Write(k053260Display, 0xba, 0x2c, 4);
        Write(k053260Display, 0xba, 0x2f, 2); Write(k053260Display, 0xba, 0x28, 1);
        Wait(k053260Display, Rate); k053260Display.Add(0x66);
        using (var displayProvider = new IndependentVgmWaveProvider(VgmFile.Load(File(k053260Display, (0xac, 3579545)), "K053260 display")!))
        {
            displayProvider.Read(new byte[4096], 0, 4096);
            var channel = displayProvider.GetSnapshot().MusicChannels.Single(c => c.Id == "K053260:0");
            check(channel.IsKeyOn && channel.NoteNumber == 60 && channel.Frequency > 0 && channel.Pan == 3,
                "Konami K053260 exposes relative PCM pitch, playback rate and graded pan per voice");
        }

        var k054539Display = new List<byte>();
        k054539Display.AddRange([0xd3, 0, 0, 0, 0xd3, 0, 1, 0, 0xd3, 0, 2, 1,
            0xd3, 0, 3, 0, 0xd3, 0, 5, 0x10, 0xd3, 2, 0x14, 1]);
        Wait(k054539Display, Rate); k054539Display.Add(0x66);
        using (var displayProvider = new IndependentVgmWaveProvider(VgmFile.Load(File(k054539Display, (0xa0, 18432000)), "K054539 display")!))
        {
            displayProvider.Read(new byte[4096], 0, 4096);
            var channel = displayProvider.GetSnapshot().MusicChannels.Single(c => c.Id == "K054539:0");
            check(channel.IsKeyOn && channel.NoteNumber == 60 && channel.Frequency > 0 && channel.Pan == 3,
                "Konami K054539 exposes relative PCM pitch, playback rate and pan per voice");
        }

        var c352Display = new List<byte>();
        var c352Rom = new byte[8 + 256];
        BinaryPrimitives.WriteUInt32LittleEndian(c352Rom, 256);
        Array.Fill(c352Rom, (byte)0x40, 8, 256);
        Block(c352Display, 0x92, c352Rom);
        c352Display.AddRange([0xe1, 0, 0, 0xff, 0xff, 0xe1, 0, 2, 0x80, 0,
            0xe1, 0, 6, 0, 0xff, 0xe1, 0, 3, 0x40, 2, 0xe1, 2, 2, 0, 0]);
        Wait(c352Display, Rate); c352Display.Add(0x66);
        using (var displayProvider = new IndependentVgmWaveProvider(VgmFile.Load(File(c352Display, (0xdc, 24576000)), "C352 display")!))
        {
            displayProvider.Read(new byte[4096], 0, 4096);
            var channel = displayProvider.GetSnapshot().MusicChannels.Single(c => c.Id == "C352:0");
            check(channel.IsKeyOn && channel.NoteNumber == 60 && channel.Frequency > 0 && channel.Pan == 3,
                "Namco C352 exposes relative PCM pitch, playback rate and four-output pan per voice");
        }

        var multiPcmDisplay = new List<byte>();
        var multiPcmRom = new byte[8 + 512]; Put(multiPcmRom, 0, 512);
        multiPcmRom[8] = 0; multiPcmRom[9] = 1; multiPcmRom[10] = 0; // Sample 0 starts at 0x100.
        multiPcmRom[13] = 0xfe; multiPcmRom[14] = 0xff;             // 256 decoded samples.
        multiPcmRom[16] = 0xf0; multiPcmRom[18] = 0x0f;            // Fast attack, immediate test release.
        for (int i = 0; i < 256; i++) multiPcmRom[8 + 0x100 + i] = (byte)(90 * Math.Sin(2 * Math.PI * i / 32));
        Block(multiPcmDisplay, 0x89, multiPcmRom);
        void MultiPcmWrite(byte selector, byte register, byte value)
        {
            Write(multiPcmDisplay, 0xb5, 1, selector);
            Write(multiPcmDisplay, 0xb5, 2, register);
            Write(multiPcmDisplay, 0xb5, 0, value);
        }
        foreach (byte selector in new byte[] { 0, 8 })
        {
            MultiPcmWrite(selector, 1, 0);
            MultiPcmWrite(selector, 2, 0);
            MultiPcmWrite(selector, 5, selector == 0 ? (byte)1 : (byte)65);
        }
        MultiPcmWrite(0, 0, 0x70); MultiPcmWrite(0, 3, 0x10); MultiPcmWrite(0, 4, 0x80);
        MultiPcmWrite(8, 0, 0x90); MultiPcmWrite(8, 3, 0x18); MultiPcmWrite(8, 4, 0x80);
        Wait(multiPcmDisplay, 2048); multiPcmDisplay.Add(0x66);
        using (var multiPcmProvider = new IndependentVgmWaveProvider(VgmFile.Load(File(multiPcmDisplay, (0x88, 8000000)), "MultiPCM display")!))
        {
            var audio = new byte[1024 * 4]; multiPcmProvider.Read(audio, 0, audio.Length);
            var channels = multiPcmProvider.GetSnapshot().MusicChannels.Where(c => c.ChipName == "MultiPCM").ToArray();
            check(channels.Length == 2 && channels.All(c => c.IsInUse && c.IsKeyOn),
                "Sega Model 2 MultiPCM exposes each used physical voice instead of one mixed lane");
            var voice1 = channels.Single(c => c.Id == "MultiPCM:0");
            var voice8 = channels.Single(c => c.Id == "MultiPCM:7");
            check(voice1.NoteNumber == 60 && voice1.Pan == 1 && voice1.Level > .9 &&
                  voice8.NoteNumber == 67 && voice8.Pan == 2 && voice8.Level is > .2 and < .3,
                "MultiPCM voices expose relative pitch, graded level and stereo pan for keyboard graphics");
            check(Enumerable.Range(0, audio.Length / 2).Select(i => BinaryPrimitives.ReadInt16LittleEndian(audio.AsSpan(i * 2, 2))).Any(v => v != 0),
                "MultiPCM per-voice monitoring preserves audible PCM output");
        }

        var multiPcmPitch = new List<byte>();
        Block(multiPcmPitch, 0x89, multiPcmRom);
        void MultiPcmPitchWrite(byte register, byte value)
        {
            Write(multiPcmPitch, 0xb5, 1, 0);
            Write(multiPcmPitch, 0xb5, 2, register);
            Write(multiPcmPitch, 0xb5, 0, value);
        }
        MultiPcmPitchWrite(1, 0);
        MultiPcmPitchWrite(2, 0);
        MultiPcmPitchWrite(5, 1);
        MultiPcmPitchWrite(0, 0);
        MultiPcmPitchWrite(3, 0x10);
        MultiPcmPitchWrite(4, 0x80);
        Wait(multiPcmPitch, Rate / 2);
        double daytonaPitch = Frequency(Render(File(multiPcmPitch, (0x88, 8000000)), Rate / 2));
        double expectedDaytonaPitch = 8000000.0 / 180 / 32;
        check(Math.Abs(daytonaPitch - expectedDaytonaPitch) < expectedDaytonaPitch * .01,
            $"Sega Model 2 MultiPCM uses VGM's clock/180 pitch (expected {expectedDaytonaPitch:F2} Hz, got {daytonaPitch:F2} Hz)");

        // Model 2 logs use C3 to redirect the 1 MB sample window. Verify that
        // the VGM bank mask/value selects the requested ROM megabyte.
        var bankedMultiPcm = new VgmMultiPcmAudio(8000000);
        var bankedRom = new byte[0x400000];
        Array.Fill(bankedRom, (byte)0xff);
        bankedRom[0] = 0x10; bankedRom[1] = 0x01; bankedRom[2] = 0x00; // Banked 0x100100 start.
        bankedRom[5] = 0xff; bankedRom[6] = 0x00;                    // 256 samples.
        bankedRom[8] = 0xf0; bankedRom[10] = 0x0f;                 // Immediate attack/release.
        for (int i = 0; i < 256; i++) bankedRom[0x200100 + i] = (byte)(100 * Math.Sin(2 * Math.PI * i / 32));
        bankedMultiPcm.LoadRom(bankedRom.Length, 0, bankedRom);
        bankedMultiPcm.WriteBank(3, 0x20);
        void BankedWrite(byte register, byte value)
        {
            bankedMultiPcm.Write(1, 0); bankedMultiPcm.Write(2, register); bankedMultiPcm.Write(0, value);
        }
        BankedWrite(1, 0); BankedWrite(2, 0); BankedWrite(3, 0x10); BankedWrite(5, 1); BankedWrite(4, 0x80);
        int[][] bankedOutput = [new int[512], new int[512]];
        bankedMultiPcm.Update(bankedOutput, 512);
        check(bankedOutput[0].Any(value => value != 0) && bankedOutput[1].Any(value => value != 0),
            "Sega Model 2 MultiPCM C3 bank switching reaches the selected sample ROM megabyte");

        // Daytona's C3 1 MB bank maps the upper 512 KB to the next half-bank.
        // A separate silent lower half catches an incorrect shared bank address.
        var model2Rom = new byte[0x200000];
        Array.Fill(model2Rom, (byte)0xff);
        model2Rom[0] = 0x18; model2Rom[1] = 0x02; model2Rom[2] = 0x00;
        model2Rom[5] = 0xff; model2Rom[6] = 0x00;
        model2Rom[8] = 0xf0; model2Rom[10] = 0x0f;
        Array.Clear(model2Rom, 0x100200, 256);
        for (int i = 0; i < 256; i++)
            model2Rom[0x180200 + i] = (byte)(100 * Math.Sin(2 * Math.PI * i / 32));
        var model2Payload = new byte[8 + model2Rom.Length];
        Put(model2Payload, 0, (uint)model2Rom.Length);
        model2Rom.CopyTo(model2Payload, 8);
        var model2Commands = new List<byte>();
        Block(model2Commands, 0x89, model2Payload);
        model2Commands.AddRange([0xc3, 3, 0x10, 0]);
        void Model2Write(byte register, byte value)
        {
            Write(model2Commands, 0xb5, 1, 0);
            Write(model2Commands, 0xb5, 2, register);
            Write(model2Commands, 0xb5, 0, value);
        }
        Model2Write(1, 0); Model2Write(2, 0); Model2Write(5, 1);
        Model2Write(0, 0); Model2Write(3, 0x10); Model2Write(4, 0x80);
        Wait(model2Commands, 2048);
        var model2Output = Render(File(model2Commands, (0x88, 8000000)), 2048);
        check(model2Output.Max(sample => Math.Abs((int)sample)) > 1000,
            "Sega Model 2 C3 one-megabyte bank preserves the upper half's sample data");

        var routed = new List<byte>();
        Block(routed, 0x01, [1, 2, 3]);
        routed.AddRange([0x68, 0x66, 0x01, 0, 0, 0, 0x20, 0, 0, 3, 0, 0]);
        Block(routed, 0xc0, [0x30, 0, 1, 2]);
        Block(routed, 0xc1, [0x30, 0, 1, 2]);
        Block(routed, 0xc2, [0x00, 0x80, 1, 2]);
        foreach (byte command in new byte[] { 0x51, 0x58, 0x5a, 0x5b, 0x5c, 0x5d, 0x5e, 0xa0,
            0xb0, 0xb1, 0xb2, 0xb3, 0xb4, 0xb5, 0xb8, 0xba, 0xbf })
            Write(routed, command, 0x12, 0x34);
        routed.AddRange([0xd0, 0, 0x12, 0x34, 0xd1, 0, 0x12, 0x34]);
        routed.AddRange([0xd2, 0, 0x12, 0x34, 0xd3, 0, 0x12, 0x34, 0xd4, 0, 0x12, 0x34]);
        routed.AddRange([0xc1, 0x12, 0, 0x34, 0xc2, 0x12, 0, 0x34, 0xc3, 0, 0x12, 0x34, 0xc4, 0, 0x34, 0x12]);
        routed.AddRange([0xe1, 0x12, 0, 0x34, 0]);
        Wait(routed, 32); routed.Add(0x66);
        var routedFile = File(routed, (0x10, 3579545), (0x4c, 8000000), (0x50, 3579545), (0x54, 3579545),
            (0x58, 3579545), (0x68, 16934400), (0x5c, 14318180), (0x74, 1789750), (0x70, 23011361),
            (0x80, 4194304), (0x84, 1789773), (0x98, 1000000), (0x9c, 3579545), (0xa0, 18432000),
            (0xa8, 8000000), (0xac, 3579545), (0xdc, 24576000), (0x40, 8000000), (0x60, 33868800),
            (0x64, 16934400), (0x6c, 12500000), (0x88, 8000000), (0xb4, 4000000), (0xe0, 3579545));
        using (var routedProvider = new IndependentVgmWaveProvider(VgmFile.Load(routedFile, "additional chips")!))
        {
            routedProvider.Read(new byte[4096], 0, 4096);
            var names = routedProvider.GetSnapshot().RegisterBanks.Select(bank => bank.ChipName).ToHashSet();
            foreach (string name in new[] { "YM2413", "YM2610", "YM3812", "YM3526", "Y8950", "YMZ280B", "YMF262",
                "AY8910", "PWM", "GameBoy", "NES APU", "OKIM6295", "K051649", "K053260", "K054539", "C140", "C352" })
                check(names.Contains(name), $"{name} command routing exposes a register bank");
            foreach (string name in new[] { "YMF278B", "YMF271", "RF5C68", "RF5C164", "MultiPCM", "QSound", "IremGA20" })
                check(names.Contains(name), $"{name} command routing exposes a register bank");
        }

        foreach (var chip in new[] { (Cmd: (byte)0x52, Offset: 0x2c, Clock: 7670454u, Divider: 144),
            (Cmd: (byte)0x55, Offset: 0x44, Clock: 4000000u, Divider: 72),
            (Cmd: (byte)0x56, Offset: 0x48, Clock: 7987200u, Divider: 144) })
        {
            foreach (uint clock in new[] { chip.Clock, chip.Clock * 9 / 10 })
            {
                var commands = new List<byte>(); Tone(commands, chip.Cmd); Wait(commands, Rate); commands.Add(0x66);
                double expected = 644.0 * clock * 8 / (chip.Divider * 1048576.0);
                double actual = Frequency(Render(File(commands, (chip.Offset, clock)), 22050));
                check(Math.Abs(actual / expected - 1) < .015, $"chip {chip.Cmd:X2} clock {clock}: measured {actual:F2} Hz, expected {expected:F2} Hz");
            }
        }

        foreach (var chip in new[] { (Cmd: (byte)0x55, Offset: 0x44, Clock: 4000000u, Divider: 32),
            (Cmd: (byte)0x56, Offset: 0x48, Clock: 7987200u, Divider: 64) })
        {
            var ssg = new List<byte>();
            Write(ssg, chip.Cmd, 0, 200); Write(ssg, chip.Cmd, 1, 0);
            Write(ssg, chip.Cmd, 7, 0x3e); Write(ssg, chip.Cmd, 8, 15);
            Wait(ssg, Rate); ssg.Add(0x66);
            double expected = chip.Clock / (chip.Divider * 200.0);
            double actual = Frequency(Render(File(ssg, (chip.Offset, chip.Clock)), 22050));
            check(Math.Abs(actual / expected - 1) < .015, $"SSG {chip.Cmd:X2}: measured {actual:F2} Hz, expected {expected:F2} Hz");
        }

        var combined = new List<byte>();
        Tone(combined, 0x52, 644); Write(combined, 0x52, 0xb4, 0x80);
        Tone(combined, 0x56, 900); Write(combined, 0x56, 0xb4, 0x40);
        Wait(combined, Rate); combined.Add(0x66);
        var combinedWave = Render(File(combined, (0x2c, 7670454), (0x48, 7987200)), 22050);
        check(Math.Abs(Frequency(combinedWave) - 261.72) < 1 && Math.Abs(Frequency(combinedWave, 1) - 380.86) < 1,
            "simultaneous YM2612 and YM2608 retain independent registers and clocks");

        // Direct DAC writes: bipolar waveform, pan and DAC disable, not merely nonzero/DC output.
        var dac = new List<byte>();
        Write(dac, 0x52, 0x2b, 0x80); Write(dac, 0x53, 0xb6, 0x80);
        for (int i = 0; i < 200; i++) { Write(dac, 0x52, 0x2a, (byte)(i % 2 == 0 ? 0 : 255)); Wait(dac, 20); }
        Write(dac, 0x52, 0x2b, 0); Wait(dac, 200); dac.Add(0x66);
        var wave = Render(File(dac, (0x2c, 7670454)), 4200);
        check(wave.Where((_, i) => i % 2 == 0).Min() < -1000 && wave.Max() > 1000, "YM2612 direct DAC writes produce bipolar audio including sample zero");
        check(wave.Where((_, i) => i % 2 == 1).Distinct().Count() == 1, "YM2612 DAC pan suppresses right-channel AC audio (core retains hardware DC bias)");
        check(wave.Skip(8100).Distinct().Count() == 1, "YM2612 DAC disable settles to the core DC baseline after resampling");
        check(wave.SequenceEqual(Render(File(dac, (0x2c, 7670454)), 4200, 17)), "DAC timeline is independent of output buffer size");

        // Legacy PCM banks concatenate; seek selects samples across block boundaries.
        var legacy = new List<byte>();
        Write(legacy, 0x52, 0x2b, 0x80);
        Block(legacy, 0, [128, 0]); Block(legacy, 0, [255, 128]);
        legacy.AddRange([0xe0, 1, 0, 0, 0, 0x81, 0x81, 0x81]);
        Wait(legacy, 2); legacy.Add(0x66);
        wave = Render(File(legacy, (0x2c, 7670454)), 5);
        var directLegacy = new List<byte>();
        Write(directLegacy, 0x52, 0x2b, 0x80);
        Write(directLegacy, 0x52, 0x2a, 0); Wait(directLegacy, 1);
        Write(directLegacy, 0x52, 0x2a, 255); Wait(directLegacy, 1);
        Write(directLegacy, 0x52, 0x2a, 128); Wait(directLegacy, 3); directLegacy.Add(0x66);
        check(wave.SequenceEqual(Render(File(directLegacy, (0x2c, 7670454)), 5)),
            "legacy DAC seek across concatenated banks matches explicit timed DAC writes");

        // Fast-call block selection, interleaved samples, reverse loop and explicit stop.
        var stream = new List<byte>();
        Write(stream, 0x52, 0x2b, 0x80);
        Block(stream, 0, [128, 128]); Block(stream, 0, [128, 0, 128, 255]);
        stream.AddRange([0x90, 0, 2, 0, 0x2a, 0x91, 0, 0, 2, 1, 0x92, 0, 0xb9, 0x01, 0, 0,
            0x95, 0, 1, 0, 0x11]);
        Wait(stream, 800); stream.AddRange([0x94, 0]); Write(stream, 0x52, 0x2a, 128);
        Wait(stream, 800); stream.Add(0x66);
        wave = Render(File(stream, (0x2c, 7670454)), 1600, 37);
        check(Enumerable.Range(0, 8).All(i => i % 2 == 0 ? wave[(50 + i * 100) * 2] > 2000 : wave[(50 + i * 100) * 2] < -1000),
            "DAC fast-call uses selected block, step/base, reverse and loop");
        check(wave.Skip(1700).Distinct().Count() == 1, "stopping a DAC stream prevents subsequent writes");

        // SegaPCM ROM upload and channel registers, at native 31250 Hz resampled to 44100 Hz.
        var sega = new List<byte>();
        var rom = new byte[8 + 65536]; Put(rom, 0, 65536);
        for (int i = 8; i < rom.Length; i++) rom[i] = (byte)(128 + 90 * Math.Sin(2 * Math.PI * (i - 8) / 64));
        Block(sega, 0x80, rom);
        void S(int address, byte value) => sega.AddRange([0xc0, (byte)address, (byte)(address >> 8), value]);
        S(2, 64); S(3, 0); S(4, 0); S(5, 0); S(6, 0xfe); S(7, 128); S(0x84, 0); S(0x85, 0); S(0x86, 2);
        Wait(sega, Rate); sega.Add(0x66);
        var segaFile = File(sega, (0x38, 4000000), (0x3c, 0x0070000c));
        wave = Render(segaFile, 22050);
        double segaPitch = Frequency(wave);
        check(Math.Abs(segaPitch - 244.140625) < 2, $"SegaPCM ROM/register playback resampled at correct pitch: {segaPitch:F2} Hz");
        check(wave.Where((_, i) => i % 2 == 1).All(x => x == 0), "SegaPCM left/right volumes are independent");
        check(wave.SequenceEqual(Render(segaFile, 22050, 17)), "SegaPCM resampler preserves phase across buffers");

        using (var segaProvider = new IndependentVgmWaveProvider(VgmFile.Load(segaFile, "SegaPCM monitor")!))
        {
            segaProvider.Read(new byte[4], 0, 4);
            var channel = segaProvider.GetSnapshot().MusicChannels.Single(c => c.Id == "SegaPCM:0");
            check(channel.IsInUse && channel.IsKeyOn && channel.NoteNumber == 60 && channel.Pan == 2 && channel.Level > .49,
                "SegaPCM monitor uses the physical voice, delta pitch, stereo volume and active flag");
            check(segaProvider.GetSnapshot().MusicChannels.Count(c => c.ChipName == "SegaPCM") == 1,
                "SegaPCM monitor omits untouched physical voices instead of hiding used channels");
        }

        // X68000 OKIM6258: control, data activity and mute-bit pan must drive the PCM8 lane.
        var x68 = new List<byte>();
        x68.AddRange([0xb7, 0, 2, 0xb7, 2, 1, 0xb7, 1, 0xf7]);
        Wait(x68, 256); x68.AddRange([0xb7, 0, 1, 0x66]);
        using (var x68Provider = new IndependentVgmWaveProvider(VgmFile.Load(File(x68, (0x30, 4000000), (0x90, 4000000)), "X68000 PCM")!))
        {
            x68Provider.Read(new byte[128], 0, 128);
            var pcm = x68Provider.GetSnapshot().MusicChannels.Single(c => c.Id == "OKIM6258:0");
            check(pcm.IsInUse && pcm.IsKeyOn && pcm.Pan == 2 && pcm.Level > 0,
                "X68000 PCM8 lane follows OKIM6258 play, ADPCM data and left-only pan");
            x68Provider.Read(new byte[1024], 0, 1024);
            pcm = x68Provider.GetSnapshot().MusicChannels.Single(c => c.Id == "OKIM6258:0");
            check(!pcm.IsKeyOn && pcm.Level == 0, "X68000 PCM8 lane clears immediately on OKIM6258 stop");
        }

        var x68Audio = new List<byte> { 0xb7, 0, 2, 0xb7, 2, 0 };
        for (int i = 0; i < 220; i++)
        {
            x68Audio.AddRange([0xb7, 1, (byte)(i % 2 == 0 ? 0x77 : 0xff)]);
            Wait(x68Audio, 23);
        }
        x68Audio.AddRange([0xb7, 0, 1, 0x66]);
        wave = Render(File(x68Audio, (0x30, 4000000), (0x90, 4000000)), 5000, 37);
        check(wave.Max(sample => Math.Abs((int)sample)) > 1000,
            "X68000 OKIM6258 PCM is resampled from its native clock and remains audible at 16-bit mix level");

        var x68Stream = new List<byte>();
        Block(x68Stream, 4, Enumerable.Range(0, 240).Select(i => (byte)(i % 2 == 0 ? 0x77 : 0xff)).ToArray());
        x68Stream.AddRange([0xb7, 0, 2,
            0x90, 0, 0x17, 0, 1,
            0x91, 0, 4, 1, 0,
            0x92, 0, 0x42, 0x0f, 0, 0,
            0x95, 0, 0, 0, 0]);
        Wait(x68Stream, 5000);
        x68Stream.AddRange([0xb7, 0, 1, 0x66]);
        wave = Render(File(x68Stream, (0x30, 4000000), (0x90, 4000000)), 5000, 37);
        check(wave.Max(sample => Math.Abs((int)sample)) > 1000,
            "X68000 OKIM6258 PCM data-bank stream reaches the ADPCM data port");

        var ending = new List<byte>();
        Tone(ending, 0x52); Wait(ending, 100); ending.Add(0x66);
        using (var endingProvider = new IndependentVgmWaveProvider(VgmFile.Load(File(ending, (0x2c, 7670454)), "ending drain")!))
        {
            int finished = 0;
            endingProvider.PlaybackFinished += () => finished++;
            byte[] finalAudio = new byte[200 * 4];
            int read = endingProvider.Read(finalAudio, 0, finalAudio.Length);
            int next = endingProvider.Read(finalAudio, 0, finalAudio.Length);
            check(read > 0 && read <= finalAudio.Length && next == 0 && !endingProvider.IsPlaying && finished == 0,
                "VGM returns final audio then EOF so WaveOut drains without an early stop event");
        }

        // PC Engine / TurboGrafx-16 HuC6280: waveform RAM, pitch, balance and six display lanes.
        var tg16 = new List<byte>();
        void H(byte register, byte value) => tg16.AddRange([0xb9, register, value]);
        H(0, 0); H(1, 0xff); H(4, 0x1f); H(5, 0x4c);
        for (int i = 0; i < 32; i++) H(6, (byte)Math.Clamp((int)Math.Round(15 + 15 * Math.Sin(2 * Math.PI * i / 32)), 0, 31));
        const int tgPeriod = 254;
        H(2, (byte)tgPeriod); H(3, (byte)(tgPeriod >> 8)); H(4, 0x9f);
        Wait(tg16, Rate); tg16.Add(0x66);
        var tgFile = File(tg16, (0xa4, 3579545));
        wave = Render(tgFile, 22050);
        double tgPitch = Frequency(wave);
        check(wave.Max(sample => Math.Abs((int)sample)) > 1000 && Math.Abs(tgPitch - 440.4) < 4,
            $"TG16 HuC6280 waveform audio renders at the programmed pitch: {tgPitch:F2} Hz");
        using (var tgProvider = new IndependentVgmWaveProvider(VgmFile.Load(tgFile, "TG16 monitor")!))
        {
            tgProvider.Read(new byte[4], 0, 4);
            var snapshot = tgProvider.GetSnapshot();
            var channel = snapshot.MusicChannels.Single(c => c.Id == "HuC6280:0");
            check(snapshot.MusicChannels.Count(c => c.ChipName == "HuC6280") == 6 && channel.IsInUse &&
                  channel.IsKeyOn && channel.NoteNumber.HasValue && channel.Pan == 3 && channel.Level > .75,
                "TG16 monitor exposes six HuC6280 lanes with keyboard, meter and pan state");
            check(channel.Waveform != null && channel.Waveform.Length == 32 && channel.Waveform[0] > 0,
                "TG16 monitor exposes 32-sample HuC6280 wavetable waveform");
            check(Math.Abs(channel.PanLeftLevel!.Value - 4 / 15.0) < .001 &&
                  Math.Abs(channel.PanRightLevel!.Value - 12 / 15.0) < .001,
                "TG16 monitor preserves graded HuC6280 left/right balance");
            check(snapshot.RegisterBanks.Any(bank => bank.ChipName == "HuC6280" && bank.Values[0x20] == (byte)tgPeriod),
                "TG16 HuC6280 register image is available in the full player");
        }

        // Mega Drive lane order keeps FM, three PSG tones, noise and DAC within twelve rows.
        var mega = new List<byte>();
        Tone(mega, 0x52);
        mega.AddRange([0x50, 0x8a, 0x50, 0x10, 0x50, 0x90]);
        Write(mega, 0x52, 0x2b, 0x80); Write(mega, 0x52, 0x2a, 0xff);
        Wait(mega, 4); mega.Add(0x66);
        using (var megaProvider = new IndependentVgmWaveProvider(VgmFile.Load(File(mega, (0x0c, 3579545), (0x2c, 7670454)), "Mega Drive monitor")!))
        {
            megaProvider.Read(new byte[4], 0, 4);
            var channels = megaProvider.GetSnapshot().MusicChannels;
            check(channels.Length == 11 && channels.Count(c => c.ChipName == "SN76489") == 4 &&
                  channels.Any(c => c.Id == "YM2612:DAC" && c.IsKeyOn && c.Level > 0),
                "Mega Drive monitor exposes six FM, four PSG/noise and one DAC lane within twelve rows");
            check(channels.Single(c => c.Id == "SN76489:0").NoteNumber.HasValue,
                "Mega Drive PSG tone exposes a keyboard note");
        }

        // Dual SN76489 test: chip 1 via 0x50, chip 2 via 0x30 / 0x3f
        var dualPsgData = new List<byte>();
        // Chip 1: Tone 0 (~440Hz: divider = 254 = 0x0fe -> 0x8e, 0x0f), vol max (0x90)
        dualPsgData.AddRange([0x50, 0x8e, 0x50, 0x0f, 0x50, 0x90]);
        // Chip 2: Tone 0 (~880Hz: divider = 127 = 0x07f -> 0x8f, 0x07), vol max (0x90), GG stereo (0xff)
        dualPsgData.AddRange([0x30, 0x8f, 0x30, 0x07, 0x30, 0x90, 0x3f, 0xff]);
        Wait(dualPsgData, 4); dualPsgData.Add(0x66);
        using (var dualPsgProvider = new IndependentVgmWaveProvider(VgmFile.Load(File(dualPsgData, (0x0c, (uint)(3579545 | 0x40000000))), "Dual PSG")!))
        {
            dualPsgProvider.Read(new byte[4], 0, 4);
            var channels = dualPsgProvider.GetSnapshot().MusicChannels;
            check(channels.Count(c => c.ChipName == "SN76489") == 4,
                "First SN76489 exposes 4 channels");
            check(channels.Count(c => c.ChipName == "SN76489 #2") == 4,
                "Second SN76489 exposes 4 channels");
            var psg1 = channels.Single(c => c.Id == "SN76489:0");
            var psg2 = channels.Single(c => c.Id == "SN76489 #2:0");
            check(psg1.IsKeyOn && psg1.Level > 0 && psg1.NoteNumber.HasValue,
                "SN76489 #1 has key-on, level meter, and pitch note");
            check(psg2.IsKeyOn && psg2.Level > 0 && psg2.NoteNumber.HasValue,
                "SN76489 #2 has key-on, level meter, and pitch note");
        }

        using (var ndPsgProvider = new VgmRegisterPlaybackMonitor(VgmFile.Load(File(dualPsgData, (0x0c, (uint)(3579545 | 0x40000000))), "ND PSG")!, true))
        {
            ndPsgProvider.Advance(1);
            var channels = ndPsgProvider.GetSnapshot().MusicChannels;
            check(channels.Where(c => c.ChipName.StartsWith("SN764")).All(c => c.IsHardwareOutput),
                "Dual SN76489 channels are all flagged as IsHardwareOutput when ND6 is active");
        }

        // A reserved 4D command has two operands in VGM 1.60+. Its second operand 0x66 must not be read as end of data.
        var reservedData = new List<byte> { 0x4d, 0x00, 0x66 };
        reservedData.AddRange([0x50, 0x8e, 0x50, 0x0f, 0x50, 0x90]);
        Wait(reservedData, 4); reservedData.Add(0x66);
        using (var reservedMonitor = new VgmRegisterPlaybackMonitor(VgmFile.Load(File(reservedData, (0x0c, 3579545u)), "Reserved 4D")!))
        {
            reservedMonitor.Advance(1);
            check(reservedMonitor.GetSnapshot().MusicChannels.Any(c => c.Id == "SN76489:0" && c.IsKeyOn),
                "Display monitor skips both operands of a reserved 4D command");
        }

        // OKIM6295 pin 7 flag (bit 31) preserves divisor 132 (2MHz / 132 = 15151Hz) and exposes four PCM lanes.
        var okiData = new List<byte>();
        okiData.AddRange([0xb8, 0x00, 0x10]); // OKIM6295 command
        Wait(okiData, 4); okiData.Add(0x66);
        using (var okiProvider = new IndependentVgmWaveProvider(VgmFile.Load(File(okiData, (0x98, 0x801e8480u)), "OKIM6295 monitor")!))
        {
            okiProvider.Read(new byte[4], 0, 4);
            var channels = okiProvider.GetSnapshot().MusicChannels;
            check(channels.Count(c => c.ChipName == "OKIM6295") == 4,
                "OKIM6295 monitor exposes four PCM channels");
            check(channels.All(c => c.ChipName == "OKIM6295" && c.Kind == MirrorFlux.Services.Music.MusicChannelKind.Adpcm && !c.IsKeyOn),
                "OKIM6295 idle voices retain ADPCM kind without fabricated key-on");
        }
    }
}
