using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ImageMagick;

namespace MirrorFlux.Services;

/// <summary>
/// ImageMagick のネイティブ DLL を、アプリが用意した場所から読み込ませる。
/// </summary>
internal static class ImageNativeLibrary
{
    private static readonly Lazy<IntPtr> Library = new(() => NativeAssetLibrary.Load("Image", "Magick.Native-Q8-x64.dll"));

    /// <summary>
    /// Magick のアセンブリに DLL の解決処理を登録する。アプリの GME 用の解決処理とは別に登録する。
    /// 単一ファイル配布では BaseDirectory が展開キャッシュの場所になることがあるため、場所を自前で決める。
    /// </summary>
    [ModuleInitializer]
    internal static void Initialize() => NativeLibrary.SetDllImportResolver(typeof(MagickImage).Assembly, Resolve);

    /// <summary>
    /// Magick.Native の DLL だけを自前で読み込み、それ以外は既定の探し方に任せる。
    /// </summary>
    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? search)
    {
        if (name != "Magick.Native-Q8-x64.dll") return IntPtr.Zero;
        return Library.Value;
    }
}
