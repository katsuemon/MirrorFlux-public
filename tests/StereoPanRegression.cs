using System.Buffers.Binary;
using System.IO;
using MirrorFlux.Services.Vgm;

/// <summary>表示の左右（Pan のビット1＝左、ビット0＝右）が、実際に鳴っている側と合っていることの検査。</summary>
internal static class StereoPanRegression
{
    public static void Run(Action<bool, string> check)
    {
        // YM2151 の 0x20 はビット6が左、ビット7が右。左だけ（0x40）と右だけ（0x80）で鳴らし、音と表示の両方を見る。
        foreach (var (register, expectedPan, side) in new[] { (0x47, 2, "left"), (0x87, 1, "right") })
        {
            var commands = new List<byte> { 0x54, 0x20, (byte)register, 0x54, 0x28, 0x4a };
            for (int slot = 0; slot < 4; slot++) commands.AddRange([0x54, (byte)(0x60 + slot * 8), 0x00, 0x54, (byte)(0x80 + slot * 8), 0x1f]);
            commands.AddRange([0x54, 0x08, 0x78, 0x61, 0x44, 0xac]);
            byte[] data = new byte[256];
            "Vgm "u8.CopyTo(data);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), 0x171);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x34), 256 - 0x34);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x30), 4000000);
            data = [.. data, .. commands, 0x66];
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), (uint)data.Length - 4);

            using var provider = new IndependentVgmWaveProvider(VgmFile.Load(data, "opm-pan.vgm")!);
            byte[] audio = new byte[8192 * 4];
            int read = provider.Read(audio, 0, audio.Length);
            long left = 0, right = 0;
            for (int i = 0; i + 3 < read; i += 4)
            {
                left += Math.Abs((int)BinaryPrimitives.ReadInt16LittleEndian(audio.AsSpan(i)));
                right += Math.Abs((int)BinaryPrimitives.ReadInt16LittleEndian(audio.AsSpan(i + 2)));
            }
            var channel = provider.GetSnapshot().MusicChannels.Single(c => c.Id == "YM2151:FM:0");
            bool soundOnSide = side == "left" ? left > 0 && right == 0 : right > 0 && left == 0;
            check(soundOnSide && channel.Pan == expectedPan,
                $"YM2151 pan display shows {side} on the side that actually sounds");
        }
        // MDX の p は1が左、2が右（MXDRV は YM2151 の 0x20 のビット6・7へそのまま入れる）。
        foreach (var (p, expectedPan, side) in new[] { (1, 2, "left"), (2, 1, "right") })
        {
            byte[] score = MdxRegression.CreateScore([0xfd, 0, 0xfc, (byte)p, 0xfb, 15, 0xff, 200, 0xa4, 60, 0xf1, 0]);
            string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".mdx");
            try
            {
                File.WriteAllBytes(path, score);
                using var provider = new MirrorFlux.Services.Music.MdxWaveProvider(path);
                byte[] audio = new byte[8192 * 4];
                provider.Read(audio, 0, audio.Length);
                long left = 0, right = 0;
                for (int i = 0; i + 3 < audio.Length; i += 4)
                {
                    left += Math.Abs((int)BinaryPrimitives.ReadInt16LittleEndian(audio.AsSpan(i)));
                    right += Math.Abs((int)BinaryPrimitives.ReadInt16LittleEndian(audio.AsSpan(i + 2)));
                }
                var channel = provider.Snapshot().Channels.Single(c => c.Id == "YM2151:FM:0");
                bool soundOnSide = side == "left" ? left > 0 && right == 0 : right > 0 && left == 0;
                check(soundOnSide && channel.Pan == expectedPan, $"MDX p{p} pan display shows {side} on the side that actually sounds");
            }
            finally { File.Delete(path); }
        }
    }
}