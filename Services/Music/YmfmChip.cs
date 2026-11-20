using System.Runtime.InteropServices;

namespace MirrorFlux.Services.Music;

/// <summary>
/// BSD ライセンスの ymfm を自前の C ABI で包んだ音源。
/// libvgm は読み込まない。
/// </summary>
internal sealed class YmfmChip : IDisposable
{
    /// <summary>mf_ymfm.dll の関数を結び付ける。</summary>
    private static class Api
    {
        private static readonly IntPtr Library = NativeAssetLibrary.Load("Ymfm", "mf_ymfm.dll");
        private static T Bind<T>(string name) where T : Delegate =>
            Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(Library, "mf_ymfm_chip_" + name));
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate IntPtr CreateFn(uint type, uint variant, uint clock, uint rate);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void CloseFn(IntPtr chip);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int WriteFn(IntPtr chip, byte port, byte register, byte value);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void RenderFn(IntPtr chip, [Out] int[] output, uint frames);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void MixFn(IntPtr chip, double fm, double ssg);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void MaskFn(IntPtr chip, uint mask);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int MemoryFn(IntPtr chip, uint bank, uint size, uint offset, byte[] data, uint length);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate uint PhaseStepsFn(IntPtr chip, [Out] uint[] steps, uint count);
        internal static readonly CreateFn Create = Bind<CreateFn>("create");
        internal static readonly CloseFn Close = Bind<CloseFn>("close");
        internal static readonly WriteFn Write = Bind<WriteFn>("write");
        internal static readonly RenderFn Render = Bind<RenderFn>("render");
        internal static readonly MixFn Mix = Bind<MixFn>("mix");
        internal static readonly MaskFn Mask = Bind<MaskFn>("mask");
        internal static readonly MemoryFn Memory = Bind<MemoryFn>("memory");
        internal static readonly PhaseStepsFn PhaseSteps = Bind<PhaseStepsFn>("phase_steps");
    }
    private IntPtr _handle;
    internal uint Mask { get; private set; }
    /// <summary>
    /// 指定の種類とクロックで音源を作る。
    /// クロックの最上位ビットは種類の変種を表す。
    /// </summary>
    internal YmfmChip(int type, uint clock)
    {
        _handle = Api.Create((uint)type, clock >> 31, clock & 0x3fffffff, 44100);
        if (_handle == IntPtr.Zero) throw new InvalidOperationException("ymfm音源を初期化できません。");
        // 単体の音源で鳴らした音を既存の出力と比べて決めた FM と SSG の混合比。旧プレーヤーの正規化コードを写したものではなく、このアプリの方針。
        Api.Mix(_handle, type == 1 ? 4 : type is 2 or 3 or 6 or 7 or 8 ? 2 : 1, type == 6 ? .5 : .75);
    }
    /// <summary>FM と SSG の音量を、種類ごとの混合比に掛けて設定する。</summary>
    internal void Mix(int type, double volume, double ssgVolume) => Api.Mix(_handle, (type == 1 ? 4 : type is 2 or 3 or 6 or 7 or 8 ? 2 : 1) * volume, (type == 6 ? .5 : .75) * ssgVolume);
    /// <summary>
    /// レジスタに書き込む。
    /// 書き込み待ちがあふれたら例外にする。
    /// </summary>
    internal void Write(byte port, byte register, byte value)
    {
        if (Api.Write(_handle, port, register, value) != 0) throw new InvalidOperationException("ymfm書込みキューの上限です。");
    }
    internal void Render(int[] output, int frames) => Api.Render(_handle, output, (uint)frames);
    internal uint PhaseSteps(uint[] steps) => _handle != IntPtr.Zero ? Api.PhaseSteps(_handle, steps, (uint)steps.Length) : 0;
    internal void SetMask(uint mask) { Mask = mask; Api.Mask(_handle, mask); }
    /// <summary>ADPCM などのサンプルメモリーを設定する。</summary>
    internal void Memory(uint bank, uint size, uint offset, byte[] data)
    {
        if (Api.Memory(_handle, bank, size, offset, data, (uint)data.Length) != 0)
            throw new InvalidOperationException("ymfmサンプルメモリーを設定できません。");
    }
    public void Dispose()
    {
        if (_handle != IntPtr.Zero) { Api.Close(_handle); _handle = IntPtr.Zero; }
    }
}
