using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace MirrorFlux.Services.Music;

/// <summary>
/// Game Music Emu（gme.dll）の呼び出し口。
/// DLL は NativeAssetLibrary で外部データから読み込む。
/// </summary>
internal static class GmeNative
{
    static GmeNative() => NativeLibrary.SetDllImportResolver(typeof(GmeNative).Assembly, Resolve);
    /// <summary>"gme" の読み込み先を、外部データから読み込んだ DLL に向ける。</summary>
    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? search)
    {
        if (name != "gme") return IntPtr.Zero;
        return Library.Value;
    }
    private static readonly Lazy<IntPtr> Library = new(() => NativeAssetLibrary.Load("Gme", "gme.dll"));
    /// <summary>gme の戻り値がエラー文字列なら例外にする。</summary>
    public static void Check(IntPtr error) { if (error != IntPtr.Zero) throw new InvalidDataException(Marshal.PtrToStringAnsi(error)); }
    [DllImport("gme", CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr gme_open_data(byte[] data, int size, out IntPtr emu, int rate);
    [DllImport("gme", CallingConvention = CallingConvention.Cdecl)] internal static extern void gme_delete(IntPtr emu);
    [DllImport("gme", CallingConvention = CallingConvention.Cdecl)] internal static extern int gme_track_count(IntPtr emu);
    [DllImport("gme", CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr gme_start_track(IntPtr emu, int track);
    [DllImport("gme", CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr gme_play(IntPtr emu, int count, [Out] short[] samples);
    [DllImport("gme", CallingConvention = CallingConvention.Cdecl)] internal static extern int gme_voice_count(IntPtr emu);
    [DllImport("gme", CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr gme_voice_name(IntPtr emu, int index);
    [DllImport("gme", CallingConvention = CallingConvention.Cdecl)] internal static extern void gme_mute_voices(IntPtr emu, int mask);
    [DllImport("gme", CallingConvention = CallingConvention.Cdecl)] internal static extern void gme_ignore_silence(IntPtr emu, int ignore);
    [DllImport("gme", CallingConvention = CallingConvention.Cdecl)] internal static extern void gme_set_fade(IntPtr emu, int startMilliseconds);
    [DllImport("gme", CallingConvention = CallingConvention.Cdecl)] internal static extern int gme_track_ended(IntPtr emu);
    [DllImport("gme", CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr gme_warning(IntPtr emu);
}
