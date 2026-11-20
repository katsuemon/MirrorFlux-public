using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace MirrorFlux.Services;

/// <summary>
/// EXE に埋め込んだネイティブ DLL を一時フォルダーへ書き出して読み込む。アプリのフォルダーに同名の DLL があればそちらを使う。
/// </summary>
internal static class NativeAssetLibrary
{
    /// <summary>
    /// アプリのフォルダーを返す。dotnet コマンドから起動したときは BaseDirectory を使う。
    /// </summary>
    internal static string ResolveApplicationDirectory(string? processPath, string baseDirectory) =>
        string.IsNullOrEmpty(processPath) || Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? baseDirectory : Path.GetDirectoryName(processPath) ?? baseDirectory;

    /// <summary>
    /// group の埋め込みリソースを書き出し、依存 DLL を先に読み込んでから library を読み込む。
    /// </summary>
    internal static IntPtr Load(string group, string library, params string[] dependencies)
    {
        // 埋め込みリソースの名前と中身からハッシュを作り、版ごとに別の書き出し先にする。
        var assembly = typeof(NativeAssetLibrary).Assembly;
        string prefix = "MirrorFlux.Assets." + group + ".";
        string[] resources = assembly.GetManifestResourceNames().Where(n => n.StartsWith(prefix, StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal).ToArray();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string resource in resources)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(resource + "\0"));
            using var input = assembly.GetManifestResourceStream(resource)!;
            byte[] buffer = new byte[81920]; int read;
            while ((read = input.Read(buffer)) != 0) hash.AppendData(buffer.AsSpan(0, read));
        }
        string directory = Path.Combine(Path.GetTempPath(), "MirrorFlux", group.ToLowerInvariant(), Convert.ToHexString(hash.GetHashAndReset()));
        Directory.CreateDirectory(directory);
        // まだないファイルだけ、別名で書いてから置く。ほかのプロセスが先に置いていればそれを使う。
        foreach (string resource in resources)
        {
            string path = Path.Combine(directory, resource[prefix.Length..]);
            if (File.Exists(path)) continue;
            string temporary = path + "." + Guid.NewGuid().ToString("N");
            try
            {
                using (var input = assembly.GetManifestResourceStream(resource)!)
                using (var output = File.Create(temporary)) input.CopyTo(output);
                try { File.Move(temporary, path); }
                catch (IOException) when (File.Exists(path)) { }
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        // アプリのフォルダーにある DLL を優先して読み込む。
        string app = ResolveApplicationDirectory(Environment.ProcessPath, AppContext.BaseDirectory);
        string Select(string file) => File.Exists(Path.Combine(app, file)) ? Path.Combine(app, file) : Path.Combine(directory, file);
        foreach (string dependency in dependencies) NativeLibrary.Load(Select(dependency));
        return NativeLibrary.Load(Select(library));
    }
}
