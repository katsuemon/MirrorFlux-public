using System.Buffers.Binary;
using System.IO;
using MirrorFlux.Models;
using MirrorFlux.Services.Vgm;
using MirrorFlux.Services.Music;

internal static class VgmStreamRegression
{
    private static void Check(bool ok,string name) { if(!ok) throw new Exception(name); Console.WriteLine("PASS PCM stream: "+name); }
    private static byte[] U32(uint n)=>BitConverter.GetBytes(n);
    private static byte[] Pack(int bits,params int[] codes)
    {
        byte[] data=new byte[(bits*codes.Length+7)/8]; int at=0;
        foreach(int code in codes) for(int b=bits-1;b>=0;b--,at++) data[at/8]|=(byte)(((code>>b)&1)<<(7-at%8));
        return data;
    }
    private static byte[] Compressed(byte type,byte subtype,byte decoded,byte encoded,ushort bias,params int[] codes)=>
        [type,..U32((uint)(codes.Length*(decoded/8))),decoded,encoded,subtype,(byte)bias,(byte)(bias>>8),..Pack(encoded,codes)];
    private static byte[] Block(byte kind,byte[] payload)=>[0x67,0x66,kind,..U32((uint)payload.Length),..payload];
    private static byte[] Table(byte type,byte subtype,byte decoded,byte encoded,params ushort[] values)=>
        [type,subtype,decoded,encoded,(byte)values.Length,(byte)(values.Length>>8),..values.SelectMany(v=>decoded==8?new[]{(byte)v}:new[]{(byte)v,(byte)(v>>8)})];
    internal static void VerifyStandalone()
    {
        byte[] wave=Compressed(0,1,8,4,0,Enumerable.Range(0,512).Select(i=>(i/8)&15).ToArray());
        byte[] data=Vgm([..Block(0x40,wave),0x52,0xb6,0xc0,0x52,0x2b,0x80,
            0x90,0,2,0,0x2a,0x91,0,0,1,0,0x92,0,..U32(11025),0x95,0,0,0,1]);
        using var player=new IndependentVgmWaveProvider(VgmFile.Load(data)!);
        byte[] audio=new byte[2048*4]; player.Read(audio,0,audio.Length);
        Check(Rms(audio)>100,"compressed stream produces standalone audio");
        Check(player.GetSnapshot().MusicChannels.Single(c=>c.Id=="YM2612:DAC").IsKeyOn,"compressed stream activity reaches the shared display monitor");
    }
    internal static void Run()
    {
        var banks=new VgmPcmBanks();
        banks.Load(0x40,Compressed(0,0,8,5,32,0,1,17,31,5),0);
        Check(banks.Get(0)!.Bytes.SequenceEqual(new byte[]{32,33,49,63,37}),"MSB-first codes cross byte boundaries");
        banks.Load(0x40,Compressed(0,0,8,5,32,0,1,17,31,5),0);
        Check(banks.Get(0)!.Blocks.Count==1,"loop does not append a data block again");
        banks.Load(0x41,Compressed(0,1,16,4,0x100,0,1,15),1);
        Check(banks.Get(1)!.Bytes.SequenceEqual(new byte[]{0,1,0,0x11,0,0xf1}),"16-bit shifted values are little endian");
        banks.Load(0x7f,Table(0,2,8,2,10,20,30,40),2);
        banks.Load(0x42,Compressed(0,2,8,2,99,3,0,2,1),3);
        Check(banks.Get(2)!.Bytes.SequenceEqual(new byte[]{40,10,30,20}),"table lookup ignores the addition value");
        banks.Load(0x7f,Table(1,0,8,2,1,255,2,254),4);
        banks.Load(0x43,Compressed(1,0,8,2,255,0,0,1,3),5);
        Check(banks.Get(3)!.Bytes.SequenceEqual(new byte[]{0,1,0,254}),"DPCM state wraps at eight bits");
        banks.Load(0x7f,Table(0,2,8,2,50,60,70,80),6);
        banks.Load(0x42,Compressed(0,2,8,2,0,0,1),7);
        Check(banks.Get(2)!.Bytes.SequenceEqual(new byte[]{40,10,30,20,50,60}),"replacement table affects following blocks only");
        banks.Load(0x7f,Table(1,0,16,1,1,65535),8);
        banks.Load(0x44,Compressed(1,0,16,1,65535,0,0,1),9);
        Check(banks.Get(4)!.Bytes.SequenceEqual(new byte[]{0,0,1,0,0,0}),"16-bit DPCM wrap and little endian output");
        foreach(var invalid in new[]{new byte[9],Compressed(0,0,8,8,0,1)[..^1],Compressed(0,2,8,2,0,1),
            new byte[]{0,1,0,0,0,16,4,0,0,0},new byte[]{0,255,255,255,127,8,1,0,0,0}})
        {
            bool rejected=false;
            try { new VgmPcmBanks().Load(0x40,invalid,0); }
            catch(Exception ex) when(ex is InvalidDataException or NotSupportedException) { rejected=true; }
            Check(rejected,"malformed/oversized compressed block rejected before allocation or playback");
        }
        VerifyScheduler(); VerifyCompatibilityPolicy(); VerifyAudio();
    }
    private static void VerifyScheduler()
    {
        var banks=new VgmPcmBanks(); banks.Load(0,[10,11,12,13,14,15,16,17],0);
        int frame=0; var events=new List<(int Frame,byte Value)>();
        var streams=new VgmDacStreams(banks,(_,_,_,v)=>events.Add((frame,v)));
        streams.Command([0x90,0,2,0,0x2a]); streams.Command([0x91,0,0,2,1]);
        streams.Command([0x92,0,..U32(11025)]); streams.Command([0x93,0,..U32(0),1,..U32(4)]);
        for(frame=0;frame<20;frame++) { streams.EmitDue(); streams.Advance(1); }
        Check(events.SequenceEqual(new[]{(0,(byte)11),(4,(byte)13),(8,(byte)15),(12,(byte)17)}),"interleaved step/base and finite count");
        events.Clear(); frame=0; streams.Command([0x95,0,0,0,0x11]);
        for(frame=0;frame<20;frame++) { streams.EmitDue(); streams.Advance(1); }
        Check(events.SequenceEqual(new[]{(0,(byte)17),(4,(byte)15),(8,(byte)13),(12,(byte)11),(16,(byte)17)}),"fast reverse loop restarts at last element");
        events.Clear(); frame=0; streams.Command([0x91,0,0,1,0]); streams.Command([0x92,0,..U32(16000)]);
        streams.Command([0x93,0,..U32(0),0x81,..U32(8)]);
        for(frame=0;frame<44100;frame++) { streams.EmitDue(); streams.Advance(1); }
        Check(events.Count==16000 && events.Take(5).Select(e=>e.Frame).SequenceEqual(new[]{0,3,6,9,12}),"non-dividing sample rate has no cumulative drift");
        streams.Command([0x94,255]); int count=events.Count; streams.EmitDue(); streams.Advance(1000); streams.EmitDue();
        Check(events.Count==count,"stop-all prevents further writes");
        streams.Command([0x90,255,2,0,0x2a]); streams.Command([0x95,255,0,0,1]); streams.EmitDue();
        Check(events.Count==count,"reserved stream ID is ignored");
        streams.Command([0x92,0,..U32(0)]); streams.Command([0x93,0,..U32(0),1,..U32(8)]); streams.EmitDue(); streams.Advance(100);
        Check(events.Count==count,"zero sample rate suspends stream");
        streams.Command([0x92,0,..U32(11025)]); streams.EmitDue(); Check(events.Count==count+1,"restored sample rate resumes stream");
    }
    private static void VerifyCompatibilityPolicy()
    {
        var banks=new VgmPcmBanks(); banks.Load(0,[10,20,30,40],0);
        var values=new List<byte>(); var streams=new VgmDacStreams(banks,(_,_,_,v)=>values.Add(v),strictBounds:false,stopOnSetup:false);
        streams.Command([0x90,0,2,0,0x2a]); streams.Command([0x91,0,0,1,0]); streams.Command([0x92,0,..U32(88200)]);
        streams.Command([0x93,0,..U32(0),1,..U32(8)]);
        streams.EmitDue(); streams.Advance(1); streams.EmitDue();
        Check(values.SequenceEqual(new byte[]{10,20,30}),"legacy telemetry accepts rates above output rate and clamps sample count");
        streams.Command([0x90,0,2,0,0x2a]); streams.Advance(1); streams.EmitDue();
        Check(values.SequenceEqual(new byte[]{10,20,30,40}),"NDSIF retains its existing setup-without-stop behavior");
        values.Clear(); streams.Command([0x92,0,..U32(uint.MaxValue)]);
        streams.Command([0x93,0,..U32(0),2,..U32(uint.MaxValue)]); streams.EmitDue(); streams.Advance(1); streams.EmitDue();
        Check(values.Count==4,"large duration arithmetic clamps without signed overflow");
        byte[] high=Vgm([0x90,0,2,0,0x2a,0x92,0,..U32(88200)]);
        Check(IndependentVgmWaveProvider.Supports(VgmFile.Load(high)!,out _),"high-rate stream uses independent scheduling");
        byte[] mikey=Vgm([]); BinaryPrimitives.WriteUInt32LittleEndian(mikey.AsSpan(0xe4),16000000);
        Check(IndependentVgmWaveProvider.Supports(VgmFile.Load(mikey)!,out _),"Mikey mixed with Yamaha is supported");
    }
    private static byte[] Vgm(IEnumerable<byte> commands,bool dual=false)
    {
        byte[] header=new byte[256]; "Vgm "u8.CopyTo(header);
        void Put(int p,uint v)=>BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(p),v);
        Put(8,0x171); Put(0x34,256-0x34); Put(0x2c,7670454u|(dual?0x40000000u:0));
        byte[] bytes=[..header,..commands,0x61,0xff,0x7f,0x66]; BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4),(uint)bytes.Length-4); return bytes;
    }
    private static byte[] Render(byte[] data,int chunk=1024)
    {
        using IVgmWaveProvider p=VgmWaveProviderFactory.Create(VgmFile.Load(data)!,new AppSettings());
        Check(p is IndependentVgmWaveProvider,"whole file selects independent route");
        byte[] output=new byte[16000*4];
        for(int i=0;i<16000;i+=chunk) { int size=Math.Min(chunk,16000-i)*4; int got=p.Read(output,i*4,size); if(got!=size) throw new Exception($"Stream read {got}/{size} at {i}, playing={p.IsPlaying}, loops={p.GetSnapshot().LoopCount}"); }
        AudioBaseline.Verify(data,output);
        return output;
    }
    private static double Rms(byte[] data)
    {
        var samples=Enumerable.Range(1000,data.Length/4-1000).Select(i=>(double)BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(i*4))).ToArray();
        double mean=samples.Average(); return Math.Sqrt(samples.Average(v=>(v-mean)*(v-mean)));
    }
    private static void VerifyAudio()
    {
        byte[] wave=Enumerable.Range(0,512).Select(i=>(byte)(128+80*Math.Sin(2*Math.PI*i/64))).ToArray();
        foreach(bool second in new[]{false,true})
        foreach(byte flags in new byte[]{1,0x11})
        foreach(uint frequency in new uint[]{11025,16000,22050,44100})
        {
            byte cmd=second?(byte)0xa2:(byte)0x52;
            byte[] data=Vgm([..Block(0,wave),cmd,0xb6,0xc0,cmd,0x2b,0x80,
                0x90,0,(byte)(second?0x82:2),0,0x2a,0x91,0,0,1,0,0x92,0,..U32(frequency),0x95,0,0,0,flags],second);
            byte[] audio=Render(data);
            Check(audio.SequenceEqual(Render(data,chunk:127)),"stream callback invariant");
            double ratio=Rms(audio)/AudioBaseline.Reference(data).Rms;
            Check(AudioBaseline.Verify(data,audio),"stream waveform matches the validated timing baseline");
            Console.WriteLine($"COMPARE stream second={second} reverse={(flags&16)!=0} rate={frequency}: {ratio:F4}");
            Check(Rms(audio)>100 && Math.Abs(ratio-1)<.025,"stream reference amplitude");
        }
        var scenarios=new Dictionary<string,byte[]> {
            ["until end, interleaved"]=[0x91,0,0,2,1,0x93,0,..U32(0),0x83,..U32(0)],
            ["retained position"]=[0x93,0,..U32(16),0x81,..U32(128),0x61,0x35,0x12,0x93,0,..U32(uint.MaxValue),0x81,..U32(64)],
            ["mode zero"]=[0x93,0,..U32(0),0x81,..U32(128),0x61,0x35,0x12,0x93,0,..U32(256),0x80,..U32(0)],
            ["reset control while playing"]=[0x95,0,0,0,1,0x61,0x35,0x12,0x90,0,2,0,0x2a],
            ["frequency change"]=[0x95,0,0,0,1,0x61,0x35,0x12,0x92,0,..U32(16000)],
            ["stop and restart"]=[0x95,0,0,0,1,0x61,0x35,0x12,0x94,255,0x61,0xe8,3,0x95,0,0,0,1],
            ["second data block"]=[0x95,0,1,0,1],
            ["zero frequency and resume"]=[0x95,0,0,0,1,0x61,0x35,0x12,0x92,0,..U32(0),0x61,0xe8,3,0x92,0,..U32(11025)],
            ["change data stepping"]=[0x95,0,0,0,1,0x61,0x35,0x12,0x91,0,0,2,1,0x95,0,1,0,1],
            ["ignored reserved ID"]=[0x90,255,0xff,0xff,0xff,0x91,255,255,255,255,0x92,255,..U32(uint.MaxValue),0x93,255,..U32(uint.MaxValue),255,..U32(uint.MaxValue),0x95,255,255,255,255,0x95,0,0,0,1]
        };
        foreach(var (name,commands) in scenarios)
        {
            byte[] data=Vgm([..Block(0,wave),..Block(0,wave.Reverse().ToArray()),0x52,0xb6,0xc0,0x52,0x2b,0x80,
                0x90,0,2,0,0x2a,0x91,0,0,1,0,0x92,0,..U32(11025),..commands]);
            byte[] audio=Render(data);
            double ratio=Rms(audio)/AudioBaseline.Reference(data).Rms;
            Console.WriteLine($"COMPARE {name}: ratio={ratio:F4}");
            Check(Math.Abs(ratio-1)<.025 && AudioBaseline.Verify(data,audio),"stream command behavior: "+name);
            Check(audio.SequenceEqual(Render(data,chunk:127)),"command changes are callback invariant: "+name);
        }
        byte[] late=Vgm([..Block(0,wave),0x90,0,2,0,0x2a,0x91,0,0,1,0,0x92,0,..U32(11025),0x95,0,0,0,1,0x61,0,1,..Block(0,wave)]);
        Check(IndependentVgmWaveProvider.Supports(VgmFile.Load(late)!,out _),"late PCM append is supported");
        byte[] milliseconds=Vgm([..Block(0,wave),0x90,0,2,0,0x2a,0x91,0,0,1,0,0x92,0,..U32(11025),0x93,0,..U32(0),0x82,..U32(40)]);
        Check(IndependentVgmWaveProvider.Supports(VgmFile.Load(milliseconds)!,out _),"millisecond lengths use the public VGM units");
        byte[] loop=Vgm([..Block(0,wave),0x52,0xb6,0xc0,0x52,0x2b,0x80,0x90,0,2,0,0x2a,
            0x91,0,0,1,0,0x92,0,..U32(11025),0x95,0,0,0,1]);
        BinaryPrimitives.WriteUInt32LittleEndian(loop.AsSpan(0x1c),256-0x1c);
        loop[^3]=0; loop[^2]=32;
        BinaryPrimitives.WriteUInt32LittleEndian(loop.AsSpan(0x18),8192);
        BinaryPrimitives.WriteUInt32LittleEndian(loop.AsSpan(0x20),8192);
        byte[] loopAudio=Render(loop);
        Check(loopAudio.SequenceEqual(Render(loop,chunk:127)),"VGM loops retain stream timing and do not duplicate banks");
        // The reference player misreads a repeated bank definition inside a loop.
        // Compare equivalent looping commands after the bank; independently test
        // bank-containing loops above so that this legacy defect is not reproduced.
        BinaryPrimitives.WriteUInt32LittleEndian(loop.AsSpan(0x1c),256+7+512-0x1c);
        Check(AudioBaseline.Verify(loop,Render(loop)),"stream restart across VGM loops retains validated timing");
        foreach(byte width in new byte[]{8,16})
        foreach(byte method in new byte[]{0,1,2,3})
        {
            int[] codes=Enumerable.Range(0,512).Select(i=>(i/8)&15).ToArray();
            byte[] payload; var tableBytes=new List<byte>(); var words=new List<ushort>();
            ushort[] table=Enumerable.Range(0,16).Select(i=>(ushort)(width==8?i*12:0x1234+i*127)).ToArray();
            if(method==3) table=Enumerable.Range(0,16).Select(i=>(ushort)(i%2==0?i/2+1:65536-i/2-1)).ToArray();
            if(method>=2) tableBytes.AddRange(Block(0x7f,Table((byte)(method==3?1:0),(byte)(method==3?0:2),width,4,table)));
            payload=Compressed((byte)(method==3?1:0),(byte)(method==3?0:method),width,4,32,codes);
            int state=32,mask=width==8?255:65535;
            foreach(int code in codes)
            {
                int value=method switch {0=>code+32,1=>(code<<(width-4))+32,2=>table[code],_=>state+table[code]};
                state=value&mask; words.Add((ushort)state);
            }
            byte[] plain=words.SelectMany(v=>width==8?new[]{(byte)v}:new[]{(byte)v,(byte)(v>>8)}).ToArray();
            byte[] Play(byte[] block)=>Vgm([..tableBytes,..block,0x52,0xb6,0xc0,0x52,0x2b,0x80,0xe0,0,0,0,0,..Enumerable.Repeat((byte)0x8f,512)]);
            byte[] packed=Play(Block(0x40,payload)), decoded=Play(Block(0,plain));
            Check(Render(packed).SequenceEqual(Render(decoded)),"legacy DAC decoded audio "+width+"-bit method "+method);
            Check(AudioBaseline.Verify(packed,Render(packed)),"validated decoded audio "+width+"-bit method "+method);
        }
        foreach(byte compression in new byte[]{0,1,2,3})
        {
            int[] codes=Enumerable.Range(0,512).Select(i=>(i/8)&15).ToArray();
            byte[] payload, expected; var tables=new List<byte>();
            if(compression<2)
            {
                payload=Compressed(0,compression,8,4,32,codes);
                expected=codes.Select(c=>(byte)(32+(compression==0?c:c<<4))).ToArray();
            }
            else if(compression==2)
            {
                ushort[] values=Enumerable.Range(0,16).Select(i=>(ushort)(32+i*12)).ToArray();
                tables.AddRange(Block(0x7f,Table(0,2,8,4,values))); payload=Compressed(0,2,8,4,0,codes);
                expected=codes.Select(c=>(byte)values[c]).ToArray();
            }
            else
            {
                tables.AddRange(Block(0x7f,Table(1,0,8,4,1,255,2,254,3,253,4,252,5,251,6,250,7,249,8,248)));
                payload=Compressed(1,0,8,4,128,codes); int current=128;
                expected=codes.Select(c=>(byte)(current=(current+(c%2==0?c/2+1:-(c/2+1)))&255)).ToArray();
            }
            byte[] Play(byte[] block)=>Vgm([..tables,..block,0x52,0xb6,0xc0,0x52,0x2b,0x80,
                0x90,0,2,0,0x2a,0x91,0,0,1,0,0x92,0,..U32(11025),0x95,0,0,0,1]);
            byte[] compressed=Play(Block(0x40,payload)), plain=Play(Block(0,expected));
            Check(Render(compressed).SequenceEqual(Render(plain)),"compressed and plain PCM audio are identical method "+compression);
            Check(AudioBaseline.Verify(compressed,Render(compressed)),"validated decoded PCM method "+compression);
        }
    }
}
