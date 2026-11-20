using System.Runtime.InteropServices;
namespace MirrorFlux.Services.Music;

/// <summary>
/// VGM のスケジューラーが使う共通の音源の入口。
/// 時刻の管理はマネージド側で行う。
/// ymfm で鳴らす音源と、mf_chips.dll の音源を同じ形で扱う。
/// </summary>
internal sealed class VgmChip : IDisposable
{
    private readonly YmfmChip? _ymfm;
    private IntPtr _native;
    private readonly bool _variant;
    [StructLayout(LayoutKind.Sequential)]
    /// <summary>音源が報告する1音分の状態（種類・番号・チャンネル・フラグ・周波数・ノート・レベル・左右）。</summary>
    internal struct Voice { public uint Type, Instance, Channel, Flags; public double Frequency; public int Note; public float Level, Left, Right; }
    internal uint Mask { get; private set; }
    internal int Type { get; }
    internal uint Clock { get; }
    /// <summary>mf_chips.dll の関数を結び付ける。</summary>
    private static class Api
    {
        private static readonly IntPtr Library = NativeAssetLibrary.Load("Chips", "mf_chips.dll");
        private static T Bind<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(Library, "mf_chip_" + name));
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate IntPtr CreateFn(uint type, uint clock, uint rate, byte[] header, uint size);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void CloseFn(IntPtr chip);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int WriteFn(IntPtr chip, uint port, uint address, uint value);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void RenderFn(IntPtr chip, [Out] int[] output, uint frames);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void MaskFn(IntPtr chip, uint mask);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int MemoryFn(IntPtr chip, uint bank, uint size, uint offset, byte[] bytes, uint length);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int VoicesFn(IntPtr chip, uint instance, [Out] Voice[] output, int capacity);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate double WaveFn(IntPtr chip, int channel, [Out] double[] output, int capacity);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int ChannelFn(IntPtr chip, int channel, int muted);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void LinkFn(IntPtr tone, IntPtr noise);
        internal static readonly LinkFn Link = Bind<LinkFn>("link");
        internal static readonly VoicesFn Voices = Bind<VoicesFn>("voices");
        internal static readonly WaveFn Wave = Bind<WaveFn>("wave");
        internal static readonly ChannelFn Channel = Bind<ChannelFn>("channel");
        internal static readonly CreateFn Create = Bind<CreateFn>("create");
        internal static readonly CloseFn Close = Bind<CloseFn>("close");
        internal static readonly WriteFn Write = Bind<WriteFn>("write");
        internal static readonly RenderFn Render = Bind<RenderFn>("render");
        internal static readonly MaskFn Mask = Bind<MaskFn>("mask");
        internal static readonly MemoryFn Memory = Bind<MemoryFn>("memory");
    }
    /// <summary>
    /// 音源を作る。
    /// FM 系は ymfm、それ以外は mf_chips.dll で作る。
    /// クロックの最上位ビットは変種を表す。
    /// </summary>
    internal VgmChip(int type, uint clock, byte[] header)
    {
        Type = type; Clock = clock & 0x3fffffff; _variant = (clock & 0x80000000) != 0;
        if (type is 1 or 2 or 3 or >= 6 and <= 13) _ymfm = new YmfmChip(type, clock);
        else
        {
            // MultiPCM（種類28）でヘッダーが指定すれば別のコアを使う。
            int core = type == 28 && header.Length > 0x96 && header[0x96] == 2 ? 48 : type;
            // VGM の MultiPCM のクロックは昔からの /180 のレートを前提にしている。単独の音源コアは /224 を前提にしていて、以前の VGM 再生アダプターも同じだったので換算する。
            uint nativeClock = type == 21
                ? (uint)((ulong)(clock & 0x3fffffff) * 224 / 180) | (clock & 0xc0000000)
                : clock;
            _native = Api.Create((uint)core, nativeClock, 44100, header, (uint)header.Length);
            if (_native == IntPtr.Zero) throw new InvalidOperationException($"音源 {type:X2} を初期化できません。");
        }
    }
    /// <summary>
    /// レジスタに書き込む。
    /// MultiPCM のバンク指定（ポート257）はレジスタ書き込みに置き換える。
    /// </summary>
    internal void Write(uint port, uint address, uint value)
    {
        if (_ymfm != null) { _ymfm.Write((byte)port, (byte)address, (byte)value); return; }
        if (Type == 21 && port == 257)
        {
            // C3 は VGM のバンク指定コマンドで、同じ値のアドレスの組ではない。1 MB モードではレジスタ 0x10 が続きの 512 KB 2つを割り当てる。
            int mask = (int)address & 3;
            byte bank = (byte)value;
            if (mask == 3 && (bank & 8) == 0)
                Write(0, 0x10, (uint)(bank / 16));
            else
            {
                if ((mask & 2) != 0) Write(0, 0x11, (uint)(bank / 8));
                if ((mask & 1) != 0) Write(0, 0x12, (uint)(bank / 8));
            }
            return;
        }
        if (Api.Write(_native, port, address, value) != 0) throw new InvalidOperationException($"音源 {Type:X2} への書込みが不正です。");
    }
    /// <summary>ROM や RAM のサンプルメモリーを設定する。</summary>
    internal void Memory(uint bank, uint size, uint offset, byte[] bytes)
    {
        if (_ymfm != null) _ymfm.Memory(bank, size, offset, bytes);
        else if (Api.Memory(_native, bank, size, offset, bytes, (uint)bytes.Length) != 0) throw new InvalidOperationException($"音源 {Type:X2} のメモリー範囲が不正です。");
    }
    internal void Link(VgmChip noise) => Api.Link(_native, noise._native);
    internal void Mix(double volume, double ssgVolume) { _ymfm?.Mix(Type, volume, ssgVolume); }
    internal void Render(int[] output, int frames) { if (_ymfm != null) _ymfm.Render(output, frames); else Api.Render(_native, output, (uint)frames); }
    internal uint FmPhaseSteps(uint[] steps) => _ymfm?.PhaseSteps(steps) ?? 0;
    internal void SetMask(uint mask) { Mask = mask; if (_ymfm != null) _ymfm.SetMask(mask); else Api.Mask(_native, mask); }
    /// <summary>
    /// 音源の各音の状態を読む。
    /// 変種の音源（種類37・44）には印のフラグを足す。
    /// </summary>
    internal int Voices(int instance, Voice[] output)
    {
        if (_native == IntPtr.Zero) return 0;
        int count = Api.Voices(_native, (uint)instance, output, output.Length);
        if (_variant && Type is 37 or 44) for (int i = 0; i < count; i++) output[i].Flags |= 16;
        return count;
    }
    internal double Wave(int channel, double[] output) => _native == IntPtr.Zero ? 0 : Api.Wave(_native, channel, output, output.Length);
    /// <summary>
    /// チャンネルの消音を切り替える。
    /// 種類14は音源側の関数で、それ以外はマスクのビットで切り替える。
    /// </summary>
    internal bool SetChannel(int channel, bool muted)
    {
        if (Type == 14) return Api.Channel(_native, channel, muted ? 1 : 0) != 0;
        if (channel is < 0 or > 31) return false;
        uint bit = 1u << channel; SetMask(muted ? Mask | bit : Mask & ~bit); return true;
    }
    public void Dispose() { _ymfm?.Dispose(); if (_native != IntPtr.Zero) { Api.Close(_native); _native = IntPtr.Zero; } }
}
