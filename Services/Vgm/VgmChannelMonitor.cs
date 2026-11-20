using MirrorFlux.Services.Music;

namespace MirrorFlux.Services.Vgm;

/// <summary>
/// 対応しているVGM音源チップ（OPM・OPN系、SN76489、DAC、OKIM6258）のレジスタから、鍵盤・レベルメーター用の情報を作る。
/// レベルはキーと減衰量のレジスタから求めた推定値で、ボイスごとの実際の音声を測ったものではない。音源チップごとに自分のレジスタの写しを持つ。
/// </summary>
internal sealed class VgmChannelMonitor
{
    /// <summary>FMチャンネル1個分の、表示用エンベロープの状態。</summary>
    private sealed class FmEnvelope
    {
        public double Level;
        public double LastSeconds;
        public int KeyGeneration;
        public bool WasKeyOn;
        public bool Initialized;
    }

    /// <summary>FM音源チップ1個分のレジスタの写しとチャンネルごとの状態。</summary>
    private sealed class FmChip(string name, uint clock, int count, int instance = 0)
    {
        public readonly string Name = name;
        public readonly int Instance = instance;
        public string DisplayName => Instance == 0 ? Name : $"{Name} #{Instance + 1}";
        public readonly uint Clock = clock & 0x3fffffff;
        public readonly byte[] Registers = new byte[512];
        public readonly bool[] RegistersWritten = new bool[512];
        public readonly int[] Slots = new int[count];
        public readonly string[] LastData = new string[count];
        public readonly bool[] Used = new bool[count];
        public readonly bool[] SsgUsed = new bool[3];
        public readonly int[] KeyGenerations = new int[count];
        public readonly FmEnvelope[] Envelopes = Enumerable.Range(0, count).Select(_ => new FmEnvelope()).ToArray();
    }
    /// <summary>SN76489 1個分の音程・減衰量・ステレオ設定。</summary>
    private sealed class PsgState
    {
        public readonly int[] Tone = new int[4];
        public readonly int[] Attenuation = [15, 15, 15, 15];
        public int Latch;
        public int Stereo = 0xff;
        public readonly bool[] Used = new bool[4];
    }

    private readonly object _sync = new();
    private readonly List<FmChip> _fm = [];
    private readonly uint _psgClock;
    private readonly uint _secondPsgClock;
    private readonly bool _hasDac, _hasOki;
    private readonly PsgState[] _psg = [new(), new()];
    private bool _hasSecondPsg;
    private bool _dacUsed, _okiUsed;

    /// <summary>曲のヘッダーから、監視するFM音源チップ、SN76489、DAC、OKIM6258を決める。</summary>
    public VgmChannelMonitor(VgmFile file, bool nativeOutput = false)
    {
        if (file.Ym2151Clock != 0) _fm.Add(new("YM2151", file.Ym2151Clock, 8));
        if (file.Ym2612Clock != 0) _fm.Add(new("YM2612", file.Ym2612Clock, 6));
        if (file.Ym2610Clock != 0) _fm.Add(new("YM2610", file.Ym2610Clock, 6));
        if (file.Ym2608Clock != 0) _fm.Add(new("YM2608", file.Ym2608Clock, 6));
        if ((file.Ym2608Clock & 0x40000000) != 0) _fm.Add(new("YM2608", file.Ym2608Clock, 6, 1));
        if (file.Ym2203Clock != 0) _fm.Add(new("YM2203", file.Ym2203Clock, 3));
        if (file.HasSecondYm2203) _fm.Add(new("YM2203", file.Ym2203Clock, 3, 1));
        _psgClock = file.Sn76489Clock & 0x3fffffff;
        _secondPsgClock = file.SecondSn76489Clock != 0 ? file.SecondSn76489Clock : _psgClock;
        _hasSecondPsg = file.HasSecondSn76489;
        _hasDac = file.Ym2612Clock != 0;
        _hasOki = !nativeOutput && file.OkiM6258Clock != 0;
    }

    /// <summary>FM音源チップへの書込みを記録し、キーオンと使用中のチャンネルを更新する。</summary>
    public void WriteFm(string chipName, int port, byte reg, byte value, int instance = 0)
    {
        lock (_sync)
        {
            var chip = _fm.Find(c => c.Name == chipName && c.Instance == instance);
            if (chip == null || port is < 0 or > 1) return;
            chip.Registers[port * 256 + reg] = value;
            chip.RegistersWritten[port * 256 + reg] = true;
            // OPN系のSSG部分（ポート0の0x00〜0x0D）なら、使ったSSGチャンネルを記録する。
            if (port == 0 && chipName is "YM2203" or "YM2608" or "YM2610")
            {
                if (reg <= 5) chip.SsgUsed[reg / 2] = true;
                else if (reg is >= 8 and <= 10) chip.SsgUsed[reg - 8] = true;
                else if (reg is 6 or 7 or 11 or 12 or 13) Array.Fill(chip.SsgUsed, true);
            }
            if (chipName == "YM2612" && reg == 0x2a) _dacUsed = true;
            // YM2151は0x08、OPN系は0x28がキーオン。立ち上がりを数え、表示用の書込み内容を残す。
            if (port == 0 && chipName == "YM2151" && reg == 8)
            {
                int ch = value & 7;
                int prevSlots = chip.Slots[ch];
                chip.Slots[ch] = (value >> 3) & 15;
                if (chip.Slots[ch] != 0 && prevSlots == 0) chip.KeyGenerations[ch]++;
                chip.LastData[ch] = $"08:{value:X2}";
                chip.Used[ch] = true;
            }
            else if (port == 0 && chipName != "YM2151" && reg == 0x28)
            {
                int raw = value & 7;
                int ch = (raw & 3) + ((raw & 4) != 0 ? 3 : 0);
                if ((raw & 3) == 3 || ch >= chip.Slots.Length) return;
                int prevSlots = chip.Slots[ch];
                chip.Slots[ch] = value >> 4;
                if (chip.Slots[ch] != 0 && prevSlots == 0) chip.KeyGenerations[ch]++;
                chip.LastData[ch] = $"28:{value:X2}";
                chip.Used[ch] = true;
            }
            else if (TryGetFmChannel(chip, port, reg, out int channel)) chip.Used[channel] = true;
        }
    }

    /// <summary>SN76489のステレオ設定（Game Gear）を記録する。</summary>
    public void WritePsgStereo(byte value, int instance = 0)
    {
        lock (_sync)
        {
            if (instance is >= 0 and < 2)
            {
                if (instance == 1) _hasSecondPsg = true;
                _psg[instance].Stereo = value;
            }
        }
    }

    /// <summary>SN76489への書込みを記録する。ラッチ付きなら対象と種類を切り替え、音程か減衰量を更新する。</summary>
    public void WritePsg(byte value, int instance = 0)
    {
        lock (_sync)
        {
            if (instance is < 0 or >= 2) return;
            if (instance == 1) _hasSecondPsg = true;
            var psg = _psg[instance];
            if ((value & 0x80) != 0) psg.Latch = (value >> 4) & 7;
            int ch = psg.Latch >> 1;
            psg.Used[ch] = true;
            if ((psg.Latch & 1) != 0) psg.Attenuation[ch] = value & 15;
            else if (ch == 3) psg.Tone[ch] = value & 7;
            else if ((value & 0x80) != 0) psg.Tone[ch] = (psg.Tone[ch] & 0x3f0) | (value & 15);
            else psg.Tone[ch] = (psg.Tone[ch] & 15) | ((value & 0x3f) << 4);
        }
    }

    /// <summary>PCM音源チップが使われたことを記録する。</summary>
    public void WritePcm(string chipName)
    {
        lock (_sync)
        {
            if (chipName == "OKIM6258") _okiUsed = true;
        }
    }

    /// <summary>FMのレジスタ番号から対象のチャンネルを求める。</summary>
    private static bool TryGetFmChannel(FmChip chip, int port, byte reg, out int channel)
    {
        channel = -1;
        if (chip.Name == "YM2151")
        {
            if (reg is < 0x20 or > 0xff) return false;
            channel = reg & 7;
        }
        else
        {
            bool channelRegister = reg is >= 0x30 and <= 0x9f or >= 0xa0 and <= 0xa6 or >= 0xb0 and <= 0xb6;
            int local = reg & 3;
            if (!channelRegister || local >= 3) return false;
            channel = port * 3 + local;
        }
        return channel >= 0 && channel < chip.Used.Length;
    }

    private static int? MidiNote(double frequency) => frequency > 0
        ? Math.Clamp((int)Math.Round(69 + 12 * Math.Log2(frequency / 440)), 0, 127) : null;

    // FMのエンベロープのレートは対数的に効く。この時間はARとRRの曲線を表示用に近似した上限付きの値。
    // 音源チップのキースケールやオペレーターごとの音は、この監視で使う混合後の出力からは分からない。
    private static double AttackSeconds(int rate) => Math.Clamp(12 * Math.Pow(2, -Math.Clamp(rate, 0, 31) / 3.0), .015, 12);
    private static double ReleaseSeconds(int rate) => Math.Clamp(12 * Math.Pow(2, -Math.Clamp(rate, 0, 15) / 2.0), .06, 12);

    /// <summary>表示用エンベロープを経過時間ぶん進める。キーオンの立ち上がりで0からアタックし、キーオフでリリースする。</summary>
    private static double AdvanceEnvelope(FmEnvelope envelope, bool keyOn, int keyGeneration, double seconds,
        double attackSeconds, double releaseSeconds, bool playing)
    {
        // 初回や巻き戻し時は状態を初期化する。
        if (!envelope.Initialized || seconds < envelope.LastSeconds)
        {
            envelope.Initialized = true;
            envelope.LastSeconds = seconds;
            envelope.Level = 0;
            envelope.WasKeyOn = false;
            envelope.KeyGeneration = keyGeneration;
        }

        // キーオンの立ち上がりと再キーオンでは0からやり直す。
        double elapsed = Math.Max(0, seconds - envelope.LastSeconds);
        bool retriggered = keyOn && (!envelope.WasKeyOn || keyGeneration != envelope.KeyGeneration);
        if (retriggered) envelope.Level = 0;

        // 停止中は0、キーオン中はアタック、キーオフ中はリリースで進める。
        if (!playing)
        {
            envelope.Level = 0;
            keyOn = false;
        }
        else if (keyOn)
        {
            envelope.Level = Math.Min(1, envelope.Level + elapsed / Math.Max(.001, attackSeconds));
        }
        else
        {
            envelope.Level = Math.Max(0, envelope.Level - elapsed / Math.Max(.001, releaseSeconds));
        }

        envelope.LastSeconds = seconds;
        envelope.WasKeyOn = keyOn;
        envelope.KeyGeneration = keyGeneration;
        return envelope.Level;
    }

    /// <summary>FM音源チップのレジスタ一覧を表示用に返す。</summary>
    public MusicRegisterBank[] CaptureRegisters()
    {
        lock (_sync)
        {
            return _fm.SelectMany(chip => Enumerable.Range(0, chip.Name is "YM2612" or "YM2608" or "YM2610" ? 2 : 1)
                .Select(port => new MusicRegisterBank
                {
                    ChipName = chip.DisplayName, Port = port,
                    Values = chip.Registers.AsSpan(port * 256, 256).ToArray()
                })).ToArray();
        }
    }

    /// <summary>FM・SSG・PSG・PCMの全チャンネルの表示用情報を作る。</summary>
    public MusicChannelSnapshot[] Capture(VgmVisualSnapshot source)
    {
        lock (_sync)
        {
            var result = new List<MusicChannelSnapshot>();
            foreach (var chip in _fm)
            {
                bool opm = chip.Name == "YM2151";
                var r = chip.Registers;
                for (int ch = 0; ch < chip.Slots.Length; ch++)
                {
                    // YM2610の1・4チャンネルは存在しないので表示しない。
                    if (chip.Name == "YM2610" && ch is 0 or 3) continue;
                    int local = ch % 3, port = ch / 3;
                    int bank = port * 256;
                    // アルゴリズム、フィードバック、パンを読む。
                    int alg = r[opm ? 0x20 + ch : bank + 0xb0 + local] & 7;
                    int feedback = (r[opm ? 0x20 + ch : bank + 0xb0 + local] >> 3) & 7;
                    int pan = chip.Name == "YM2203" ? 3 : (r[opm ? 0x20 + ch : bank + 0xb4 + local] >> 6) & 3;
                    // OPNのステレオ対応コアはB4〜B6をC0にリセットするが、その書込みを省いた記録がある。
                    // レジスタ表示には最後に書かれた値をそのまま出し、未書込みのパンは中央として、明示的な0（ミュート）と区別する。
                    if (!opm && chip.Name != "YM2203" && !chip.RegistersWritten[bank + 0xb4 + local]) pan = 3;
                    // OPMはビット7が右、OPNはビット7が左。
                    if (opm) pan = ((pan & 1) << 1) | ((pan & 2) >> 1);
                    // OPMはKC・KF、OPN系はF-Numberとブロックから周波数を求める。
                    double frequency;
                    if (opm)
                    {
                        int kc = r[0x28 + ch];
                        int[] notes = [1, 2, 3, 3, 4, 5, 6, 6, 7, 8, 9, 9, 10, 11, 12, 12];
                        double note = ((kc >> 4) & 7) * 12 + 12 + notes[kc & 15] + (r[0x30 + ch] >> 2) / 64.0;
                        frequency = 440 * Math.Pow(2, (note - 69) / 12) * chip.Clock / 3579545.0;
                    }
                    else
                    {
                        int high = r[bank + 0xa4 + local];
                        int fnum = r[bank + 0xa0 + local] | ((high & 7) << 8);
                        int block = (high >> 3) & 7;
                        double divider = chip.Name == "YM2203" ? 72 : 144;
                        frequency = fnum * chip.Clock * Math.Pow(2, block - 1) / (divider * 1048576.0);
                    }
                    // アルゴリズムごとのキャリアのうち最も大きい総レベルを音量とし、そのアタック・リリースのレートを使う。
                    int[] carriers = [8, 8, 8, 8, 10, 14, 14, 15];
                    double level = 0;
                    int attackRate = 0, releaseRate = 0;
                    for (int slot = 0; slot < 4; slot++)
                    {
                        if ((carriers[alg] & (1 << slot)) == 0) continue;
                        int tl = r[opm ? 0x60 + slot * 8 + ch : bank + 0x40 + slot * 4 + local] & 127;
                        double carrierLevel = Math.Pow(10, -tl * .75 / 20);
                        int carrierAttack = r[opm ? 0x80 + slot * 8 + ch : bank + 0x50 + slot * 4 + local] & 31;
                        int carrierRelease = r[opm ? 0xe0 + slot * 8 + ch : bank + 0x80 + slot * 4 + local] & 15;
                        if (carrierLevel > level + .000001)
                        {
                            level = carrierLevel;
                            attackRate = carrierAttack;
                            releaseRate = carrierRelease;
                        }
                        else if (Math.Abs(carrierLevel - level) <= .000001)
                        {
                            attackRate = Math.Max(attackRate, carrierAttack);
                            releaseRate = Math.Min(releaseRate, carrierRelease);
                        }
                    }
                    // YM2612の6チャンネル目はDACが有効ならFMとして扱わない。
                    bool dac = chip.Name == "YM2612" && ch == 5 && (r[0x2b] & 0x80) != 0;
                    bool keyed = chip.Slots[ch] != 0 && !dac;
                    // 表示用エンベロープを進め、レベルメーターの値を求める。
                    double attackSeconds = AttackSeconds(attackRate);
                    double releaseSeconds = ReleaseSeconds(releaseRate);
                    double envelope = AdvanceEnvelope(chip.Envelopes[ch], keyed, chip.KeyGenerations[ch], source.CurrentTime.TotalSeconds,
                        attackSeconds, releaseSeconds, source.IsPlaying);
                    bool active = keyed && pan != 0 && source.IsPlaying;
                    double meterLevel = source.IsPlaying && pan != 0 ? envelope * level : 0;
                    // ハードウェアLFOによる音程の揺れ（PMS）が有効か判定する。
                    bool hasHwPm = opm
                        ? ((r[0x38 + ch] >> 4) & 7) != 0
                        : chip.Name != "YM2203" && (r[0x22] & 8) != 0 && (r[bank + 0xb4 + local] & 7) != 0;
                    result.Add(new()
                    {
                        Id = $"{chip.DisplayName}:FM:{ch}", Label = $"FM{ch + 1:00}", ChipName = chip.DisplayName,
                        Kind = MusicChannelKind.Fm, NoteNumber = MidiNote(frequency), Frequency = frequency,
                        IsKeyOn = active, KeyGeneration = chip.KeyGenerations[ch], HasHardwarePitchLfo = hasHwPm, IsInUse = chip.Used[ch], Level = meterLevel, Pan = pan, Volume = (int)Math.Round(level * 127),
                        UsesEnvelopeTiming = true, AttackSeconds = attackSeconds, ReleaseSeconds = releaseSeconds,
                        IsReleasing = !keyed && envelope > 0,
                        Algorithm = alg, Feedback = feedback, SlotMask = chip.Slots[ch], DataString = chip.LastData[ch] ?? ""
                    });
                }
                // OPN系のSSG部分を追加する。
                if (chip.Name is "YM2203" or "YM2608" or "YM2610") CaptureSsg(chip, source.IsPlaying, result);
            }
            // SN76489（2個目を含む）の4チャンネルを追加する。
            if (_psgClock > 0 || _hasSecondPsg)
            {
                int count = _hasSecondPsg ? 2 : 1;
                for (int inst = 0; inst < count; inst++)
                {
                    uint currentClock = inst == 0 ? _psgClock : _secondPsgClock;
                    double psgClock = currentClock > 0 ? currentClock : 3579545;
                    var psg = _psg[inst];
                    string chipName = inst == 0 ? "SN76489" : "SN76489 #2";
                    for (int ch = 0; ch < 4; ch++)
                    {
                        double frequency = ch < 3 ? psgClock / (32.0 * Math.Max(1, psg.Tone[ch])) : 0;
                        int pan = (((psg.Stereo >> (ch + 4)) & 1) << 1) | ((psg.Stereo >> ch) & 1);
                        double level = psg.Attenuation[ch] == 15 ? 0 : Math.Pow(10, -psg.Attenuation[ch] / 10.0);
                        result.Add(new()
                        {
                            Id = $"{chipName}:{ch}", Label = ch == 3 ? "NOISE" : $"PSG{ch + 1:00}", ChipName = chipName,
                            Kind = ch == 3 ? MusicChannelKind.Noise : MusicChannelKind.Psg,
                            Frequency = ch < 3 ? frequency : null, NoteNumber = ch < 3 ? MidiNote(frequency) : null,
                            IsKeyOn = level > 0 && pan != 0 && source.IsPlaying, IsInUse = psg.Used[ch], Level = source.IsPlaying && pan != 0 ? level : 0,
                            Volume = 15 - psg.Attenuation[ch], Pan = pan, DataString = ch == 3 ? $"N:{psg.Tone[ch]:X1}" : $"T:{psg.Tone[ch]:X3}"
                        });
                    }
                }
            }
            // YM2612のDACとOKIM6258（PCM8）を追加する。
            if (_hasDac) AddPcm(result, source, "YM2612", "DAC", 1, _dacUsed);
            if (_hasOki) AddPcm(result, source, "OKIM6258", "PCM8", 0, _okiUsed);
            return result.ToArray();
        }
    }

    /// <summary>PCMチャンネル1個分の表示用情報を追加する。</summary>
    private static void AddPcm(List<MusicChannelSnapshot> target, VgmVisualSnapshot source, string chip, string label, int index, bool inUse)
    {
        var pcm = source.PcmChannels[index];
        target.Add(new() { Id = $"{chip}:{label}", Label = label, ChipName = chip, Kind = chip == "OKIM6258" ? MusicChannelKind.Adpcm : MusicChannelKind.Pcm,
            IsKeyOn = pcm.IsActive && source.IsPlaying, IsInUse = inUse, Level = source.IsPlaying ? pcm.Level : 0, Pan = pcm.Pan });
    }

    /// <summary>OPN系のSSG 3チャンネルの表示用情報を追加する。</summary>
    private static void CaptureSsg(FmChip chip, bool playing, List<MusicChannelSnapshot> target)
    {
        var r = chip.Registers;
        for (int ch = 0; ch < 3; ch++)
        {
            int period = r[ch * 2] | ((r[ch * 2 + 1] & 15) << 8);
            double frequency = chip.Clock / ((chip.Name is "YM2608" or "YM2610" ? 64.0 : 32.0) * Math.Max(1, period));
            bool tone = (r[7] & (1 << ch)) == 0, noise = (r[7] & (8 << ch)) == 0;
            int volume = r[8 + ch] & 15;
            bool envelope = (r[8 + ch] & 16) != 0;
            bool active = playing && (tone || noise) && (envelope || volume != 0);
            target.Add(new() { Id = $"{chip.DisplayName}:SSG:{ch}", Label = $"SSG{ch + 1:00}", ChipName = chip.DisplayName,
                Kind = tone ? MusicChannelKind.Psg : MusicChannelKind.Noise, IsKeyOn = active,
                IsInUse = chip.SsgUsed[ch],
                NoteNumber = tone ? MidiNote(frequency) : null, Frequency = tone ? frequency : null,
                Level = active ? (envelope ? .6 : Math.Pow(10, (volume - 15) * 1.5 / 20)) : 0,
                Volume = envelope ? null : volume, DataString = envelope ? "ENV" : $"T:{period:X3}" });
        }
    }
}
