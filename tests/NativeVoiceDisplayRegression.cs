using System.Buffers.Binary;
using MirrorFlux.Services.Vgm;

internal static class NativeVoiceDisplayRegression
{
    public static void Run(Action<bool,string> check)
    {
        var commands = new List<byte>();
        // Keep pitch and level registers fixed while the sample changes from A4 to E5.
        // A single voice is carrying a recording, as in the CAVE soundtracks.
        byte[] rom = new byte[8 + 8820];
        BinaryPrimitives.WriteUInt32LittleEndian(rom,8820);
        int signal = 0, step = 127;
        int[] scales = [230,230,230,230,307,409,512,614];
        for (int i = 0; i < 17640; i++)
        {
            double hz = i < 8820 ? 440 : 659.25;
            double target = (i < 8820 ? 14000 : 3500) * Math.Sin(2 * Math.PI * hz * i / 44100);
            int best = 0, nextSignal = 0;
            double error = double.MaxValue;
            for (int nibble = 0; nibble < 16; nibble++)
            {
                int delta = (2 * (nibble & 7) + 1) * step / 8;
                int candidate = Math.Clamp(signal * 254 / 256 + ((nibble & 8) != 0 ? -delta : delta), -32768, 32767);
                double distance = Math.Abs(target - candidate);
                if (distance < error) { error = distance; best = nibble; nextSignal = candidate; }
            }
            signal = nextSignal; step = Math.Clamp(step * scales[best & 7] >> 8, 127, 0x6000);
            rom[8 + i/2] |= (byte)(best << ((i & 1) == 0 ? 4 : 0));
        }
        commands.AddRange([0x67,0x66,0x86]); commands.AddRange(BitConverter.GetBytes(rom.Length)); commands.AddRange(rom);
        void W(byte reg, byte value) => commands.AddRange([0x5d,reg,value]);
        W(0xff,0x80); W(0,0xff); W(2,0xff); W(3,8); W(0x43,0x22); W(0x63,0x74); W(1,0xa0);
        commands.AddRange([0x61,0xff,0x7f,0x66]);
        using (var player = Create(commands,0x68,16934400))
        {
            byte[] buffer = new byte[256*4];
            MusicSample(24, out var a, out double loud);
            MusicSample(32, out var e, out double quiet);
            check(a == 69 && e == 76 && loud > quiet*2 && quiet > .03,
                $"YMZ280B single ADPCM voice estimates A4/E5 and measures sample amplitude ({a}/{e}, {loud:F2}/{quiet:F2})");
            void MusicSample(int blocks, out int? note, out double level)
            {
                for (int i=0;i<blocks;i++) player.Read(buffer,0,buffer.Length);
                var channels = player.GetSnapshot().MusicChannels;
                var v = channels.Single(c=>c.Id=="YMZ280B:0");
                note = v.IsNoteEstimated ? v.NoteNumber : null; level = v.Level;
                if (channels.Length!=8 || channels.Count(c=>c.IsKeyOn)!=1) throw new Exception("Spurious YMZ280B voice");
            }
        }

        commands.Clear();
        void O(int port,int reg,int data) => commands.AddRange([0xd1,(byte)port,(byte)reg,(byte)data]);
        O(6,0,3); // four independent slots in group 0, including bank 3 / slot 36
        O(3,0x30,1); O(3,0x50,31); O(3,0xa0,0x3a); O(3,0x90,0x38); O(3,0xd0,0); O(3,0,1);
        commands.AddRange([0x61,0xff,0x7f,0x66]);
        using (var player = Create(commands,0x64,16934400))
        {
            byte[] buffer = new byte[2048];
            int Range() {
                player.Read(buffer,0,buffer.Length);
                var samples = Enumerable.Range(100,412).Select(i=>(int)BinaryPrimitives.ReadInt16LittleEndian(buffer.AsSpan(i*4))).ToArray();
                return samples.Max()-samples.Min();
            }
            int active = Range(); var voice = player.GetSnapshot().MusicChannels.Single(c=>c.Id=="YMF271:36");
            check(voice.IsKeyOn && voice.NoteNumber==69 && voice.Level>0 && voice.Kind==MirrorFlux.Services.Music.MusicChannelKind.Fm,
                "YMF271 independently displays an FM voice above slot 32");
            check(player.SetChannelMuted(voice,true), "YMF271 accepts a mask above slot 32");
            int muted = Range(); player.SetChannelMuted(voice,false); int restored = Range();
            check(active>100 && muted<=2 && restored>100, $"YMF271 output follows the displayed voice mask ({active}/{muted}/{restored})");
        }
    }

    private static IndependentVgmWaveProvider Create(List<byte> commands, int field, uint clock)
    {
        byte[] file = new byte[256+commands.Count]; "Vgm "u8.CopyTo(file);
        void Put(int p,uint value) => BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(p),value);
        Put(4,(uint)file.Length-4); Put(8,0x171); Put(0x34,256-0x34); Put(field,clock); commands.CopyTo(file,256);
        return new IndependentVgmWaveProvider(VgmFile.Load(file)!);
    }
}
