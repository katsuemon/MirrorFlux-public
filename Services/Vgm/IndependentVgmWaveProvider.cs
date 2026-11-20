using System.Buffers.Binary;
using System.IO;
using NAudio.Wave;
using MirrorFlux.Services.Music;

namespace MirrorFlux.Services.Vgm;

/// <summary>VGMの命令の進行はMirrorFlux自身が受け持ち、音声はライセンスが明示された単体の音源チップコアで作る。</summary>
public sealed class IndependentVgmWaveProvider : IVgmWaveProvider
{
    private readonly object _sync = new();
    private readonly IndependentVgmProgram _program;
    private readonly Dictionary<(int Type, int Instance), VgmChip> _chips = [];
    private readonly VgmPcmBanks _banks = new();
    private readonly VgmDacStreams _streams;
    private readonly VgmNativeVoiceMonitor _voices = new();
    private VgmHardwareSession? _hardware;
    public bool IsNd6Active => _hardware?.IsNd6Active == true;
    /// <summary>この曲の音源チップのうち1つでも実機（NDSIF）へ出力しているか。</summary>
    public bool HasHardwareOutput => _hardware != null && _program.Devices.Any(d => _hardware.HardwareName(d.Type, d.Instance) != null);
    // ND8の音声ストリームの診断情報。
    internal uint Nd8AudioPlayed => _hardware?.Nd8AudioPlayed ?? 0;
    internal string Nd8AudioStage => _hardware?.Nd8AudioStage ?? "no session";
    internal string Nd8AudioDiagnostics => _hardware?.Nd8AudioDiagnostics ?? "no session";
    private readonly AudioSpectrumAnalyzer _spectrum = new();
    private VgmRegisterPlaybackMonitor? _monitor;
    private readonly uint[] _fmSteps = new uint[32];
    private readonly Dictionary<string, double> _fmPitches = new(StringComparer.OrdinalIgnoreCase);
    private readonly int[] _lastVgmKeyGen = new int[PitchTimeline.MaxChannels];
    private readonly bool[] _wasVgmKeyOn = new bool[PitchTimeline.MaxChannels];
    public PitchTimeline Timeline { get; } = new();
    // 音高の時間軸へ記録する間隔の上限（サンプル）。DACを多用する曲では合成単位が数サンプルになるため、
    // 毎回記録すると履歴が音声バッファ分より短くなる。チャンネルの状態が変わったときはすぐ記録する。
    internal const int TimelineRecordInterval = 64;
    private MusicChannelSnapshot[]? _lastRecordedChannels;
    private long _lastTimelineFrame = -TimelineRecordInterval;
    // 表示用スナップショットの履歴。合成は音声出力より先に進むので、聞こえている位置の状態を出せるように一定間隔で残す。
    internal const int SnapshotInterval = 441;
    private const int SnapshotHistory = 128;
    private readonly (long Frame, VgmVisualSnapshot Snapshot)[] _snapshots = new (long, VgmVisualSnapshot)[SnapshotHistory];
    private int _snapshotCount, _snapshotNext;
    private long _lastSnapshotFrame = -SnapshotInterval;
    private readonly int[] _chipAudio = new int[2048 * 2];
    private readonly long[] _accumulator = new long[2048 * 2];
    private readonly int[] _mix = new int[2048 * 2];
    private readonly VgmWaitTimer _waits = new();
    private int _index, _wait, _pcmCursor, _loops, _voiceFrames;
    private long _frames;
    private bool _ended, _disposed;
    private readonly bool _linkedSn;
    public VgmFile Vgm { get; }
    public WaveFormat WaveFormat { get; } = new(44100, 16, 2);
    private bool _paused;
    public bool IsPaused { get => _paused; set { if (value != _paused) { _paused = value; _hardware?.Pause(value, _frames); } } }
    public bool IsPlaying => !_ended && !_disposed;
    // 曲の終わりではReadが0を返すことで、WaveOutが送り済みの音声を出し切ってから止まる。そのためこのイベントは使わない。
    public event Action? PlaybackFinished { add { } remove { } }
    /// <summary>指定した種類の音源チップに対応しているか。</summary>
    public static bool SupportsDevice(byte type) => IndependentVgmProgram.HasChip(type);
    /// <summary>曲全体を再生できるか確かめ、できなければ理由を返す。</summary>
    public static bool Supports(VgmFile file, out string reason)
    {
        try { IndependentVgmProgram.Parse(file); reason = ""; return true; }
        catch (NotSupportedException ex) { reason = ex.Message; return false; }
    }
    /// <summary>曲を解析し、実機出力の接続と各音源チップのコアを用意する。</summary>
    public IndependentVgmWaveProvider(VgmFile file, MirrorFlux.Models.AppSettings? settings = null)
    {
        Vgm = file; _program = IndependentVgmProgram.Parse(file);
        _streams = new VgmDacStreams(_banks, WriteStream, writeWord: WriteStreamWord);
        try
        {
            // 実機出力を開き、宣言された音源チップごとにコアを作って音量を設定する。YM2608にはリズム音ROMを読み込む。
            _hardware = new VgmHardwareSession(file, _program, settings ?? new MirrorFlux.Models.AppSettings());
            byte[]? rhythm = _program.Devices.Any(d => d.Type == 7) ? Ym2608RhythmRom.Load() : null;
            foreach (var d in _program.Devices)
            {
                var chip = new VgmChip(d.Type, d.Clock, _program.Header); _chips.Add((d.Type, d.Instance), chip);
                chip.Mix(d.Volume, d.SsgVolume);
                if (d.Type == 7 && rhythm != null) chip.Memory(1, (uint)rhythm.Length, 0, rhythm);
                // YMF278B（OPL4）は波形ROM yrw801.rom を曲のフォルダー、EXEのフォルダー、romsフォルダーの順に探す。
                // 見つからず、曲がROMの音色を使うのに自前のデータを持たない場合は再生しない。
                if (d.Type == 13)
                {
                    string app = NativeAssetLibrary.ResolveApplicationDirectory(Environment.ProcessPath, AppContext.BaseDirectory);
                    var dirs = new[] { Path.GetDirectoryName(file.FilePath), app, Path.Combine(app, "roms") };
                    string? path = dirs.Where(d => !string.IsNullOrEmpty(d)).Select(d => Path.Combine(d!, "yrw801.rom")).FirstOrDefault(File.Exists);
                    if (path != null && new FileInfo(path).Length <= 64 * 1024 * 1024) { byte[] rom = File.ReadAllBytes(path); chip.Memory(3, (uint)rom.Length, 0, rom); }
                    else if (_program.Commands.Any(c => c.Type == 13 && c.Instance == d.Instance && c.Port == 2 && c.Address is >= 8 and <= 31) &&
                        !_program.Commands.Any(c => c.Type == 13 && c.Instance == d.Instance && (c.Code == 0x67 || c.Port == 2 && c.Address == 6)))
                        throw new InvalidDataException("OPL4の波形ROM yrw801.rom が見つかりません。曲またはEXEのフォルダーへ配置してください。");
                }
            }
            // SN76489のクロックのビット30・31が両方立っていれば（T6W28）、2個目を1個目につなぐ。
            _linkedSn = _program.Devices.Any(d => d.Type == 0 && (d.Clock & 0xc0000000) == 0xc0000000);
            if (_linkedSn && _chips.TryGetValue((0, 1), out var noise)) _chips[(0, 0)].Link(noise);
            // 実機で鳴らす音源チップはソフト側を全チャンネル消音する。
            foreach (var entry in _chips)
                if (_hardware.IsHardwareChip(entry.Key.Type, entry.Key.Instance)) entry.Value.SetMask(uint.MaxValue);
            // 表示用の監視を用意する。失敗したら作ったものを破棄する。
            _monitor = new VgmRegisterPlaybackMonitor(file, IsNd6Active, _program);
            _voices.Capture(_chips, true);
        }
        catch { Dispose(); throw; }
    }
    /// <summary>DACストリームの1バイトを音源チップに書き込む。RF5C68・RF5C164・HuC6280はポート指定（255以外）とレジスタ値の2回に分けて書き、SN76489はラッチ付きの書込みにする。</summary>
    private void WriteStream(byte chip, byte port, byte register, byte value)
    {
        if (!_chips.TryGetValue((chip & 0x7f, chip >> 7), out var target)) return;
        if ((chip & 0x7f) is 5 or 16 or 27)
        {
            if (port != 255) target.Write(0, (uint)(register >> 4), port);
            target.Write(0, (uint)(register & 15), value);
        }
        else if ((chip & 0x7f) == 0) target.Write(0, 0, (uint)(0x80 | (register & 0x70) | (value & 15)));
        else target.Write(port, register, value);
    }
    /// <summary>DACストリームの2バイト値を音源チップに書き込む。SN76489は2回に分け、C352とES5506はポート付きで書く。</summary>
    private void WriteStreamWord(byte chip, byte port, byte register, ushort value)
    {
        if (!_chips.TryGetValue((chip & 0x7f, chip >> 7), out var target)) return;
        if ((chip & 0x7f) == 0) { target.Write(0, 0, (uint)(0x80 | (register & 0x70) | (value & 15))); target.Write(0, 0, (uint)(value >> 4) & 63); }
        else if ((chip & 0x7f) == 39) target.Write(0, (uint)(port << 8 | register), value);
        else target.Write((chip & 0x7f) == 37 ? 258u : 0, register, value);
    }
    /// <summary>次の待ち時間まで命令を実行する。</summary>
    private void ExecuteToWait()
    {
        byte[] bytes = _program.Data;
        // 解析時に待ち時間のないループを拒否しているが、音声スレッドを止めないための保険として、待ちなしでループを2周したら再生を終える。
        bool looped = false;
        while (_wait == 0 && !_ended)
        {
            var c = _program.Commands[_index++]; int p = c.Offset;
            switch (c.Code)
            {
                // 0x61〜0x64・0x70〜0x8F: 待ち時間。0x8nはYM2612のDACへPCMバンクの次の1バイトも書く。
                case >= 0x61 and <= 0x64 or >= 0x70 and <= 0x8f:
                    if (c.Code >= 0x80)
                    {
                        var pcm = _banks.Get(0)?.Data;
                        if (pcm == null || _pcmCursor >= pcm.Count) throw new InvalidDataException("VGM PCMデータ範囲が不正です。");
                        _chips[(2, 0)].Write(0, 0x2a, pcm[_pcmCursor++]);
                    }
                    _waits.TryWait(bytes.AsSpan(p, c.Length), out _wait); break;
                // 0xE0: 旧式DACのデータ位置を移す。
                case 0xe0: _pcmCursor = (int)IndependentVgmProgram.U32(bytes, p + 1); break;
                // 0x67: データブロック。0x00〜0x7FはPCMバンクへ、それ以外は音源チップのROM・RAMへ読み込む。
                case 0x67:
                    byte kind = bytes[p + 2];
                    if (kind <= 0x7f) _banks.Load(kind, bytes.AsSpan(p + 7, c.Length - 7), p + 7);
                    else if (c.Type >= 0)
                    {
                        // ROMは先頭8バイトが全体の大きさと開始アドレス、RAMは2バイトか4バイトの開始アドレス。種類0x87は0x200000以降に置く。
                        int metadata = kind < 0xc0 ? 8 : kind < 0xe0 ? 2 : 4;
                        uint address = kind < 0xc0 ? IndependentVgmProgram.U32(bytes, p + 11) : kind < 0xe0 ? BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(p + 7)) : IndependentVgmProgram.U32(bytes, p + 7);
                        uint size = kind < 0xc0 ? IndependentVgmProgram.U32(bytes, p + 7) : Math.Max(0x10000u, address + (uint)(c.Length - 7 - metadata));
                        if (kind == 0x87) address += 0x200000;
                        if (kind == 0x87) size += 0x200000;
                        _chips[(c.Type, c.Instance)].Memory(c.Port, size, address, bytes.AsSpan(p + 7 + metadata, c.Length - 7 - metadata).ToArray());
                    }
                    break;
                // 0x68: PCMバンクから音源チップのRAMへ転送する。
                case 0x68:
                    if (c.Type >= 0)
                    {
                        int length = (int)VgmCommandDecoder.U24(bytes, p + 9); if (length == 0) length = 0x1000000;
                        int source = (int)VgmCommandDecoder.U24(bytes, p + 3); uint address = VgmCommandDecoder.U24(bytes, p + 6);
                        _chips[(c.Type, c.Instance)].Memory(0, Math.Max(0x10000, address + (uint)length), address, _banks.Get((byte)(bytes[p + 2] & 0x7f))!.Bytes.Slice(source, length).ToArray());
                    }
                    break;
                // 0x90〜0x95: DACストリームの制御。
                case >= 0x90 and <= 0x95: _streams.Command(bytes.AsSpan(p, c.Length)); break;
                // 0x66: 終了。ループがあればループ位置へ戻る。
                case 0x66:
                    if (_program.LoopIndex >= 0 && !looped) { _index = _program.LoopIndex; _loops++; looped = true; }
                    else _ended = true;
                    break;
                // それ以外のレジスタ書込みは実機とソフト側の音源チップの両方へ送る。
                default: if (c.Type >= 0) { _hardware?.Write(c, _frames); _chips[(c.Type, c.Instance)].Write(c.Port, c.Address, c.Value); } break;
            }
        }
    }
    /// <summary>次の待ち時間までの命令を実行しながら、各音源チップの音を合成して16ビットステレオで書き出す。</summary>
    public int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (offset < 0 || count < 0 || offset > buffer.Length - count) throw new ArgumentOutOfRangeException(nameof(count));
        lock (_sync)
        {
            // 終了後は0を返し、一時停止中は無音を返す。
            if (!IsPlaying) return 0;
            int capacity = count / 4;
            if (capacity == 0) return 0;
            if (IsPaused) { Array.Clear(buffer, offset, capacity * 4); return capacity * 4; }
            int written = 0;
            while (written < capacity)
            {
                // 命令を実行し、DACストリーム、待ち時間、バッファの残りのうち一番近いところまでを1回の合成単位にする。T6W28のときは1フレームずつ進める。
                ExecuteToWait(); if (_ended) break;
                _streams.EmitDue();
                int frames = Math.Min(_streams.FramesUntilWrite(), Math.Min(_linkedSn ? 1 : 64, Math.Min(_wait, capacity - written)));
                // 各音源チップを合成して足し合わせる。FM音源チップはコア側で音量を掛けているので、ここでは掛けない。
                Array.Clear(_accumulator, 0, frames * 2);
                foreach (var device in _program.Devices)
                {
                    var chip = _chips[(device.Type, device.Instance)]; chip.Render(_chipAudio, frames);
                    double gain = device.Type is 1 or 2 or 3 or >= 6 and <= 13 ? 1 : device.Volume;
                    for (int i = 0; i < frames * 2; i++) _accumulator[i] += (long)(_chipAudio[i] * gain);
                }
                // 幅の広い整数で足し合わせてから最後に飽和させる。VGM全体の音量補正は1回だけ掛ける。
                for (int i = 0; i < frames * 2; i++)
                {
                    int sample = (int)Math.Clamp(_accumulator[i] * _program.Gain, short.MinValue, short.MaxValue);
                    _mix[i] = sample;
                    BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(offset + written * 4 + i * 2), (short)sample);
                }
                // スペクトラム、時間、DACストリーム、表示、実機出力、音高の時間軸を進める。
                _spectrum.AddSamples(_mix.AsSpan(0, frames * 2));
                _wait -= frames; _frames += frames; written += frames;
                _streams.Advance(frames);
                _monitor?.Advance(frames);
                _hardware?.AfterAdvance(_monitor);
                UpdateFmPitchesAndTimeline();
                _voiceFrames += frames;
                if (_voiceFrames >= 256) { _voices.Capture(_chips, IsPlaying, _voiceFrames); _voiceFrames = 0; }
                if (_frames - _lastSnapshotFrame >= SnapshotInterval) RememberSnapshot();
            }
            // 呼び出し側のバッファ末尾と重なっても、曲の終わりとループ位置を正しく検出できるように命令を進めておく。
            if (_wait == 0 && !_ended) ExecuteToWait();
            if (_voiceFrames > 0) { _voices.Capture(_chips, IsPlaying, _voiceFrames); _voiceFrames = 0; }
            return written * 4;
        }
    }
    /// <summary>Span版のRead。一時配列を借りて読み込む。</summary>
    public int Read(Span<byte> buffer)
    {
        byte[] temporary = System.Buffers.ArrayPool<byte>.Shared.Rent(buffer.Length);
        try { int count = Read(temporary, 0, buffer.Length); temporary.AsSpan(0, count).CopyTo(buffer); return count; }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(temporary); }
    }
    /// <summary>表示中のチャンネルに対応する音源チップのチャンネルをミュートする。実機で鳴らしている音源チップは対象外。</summary>
    public bool SetChannelMuted(MusicChannelSnapshot channel, bool muted)
    {
        lock (_sync)
        {
            if (_disposed) return false;
            int deviceIndex = _program.Devices.FindIndex(d => d.Name.Equals(channel.ChipName, StringComparison.OrdinalIgnoreCase) || d.Type == 20 && channel.ChipName == d.Name.Replace("APU", "FDS"));
            if (deviceIndex < 0) return false;
            var device = _program.Devices[deviceIndex];
            if (!_chips.TryGetValue((device.Type, device.Instance), out var chip)) return false;
            // チャンネルIDの末尾から、コア側のチャンネル番号を決める。SSGは24番、リズム・ADPCMは6番から始まる。
            string[] parts = channel.Id.Split(':');
            int bit;
            if (parts[^1] == "MIX") { chip.SetMask(muted ? uint.MaxValue : 0); return true; }
            if (parts[^1] == "DAC") bit = 6;
            else if (parts[^1] == "ADPCM-B") bit = 12;
            else if (int.TryParse(parts[^1], out bit))
            {
                if (parts.Length == 3) bit += parts[1] switch { "SSG" => 24, "RHYTHM" or "ADPCM" => 6, _ => 0 };
            }
            else return false;
            if (channel.ChipName.StartsWith("NES FDS", StringComparison.Ordinal)) bit = 5;
            if (_hardware?.IsHardwareChip(device.Type, device.Instance) == true) return false;
            return chip.SetChannel(bit, muted);
        }
    }
    /// <summary>LFOで揺れているFMチャンネルの音高を、コアから得た実際の音高に置き換える。レジスタ上の音高から離れすぎる場合は置き換えない。</summary>
    private static MusicChannelSnapshot ApplyFmPitch(MusicChannelSnapshot ch, IReadOnlyDictionary<string, double> fmPitches)
    {
        if (ch.Kind != MusicChannelKind.Fm || !ch.HasHardwarePitchLfo || !fmPitches.TryGetValue(ch.Id, out double fmPitch))
            return ch;
        if (MusicPitch.FromFrequency(ch.Frequency) is double regPitch && Math.Abs(fmPitch - regPitch) > 0.85)
            return ch;
        return ch with { Pitch = fmPitch };
    }

    /// <summary>FM音源チップの位相の増分から実際の音高を求め、表示用の音高の時間軸に記録する。</summary>
    private void UpdateFmPitchesAndTimeline()
    {
        foreach (var device in _program.Devices)
        {
            if (device.Type is not (1 or 2 or 3 or >= 6 and <= 13)) continue;
            var chip = _chips[(device.Type, device.Instance)];
            uint fmDiv = chip.FmPhaseSteps(_fmSteps);
            int maxCh = device.Type switch
            {
                3 => 8,
                2 or 7 or 8 => 6,
                6 => 3,
                12 or 13 => 18,
                _ => 9
            };
            for (int ch = 0; ch < maxCh; ch++)
            {
                string id = $"{device.Name}:FM:{ch}";
                uint step = _fmSteps[ch];
                double? pitch = device.Type == 3
                    ? MusicPitch.FromOpmPhaseStep(step, chip.Clock)
                    : MusicPitch.FromFmPhaseStep(step, chip.Clock, fmDiv);
                if (pitch is double p && double.IsFinite(p)) _fmPitches[id] = p;
                else _fmPitches.Remove(id);
            }
        }
        // 表示用チャンネルごとの状態を時間軸に記録する。キーオン直後はLFOの音高を使わない。
        if (_monitor == null) return;
        var latest = _monitor.LatestChannels;
        if (latest.Length == 0) return;
        // 状態が変わっていなければ、一定間隔ごとにだけ記録する（LFOの音高はこの間隔で追う）。
        if (ReferenceEquals(latest, _lastRecordedChannels) && _frames - _lastTimelineFrame < TimelineRecordInterval) return;
        _lastRecordedChannels = latest;
        _lastTimelineFrame = _frames;
        int count = Math.Min(latest.Length, PitchTimeline.MaxChannels);
        Span<ChannelRealtimeState> states = stackalloc ChannelRealtimeState[ count];
        for (int i = 0; i < count; i++)
        {
            var rawCh = latest[i];
            bool isKeyOnAttack = rawCh.IsKeyOn && (!_wasVgmKeyOn[i] || rawCh.KeyGeneration != _lastVgmKeyGen[i]);
            _wasVgmKeyOn[i] = rawCh.IsKeyOn;
            _lastVgmKeyGen[i] = rawCh.KeyGeneration;
            var ch = isKeyOnAttack ? rawCh : ApplyFmPitch(rawCh, _fmPitches);
            double? dispPitch = ch.IsKeyOn ? MusicPitch.Display(ch) : null;
            float pitch = dispPitch.HasValue ? (float)dispPitch.Value : float.NaN;
            states[i] = new ChannelRealtimeState(
                pitch,
                (float)Math.Clamp(ch.Level, 0.0, 1.0),
                ch.NoteNumber,
                ch.IsKeyOn,
                ch.IsInUse,
                ch.IsHardwareOutput,
                ch.IsNoteEstimated,
                (ushort)ch.KeyGeneration);
        }
        Timeline.RecordFast(_frames, states);
    }

    /// <summary>いま合成し終えた位置の表示用スナップショット。</summary>
    public VgmVisualSnapshot GetSnapshot()
    {
        lock (_sync) return BuildSnapshot();
    }

    /// <summary>
    /// 指定した再生位置（実際に聞こえているサンプル位置）の表示用スナップショット。
    /// 合成は音声出力のバッファの分だけ先に進んでいるので、履歴からその位置以前で一番新しいものを返す。
    /// 履歴がない、または停止中は、いまの状態を返す。
    /// </summary>
    public VgmVisualSnapshot GetSnapshot(double playbackSample)
    {
        lock (_sync)
        {
            if (_snapshotCount == 0 || !IsPlaying) return BuildSnapshot();
            VgmVisualSnapshot? chosen = null, oldest = null;
            long chosenFrame = long.MinValue, oldestFrame = long.MaxValue;
            for (int i = 0; i < _snapshotCount; i++)
            {
                var (frame, snapshot) = _snapshots[i];
                if (frame <= playbackSample && frame > chosenFrame) { chosen = snapshot; chosenFrame = frame; }
                if (frame < oldestFrame) { oldest = snapshot; oldestFrame = frame; }
            }
            var result = chosen ?? oldest!;
            result.IsPaused = IsPaused;
            result.SpectrumLevels = _spectrum.Capture();
            return result;
        }
    }

    /// <summary>今の位置のスナップショットを履歴に残す（音声スレッドから、_syncの中で呼ぶ）。</summary>
    private void RememberSnapshot()
    {
        _lastSnapshotFrame = _frames;
        _snapshots[_snapshotNext] = (_frames, BuildSnapshot(captureSpectrum: false));
        _snapshotNext = (_snapshotNext + 1) % SnapshotHistory;
        if (_snapshotCount < SnapshotHistory) _snapshotCount++;
    }

    /// <summary>表示用のスナップショットを作る。レジスタ監視とネイティブの発音情報をまとめ、実機出力やFMコアの表示名を付ける。</summary>
    private VgmVisualSnapshot BuildSnapshot(bool captureSpectrum = true)
    {
        {
            var snapshot = _monitor?.GetSnapshot() ?? new VgmVisualSnapshot();
            // ネイティブの発音情報がある音源チップはそちらを使い、それ以外はレジスタ監視の結果を使う。実機で鳴らしているチャンネルには印を付ける。
            var channels = _voices.Channels;
            var nativeNames = channels.Select(c => c.ChipName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            snapshot.MusicChannels = [.. snapshot.MusicChannels.Where(c => !nativeNames.Contains(c.ChipName)).Select(c => ApplyFmPitch(c, _fmPitches)), .. channels];
            snapshot.MusicChannels = snapshot.MusicChannels.Select(c =>
                _program.Devices.Any(d => _hardware?.IsHardwareChip(d.Type, d.Instance) == true &&
                    (c.ChipName.Equals(d.Name, StringComparison.OrdinalIgnoreCase) ||
                     c.ChipName.Equals(d.Name.Split(" #")[0], StringComparison.OrdinalIgnoreCase)))
                    ? c with { IsHardwareOutput = true } : c).ToArray();
            snapshot.IsPlaying = IsPlaying; snapshot.IsPaused = IsPaused;
            snapshot.CurrentTime = TimeSpan.FromSeconds(_frames / 44100.0);
            snapshot.LoopCount = _loops;
            var routes = _program.Devices.Select(d => (d.Type, _hardware?.HardwareName(d.Type, d.Instance))).ToArray();
            snapshot.FmCoreName = FmCoreName(routes);
            snapshot.ExtDeviceName = ExtDeviceName(routes.Select(r => r.Item2));
            // チャンネル情報がない音源チップは、まとめて1個のMIXチャンネルとして出す。
            foreach (var device in _program.Devices)
                if (!snapshot.MusicChannels.Any(c => c.ChipName.Equals(device.Name, StringComparison.OrdinalIgnoreCase)))
                    snapshot.MusicChannels = [..snapshot.MusicChannels,new MusicChannelSnapshot {
                        Id=device.Name+":MIX",ChipName=device.Name,Label="MIX",Kind=MusicChannelKind.Other,IsInUse=true,
                        DataString="Audio core available; per-channel telemetry unavailable" }];
            // 停止中は発音とレベルを消す。
            if (!IsPlaying)
            {
                snapshot.MusicChannels = snapshot.MusicChannels.Select(c => c with { IsKeyOn = false, Level = 0 }).ToArray();
                foreach (var c in snapshot.OpmChannels) { c.IsKeyOn = false; c.Level = 0; }
                foreach (var c in snapshot.PcmChannels) { c.IsActive = false; c.Level = 0; }
            }
            if (captureSpectrum) snapshot.SpectrumLevels = _spectrum.Capture();
            snapshot.SpectrumKind = "VGM AUDIO OUTPUT";
            return snapshot;
        }
    }
    /// <summary>
    /// FM CORE欄の表示。ソフトで鳴らしているヤマハのFM音源チップだけを対象に、実装名を重複なく並べる。
    /// ymfmの11種はymfm、YMF271はmf_chips。実機へ出力している音源チップと、PSG・PCMなどFM以外の音源チップは含めない。
    /// 該当がなければ「—」。実機はEXT DEVICE欄（ExtDeviceName）に出す。
    /// </summary>
    internal static string FmCoreName(IEnumerable<(int Type, string? Hardware)> devices)
    {
        var names = devices
            .Where(d => d.Hardware == null && d.Type is 1 or 2 or 3 or >= 6 and <= 14)
            .Select(d => d.Type == 14 ? "mf_chips" : "ymfm")
            .Distinct(StringComparer.Ordinal).ToArray();
        return names.Length == 0 ? "—" : string.Join(" + ", names);
    }

    /// <summary>
    /// EXT DEVICE欄の表示。この曲で何か1つでも実機へ出力している基板を、ND6・ND7・ND8の短い名前で番号順に並べる。
    /// FM以外（ND6のSN76489、ND7のAY8910、ND8のOKIM6258）だけを出力している基板も含む。使っていなければ「—」。
    /// </summary>
    internal static string ExtDeviceName(IEnumerable<string?> boards)
    {
        var names = boards
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => new string(name!.Where(char.IsAsciiDigit).ToArray()))
            .Where(number => number.Length != 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(number => int.Parse(number, System.Globalization.CultureInfo.InvariantCulture))
            .Select(number => "ND" + number).ToArray();
        return names.Length == 0 ? "—" : string.Join(" ", names);
    }

    /// <summary>実機出力、音源チップのコア、表示用の監視を破棄する。</summary>
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return; _disposed = true;
            _hardware?.Dispose(); _hardware = null;
            foreach (var chip in _chips.Values) chip.Dispose();
            _chips.Clear(); _monitor?.Dispose(); _monitor = null;
        }
    }
}
