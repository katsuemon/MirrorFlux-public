using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using MirrorFlux.Services;

namespace MirrorFlux.Models;

/// <summary>
/// ファイル一覧の1項目。ファイル・フォルダー・親フォルダー（..）を表し、表示用の文字列とマーク状態を持つ。
/// </summary>
public sealed class FileEntry : INotifyPropertyChanged
{
    private bool _isMarked;

    public FileEntry(string fullPath, string name, bool isDirectory, bool isParent, long length, DateTime lastWriteTime)
    {
        FullPath = fullPath;
        Name = name;
        IsDirectory = isDirectory;
        IsParent = isParent;
        Length = length;
        LastWriteTime = lastWriteTime;
    }

    public string FullPath { get; }
    public string Name { get; }
    public bool IsDirectory { get; }
    public bool IsParent { get; }
    public long Length { get; internal set; }
    public DateTime LastWriteTime { get; }

    /// <summary>
    /// サイズを数値と単位（Byte / KByte / MByte / GByte / TByte）に分ける。1024で単位を上げ、
    /// 10未満は小数2桁、100未満は小数1桁、それ以上は整数で表す。負の値は 0 Byte。
    /// </summary>
    public static (string Number, string Unit) FormatSizeParts(long bytes)
    {
        // 1024未満はByteのまま整数で出す。
        if (bytes < 0) return ("0", "Byte");
        if (bytes < 1024) return (bytes.ToString(CultureInfo.InvariantCulture), "Byte");

        // 1024で割りながら単位を上げる。
        string[] units = ["KByte", "MByte", "GByte", "TByte"];
        double size = bytes;
        int unitIndex = -1;
        while (size >= 1024 && unitIndex < units.Length - 1)
        {
            size /= 1024.0;
            unitIndex++;
        }

        // 値の大きさで小数の桁数を変える。
        string numStr = size < 10
            ? size.ToString("0.00", CultureInfo.InvariantCulture)
            : size < 100
                ? size.ToString("0.0", CultureInfo.InvariantCulture)
                : size.ToString("0", CultureInfo.InvariantCulture);
        return (numStr, units[unitIndex]);
    }

    /// <summary>
    /// ファイル一覧・ダイアログ・ビューア用のサイズ表記を返す（例："123 Byte"、"1.21 KByte"、"45.6 MByte"）。
    /// </summary>
    public static string FormatFileSize(long bytes)
    {
        var (number, unit) = FormatSizeParts(bytes);
        return $"{number} {unit}";
    }

    public Geometry IconGeometry => FileIconService.GetIcon(Name, IsDirectory, IsParent).Geometry;
    public Brush IconBrush => FileIconService.GetIcon(Name, IsDirectory, IsParent).Brush;

    public string DisplayName => IsParent ? ".." : IsDirectory ? $"[{Name}]" : Name;
    public string BaseName => IsParent ? ".." : IsDirectory ? Name : Path.GetFileNameWithoutExtension(Name);
    public string ExtText => IsDirectory || IsParent ? string.Empty : Path.GetExtension(Name);
    public string SizeText => IsDirectory ? "< DIR >" : FormatFileSize(Length);
    public string SizeDisplay => SizeText;
    public string TypeText => IsParent ? "PARENT" : IsDirectory ? "DIR" : (Path.GetExtension(Name).TrimStart('.').ToUpperInvariant());
    public string ModifiedText => LastWriteTime == DateTime.MinValue
        ? string.Empty
        : LastWriteTime.ToString("yyyy-MM-dd  HH:mm:ss", CultureInfo.InvariantCulture);
    public string DateTimeDisplay => LastWriteTime == DateTime.MinValue
        ? string.Empty
        : LastWriteTime.ToString("yyyy-MM-dd  HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>
    /// マークされているか。変わったらマーク表示も更新を知らせる。
    /// </summary>
    public bool IsMarked
    {
        get => _isMarked;
        set
        {
            if (_isMarked == value)
            {
                return;
            }

            _isMarked = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(MarkText));
        }
    }

    public string MarkText => IsMarked ? "*" : string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}


