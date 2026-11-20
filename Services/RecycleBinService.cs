using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace MirrorFlux.Services
{
    /// <summary>
    /// ファイル・フォルダーを Windows のごみ箱へ送る。
    /// </summary>
    public static class RecycleBinService
    {
        /// <summary>
        /// SHFileOperation に渡す操作内容。
        /// </summary>
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd;
            public uint wFunc;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string pFrom;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string? pTo;
            public ushort fFlags;
            [MarshalAs(UnmanagedType.Bool)]
            public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string? lpszProgressTitle;
        }

        // ごみ箱へ送り、Windows の確認・進捗・エラーのダイアログは出さない。確認と結果の表示はアプリ側で行う。
        private const uint FO_DELETE = 0x0003;
        private const ushort FOF_ALLOWUNDO = 0x0040;
        private const ushort FOF_NOCONFIRMATION = 0x0010;
        private const ushort FOF_SILENT = 0x0004;
        private const ushort FOF_NOERRORUI = 0x0400;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int SHFileOperation(ref SHFILEOPSTRUCT FileOp);

        /// <summary>
        /// 指定したファイルまたはフォルダーをごみ箱へ送り、成否と失敗理由を返す。
        /// </summary>
        public static (bool Success, string? Reason) DeleteToRecycleBinWithResult(string path)
            => DeleteToRecycleBinWithResult(path, RecycleWithShell);

        /// <summary>
        /// recycle で実際にごみ箱へ送り、結果のコードを失敗理由の文に直す。
        /// </summary>
        internal static (bool Success, string? Reason) DeleteToRecycleBinWithResult(
            string path, Func<string, (int Code, bool Aborted)> recycle)
        {
            // 対象を確かめる。
            if (string.IsNullOrWhiteSpace(path)) return (false, "パスが指定されていません");
            if (!File.Exists(path) && !Directory.Exists(path)) return (false, "対象のファイルまたはフォルダーが見つかりません");

            // ごみ箱へ送り、成功・中止を判定する。
            var (ret, aborted) = recycle(path);
            if (ret == 0 && !aborted)
            {
                return (true, null);
            }

            if (aborted)
            {
                return (false, "操作が中止されました");
            }

            // エラーコードを理由の文にする。
            return (false, ret switch
            {
                2 => "指定されたファイルが見つかりません",
                3 => "指定されたパスが見つかりません",
                5 => "アクセスが拒否されました (権限不足または属性制限)",
                32 => "ファイルが別のプロセスによって使用されています (プロセスロック中)",
                33 => "ファイルの一部が別のプロセスによってロックされています",
                0x78 => "アクセスが拒否されました (権限不足または属性制限)",
                0x7C => "パスが存在しないか長すぎます",
                _ => $"エラーコード: 0x{ret:X4}"
            });
        }

        /// <summary>
        /// SHFileOperation でごみ箱へ送り、戻り値と中止されたかを返す。
        /// </summary>
        private static (int Code, bool Aborted) RecycleWithShell(string path)
        {
            // SHFileOperation の pFrom は NULL 2つで終わらせる必要がある。
            string doubleNullPath = path.TrimEnd('\\', '/') + "\0\0";

            var op = new SHFILEOPSTRUCT
            {
                hwnd = IntPtr.Zero,
                wFunc = FO_DELETE,
                pFrom = doubleNullPath,
                pTo = null,
                fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI,
                fAnyOperationsAborted = false,
                hNameMappings = IntPtr.Zero,
                lpszProgressTitle = null
            };

            int ret = SHFileOperation(ref op);
            return (ret, op.fAnyOperationsAborted);
        }

        /// <summary>
        /// 指定したファイルまたはフォルダーをごみ箱へ送る。
        /// </summary>
        public static bool DeleteToRecycleBin(string path)
        {
            return DeleteToRecycleBinWithResult(path).Success;
        }

        /// <summary>
        /// 複数のファイル・フォルダーを1件ずつごみ箱へ送り、成功数と失敗数を返す。
        /// </summary>
        public static (int SuccessCount, int ErrorCount) DeleteToRecycleBin(IEnumerable<string> paths)
        {
            int success = 0;
            int error = 0;

            foreach (var path in paths)
            {
                if (DeleteToRecycleBin(path))
                {
                    success++;
                }
                else
                {
                    error++;
                }
            }

            return (success, error);
        }
    }
}
