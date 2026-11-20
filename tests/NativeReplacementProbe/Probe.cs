using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
public class StartupHook
{
    public static void Initialize()
    {
        string report = Environment.GetEnvironmentVariable("MF_NATIVE_PROBE_REPORT")!;
        try
        {
            var assembly = Assembly.Load("MirrorFlux.App");
            var loader = assembly.GetType("MirrorFlux.Services.NativeAssetLibrary", true)!;
            var load = loader.GetMethod("Load", BindingFlags.NonPublic | BindingFlags.Static)!;
            foreach (var pair in new[] { ("Gme","gme.dll"), ("Rar","mf_rar.dll"), ("Ymfm","mf_ymfm.dll"), ("Chips","mf_chips.dll"), ("Lha","mf_lha.dll") })
                load.Invoke(null, new object[] { pair.Item1, pair.Item2, Array.Empty<string>() });
            var magick = Assembly.Load("Magick.NET-Q8-x64");
            var magickType = magick.GetType("ImageMagick.MagickNET", true)!;
            var version = magickType.GetProperty("Version")!.GetValue(null);
            var delegates = magickType.GetProperty("Delegates")!.GetValue(null);
            var names = new[] { "gme.dll", "mf_rar.dll", "mf_ymfm.dll", "mf_chips.dll", "mf_lha.dll", "Magick.Native-Q8-x64.dll" };
            var modules = Process.GetCurrentProcess().Modules.Cast<ProcessModule>().Where(m => names.Contains(m.ModuleName, StringComparer.OrdinalIgnoreCase)).Select(m => m.FileName).ToArray();
            string root = Path.GetDirectoryName(Environment.ProcessPath)!;
            var imageHash = Environment.GetEnvironmentVariable("MF_NATIVE_PROBE_IMAGE_HASH");
            bool ok = names.All(name => modules.Any(path => {
                if (imageHash != null && name == "Magick.Native-Q8-x64.dll")
                    return Path.GetFileName(path).Equals(name, StringComparison.OrdinalIgnoreCase)
                        && !Path.GetDirectoryName(path)!.Equals(root, StringComparison.OrdinalIgnoreCase)
                        && Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).Equals(imageHash, StringComparison.OrdinalIgnoreCase);
                return string.Equals(path, Path.Combine(root,name), StringComparison.OrdinalIgnoreCase);
            }));
            File.WriteAllText(report, JsonSerializer.Serialize(new { success=ok, magickVersion=version, embeddedImageVerified=imageHash != null && ok, modules }, new JsonSerializerOptions { WriteIndented=true }));
            Environment.Exit(ok ? 0 : 1);
        }
        catch(Exception ex) { File.WriteAllText(report,ex.ToString()); Environment.Exit(1); }
    }
}
