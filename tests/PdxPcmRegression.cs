using System.IO;
using MirrorFlux.Services.Music;
internal static class PdxPcmRegression
{
    public static void Run(Action<bool, string> check)
    {
        check(PdxPcmMixer.Decode(new byte[] { 0x10, 0x98 }, 4).SequenceEqual(new short[] { 0, 128, 64, 0 }), "PDX ADPCM follows the X68000 PCM8 predictor and low-nibble order");
        check(PdxPcmMixer.Decode(new byte[] { 0x80, 0, 0, 0, 0x7f, 0xff }, 5).SequenceEqual(new short[] { -32768, 0, 32767 }), "PCM8 mode 5 decodes big endian signed 16 bit samples");
        check(PdxPcmMixer.Decode(new byte[] { 0x80, 0, 0x7f }, 6).SequenceEqual(new short[] { -32768, 0, 32512 }), "PCM8 mode 6 decodes signed 8 bit samples without DC offset");
        var mixer = new PdxPcmMixer(); var output = new[] { new int[32], new int[32] };
        byte[] bank = [0x03, 0xe8, 0x03, 0xe8, 0x7f, 0xff];
        mixer.Start(0, bank, 0, 4, 5, 1, 8); mixer.Mix(output, 32);
        check(output[0].Take(6).All(v => v == 1000) && output[0].Skip(6).All(v => v == 0) && output[1].All(v => v == 0) && !mixer.IsActive(0), "PDX exact sample end excludes next bank entry and preserves left pan");
        mixer.Start(0, bank, 0, 4, 5, 3, 4); mixer.Mix(output, 32);
        check(output[0][0] == 375 && output[1][0] == 375, "PCM8 volume steps use linear hardware gain instead of excessive MPCM attenuation conversion");
        check(Math.Abs(PdxPcmMixer.Frequency(0) - 3906.25) < .001 && Math.Abs(PdxPcmMixer.Frequency(1) - 5208.33333333) < .001 && PdxPcmMixer.Frequency(4) == 15625 && PdxPcmMixer.Frequency(5) == 15625 && PdxPcmMixer.Frequency(6) == 15625, "PCM8 F0-F4 rates follow MXDRV and raw PCM modes use 15625 Hz");
        byte[] testBank = new byte[772]; System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(testBank.AsSpan(36 * 8), 768); System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(testBank.AsSpan(36 * 8 + 4), 4);
        int panVolume = -1;
        var sequence = new MxdrvCore(MdxRegression.CreateScore([0xfb, 8, 0xa4, 60, 0xf1, 0], 9), testBank);
        sequence.PcmOutCallback = (_, _, _, _, value) => panVolume = value;
        for (int tick = 0; tick < 4; tick++) sequence.Tick();
        check(panVolume == (3 << 8 | 8), "MXDRV default PCM pan is audible stereo and volume 8 reaches PCM8 gain 8");
        foreach (byte pan in new byte[] { 0, 1, 2, 3 })
        {
            var score = new MxdrvCore(MdxRegression.CreateScore([0xfc, pan, 0xa4, 1, 0xa4, 1, 0xa4, 1, 0xf1, 0], 9), testBank);
            var masks = new List<int>();
            score.PcmOutCallback = (_, _, _, _, value) => masks.Add(value >> 8);
            for (int tick = 0; tick < 12; tick++) score.Tick();
            check(masks.Count == 3 && masks.All(mask => mask == pan), $"PCM p{pan} preserves output mask across three retriggers");
        }
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".mdx");
        var gated = new MxdrvCore(MdxRegression.CreateScore([0xfc, 3, 0xf8, 1, 0xa4, 60, 0xf1, 0], 9), testBank);
        int gateStops = 0;
        gated.PcmOutCallback = (ch, off, len, mode, pv) => mixer.Start(ch, testBank, off, len, mode, pv >> 8, pv & 255);
        gated.PcmStopCallback = ch => { gateStops++; mixer.Stop(ch); };
        gated.Tick();
        check(mixer.IsActive(0), "PCM gate fixture begins an audible sample");
        for (int i = 0; i < 10; i++) gated.Tick();
        mixer.Mix(output, 32);
        check(gateStops == 1 && !mixer.IsActive(0) && output.All(side => side.All(value => value == 0)), "PCM gate stops the sample before natural end and silences subsequent output");
        try
        {
            string bankPath = Path.ChangeExtension(path, ".pdx");
            try
            {
                byte[] score = MdxRegression.CreateScore([0xfc, 3, 0xa4, 60, 0xf1, 0], 9);
                File.WriteAllBytes(bankPath, testBank);
                File.WriteAllBytes(path, [.. score.Take(7), .. System.Text.Encoding.ASCII.GetBytes(Path.GetFileName(bankPath) + "\0"), .. score.Skip(8)]);
                using var numbered = new MdxWaveProvider(path);
                var lane = numbered.Snapshot().Channels.Single(c => c.Id == "PCM8:0");
                check(lane.IsKeyOn && lane.NoteNumber == 36 && lane.KeyboardNotes.Contains(36), "MDX PCM keyboard and meter expose PDX sample 36 instead of frequency note 60");
            }
            finally { File.Delete(bankPath); }
            byte[] original = MdxRegression.CreateTestMdx();
            File.WriteAllBytes(path, [.. original.Take(7), .. System.Text.Encoding.ASCII.GetBytes("missing-bank-unique\0"), .. original.Skip(8)]);
            using var provider = new MdxWaveProvider(path, []); var audio = new byte[44100 * 4]; provider.Read(audio, 0, audio.Length);
            check(provider.Snapshot().FileTypeDisplay == "MDX (MXDRV 2.06) + missing-bank-unique.pdx", "MDX FileTypeDisplay appends .pdx extension when omitted in MDX header");
            check(audio.Any(b => b != 0) && provider.Snapshot().AuxiliaryFileStatus.Contains("PDX: MISSING") && provider.PdxFilePath == "", "Missing PDX continues non-silent FM playback and reports FM ONLY");
            check(provider.Snapshot().Channels.Where(c => c.Kind == MusicChannelKind.Pcm).All(c => !c.IsKeyOn), "Missing PDX never reports nonexistent PCM playback");
        }
        finally { File.Delete(path); }
    }
}
