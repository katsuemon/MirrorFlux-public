using System.Runtime.InteropServices;

namespace MirrorFlux.Services.Music;

/// <summary>
/// 単独で使う BSD ライセンスの YM2151（ymfm）の呼び出し口。
/// VGM プレイヤーとは独立している。
/// </summary>
internal static class YmfmNative
{
    private static readonly IntPtr Library = NativeAssetLibrary.Load("Ymfm", "mf_ymfm.dll");
    private static T Bind<T>(string name) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(Library, name));
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate IntPtr CreateDelegate(uint clock, uint rate);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void CloseDelegate(IntPtr chip);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int WriteDelegate(IntPtr chip, byte register, byte value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void RenderDelegate(IntPtr chip, [Out] int[] output, uint frames);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void PhaseStepsDelegate(IntPtr chip, [Out] uint[] steps);
    internal static readonly CreateDelegate Create = Bind<CreateDelegate>("mf_ymfm_opm_create");
    internal static readonly CloseDelegate Close = Bind<CloseDelegate>("mf_ymfm_opm_close");
    private static readonly WriteDelegate WriteNative = Bind<WriteDelegate>("mf_ymfm_opm_write");
    internal static readonly RenderDelegate Render = Bind<RenderDelegate>("mf_ymfm_opm_render");
    internal static readonly PhaseStepsDelegate PhaseSteps = Bind<PhaseStepsDelegate>("mf_ymfm_opm_phase_steps");
    /// <summary>
    /// レジスタに書き込む。
    /// 書き込み待ちがあふれたら例外にする。
    /// </summary>
    internal static void Write(IntPtr chip, byte register, byte value)
    {
        if (WriteNative(chip, register, value) != 0) throw new OutOfMemoryException("YM2151 register queue is full.");
    }
}
