using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MirrorFlux.Services;

/// <summary>
/// 同梱の mf_rar.dll の関数を呼ぶための宣言。書庫を開き、項目を順に進め、中身を読む。
/// </summary>
internal static class RarNative
{
    private static readonly IntPtr Library = NativeAssetLibrary.Load("Rar", "mf_rar.dll");
    private static T Load<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(Library, name));
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr OpenDelegate(IntPtr paths, int rar5);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void CloseDelegate(IntPtr handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int IntDelegate(Handle handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate long LongDelegate(Handle handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate IntPtr StringDelegate(Handle handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int ReadDelegate(Handle handle, IntPtr buffer, int count);
    private static readonly OpenDelegate OpenRaw = Load<OpenDelegate>("mf_rar_open");
    private static readonly CloseDelegate Close = Load<CloseDelegate>("mf_rar_close");
    internal static readonly IntDelegate Next = Load<IntDelegate>("mf_rar_next");
    internal static readonly IntDelegate IsDirectory = Load<IntDelegate>("mf_rar_directory");
    internal static readonly LongDelegate Size = Load<LongDelegate>("mf_rar_size");
    internal static readonly LongDelegate Time = Load<LongDelegate>("mf_rar_time");
    internal static readonly StringDelegate Name = Load<StringDelegate>("mf_rar_name");
    internal static readonly StringDelegate Link = Load<StringDelegate>("mf_rar_link");
    internal static readonly StringDelegate Error = Load<StringDelegate>("mf_rar_error");
    internal static readonly ReadDelegate Read = Load<ReadDelegate>("mf_rar_read");
    /// <summary>
    /// 開いた書庫のハンドル。破棄すると DLL 側で閉じる。
    /// </summary>
    internal sealed class Handle : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal Handle(IntPtr pointer) : base(true) => SetHandle(pointer);
        protected override bool ReleaseHandle() { RarNative.Close(handle); return true; }
    }
    /// <summary>
    /// ボリュームのパスの一覧で書庫を開く。開けなければ DLL のエラー文で例外にする。
    /// </summary>
    internal static Handle Open(IReadOnlyList<string> paths, bool rar5)
    {
        // パスを UTF-16 の文字列にし、末尾を null にしたポインターの配列として渡す。
        IntPtr list = Marshal.AllocHGlobal(checked((paths.Count + 1) * IntPtr.Size));
        var pointers = new List<IntPtr>();
        try
        {
            for (int i = 0; i < paths.Count; i++)
            {
                IntPtr pointer = Marshal.StringToHGlobalUni(paths[i]); pointers.Add(pointer);
                Marshal.WriteIntPtr(list, i * IntPtr.Size, pointer);
            }
            Marshal.WriteIntPtr(list, paths.Count * IntPtr.Size, IntPtr.Zero);
            // 開いた直後のエラーを確かめる。
            var handle = new Handle(OpenRaw(list, rar5 ? 1 : 0));
            if (handle.IsInvalid) { handle.Dispose(); throw new OutOfMemoryException(); }
            string error = Marshal.PtrToStringUTF8(Error(handle)) ?? "";
            if (error.Length != 0) { handle.Dispose(); throw new InvalidDataException(error); }
            return handle;
        }
        finally { foreach (IntPtr pointer in pointers) Marshal.FreeHGlobal(pointer); Marshal.FreeHGlobal(list); }
    }
    internal static InvalidDataException Failure(Handle handle) => new("RAR: " + Marshal.PtrToStringUTF8(Error(handle)));
}
