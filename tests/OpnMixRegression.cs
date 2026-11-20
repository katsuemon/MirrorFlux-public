using System.Buffers.Binary;
using MirrorFlux.Services.Music;
using MirrorFlux.Services.Vgm;

internal static class OpnMixRegression
{
    private static byte[] Fixture(int field, uint clock, byte command, bool fm, ushort? fmVolume, ushort? ssgVolume, bool second)
    {
        var commands = new List<byte>();
        byte op = (byte)(command + (second ? 0x50 : 0));
        void W(int r, int v) => commands.AddRange([op, (byte)r, (byte)v]);
        if (fm)
        {
            int ch = command == 0x58 ? 1 : 0;
            if (command == 0x56) W(0x29, 0x80);
            W(0xb0 + ch, 7);
            foreach (int s in new[] { 0, 4, 8, 12 })
            {
                W(0x30 + s + ch, 1); W(0x40 + s + ch, 24);
                W(0x50 + s + ch, 31); W(0x80 + s + ch, 15);
            }
            W(0xa4 + ch, 0x22); W(0xa0 + ch, 0x69); W(0x28, 0xf0 | ch);
        }
        else { W(7, 0x3e); W(0, 254); W(1, 0); W(8, 15); }
        commands.AddRange([0x61, 0xff, 0x7f, 0x66]);
        byte[] data = new byte[256 + commands.Count]; "Vgm "u8.CopyTo(data);
        void Put(int p, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(p), v);
        Put(4, (uint)data.Length - 4); Put(8, 0x171); Put(0x34, 256 - 0x34);
        Put(field, clock | (second ? 0x40000000u : 0)); commands.CopyTo(data, 256);
        if (fmVolume.HasValue || ssgVolume.HasValue)
        {
            Put(0xbc, 4); Put(0xc0, 12); Put(0xc8, 4);
            int pos = 0xcd;
            void Volume(byte type, ushort value)
            {
                data[0xcc]++; data[pos++] = type; data[pos++] = second ? (byte)1 : (byte)0;
                BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(pos), value); pos += 2;
            }
            byte type = (byte)(command - 0x4f - (command == 0x58 ? 1 : 0));
            if (fmVolume.HasValue) Volume(type, fmVolume.Value);
            if (ssgVolume.HasValue) Volume((byte)(type | 0x80), ssgVolume.Value);
        }
        return data;
    }

    private static double Rms(byte[] data)
    {
        using var p=new IndependentVgmWaveProvider(VgmFile.Load(data)!);
        byte[] audio = new byte[4410 * 4]; p.Read(audio, 0, audio.Length); p.Read(audio, 0, audio.Length);
        double energy = 0;
        for (int i = 0; i < audio.Length; i += 2)
        {
            double value = BinaryPrimitives.ReadInt16LittleEndian(audio.AsSpan(i)); energy += value * value;
        }
        double rms=Math.Sqrt(energy / (audio.Length / 2));
        return rms;
    }

    public static void Run(Action<bool, string> check)
    {
        foreach (var chip in new[] { (0x44, 4000000u, (byte)0x55), (0x48, 8000000u, (byte)0x56), (0x4c, 8000000u, (byte)0x58) })
        foreach (bool second in new[] { false, true })
        foreach (var volume in new (ushort?, ushort?)[] { (null, null), (128, 104), (128, 0), (0, 104), (0, 0), (1, 1), (128, 0x8080), (128, 0x8200) })
        foreach (bool fm in new[] { false, true })
        {
            byte[] fixture = Fixture(chip.Item1, chip.Item2, chip.Item3, fm, volume.Item1, volume.Item2, second);
            double expected = AudioBaseline.Mix(fixture), actual = Rms(fixture);
            double ratio = expected > 0 ? actual / expected : 0;
            check(expected > 0 ? ratio is > .94 and < 1.06 : actual == 0,
                $"OPN {chip.Item3:X2} #{(second ? 2 : 1)} {(fm ? "FM" : "SSG")} volume {volume.Item1:X}/{volume.Item2:X}: recorded {expected:F1}, ymfm {actual:F1}");
        }
        // The missing virtual SSG bus also changed the common normalization for
        // other chips. A silent OPNA must not change the accompanying OPM level.
        byte[] mixed = Fixture(0x48, 8000000, 0x56, false, 128, 104, false);
        mixed[256 + 3 * 3 + 2] = 0; // SSG amplitude register 8
        var opm = new List<byte>();
        void W(int r, int v) => opm.AddRange([0x54, (byte)r, (byte)v]);
        W(0x20, 0xc7); W(0x28, 0x4a);
        foreach (int s in new[] { 0, 8, 16, 24 })
        {
            W(0x40 + s, 1); W(0x60 + s, 24); W(0x80 + s, 31); W(0xe0 + s, 15);
        }
        W(8, 0x78);
        int wait = mixed.Length - 4;
        mixed = [..mixed.AsSpan(0, wait), ..opm, ..mixed.AsSpan(wait)];
        BinaryPrimitives.WriteUInt32LittleEndian(mixed.AsSpan(4), (uint)mixed.Length - 4);
        BinaryPrimitives.WriteUInt32LittleEndian(mixed.AsSpan(0x30), 4000000);
        double reference = AudioBaseline.Mix(mixed), output = Rms(mixed);
        check(output / reference is > .94 and < 1.06, $"OPNA mix normalization preserves accompanying OPM ({output / reference:F3})");
    }
}
