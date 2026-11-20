using System.IO;
using System.IO.Compression;
using System.Buffers.Binary;
using MirrorFlux.Services.Vgm;

internal static class AudioArchiveAudit
{
    public static void Run(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        foreach (var entry in zip.Entries.Where(e => e.FullName.EndsWith(".vgz", StringComparison.OrdinalIgnoreCase) || e.FullName.EndsWith(".vgm", StringComparison.OrdinalIgnoreCase)))
        {
            using var input = entry.Open();
            using var memory = new MemoryStream();
            input.CopyTo(memory);
            var file = VgmFile.Load(memory.ToArray(), entry.Name)!;
            using var provider = new IndependentVgmWaveProvider(file);
            int peak = 0, nonzero = 0;
            var pitchedVoices = new HashSet<string>();
            var buffer = new byte[4096];
            for (int frame = 0; frame < 44100 * 8; frame += 1024)
            {
                provider.Read(buffer, 0, buffer.Length);
                foreach (var channel in provider.GetSnapshot().MusicChannels)
                    if (channel.IsKeyOn && channel.NoteNumber.HasValue) pitchedVoices.Add(channel.Id);
                for (int i = 0; i < buffer.Length; i += 2)
                {
                    int sample = BinaryPrimitives.ReadInt16LittleEndian(buffer.AsSpan(i, 2));
                    peak = Math.Max(peak, Math.Abs(sample));
                    if (sample != 0) nonzero++;
                }
            }
            var snapshot = provider.GetSnapshot();
            Console.WriteLine($"  Keyboard note activity: {string.Join(",", pitchedVoices)}; lanes={snapshot.MusicChannels.Length}");
            Console.WriteLine($"{entry.Name}: peak={peak}, nonzero={nonzero}, used={string.Join(",", snapshot.MusicChannels.Where(c => c.IsInUse).Select(c => c.ChipName).Distinct())}, banks={snapshot.RegisterBanks.Length}");
            if (peak == 0) throw new Exception($"Silent track: {entry.Name}");
            if (file.HasSecondYm2203 && !snapshot.MusicChannels.Any(c => c.ChipName == "YM2203 #2" && c.IsInUse))
                throw new Exception($"Second chip not addressed: {entry.Name}");
        }
    }
}
