using NAudio.Wave;
using MirrorFlux.Services.Music;

namespace MirrorFlux.Services.Vgm;

/// <summary>VGM再生用の波形供給元が備える共通の操作。再生状態、チャンネルのミュート、表示用スナップショットを扱う。</summary>
public interface IVgmWaveProvider : IWaveProvider, IDisposable
{
    int Read(byte[] buffer, int offset, int count);
    VgmFile Vgm { get; }
    bool IsPlaying { get; }
    bool IsPaused { get; set; }
    event Action? PlaybackFinished;
    bool SetChannelMuted(MusicChannelSnapshot channel, bool muted);
    VgmVisualSnapshot GetSnapshot();
}

/// <summary>設定に合わせてVGM用の波形供給元を作る。</summary>
public static class VgmWaveProviderFactory
{
    public static IVgmWaveProvider Create(VgmFile file, MirrorFlux.Models.AppSettings? settings = null)
    {
        settings ??= new SettingsStore().Load().Settings;
        return new IndependentVgmWaveProvider(file,settings);
    }
}
