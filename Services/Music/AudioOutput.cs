using NAudio;
using NAudio.Wave;

namespace MirrorFlux.Services.Music;

/// <summary>Windows の音声出力デバイスの有無と、デバイスがないときの表示文言。</summary>
public static class AudioOutput
{
    /// <summary>デバイス切断後も停止と解放をそれぞれ試す。</summary>
    internal static void Close(IWavePlayer? output)
    {
        if (output == null) return;
        try { output.Stop(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
        try { output.Dispose(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
    }

    public const string NoDeviceMessage = "音声出力デバイスがありません";

    /// <summary>
    /// 使える出力デバイスの数。
    /// テストで差し替えられる。
    /// </summary>
    internal static Func<int> DeviceCount { get; set; } = () => WaveOut.DeviceCount;

    public static bool HasDevice
    {
        get { try { return DeviceCount() > 0; } catch (Exception) { return false; } }
    }

    /// <summary>出力を開けなかった原因が「デバイスがない」ことかを判定する。</summary>
    public static bool IsNoDevice(Exception ex) =>
        ex is MmException { Result: MmResult.BadDeviceId or MmResult.NoDriver or MmResult.NotEnabled } || !HasDevice;
}
