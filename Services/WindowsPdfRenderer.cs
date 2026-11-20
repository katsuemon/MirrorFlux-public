using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace MirrorFlux.Services;

/// <summary>
/// Windows 10/11 に標準で入っている Windows.Data.Pdf（WinRT の API）で PDF を開いて描画する。
/// 外部ライブラリや大きな DLL を足さず、OS 標準の描画エンジンでページを描く。WinRT の関数は COM の vtable の番号で直接呼ぶ。
/// </summary>
public sealed class WindowsPdfDocument : IDisposable
{
    private static Guid IID_IRandomAccessStream = new("905A0FE1-BC53-11DF-8C49-001E4FC686DA");
    private static Guid IID_IPdfDocumentStatics = new("433A0B5F-C007-4788-90F2-08143D922599");
    private static Guid IID_IPdfDocument = new("AC7EBEDD-80FA-4089-846E-81B77FF5A86C");
    private static Guid IID_IPdfPage = new("9DB4B0C8-5320-4CFC-AD76-493FDAD0E594");
    private static Guid IID_IPdfPageRenderOptions = new("3C98056F-B7CF-4C29-9A04-52D90267F425");
    private static Guid IID_IAsyncInfo = new("00000036-0000-0000-C000-000000000046");
    // WIC の BMP エンコーダー ID。無圧縮の BMP で出力し、PNG の zlib 圧縮にかかる時間を省く。
    private static readonly Guid WicBmpEncoderId = new("69BE8BB4-D66D-47C8-865A-ED1589433782");

    [DllImport("combase.dll", PreserveSig = true)]
    private static extern int RoInitialize(int initType);

    [DllImport("combase.dll", PreserveSig = true, CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string sourceString, int length, out IntPtr hstring);

    [DllImport("combase.dll", PreserveSig = true)]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll", PreserveSig = true)]
    private static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);

    [DllImport("combase.dll", PreserveSig = true)]
    private static extern int RoActivateInstance(IntPtr activatableClassId, out IntPtr instance);

    [DllImport("shcore.dll", PreserveSig = true, CharSet = CharSet.Unicode)]
    private static extern int CreateRandomAccessStreamOnFile(string filePath, uint accessMode, ref Guid riid, out IntPtr ppv);

    [DllImport("shcore.dll", PreserveSig = true)]
    private static extern int CreateRandomAccessStreamOverStream(IntPtr stream, uint options, ref Guid riid, out IntPtr ppv);

    [DllImport("ole32.dll", PreserveSig = true)]
    private static extern int CreateStreamOnHGlobal(IntPtr hGlobal, [MarshalAs(UnmanagedType.Bool)] bool fDeleteOnRelease, out IntPtr ppstm);

    [DllImport("ole32.dll", PreserveSig = true)]
    private static extern int GetHGlobalFromStream(IntPtr pstm, out IntPtr phglobal);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern UIntPtr GlobalSize(IntPtr hMem);

    /// <summary>
    /// ページの大きさ（DIP）。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct PdfPageSize
    {
        public float Width;
        public float Height;
    }

    /// <summary>
    /// WinRT の Windows.UI.Color。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct WinRtColor
    {
        public byte A;
        public byte R;
        public byte G;
        public byte B;
    }

    private IntPtr _pdfDoc;
    private IntPtr _inputStream;
    private bool _disposed;

    public uint PageCount { get; }
    public bool IsPasswordProtected { get; }

    private WindowsPdfDocument(IntPtr pdfDoc, IntPtr inputStream, uint pageCount, bool isPasswordProtected)
    {
        _pdfDoc = pdfDoc;
        _inputStream = inputStream;
        PageCount = pageCount;
        IsPasswordProtected = isPasswordProtected;
    }

    /// <summary>
    /// PDF ファイルを開き、ページ数とパスワード保護の有無を読む。開けなければ null を返す。
    /// </summary>
    public static async Task<WindowsPdfDocument?> OpenAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return null;
        }

        return await Task.Run(() =>
        {
            // 作業スレッドで WinRT をマルチスレッド（RO_INIT_MULTITHREADED）として初期化する。
            RoInitialize(1);

            IntPtr inputStream = IntPtr.Zero;
            IntPtr factory = IntPtr.Zero;
            IntPtr asyncOp = IntPtr.Zero;
            IntPtr pdfDoc = IntPtr.Zero;
            IntPtr hClassId = IntPtr.Zero;

            try
            {
                // ファイルを読み取り専用（0）のストリームとして開く。
                int hr = CreateRandomAccessStreamOnFile(Path.GetFullPath(filePath), 0, ref IID_IRandomAccessStream, out inputStream);
                if (hr < 0 || inputStream == IntPtr.Zero)
                {
                    return null;
                }

                // PdfDocument のファクトリーを取る。
                const string className = "Windows.Data.Pdf.PdfDocument";
                hr = WindowsCreateString(className, className.Length, out hClassId);
                if (hr < 0) return null;

                hr = RoGetActivationFactory(hClassId, ref IID_IPdfDocumentStatics, out factory);
                if (hr < 0 || factory == IntPtr.Zero) return null;

                // IPdfDocumentStatics::LoadFromStreamAsync（vtable の8番）で読み込みを始め、終わるまで待つ。
                unsafe
                {
                    var vtbl = *(IntPtr**)factory;
                    var loadFromStreamAsync = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, out IntPtr, int>)vtbl[8];
                    hr = loadFromStreamAsync(factory, inputStream, out asyncOp);
                    if (hr < 0 || asyncOp == IntPtr.Zero) return null;
                }

                if (!WaitForAsyncCompletion(asyncOp, cancellationToken))
                {
                    return null;
                }

                // IAsyncOperation<PdfDocument>::GetResults（vtable の8番）で結果を取り、IPdfDocument にする。
                IntPtr docUnk;
                unsafe
                {
                    var vtbl = *(IntPtr**)asyncOp;
                    var getResults = (delegate* unmanaged[Stdcall]<IntPtr, out IntPtr, int>)vtbl[8];
                    hr = getResults(asyncOp, out docUnk);
                    if (hr < 0 || docUnk == IntPtr.Zero) return null;
                }

                hr = Marshal.QueryInterface(docUnk, in IID_IPdfDocument, out pdfDoc);
                Marshal.Release(docUnk);
                if (hr < 0 || pdfDoc == IntPtr.Zero) return null;

                // ページ数（7番）とパスワード保護の有無（8番）を読む。
                uint pageCount;
                byte isPasswordProtected;
                unsafe
                {
                    var vtbl = *(IntPtr**)pdfDoc;
                    var getPageCount = (delegate* unmanaged[Stdcall]<IntPtr, out uint, int>)vtbl[7];
                    var getIsPwd = (delegate* unmanaged[Stdcall]<IntPtr, out byte, int>)vtbl[8];
                    if (getPageCount(pdfDoc, out pageCount) < 0) return null;
                    getIsPwd(pdfDoc, out isPasswordProtected);
                }

                // 文書とストリームの所有を新しいオブジェクトへ移し、下の後片付けで解放しないようにする。
                var result = new WindowsPdfDocument(pdfDoc, inputStream, pageCount, isPasswordProtected != 0);
                pdfDoc = IntPtr.Zero;
                inputStream = IntPtr.Zero;
                return result;
            }
            finally
            {
                // 途中で取った COM オブジェクトと文字列を解放する。
                if (asyncOp != IntPtr.Zero) Marshal.Release(asyncOp);
                if (factory != IntPtr.Zero) Marshal.Release(factory);
                if (hClassId != IntPtr.Zero) WindowsDeleteString(hClassId);
                if (pdfDoc != IntPtr.Zero) Marshal.Release(pdfDoc);
                if (inputStream != IntPtr.Zero) Marshal.Release(inputStream);
            }
        }, cancellationToken);
    }

    /// <summary>
    /// ページの大きさを返す。取れなければ US レターの 612x792 を返す。
    /// </summary>
    public PdfPageSize GetPageSize(uint pageIndex)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (pageIndex >= PageCount) return new PdfPageSize { Width = 612, Height = 792 };

        IntPtr pageUnk = IntPtr.Zero;
        IntPtr pdfPage = IntPtr.Zero;
        try
        {
            // IPdfDocument::GetPage（6番）でページを取り、IPdfPage の Size（10番）を読む。
            unsafe
            {
                var docVtbl = *(IntPtr**)_pdfDoc;
                var getPage = (delegate* unmanaged[Stdcall]<IntPtr, uint, out IntPtr, int>)docVtbl[6];
                if (getPage(_pdfDoc, pageIndex, out pageUnk) < 0 || pageUnk == IntPtr.Zero)
                {
                    return new PdfPageSize { Width = 612, Height = 792 };
                }

                if (Marshal.QueryInterface(pageUnk, in IID_IPdfPage, out pdfPage) < 0 || pdfPage == IntPtr.Zero)
                {
                    return new PdfPageSize { Width = 612, Height = 792 };
                }

                var pageVtbl = *(IntPtr**)pdfPage;
                var getSize = (delegate* unmanaged[Stdcall]<IntPtr, out PdfPageSize, int>)pageVtbl[10];
                if (getSize(pdfPage, out var size) >= 0 && size.Width > 0 && size.Height > 0)
                {
                    return size;
                }
            }
        }
        finally
        {
            if (pdfPage != IntPtr.Zero) Marshal.Release(pdfPage);
            if (pageUnk != IntPtr.Zero) Marshal.Release(pageUnk);
        }

        return new PdfPageSize { Width = 612, Height = 792 };
    }

    /// <summary>
    /// ページを指定の大きさで白背景に描画し、画像とページの大きさを返す。幅が0なら高さと縦横比から幅を決める。
    /// </summary>
    public async Task<(BitmapSource? Bitmap, PdfPageSize PageSize)> RenderPageAsync(
        uint pageIndex,
        uint targetWidth,
        uint targetHeight,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (pageIndex >= PageCount)
        {
            return (null, default);
        }

        return await Task.Run(() =>
        {
            RoInitialize(1);

            IntPtr pageUnk = IntPtr.Zero;
            IntPtr pdfPage = IntPtr.Zero;
            IntPtr optionsIns = IntPtr.Zero;
            IntPtr options = IntPtr.Zero;
            IntPtr hOptionsClass = IntPtr.Zero;
            IntPtr comStream = IntPtr.Zero;
            IntPtr outStream = IntPtr.Zero;
            IntPtr asyncAction = IntPtr.Zero;

            try
            {
                // ページを取り、大きさを読む。読めなければ 612x792 とする。
                PdfPageSize pageSize;
                unsafe
                {
                    var docVtbl = *(IntPtr**)_pdfDoc;
                    var getPage = (delegate* unmanaged[Stdcall]<IntPtr, uint, out IntPtr, int>)docVtbl[6];
                    if (getPage(_pdfDoc, pageIndex, out pageUnk) < 0 || pageUnk == IntPtr.Zero)
                    {
                        return (null, default);
                    }

                    if (Marshal.QueryInterface(pageUnk, in IID_IPdfPage, out pdfPage) < 0 || pdfPage == IntPtr.Zero)
                    {
                        return (null, default);
                    }

                    var pageVtbl = *(IntPtr**)pdfPage;
                    var getSize = (delegate* unmanaged[Stdcall]<IntPtr, out PdfPageSize, int>)pageVtbl[10];
                    if (getSize(pdfPage, out pageSize) < 0 || pageSize.Width <= 0 || pageSize.Height <= 0)
                    {
                        pageSize = new PdfPageSize { Width = 612, Height = 792 };
                    }
                }

                // ディスクに書かず、メモリ上の IStream を IRandomAccessStream にしてそこへ直接描画する。
                if (CreateStreamOnHGlobal(IntPtr.Zero, true, out comStream) < 0 || comStream == IntPtr.Zero)
                {
                    return (null, pageSize);
                }

                int hr = CreateRandomAccessStreamOverStream(comStream, 0, ref IID_IRandomAccessStream, out outStream);
                if (hr < 0 || outStream == IntPtr.Zero)
                {
                    return (null, pageSize);
                }

                // 描画オプションを作れたら、幅（9番）・高さ（11番）・背景色（13番）・エンコーダー（17番）を設定して
                // RenderWithOptionsToStreamAsync（ページの7番）で描く。作れなければ RenderToStreamAsync（6番）で既定の大きさで描く。
                const string optionsClassName = "Windows.Data.Pdf.PdfPageRenderOptions";
                if (WindowsCreateString(optionsClassName, optionsClassName.Length, out hOptionsClass) >= 0 &&
                    RoActivateInstance(hOptionsClass, out optionsIns) >= 0 &&
                    optionsIns != IntPtr.Zero &&
                    Marshal.QueryInterface(optionsIns, in IID_IPdfPageRenderOptions, out options) >= 0 &&
                    options != IntPtr.Zero)
                {
                    unsafe
                    {
                        var optVtbl = *(IntPtr**)options;
                        var putDestWidth = (delegate* unmanaged[Stdcall]<IntPtr, uint, int>)optVtbl[9];
                        var putDestHeight = (delegate* unmanaged[Stdcall]<IntPtr, uint, int>)optVtbl[11];
                        var putBgColor = (delegate* unmanaged[Stdcall]<IntPtr, WinRtColor, int>)optVtbl[13];
                        var putEncoderId = (delegate* unmanaged[Stdcall]<IntPtr, Guid, int>)optVtbl[17];

                        uint actualWidth = targetWidth;
                        uint actualHeight = targetHeight;
                        if (actualWidth == 0 && actualHeight > 0 && pageSize.Height > 0)
                        {
                            double aspect = pageSize.Width / (double)pageSize.Height;
                            actualWidth = (uint)Math.Clamp(actualHeight * aspect, 64.0, 4096.0);
                        }
                        if (actualWidth > 0) putDestWidth(options, actualWidth);
                        if (actualHeight > 0) putDestHeight(options, actualHeight);
                        putBgColor(options, new WinRtColor { A = 255, R = 255, G = 255, B = 255 });
                        // PNG の zlib 圧縮を省き、無圧縮の BMP でメモリへ書き出して大幅に速くする。
                        putEncoderId(options, WicBmpEncoderId);

                        var pageVtbl = *(IntPtr**)pdfPage;
                        var renderWithOptions = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, out IntPtr, int>)pageVtbl[7];
                        hr = renderWithOptions(pdfPage, outStream, options, out asyncAction);
                    }
                }
                else
                {
                    unsafe
                    {
                        var pageVtbl = *(IntPtr**)pdfPage;
                        var render = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, out IntPtr, int>)pageVtbl[6];
                        hr = render(pdfPage, outStream, out asyncAction);
                    }
                }

                // 描画が終わるまで待ち、書き出されたメモリを取り出す。
                if (hr < 0 || asyncAction == IntPtr.Zero || !WaitForAsyncCompletion(asyncAction, cancellationToken))
                {
                    return (null, pageSize);
                }

                if (GetHGlobalFromStream(comStream, out IntPtr hGlobal) < 0 || hGlobal == IntPtr.Zero)
                {
                    return (null, pageSize);
                }

                long byteLen = (long)GlobalSize(hGlobal);
                if (byteLen <= 0)
                {
                    return (null, pageSize);
                }

                IntPtr lockedPtr = GlobalLock(hGlobal);
                if (lockedPtr == IntPtr.Zero)
                {
                    return (null, pageSize);
                }

                // メモリをロックしたまま BMP を読み切ってフリーズする。
                try
                {
                    unsafe
                    {
                        using var ums = new UnmanagedMemoryStream((byte*)lockedPtr, byteLen);
                        var bmp = new BitmapImage();
                        bmp.BeginInit();
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.StreamSource = ums;
                        bmp.EndInit();
                        bmp.Freeze();
                        return ((BitmapSource?)bmp, pageSize);
                    }
                }
                finally
                {
                    GlobalUnlock(hGlobal);
                }
            }
            finally
            {
                // 途中で取った COM オブジェクトと文字列を解放する。
                if (asyncAction != IntPtr.Zero) Marshal.Release(asyncAction);
                if (outStream != IntPtr.Zero) Marshal.Release(outStream);
                if (comStream != IntPtr.Zero) Marshal.Release(comStream);
                if (options != IntPtr.Zero) Marshal.Release(options);
                if (optionsIns != IntPtr.Zero) Marshal.Release(optionsIns);
                if (hOptionsClass != IntPtr.Zero) WindowsDeleteString(hOptionsClass);
                if (pdfPage != IntPtr.Zero) Marshal.Release(pdfPage);
                if (pageUnk != IntPtr.Zero) Marshal.Release(pageUnk);
            }
        }, cancellationToken);
    }

    /// <summary>
    /// WinRT の非同期処理が終わるまで最大 15 秒待つ。完了なら true、取り消し・エラー・時間切れなら false を返す。
    /// </summary>
    private static bool WaitForAsyncCompletion(IntPtr asyncUnk, CancellationToken cancellationToken)
    {
        if (Marshal.QueryInterface(asyncUnk, in IID_IAsyncInfo, out IntPtr asyncInfo) < 0 || asyncInfo == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int spins = 0;
            while (sw.ElapsedMilliseconds < 15000)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int status;
                unsafe
                {
                    var vtbl = *(IntPtr**)asyncInfo;
                    // IAsyncInfo::get_Status は vtable の7番。
                    var getStatus = (delegate* unmanaged[Stdcall]<IntPtr, out int, int>)vtbl[7];
                    if (getStatus(asyncInfo, out status) < 0)
                    {
                        return false;
                    }
                }

                // 状態が 1 なら完了、2 は取り消し、3 はエラー。
                if (status == 1)
                {
                    return true;
                }
                if (status is 2 or 3)
                {
                    return false;
                }

                // 最初の15回は譲るだけにし、その後は 1 ミリ秒ずつ休んで待つ。
                if (spins++ < 15)
                {
                    Thread.Yield();
                }
                else
                {
                    Thread.Sleep(1);
                }
            }
            return false;
        }
        finally
        {
            Marshal.Release(asyncInfo);
        }
    }

    /// <summary>
    /// 文書とストリームの COM オブジェクトを解放する。
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_pdfDoc != IntPtr.Zero)
        {
            Marshal.Release(_pdfDoc);
            _pdfDoc = IntPtr.Zero;
        }
        if (_inputStream != IntPtr.Zero)
        {
            Marshal.Release(_inputStream);
            _inputStream = IntPtr.Zero;
        }
    }
}
