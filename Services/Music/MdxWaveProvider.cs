using System.Buffers;
using System.Buffers.Binary;
using NAudio.Wave;

namespace MirrorFlux.Services.Music;

/// <summary>
/// MDX の曲を鳴らすプロバイダー。
/// 演奏は MXDRV 互換の解釈器（<see cref="MxdrvCore"/>）で進め、FM は ymfm の YM2151（OPM）、PDX の音は X68000 の PCM8 ミキサーで作る。
/// NanoDrive 8 へ実機出力するときは、基板で鳴らす音をPCからは出さない。
/// </summary>
public sealed class MdxWaveProvider : IPlaybackWaveProvider
{
    private const int Rate = 44_100;
    private const int OpmClock = 4_000_000;
    private const int TailSeconds = 8;

    // MXDRV の音量0〜15を YM2151 の減衰量に直す表。
    private static readonly byte[] VolumeTable =
    [
        0x2a, 0x28, 0x25, 0x22, 0x20, 0x1d, 0x1a, 0x18,
        0x15, 0x12, 0x10, 0x0d, 0x0a, 0x08, 0x05, 0x02
    ];

    private readonly object _sync = new();
    private readonly MdxFile _file;
    private readonly MxdrvCore _core;
    private IntPtr _ymfm;
    public string CoreName => "ymfm";
    private readonly PdxPcmMixer _pcmMixer = new();
    private readonly KeyboardActivity _keyboardActivity = new();
    private readonly AudioSpectrumAnalyzer _spectrum = new();
    private readonly uint[] _opmPhaseSteps = new uint[8];
    public PitchTimeline Timeline { get; } = new();
    private int[] _mix = [];
    private int[] _spectrumInput = [];
    private int[][] _pcm = [[], []];
    private double _samplesToTick;
    private long _renderedFrames;
    private int _tailFrames = -1;
    private bool _disposed;

    public WaveFormat WaveFormat { get; } = new(Rate, 16, 2);
    public string Title => _file.Title;
    public string PdxFileName => _file.PdxFileName;
    public string PdxFilePath => _file.PdxResolvedPath;
    private bool _isPaused;
    /// <summary>
    /// 一時停止の切り替え。
    /// 実機出力中なら基板の再生も止める・再開する。
    /// </summary>
    public bool IsPaused
    {
        get => _isPaused;
        set
        {
            if (_isPaused == value) return;
            _isPaused = value;
            _hardware?.Pause(value, _renderedFrames);
        }
    }
    public bool Ended { get; private set; }

    // NanoDrive 8 へ出力しているとき、その接続。FM（とPDXのPCM）はPCでは鳴らさない。
    private readonly MdxHardwareSession? _hardware;
    private bool FmOnHardware => _hardware?.IsActive == true;
    private bool PcmOnHardware => FmOnHardware && _hardware!.SendsPcm;
    public bool HasHardwareOutput => FmOnHardware;

    /// <summary>MDX と PDX を読み込み、設定が有効なら NanoDrive 8 を開き、ymfm と MXDRV を準備する。</summary>
    public MdxWaveProvider(string path, IEnumerable<string>? pdxSearchPaths = null, MirrorFlux.Models.AppSettings? settings = null)
    {
        _file = MdxFile.Load(path, pdxSearchPaths);
        _core = new MxdrvCore(_file.Data, _file.PdxData);
        settings ??= new SettingsStore().Load().Settings;
        if (settings.Nd8Ndsif)
        {
            try { _hardware = MdxHardwareSession.TryOpen(_file, settings); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("MDX ND8: " + ex.Message); }
        }

        // MDX は常に独立した ymfm で鳴らす。
        _ymfm = YmfmNative.Create(OpmClock, Rate);
        if (_ymfm == IntPtr.Zero) throw new InvalidOperationException("ymfm YM2151 initialization failed.");
        _core.PcmOutCallback = OnPcmOut;
        _core.WriteOpmCallback = WriteOpm;
        _core.PcmStopCallback = _pcmMixer.Stop;

        // 最初のティックで、開始時の音色と各音の状態を設定しておく。
        try { _core.Tick(); }
        catch { if (_ymfm != IntPtr.Zero) { YmfmNative.Close(_ymfm); _ymfm = IntPtr.Zero; } throw; }
        RecordKeyboardState();
        _samplesToTick = SamplesPerTick;
    }

    /// <summary>
    /// MXDRV の1ティックが何サンプルか。
    /// テンポ値（タイマーB）から求める。
    /// </summary>
    private double SamplesPerTick => Rate * 1024.0 * (256 - _core.Tempo) / OpmClock;

    /// <summary>
    /// PCM8 の発音を受け、ミキサーで鳴らし始める。
    /// panVol の上位バイトが左右、下位バイトが音量。
    /// </summary>
    private void OnPcmOut(int pcmChannel, int offset, int length, int mode, int panVol)
    {
        if (_file.PdxData == null || pcmChannel < 0 || pcmChannel >= 8) return;
        int pan = (panVol >> 8) & 3;
        int vol = panVol & 0xff;
        _pcmMixer.Start(pcmChannel, _file.PdxData, offset, length, mode, pan, vol);
    }

    private int _fmMuted;
    private readonly int[] _fmKeyGens = new int[8];
    private readonly byte[] _fmSlotMasks = new byte[8];
    private readonly bool[] _fmKeyOnReset = new bool[8];
    private readonly int[] _pcmKeyGens = new int[8];
    private readonly bool[] _wasPcmActive = new bool[8];
    /// <summary>
    /// YM2151 への書き込みを ymfm と実機へ送る。
    /// キーオンの回数を数え、消音中のチャンネルは左右の出力ビットを落とす。
    /// </summary>
    private void WriteOpm(int register, byte value)
    {
        // キーオン（0x08）からチャンネルとスロットを読み、新しく鳴った回数を数える。
        if (register == 0x08)
        {
            int ch = value & 7;
            byte slots = (byte)((value >> 3) & 0x0f);
            if (slots != 0 && _fmSlotMasks[ch] == 0)
            {
                _fmKeyGens[ch]++;
                _fmKeyOnReset[ch] = true;
            }
            else if (slots == 0)
            {
                _fmKeyOnReset[ch] = true;
            }
            _fmSlotMasks[ch] = slots;
        }
        // 消音中のチャンネルは 0x20〜0x27 の左右ビットを落としてから書く。
        if (register is >= 0x20 and <= 0x27 && (_fmMuted & (1 << (register - 0x20))) != 0) value &= 0x3f;
        if (_ymfm != IntPtr.Zero) YmfmNative.Write(_ymfm, (byte)register, value);
        _hardware?.WriteOpm(register, value, _renderedFrames);
    }
    /// <summary>
    /// チャンネルの消音を切り替える。
    /// PCM8 はミキサーのマスク、YM2151 は左右ビットの書き直しで行う。
    /// </summary>
    public bool SetChannelMuted(MusicChannelSnapshot channel, bool muted)
    {
        lock (_sync)
        {
            string[] parts = channel.Id.Split(':');
            if (!int.TryParse(parts[^1], out int voice) || (uint)voice >= 8) return false;
            int flag = 1 << voice;
            if (parts[0] == "PCM8") _pcmMixer.MutedMask = muted ? _pcmMixer.MutedMask | flag : _pcmMixer.MutedMask & ~flag;
            else if (parts[0] == "YM2151")
            {
                _fmMuted = muted ? _fmMuted | flag : _fmMuted & ~flag;
                WriteOpm(0x20 + voice, _core.OpmRegisters[0x20 + voice]);
            }
            else return false;
            return true;
        }
    }
    /// <summary>
    /// 指定バイト数の音を作る。
    /// ティックの境目ごとに MXDRV を進め、間を ymfm と PCM8 で鳴らす。
    /// 最初の読み出しの後に基板の時計を動かし始める。
    /// </summary>
    public int Read(byte[] buffer, int offset, int count)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            int frames = count / 4;
            int written = 0;
            if (IsPaused)
            {
                Array.Clear(buffer, offset, frames * 4);
                return frames * 4;
            }
            // 次のティックまでのサンプル数ずつ作る。
            while (written < frames && !Ended)
            {
                if (_samplesToTick <= .0001)
                {
                    _core.Tick();
                    RecordKeyboardState();
                    _samplesToTick += Math.Max(1, SamplesPerTick);
                }
                int chunk = Math.Min(frames - written, Math.Max(1, (int)Math.Ceiling(_samplesToTick)));
                Render(buffer, offset + written * 4, chunk);
                written += chunk;
                _samplesToTick -= chunk;
            }
            if (written < frames) Array.Clear(buffer, offset + written * 4, (frames - written) * 4);
            _hardware?.StartOnce();
            return frames * 4;
        }
    }

    /// <summary>
    /// Span 版の Read。
    /// 借りた配列で読んでから写す。
    /// </summary>
    public int Read(Span<byte> buffer)
    {
        byte[] rented = ArrayPool<byte>.Shared.Rent(buffer.Length);
        try
        {
            int read = Read(rented, 0, buffer.Length);
            rented.AsSpan(0, read).CopyTo(buffer);
            return read;
        }
        finally { ArrayPool<byte>.Shared.Return(rented); }
    }

    /// <summary>
    /// 64フレームずつ FM と PCM8 を鳴らしてミックスし、スペクトラムと表示用の時系列にも記録する。
    /// 曲の終わりの後は余韻の分だけ鳴らしてから終了にする。
    /// </summary>
    private void Render(byte[] destination, int offset, int frames)
    {
        int done = 0;
        while (done < frames)
        {
            // ymfm で FM を鳴らし、鍵盤の音程用に位相増分も読む。PCM8 もミックスする。
            int slice = Math.Min(64, frames - done);
            EnsureBuffers(slice);
            Array.Clear(_mix, 0, slice * 2);
            if (_ymfm != IntPtr.Zero)
            {
                YmfmNative.Render(_ymfm, _mix, (uint)slice);
                YmfmNative.PhaseSteps(_ymfm, _opmPhaseSteps);
            }
            _pcmMixer.Mix(_pcm, slice);
            int sliceOffset = offset + done * 4;
            // 基板で鳴らしている音はPCからは出さない（表示用の計算は続ける）。
            int fm = FmOnHardware ? 0 : 1, pcm = PcmOnHardware ? 0 : 1;
            for (int i = 0; i < slice; i++)
            {
                int left = Math.Clamp(_mix[i * 2] * fm + _pcm[0][i] * pcm, short.MinValue, short.MaxValue);
                int right = Math.Clamp(_mix[i * 2 + 1] * fm + _pcm[1][i] * pcm, short.MinValue, short.MaxValue);
                BinaryPrimitives.WriteInt16LittleEndian(destination.AsSpan(sliceOffset + i * 4, 2), (short)left);
                BinaryPrimitives.WriteInt16LittleEndian(destination.AsSpan(sliceOffset + i * 4 + 2, 2), (short)right);
                _spectrumInput[i * 2] = left;
                _spectrumInput[i * 2 + 1] = right;
            }
            _spectrum.AddSamples(_spectrumInput.AsSpan(0, slice * 2));
            done += slice;
            _renderedFrames += slice;
            Timeline.Record(_renderedFrames, CaptureChannels());
        }

        // 曲が終わったら TailSeconds 秒の余韻を鳴らしてから終える。
        if (_core.Ended)
        {
            if (_tailFrames < 0) _tailFrames = TailSeconds * Rate;
            _tailFrames -= frames;
            if (_tailFrames <= 0) Ended = true;
        }
    }

    /// <summary>作業用バッファを必要な大きさにそろえる。</summary>
    private void EnsureBuffers(int frames)
    {
        if (_mix.Length < frames * 2) _mix = new int[frames * 2];
        if (_spectrumInput.Length < frames * 2) _spectrumInput = new int[frames * 2];
        if (_pcm[0].Length < frames) _pcm = [new int[frames], new int[frames]];
    }

    private void RecordKeyboardState() => _keyboardActivity.Record(CaptureChannels());

    /// <summary>FM8音と PCM8 の8音の表示用の状態を作る。</summary>
    private MusicChannelSnapshot[] CaptureChannels()
    {
        var channels = new MusicChannelSnapshot[16];

        // FM 0〜7。
        for (int i = 0; i < 8; i++)
        {
            var ch = _core.FmChannels[i];
            // 音量から表示レベルを、ノート＋3を光らせる鍵とする。
            int att = (ch.Volume & 0x80) != 0 ? ch.Volume & 0x7f : VolumeTable[ch.Volume & 0x0f];
            double level = Math.Clamp((127 - att) / 127.0, 0, 1);
            int? midi = ch.CurrentNote is int note ? Math.Clamp(note + 3, 0, 127) : null;
            // 0x20 のレジスタはビット6が左、ビット7が右。表示用の値に並べ替える。
            int pan = StereoPan.FromOpm(ch.PanFlCon);
            double? finePitch = null;
            // 細かい音程はレジスタの KC/KF から求める。ハードウェアの PM が効いていてキーオン直後でなければ、ymfm の位相増分から求めた値を使う（レジスタの値から1.5半音以上離れたら使わない）。
            if (midi is int key)
            {
                double regPitch = MusicPitch.AlignOctave(MusicPitch.FromOpm(_core.OpmRegisters[0x28 + i], _core.OpmRegisters[0x30 + i], OpmClock), key);
                bool hasHwPm = ((_core.OpmRegisters[0x38 + i] >> 4) & 7) != 0 && (_core.OpmRegisters[0x19] & 0x7f) != 0;
                bool isKeyOnAttack = _fmKeyOnReset[i] || !ch.IsKeyOn;
                double? ymfmPitch = (!isKeyOnAttack && hasHwPm && MusicPitch.FromOpmPhaseStep(_opmPhaseSteps[i], OpmClock) is double yp)
                    ? MusicPitch.AlignOctave(yp, key)
                    : null;
                finePitch = ymfmPitch is double p && Math.Abs(p - regPitch) <= 1.5 ? p : regPitch;
                if (ch.IsKeyOn) _fmKeyOnReset[i] = false;
            }

            channels[i] = new MusicChannelSnapshot
            {
                Id = $"YM2151:FM:{i}",
                ChipName = "YM2151",
                Label = $"FM{i + 1:00}",
                Kind = MusicChannelKind.Fm,
                IsHardwareOutput = FmOnHardware,
                NoteNumber = midi,
                // ディチューン・ポルタメント・MXDRVのピッチLFO・ymfmのハードLFO PMを反映した連続ピッチ。光らせる鍵（ドライバーのノート+3）はOPMの実際の音程より1オクターブ低い表記のため、線もオクターブ単位で鍵に合わせる。
                Pitch = finePitch,
                IsKeyOn = ch.IsKeyOn,
                KeyGeneration = _fmKeyGens[i],
                IsInUse = ch.IsInUse,
                Level = ch.IsKeyOn ? level : 0,
                Pan = pan,
                Volume = ch.Volume,
                Algorithm = ch.PanFlCon & 7,
                Feedback = (ch.PanFlCon >> 3) & 7,
                SlotMask = ch.KeyonSlotMask >> 3,
                DataString = ch.CurrentNote is int n ? $"N:{n:00} @{ch.CurrentVoice}" : $"@{ch.CurrentVoice}"
            };
        }

        // PCM 0〜7（PCM 0 は FmChannels[8]、PCM 1〜7 は PcmChannels[0..6]）。
        for (int pcm = 0; pcm < 8; pcm++)
        {
            var ch = pcm == 0 ? _core.FmChannels[8] : _core.PcmChannels[pcm - 1];
            int pan = ch.PanFlCon & 3;
            int mode = (ch.PanFlCon >> 2) & 7;
            double rate = PdxPcmMixer.Frequency(mode);
            // PCM の鍵盤とメーターのノートは PDX のサンプル番号を表し、レートではない。
            int note = ch.PcmSampleIndex;
            // MXDRV が入れ替えて持っている0と3を戻すと p の値（ビット0＝左、ビット1＝右）になる。表示用の値に並べ替える。
            pan = StereoPan.FromLeftInBit0(pan is 0 or 3 ? pan ^ 3 : pan);
            bool active = ch.IsKeyOn && _pcmMixer.IsActive(pcm);
            if (active && !_wasPcmActive[pcm]) _pcmKeyGens[pcm]++;
            _wasPcmActive[pcm] = active;
            double level = Math.Clamp(_pcmMixer.Level(pcm), 0, 1);

            channels[8 + pcm] = new MusicChannelSnapshot
            {
                Id = $"PCM8:{pcm}",
                ChipName = "PCM8",
                Label = $"PCM{pcm + 1:00}",
                Kind = MusicChannelKind.Pcm,
                IsHardwareOutput = PcmOnHardware,
                NoteNumber = active ? note : null,
                Frequency = rate,
                IsKeyOn = active,
                KeyGeneration = _pcmKeyGens[pcm],
                IsInUse = ch.IsInUse && _file.PdxData != null,
                Level = active ? level : 0,
                Pan = pan,
                Volume = ch.Volume,
                DataString = ch.IsKeyOn ? $"PDX:{ch.PcmSampleIndex:000} F{mode}" : $"F{mode}"
            };
        }

        return channels;
    }

    /// <summary>
    /// 表示用の状態を返す。
    /// PDX の有無と実機出力の状態も載せる。
    /// </summary>
    public MusicPlaybackSnapshot Snapshot()
    {
        float[] spectrumLevels = _spectrum.Capture();
        lock (_sync)
        {
            MusicChannelSnapshot[] channels = CaptureChannels();
            string pdxDisplayName = _file.PdxFileName.EndsWith(".pdx", StringComparison.OrdinalIgnoreCase)
                ? _file.PdxFileName
                : _file.PdxFileName + ".pdx";
            return new MusicPlaybackSnapshot
            {
                IsPlaying = !Ended,
                IsPaused = IsPaused,
                Title = Title,
                SystemName = "X68000 YM2151 + PCM8",
                FormatName = "MDX",
                FileTypeDisplay = !string.IsNullOrEmpty(_file.PdxFileName)
                    ? $"MDX (MXDRV 2.06) + {pdxDisplayName}"
                    : "MDX (MXDRV 2.06)",
                SoundSourceDisplay = "YM2151 (OPM) + PCM8",
                AuxiliaryFileStatus = _file.PdxData != null ? "PDX: " + System.IO.Path.GetFileName(_file.PdxResolvedPath) + " | " + ArchiveService.GetOriginalViewPath(_file.PdxResolvedPath) : string.IsNullOrWhiteSpace(_file.PdxFileName) ? "PDX: NONE" : "PDX: MISSING " + _file.PdxFileName + " (FM ONLY)",
                CurrentTime = TimeSpan.FromSeconds(_renderedFrames / (double)Rate),
                LoopCount = _core.LoopCount,
                FmCoreName = FmOnHardware ? "—" : "ymfm",
                ExtDeviceName = FmOnHardware ? "ND8" : "—",
                Channels = _keyboardActivity.Apply(channels),
                SpectrumLevels = spectrumLevels,
                SpectrumKind = "AUDIO FFT / MXDRV",
                RegisterBanks = [new MusicRegisterBank { ChipName = "YM2151", Values = _core.OpmRegisters.ToArray() }]
            };
        }
    }

    /// <summary>ymfm を閉じ、PCM8 を止めて実機の接続を切る。</summary>
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                if (_ymfm != IntPtr.Zero) { YmfmNative.Close(_ymfm); _ymfm = IntPtr.Zero; }
                for (int channel = 0; channel < 8; channel++) _pcmMixer.Stop(channel);
            }
            finally { _hardware?.Dispose(); }
        }
    }
}
