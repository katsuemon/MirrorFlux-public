using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace MirrorFlux.Services;

/// <summary>
/// 名前に含まれる数値の大小を考えて並べる自然順の比較。Windows の StrCmpLogicalW を使う。
/// 例: "1.jpg" &lt; "2.jpg" &lt; "10.jpg"
/// </summary>
public sealed class NaturalStringComparer : IComparer<string>
{
    public static readonly NaturalStringComparer Instance = new();

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int StrCmpLogicalW(string x, string y);

    /// <summary>
    /// null を先にして比べる。Windows 以外では大文字・小文字を区別しない文字列比較にする。
    /// </summary>
    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;
        if (OperatingSystem.IsWindows())
        {
            return StrCmpLogicalW(x, y);
        }
        return StringComparer.CurrentCultureIgnoreCase.Compare(x, y);
    }
}
