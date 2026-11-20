using System.IO;
using System.IO.Compression;
using MirrorFlux.Models;

internal static class PublicAssetsRegression
{
    public static void Run(Action<bool, string> check)
    {
        var assembly = typeof(AppSettings).Assembly;
        string[] media = [".rom", ".wav", ".pdx", ".mdx", ".nsf", ".spc", ".vgm", ".vgz"];
        var names = assembly.GetManifestResourceNames();
        check(!assembly.GetReferencedAssemblies().Any(a => a.Name is "MDSound" || a.Name!.StartsWith("Amiga.FileFormats", StringComparison.Ordinal)),
            "Application no longer references MDSound/fmgen or Amiga.FileFormats");
        check(MirrorFlux.Services.NativeAssetLibrary.ResolveApplicationDirectory(@"C:\Apps\mf.exe", @"C:\Temp\extracted") == @"C:\Apps",
            "Native replacements are loaded beside original self-extracting EXE");
        check(MirrorFlux.Services.NativeAssetLibrary.ResolveApplicationDirectory(@"C:\dotnet\dotnet.exe", @"C:\Tests") == @"C:\Tests",
            "Dotnet-hosted native replacements use the application directory");
        check(!names.Any(n => media.Any(ext => n.EndsWith(ext, StringComparison.OrdinalIgnoreCase))),
            "Public assembly has no standalone ROM, waveform or music resources");
        foreach (string name in names.Where(n => n.EndsWith(".source.zip", StringComparison.OrdinalIgnoreCase)))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            check(!zip.Entries.Any(e => media.Contains(Path.GetExtension(e.FullName).ToLowerInvariant()) ||
                e.Name.Equals("fmopn_2608rom.h", StringComparison.OrdinalIgnoreCase)),
                name + " excludes sample media and the unused embedded ROM header");
        }
    }
}
