using System.IO;
using System.Runtime.InteropServices;
using NAudio.Wave;

namespace MirrorFlux.Services.Music;

/// <summary>
/// Game Music Emu で NSF・SPC などを鳴らす。
/// 聞こえる音とは別に、各音を単独で鳴らすエミュレーターを音ごとに持ち、その音から鍵盤の音程と波形を推定する。
/// </summary>
public sealed class GmeWaveProvider : IPlaybackWaveProvider
{
    private const int Rate = 44100, VoiceRate = 8000, Window = 256;
    private readonly KeyboardActivity _keyboardActivity = new();
    private readonly int _voiceRate;
    private readonly object _sync = new();
    private IntPtr _main;
    private readonly List<IntPtr> _voices = [];
    private readonly VoiceDisplay[] _voiceDisplays;
    private readonly bool _hasFds;
    private readonly bool _hasVrc6;
    private readonly bool _hasVrc7;
    private readonly float[][] _history;
    private readonly int[] _cursor;
    private readonly byte[]?[] _waveforms;
    private AudioSpectrumAnalyzer _spectrum = new();
    private long _frames, _voiceFrames;
    private short[] _samples = [], _voiceSamples = [];
    private int[] _spectrumSamples = [];
    private MusicChannelSnapshot[] _channels = [];
    private long _lastAnalysis;
    /// <summary>音ごとの表示の仕方（ID・音源名・ラベル・種類・ノイズか・波形を取るか・矩形波か・推定音程か・波形のラベル）。</summary>
    private readonly record struct VoiceDisplay(string Id, string ChipName, string Label, MusicChannelKind Kind,
        bool IsNoise, bool CaptureWaveform, bool IsSquare, bool IsNoteEstimated, string WaveformLabel);
    public WaveFormat WaveFormat { get; } = new(Rate, 16, 2);
    public int TrackCount { get; }
    public int TrackIndex { get; private set; }
    public string FormatName { get; }
    public string Title { get; }
    public bool IsPaused { get; set; }
    public bool Ended { get; private set; }

    /// <summary>ファイルを開き、聞こえる音用のエミュレーターと、音ごとに単独で鳴らすエミュレーターを用意する。</summary>
    public GmeWaveProvider(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        FormatName = Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
        // SPC は音ごとのエミュレーションのブロック時刻を聞こえる音と同じにするため、44.1 kHz で鳴らす。
        _voiceRate = FormatName == "SPC" ? Rate : VoiceRate;
        Title = Path.GetFileNameWithoutExtension(path);
        // 聞こえる音用を開き、音の名前から FDS・VRC6・VRC7 の有無を調べる。
        try
        {
            GmeNative.Check(GmeNative.gme_open_data(data, data.Length, out _main, Rate));
            TrackCount = GmeNative.gme_track_count(_main);
            int count = Math.Min(32, GmeNative.gme_voice_count(_main));
            string[] names = Enumerable.Range(0, count).Select(i => Marshal.PtrToStringAnsi(GmeNative.gme_voice_name(_main, i)) ?? $"CH {i + 1}").ToArray();
            _hasFds = FormatName is "NSF" or "NSFE" &&
                names.Any(name => string.Equals(name, "Wave", StringComparison.OrdinalIgnoreCase));
            _hasVrc6 = (FormatName is "NSF" or "NSFE") &&
                names.Any(name => string.Equals(name, "Saw Wave", StringComparison.OrdinalIgnoreCase));
            _hasVrc7 = (FormatName is "NSF" or "NSFE") &&
                names.Any(name => string.Equals(name, "FM 1", StringComparison.OrdinalIgnoreCase));
            _voiceDisplays = names.Select((name, index) => DescribeVoice(name, index)).ToArray();
            _history = Enumerable.Range(0, count).Select(_ => new float[Window]).ToArray();
            _cursor = new int[count];
            _waveforms = new byte[]?[count];
            // 音ごとに、他の音を消したエミュレーターを開く。
            for (int i = 0; i < count; i++)
            {
                GmeNative.Check(GmeNative.gme_open_data(data, data.Length, out var voice, _voiceRate));
                _voices.Add(voice);
                GmeNative.gme_mute_voices(voice, ~(1 << i));
            }
            // ファイルに書かれた最初のトラックから始める。
            int initial = FormatName == "NSF" && data.Length > 7 ? data[7] - 1 : FormatName == "HES" && data.Length > 5 ? data[5] : FormatName == "GBS" && data.Length > 5 ? data[5] - 1 : 0;
            SelectTrack(Math.Clamp(initial, 0, Math.Max(0, TrackCount - 1)));
        }
        catch { Dispose(); throw; }
    }
    /// <summary>音の名前から、鍵盤とチャンネルの表示の仕方を決める。</summary>
    private VoiceDisplay DescribeVoice(string name, int index)
    {
        bool noise = name.Contains("noise", StringComparison.OrdinalIgnoreCase) || name.Contains("DMC", StringComparison.OrdinalIgnoreCase);
        bool fds = _hasFds && name.Equals("Wave", StringComparison.OrdinalIgnoreCase);
        bool square = (FormatName is "NSF" or "NSFE") && (name is "Square 1" or "Square 2");
        bool vrc6Pulse = _hasVrc6 && (name is "Square 3" or "Square 4");
        bool vrc6Saw = _hasVrc6 && name.Equals("Saw Wave", StringComparison.OrdinalIgnoreCase);
        bool vrc7 = _hasVrc7 && name.StartsWith("FM ", StringComparison.OrdinalIgnoreCase);
        return new($"{FormatName}:{index}",
            fds ? "NES FDS" : vrc6Pulse || vrc6Saw ? "NES VRC6" : vrc7 ? "NES VRC7" : $"{FormatName} {name}",
            fds ? "FDS" : vrc6Pulse ? name == "Square 3" ? "PULSE1" : "PULSE2" : vrc6Saw ? "SAW" : name,
            vrc7 ? MusicChannelKind.Fm : fds ? MusicChannelKind.Wavetable : noise ? MusicChannelKind.Noise : FormatName == "SPC" ? MusicChannelKind.Pcm : MusicChannelKind.Psg,
            noise, fds || square || vrc6Pulse || vrc6Saw, square, vrc6Pulse || vrc6Saw || vrc7,
            vrc6Pulse || vrc6Saw ? "Waveform : " : "Wavetable : ");
    }
    private int _muteMask;
    /// <summary>
    /// チャンネルの消音を切り替える。
    /// 聞こえる音用のエミュレーターだけに反映する。
    /// </summary>
    public bool SetChannelMuted(MusicChannelSnapshot channel, bool muted)
    {
        lock (_sync)
        {
            if (_main == IntPtr.Zero || !channel.Id.StartsWith(FormatName + ":", StringComparison.Ordinal)
                || !int.TryParse(channel.Id.AsSpan(FormatName.Length + 1), out int voice) || (uint)voice >= _voices.Count) return false;
            int flag = 1 << voice;
            _muteMask = muted ? _muteMask | flag : _muteMask & ~flag;
            GmeNative.gme_mute_voices(_main, _muteMask);
            return true;
        }
    }
    /// <summary>
    /// トラックを切り替える。
    /// すべてのエミュレーターを同じトラックの先頭から始め、表示の記録も消す。
    /// </summary>
    public bool SelectTrack(int index)
    {
        lock (_sync)
        {
            if (_main == IntPtr.Zero || index < 0 || index >= TrackCount) return false;
            // 単独の音を聞こえる音と同じ時間軸に保つため、無音の検出を止める。SPC の長さタグは当てにならず、この DLL には autoload の上限を変える関数がないので、フェードは6時間後（GME の32ビットのサンプル数に収まる範囲）まで延ばす。フェード開始を負にすると GME 0.6.3 では無音の先読みが有効になってしまうので使わない。
            foreach (var emu in _voices.Prepend(_main))
            {
                GmeNative.gme_ignore_silence(emu, 1);
                GmeNative.Check(GmeNative.gme_start_track(emu, index));
                GmeNative.gme_set_fade(emu, 6 * 60 * 60 * 1000);
            }
            _muteMask = 0; GmeNative.gme_mute_voices(_main, 0);
            TrackIndex = index; _frames = _voiceFrames = _lastAnalysis = 0; Ended = false;
            foreach (var history in _history) Array.Clear(history);
            Array.Clear(_cursor); Array.Clear(_waveforms); _channels = []; _spectrum = new(); _keyboardActivity.Clear();
            return true;
        }
    }
    /// <summary>指定バイト数を、220フレームずつに分けて読む。</summary>
    public int Read(byte[] buffer, int offset, int count)
    {
        int read = 0;
        while (read < count)
        {
            int size = ReadBlock(buffer, offset + read, Math.Min(220 * 4, count - read));
            if (size == 0) break;
            read += size;
        }
        return read;
    }
    /// <summary>
    /// 1塊分を鳴らす。
    /// 聞こえる音を作り、同じ時間分だけ音ごとのエミュレーターも進めて履歴に残す。
    /// </summary>
    private int ReadBlock(byte[] buffer, int offset, int count)
    {
        lock (_sync)
        {
            if (_main == IntPtr.Zero || Ended) return 0;
            if (IsPaused) { Array.Clear(buffer, offset, count); return count; }
            int frames = count / 4;
            if (_samples.Length < frames * 2) { _samples = new short[frames * 2]; _spectrumSamples = new int[frames * 2]; }
            // 聞こえる音を作り、スペクトラムにも渡す。
            try { GmeNative.Check(GmeNative.gme_play(_main, frames * 2, _samples)); }
            catch (Exception ex) { throw new InvalidDataException($"{FormatName} main audio at frame {_frames}", ex); }
            Buffer.BlockCopy(_samples, 0, buffer, offset, frames * 4);
            for (int i = 0; i < frames * 2; i++) _spectrumSamples[i] = _samples[i];
            _spectrum.AddSamples(_spectrumSamples.AsSpan(0, frames * 2));
            _frames += frames;
            // 音ごとのエミュレーターを、聞こえる音と同じ時刻まで進める。
            long target = _frames * _voiceRate / Rate;
            int voiceCount = (int)(target - _voiceFrames);
            if (_voiceSamples.Length < voiceCount * 2) _voiceSamples = new short[voiceCount * 2];
            for (int i = 0; i < _voices.Count; i++)
            {
                if (voiceCount > 0)
                {
                    try { GmeNative.Check(GmeNative.gme_play(_voices[i], voiceCount * 2, _voiceSamples)); }
                    catch (Exception ex) { throw new InvalidDataException($"{FormatName} voice {i} at frame {_voiceFrames}", ex); }
                }
                for (int f = 0; f < voiceCount; f++)
                {
                    // 表示用の 8 kHz に間引いて履歴に足す。左右の大きいほうを使い、片側に振った音も位相の打ち消しなしで残す。
                    if ((_voiceFrames + f + 1) * VoiceRate / _voiceRate == (_voiceFrames + f) * VoiceRate / _voiceRate) continue;
                    short l = _voiceSamples[f * 2], r = _voiceSamples[f * 2 + 1];
                    _history[i][_cursor[i]] = (Math.Abs((int)l) >= Math.Abs((int)r) ? l : r) / 32768f;
                    _cursor[i] = (_cursor[i] + 1) % Window;
                }
            }
            // 約10msごとに解析し、曲の終わりを確かめる。
            _voiceFrames = target;
            if (target - _lastAnalysis >= _voiceRate / 100) { Analyze(); _lastAnalysis = target; }
            Ended = GmeNative.gme_track_ended(_main) != 0;
            if (Ended) GmeNative.Check(GmeNative.gme_warning(_main));
            return frames * 4;
        }
    }
    /// <summary>
    /// Span 版の Read。
    /// 借りた配列で読んでから写す。
    /// </summary>
    public int Read(Span<byte> buffer)
    {
        byte[] rented = System.Buffers.ArrayPool<byte>.Shared.Rent(buffer.Length);
        try { int read = Read(rented, 0, buffer.Length); rented.AsSpan(0, read).CopyTo(buffer); return read; }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(rented); }
    }
    /// <summary>音ごとの履歴から、レベル・推定音程・波形を求めてチャンネルの状態を作る。</summary>
    private void Analyze()
    {
        var channels = new MusicChannelSnapshot[_voices.Count];
        var wave = new double[Window];
        for (int voice = 0; voice < channels.Length; voice++)
        {
            VoiceDisplay display = _voiceDisplays[voice];
            // 直流分を除いて RMS を求め、音があればノイズ以外の音程を推定する。
            double mean = _history[voice].Average(v => (double)v), energy = 0;
            for (int i = 0; i < Window; i++) { wave[i] = _history[voice][(_cursor[voice] + i) % Window] - mean; energy += wave[i] * wave[i]; }
            double rms = Math.Sqrt(energy / Window);
            int? note = !display.IsNoise && rms > .001 ? AudioPitchDetector.Estimate(wave, VoiceRate, .85) : null;
            // 波形を取る音は単独の音から波形を作る。矩形波は高い側の点の数からデューティを決める。
            if (rms > .001 && display.CaptureWaveform)
                _waveforms[voice] = NesWaveform.FromIsolatedAudio(wave, display.IsSquare) ?? _waveforms[voice];
            int highSamples = display.IsSquare && _waveforms[voice] != null ? _waveforms[voice]!.Count(value => value > 127) : 0;
            int duty = highSamples switch { 4 => 0, 8 => 1, 16 => 2, 24 => 3, _ => 2 };
            channels[voice] = new() { Id = display.Id,
                ChipName = display.ChipName, Label = display.Label, Kind = display.Kind,
                NoteNumber = note, IsNoteEstimated = note.HasValue && display.IsNoteEstimated, IsKeyOn = rms > .001, IsInUse = true,
                Level = Math.Clamp(rms * 6, 0, 1), Pan = 3, DataString = note.HasValue ? "ESTIMATED PITCH" : "ACTIVITY",
                Waveform = _waveforms[voice], WaveformLabel = display.IsSquare ? NesWaveform.DutyLabel(duty) : display.WaveformLabel };
        }
        _channels = channels;
        _keyboardActivity.Record(channels);
    }
    /// <summary>
    /// 表示用の状態を返す。
    /// 形式ごとの名前と音源名を付ける。
    /// </summary>
    public MusicPlaybackSnapshot Snapshot()
    {
        lock (_sync) return new() { IsPlaying = _main != IntPtr.Zero && !Ended, IsPaused = IsPaused,
            Title = TrackCount > 1 ? $"{Title} [{TrackIndex + 1}/{TrackCount}]" : Title, FormatName = FormatName,
            FileTypeDisplay = FormatName switch
            {
                "NSF" => "NSF (NES Sound Format)",
                "NSFE" => "NSFE (Extended NES)",
                "HES" => "HES (PC Engine / HuC6280)",
                "SPC" => "SPC (Super NES / SPC700)",
                "GBS" => "GBS (Game Boy Sound)",
                "AY" => "AY (ZX Spectrum)",
                "GYM" => "GYM (Sega Mega Drive / Genesis)",
                "KSS" => "KSS (MSX Sound)",
                "SAP" => "SAP (Atari XL / POKEY)",
                _ => $"{FormatName} Sound File"
            },
            SoundSourceDisplay = FormatName switch
            {
                "NSF" or "NSFE" => "NES APU (2A03)" + (_hasFds ? " + FDS" : "") +
                    (_hasVrc6 ? " + VRC6" : "") + (_hasVrc7 ? " + VRC7" : ""),
                "HES" => "HuC6280 (PSG)",
                "SPC" => "SPC700 / S-DSP",
                "GBS" => "Game Boy DMG",
                "AY" => "AY-3-8910",
                "GYM" => "YM2612 + SN76489",
                "KSS" => "PSG + SCC + OPLL",
                "SAP" => "Atari POKEY",
                _ => FormatName
            },
            CurrentTime = TimeSpan.FromSeconds(_frames / (double)Rate), Channels = _keyboardActivity.Apply(_channels),
            SpectrumLevels = _spectrum.Capture(), SpectrumKind = "AUDIO FFT / ESTIMATED NOTES", TrackIndex = TrackIndex, TrackCount = TrackCount };
    }
    /// <summary>すべてのエミュレーターを閉じる。</summary>
    public void Dispose()
    {
        lock (_sync)
        {
            foreach (var voice in _voices) GmeNative.gme_delete(voice);
            _voices.Clear();
            if (_main != IntPtr.Zero) { GmeNative.gme_delete(_main); _main = IntPtr.Zero; }
        }
    }
}
