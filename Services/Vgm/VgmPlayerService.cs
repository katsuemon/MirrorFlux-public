using System;
using System.IO;
using System.Threading;
using NAudio.Wave;
using MirrorFlux.Services.Music;

namespace MirrorFlux.Services.Vgm
{
    /// <summary>VGMの再生を管理する唯一のサービス。音声出力の開閉、一時停止、停止、表示用スナップショットの取得を受け持つ。</summary>
    public sealed class VgmPlayerService : IDisposable
    {
        private static readonly Lazy<VgmPlayerService> _instance = new(() => new VgmPlayerService());
        public static VgmPlayerService Instance => _instance.Value;

        private IWavePlayer? _wavePlayer;
        private IVgmWaveProvider? _waveProvider;
        private OutputGainWaveProvider? _gainProvider;
        private readonly object _lock = new();
        private float _outputGain = 1f;
        public void SetOutputGain(float gain) { lock (_lock) { _outputGain = Math.Clamp(gain, 0f, 1f); _gainProvider?.SetGain(_outputGain); } }

        public bool SetChannelMuted(MusicChannelSnapshot channel, bool muted) { lock (_lock) return _waveProvider?.SetChannelMuted(channel, muted) ?? false; }
        public bool IsPlaying => _waveProvider?.IsPlaying ?? false;
        public bool IsPaused => _waveProvider?.IsPaused ?? false;
        public VgmFile? CurrentVgm => _waveProvider?.Vgm;

        public event Action? PlaybackStarted;
        public event Action? PlaybackStopped;
        public event Action? PlaybackPaused;
        public event Action? PlaybackResumed;

        private VgmPlayerService()
        {
        }

        /// <summary>ファイルを読み込んで再生する。</summary>
        public bool Play(string filePath)
        {
            var vgm = VgmFile.Load(filePath);
            if (vgm == null) return false;
            return Play(vgm);
        }

        /// <summary>メモリ上のVGMデータを再生する。</summary>
        public bool Play(byte[] bytes, string title)
        {
            var vgm = VgmFile.Load(bytes, title);
            if (vgm == null) return false;
            return Play(vgm);
        }

        /// <summary>直前の再生開始に失敗した理由（利用者に見せる文言）。成功時はnull。</summary>
        public string? LastError { get; private set; }

        /// <summary>音声出力デバイスがなく、実機（NDSIF）だけで再生しているか。</summary>
        public bool IsHardwareOnly => _wavePlayer is SilentWavePlayer;

        /// <summary>テスト用: 音声出力を開く処理の差し替え。</summary>
        internal Func<IWavePlayer> CreateOutput { get; set; } = () => new WaveOut();
        /// <summary>テスト用: 再生プロバイダーを作る処理の差し替え（設定を渡すため）。</summary>
        internal Func<VgmFile, IVgmWaveProvider> CreateProvider { get; set; } = file => VgmWaveProviderFactory.Create(file);

        /// <summary>前の再生を止め、新しい曲の再生を始める。失敗したら理由をLastErrorに残す。</summary>
        public bool Play(VgmFile vgm)
        {
            lock (_lock)
            {
                StopInternal();
                LastError = null;

                try
                {
                    // 波形供給元と出力音量を用意し、音声出力を開いて再生する。
                    _waveProvider = CreateProvider(vgm);
                    _waveProvider.PlaybackFinished += OnPlaybackFinished;
                    _gainProvider = new OutputGainWaveProvider(_waveProvider);
                    _gainProvider.SetGain(_outputGain);
                    _wavePlayer = OpenOutput(_gainProvider, _waveProvider);
                    _wavePlayer.Play();

                    PlaybackStarted?.Invoke();
                    return true;
                }
                catch (Exception ex)
                {
                    // 失敗したら後始末をして、出力デバイスがない場合はその旨を残す。
                    System.Diagnostics.Debug.WriteLine($"VgmPlayerService.Play error: {ex}");
                    LastError ??= AudioOutput.IsNoDevice(ex) ? AudioOutput.NoDeviceMessage : null;
                    StopInternal();
                    return false;
                }
            }
        }

        /// <summary>
        /// 音声出力を開く。通常はWaveOutを使う。
        /// 音声出力デバイスがないときは、実機（NDSIF）へ出力している音源チップがあれば無音の再生器で実機だけ鳴らす。
        /// 実機もなければ「音声出力デバイスがありません」で失敗する。
        /// </summary>
        private IWavePlayer OpenOutput(IWaveProvider output, IVgmWaveProvider provider)
        {
            IWavePlayer? player = null;
            try
            {
                player = CreateOutput();
                player.Init(output);
                return player;
            }
            catch (Exception ex) when (AudioOutput.IsNoDevice(ex))
            {
                try { player?.Dispose(); } catch { }
                if (provider is IndependentVgmWaveProvider { HasHardwareOutput: true })
                {
                    var silent = new SilentWavePlayer();
                    silent.Init(output);
                    return silent;
                }
                LastError = AudioOutput.NoDeviceMessage;
                throw;
            }
        }

        /// <summary>再生中なら一時停止する。</summary>
        public void Pause()
        {
            lock (_lock)
            {
                if (_waveProvider != null && _waveProvider.IsPlaying && !_waveProvider.IsPaused)
                {
                    _waveProvider.IsPaused = true;
                    _wavePlayer?.Pause();
                    PlaybackPaused?.Invoke();
                }
            }
        }

        /// <summary>一時停止中なら再開する。</summary>
        public void Resume()
        {
            lock (_lock)
            {
                if (_waveProvider != null && _waveProvider.IsPaused)
                {
                    _waveProvider.IsPaused = false;
                    _wavePlayer?.Play();
                    PlaybackResumed?.Invoke();
                }
            }
        }

        public void TogglePause()
        {
            if (IsPaused) Resume();
            else if (IsPlaying) Pause();
        }

        public void Stop()
        {
            lock (_lock)
            {
                StopInternal();
            }
        }

        /// <summary>音声出力と波形供給元を破棄して再生を終える。</summary>
        private void StopInternal()
        {
            if (_wavePlayer != null)
            {
                MirrorFlux.Services.Music.AudioOutput.Close(_wavePlayer);
                _wavePlayer = null;
            }

            _gainProvider = null;

            if (_waveProvider != null)
            {
                _waveProvider.PlaybackFinished -= OnPlaybackFinished;
                _waveProvider.Dispose();
                _waveProvider = null;
            }

            PlaybackStopped?.Invoke();
        }

        /// <summary>曲の終わりで再生を止める。UIスレッド以外から呼ばれることがあるため、ロックの中で止める。</summary>
        private void OnPlaybackFinished()
        {
            lock (_lock)
            {
                StopInternal();
            }
        }

        /// <summary>表示用のスナップショットを返す。再生していなければ空の状態を返す。</summary>
        public VgmVisualSnapshot GetSnapshot()
        {
            var provider = _waveProvider;
            if (provider == null)
            {
                return new VgmVisualSnapshot { IsPlaying = false };
            }
            // パン・レジスタなども、鍵盤と同じく実際に聞こえている位置の状態を出す。
            var player = _wavePlayer;
            if (provider is IndependentVgmWaveProvider ivgm && player != null)
                return ivgm.GetSnapshot(ivgm.Timeline.GetPlaybackSamplePosition(player, ivgm.IsPlaying, ivgm.IsPaused));
            return provider.GetSnapshot();
        }

        /// <summary>実際に聞こえている位置に合わせて、スナップショットの音高をそろえる。</summary>
        public MusicPlaybackSnapshot ApplyRealtimePitches(MusicPlaybackSnapshot snapshot)
        {
            if (_waveProvider is not IndependentVgmWaveProvider ivgm || _wavePlayer == null)
                return snapshot;
            return ivgm.Timeline.ApplyTo(snapshot, _wavePlayer, ivgm.IsPlaying, ivgm.IsPaused);
        }

        public void Dispose()
        {
            Stop();
        }
    }

}
