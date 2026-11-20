// MultiPCM/YMW258 emulation based on the BSD-3-Clause MAME/libvgm core by
// Miguel Angel Horna (ElSemi). This managed port keeps VGM's historical
// clock/180 convention while matching the current sample, bank, loop,
// envelope, pan and LFO behaviour.
// （第三者の著作権表示のため原文のまま残す。日本語: MAME・libvgm のコアを元にした移植で、
// VGM のクロック/180 の扱いを保ったまま、サンプル・バンク・ループ・エンベロープ・パン・LFO を現行のコアに合わせている。）

namespace MirrorFlux.Services.Vgm;

/// <summary>セガのMultiPCM（YMW258）の28スロットを、ROMのサンプルから合成する。</summary>
internal sealed class VgmMultiPcmAudio
{
    // 音量・エンベロープ・LFOの固定小数点のビット数。
    private const int TlShift = 12;
    private const int EgShift = 16;
    private const int LfoShift = 8;
    private const int FixedOne = 1 << TlShift;

    /// <summary>エンベロープの段階。</summary>
    private enum EnvelopeState { Attack, Decay1, Decay2, Release }

    /// <summary>ROMの先頭にあるサンプル情報（開始・ループ・終了位置、エンベロープ、LFO、形式）。</summary>
    private sealed class Sample
    {
        public uint Start, Loop, End;
        public byte Attack, Decay1, Decay2, DecayLevel, Release, KeyRateScale;
        public byte LfoVibrato, LfoAmplitude, Format;
    }

    /// <summary>スロット1個分のエンベロープの状態。</summary>
    private sealed class Envelope
    {
        public int Volume, AttackRate, Decay1Rate, Decay2Rate, ReleaseRate, DecayLevel;
        public EnvelopeState State;
    }

    /// <summary>音程または音量を揺らすLFO1個分の状態。</summary>
    private sealed class Lfo
    {
        public ushort Phase;
        public uint PhaseStep;
        public int[] Table = [];
        public int[] Scale = [];
    }

    /// <summary>発音スロット1個分のレジスタと再生状態。</summary>
    private sealed class Slot
    {
        public readonly byte[] Registers = new byte[11];
        public bool Playing, Reverse;
        public Sample Sample = new();
        public uint Base, Offset, Step;
        public byte Octave, Pan, TotalLevelDestination, Vibrato, Tremolo;
        public ushort Pitch;
        public int TotalLevel, TotalLevelStep, PreviousSample;
        public readonly Envelope Envelope = new();
        public readonly Lfo PitchLfo = new(), AmplitudeLfo = new();
    }

    // エンベロープの各レートに対応する時間（ミリ秒）。
    private static readonly double[] BaseTimes =
    [
        0, 0, 0, 0, 6222.95, 4978.37, 4148.66, 3556.01,
        3111.47, 2489.21, 2074.33, 1778.00, 1555.74, 1244.63, 1037.19, 889.02,
        777.87, 622.31, 518.59, 444.54, 388.93, 311.16, 259.32, 222.27,
        194.47, 155.60, 129.66, 111.16, 97.23, 77.82, 64.85, 55.60,
        48.62, 38.91, 32.43, 27.80, 24.31, 19.46, 16.24, 13.92,
        12.15, 9.75, 8.12, 6.98, 6.08, 4.90, 4.08, 3.49,
        3.04, 2.49, 2.13, 1.90, 1.72, 1.41, 1.18, 1.04,
        .91, .73, .59, .50, .45, .45, .45, .45
    ];

    // LFOの周波数と揺れ幅、パン・音量・LFOの変換表。
    private static readonly float[] LfoFrequencies = [.168f, 2.019f, 3.196f, 4.206f, 5.215f, 5.888f, 6.224f, 7.066f];
    private static readonly float[] PhaseScaleLimits = [0, 3.378f, 5.065f, 6.750f, 10.114f, 20.170f, 40.180f, 79.307f];
    private static readonly float[] AmplitudeScaleLimits = [0, .4f, .8f, 1.5f, 3, 6, 12, 24];
    private static readonly int[] LeftPan = new int[0x800], RightPan = new int[0x800];
    private static readonly int[] LinearToExponential = new int[0x400];
    private static readonly int[] PitchTable = new int[256], AmplitudeTable = new int[256];
    private static readonly int[][] PitchScales = Enumerable.Range(0, 8).Select(_ => new int[256]).ToArray();
    private static readonly int[][] AmplitudeScales = Enumerable.Range(0, 8).Select(_ => new int[256]).ToArray();

    private readonly Slot[] _slots = Enumerable.Range(0, 28).Select(_ => new Slot()).ToArray();
    private readonly uint[] _attackSteps = new uint[64], _decayReleaseSteps = new uint[64], _frequencySteps = new uint[0x400];
    private readonly int[] _totalLevelSteps = new int[2];
    private byte[] _rom = [];
    private uint _romMask;
    private int _currentSlot, _address;
    private bool _segaBanking;
    private uint _bank0, _bank1;

    /// <summary>音源チップ本来のサンプルレート。</summary>
    public uint NativeRate { get; }

    /// <summary>パンと総レベル、総レベルの線形・指数変換、LFOの波形と揺れ幅の表を作る。</summary>
    static VgmMultiPcmAudio()
    {
        // パンと総レベルの組合せごとに左右の音量を求める。
        for (int level = 0; level < 0x80; level++)
        {
            double total = Math.Pow(10, level * (-24.0 / 64) / 20) / 4;
            for (int pan = 0; pan < 16; pan++)
            {
                double left, right;
                if (pan == 8) left = right = 0;
                else if (pan == 0) left = right = 1;
                else if ((pan & 8) != 0)
                {
                    int inverted = 16 - pan;
                    left = 1;
                    right = (inverted & 7) == 7 ? 0 : Math.Pow(10, inverted * (-12.0 / 4) / 20);
                }
                else
                {
                    left = (pan & 7) == 7 ? 0 : Math.Pow(10, pan * (-12.0 / 4) / 20);
                    right = 1;
                }
                LeftPan[(pan << 7) | level] = ToFixed(left * total, TlShift);
                RightPan[(pan << 7) | level] = ToFixed(right * total, TlShift);
            }
        }

        // エンベロープの線形値から音量への変換表。
        for (int i = 0; i < 0x400; i++)
            LinearToExponential[i] = ToFixed(Math.Pow(10, -(96.0 - 96.0 * i / 0x400) / 20), TlShift);

        // LFOの三角波の表と、揺れ幅ごとの倍率の表。
        for (int i = 0; i < 256; i++)
        {
            PitchTable[i] = i < 64 ? i * 2 + 128 : i < 128 ? 383 - i * 2 : i < 192 ? 384 - i * 2 : i * 2 - 383;
            AmplitudeTable[i] = i < 128 ? 255 - i * 2 : i * 2 - 256;
        }
        for (int table = 0; table < 8; table++)
        {
            for (int i = -128; i < 128; i++)
                PitchScales[table][i + 128] = ToFixed(Math.Pow(2, PhaseScaleLimits[table] * i / 128 / 1200), LfoShift);
            for (int i = 0; i < 256; i++)
                AmplitudeScales[table][i] = ToFixed(Math.Pow(10, -AmplitudeScaleLimits[table] * i / 256 / 20), LfoShift);
        }
    }

    /// <summary>VGMのクロックから音源チップ本来のレートと、周波数・エンベロープ・総レベルの変化量を求める。</summary>
    public VgmMultiPcmAudio(uint vgmClock)
    {
        // VGMの記録は従来の/180のクロックを使う。現行の/224のコアには224/180倍したクロックを渡すので、本来のレートはこれと同じになる。
        double rate = (vgmClock & 0x3fffffff) / 180.0;
        NativeRate = Math.Max(1, (uint)rate);
        // 音程ごとの再生速度と、エンベロープ・総レベルの1サンプルあたりの変化量を求める。
        for (int i = 0; i < 0x400; i++)
            _frequencySteps[i] = (uint)ToFixed(rate * (1024.0 + i) / 1024.0, TlShift);
        for (int i = 4; i < 64; i++)
        {
            _attackSteps[i] = (uint)((0x400L << EgShift) / (BaseTimes[i] * 44100 / 1000));
            _decayReleaseSteps[i] = (uint)((0x400L << EgShift) / (BaseTimes[i] * 14.32833 * 44100 / 1000));
        }
        _attackSteps[63] = 0x400u << EgShift;
        _totalLevelSteps[0] = -(int)((0x80L << TlShift) / (78.2 * 44100 / 1000));
        _totalLevelSteps[1] = (int)((0x80L << TlShift) / (78.2 * 2 * 44100 / 1000));
    }

    /// <summary>レジスタに書き込む。0はデータ、1はスロット選択、2はアドレス、0x10〜0x12はセガ方式のバンク切替。</summary>
    public void Write(int offset, byte data)
    {
        switch (offset)
        {
            case 0:
                if (_currentSlot >= 0 && _address < 11) WriteSlot(_slots[_currentSlot], _address, data);
                break;
            case 1:
                int selector = data & 0x1f;
                _currentSlot = (selector & 7) == 7 ? -1 : selector - selector / 8;
                break;
            case 2:
                _address = data;
                break;
            case 0x10:
                _segaBanking = true;
                _bank0 = (uint)data << 20;
                _bank1 = ((uint)data << 20) | 0x080000;
                break;
            case 0x11:
                _segaBanking = true;
                _bank0 = (uint)data << 19;
                break;
            case 0x12:
                _segaBanking = true;
                _bank1 = (uint)data << 19;
                break;
        }
    }

    /// <summary>VGMのバンク指定命令を、セガ方式のバンク切替の書込みに直す。</summary>
    public void WriteBank(byte bankMask, ushort value)
    {
        int mask = bankMask & 3;
        byte bank = (byte)value;
        if (mask == 3 && (bank & 8) == 0) Write(0x10, (byte)(bank / 0x10));
        else
        {
            if ((mask & 2) != 0) Write(0x11, (byte)(bank / 8));
            if ((mask & 1) != 0) Write(0x12, (byte)(bank / 8));
        }
    }

    /// <summary>ROMの一部を読み込む。全体の大きさが変わったら0xFFで埋めた領域を確保し直す。</summary>
    public void LoadRom(int totalSize, int offset, ReadOnlySpan<byte> data)
    {
        if (totalSize <= 0 || offset < 0 || offset > totalSize) return;
        if (_rom.Length != totalSize)
        {
            byte[] resized = new byte[totalSize];
            Array.Fill(resized, (byte)0xff);
            _rom.AsSpan(0, Math.Min(_rom.Length, resized.Length)).CopyTo(resized);
            _rom = resized;
            _romMask = PowerOfTwoMask((uint)totalSize);
        }
        data[..Math.Min(data.Length, totalSize - offset)].CopyTo(_rom.AsSpan(offset));
    }

    /// <summary>全スロットを合成して、左右の出力に指定サンプル数を書き込む。</summary>
    public void Update(int[][] outputs, int samples)
    {
        if (_rom.Length == 0) return;
        for (int i = 0; i < samples; i++)
        {
            int left = 0, right = 0;
            foreach (Slot slot in _slots)
            {
                if (!slot.Playing) continue;
                // 読み出し位置のサンプルと直前のサンプルを補間する。
                int volume = (slot.TotalLevel >> TlShift) | (slot.Pan << 7);
                uint samplePosition = slot.Offset >> TlShift;
                uint step = slot.Step;
                int fraction = (int)(slot.Offset & (FixedOne - 1));
                uint position = slot.Reverse ? slot.Sample.End - samplePosition - 1 : samplePosition;
                int currentSample = ReadSample(slot, position);
                int sample = (currentSample * fraction + slot.PreviousSample * (FixedOne - fraction)) >> TlShift;

                // 再生位置を進め、終端を越えたらループ位置へ戻す。
                if (slot.Vibrato != 0) step = (uint)(((long)step * PitchLfoStep(slot.PitchLfo)) >> TlShift);
                slot.Offset += step;
                if (samplePosition != (slot.Offset >> TlShift)) slot.PreviousSample = currentSample;
                if (slot.Sample.End > slot.Sample.Loop && slot.Offset >= (slot.Sample.End << TlShift))
                {
                    slot.Offset -= (slot.Sample.End - slot.Sample.Loop) << TlShift;
                    slot.Reverse = false;
                }

                // 総レベルを目標値へ近づけ、LFO・エンベロープ・パンを掛けて左右に足す。
                if ((slot.TotalLevel >> TlShift) != slot.TotalLevelDestination)
                {
                    slot.TotalLevel += slot.TotalLevelStep;
                    if (slot.TotalLevelStep < 0 && (slot.TotalLevel >> TlShift) < slot.TotalLevelDestination ||
                        slot.TotalLevelStep > 0 && (slot.TotalLevel >> TlShift) > slot.TotalLevelDestination)
                        slot.TotalLevel = slot.TotalLevelDestination << TlShift;
                }
                if (slot.Tremolo != 0) sample = (int)(((long)sample * AmplitudeLfoStep(slot.AmplitudeLfo)) >> TlShift);
                sample = (int)(((long)sample * UpdateEnvelope(slot)) >> 10);
                left += (int)(((long)LeftPan[volume] * sample) >> TlShift);
                right += (int)(((long)RightPan[volume] * sample) >> TlShift);
            }
            outputs[0][i] = left;
            outputs[1][i] = right;
        }
    }

    /// <summary>スロットのレジスタに書き込み、対応する再生設定を更新する。</summary>
    private void WriteSlot(Slot slot, int register, byte data)
    {
        slot.Registers[register] = data;
        switch (register)
        {
            // 0: パン。
            case 0:
                slot.Pan = (byte)(data >> 4);
                break;
            // 1: サンプル番号。ROMのサンプル情報を読み直し、セガ方式のバンクなら開始位置を切り替える。
            case 1:
                slot.Sample = ReadSampleMetadata(slot.Registers[1] | ((slot.Registers[2] & 1) << 8));
                WriteSlot(slot, 6, slot.Sample.LfoVibrato);
                slot.Registers[7] = (byte)((slot.Sample.Attack << 4) | slot.Sample.Decay1);
                slot.Registers[8] = (byte)((slot.Sample.DecayLevel << 4) | slot.Sample.Decay2);
                slot.Registers[9] = (byte)((slot.Sample.KeyRateScale << 4) | slot.Sample.Release);
                WriteSlot(slot, 10, slot.Sample.LfoAmplitude);
                slot.Base = slot.Sample.Start;
                if (_segaBanking)
                {
                    slot.Base &= 0x1fffff;
                    if ((slot.Base & 0x100000) != 0)
                        slot.Base = (slot.Base & 0x07ffff) | ((slot.Base & 0x080000) != 0 ? _bank1 : _bank0);
                }
                if (slot.Playing) Retrigger(slot);
                break;
            // 2・3: 音程（オクターブとF-Number）。
            case 2:
            case 3:
                slot.Octave = (byte)(slot.Registers[3] >> 4);
                slot.Pitch = (ushort)(((slot.Registers[3] & 15) << 6) | (slot.Registers[2] >> 2));
                UpdateStep(slot);
                break;
            // 4: キーオン・キーオフ。
            case 4:
                if ((data & 0x80) != 0)
                {
                    slot.Playing = true;
                    Retrigger(slot);
                }
                else if (slot.Playing)
                {
                    if (slot.Sample.Release != 15) slot.Envelope.State = EnvelopeState.Release;
                    else slot.Playing = false;
                }
                break;
            // 5: 総レベルの目標値。ビット0が0なら徐々に、1ならすぐに変える。
            case 5:
                slot.TotalLevelDestination = (byte)((data >> 1) & 0x7f);
                if ((data & 1) == 0)
                    slot.TotalLevelStep = (slot.TotalLevel >> TlShift) > slot.TotalLevelDestination ? _totalLevelSteps[0] : _totalLevelSteps[1];
                else slot.TotalLevel = slot.TotalLevelDestination << TlShift;
                break;
            // 6・10: LFOの周波数と、音程・音量の揺れ幅。
            case 6:
            case 10:
                byte frequency = (byte)((slot.Registers[6] >> 3) & 7);
                slot.Vibrato = (byte)(slot.Registers[6] & 7);
                slot.Tremolo = (byte)(slot.Registers[10] & 7);
                if (data != 0)
                {
                    ConfigureLfo(slot.PitchLfo, frequency, slot.Vibrato, false);
                    ConfigureLfo(slot.AmplitudeLfo, frequency, slot.Tremolo, true);
                }
                break;
            // 7〜9: エンベロープのレート。
            case 7:
            case 8:
            case 9:
                slot.Sample.Attack = (byte)(slot.Registers[7] >> 4);
                slot.Sample.Decay1 = (byte)(slot.Registers[7] & 15);
                slot.Sample.DecayLevel = (byte)(slot.Registers[8] >> 4);
                slot.Sample.Decay2 = (byte)(slot.Registers[8] & 15);
                slot.Sample.KeyRateScale = (byte)(slot.Registers[9] >> 4);
                slot.Sample.Release = (byte)(slot.Registers[9] & 15);
                CalculateEnvelope(slot);
                break;
        }
    }

    /// <summary>ROMから12バイトのサンプル情報を読む。</summary>
    private Sample ReadSampleMetadata(int index)
    {
        uint address = ((uint)index * 12) & _romMask;
        uint rawStart = ((uint)ReadByte(address) << 16) | ((uint)ReadByte(address + 1) << 8) | ReadByte(address + 2);
        return new Sample
        {
            Format = (byte)((rawStart >> 20) & 0xfe), Start = rawStart & 0x3fffff,
            Loop = (uint)((ReadByte(address + 3) << 8) | ReadByte(address + 4)),
            End = (uint)(0x10000 - ((ReadByte(address + 5) << 8) | ReadByte(address + 6))),
            LfoVibrato = ReadByte(address + 7), Attack = (byte)(ReadByte(address + 8) >> 4),
            Decay1 = (byte)(ReadByte(address + 8) & 15), Decay2 = (byte)(ReadByte(address + 9) & 15),
            DecayLevel = (byte)(ReadByte(address + 9) >> 4), Release = (byte)(ReadByte(address + 10) & 15),
            KeyRateScale = (byte)(ReadByte(address + 10) >> 4), LfoAmplitude = (byte)(ReadByte(address + 11) & 15)
        };
    }

    /// <summary>サンプルを1個読む。形式のビット2が立っていれば12ビット、それ以外は8ビット。</summary>
    private int ReadSample(Slot slot, uint position)
    {
        if ((slot.Sample.Format & 4) != 0)
        {
            uint address = slot.Base + (position >> 1) * 3;
            return (short)((position & 1) == 0
                ? (ReadByte(address) << 8) | ((ReadByte(address + 1) & 15) << 4)
                : (ReadByte(address + 2) << 8) | (ReadByte(address + 1) & 0xf0));
        }
        return (short)(ReadByte(slot.Base + position) << 8);
    }

    /// <summary>キーオン時に再生位置とエンベロープを初期化する。</summary>
    private void Retrigger(Slot slot)
    {
        slot.Offset = 0;
        slot.PreviousSample = 0;
        slot.TotalLevel = slot.TotalLevelDestination << TlShift;
        CalculateEnvelope(slot);
        slot.Envelope.State = EnvelopeState.Attack;
        slot.Envelope.Volume = (0x3ff - 0x2a0) << EgShift;
    }

    /// <summary>オクターブと音程から1サンプルあたりの再生位置の進み量を求める。</summary>
    private void UpdateStep(Slot slot)
    {
        int octave = (slot.Octave - 1) & 15;
        uint pitch = _frequencySteps[slot.Pitch];
        pitch = (octave & 8) != 0 ? pitch >> (16 - octave) : pitch << octave;
        slot.Step = (uint)(pitch / (NativeRate == 0 ? 1 : NativeRate));
    }

    /// <summary>キースケールを加味して、エンベロープの各段階のレートを求める。</summary>
    private void CalculateEnvelope(Slot slot)
    {
        int octave = (slot.Octave & 8) != 0 ? slot.Octave - 16 : slot.Octave;
        int rate = slot.Sample.KeyRateScale != 15
            ? (octave + slot.Sample.KeyRateScale) * 2 + ((slot.Pitch >> 9) & 1) : 0;
        slot.Envelope.AttackRate = (int)GetRate(_attackSteps, rate, slot.Sample.Attack);
        slot.Envelope.Decay1Rate = (int)GetRate(_decayReleaseSteps, rate, slot.Sample.Decay1);
        slot.Envelope.Decay2Rate = (int)GetRate(_decayReleaseSteps, rate, slot.Sample.Decay2);
        slot.Envelope.ReleaseRate = (int)GetRate(_decayReleaseSteps, rate, slot.Sample.Release);
        slot.Envelope.DecayLevel = 15 - slot.Sample.DecayLevel;
    }

    /// <summary>エンベロープを1サンプル進め、音量の倍率を返す。</summary>
    private int UpdateEnvelope(Slot slot)
    {
        Envelope envelope = slot.Envelope;
        switch (envelope.State)
        {
            case EnvelopeState.Attack:
                envelope.Volume += (int)(((long)((0x817 << (EgShift - 1)) - envelope.Volume) * envelope.AttackRate) >> 24);
                if (envelope.Volume >= (0x3ff << EgShift))
                {
                    envelope.State = envelope.Decay1Rate >= (0x400 << EgShift) ? EnvelopeState.Decay2 : EnvelopeState.Decay1;
                    envelope.Volume = 0x3ff << EgShift;
                }
                break;
            case EnvelopeState.Decay1:
                envelope.Volume = Math.Max(0, envelope.Volume - envelope.Decay1Rate);
                if ((envelope.Volume >> (EgShift + 6)) <= envelope.DecayLevel) envelope.State = EnvelopeState.Decay2;
                break;
            case EnvelopeState.Decay2:
                envelope.Volume = Math.Max(0, envelope.Volume - envelope.Decay2Rate);
                break;
            case EnvelopeState.Release:
                envelope.Volume -= envelope.ReleaseRate;
                if (envelope.Volume <= 0)
                {
                    envelope.Volume = 0;
                    slot.Playing = false;
                }
                break;
        }
        return LinearToExponential[Math.Clamp(envelope.Volume >> EgShift, 0, 0x3ff)];
    }

    /// <summary>LFOの速度と、使う波形・揺れ幅の表を設定する。</summary>
    private void ConfigureLfo(Lfo lfo, int frequency, int scale, bool amplitude)
    {
        double step = LfoFrequencies[frequency] * 256 / NativeRate;
        lfo.PhaseStep = (uint)((1 << LfoShift) * step);
        lfo.Table = amplitude ? AmplitudeTable : PitchTable;
        lfo.Scale = amplitude ? AmplitudeScales[scale] : PitchScales[scale];
    }

    /// <summary>音程用LFOを1サンプル進め、倍率を返す。</summary>
    private static int PitchLfoStep(Lfo lfo)
    {
        lfo.Phase += (ushort)lfo.PhaseStep;
        return lfo.Scale[lfo.Table[(lfo.Phase >> LfoShift) & 255]] << (TlShift - LfoShift);
    }

    /// <summary>音量用LFOを1サンプル進め、倍率を返す。</summary>
    private static int AmplitudeLfoStep(Lfo lfo)
    {
        lfo.Phase += (ushort)lfo.PhaseStep;
        return lfo.Scale[lfo.Table[(lfo.Phase >> LfoShift) & 255]] << (TlShift - LfoShift);
    }

    /// <summary>ROMから1バイト読む。範囲外は0xFF。</summary>
    private byte ReadByte(uint address)
    {
        int index = (int)(address & _romMask);
        return (uint)index < (uint)_rom.Length ? _rom[index] : (byte)0xff;
    }
    private static uint GetRate(uint[] steps, int rate, int value) => value == 0 ? steps[0] : value == 15 ? steps[63] : steps[Math.Clamp(4 * value + rate, 0, 63)];
    private static int ToFixed(double value, int bits) => (int)((1 << bits) * value);
    /// <summary>指定値以上の2のべき乗から1を引いたマスクを返す。</summary>
    private static uint PowerOfTwoMask(uint value)
    {
        uint mask = 1;
        while (mask < value) mask <<= 1;
        return mask - 1;
    }
}
