using System.Buffers.Binary;
using MirrorFlux.Services.Vgm;
internal static class StandaloneChipRegression
{
    internal static void Run()
    {
        int count=0;
        void Check(bool ok,string label){if(!ok)throw new Exception(label);Console.WriteLine("PASS standalone: "+label);count++;}
        int[] fields=[0x0c,0x10,0x2c,0x30,0x38,0x40,0x44,0x48,0x4c,0x50,0x54,0x58,0x5c,0x60,0x64,0x68,0x6c,0x70,0x74,0x80,0x84,0x88,0x8c,0x90,0x98,0x9c,0xa0,0xa4,0xa8,0xac,0xb0,0xb4,0xb8,0xc0,0xc4,0xc8,0xcc,0xd0,0xd8,0xdc,0xe0,0xe4,0xe8,0xec,0xf0,0xf4,0xf8,0xfc];
        foreach(int field in fields) {
            byte[] data=new byte[260];"Vgm "u8.CopyTo(data);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8),0x172);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x34),256-0x34);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(field),3579545);data[0xd6]=72;
            data[256]=0x61;data[257]=0xff;data[258]=0x7f;data[259]=0x66;
            using var player=new IndependentVgmWaveProvider(VgmFile.Load(data)!);
            byte[] output=new byte[1024];Check(player.Read(output,0,output.Length)==output.Length,$"chip {Array.IndexOf(fields,field):X2} creates/renders");
        }
        for(int type=0;type<48;type++)VgmChipRegression.AuditRoute(type);
        VgmIntegrationRegression.Run();
        using var rom=new SyntheticRhythmRom();
        VgmAudioRegression.Run(Check);
        VgmChipRegression.Run();
        Console.WriteLine($"Standalone chip checks: {count}");
    }
}
