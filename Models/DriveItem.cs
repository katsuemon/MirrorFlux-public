namespace MirrorFlux.Models;

/// <summary>
/// ドライブ選択ダイアログに並べる1ドライブの情報。
/// </summary>
public sealed class DriveItem
{
    public char Letter { get; }
    public string DeviceTypeDisplay { get; }
    public string VolumeDisplay { get; }
    public string RootPath { get; }
    public bool IsReady { get; }
    public bool IsCurrent { get; }

    public DriveItem(
        char letter,
        string deviceTypeDisplay,
        string volumeDisplay,
        string rootPath,
        bool isReady,
        bool isCurrent)
    {
        Letter = letter;
        DeviceTypeDisplay = deviceTypeDisplay;
        VolumeDisplay = volumeDisplay;
        RootPath = rootPath;
        IsReady = isReady;
        IsCurrent = isCurrent;
    }
}
