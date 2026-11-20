using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using MirrorFlux.Models;
using MirrorFlux.Services.Music;
using MirrorFlux.Services.Vgm;

internal static class IndependentVgmRegression
{
    private static void Check(bool ok,string name)
    {
        if (!ok) throw new Exception(name);
        Console.WriteLine("PASS independent VGM: "+name);
    }
    private static void Put(byte[] b,int offset,uint value) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(offset),value);
    private static byte[] File(int field,uint clock,IEnumerable<byte> commands,int frames=24000)
    {
        byte[] header=new byte[256]; "Vgm "u8.CopyTo(header); Put(header,8,0x171);
        Put(header,0x34,256-0x34); Put(header,field,clock);
        byte[] bytes=[..header,..commands,0x61,(byte)frames,(byte)(frames>>8),0x66];
        Put(bytes,4,(uint)bytes.Length-4); Put(bytes,0x18,(uint)frames); return bytes;
    }
    private static byte[] Render(byte[] data,int chunk=1024,int frames=16000)
    {
        using IVgmWaveProvider p=VgmWaveProviderFactory.Create(VgmFile.Load(data)!,new AppSettings());
        Check(p is IndependentVgmWaveProvider,"factory selected independent player");
        byte[] output=new byte[frames*4];
        for(int i=0;i<frames;i+=chunk)
        {
            int size=Math.Min(chunk,frames-i)*4;
            int count=p.Read(output,i*4,size);
            if(count!=size) throw new Exception($"Unexpected EOF {i}/{frames}");
        }
        AudioBaseline.Verify(data,output);
        return output;
    }
    private static double Rms(byte[] samples)
    {
        var values=Enumerable.Range(1000,samples.Length/4-1000).Select(i=>(double)BinaryPrimitives.ReadInt16LittleEndian(samples.AsSpan(i*4))).ToArray();
        double mean=values.Average(); return Math.Sqrt(values.Average(x=>(x-mean)*(x-mean)));
    }
    private static int Crossings(byte[] samples)
    {
        var values=Enumerable.Range(1000,samples.Length/4-1000).Select(i=>(double)BinaryPrimitives.ReadInt16LittleEndian(samples.AsSpan(i*4))).ToArray();
        double mean=values.Average(); return Enumerable.Range(1,values.Length-1).Count(i=>values[i-1]<mean && values[i]>=mean);
    }
    internal static void Run()
    {
        var examples=new List<(string Name,byte[] Data)>();
        foreach(var c in new[]{(0x44,4000000u,(byte)0x55,"YM2203"),(0x48,8000000u,(byte)0x56,"YM2608"),(0x4c,8000000u,(byte)0x58,"YM2610")})
            examples.Add((c.Item4+" SSG",File(c.Item1,c.Item2,[c.Item3,7,0x3e,c.Item3,0,254,c.Item3,1,0,c.Item3,8,15])));
        var opm=new List<byte>(); var score=new MxdrvCore(MdxRegression.CreateTestMdx(),null);
        score.WriteOpmCallback=(r,v)=>opm.AddRange([0x54,(byte)r,v]); score.Tick();
        for(int p=0;p<opm.Count;p+=3) if(opm[p+1] is >=0x60 and <=0x7f) opm[p+2]=(byte)Math.Min(127,opm[p+2]+24);
        examples.Add(("YM2151",File(0x30,4000000,opm)));
        foreach(var c in new[]{(0x2c,7670454u,(byte)0x52,"YM2612"),(0x44,4000000u,(byte)0x55,"YM2203 FM"),
            (0x48,8000000u,(byte)0x56,"YM2608 FM"),(0x4c,8000000u,(byte)0x58,"YM2610 FM")})
        {
            var tone=new List<byte>(); void W(byte r,byte v)=>tone.AddRange([c.Item3,(byte)(c.Item1==0x4c && r!=0x28 ? r+1:r),(byte)(c.Item1==0x4c && r==0x28 ? v|1:v)]);
            W(0xb0,7); W(0xb4,0xc0); if(c.Item1==0x48) W(0x29,0x80);
            foreach(byte slot in new byte[]{0,4,8,12}) { W((byte)(0x30+slot),1); W((byte)(0x40+slot),0x18); W((byte)(0x50+slot),0x1f); W((byte)(0x80+slot),0x0f); }
            W(0xa4,0x22); W(0xa0,0x69); W(0x28,0xf0);
            examples.Add((c.Item4,File(c.Item1,c.Item2,tone)));
        }
        byte[] opnb=(byte[])examples.Single(e=>e.Name=="YM2612").Data.Clone();
        Put(opnb,0x2c,0); Put(opnb,0x4c,8000000u|0x80000000u);
        for(int n=256;n<opnb.Length-4;n+=3) opnb[n]=0x58;
        examples.Add(("YM2610B",opnb));
        foreach(var c in new[]{(0x50,(byte)0x5a,3579545u,"YM3812"),(0x54,(byte)0x5b,3579545u,"YM3526"),
            (0x58,(byte)0x5c,3579545u,"Y8950"),(0x5c,(byte)0x5e,14318180u,"YMF262")})
        {
            var tone=new List<byte>();
            foreach(var rv in new (byte,byte)[]{(0x20,0x21),(0x23,0x21),(0x40,0x20),(0x43,0),(0x60,0xf0),(0x63,0xf0),(0x80,0x0f),(0x83,0x0f),(0xc0,0x31),(0xa0,0x80),(0xb0,0x31)}) tone.AddRange([c.Item2,rv.Item1,rv.Item2]);
            examples.Add((c.Item4,File(c.Item1,c.Item3,tone)));
        }
        foreach(uint variant in new[]{0u})
            examples.Add((variant==0?"YM2413":"VRC7",File(0x10,3579545|variant,[0x51,0x30,0x10,0x51,0x10,0x80,0x51,0x20,0x19])));
        foreach(var (name,data) in examples)
        {
            byte[] output=Render(data);
            Check(Rms(output)>5,name+" audible output");
            Check(output.SequenceEqual(Render(data,127)),name+" callback invariant");
        }
        VgmStreamRegression.VerifyStandalone();
        Check(!Process.GetCurrentProcess().Modules.Cast<ProcessModule>().Any(m=>m.ModuleName.Equals("mf_vgm.dll",StringComparison.OrdinalIgnoreCase)),
            "independent playback has not loaded mf_vgm.dll");
        foreach(var (name,data) in examples)
        {
            byte[] independentAudio=Render(data);
            var referenceAudio=AudioBaseline.Reference(data);
            double current=Rms(independentAudio), reference=referenceAudio.Rms;
            Check(Math.Abs(Crossings(independentAudio)-referenceAudio.Crossings)<=2,name+" pitch crossing count matches reference");
            Console.WriteLine($"COMPARE {name}: RMS independent={current:F2}, reference={reference:F2}, ratio={current/reference:F4}");
            Check(reference>5 && Math.Abs(current/reference-1)<.025,name+" reference loudness within 2.5%");
        }
        for(int drum=0;drum<5;drum++)
        {
            byte[] data=File(0x10,3579545,[0x51,0x16,0x80,0x51,0x17,0x90,0x51,0x18,0xa0,
                0x51,0x26,8,0x51,0x27,8,0x51,0x28,8,0x51,0x36,0,0x51,0x37,0,0x51,0x38,0,0x51,0x0e,(byte)(0x20|(1<<drum))]);
            byte[] audio=Render(data,frames:4096);
            double reference=AudioBaseline.Reference(data,4096).Rms;
            Check(Rms(audio)>5,"OPLL rhythm audible "+drum);
            Check(Math.Abs(Rms(audio)/reference-1)<.025,"OPLL rhythm reference amplitude "+drum);
            Check(audio.SequenceEqual(Render(data,127,frames:4096)),"OPLL rhythm callback invariant "+drum);
        }
        VgmStreamRegression.Run();
        VerifyPcm();
        byte[] ssg=examples[0].Data;
        using(var player=new IndependentVgmWaveProvider(VgmFile.Load(ssg)!))
        {
            byte[] output=new byte[4096*4]; player.Read(output,0,output.Length);
            var channel=player.GetSnapshot().MusicChannels.Single(c=>c.Id=="YM2203:SSG:0");
            Check(player.SetChannelMuted(channel,true),"SSG mute accepts UI channel");
            player.Read(output,0,output.Length); Check(Rms(output)<1,"SSG mute silences output");
            Check(player.SetChannelMuted(channel,false),"SSG unmute accepted");
            player.Read(output,0,output.Length); Check(Rms(output)>5,"SSG unmute restores output");
            TimeSpan time=player.GetSnapshot().CurrentTime; player.IsPaused=true;
            player.Read(output,0,output.Length); Check(output.All(b=>b==0)&&player.GetSnapshot().CurrentTime==time,"pause silences and freezes time");
        }
        foreach(var (name,data) in examples.Where(e=>e.Name is "YM2151" or "YM2612" or "YM2413" or "YM3812" or "YMF262" or "Y8950"))
        {
            using var p=new IndependentVgmWaveProvider(VgmFile.Load(data)!);
            byte[] audio=new byte[2048*4]; p.Read(audio,0,audio.Length);
            string id=p.GetSnapshot().MusicChannels.First(c=>c.IsKeyOn || c.Label=="MIX").Id;
            VerifyMute(data,id);
        }
        foreach(byte volume in new byte[]{0x20,0xc1,0xff})
        {
            byte[] changed=(byte[])ssg.Clone(); changed[0x7c]=volume;
            Check(Math.Abs(Rms(Render(changed))/AudioBaseline.Reference(changed).Rms-1)<.025,"volume modifier "+volume.ToString("X2"));
        }
        using(var p=VgmWaveProviderFactory.Create(VgmFile.Load(ssg)!,new AppSettings { Nd6Ndsif=true }))
            Check(p is IndependentVgmWaveProvider,"NDSIF setting uses the independent player");
        byte[] second=(byte[])ssg.Clone(); Put(second,0x44,4000000|0x40000000);
        for(int p=256;p<268;p+=3) second[p]=0xa5;
        Check(Rms(Render(second))>5,"second chip audio");
        Check(Math.Abs(Rms(Render(second))/AudioBaseline.Reference(second).Rms-1)<.025,"dual-chip normalization matches reference");
        byte[] variants=File(0x10,0x80000000|3579545,[]);
        Check(IndependentVgmWaveProvider.Supports(VgmFile.Load(variants)!,out _),"VRC7 uses standalone ymfm");
        byte[] shortFile=File(0x44,4000000,[],735+882+16);
        shortFile=[..shortFile[..256],0x62,0x63,0x7f,0x66]; Put(shortFile,4,(uint)shortFile.Length-4);
        using(var p=new IndependentVgmWaveProvider(VgmFile.Load(shortFile)!))
        {
            byte[] b=new byte[2000*4]; Check(p.Read(b,0,b.Length)==(735+882+16)*4,"wait commands have exact frame count");
            Check(!p.IsPlaying && p.Read(b,0,b.Length)==0,"EOF drains naturally");
        }
        byte[] loop=File(0x44,4000000,[],100); Put(loop,0x1c,256-0x1c);
        using(var p=new IndependentVgmWaveProvider(VgmFile.Load(loop)!))
        {
            p.Read(new byte[250*4],0,250*4); Check(p.GetSnapshot().LoopCount==2,"loop count and remainder");
        }
        byte[] extra=(byte[])ssg.Clone(); Put(extra,0xbc,4); Put(extra,0xc0,4);
        Check(IndependentVgmWaveProvider.Supports(VgmFile.Load(extra)!,out _),"extra header accepted");
        byte[] mixed=(byte[])ssg.Clone(); Put(mixed,0x0c,3579545);
        using(var p=VgmWaveProviderFactory.Create(VgmFile.Load(mixed)!,new AppSettings()))
            Check(p is IndependentVgmWaveProvider,"mixed chips use the independent player");
        byte[] stream=File(0x2c,7670454,[0x90,0,2,0,0x2a]);
        Check(IndependentVgmWaveProvider.Supports(VgmFile.Load(stream)!,out _),"YM2612 DAC stream setup admitted");
        byte[] badLoop=File(0x44,4000000,[],100); Put(badLoop,0x1c,257-0x1c);
        byte[] zeroLoop=File(0x44,4000000,[],0); Put(zeroLoop,0x1c,256-0x1c);
        byte[] truncated=File(0x44,4000000,[],0)[..257]; truncated[256]=0x55; Put(truncated,4,(uint)truncated.Length-4);
        foreach(var bad in new[]{badLoop,zeroLoop,truncated})
        {
            bool rejected=false;
            try { IndependentVgmWaveProvider.Supports(VgmFile.Load(bad)!,out _); }
            catch(InvalidDataException) { rejected=true; }
            Check(rejected,"invalid loop/command boundary rejected");
        }
        // ループ内の待機上書きで2周目以降の待ち時間が0になるデータは、再生前に拒否する。
        byte[] zeroedLoop=File(0x44,4000000,[0x62,0x64,0x62,0,0],0); Put(zeroedLoop,0x1c,256-0x1c);
        bool zeroedRejected=false;
        try { IndependentVgmWaveProvider.Supports(VgmFile.Load(zeroedLoop)!,out _); }
        catch(InvalidDataException) { zeroedRejected=true; }
        Check(zeroedRejected,"loop whose wait override removes all later waits is rejected");
        byte[] overrideLoop=File(0x44,4000000,[0x64,0x62,10,0,0x62],0); Put(overrideLoop,0x1c,256-0x1c);
        using(var p=new IndependentVgmWaveProvider(VgmFile.Load(overrideLoop)!))
        {
            p.Read(new byte[35*4],0,35*4); Check(p.GetSnapshot().LoopCount==3,"loop with a non-zero wait override keeps looping");
        }
        // VGZ: 通常の圧縮は読み込み、展開後が上限を超えるものは読み込まない。
        static byte[] Gzip(Action<Stream> write)
        {
            using var output=new MemoryStream();
            using(var gz=new System.IO.Compression.GZipStream(output,System.IO.Compression.CompressionLevel.Fastest)) write(gz);
            return output.ToArray();
        }
        Check(VgmFile.Load(Gzip(s=>s.Write(loop)))?.RawData.Length==loop.Length,"VGZ within the size limit loads");
        byte[] bomb=Gzip(s=>
        {
            s.Write(loop); byte[] zeros=new byte[1<<20];
            for(long written=0;written<=VgmFile.MaxUncompressedBytes;written+=zeros.Length) s.Write(zeros);
        });
        Check(bomb.Length<4*1024*1024 && VgmFile.Load(bomb)==null,"VGZ expanding beyond the size limit is not loaded");
        byte[] compressed=File(0x2c,7670454,[0x67,0x66,0x40,10,0,0,0,0,0,0,0,0,8,9,0,0,0]);
        Check(!IndependentVgmWaveProvider.Supports(VgmFile.Load(compressed)!,out _),"invalid compressed bit width is rejected");
        byte[] lateRom=File(0x48,8000000,[0x56,0x29,0x80,0x67,0x66,0x81,8,0,0,0,0,0,4,0,0,0,0,0]);
        Check(IndependentVgmWaveProvider.Supports(VgmFile.Load(lateRom)!,out _),"ROM replacement after registers is supported");
        byte[] s98=new byte[48]; "S983"u8.CopyTo(s98); Put(s98,4,1); Put(s98,8,100); Put(s98,20,48); Put(s98,28,1); Put(s98,32,2); Put(s98,36,4000000);
        s98=[..s98,0,7,0x3e,0,0,254,0,1,0,0,8,15,..Enumerable.Repeat((byte)0xff,40),0xfd];
        Check(Rms(Render(S98Decoder.Convert(s98,"generated S98")))>5,"converted S98 uses independent playback");
        foreach(var data in new[]{File(0x44,4000000,[])[..^1],File(0x44,4000000,[0x67,0x66,0x81,255,255,255,127])})
        {
            bool rejected=false;
            try { rejected=!IndependentVgmWaveProvider.Supports(VgmFile.Load(data)!,out _); }
            catch(InvalidDataException) { rejected=true; }
            Check(rejected,"malformed or excessive data is rejected before synthesis");
        }
    }
    private static void VerifyMute(byte[] data,string id)
    {
        using var p=new IndependentVgmWaveProvider(VgmFile.Load(data)!);
        byte[] audio=new byte[2048*4]; p.Read(audio,0,audio.Length);
        var channel=p.GetSnapshot().MusicChannels.Single(c=>c.Id==id);
        Check(Rms(audio)>5,id+" active before mute");
        Check(p.SetChannelMuted(channel,true),id+" UI mute accepted");
        p.Read(audio,0,audio.Length); Check(Rms(audio)<1,id+" silent while muted");
        Check(p.SetChannelMuted(channel,false),id+" unmute accepted");
        p.Read(audio,0,audio.Length); Check(Rms(audio)>5,id+" restored after unmute");
    }
    private static void VerifyPcm()
    {
        var bank=new List<byte>{0x67,0x66,0}; byte[] wave=Enumerable.Range(0,2000).Select(i=>(byte)((i&7)<4?32:224)).ToArray();
        bank.AddRange(BitConverter.GetBytes(wave.Length)); bank.AddRange(wave);
        bank.AddRange([0x52,0xb6,0xc0,0x52,0x2b,0x80,0xe0,0,0,0,0]); bank.AddRange(Enumerable.Repeat((byte)0x8f,wave.Length));
        byte[] dac=File(0x2c,7670454,bank);
        byte[] output=Render(dac);
        Check(Rms(output)>100,"legacy PCM bank and DAC wait commands");
        Check(output.SequenceEqual(Render(dac,127)),"legacy DAC callback invariant");
        Check(Math.Abs(Rms(output)/AudioBaseline.Reference(dac).Rms-1)<.025,"legacy DAC reference amplitude");
        // Loop starts at the explicit seek; sample banks are not duplicated on repeats.
        int seek=256+7+wave.Length+6; Put(dac,0x1c,(uint)(seek-0x1c));
        Check(Render(dac,127,frames:120000).SequenceEqual(Render(dac,1024,frames:120000)),"legacy DAC repeats without bank growth or timing drift");
        foreach(bool b in new[]{false,true})
        {
            var commands=new List<byte>{0x67,0x66,(byte)(b?0x83:0x82)};
            byte[] rom=new byte[8+4096]; Put(rom,0,4096); for(int n=8;n<rom.Length;n++) rom[n]=(byte)((n&1)==0?0x17:0x9f);
            commands.AddRange(BitConverter.GetBytes(rom.Length)); commands.AddRange(rom);
            void W(byte r,byte v)=>commands.AddRange([(byte)(b?0x58:0x59),r,v]);
            if(b) { W(0x11,0xc0); W(0x12,0); W(0x13,0); W(0x14,0xff); W(0x15,0); W(0x19,0xff); W(0x1a,0xff); W(0x1b,0xff); W(0x10,0x80); }
            else { W(1,0x3f); W(8,0xdf); W(0x10,0); W(0x18,0); W(0x20,0xf); W(0x28,0); W(0,1); }
            byte[] data=File(0x4c,8000000,commands);
            byte[] audio=Render(data,frames:4096);
            Check(Rms(audio)>5,"YM2610 ROM ADPCM "+(b?"B":"A"));
            Check(audio.SequenceEqual(Render(data,127,frames:4096)),"ADPCM callback invariant");
            Check(Math.Abs(Rms(audio)/AudioBaseline.Reference(data,4096).Rms-1)<.025,"ADPCM reference amplitude");
            VerifyMute(data,"YM2610:ADPCM:"+(b?"6":"0"));
        }
        foreach(bool upload in new[]{false,true})
        {
            var commands=new List<byte>();
            if(!upload)
            {
                byte[] rom=new byte[8+1024]; Put(rom,0,262144); for(int n=8;n<rom.Length;n++) rom[n]=(byte)((n&1)==0?0x17:0x9f);
                commands.AddRange([0x67,0x66,0x81]); commands.AddRange(BitConverter.GetBytes(rom.Length)); commands.AddRange(rom);
            }
            void W(byte r,byte v)=>commands.AddRange([0x57,r,v]);
            W(0,1); W(1,0x82); W(2,0); W(3,0); W(4,31); W(5,0); W(0x0c,0xff); W(0x0d,0xff);
            if(upload) { W(0,0x60); for(int i=0;i<1024;i++) W(8,(byte)((i&1)==0?0x17:0x9f)); W(0,1); }
            W(9,0xff); W(10,0xff); W(11,0xff); W(0,0xa0);
            byte[] data=File(0x48,8000000,commands);
            Check(Rms(Render(data,frames:4096))>5,"YM2608 ADPCM "+(upload?"register upload":"ROM block"));
            Check(Render(data,frames:4096).SequenceEqual(Render(data,127,frames:4096)),"OPNA ADPCM callback invariant");
        }
        {
            byte[] rom=new byte[8+8192]; Put(rom,0,8192);
            for(int n=8;n<rom.Length;n++) rom[n]=(byte)((n&1)==0?0x17:0x9f);
            var commands=new List<byte>{0x67,0x66,0x88}; commands.AddRange(BitConverter.GetBytes(rom.Length)); commands.AddRange(rom);
            void W(byte r,byte v)=>commands.AddRange([0x5c,r,v]);
            W(7,1); W(8,1); W(9,0); W(10,0); W(11,0xff); W(12,0); W(0x10,0xff); W(0x11,0xff); W(0x12,0x80); W(7,0xa0);
            byte[] data=File(0x58,3579545,commands);
            byte[] audio=Render(data,frames:4096);
            Check(Rms(audio)>5,"Y8950 ADPCM ROM transfer");
            Check(Math.Abs(Rms(audio)/AudioBaseline.Reference(data,4096).Rms-1)<.025,"Y8950 ADPCM reference amplitude");
            VerifyMute(data,"Y8950:MIX");
        }
        using var synthetic=new SyntheticRhythmRom();
        for(int drum=0;drum<6;drum++)
        {
            byte[] data=File(0x48,8000000,[0x56,0x11,63,0x56,(byte)(0x18+drum),0xdf,0x56,0x10,(byte)(1<<drum)]);
            byte[] audio=Render(data,frames:4096); Check(audio.Any(b=>b!=0),"external synthetic rhythm "+drum);
        }
        var resolve=typeof(MdxWaveProvider).Assembly.GetType("MirrorFlux.Services.Music.Ym2608RhythmRom")!
            .GetMethod("ResolveDirectory",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic)!;
        string path=Path.Combine((string)resolve.Invoke(null,[Environment.ProcessPath,AppContext.BaseDirectory])!,"YM2608_ADPCM.ROM");
        byte[] saved=System.IO.File.ReadAllBytes(path);
        try
        {
            System.IO.File.Delete(path);
            Check(Render(File(0x48,8000000,[0x56,0x11,63,0x56,0x18,0xdf,0x56,0x10,1])).All(b=>b==0),"missing external rhythm ROM is silent");
        }
        finally { System.IO.File.WriteAllBytes(path,saved); }
    }

}
