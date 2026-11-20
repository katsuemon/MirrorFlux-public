using System.Buffers.Binary;
using MirrorFlux.Services.Music;
using MirrorFlux.Services.Vgm;

internal static class OpnFmRegression
{
    private static byte[] File(int field, uint clock, List<byte> commands)
    {
        byte[] data = new byte[256 + commands.Count + 1];
        "Vgm "u8.CopyTo(data);
        void Put(int p, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(p), v);
        Put(4, (uint)data.Length - 4); Put(8, 0x171); Put(0x34, 256 - 0x34); Put(field, clock);
        commands.CopyTo(data, 256); data[^1] = 0x66;
        return data;
    }

    private static void Tone(List<byte> data, byte command, int channel)
    {
        byte port = (byte)(command + channel / 3);
        int local = channel % 3;
        void W(int r, int v) => data.AddRange([port, (byte)r, (byte)v]);
        W(0xb0 + local, 7);
        // Deliberately omit B4-B6: real logs can rely on the chip reset PAN.
        foreach (int slot in new[] { 0, 4, 8, 12 })
        {
            W(0x30 + slot + local, 1); W(0x40 + slot + local, 24);
            W(0x50 + slot + local, 31); W(0x60 + slot + local, 20);
            W(0x70 + slot + local, 20); W(0x80 + slot + local, 0x1f);
        }
        W(0xa4 + local, 0x22); W(0xa0 + local, 0x69);
    }

    public static void Run(Action<bool, string> check)
    {
        // An output sample is not an FM envelope clock for oversampled OPN chips.
        // Use a non-aligned duration and both register banks to exercise every phase.
        foreach (var chip in new[] { (0x44, 4000000u, (byte)0x55, 0), (0x48, 8000000u, (byte)0x56, 0),
            (0x48, 8000000u, (byte)0x56, 5), (0x4c, 8000000u, (byte)0x58, 1),
            (0x4c, 8000000u, (byte)0x58, 4), (0x2c, 7670454u, (byte)0x52, 5) })
        {
            var commands = new List<byte>();
            if (chip.Item1 == 0x48) commands.AddRange([0x56, 0x29, 0x80]);
            Tone(commands, chip.Item3, chip.Item4);
            byte key = (byte)(chip.Item4 + (chip.Item4 >= 3 ? 1 : 0));
            for (int n = 0; n < 100; n++)
                commands.AddRange([chip.Item3, 0x28, key, chip.Item3, 0x28, (byte)(0xf0 | key), 0x61, 0x3b, 0x11]);
            byte[] file = File(chip.Item1, chip.Item2, commands);
            byte[] whole = new byte[4411 * 4 * 100];
            using (var provider = new IndependentVgmWaveProvider(VgmFile.Load(file)!))
                provider.Read(whole, 0, whole.Length);
            double minimum = double.MaxValue;
            for (int n = 1; n < 100; n++)
            {
                double energy = 0;
                for (int i = n * 4411 * 4; i < (n + 1) * 4411 * 4; i += 2)
                {
                    double v = BinaryPrimitives.ReadInt16LittleEndian(whole.AsSpan(i));
                    energy += v * v;
                }
                minimum = Math.Min(minimum, Math.Sqrt(energy / (4411 * 2)));
            }
            check(minimum > 100, $"OPN {chip.Item3:X2} FM{chip.Item4 + 1} all 100 attacks audible (min RMS {minimum:F2})");
            if (chip.Item1 == 0x48 && chip.Item4 == 5)
            {
                using var provider = new IndependentVgmWaveProvider(VgmFile.Load(file)!);
                byte[] split = new byte[whole.Length];
                for (int i = 0; i < split.Length; i += 508) provider.Read(split, i, Math.Min(508, split.Length - i));
                check(whole.SequenceEqual(split), "OPNA key timing is independent of callback size");
            }
        }

        foreach (var chip in new[] { (0x48, 8000000u, (byte)0x56, 0), (0x48, 8000000u, (byte)0x56, 5),
            (0x4c, 8000000u, (byte)0x58, 1), (0x4c, 8000000u, (byte)0x58, 4), (0x2c, 7670454u, (byte)0x52, 5) })
        {
            var commands = new List<byte>();
            if (chip.Item1 == 0x48) commands.AddRange([0x56, 0x29, 0x80]);
            Tone(commands, chip.Item3, chip.Item4);
            byte key = (byte)(chip.Item4 + (chip.Item4 >= 3 ? 1 : 0));
            commands.AddRange([chip.Item3, 0x28, (byte)(0xf0 | key), 0x61, 0x3a, 0x11]);
            commands.AddRange([(byte)(chip.Item3 + chip.Item4 / 3), (byte)(0xb4 + chip.Item4 % 3), 0, 0x61, 0x3a, 0x11]);
            using var provider = new IndependentVgmWaveProvider(VgmFile.Load(File(chip.Item1, chip.Item2, commands))!);
            byte[] audio = new byte[2205 * 4]; provider.Read(audio, 0, audio.Length);
            var channel = provider.GetSnapshot().MusicChannels.Single(c => c.Kind == MusicChannelKind.Fm && c.Id.EndsWith($":{chip.Item4}"));
            check(channel.IsInUse && channel.IsKeyOn && channel.NoteNumber.HasValue && channel.Level > 0 && channel.Pan == 3,
                $"ymfm OPN {chip.Item3:X2} FM{chip.Item4 + 1} default PAN lights keyboard and meter");
            // The register viewer remains a last-written image, not synthesized reset writes.
            check(provider.GetSnapshot().RegisterBanks[chip.Item4 / 3].Values[0xb4 + chip.Item4 % 3] == 0,
                "implicit PAN does not change the register write image");
            provider.Read(audio, 0, audio.Length); provider.Read(audio, 0, audio.Length);
            channel = provider.GetSnapshot().MusicChannels.Single(c => c.Kind == MusicChannelKind.Fm && c.Id.EndsWith($":{chip.Item4}"));
            check(!channel.IsKeyOn && channel.Level == 0 && channel.Pan == 0, "explicit zero PAN still mutes keyboard and meter");
        }
    }
}
