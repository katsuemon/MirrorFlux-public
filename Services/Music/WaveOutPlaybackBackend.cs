using NAudio.Wave;

namespace MirrorFlux.Services.Music;

/// <summary>WaveOut で鳴らす再生バックエンドが共通で持つ波形の約束。</summary>
public interface IPlaybackWaveProvider : IWaveProvider, IDisposable
{
    bool IsPaused { get; set; }
    bool Ended { get; }
    MusicPlaybackSnapshot Snapshot();
    /// <summary>実機（NanoDrive）へ出力していて、音声出力デバイスがなくても再生を続けられるか。</summary>
    bool HasHardwareOutput => false;
}

/// <summary>
/// GME・MDX・音声ファイルで共通の再生処理（WaveOut、出力ゲイン、一時停止、停止、イベント）。
/// 派生クラスは自分の波形の作り方と、形式固有の操作だけを持つ。
/// 派生クラスの基底リストにも IMusicPlaybackBackend を書くこと（既定実装のあるメンバーを上書きするため）。
/// </summary>
public abstract class WaveOutPlaybackBackend<TProvider> : IMusicPlaybackBackend
    where TProvider : class, IPlaybackWaveProvider
{
    protected readonly object Sync = new();
    private OutputGainWaveProvider? _gainProvider;
    private float _outputGain = 1f;

    protected TProvider? Provider { get; private set; }
    protected IWavePlayer? Output { get; private set; }

    public abstract string FormatName { get; }
    public abstract IReadOnlyCollection<string> SupportedExtensions { get; }
    public string? CurrentFilePath { get; private set; }
    /// <summary>
    /// 直前の Play が失敗した理由。
    /// 音声出力デバイスがなく、実機にも出せなかったときに入る。
    /// </summary>
    public string? LastError { get; private set; }
    public bool IsPlaying => Provider != null && !Provider.Ended;
    public bool IsPaused => Provider?.IsPaused ?? false;
    public event Action? PlaybackStarted, PlaybackStopped, PlaybackPaused, PlaybackResumed;

    protected abstract TProvider CreateProvider(string path);
    protected void RaisePlaybackStarted() => PlaybackStarted?.Invoke();

    /// <summary>
    /// 出力の音量を設定する。
    /// 再生中なら出力ゲインにすぐ反映する。
    /// </summary>
    public void SetOutputGain(float gain)
    {
        lock (Sync)
        {
            _outputGain = Math.Clamp(gain, 0f, 1f);
            _gainProvider?.SetGain(_outputGain);
        }
    }

    /// <summary>
    /// 前の曲を止め、プロバイダーと出力を作って再生を始める。
    /// 失敗したら後片付けをし、デバイスがない場合は理由を残す。
    /// </summary>
    public bool Play(string path)
    {
        lock (Sync)
        {
            StopCore();
            LastError = null;
            try
            {
                Provider = CreateProvider(path);
                _gainProvider = new OutputGainWaveProvider(Provider);
                _gainProvider.SetGain(_outputGain);
                Output = OpenOutput(_gainProvider, Provider);
                Output.Play();
                CurrentFilePath = path;
                PlaybackStarted?.Invoke();
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex);
                if (AudioOutput.IsNoDevice(ex)) LastError = AudioOutput.NoDeviceMessage;
                StopCore();
                return false;
            }
        }
    }

    /// <summary>
    /// 通常は WaveOut。
    /// 音声出力デバイスがないときは、実機へ出力している曲なら無音の再生器で実機だけ鳴らす。
    /// </summary>
    private static IWavePlayer OpenOutput(IWaveProvider output, TProvider provider)
    {
        WaveOut? waveOut = null;
        try
        {
            waveOut = new WaveOut();
            waveOut.Init(output);
            return waveOut;
        }
        catch (Exception ex) when (AudioOutput.IsNoDevice(ex) && provider.HasHardwareOutput)
        {
            try { waveOut?.Dispose(); } catch { }
            var silent = new SilentWavePlayer();
            silent.Init(output);
            return silent;
        }
    }

    /// <summary>一時停止と再開を切り替え、通知を出す。</summary>
    public void TogglePause()
    {
        lock (Sync)
        {
            if (Provider == null) return;
            Provider.IsPaused = !Provider.IsPaused;
            if (Provider.IsPaused) { Output?.Pause(); PlaybackPaused?.Invoke(); }
            else { Output?.Play(); PlaybackResumed?.Invoke(); }
        }
    }

    /// <summary>出力とプロバイダーを閉じる。</summary>
    private void StopCore()
    {
        AudioOutput.Close(Output);
        Output = null;
        _gainProvider = null;
        try { Provider?.Dispose(); }
        finally { Provider = null; CurrentFilePath = null; }
    }

    /// <summary>停止して通知を出す。</summary>
    public void Stop()
    {
        lock (Sync)
        {
            StopCore();
            PlaybackStopped?.Invoke();
        }
    }

    public virtual MusicPlaybackSnapshot GetSnapshot()
    {
        lock (Sync) return Provider?.Snapshot() ?? new();
    }

    public void Dispose() => Stop();
}
