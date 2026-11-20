using System.Buffers.Binary;
using MirrorFlux.Services.Vgm;

internal static class Es550xRegression
{
    public static void Run(Action<bool,string> check)
    {
        foreach (bool otto in new[] { false, true })
        {
            string chip = otto ? "ES5506" : "ES5505";
            using var p = new IndependentVgmWaveProvider(VgmFile.Load(Fixture(otto))!);
            byte[] audio = new byte[2048];
            int Range()
            {
                p.Read(audio, 0, audio.Length);
                int[] points = Enumerable.Range(80, 432).Select(i => (int)BinaryPrimitives.ReadInt16LittleEndian(audio.AsSpan(i * 4))).ToArray();
                return points.Max() - points.Min();
            }
            int sounding = Range();
            var lanes = p.GetSnapshot().MusicChannels;
            var voice = lanes.Single(c => c.Id == chip + ":31");
            Console.WriteLine($"{chip} lanes={lanes.Length} keys={lanes.Count(c=>c.IsKeyOn)} voice32={voice.IsKeyOn}/{voice.NoteNumber}/{voice.Level:F2} range={sounding}");
            check(lanes.Length == 32 && voice.IsKeyOn && voice.NoteNumber == 60 && voice.Level > 0
                && voice.KeyboardNotes.Contains(60) && lanes.Count(c=>c.IsKeyOn)==1, $"{chip} voice 32 has independent PCM telemetry");
            check(p.SetChannelMuted(voice,true), $"{chip} accepts channel mute");
            int muted = Range(); p.SetChannelMuted(voice,false); int restored = Range();
            check(sounding > 100 && muted <= 2 && restored > 100, $"{chip} sample ROM audio and mute {sounding}/{muted}/{restored}");
        }
    }

    private static byte[] Fixture(bool otto)
    {
        var commands = new List<byte>();
        void Byte(int offs,int v) => commands.AddRange([0xbe,(byte)offs,(byte)v]);
        void Word(int offs,int v) => commands.AddRange([0xd6,(byte)offs,(byte)v,(byte)(v>>8)]);
        void Reg(int reg,int v)
        {
            if (otto) { Byte(reg*4,v>>24); Byte(reg*4+1,v>>16); Word(reg*4+2,v); }
            else Word(reg*2,v);
        }
        // ES5505 uses an 8-bit ROM. ES5506 uses a 16-bit ROM in external bank 1.
        int stride = otto ? 2 : 1;
        byte[] rom = new byte[256 * stride + 8];
        uint bankOffset = otto ? (1u << 21) : 0;
        BinaryPrimitives.WriteUInt32LittleEndian(rom,bankOffset + (uint)(256 * stride));
        BinaryPrimitives.WriteUInt32LittleEndian(rom.AsSpan(4), (otto ? 0x10000000u : 0x90000000u) | bankOffset);
        for (int i=0;i<256;i++)
        {
            double sample = Math.Sin(i * 2 * Math.PI / 64);
            if (otto) BinaryPrimitives.WriteInt16LittleEndian(rom.AsSpan(8 + 2 * i), (short)(sample * 25600));
            else rom[8+i] = (byte)(sbyte)(100 * sample);
        }
        commands.AddRange([0x67,0x66,0x90]); commands.AddRange(BitConverter.GetBytes(rom.Length)); commands.AddRange(rom);
        if (otto)
        {
            Byte(0x40+31,1);
            Reg(15,0x20+31); Reg(1,0); Reg(2,255<<11); Reg(3,0);
            Reg(15,31); Reg(11,31); Reg(1,2048); Reg(2,65535); Reg(4,65535); Reg(7,65535); Reg(9,65535); Reg(0,0x4308);
        }
        else
        {
            Reg(15,31); Reg(13,31); Reg(1,1024); Reg(4,1); Reg(5,0xfe00);
            Reg(6,0xfff0); Reg(7,0xfff0); Reg(8,0xff00); Reg(9,0xff00); Reg(0,0xc0c);
        }
        commands.AddRange([0x61,0xff,0x7f,0x66]);
        byte[] bytes=new byte[256+commands.Count]; "Vgm "u8.CopyTo(bytes);
        void Put(int offset,uint v) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset),v);
        Put(4,(uint)bytes.Length-4); Put(8,0x171); Put(0x34,256-0x34); Put(0xd0,16000000u|(otto?0x80000000u:0));
        bytes[0xd5]=2; commands.CopyTo(bytes,256); return bytes;
    }
}
