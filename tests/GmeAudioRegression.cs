using System.IO;
using System.Diagnostics;
using MirrorFlux.Services.Music;

internal static class GmeAudioRegression
{
    public static void Run(Action<bool, string> check)
    {
        string path = Path.Combine(Path.GetTempPath(), "mf-gme-" + Guid.NewGuid().ToString("N") + ".nsf");
        try
        {
            byte[] file = new byte[128 + 64];
            "NESM\x1a"u8.CopyTo(file);
            file[5] = 1; file[6] = 2; file[7] = 1;
            file[9] = file[11] = file[13] = 0x80; file[12] = 0x20;
            file[110] = 0xff; file[111] = 0x40;
            byte[] init = [0xa9,1,0x8d,0x15,0x40,0xa9,0xbf,0x8d,0,0x40,0xa9,0xfd,0x8d,2,0x40,0xa9,8,0x8d,3,0x40,0x60];
            init.CopyTo(file, 128); file[128 + 0x20] = 0x60;
            File.WriteAllBytes(path, file);
            using var provider = new GmeWaveProvider(path);
            var buffer = new byte[4096];
            int nonzero = 0;
            for (int i = 0; i < 20; i++) { provider.Read(buffer, 0, buffer.Length); nonzero += buffer.Count(b => b != 0); }
            var frame = provider.Snapshot();
            check(nonzero > 1000 && frame.TrackCount == 2 && frame.Channels.Length >= 5, "NSF synthesis exposes track count and isolated voices");
            check(frame.Channels.Any(c => c.IsKeyOn && c.NoteNumber is >= 68 and <= 70), "NSF isolated 440 Hz pulse produces A4 keyboard estimate");
            check(!frame.Channels.Where(c => !c.IsKeyOn || c.Kind == MusicChannelKind.Noise).Any(c => c.NoteNumber == 60), "NSF noise and unpitched voices do not falsely light 60 (o4c)");
            provider.IsPaused = true; var before = provider.Snapshot().CurrentTime;
            provider.Read(buffer, 0, buffer.Length);
            check(buffer.All(b => b == 0) && provider.Snapshot().CurrentTime == before, "GME pause outputs silence without advancing the track");
            check(provider.SelectTrack(1) && provider.TrackIndex == 1 && provider.Snapshot().CurrentTime == TimeSpan.Zero, "GME track selection resets its playback timeline");
            check(!provider.SelectTrack(2), "GME rejects an out-of-range subsong");
            string nsfe = Path.ChangeExtension(path, ".nsfe");
            try
            {
                using (var writer = new BinaryWriter(File.Create(nsfe)))
                {
                    writer.Write("NSFE"u8);
                    writer.Write(10); writer.Write("INFO"u8);
                    writer.Write(new byte[] { 0, 0x80, 0, 0x80, 0x20, 0x80, 0, 0, 2, 0 });
                    writer.Write(64); writer.Write("DATA"u8); writer.Write(file.AsSpan(128));
                    writer.Write(0); writer.Write("NEND"u8);
                }
                using var extended = new GmeWaveProvider(nsfe);
                for (int i = 0; i < 20; i++) extended.Read(buffer, 0, buffer.Length);
                check(extended.TrackCount == 2 && extended.Snapshot().Channels.Any(c => c.IsKeyOn && c.NoteNumber is >= 68 and <= 70), "NSFE chunks decode with pitched keyboard activity");
            }
            finally { File.Delete(nsfe); }
            for (int duty = 0; duty < 4; duty++)
            {
                file[128 + 6] = (byte)((duty << 6) | 0x3f);
                File.WriteAllBytes(path, file);
                using var dutyProvider = new GmeWaveProvider(path);
                for (int i = 0; i < 20; i++) dutyProvider.Read(buffer, 0, buffer.Length);
                var square = dutyProvider.Snapshot().Channels.Single(c => c.Label == "Square 1");
                check(square.Waveform?.Count(value => value > 127) == new[] { 4, 8, 16, 24 }[duty] &&
                      square.WaveformLabel == $"Duty {new[] { "12.5", "25.0", "50.0", "75.0" }[duty]}% : ",
                    $"NSF Square 1 audio reveals duty ratio {duty}");
            }
            file[128 + 6] = 0xbf;
            byte[] fdsFile = new byte[128 + 0x81];
            "NESM\x1a"u8.CopyTo(fdsFile);
            fdsFile[5] = 1; fdsFile[6] = 1; fdsFile[7] = 1;
            fdsFile[9] = fdsFile[11] = fdsFile[13] = 0x80;
            fdsFile[12] = 0x80; // play routine at $8080
            fdsFile[0x7b] = 0x04; // FDS expansion
            var fdsProgram = new List<byte>();
            fdsProgram.AddRange([0xa9, 0x02, 0x8d, 0x23, 0x40]); // FDS I/O enable
            fdsProgram.AddRange([0xa9, 0x80, 0x8d, 0x89, 0x40]); // wavetable write enable
            fdsProgram.AddRange([0xa2, 0x00]); // LDX #0
            int loop = fdsProgram.Count;
            fdsProgram.AddRange([0x8a, 0x29, 0x3f, 0x9d, 0x40, 0x40, 0xe8, 0xe0, 0x40]); // table ramp
            int branch = fdsProgram.Count;
            fdsProgram.AddRange([0xd0, 0x00]); // BNE loop
            fdsProgram[branch + 1] = unchecked((byte)(loop - (branch + 2)));
            fdsProgram.AddRange([0xa9, 0x00, 0x8d, 0x89, 0x40,
                              0xa9, 0x9f, 0x8d, 0x80, 0x40,
                              0xa9, 0x07, 0x8d, 0x82, 0x40,
                              0xa9, 0x04, 0x8d, 0x83, 0x40, 0x60]);
            fdsProgram.CopyTo(fdsFile, 128);
            fdsFile[128 + 0x80] = 0x60;
            File.WriteAllBytes(path, fdsFile);
            using (var fds = new GmeWaveProvider(path))
            {
                var sound = new byte[4096];
                for (int i = 0; i < 20; i++) fds.Read(sound, 0, sound.Length);
                var fdsChannel = fds.Snapshot().Channels.Single(c => c.ChipName == "NES FDS");
                check(fds.Snapshot().Channels.Length == 6 && fdsChannel.Label == "FDS" &&
                      fdsChannel.Kind == MusicChannelKind.Wavetable && fdsChannel.IsKeyOn &&
                      fdsChannel.Level > 0 && fdsChannel.NoteNumber is >= 68 and <= 70,
                    "NSF FDS isolated voice drives the sixth keyboard and meter channel");
                check(fdsChannel.Waveform is { Length: 32 } wave && wave.Distinct().Count() > 8,
                    "NSF FDS draws a cycle of its isolated wavetable audio");
            }

            static byte[] ExpansionNsf(byte expansion, params (int Address, byte Value)[] writes)
            {
                var result = new byte[128 + 0x101];
                "NESM\x1a"u8.CopyTo(result);
                result[5] = result[6] = result[7] = 1;
                result[9] = result[11] = result[13] = 0x80;
                result[12] = 0x00; result[13] = 0x81;
                result[0x7b] = expansion;
                var program = new List<byte>();
                foreach (var (address, value) in writes)
                    program.AddRange([0xa9, value, 0x8d, (byte)address, (byte)(address >> 8)]);
                program.Add(0x60);
                program.CopyTo(result, 128);
                result[128 + 0x100] = 0x60;
                return result;
            }

            File.WriteAllBytes(path, ExpansionNsf(1,
                (0x9000, 0x3f), (0x9001, 0xfd), (0x9002, 0x80),
                (0xa000, 0x5f), (0xa001, 0xdd), (0xa002, 0x80),
                (0xb000, 0x30), (0xb001, 0xfd), (0xb002, 0x80)));
            using (var vrc6 = new GmeWaveProvider(path))
            {
                for (int i = 0; i < 20; i++) vrc6.Read(buffer, 0, buffer.Length);
                var voices = vrc6.Snapshot().Channels.Where(c => c.ChipName == "NES VRC6").ToArray();
                check(voices.Length == 3 && voices.Select(c => c.Label).ToHashSet().SetEquals(["PULSE1", "PULSE2", "SAW"]) &&
                      vrc6.Snapshot().SoundSourceDisplay.Contains("VRC6"),
                    $"NSF VRC6 separates two pulses and saw from the base APU ({string.Join(',', vrc6.Snapshot().Channels.Select(c => c.ChipName + ':' + c.Label))})");
                check(voices.All(c => c.Waveform is { Length: 32 } wave && wave.Distinct().Count() > 1),
                    "NSF VRC6 pulse and saw voices expose cycle waveforms");
            }

            File.WriteAllBytes(path, ExpansionNsf(2,
                (0x9010, 0x30), (0x9030, 0x11),
                (0x9010, 0x10), (0x9030, 0x98),
                (0x9010, 0x20), (0x9030, 0x15)));
            using (var vrc7 = new GmeWaveProvider(path))
            {
                for (int i = 0; i < 20; i++) vrc7.Read(buffer, 0, buffer.Length);
                var voices = vrc7.Snapshot().Channels.Where(c => c.ChipName == "NES VRC7").ToArray();
                check(voices.Length == 6 && voices.All(c => c.Kind == MusicChannelKind.Fm) &&
                      vrc7.Snapshot().SoundSourceDisplay.Contains("VRC7"),
                    "NSF VRC7 exposes six separate FM channels");
                check(voices.Any(c => c.IsKeyOn && c.Level > 0),
                    "NSF VRC7 FM voice drives its keyboard and level meter");
            }

            file[128] = 0x60;
            File.WriteAllBytes(path, file);
            using var silent = new GmeWaveProvider(path);
            var silence = new byte[4410 * 4];
            for (int i = 0; i < 100; i++) silent.Read(silence, 0, silence.Length);
            check(!silent.Ended && silent.Snapshot().CurrentTime.TotalSeconds == 10,
                "GME keeps silent sections on the shared timeline without premature termination");
            string gbsPath = Path.ChangeExtension(path, ".gbs");
            try
            {
                byte[] gbs = new byte[112 + 128]; "GBS"u8.CopyTo(gbs);
                gbs[3]=1; gbs[4]=2; gbs[5]=2; // Select second track by header.
                gbs[7]=gbs[9]=gbs[11]=0x40; gbs[10]=0x60; gbs[12]=0xfe; gbs[13]=0xff;
                byte[] program = [0x3e,0x80,0xe0,0x26, 0x3e,0x77,0xe0,0x24, 0x3e,0x11,0xe0,0x25,
                    0x3e,0x80,0xe0,0x11, 0x3e,0xf0,0xe0,0x12, 0x3e,0xd6,0xe0,0x13, 0x3e,0x86,0xe0,0x14, 0xc9];
                program.CopyTo(gbs,112); gbs[112+0x60]=0xc9; File.WriteAllBytes(gbsPath,gbs);
                using var gameBoy = new GmeWaveProvider(gbsPath);
                for(int i=0;i<20;i++) gameBoy.Read(buffer,0,buffer.Length);
                var state = gameBoy.Snapshot();
                check(state.TrackIndex==1 && state.TrackCount==2 && state.Channels.Length==4 && buffer.Any(b=>b!=0), "GBS renders four voices and honors its initial subsong");
                check(state.Channels.Any(c=>c.KeyboardNotes.Any(n=>n is >=68 and <=70)), "GBS pulse waveform lights its estimated A4 key");
            }
            finally { File.Delete(gbsPath); }
        }
        finally { File.Delete(path); }
    }
    public static void Audit(string path)
    {
        using var provider = new GmeWaveProvider(path);
        var timer = Stopwatch.StartNew();
        var buffer = new byte[16384];
        var active = new HashSet<string>(); var waves = new HashSet<string>(); int nonzero = 0;
        for (int i = 0; i < 600; i++)
        {
            int count = new[] { 508, 4096, 16384 }[i % 3];
            int read = provider.Read(buffer, 0, count); nonzero += buffer.AsSpan(0, read).ToArray().Count(b => b != 0);
            foreach (var channel in provider.Snapshot().Channels)
            {
                if (channel.IsKeyOn) active.Add($"{channel.Label}:{channel.NoteNumber}");
                if (channel.Waveform is { Length: 32 } wave && wave.Distinct().Count() > 1)
                    waves.Add(channel.Label);
            }
        }
        Console.WriteLine($"{provider.FormatName}: tracks={provider.TrackCount}, voices={provider.Snapshot().Channels.Length}, nonzero={nonzero}, audio={provider.Snapshot().CurrentTime.TotalSeconds:F2}s, render={timer.Elapsed.TotalSeconds:F2}s, chips={string.Join(',', provider.Snapshot().Channels.GroupBy(c => c.ChipName).Select(group => $"{group.Key}:{group.Count()}"))}, waves={string.Join(',', waves)}, notes={string.Join(',', active.Take(30))}");
        if (nonzero == 0 || active.Count == 0 || provider.Snapshot().CurrentTime.TotalSeconds < 23)
            throw new Exception("Missing audio/keyboard activity or premature track termination");
    }
}
