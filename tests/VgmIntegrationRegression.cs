using System.Buffers.Binary;
using System.Diagnostics;
using MirrorFlux.Services.Vgm;
internal static class VgmIntegrationRegression {
 static void Check(bool ok,string text){if(!ok)throw new Exception(text);Console.WriteLine("PASS VGM integration: "+text);}
 static void Put(byte[] b,int p,uint value)=>BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(p),value);
 static byte[] File(int field,uint clock,IEnumerable<byte> commands,int frames=24000) {
  byte[] b=new byte[256];"Vgm "u8.CopyTo(b);Put(b,8,0x172);Put(b,0x34,256-0x34);Put(b,field,clock);
  b=[..b,..commands,0x61,(byte)frames,(byte)(frames>>8),0x66];Put(b,4,(uint)b.Length-4);return b;
 }
 static byte[] Render(byte[] data,int chunk=1024,int frames=12000) {
  using var p=new IndependentVgmWaveProvider(VgmFile.Load(data)!);byte[] output=new byte[frames*4];
  for(int n=0;n<frames;n+=chunk){int size=Math.Min(chunk,frames-n)*4;if(p.Read(output,n*4,size)!=size)throw new Exception("Unexpected EOF");}
  return output;
 }
 static int Range(byte[] data)=>Enumerable.Range(100,data.Length/4-100).Select(i=>(int)BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(i*4))).Max()-Enumerable.Range(100,data.Length/4-100).Select(i=>(int)BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(i*4))).Min();
 static byte[] Block(byte type,byte[] rom,uint size,uint offset=0)=>[0x67,0x66,type,..BitConverter.GetBytes(rom.Length+8),..BitConverter.GetBytes(size),..BitConverter.GetBytes(offset),..rom];
 internal static void Run() {
  byte[] wait=File(0x0c,3579545,[0x64,0x62,7,0,0x64,0x63,11,0,0x62,0x63,0x7f],0);
  using(var p=new IndependentVgmWaveProvider(VgmFile.Load(wait)!)) {byte[] b=new byte[4096];Check(p.Read(b,0,b.Length)==34*4 && !p.IsPlaying,"wait overrides and EOF are sample exact");}
  var banks=new VgmPcmBanks();banks.Load(0,[1,2,3,4,5,6,7,8],0);var writes=new List<ushort>();
  var stream=new VgmDacStreams(banks,(_,_,_,_)=>throw new Exception("word stream routed to byte writer"),writeWord:(_,_,_,v)=>writes.Add(v));
  stream.Command([0x90,0,31,0,6]);stream.Command([0x91,0,0,1,0]);stream.Command([0x92,0,0x44,0xac,0,0]);stream.Command([0x95,0,0,0,0]);
  for(int i=0;i<4;i++){stream.EmitDue();stream.Advance(1);}Check(writes.SequenceEqual(new ushort[]{0x201,0x403,0x605,0x807}),"16-bit DAC words retain ordering and byte stride");
  // A new bank extends the data available to subsequent starts, using public millisecond units.
  banks.Load(0,[9,10],1);writes.Clear();stream.Command([0x92,0,0xe8,3,0,0]);stream.Command([0x93,0,0,0,0,0,2,5,0,0,0]);
  for(int i=0;i<221;i++){stream.EmitDue();stream.Advance(1);}Check(writes.Count==5 && writes[^1]==0xa09,"late bank append and millisecond stream length");
  var fds=new List<byte>{0xb4,0x3f,2,0xb4,0x29,0x80};
  for(int i=0;i<64;i++)fds.AddRange([0xb4,(byte)(0x40+i),(byte)(32+30*Math.Sin(i*Math.PI/32))]);
  fds.AddRange([0xb4,0x29,0,0xb4,0x20,0x9f,0xb4,0x27,0x80,0xb4,0x22,0x80,0xb4,0x23,1]);
  byte[] fdsFile=File(0x84,0x80000000|1789773,fds);byte[] fdsAudio=Render(fdsFile);
  Check(Range(fdsAudio)>100,"NES FDS synthesizes its uploaded wavetable");Check(fdsAudio.SequenceEqual(Render(fdsFile,127)),"FDS output is callback independent");
  var swan=new List<byte>();for(int i=0;i<16;i++)swan.AddRange([0xc6,0,(byte)i,(byte)(i<8?0xee:0x11)]);
  swan.AddRange([0xbc,0,0xd0,0xbc,1,7,0xbc,8,0xff,0xbc,0x10,1,0xbc,0x11,8]);byte[] ws=File(0xc0,3072000,swan);
  Check(Range(Render(ws))>100,"WonderSwan synthesizes wave RAM");Check(Render(ws).SequenceEqual(Render(ws,127)),"WonderSwan output is callback independent");
  byte[] sn=File(0x0c,0xc0000000|3579545,[0x50,0x85,0x50,0x11,0x50,0x90,0x30,0x90]);
  Check(Range(Render(sn))>100,"linked T6W28 produces sound");Check(Render(sn).SequenceEqual(Render(sn,127)),"linked T6W28 advances both cores independent of callback size");
  byte[] rom=new byte[512];rom[1]=1;rom[5]=0xff;rom[6]=0;rom[8]=0xf0;rom[10]=0xf;
  for(int i=256;i<512;i++)rom[i]=(byte)((i&16)==0?40:216);
  byte[] opl=File(0x60,33868800,[..Block(0x84,rom,512),0xd0,1,5,3,0xd0,2,0xf9,0,0xd0,2,8,0,0xd0,2,0x38,0,0xd0,2,0x50,1,0xd0,2,0x68,0x80]);
  Check(Range(Render(opl))>100,"OPL4 PCM reads generated ROM through its independent core");
  byte[] swapped=(byte[])rom.Clone();swapped[2]=0x80;
  byte[] ordered=File(0x60,33868800,[..Block(0x84,rom,512),0xd0,1,5,3,0xd0,2,0xf9,0,0xd0,2,8,0,
    ..Block(0x84,swapped[..12],512),..Block(0x87,new byte[16],16),0xd0,2,0x38,0,0xd0,2,0x50,1,0xd0,2,0x68,0x80]);
  Check(Render(opl).SequenceEqual(Render(ordered)),"queued wavetable selection precedes header replacement; OPL4 RAM upload preserves ROM");
  using(var p=new IndependentVgmWaveProvider(VgmFile.Load(opl)!)) {
   byte[] b=new byte[8192];p.Read(b,0,b.Length);var channel=p.GetSnapshot().MusicChannels.Single(c=>c.Label=="MIX");
   Check(p.SetChannelMuted(channel,true),"OPL4 MIX mute accepted");p.Read(b,0,b.Length);p.Read(b,0,b.Length);Check(Range(b)==0,"OPL4 MIX mute silences PCM");
   p.SetChannelMuted(channel,false);p.Read(b,0,b.Length);Check(Range(b)>100,"OPL4 PCM unmute resumes output");
  }
  var opm=new List<byte>();var score=new MirrorFlux.Services.Music.MxdrvCore(MdxRegression.CreateTestMdx(),null);
  score.WriteOpmCallback=(reg,value)=>opm.AddRange([0x54,(byte)reg,value]);score.Tick();
  byte[] legacyHeader=new byte[64];"Vgm "u8.CopyTo(legacyHeader);Put(legacyHeader,8,0x101);Put(legacyHeader,0x10,4000000);
  byte[] legacy=[..legacyHeader,..opm,0x61,0xff,0x7f,0x66];Put(legacy,4,(uint)legacy.Length-4);
  Check(VgmFile.Load(legacy)!.Ym2151Clock==4000000 && Range(Render(legacy))>100,"VGM 1.01 shared clock resolves YM2151 from actual commands");
  byte[] periodBytes=[0x15,1,0x20,2,0x30,1,0x10,3];
  byte[] streamSn=File(0x0c,3579545,[0x67,0x66,0,8,0,0,0,..periodBytes,0x50,0x90,0x90,0,0,0,0,0x91,0,0,1,0,0x92,0,0x11,0x2b,0,0,0x95,0,0,0,0]);
  var direct=new List<byte>{0x50,0x90};for(int i=0;i<8;i+=2){int period=periodBytes[i]|periodBytes[i+1]<<8;direct.AddRange([0x50,(byte)(0x80|(period&15)),0x50,(byte)((period>>4)&63),0x73]);}
  Check(Render(streamSn).SequenceEqual(Render(File(0x0c,3579545,direct))),"SN76489 word DAC stream is equivalent to latched direct writes");
  byte[] extra=File(0x0c,0x40000000|3579545,[0x30,0x85,0x30,0x11,0x30,0x90]);
  byte[] normal=Render(extra);Put(extra,0xbc,4);Put(extra,0xc0,8);Put(extra,0xc4,8);extra[0xcc]=1;extra[0xcd]=0;Put(extra,0xce,1789773);
  int Crossings(byte[] data){int count=0;for(int i=104;i<data.Length;i+=4)if(BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(i-4))<0 && BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(i))>=0)count++;return count;}
  Check(Math.Abs(Crossings(normal)/2.0-Crossings(Render(extra)))<=2,"extra header changes only the second chip clock");
  Put(extra,0xc4,uint.MaxValue);bool badHeader=false;try{IndependentVgmWaveProvider.Supports(VgmFile.Load(extra)!,out _);}catch(System.IO.InvalidDataException){badHeader=true;}
  Check(badHeader,"overflowing extra header offsets rejected before native initialization");
  // Extended commands must reach the corresponding bus width, including read-trigger writes.
  foreach(var item in new[]{(0xe8, new byte[]{0x41,0x1f,5}),(0xf8,new byte[]{0xc9,0,0x12,0x34})})
   Check(Render(File(item.Item1,24000000,item.Item2),frames:1024).Length==4096,"VGM 1.72 extended register dispatch");
  Check(!typeof(VgmFile).Assembly.GetManifestResourceNames().Any(n=>n.Contains("LibVgm",StringComparison.OrdinalIgnoreCase)),"application embeds no legacy engine assets");
  Check(!Process.GetCurrentProcess().Modules.Cast<ProcessModule>().Any(m=>m.ModuleName.Equals("mf_vgm.dll",StringComparison.OrdinalIgnoreCase)),"all chip tests run without loading the removed DLL");
 }
}
