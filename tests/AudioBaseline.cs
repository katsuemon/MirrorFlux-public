using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
// Captured 2026-11-11 after comparing generated signals with the previous engine.
// Only measurements and hashes are stored: no music, ROM or waveform payload.
internal static class AudioBaseline {
 public record Entry(string Hash,double Rms,int Crossings);
 static readonly Dictionary<string,Entry> Data=JsonSerializer.Deserialize<Dictionary<string,Entry>>(
  File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Fixtures","vgm-audio-baselines.json")))!;
 static string Key(byte[] input,int frames,string route)=>"vgm:"+Convert.ToHexString(SHA256.HashData(input))+":"+(frames*4)+":"+route;
 internal static Entry Reference(byte[] input,int frames=16000)=>Data[Key(input,frames,"legacy")];
 internal static double Mix(byte[] input)=>Data["mix:"+Convert.ToHexString(SHA256.HashData(input))].Rms;
 internal static bool Verify(byte[] input,byte[] output) {
  var expected=Data[Key(input,output.Length/4,"independent")];
  if(expected.Hash!=Convert.ToHexString(SHA256.HashData(output)))throw new Exception("Generated VGM audio differs from the validated baseline: "+Key(input,output.Length/4,"independent"));
  return true;
 }
}
