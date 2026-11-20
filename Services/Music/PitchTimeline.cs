using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.Wave;

namespace MirrorFlux.Services.Music;

/// <summary>音声スレッドからUIスレッドへロックフリーで渡す1チャンネル分のリアルタイム音程・キー状態。</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct ChannelRealtimeState
{
    /// <summary>連続ピッチ（MIDIノート番号の float、無効なときは float.NaN）。</summary>
    public readonly float Pitch;
    /// <summary>レベルメーターの値（0〜1）。</summary>
    public readonly float Level;
    /// <summary>鍵盤を光らせる整数ノート（-1 = なし）。</summary>
    public readonly sbyte NoteNumber;
    /// <summary>
    /// bit0: IsKeyOn、bit1: IsInUse、bit2: IsHardwareOutput、bit3: IsNoteEstimated。
    /// </summary>
    public readonly byte Flags;
    /// <summary>
    /// ノートの発音世代。
    /// 別のノートが鳴ったときに補間をやり直すために使う。
    /// </summary>
    public readonly ushort KeyGen;

    public bool IsKeyOn => (Flags & 1) != 0;
    public bool IsInUse => (Flags & 2) != 0;
    public bool IsHardwareOutput => (Flags & 4) != 0;
    public bool IsNoteEstimated => (Flags & 8) != 0;

    public ChannelRealtimeState(
        float pitch,
        float level,
        int? noteNumber,
        bool isKeyOn,
        bool isInUse,
        bool isHardwareOutput,
        bool isNoteEstimated,
        ushort keyGen)
    {
        Pitch = pitch;
        Level = level;
        NoteNumber = noteNumber is >= 0 and <= 127 ? (sbyte)noteNumber.Value : (sbyte)-1;
        Flags = (byte)((isKeyOn ? 1 : 0) | (isInUse ? 2 : 0) | (isHardwareOutput ? 4 : 0) | (isNoteEstimated ? 8 : 0));
        KeyGen = keyGen;
    }
}

/// <summary>
/// 音声スレッドがサンプル位置付きで書き込むロックフリー（SeqLock + ダブルバッファ）の音程履歴キュー。
/// UIスレッドは再生済みサンプル位置（WaveOut出力位置）で過去の状態を引き、前後フレーム間を線形補間する。
/// 鍵盤とレベルメーターを実際に聞こえている時刻に合わせるためのもの。
/// </summary>
public sealed class PitchTimeline
{
    public const int MaxChannels = 64;
    // 64サンプル刻みで約1.5秒分の履歴を持つ。
    private const int Capacity = 1024;
    private const int Mask = Capacity - 1;

    /// <summary>
    /// リングバッファの1コマ。
    /// Sequence が奇数なら書き込み中、偶数なら確定済み。
    /// </summary>
    private sealed class FrameSlot
    {
        public long Sequence;
        public long SamplePosition;
        public int ChannelCount;
        public readonly ChannelRealtimeState[] Channels = new ChannelRealtimeState[MaxChannels];
    }

    private readonly FrameSlot[] _ring = new FrameSlot[Capacity];
    private readonly ChannelRealtimeState[][] _doubleBuffer =
    [
        new ChannelRealtimeState[MaxChannels],
        new ChannelRealtimeState[MaxChannels]
    ];
    private readonly int[] _doubleBufferCounts = new int[2];
    private readonly long[] _doubleBufferSamples = new long[2];
    private int _activeBufferIndex;
    private long _writeCount;
    private long _latestSamplePosition;

    // チャンネルごとの前の状態（発音世代の追跡用、音声スレッド専用）。
    private readonly bool[] _wasKeyOn = new bool[MaxChannels];
    private readonly sbyte[] _lastNote = new sbyte[MaxChannels];
    private readonly float[] _lastPitch = new float[MaxChannels];
    private readonly int[] _lastRawKeyGen = new int[MaxChannels];
    private readonly ushort[] _keyGen = new ushort[MaxChannels];

    /// <summary>ほぼ整数の半音か（KF などの端数がない音程か）を判定する。</summary>
    private static bool IsIntegerSemitone(float p) => float.IsFinite(p) && MathF.Abs(p - MathF.Round(p)) < 0.005f;

    // WaveOut の再生位置をなめらかに補間して追うための基準。
    private long _anchorDeviceSamples = -1;
    private long _anchorTimestamp;

    public PitchTimeline()
    {
        for (int i = 0; i < Capacity; i++)
        {
            _ring[i] = new FrameSlot();
        }
        Array.Fill(_lastNote, (sbyte)-1);
        Array.Fill(_lastPitch, float.NaN);
    }


    /// <summary>履歴と追跡用の状態をすべて消す。</summary>
    public void Reset()
    {
        Array.Clear(_wasKeyOn);
        Array.Fill(_lastNote, (sbyte)-1);
        Array.Fill(_lastPitch, float.NaN);
        Array.Clear(_lastRawKeyGen);
        Array.Clear(_keyGen);
        _anchorDeviceSamples = -1;
        _anchorTimestamp = 0;
        for (int i = 0; i < Capacity; i++)
        {
            Volatile.Write(ref _ring[i].Sequence, 0);
            _ring[i].SamplePosition = 0;
            _ring[i].ChannelCount = 0;
        }
        Volatile.Write(ref _writeCount, 0);
        Volatile.Write(ref _latestSamplePosition, 0);
    }

    /// <summary>音声スレッドから現在のサンプル位置とチャンネル状態をロックフリーで記録する。</summary>
    public void Record(long samplePosition, ReadOnlySpan<MusicChannelSnapshot> channels)
    {
        int count = Math.Min(channels.Length, MaxChannels);
        long writeIndex = Volatile.Read(ref _writeCount);
        var slot = _ring[(int)(writeIndex & Mask)];

        // 書き込み中の印として、コマの Sequence を奇数にする。
        long seq = (writeIndex + 1) << 1;
        Volatile.Write(ref slot.Sequence, seq - 1);

        int nextBuf = 1 - Volatile.Read(ref _activeBufferIndex);
        var buf = _doubleBuffer[nextBuf];

        slot.SamplePosition = samplePosition;
        slot.ChannelCount = count;

        // 各チャンネルの音程を求め、キーオン・ノートの変化・半音単位の段差があれば発音世代を進める。音源が発音世代を持っていればその変化も見る。
        for (int i = 0; i < count; i++)
        {
            ref readonly var ch = ref channels[i];
            sbyte note = ch.NoteNumber is >= 0 and <= 127 ? (sbyte)ch.NoteNumber.Value : (sbyte)-1;
            bool keyOn = ch.IsKeyOn;
            double? dispPitch = keyOn ? MusicPitch.Display(ch) : null;
            float pitch = dispPitch.HasValue ? (float)dispPitch.Value : float.NaN;
            bool steppedWithoutKf = !float.IsFinite(_lastPitch[i]) ||
                !float.IsFinite(pitch) ||
                MathF.Abs(pitch - _lastPitch[i]) >= 0.85f ||
                (IsIntegerSemitone(pitch) && IsIntegerSemitone(_lastPitch[i]) && note != _lastNote[i]);
            if (ch.KeyGeneration > 0)
            {
                if (!_wasKeyOn[i] || steppedWithoutKf || ch.KeyGeneration != _lastRawKeyGen[i] || (note >= 0 && note != _lastNote[i]))
                    _keyGen[i]++;
                _lastRawKeyGen[i] = ch.KeyGeneration;
            }
            else if (keyOn && (!_wasKeyOn[i] || steppedWithoutKf || (note >= 0 && note != _lastNote[i])))
            {
                _keyGen[i]++;
            }
            _wasKeyOn[i] = keyOn;
            _lastNote[i] = note;
            _lastPitch[i] = pitch;

            float level = (float)Math.Clamp(ch.Level, 0.0, 1.0);

            // 状態をリングバッファのコマとダブルバッファの裏側に書く。
            var state = new ChannelRealtimeState(
                pitch,
                level,
                ch.NoteNumber,
                keyOn,
                ch.IsInUse,
                ch.IsHardwareOutput,
                ch.IsNoteEstimated,
                _keyGen[i]);

            slot.Channels[i] = state;
            buf[i] = state;
        }

        // ダブルバッファを切り替え、コマの Sequence を偶数に戻して確定する。
        _doubleBufferCounts[nextBuf] = count;
        _doubleBufferSamples[nextBuf] = samplePosition;
        Volatile.Write(ref _activeBufferIndex, nextBuf);

        Volatile.Write(ref slot.Sequence, seq);
        Volatile.Write(ref _writeCount, writeIndex + 1);
        Volatile.Write(ref _latestSamplePosition, samplePosition);
    }

    /// <summary>音声スレッドから、軽量なピッチ配列・キーオン状態だけを高速に記録する。</summary>
    public void RecordFast(long samplePosition, ReadOnlySpan<ChannelRealtimeState> states)
    {
        int count = Math.Min(states.Length, MaxChannels);
        long writeIndex = Volatile.Read(ref _writeCount);
        var slot = _ring[(int)(writeIndex & Mask)];

        // 書き込み中の印を付け、Record と同じ判定で発音世代を進めながら書く。
        long seq = (writeIndex + 1) << 1;
        Volatile.Write(ref slot.Sequence, seq - 1);

        int nextBuf = 1 - Volatile.Read(ref _activeBufferIndex);
        var buf = _doubleBuffer[nextBuf];

        slot.SamplePosition = samplePosition;
        slot.ChannelCount = count;
        for (int i = 0; i < count; i++)
        {
            ref readonly var s = ref states[i];
            bool keyOn = s.IsKeyOn;
            float pitch = s.Pitch;
            sbyte note = s.NoteNumber;
            bool steppedWithoutKf = !float.IsFinite(_lastPitch[i]) ||
                !float.IsFinite(pitch) ||
                MathF.Abs(pitch - _lastPitch[i]) >= 0.85f ||
                (IsIntegerSemitone(pitch) && IsIntegerSemitone(_lastPitch[i]) && note != _lastNote[i]);
            if (s.KeyGen > 0)
            {
                if (!_wasKeyOn[i] || steppedWithoutKf || s.KeyGen != _lastRawKeyGen[i] || (note >= 0 && note != _lastNote[i]))
                    _keyGen[i]++;
                _lastRawKeyGen[i] = s.KeyGen;
            }
            else if (keyOn && (!_wasKeyOn[i] || steppedWithoutKf || (note >= 0 && note != _lastNote[i])))
            {
                _keyGen[i]++;
            }
            _wasKeyOn[i] = keyOn;
            _lastNote[i] = note;
            _lastPitch[i] = pitch;

            var tracked = new ChannelRealtimeState(
                pitch,
                s.Level,
                note >= 0 ? note : null,
                keyOn,
                s.IsInUse,
                s.IsHardwareOutput,
                s.IsNoteEstimated,
                _keyGen[i]);
            slot.Channels[i] = tracked;
            buf[i] = tracked;
        }

        // ダブルバッファを切り替えて確定する。
        _doubleBufferCounts[nextBuf] = count;
        _doubleBufferSamples[nextBuf] = samplePosition;
        Volatile.Write(ref _activeBufferIndex, nextBuf);

        Volatile.Write(ref slot.Sequence, seq);
        Volatile.Write(ref _writeCount, writeIndex + 1);
        Volatile.Write(ref _latestSamplePosition, samplePosition);
    }

    /// <summary>
    /// WaveOut の出力済みバイト数と高精度タイマーから、現在スピーカーから鳴っているサンプル位置を計算する。
    /// WaveOut が未使用（単体テスト等）の場合は最新の生成サンプル位置を返す。
    /// </summary>
    public double GetPlaybackSamplePosition(IWavePlayer? wavePlayer, bool isPlaying, bool isPaused, int sampleRate = 44100)
    {
        long latest = Volatile.Read(ref _latestSamplePosition);
        if (wavePlayer is not WaveOut waveOut || !isPlaying)
        {
            return latest;
        }

        long deviceSamples;
        try
        {
            deviceSamples = waveOut.GetPosition() / 4;
        }
        catch
        {
            return latest;
        }

        // 出力位置が変わったら、そこを補間の基準にする。
        long now = Stopwatch.GetTimestamp();
        if (deviceSamples != _anchorDeviceSamples || _anchorDeviceSamples < 0 || isPaused)
        {
            _anchorDeviceSamples = deviceSamples;
            _anchorTimestamp = now;
        }

        if (isPaused)
        {
            return Math.Clamp(deviceSamples, 0, latest);
        }

        double elapsedSeconds = (double)(now - _anchorTimestamp) / Stopwatch.Frequency;
        // WaveOut のバッファコールバック間（最大 ~50ms）をなめらかに補間する。
        double interpolated = _anchorDeviceSamples + Math.Clamp(elapsedSeconds, 0.0, 0.06) * sampleRate;
        return Math.Clamp(interpolated, 0.0, latest);
    }

    /// <summary>ダブルバッファされた最新状態のコピーをロックフリーで取得する。</summary>
    public int CopyLatestStates(Span<ChannelRealtimeState> destination, out long samplePosition)
    {
        int idx = Volatile.Read(ref _activeBufferIndex);
        int count = Math.Min(destination.Length, _doubleBufferCounts[idx]);
        samplePosition = _doubleBufferSamples[idx];
        _doubleBuffer[idx].AsSpan(0, count).CopyTo(destination);
        return count;
    }

    /// <summary>
    /// 現在の再生位置のピッチ・キー状態・レベルをスナップショットのチャンネルへ重ねる（MDX/VGM共通）。
    /// 再生位置が取れない、または記録が無いときは元のスナップショットを返す。
    /// </summary>
    public MusicPlaybackSnapshot ApplyTo(MusicPlaybackSnapshot snapshot, IWavePlayer? wavePlayer, bool isPlaying, bool isPaused)
    {
        if (snapshot.Channels.Length == 0) return snapshot;
        double playSample = GetPlaybackSamplePosition(wavePlayer, isPlaying, isPaused);
        if (playSample <= 0) return snapshot;
        Span<ChannelRealtimeState> realtime = stackalloc ChannelRealtimeState[MaxChannels];
        int count = SampleAt(playSample, realtime);
        if (count <= 0) return snapshot;
        var channels = (MusicChannelSnapshot[])snapshot.Channels.Clone();
        int n = Math.Min(count, channels.Length);
        for (int i = 0; i < n; i++)
        {
            ref readonly var r = ref realtime[i];
            channels[i] = channels[i] with
            {
                Pitch = float.IsNaN(r.Pitch) ? null : r.Pitch,
                KeyGeneration = r.KeyGen,
                IsKeyOn = r.IsKeyOn,
                NoteNumber = r.NoteNumber >= 0 ? r.NoteNumber : null,
                KeyboardNotes = r.IsKeyOn && r.NoteNumber >= 0 ? [r.NoteNumber] : [],
                Level = r.Level
            };
        }
        return snapshot with { Channels = channels };
    }

    /// <summary>
    /// 指定された再生サンプル位置に対応する各チャンネルの状態をリングバッファからロックフリーで引き、前後サンプル間で連続ピッチを線形補間して destination に書き込む。
    /// </summary>
    public int SampleAt(double playbackSamplePosition, Span<ChannelRealtimeState> destination)
    {
        long totalWritten = Volatile.Read(ref _writeCount);
        if (totalWritten <= 0)
        {
            return 0;
        }

        long minIndex = Math.Max(0, totalWritten - Capacity + 2);
        long maxIndex = totalWritten - 1;

        Span<ChannelRealtimeState> beforeStates = stackalloc ChannelRealtimeState[MaxChannels];
        Span<ChannelRealtimeState> afterStates = stackalloc ChannelRealtimeState[MaxChannels];
        Span<ChannelRealtimeState> temp = stackalloc ChannelRealtimeState[MaxChannels];
        long beforeSample = -1, afterSample = -1;
        int beforeCount = 0, afterCount = 0;

        // 最新側から遡って playbackSamplePosition を挟む2フレームを探す。書き込み中のコマと、読み取り中に上書きされたコマは飛ばす。
        for (long idx = maxIndex; idx >= minIndex; idx--)
        {
            var slot = _ring[(int)(idx & Mask)];
            long seq1 = Volatile.Read(ref slot.Sequence);
            if ((seq1 & 1) != 0 || seq1 == 0) continue;

            long sPos = slot.SamplePosition;
            int cCount = Math.Min(slot.ChannelCount, MaxChannels);
            slot.Channels.AsSpan(0, cCount).CopyTo(temp);

            long seq2 = Volatile.Read(ref slot.Sequence);
            if (seq1 != seq2) continue;

            if (sPos >= playbackSamplePosition)
            {
                afterSample = sPos;
                afterCount = cCount;
                temp.Slice(0, cCount).CopyTo(afterStates);
            }
            else
            {
                beforeSample = sPos;
                beforeCount = cCount;
                temp.Slice(0, cCount).CopyTo(beforeStates);
                break;
            }
        }

        // 前後が揃わなければ、ある方（どちらもなければ最新状態）をそのまま返す。
        if (beforeSample < 0 && afterSample < 0)
        {
            return CopyLatestStates(destination, out _);
        }

        if (beforeSample < 0)
        {
            int n = Math.Min(destination.Length, afterCount);
            afterStates.Slice(0, n).CopyTo(destination);
            return n;
        }

        if (afterSample < 0 || afterSample <= beforeSample)
        {
            int n = Math.Min(destination.Length, beforeCount);
            beforeStates.Slice(0, n).CopyTo(destination);
            return n;
        }

        // 前後のコマの間の位置で補間する。
        float t = (float)Math.Clamp((playbackSamplePosition - beforeSample) / (double)(afterSample - beforeSample), 0.0, 1.0);
        int outCount = Math.Min(destination.Length, Math.Min(beforeCount, afterCount));
        for (int i = 0; i < outCount; i++)
        {
            ref readonly var b = ref beforeStates[i];
            ref readonly var a = ref afterStates[i];

            // 同一の発音世代（KeyGen＝間にKey-Offを挟まない連続したKey-On）かつ端数ピッチ変化のときだけ線形補間する。それ以外は近い方のコマを使う。
            bool canInterpolate = b.IsKeyOn && a.IsKeyOn &&
                b.KeyGen == a.KeyGen &&
                float.IsFinite(b.Pitch) && float.IsFinite(a.Pitch) &&
                MathF.Abs(a.Pitch - b.Pitch) < 0.85f &&
                !(IsIntegerSemitone(b.Pitch) && IsIntegerSemitone(a.Pitch));
            if (canInterpolate)
            {
                float interpPitch = b.Pitch + (a.Pitch - b.Pitch) * t;
                float interpLevel = b.Level + (a.Level - b.Level) * t;
                destination[i] = new ChannelRealtimeState(
                    interpPitch,
                    interpLevel,
                    t < 0.5f ? (b.NoteNumber >= 0 ? b.NoteNumber : null) : (a.NoteNumber >= 0 ? a.NoteNumber : null),
                    true,
                    a.IsInUse,
                    a.IsHardwareOutput,
                    a.IsNoteEstimated,
                    a.KeyGen);
            }
            else
            {
                destination[i] = t < 0.5f ? b : a;
            }
        }
        return outCount;
    }
}
