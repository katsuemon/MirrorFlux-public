using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace MirrorFlux.Services;

/// <summary>コピー先と同じフォルダーの一時ファイルへ書き終えてから、完成したコピーとして置く。</summary>
internal static class FileTransferWriter
{
    /// <summary>
    /// ファイルを一時ファイル経由でコピーする。途中で中止・失敗しても既存のコピー先は壊さない。
    /// 書いたバイト数は実行中のファイル操作（<see cref="FileOperationJob.Current"/>）へ知らせ、ファイルの途中でも中止できる。
    /// </summary>
    public static async Task CopyAsync(string source, string destination, bool overwrite, CancellationToken cancellationToken)
    {
        FileOperationPaths.ValidateFileTransfer(source, destination);
        string temporary = Path.Combine(Path.GetDirectoryName(destination)!, $".mf-copy-{Guid.NewGuid():N}.tmp");
        var job = FileOperationJob.Current;
        try
        {
            // 一時ファイルへコピーする。
            cancellationToken.ThrowIfCancellationRequested();
            try { CopyWithProgress(source, temporary, job, cancellationToken); }
            catch (IOException)
            {
                // クラウド同期のフィルターには、CopyFile は失敗しても通常のストリームなら読めるものがある。
                cancellationToken.ThrowIfCancellationRequested();
                if (File.Exists(temporary)) File.SetAttributes(temporary, FileAttributes.Normal);
                await CopyStreamAsync(source, temporary, job, cancellationToken);
            }
            // 更新日時を合わせ、置く直前にもう一度リンクなどを確かめる。
            cancellationToken.ThrowIfCancellationRequested();
            try { File.SetLastWriteTime(temporary, File.GetLastWriteTime(source)); } catch (IOException) { }
            FileOperationPaths.ValidateFileTransfer(source, destination);
            if (overwrite && File.Exists(destination))
            {
                // 通常のコピーと同じ共有の扱いにするため、先に書き込みで開いてみる。使用中ならその旨の正確なエラーになる。
                using (var writable = new FileStream(destination, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) { }
                File.Move(temporary, destination, true);
            }
            else File.Move(temporary, destination, overwrite);
        }
        finally
        {
            // 置けなかった一時ファイル（中止したときの書きかけを含む）を片付ける。
            if (File.Exists(temporary))
                try { File.SetAttributes(temporary, FileAttributes.Normal); File.Delete(temporary); }
                catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>ストリームで1MBずつコピーする。1回ごとに中止を確かめ、書いた量を知らせる。</summary>
    internal static async Task CopyStreamAsync(string source, string destination, FileOperationJob? job, CancellationToken cancellationToken)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20, true);
        using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, true);
        byte[] buffer = new byte[1 << 20];
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            job?.AddBytes(read);
        }
    }

    private const uint ProgressContinue = 0, ProgressCancel = 1;
    private const uint CopyFileFailIfExists = 0x1;
    private const int ErrorRequestAborted = 1235;

    private delegate uint CopyProgressRoutine(long totalFileSize, long totalBytesTransferred, long streamSize,
        long streamBytesTransferred, uint streamNumber, uint callbackReason, IntPtr sourceFile, IntPtr destinationFile, IntPtr data);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CopyFileEx(string existingFileName, string newFileName, CopyProgressRoutine? progressRoutine,
        IntPtr data, ref int cancel, uint copyFlags);

    /// <summary>
    /// Windows のコピー機能（CopyFileEx）で新しいファイルへコピーする。File.Copy と同じく属性も写す。
    /// 進み具合の通知のたびに中止を確かめ、中止なら書きかけを Windows が消して OperationCanceledException になる。
    /// 失敗は File.Copy と同じ種類の例外にする（IOException なら呼び出し側がストリームで試し直す）。
    /// </summary>
    private static void CopyWithProgress(string source, string destination, FileOperationJob? job,
        CancellationToken cancellationToken)
    {
        long sent = 0;
        CopyProgressRoutine routine = (_, transferred, _, _, _, _, _, _, _) =>
        {
            if (transferred > sent) { job?.AddBytes(transferred - sent); sent = transferred; }
            return cancellationToken.IsCancellationRequested ? ProgressCancel : ProgressContinue;
        };
        int cancel = 0;
        bool ok = CopyFileEx(source, destination, routine, IntPtr.Zero, ref cancel, CopyFileFailIfExists);
        int error = Marshal.GetLastWin32Error();
        GC.KeepAlive(routine);
        if (ok) return;
        if (error == ErrorRequestAborted || cancellationToken.IsCancellationRequested)
            throw new OperationCanceledException(cancellationToken);
        var exception = Marshal.GetExceptionForHR(unchecked((int)(0x80070000u | (uint)error)));
        throw exception ?? new IOException(new Win32Exception(error).Message, unchecked((int)(0x80070000u | (uint)error)));
    }
}
