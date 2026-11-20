using System.IO;
using System.Buffers.Binary;
using NAudio.Wave;
using MirrorFlux.Services.Music;
using MirrorFlux.Services.Vgm;

internal static class MusicRegression
{
    public static void Run(Action<bool, string> check)
    {
        // 登録・切り替えの検査は、作業機に音声出力デバイスがなくても同じ結果になるよう、デバイスありとして行う。
        var deviceCount = AudioOutput.DeviceCount;
        AudioOutput.DeviceCount = () => 1;
        try { RunCore(check); }
        finally { AudioOutput.DeviceCount = deviceCount; }
    }

    private static void RunCore(Action<bool, string> check)
    {
        using var service = new MusicPlaybackService([new TestBackend("A", ".test-a"), new TestBackend("B", ".test-b")]);
        int starts = 0, stops = 0;
        service.PlaybackStarted += () => starts++;
        service.PlaybackStopped += () => stops++;
        check(service.CanPlay("track.TEST-A") && !service.CanPlay("track.nsf"), "music registry advertises registered formats only, case insensitive");
        check(service.Play("track.test-a") && starts == 1, "music registry starts first decoder");
        check(!service.Play("track.unknown") && service.CurrentFilePath == "track.test-a", "unknown format keeps the current session");
        service.TogglePause();
        check(service.IsPaused, "generic pause reaches current decoder");
        service.TogglePause();
        check(!service.IsPaused, "generic resume reaches current decoder");
        check(service.Play("track.test-b") && starts == 2 && stops == 1, "switching decoder stops previous session exactly once");
        var snapshot = service.GetSnapshot();
        check(snapshot.FormatName == "B" && snapshot.Channels.Length == 24 && snapshot.Channels[^1].Kind == MusicChannelKind.Midi,
            "format-independent snapshots retain variable channel counts and non-VGM instruments");
        service.Stop();
        check(!service.IsPlaying && stops == 2, "generic stop reaches active decoder");

        var gainBackend = new TestBackend("gain", ".gain");
        using (var gainService = new MusicPlaybackService([gainBackend], 40))
        {
            check(gainService.Play("song.gain") && Math.Abs(gainBackend.OutputGain - .4f) < .001f,
                "master volume applies before playback starts");
            gainService.SetFadeGain(.5f);
            check(Math.Abs(gainBackend.OutputGain - .2f) < .001f, "fade scales the configured master volume");
            gainService.Play("next.gain");
            check(Math.Abs(gainBackend.OutputGain - .4f) < .001f, "next track restores the master volume");
        }
        check(MusicFadeGain.At(TimeSpan.Zero) == 1f &&
              Math.Abs(MusicFadeGain.At(TimeSpan.FromSeconds(5)) - .5f) < .001f &&
              MusicFadeGain.At(TimeSpan.FromSeconds(10)) == 0f &&
              MusicFadeGain.At(TimeSpan.FromSeconds(12)) == 0f,
            "automatic track fade lasts ten seconds and reaches silence");
        var gainProvider = new OutputGainWaveProvider(new ConstantWaveProvider());
        var gainBuffer = new byte[8];
        gainProvider.SetGain(.5f);
        gainProvider.Read(gainBuffer, 0, gainBuffer.Length);
        check(BinaryPrimitives.ReadInt16LittleEndian(gainBuffer) == 8192 &&
              BinaryPrimitives.ReadInt16LittleEndian(gainBuffer.AsSpan(6)) == 8192,
            "output gain scales each stereo PCM sample");
        gainProvider.SetGain(0f);
        gainProvider.Read(gainBuffer, 0, gainBuffer.Length);
        check(gainBuffer.All(b => b == 0), "zero output gain silences the PCM stream");

        var analyzer = new AudioSpectrumAnalyzer();
        check(analyzer.Capture().All(b => b == 0), "spectrum is silent without audio");
        var dcAnalyzer = new AudioSpectrumAnalyzer();
        dcAnalyzer.AddSamples(Enumerable.Repeat(12000, AudioSpectrumAnalyzer.WindowSampleCount * 2).ToArray());
        check(dcAnalyzer.Capture().All(b => b == 0), "10 Hz spectrum excludes DC from its lowest band");
        var samples = new int[AudioSpectrumAnalyzer.WindowSampleCount * 2];
        for (int i = 0; i < samples.Length / 2; i++)
        {
            samples[i * 2] = (int)(20000 * Math.Sin(2 * Math.PI * 1000 * i / 44100));
            samples[i * 2 + 1] = -samples[i * 2];
        }
        analyzer.AddSamples(samples);
        var bands = analyzer.Capture();
        int peak = Array.IndexOf(bands, bands.Max());
        check(peak is >= 13 and <= 15 && bands[peak] > .85 && bands.Length == AudioSpectrumAnalyzer.BandCount &&
              AudioSpectrumAnalyzer.MinFrequency == 10 && AudioSpectrumAnalyzer.MaxFrequency == 22050,
            "stereo spectrum detects 1 kHz within the logarithmic 10 Hz-22.05 kHz range without cancelling opposite-phase channels");
        int PeakBand(double frequency)
        {
            var toneAnalyzer = new AudioSpectrumAnalyzer();
            var tone = new int[AudioSpectrumAnalyzer.WindowSampleCount * 2];
            for (int i = 0; i < tone.Length / 2; i++)
            {
                int value = (int)(20000 * Math.Sin(2 * Math.PI * frequency * i / 44100));
                tone[i * 2] = value;
                tone[i * 2 + 1] = value;
            }
            toneAnalyzer.AddSamples(tone);
            var toneBands = toneAnalyzer.Capture();
            return Array.IndexOf(toneBands, toneBands.Max());
        }
        int lowPeak = PeakBand(100), highPeak = PeakBand(10000), edgePeak = PeakBand(21000);
        check(lowPeak is >= 6 and <= 8 && highPeak is >= 20 and <= 22 && edgePeak == 23 &&
              lowPeak < peak && peak < highPeak && highPeak < edgePeak,
            $"10 Hz-22.05 kHz spectrum places 100 Hz, 1 kHz, 10 kHz and 21 kHz in ordered bands: {lowPeak}, {peak}, {highPeak}, {edgePeak}");
        var squareAnalyzer = new AudioSpectrumAnalyzer();
        var square = new int[AudioSpectrumAnalyzer.WindowSampleCount * 2];
        for (int i = 0; i < square.Length / 2; i++)
        {
            int value = Math.Sin(2 * Math.PI * 100 * i / 44100) >= 0 ? 20000 : -20000;
            square[i * 2] = value;
            square[i * 2 + 1] = value;
        }
        squareAnalyzer.AddSamples(square);
        var squareBands = squareAnalyzer.Capture();
        check(squareBands[0] < .05 && squareBands[1] < .05,
            $"100 Hz square wave does not create false 10/20 Hz activity: {squareBands[0]:F3}, {squareBands[1]:F3}");
        var noiseAnalyzer = new AudioSpectrumAnalyzer();
        var noise = new int[AudioSpectrumAnalyzer.WindowSampleCount * 2];
        var random = new Random(1234);
        for (int i = 0; i < noise.Length / 2; i++)
        {
            int value = random.Next(-20000, 20001);
            noise[i * 2] = value;
            noise[i * 2 + 1] = value;
        }
        noiseAnalyzer.AddSamples(noise);
        var noiseBands = noiseAnalyzer.Capture();
        check(noiseBands[^1] <= noiseBands.Average() + .05,
            $"wide 16-22 kHz band does not overstate flat-noise peaks: last={noiseBands[^1]:F3}, mean={noiseBands.Average():F3}");
        analyzer.AddSamples(new int[AudioSpectrumAnalyzer.WindowSampleCount * 2]);
        check(analyzer.Capture().All(b => b == 0), "spectrum clears after silence replaces the analysis window");

        var legacy = new VgmVisualSnapshot { MusicChannels = snapshot.Channels, SpectrumLevels = bands };
        var mapped = VgmPlaybackBackend.MapSnapshot(legacy);
        legacy.MusicChannels[0] = new() { Label = "changed" };
        bands[0] = .9f;
        check(mapped.Channels[0].Label != "changed" && mapped.SpectrumLevels[0] != .9f,
            "music display snapshot arrays are detached from the decoder frame");

        byte[] commands = [0x52, 0xff, 0x12, 0x53, 0xff, 0xab, 0x54, 0, 0x34,
                           0x55, 0, 0x40, 0x55, 1, 0, 0x55, 7, 0x3e, 0x55, 8, 15,
                           0x61, 0x10, 0, 0x54, 0, 0x56, 0x61, 0xff, 0, 0x66];
        var vgm = new byte[256 + commands.Length];
        "Vgm "u8.CopyTo(vgm);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(vgm.AsSpan(8), 0x171);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(vgm.AsSpan(0x34), 256 - 0x34);
        foreach (int offset in new[] { 0x2c, 0x30, 0x44, 0x48 })
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(vgm.AsSpan(offset), 4000000);
        commands.CopyTo(vgm, 256);
        using var provider = new IndependentVgmWaveProvider(VgmFile.Load(vgm, "register test")!);
        provider.Read(new byte[4], 0, 4);
        var banks = provider.GetSnapshot().RegisterBanks;
        var ssg = provider.GetSnapshot().MusicChannels.Single(c => c.Id == "YM2203:SSG:0");
        check(ssg.IsInUse && ssg.IsKeyOn && ssg.NoteNumber.HasValue, "SSG-only writes activate a pitched keyboard lane independently of FM");
        check(banks.Length == 6 && banks.All(b => b.Values.Length == 256), "YM registers expose one or two complete 256-byte ports per chip");
        check(banks.Single(b => b.ChipName == "YM2612" && b.Port == 0).Values[255] == 0x12 &&
              banks.Single(b => b.ChipName == "YM2612" && b.Port == 1).Values[255] == 0xab &&
              banks.Single(b => b.ChipName == "YM2151").Values[0] == 0x34,
              "register ports and chips retain independent writes including boundary addresses");
        legacy.RegisterBanks = banks;
        mapped = VgmPlaybackBackend.MapSnapshot(legacy);
        provider.Read(new byte[128], 0, 128);
        check(provider.GetSnapshot().RegisterBanks[0].Values[0] == 0x56, "register display follows later playback writes");
        check(banks[0].Values[0] == 0x34, "register capture is detached from subsequent playback writes");
        banks[0].Values[0] = 0xff;
        check(mapped.RegisterBanks[0].Values[0] == 0x34, "backend deep copies register values");

        var envelopeVgmData = new byte[257];
        "Vgm "u8.CopyTo(envelopeVgmData);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(envelopeVgmData.AsSpan(8), 0x171);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(envelopeVgmData.AsSpan(0x2c), 7670454);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(envelopeVgmData.AsSpan(0x34), 256 - 0x34);
        envelopeVgmData[256] = 0x66;
        var envelopeFile = VgmFile.Load(envelopeVgmData, "envelope test")!;
        var envelopeMonitor = new VgmChannelMonitor(envelopeFile);
        envelopeMonitor.WriteFm("YM2612", 0, 0xb0, 0x07); // Algorithm 7: all operators are carriers.
        envelopeMonitor.WriteFm("YM2612", 0, 0xb4, 0xc0); // Both speakers.
        foreach (byte register in new byte[] { 0x50, 0x54, 0x58, 0x5c }) envelopeMonitor.WriteFm("YM2612", 0, register, 12);
        foreach (byte register in new byte[] { 0x80, 0x84, 0x88, 0x8c }) envelopeMonitor.WriteFm("YM2612", 0, register, 8);
        envelopeMonitor.WriteFm("YM2612", 0, 0x28, 0xf0);
        MusicChannelSnapshot EnvelopeAt(double seconds) => envelopeMonitor.Capture(new VgmVisualSnapshot
        {
            IsPlaying = true,
            CurrentTime = TimeSpan.FromSeconds(seconds)
        }).Single(channel => channel.Id == "YM2612:FM:0");
        var attackStart = EnvelopeAt(0);
        double attackTime = attackStart.AttackSeconds!.Value;
        var attackMiddle = EnvelopeAt(attackTime / 2);
        var attackEnd = EnvelopeAt(attackTime);
        check(attackStart.UsesEnvelopeTiming && attackStart.Level == 0 && attackMiddle.Level is > .45 and < .55 && attackEnd.Level > .99,
            "FM meter rises according to carrier attack rate");
        envelopeMonitor.WriteFm("YM2612", 0, 0x28, 0x00);
        var releaseStart = EnvelopeAt(attackTime);
        double releaseTime = releaseStart.ReleaseSeconds!.Value;
        var releaseMiddle = EnvelopeAt(attackTime + releaseTime / 2);
        var releaseEnd = EnvelopeAt(attackTime + releaseTime);
        check(releaseStart.IsReleasing && releaseStart.Level > .99 && releaseMiddle.Level is > .45 and < .55 && releaseEnd.Level < .01,
            "FM meter starts release on key-off and follows carrier release rate");

        check(MirrorFlux.Controls.MusicDisplayDrawing.FormatMmlNote(60) == "o4c" &&
              MirrorFlux.Controls.MusicDisplayDrawing.FormatMmlNote(61) == "o4c+" &&
              MirrorFlux.Controls.MusicDisplayDrawing.FormatMmlNote(69) == "o4a" &&
              MirrorFlux.Controls.MusicDisplayDrawing.FormatMmlNote(75) == "o5d+",
              "MML note names format octave and note with plus sign for sharps (e.g. o4c, o4c+)");
        // 鍵盤（o0c〜o8b）より低い音を「o0」にまとめない。
        check(MirrorFlux.Controls.MusicDisplayDrawing.FormatMmlNote(5) == "o-1f" &&
              MirrorFlux.Controls.MusicDisplayDrawing.FormatMmlNote(12) == "o0c" &&
              MirrorFlux.Controls.MusicDisplayDrawing.FormatMmlNote(119) == "o8b" &&
              MirrorFlux.Controls.MusicDisplayDrawing.FormatMmlNote(120) == "o9c",
              "Notes below the keyboard keep their real octave instead of being shown as o0");

        // 半音より細かい音程: OPMのKC/KF、周波数、レート比と、鍵盤上の位置。
        check(Math.Abs(MusicPitch.FromOpm(0x4a, 0, 3579545) - 69) < 1e-9 &&
              Math.Abs(MusicPitch.FromOpm(0x4a, 0x80, 3579545) - 69.5) < 1e-9 &&
              Math.Abs(MusicPitch.FromOpm(0x4e, 0, 3579545) - 72) < 1e-9 &&
              Math.Abs(MusicPitch.FromOpm(0x4a, 0, 4000000) - (69 + 12 * Math.Log2(4000000 / 3579545.0))) < 1e-9 &&
              Math.Abs(MusicPitch.FromFrequency(440)!.Value - 69) < 1e-9 &&
              Math.Abs(MusicPitch.FromRatio(2)!.Value - 72) < 1e-9 && MusicPitch.FromRatio(0) == null,
              "Fine pitch follows OPM key code and key fraction, frequency and playback ratio");
        var fm = new MusicChannelSnapshot { Kind = MusicChannelKind.Fm, NoteNumber = 69, Frequency = 440 * Math.Pow(2, .2 / 12) };
        check(Math.Abs(MusicPitch.Display(fm)!.Value - 69.2) < 1e-9 &&
              MusicPitch.Display(fm with { IsNoteEstimated = true }) == null &&
              MusicPitch.Display(fm with { NoteNumber = 71 }) == null &&
              MusicPitch.Display(new MusicChannelSnapshot { Kind = MusicChannelKind.Pcm, NoteNumber = 60, Frequency = 22050 }) == null &&
              MusicPitch.Display(new MusicChannelSnapshot { Kind = MusicChannelKind.Pcm, NoteNumber = 60, Pitch = 59.7 }) == 59.7 &&
              MusicPitch.Display(new MusicChannelSnapshot { Kind = MusicChannelKind.Fm, NoteNumber = 60, Pitch = 62.4 }) == 62.4,
              "Keyboard fine pitch uses explicit pitch (following portamento), or tone frequency for FM/PSG, never estimated or mismatched notes");
        check(Math.Abs(MusicPitch.AlignOctave(51.0, 39) - 39) < 1e-9 && Math.Abs(MusicPitch.AlignOctave(52.3, 39) - 40.3) < 1e-9 &&
              Math.Abs(MusicPitch.AlignOctave(38.8, 39) - 38.8) < 1e-9,
              "Fine pitch can be shifted by whole octaves onto the lit key");
        check(MirrorFlux.Controls.MusicDisplayDrawing.KeyCenter(24, 24, 132, 8) == 4 &&
              MirrorFlux.Controls.MusicDisplayDrawing.KeyCenter(25, 24, 132, 8) == 8 &&
              MirrorFlux.Controls.MusicDisplayDrawing.KeyCenter(24.5, 24, 132, 8) == 6 &&
              MirrorFlux.Controls.MusicDisplayDrawing.KeyCenter(26, 24, 132, 8) == 12 &&
              MirrorFlux.Controls.MusicDisplayDrawing.KeyCenter(28.5, 24, 132, 8) == 24 &&
              MirrorFlux.Controls.MusicDisplayDrawing.KeyCenter(131, 24, 132, 8) == 500 &&
              MirrorFlux.Controls.MusicDisplayDrawing.KeyCenter(23.9, 24, 132, 8) == null &&
              MirrorFlux.Controls.MusicDisplayDrawing.KeyCenter(131.5, 24, 132, 8) == null,
              "Fine pitch position runs from key centers through black-key boundaries and stays inside the keyboard");
        KeyboardPitchRenderRegression.Run(check);
        SuspendPlaybackRegression.Run(check);
        NoAudioDeviceRegression.Run(check);

        string emptyNote = MirrorFlux.Controls.MusicDisplayDrawing.FormatNoteDisplay(null);
        string naturalNote = MirrorFlux.Controls.MusicDisplayDrawing.FormatNoteDisplay(60, false, 0);
        string sharpNote = MirrorFlux.Controls.MusicDisplayDrawing.FormatNoteDisplay(61, false, 23);
        string minusSingleNote = MirrorFlux.Controls.MusicDisplayDrawing.FormatNoteDisplay(60, false, -8);
        string lowNote = MirrorFlux.Controls.MusicDisplayDrawing.FormatNoteDisplay(1, true, -45);
        int length = MirrorFlux.Controls.MusicDisplayDrawing.NoteDisplayLength;
        check(new[] { emptyNote, naturalNote, sharpNote, minusSingleNote, lowNote }.All(s => s.Length == length && s.IndexOf('¢') == length - 1) &&
              emptyNote == "Note :         +0¢" &&
              naturalNote == "Note : o4c     +0¢" &&
              sharpNote == "Note : o4c+   +23¢" &&
              minusSingleNote == "Note : o4c     -8¢" &&
              lowNote == "Note : ~o-1c+ -45¢" &&
              MusicPitch.Cents(60.23, 60) == 23 &&
              MusicPitch.Cents(59.92, 60) == -8 &&
              MusicPitch.Cents(null, 60) == 0,
              "Note and cents keep the cents column fixed even for '~o-1c+' ('Note : o4c+   +23¢')");

        check(Math.Abs(MusicPitch.FromOpmPhaseStep(65984u * 16u, 3579545)!.Value - 69.0) < 1e-6 &&
              Math.Abs(MusicPitch.FromFmPhaseStep((uint)Math.Round(440.0 * 144.0 * 134217728.0 / 8000000.0), 8000000, 144)!.Value - 69.0) < 1e-3,
              "ymfm 10.17 fixed-point phase_step converts accurately to continuous semitone pitch");

        var timeline = new PitchTimeline();
        timeline.Record(1000, [new MusicChannelSnapshot { Id = "ch0", Kind = MusicChannelKind.Fm, IsInUse = true, IsKeyOn = true, KeyGeneration = 1, NoteNumber = 60, Pitch = 60.0 }]);
        timeline.Record(2000, [new MusicChannelSnapshot { Id = "ch0", Kind = MusicChannelKind.Fm, IsInUse = true, IsKeyOn = true, KeyGeneration = 1, NoteNumber = 60, Pitch = 60.5 }]);
        timeline.Record(3000, [new MusicChannelSnapshot { Id = "ch0", Kind = MusicChannelKind.Fm, IsInUse = true, IsKeyOn = true, KeyGeneration = 2, NoteNumber = 60, Pitch = 60.8 }]);
        Span<ChannelRealtimeState> sampled = stackalloc ChannelRealtimeState[4];
        int sampledCount = timeline.SampleAt(1500, sampled);
        float heldInterpolated = sampled[0].Pitch;
        timeline.SampleAt(2500, sampled);
        float retriggeredSnapped = sampled[0].Pitch;
        check(sampledCount == 1 && Math.Abs(heldInterpolated - 60.25f) < 1e-4f &&
              (Math.Abs(retriggeredSnapped - 60.5f) < 1e-4f || Math.Abs(retriggeredSnapped - 60.8f) < 1e-4f),
              "PitchTimeline interpolates fine pitch during held notes and never connects across key-off retriggers");

        // Key-on initial KF pitch reset test: initial attack starts at KF pitch (~0 cents)
        var initialFmSnap = new MusicChannelSnapshot
        {
            Id = "ch0", Kind = MusicChannelKind.Fm, IsInUse = true, IsKeyOn = true, KeyGeneration = 1, NoteNumber = 60, Pitch = 60.0
        };
        check(MusicPitch.Cents(initialFmSnap.Pitch, initialFmSnap.NoteNumber) == 0,
              "Initial KF key-on pitch calculates exactly 0 cents offset for highlight marker");

        check(MusicPlaybackService.Instance.CanPlay("sample.mp3") &&
              MusicPlaybackService.Instance.CanPlay("sample.wav") &&
              !MusicPlaybackService.Instance.CanOpenFullViewer("sample.mp3") &&
              !MusicPlaybackService.Instance.CanOpenFullViewer("sample.wav") &&
              MusicPlaybackService.Instance.CanOpenFullViewer("sample.vgm"),
              "MP3/WAV formats are registered for mini-player playback only");

        string dummyVgm = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"mf-test-{Guid.NewGuid():N}.vgm");
        File.WriteAllText(dummyVgm, "dummy");
        try
        {
            var mockBackend = new TestBackend("VGM", ".vgm");
            using var testService = new MusicPlaybackService([mockBackend]);
            testService.Play(dummyVgm);
            check(testService.IsPlaying, "mock retro track is playing");
            var thread = new System.Threading.Thread(() =>
            {
                var fullViewer = new MirrorFlux.Controls.MusicPlayerControl(testService);
                int playCount = mockBackend.PlayCalls;
                check(fullViewer.Load(dummyVgm) && mockBackend.PlayCalls == playCount,
                    "MusicPlayerControl retains playback when loading currently playing retro track");
                fullViewer.Close();
            });
            thread.SetApartmentState(System.Threading.ApartmentState.STA);
            thread.Start();
            thread.Join();
        }
        finally
        {
            if (File.Exists(dummyVgm)) File.Delete(dummyVgm);
        }

        string tempWav = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"mf-test-{Guid.NewGuid():N}.wav");
        try
        {
            using (var writer = new NAudio.Wave.WaveFileWriter(tempWav, new NAudio.Wave.WaveFormat(44100, 16, 2)))
            {
                var waveSamples = new short[AudioSpectrumAnalyzer.WindowSampleCount * 2];
                for (int i = 0; i < AudioSpectrumAnalyzer.WindowSampleCount; i++)
                {
                    short val = (short)(20000 * Math.Sin(2 * Math.PI * 1000 * i / 44100));
                    waveSamples[i * 2] = val;
                    waveSamples[i * 2 + 1] = val;
                }
                writer.WriteSamples(waveSamples, 0, waveSamples.Length);
            }
            using var audioProvider = new AudioFileWaveProvider(tempWav);
            var pcmBuf = new byte[AudioSpectrumAnalyzer.WindowSampleCount * 4];
            int bytesRead = audioProvider.Read(pcmBuf, 0, pcmBuf.Length);
            var audioSnap = audioProvider.Snapshot();
            int wavPeak = Array.IndexOf(audioSnap.SpectrumLevels, audioSnap.SpectrumLevels.Max());
            check(bytesRead == pcmBuf.Length && audioSnap.UseSpectrumOnly && audioSnap.Channels.Length == 0 &&
                  wavPeak is >= 13 and <= 15 && audioSnap.SpectrumLevels[wavPeak] > .85,
                  "AudioFileWaveProvider decodes WAV and populates 24-band spectrum with UseSpectrumOnly enabled");
            check(audioProvider.Seek(TimeSpan.FromMilliseconds(50)) &&
                  Math.Abs(audioProvider.Snapshot().CurrentTime.TotalMilliseconds - 50) < 5,
                  "AudioFileWaveProvider supports seeking to an arbitrary time position");
        }
        finally
        {
            if (System.IO.File.Exists(tempWav)) System.IO.File.Delete(tempWav);
        }
    }

    private sealed class TestBackend(string name, string extension) : IMusicPlaybackBackend
    {
        public float OutputGain { get; private set; } = 1f;
        public void SetOutputGain(float gain) => OutputGain = gain;
        public int PlayCalls { get; private set; }
        public string FormatName => name;
        public IReadOnlyCollection<string> SupportedExtensions { get; } = [extension];
        public string? CurrentFilePath { get; private set; }
        public bool IsPlaying { get; private set; }
        public bool IsPaused { get; private set; }
        public event Action? PlaybackStarted;
        public event Action? PlaybackStopped;
        public event Action? PlaybackPaused;
        public event Action? PlaybackResumed;
        public bool Play(string path) { PlayCalls++; CurrentFilePath = path; IsPlaying = true; IsPaused = false; PlaybackStarted?.Invoke(); return true; }
        public void Stop() { IsPlaying = false; IsPaused = false; PlaybackStopped?.Invoke(); }
        public void TogglePause()
        {
            IsPaused = !IsPaused;
            if (IsPaused) PlaybackPaused?.Invoke(); else PlaybackResumed?.Invoke();
        }
        public MusicPlaybackSnapshot GetSnapshot() => new()
        {
            FormatName = name, IsPlaying = IsPlaying, IsPaused = IsPaused,
            Channels = Enumerable.Range(0, 24).Select(i => new MusicChannelSnapshot
            { Id = $"channel-{i}", Label = $"CH{i + 1}", Kind = MusicChannelKind.Midi }).ToArray()
        };
        public void Dispose() { }
    }

    private sealed class ConstantWaveProvider : IWaveProvider
    {
        public WaveFormat WaveFormat { get; } = new(44100, 16, 2);
        public int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public int Read(Span<byte> buffer)
        {
            for (int i = 0; i + 1 < buffer.Length; i += 2)
                BinaryPrimitives.WriteInt16LittleEndian(buffer.Slice(i, 2), 16384);
            return buffer.Length;
        }
    }
}
