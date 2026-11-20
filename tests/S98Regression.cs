using System.IO;
using System.IO.Compression;
using System.Buffers.Binary;
using MirrorFlux.Services.Music;
using MirrorFlux.Services.Vgm;

internal static class S98Regression
{
    private static void Put(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
    private static byte[] Fixture(int version)
    {
        var data = new byte[64]; "S980"u8.CopyTo(data); data[3] = (byte)('0' + version);
        Put(data, 4, 1); Put(data, 8, 10000); Put(data, 20, 64);
        if (version >= 2) { Put(data, 28, 1); Put(data, 32, 2); Put(data, 36, 4000000); }
        var bytes = data.ToList();
        // Two different notes lasting one tick each, followed by silence.
        bytes.AddRange([0,7,0x3e, 0,0,200, 0,1,0, 0,8,15, 0xff, 0,0,100, 0xff, 0,8,0, 0xfe,0x8c,0x4e,0xfd]);
        return bytes.ToArray();
    }

    private static byte[] OpnaSixChannelFixture()
    {
        var data = new byte[48]; "S983"u8.CopyTo(data);
        Put(data, 4, 1); Put(data, 8, 1000); Put(data, 20, 48);
        Put(data, 28, 1); Put(data, 32, 4); Put(data, 36, 7_987_200);
        var bytes = data.ToList();
        void Write(int port, byte register, byte value) => bytes.AddRange([(byte)port, register, value]);
        for (int slot = 0; slot < 4; slot++)
        {
            Write(1, (byte)(0x30 + slot * 4), 1);
            Write(1, (byte)(0x40 + slot * 4), (byte)(slot == 3 ? 8 : 127));
            Write(1, (byte)(0x50 + slot * 4), 31);
            Write(1, (byte)(0x80 + slot * 4), 15);
        }
        Write(1, 0xb0, 7); Write(1, 0xb4, 0xc0);
        Write(1, 0xa4, 0x22); Write(1, 0xa0, 0x80);
        Write(0, 0x28, 0xf4); bytes.Add(0xfe); bytes.Add(0xfe); bytes.Add(0x05);
        Write(0, 0x28, 0x04); bytes.Add(0xfd);
        return bytes.ToArray();
    }
    public static void Run(Action<bool,string> check)
    {
        foreach (int version in new[] { 0,1,2,3 })
        {
            var file = VgmFile.Load(Fixture(version), "S98 test");
            check(file != null && file.FormatName == "S98" && (version < 2 ? file.Ym2608Clock == 7987200 : file.Ym2203Clock == 4000000), $"S98 v{version} loads device clock and format");
        }
        var fixture = Fixture(3);
        var parsed = VgmFile.Load(fixture)!;
        check(parsed.TotalSamples == 44100, "S98 fractional ticks and variable waits keep exact one-second duration");
        check(VgmFile.Load(fixture[..^1])?.TotalSamples == 44100 && VgmFile.Load(fixture[..^1].Concat(new byte[] { 1, 8 }).ToArray())?.TotalSamples == 44100,
            "Legacy S98 EOF and incomplete final register write stop without invented data");
        byte[] dualHeader = new byte[64]; "S983"u8.CopyTo(dualHeader);
        Put(dualHeader,4,1); Put(dualHeader,8,1000); Put(dualHeader,20,64); Put(dualHeader,28,2);
        Put(dualHeader,32,4); Put(dualHeader,36,7_987_200); Put(dualHeader,48,4); Put(dualHeader,52,7_987_200);
        byte[] dual = [..dualHeader, 2,7,0x3e, 2,0,0xfe, 2,1,0, 2,8,15, 0xfe,0x7f,0xfd];
        using (var second = new IndependentVgmWaveProvider(VgmFile.Load(dual, "S98 dual OPNA")!))
        {
            second.Read(new byte[4096],0,4096);
            var parts = second.GetSnapshot().MusicChannels;
            check(parts.Length == 32 && parts.Single(c=>c.Id=="YM2608 #2:SSG:0").IsKeyOn &&
                !parts.Single(c=>c.Id=="YM2608:SSG:0").IsKeyOn, "S98 independently routes the second OPNA");
        }
        byte[] opnaConverted = S98Decoder.Convert(OpnaSixChannelFixture(), "OPNA six channel");
        check(opnaConverted.AsSpan(256, 3).SequenceEqual(new byte[] { 0x56, 0x29, 0x80 }),
            "S98 YM2608 conversion restores the six-channel enable state omitted by real-time loggers");
        using (var opnaProvider = new IndependentVgmWaveProvider(VgmFile.Load(OpnaSixChannelFixture(), "OPNA six channel")!))
        {
            var opnaAudio = new byte[24_000 * 4]; opnaProvider.Read(opnaAudio, 0, opnaAudio.Length);
            check(opnaAudio.Any(value => value != 0), "S98 YM2608 FM4-6 produce audible output");
            check(opnaProvider.GetSnapshot().MusicChannels.Any(channel => channel.Id == "YM2608:FM:3" && channel.IsInUse),
                "S98 YM2608 FM4 is exposed as its own keyboard and meter channel");
        }
        byte[] tags = System.Text.Encoding.UTF8.GetBytes("[S98]\ufefftitle=日本語の曲\nartist=Test\n\0");
        byte[] tagged = fixture.Concat(tags).ToArray(); Put(tagged,16,(uint)fixture.Length);
        check(VgmFile.Load(tagged)?.TrackName == "日本語の曲" && VgmFile.Load(tagged)?.Author == "Test", "S98 UTF-8 tags preserve Japanese title and author");
        using var provider = new IndependentVgmWaveProvider(parsed);
        var buffer = new byte[44100 * 4]; provider.Read(buffer, 0, buffer.Length);
        var channels = provider.GetSnapshot().MusicChannels;
        var psg = channels.Single(c => c.Id.Contains("SSG") && c.Id.EndsWith(":0"));
        check(!psg.IsKeyOn && psg.KeyboardNotes.Length == 1, "Sub-millisecond S98 notes survive a full output buffer without appearing as a simultaneous chord");
        var latch = new KeyboardActivity();
        latch.Record([new() { Id="test", NoteNumber=60, IsKeyOn=true }, new() { Id="test", NoteNumber=72, IsKeyOn=true }]);
        var current = new MusicChannelSnapshot[] { new() { Id="test", IsKeyOn=false } };
        var first = latch.Apply(current, 1000);
        check(first[0].KeyboardNotes.SequenceEqual([60]) && latch.Apply(current, 1001)[0].KeyboardNotes.SequenceEqual([60]), "Both views can read the same latched note without consuming the next event");
        long nextFrame = 1000 + System.Diagnostics.Stopwatch.Frequency / 60 + 1;
        check(latch.Apply(current, nextFrame)[0].KeyboardNotes.SequenceEqual([72]), "Rapid notes on one channel are displayed sequentially instead of simultaneously");
        check(latch.Apply(current, 1000 + System.Diagnostics.Stopwatch.Frequency)[0].KeyboardNotes.Length == 0 && first[0].KeyboardNotes.SequenceEqual([60]),
            "Short-note display expires without changing earlier snapshots");
        var overflow = new KeyboardActivity();
        for (int note = 60; note < 70; note++) overflow.Record([new() { Id="fast", NoteNumber=note, IsKeyOn=true }]);
        check(overflow.Apply([new() { Id="fast", IsKeyOn=false }], 1000)[0].KeyboardNotes.SequenceEqual([62]),
            "Extreme pitch bursts discard stale display notes instead of accumulating visible lag");
        Put(fixture,24,64);
        var looped = VgmFile.Load(fixture)!;
        check(looped.LoopOffset == 256 && looped.LoopSamples == looped.TotalSamples, "S98 loop offsets map to the translated command boundary");
        Put(fixture,12,1);
        check(VgmFile.Load(fixture) == null, "Unsupported compressed S98 is rejected instead of misplayed");
    }
    public static void Audit(string path)
    {
        if (Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var zip = ZipFile.OpenRead(path);
            foreach (var entry in zip.Entries.Where(e => e.FullName.EndsWith(".s98", StringComparison.OrdinalIgnoreCase)).Take(8))
            { using var stream = entry.Open(); using var memory = new MemoryStream(); stream.CopyTo(memory); AuditBytes(memory.ToArray(), entry.Name); }
        }
        else AuditBytes(File.ReadAllBytes(path), Path.GetFileName(path));
    }
    private static void AuditBytes(byte[] data, string title)
    {
        _ = S98Decoder.Convert(data, title); // Expose precise decoder diagnostics for real-file audits.
        var file = VgmFile.Load(data,title) ?? throw new Exception("Cannot load S98: " + title);
        using var provider = new IndependentVgmWaveProvider(file);
        var buffer = new byte[4096]; int nonzero = 0; var keys = new HashSet<string>();
        for (int i=0;i<350;i++)
        {
            provider.Read(buffer,0,buffer.Length); nonzero += buffer.Count(b=>b!=0);
            foreach(var channel in provider.GetSnapshot().MusicChannels)
                if(channel.KeyboardNotes.Length>0) keys.Add(channel.Id);
        }
        if(nonzero==0 || keys.Count==0) throw new Exception("Missing S98 audio/keyboard activity: "+title);
        Console.WriteLine($"PASS S98 {title}: nonzero={nonzero}, keyboard lanes={keys.Count}");
    }
}
