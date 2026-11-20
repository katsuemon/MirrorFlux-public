using System.IO;
using MirrorFlux.Services.Music;

internal static class MdxRegression
{
    public static void Run(Action<bool, string> check)
    {
        using var backend = new MdxPlaybackBackend();
        check(backend.SupportedExtensions.Contains(".mdx", StringComparer.OrdinalIgnoreCase),
            "MDX playback backend is registered for MDX scores");

        // Exercise a valid standard MDX track/voice layout.
        byte[] testMdx = CreateTestMdx();
        check(MdxFile.ReadTitle(testMdx) == "TEST", "MDX internal title is available to the full-player selector");
        var core = new MxdrvCore(testMdx, null);
        var opmWrites = new List<(int Reg, byte Val)>();
        core.WriteOpmCallback = (reg, val) => opmWrites.Add((reg, val));

        check(core.Global.HasMdx == 1, "MxdrvCore initialized with MDX loaded");
        check(core.FmChannels[0].IsInUse, "MxdrvCore FM channel 0 is active");

        // Step ticks and verify OPM writes (voice loading, tempo, note-on)
        for (int i = 0; i < 20; i++)
        {
            core.Tick();
        }

        check(opmWrites.Count > 0, "MxdrvCore dispatched OPM register writes to YM2151");
        check(opmWrites.Any(w => w.Reg >= 0x60 && w.Reg <= 0x7f), "MxdrvCore configured carrier attenuation (TL) on OPM");
        check(opmWrites.Any(w => w.Reg >= 0x28 && w.Reg <= 0x2f), "MxdrvCore issued Key Code (KC) pitch writes to OPM");
        check(core.FmChannels[0].CurrentNote.HasValue, "MxdrvCore tracked note pitch for player telemetry");
        VerifyCommands(check);
        VerifyLfo(check);
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".mdx");
        string zipPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".zip");
        try
        {
            File.WriteAllBytes(path, testMdx);
            using var provider = new MdxWaveProvider(path);
            check(provider.CoreName == "ymfm", "MDX uses the standalone ymfm engine");
            byte[] audio = new byte[44100 * 4];
            provider.Read(audio, 0, audio.Length);
            check(audio.Any(value => value != 0), "Valid MDX loads through playback provider and generates audio");

            using (var zipFs = File.Create(zipPath))
            using (var zip = new System.IO.Compression.ZipArchive(zipFs, System.IO.Compression.ZipArchiveMode.Create))
            {
                var e1 = zip.CreateEntry("track01.mdx");
                using (var s1 = e1.Open()) s1.Write(testMdx);
                var e2 = zip.CreateEntry("sub/track02.mdx");
                using (var s2 = e2.Open()) s2.Write(testMdx);
            }
            var prefixes = MirrorFlux.Services.ArchiveService.ReadArchiveEntryPrefixes(zipPath, ["track01.mdx", "sub/track02.mdx"], 2048);
            check(prefixes.ContainsKey("track01.mdx") && prefixes.ContainsKey("sub/track02.mdx") &&
                  MdxFile.ReadTitle(prefixes["track01.mdx"]) == "TEST" && MdxFile.ReadTitle(prefixes["sub/track02.mdx"]) == "TEST",
                "ReadArchiveEntryPrefixes batch-reads MDX headers from ZIP in a single archive pass");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(zipPath)) File.Delete(zipPath);
        }
    }

    public static void Audit(string directory, string? pdxSearchPath = null)
    {
        if (File.Exists(directory))
        {
            MdxComparison.Run(directory, Path.Combine(Environment.CurrentDirectory, ".dotnet", "mdx-comparison"));
            return;
        }
        string[] files = Directory.EnumerateFiles(directory, "*.mdx", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        if (files.Length == 0) throw new InvalidOperationException("No MDX files were found: " + directory);

        bool heardPcm = false;
        bool expectedPcm = false;
        foreach (string path in files)
        {
            using var provider = new MdxWaveProvider(path, pdxSearchPath == null ? null : new[] { pdxSearchPath });
            if (!string.IsNullOrWhiteSpace(provider.PdxFilePath))
                expectedPcm = true;

            var buffer = new byte[4096];
            long nonzero = 0;
            var fmLanes = new HashSet<string>(StringComparer.Ordinal);
            var pcmLanes = new HashSet<string>(StringComparer.Ordinal);

            // Render twenty seconds without opening an audio device. This is long
            // enough to exercise score control flow and the PDX rhythm parts in
            // the supplied OutRun set while keeping the audit quick.
            int reads = 20 * provider.WaveFormat.AverageBytesPerSecond / buffer.Length;
            for (int i = 0; i < reads && !provider.Ended; i++)
            {
                provider.Read(buffer, 0, buffer.Length);
                nonzero += buffer.Count(value => value != 0);
                foreach (var channel in provider.Snapshot().Channels.Where(channel => channel.IsInUse))
                {
                    if (channel.Kind == MusicChannelKind.Fm) fmLanes.Add(channel.Id);
                    if (channel.Kind == MusicChannelKind.Pcm && (channel.IsKeyOn || channel.KeyboardNotes.Length > 0))
                        pcmLanes.Add(channel.Id);
                }
            }

            if (nonzero == 0 || fmLanes.Count == 0)
                throw new InvalidOperationException($"Missing MDX audio/FM activity: {Path.GetFileName(path)}");
            heardPcm |= pcmLanes.Count > 0;
            Console.WriteLine($"PASS MDX {Path.GetFileName(path)}: title={provider.Title}, nonzero={nonzero}, FM={fmLanes.Count}, PCM={pcmLanes.Count}");
        }

        if (expectedPcm && !heardPcm) throw new InvalidOperationException("No PDX/PCM8 activity was detected in the MDX set.");
        Console.WriteLine($"PASS MDX corpus: files={files.Length}, PDX/PCM8 activity detected={heardPcm}");
    }

    private static void VerifyLfo(Action<bool, string> check)
    {
        // Expected MXDRV pitch accumulator values for MP0, MP1 and MP2.
        int[][] expected = [[256,-512,-256,0,256], [256,256,256,256,-256], [512,768,512,256,0]];
        var step = typeof(MxdrvCore).GetMethod("L001050", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        for (byte mode = 0; mode < 3; mode++)
        {
            var core = new MxdrvCore(CreateScore([0xec, mode, 0, 4, 0, 1, 0xa4, 120, 0xf1, 0]), null);
            core.Tick();
            var ch = core.FmChannels[0];
            int[] values = new int[5];
            for (int i = 0; i < 5; i++) { step.Invoke(core, [ch]); values[i] = ch.PitchLfoOffset; }
            check(values.SequenceEqual(expected[mode]), $"MXDRV MP{mode} accumulator matches original waveform and first half-period");
        }
    }

    private static void VerifyCommands(Action<bool, string> check)
    {
        static (MxdrvCore Core, int Keys) Run(byte[] commands, int tracks = 1)
        {
            var core = new MxdrvCore(CreateScore([0xfd, 0, .. commands], tracks), null);
            int keys = 0;
            core.WriteOpmCallback = (reg, value) => { if (reg == 8 && (value & 0x78) != 0) keys++; };
            for (int i = 0; i < 40; i++) core.Tick();
            return (core, keys);
        }
        var repeat = Run([0xf6, 2, 0, 0xa4, 0, 0xf5, 0xff, 0xfb, 0xf1, 0]);
        check(repeat.Keys == 2 && repeat.Core.Global.Ended != 0, "MXDRV signed repeat offset plays exactly twice then ends");
        var escape = Run([0xf6, 2, 0, 0xa4, 0, 0xf4, 0, 3, 0xa6, 0, 0xf5, 0xff, 0xf6, 0xf1, 0]);
        check(escape.Keys == 3 && escape.Core.Global.Ended != 0, "MXDRV repeat escape skips final tail");
        byte[] loop = [0xa4, 0, 0xf1, 0xff, 0xfb];
        var tied = Run([0xf7, 0xa4, 3, 0xa6, 3, 0xf1, 0]);
        check(tied.Keys == 1, "FM tied pitch change does not retrigger the envelope");
        var longNote = new MxdrvCore(CreateScore([0xfd, 0, 0xa4, 255, 0xa6, 1, 0xf1, 0]), null);
        int longKeys = 0;
        longNote.WriteOpmCallback = (reg, value) => { if (reg == 8 && (value & 0x78) != 0) longKeys++; };
        for (int i = 0; i < 260; i++) longNote.Tick();
        check(longKeys == 2 && longNote.Ended, "256-tick notes end and advance instead of sustaining forever");
        var one = Run(loop); var two = Run(loop, 2);
        check(one.Core.Global.LoopCount > 1 && one.Core.Global.Ended == 0, "MXDRV signed song loop continues playback");
        check(one.Core.Global.LoopCount == two.Core.Global.LoopCount, "MXDRV counts complete song loops rather than individual tracks");
        // 自分自身へ戻るループ（音符・休符なし）は演奏を止めず、そのトラックを終了する。
        var selfLoop = Task.Run(() => Run([0xf1, 0xff, 0xfd]));
        check(selfLoop.Wait(TimeSpan.FromSeconds(20)) && selfLoop.Result.Keys == 0 && selfLoop.Result.Core.Global.Ended != 0,
            "MXDRV loop without notes ends the track instead of hanging");
        var afterNote = Task.Run(() => Run([0xa4, 0, 0xf1, 0xff, 0xfd]));
        check(afterNote.Wait(TimeSpan.FromSeconds(20)) && afterNote.Result.Keys == 1 && afterNote.Result.Core.Global.Ended != 0,
            "MXDRV self-referencing loop after a note ends only after playing it");
        var cut = Task.Run(() =>
        {
            byte[] score = CreateScore([0xfd, 0, 0xa4, 0, 0xf3]);
            int last = Array.LastIndexOf(score, (byte)0xf3);
            var core = new MxdrvCore(score[..(last + 1)], null);
            for (int i = 0; i < 40; i++) core.Tick();
            return core;
        });
        check(cut.Wait(TimeSpan.FromSeconds(20)) && cut.Result.Global.Ended != 0, "MXDRV command cut off at end of data ends the track without throwing");
        foreach (var (volume, command, expected) in new (byte, byte, byte)[] {
            (0x90, 0xfa, 0x91), (0x90, 0xf9, 0x8f), (8, 0xfa, 7), (8, 0xf9, 9),
            (0xff, 0xfa, 0xff), (0x80, 0xf9, 0x80) })
        {
            var result = Run([0xfb, volume, command, 0, 0xf1, 0]);
            check(result.Core.FmChannels[0].Volume == expected, $"MXDRV volume {volume:X2}/{command:X2} follows attenuation direction and limits");
        }
    }

    internal static byte[] CreateTestMdx() => CreateScore([0xfd, 0, 0xfb, 15, 0xff, 200, 0xa4, 60, 0xf1, 0]);

    internal static byte[] CreateScore(byte[] commands, int activeTracks = 1)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("TEST\r\n\x1a"));
        writer.Write((byte)0);
        int header = 20;
        int toneOffset = header + activeTracks * commands.Length + (9 - activeTracks) * 2;
        void Word(int value) { writer.Write((byte)(value >> 8)); writer.Write((byte)value); }
        Word(toneOffset);
        int offset = header;
        for (int i = 0; i < 9; i++) { Word(offset); offset += i < activeTracks ? commands.Length : 2; }
        for (int i = 0; i < 9; i++) writer.Write(i < activeTracks ? commands : new byte[] { 0xf1, 0 });
        byte[] voice = new byte[27];
        voice[1] = 7; voice[2] = 15;
        for (int i = 0; i < 4; i++) { voice[3 + i] = 1; voice[11 + i] = 31; voice[23 + i] = 15; }
        writer.Write(voice);
        return ms.ToArray();
    }
}
