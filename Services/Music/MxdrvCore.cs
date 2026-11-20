using System.Buffers.Binary;

namespace MirrorFlux.Services.Music;

/// <summary>
/// MDX の解釈器。
/// コマンドの動きは MXDRVg 2.06+17 と照らし合わせて確かめている。
/// マネージドで作り直したもので、そのままの移植でも完全な互換の保証でもない。
/// コマンドの参照元: https://github.com/yosshin4004/portable_mdx/blob/master/src/mxdrv/mxdrv.cpp 。
/// MDX のデータ形式とコマンド一覧: https://w.atwiki.jp/mxdrv/pages/23.html （MXDRV データ資料）。
/// MDX のコマンド、音程の計算、ハードウェア・ソフトウェア LFO、ポルタメント、ゲートの時間、音量の減衰、トラック間の同期を直接解釈する。
/// </summary>
public sealed class MxdrvCore
{
    // アルゴリズムごとのキャリアのスロット。
    private static readonly byte[] CarrierSlotTable = [0x08, 0x08, 0x08, 0x08, 0x0c, 0x0e, 0x0e, 0x0f];

    // 音量0〜15を YM2151 の減衰量に直す表。
    private static readonly byte[] VolumeTable =
    [
        0x2a, 0x28, 0x25, 0x22, 0x20, 0x1d, 0x1a, 0x18,
        0x15, 0x12, 0x10, 0x0d, 0x0a, 0x08, 0x05, 0x02
    ];

    // PCM の減衰量を PCM8 の音量に直す表。
    private static readonly byte[] PcmVolumeTable =
    [
        0x0f, 0x0f, 0x0f, 0x0e, 0x0e, 0x0e, 0x0d, 0x0d,
        0x0d, 0x0c, 0x0c, 0x0b, 0x0b, 0x0b, 0x0a, 0x0a,
        0x0a, 0x09, 0x09, 0x08, 0x08, 0x08, 0x07, 0x07,
        0x07, 0x06, 0x06, 0x05, 0x05, 0x05, 0x04, 0x04,
        0x04, 0x03, 0x03, 0x02, 0x02, 0x02, 0x01, 0x01,
        0x01, 0x00, 0x00, 0xff
    ];

    // 音名（オクターブ×12＋音名）を YM2151 のキーコードに直す表。
    private static readonly byte[] KeyCodeTable =
    [
        0x00, 0x01, 0x02, 0x04, 0x05, 0x06, 0x08, 0x09, 0x0a, 0x0c, 0x0d, 0x0e,
        0x10, 0x11, 0x12, 0x14, 0x15, 0x16, 0x18, 0x19, 0x1a, 0x1c, 0x1d, 0x1e,
        0x20, 0x21, 0x22, 0x24, 0x25, 0x26, 0x28, 0x29, 0x2a, 0x2c, 0x2d, 0x2e,
        0x30, 0x31, 0x32, 0x34, 0x35, 0x36, 0x38, 0x39, 0x3a, 0x3c, 0x3d, 0x3e,
        0x40, 0x41, 0x42, 0x44, 0x45, 0x46, 0x48, 0x49, 0x4a, 0x4c, 0x4d, 0x4e,
        0x50, 0x51, 0x52, 0x54, 0x55, 0x56, 0x58, 0x59, 0x5a, 0x5c, 0x5d, 0x5e,
        0x60, 0x61, 0x62, 0x64, 0x65, 0x66, 0x68, 0x69, 0x6a, 0x6c, 0x6d, 0x6e,
        0x70, 0x71, 0x72, 0x74, 0x75, 0x76, 0x78, 0x79, 0x7a, 0x7c, 0x7d, 0x7e
    ];

    /// <summary>
    /// 1トラック分の演奏状態。
    /// MXDRV のチャンネルワークに対応する。
    /// </summary>
    public sealed class ChannelWork
    {
        public int Ptr;
        public byte PcmBank;
        public int VoiceOffset;
        public int BendDelta;
        public int BendOffset;
        public short Detune;
        public ushort NoteDetune;
        public ushort NoteFullPitch;
        public byte Flags16;
        public byte Flags17;
        public byte ChannelNumber;
        public byte CarrierSlotMask;
        public byte StepLength;
        public byte GateLength;
        public byte PanFlCon;
        public byte KeyonSlotMask;
        public byte QValue;
        public byte KeyDelay;
        public byte KeyDelayCounter;
        public byte PmsAms;
        public byte Volume;
        public byte LastVolume;
        public byte LfoDelay;
        public byte LfoDelayCounter;
        public byte PitchLfoType;
        public int PitchLfoOffsetStart;
        public int PitchLfoDeltaStart;
        public int PitchLfoDelta;
        public int PitchLfoOffset;
        public ushort PitchLfoCookedLen;
        public ushort PitchLfoLen;
        public ushort PitchLfoCounter;
        public byte VolLfoType;
        public ushort VolLfoDeltaStart;
        public ushort VolLfoCookedDelta;
        public ushort VolLfoDelta;
        public ushort VolLfoOffset;
        public ushort VolLfoLen;
        public ushort VolLfoCounter;

        // ミュージックプレイヤーの表示用の値。
        public int? CurrentNote;
        public bool IsKeyOn => (Flags16 & 0x08) != 0;
        public bool IsInUse => Ptr > 0;
        public int CurrentVoice;
        public int PcmSampleIndex;
    }

    /// <summary>
    /// 曲全体の状態。
    /// MXDRV のグローバルワークに対応する。
    /// </summary>
    public sealed class GlobalWork
    {
        public ushort TickCount;
        public byte Pcm8Enabled;
        public readonly byte[] SyncFlags = new byte[16];
        public ushort ChannelMaskTrue = 0x01ff;
        public byte TempoTimer = 200;
        public byte Paused;
        public byte Ended;
        public byte FadeoutOffset;
        public byte FadeoutFlag;
        public byte FadeoutEnable;
        public ushort ChannelEnable = 0x01ff;
        public ushort ChannelMask;
        public readonly ushort[] FadeoutSpeed = new ushort[2];
        public ushort FadeoutCounter;
        public int VoiceTableOffset;
        public int PdxTableOffset;
        public byte HasMdx;
        public byte HasPdx;
        public byte NoiseFrequency;
        public ushort LoopCount;
        public ulong PlayTime;
        public byte MusicTimer = 200;
    }

    private readonly byte[] _mdxData;
    private readonly byte[]? _pdxData;
    private readonly ChannelWork[] _channelsFm = new ChannelWork[9];
    private readonly ChannelWork[] _channelsPcm = new ChannelWork[7];
    private readonly GlobalWork _global = new();
    private readonly byte[] _opmRegisters = new byte[256];
    private ushort _randomSeed = 0x1234;
    private int _loopMarks;

    public Action<int, byte>? WriteOpmCallback { get; set; }
    public Action<int, int, int, int, int>? PcmOutCallback { get; set; }
    public Action<int>? PcmStopCallback { get; set; }

    public GlobalWork Global => _global;
    public ChannelWork[] FmChannels => _channelsFm;
    public ChannelWork[] PcmChannels => _channelsPcm;
    public byte[] OpmRegisters => _opmRegisters;
    public byte Tempo => _global.TempoTimer;
    public int LoopCount => _global.LoopCount;
    public bool Ended => _global.Ended != 0;

    /// <summary>MDX と PDX を複製して持ち、FM9トラックと PCM8 の7トラックを用意して演奏を初期化する。</summary>
    public MxdrvCore(byte[] mdxData, byte[]? pdxData)
    {
        _mdxData = (byte[])mdxData.Clone();
        _pdxData = pdxData != null ? (byte[])pdxData.Clone() : null;

        for (int i = 0; i < 9; i++)
        {
            _channelsFm[i] = new ChannelWork { ChannelNumber = (byte)i };
        }
        for (int i = 0; i < 7; i++)
        {
            _channelsPcm[i] = new ChannelWork { ChannelNumber = (byte)((i + 8) & 0x07 | 0x80) };
        }

        InitDriver();
    }

    /// <summary>
    /// ドライバーを初期化する。
    /// 各トラックの開始位置を読み、YM2151 のタイマーなどを設定する。
    /// </summary>
    private void InitDriver()
    {
        _global.HasMdx = 1;
        _global.HasPdx = (byte)(_pdxData != null ? 1 : 0);

        // MDX の基準位置（タイトルと PDX 名の後ろ）を求め、音色表とトラック表を読む。
        int baseOffset = FindMdxBaseOffset(_mdxData);
        int voiceOffset = ReadBigWord(_mdxData, baseOffset);
        _global.VoiceTableOffset = baseOffset + voiceOffset;

        int firstTrackOffset = ReadBigWord(_mdxData, baseOffset + 2);
        int trackCount = (firstTrackOffset - 2) / 2;

        if (_pdxData != null && _pdxData.Length >= 8)
        {
            _global.PdxTableOffset = 0;
        }

        // 曲全体の状態を初期化する。
        _global.TickCount = 0;
        _global.LoopCount = 0;
        _loopMarks = 0;
        _global.PlayTime = 0;
        _global.Ended = 0;
        _global.Paused = 0;
        _global.FadeoutOffset = 0;
        _global.FadeoutFlag = 0;
        _global.FadeoutEnable = 0;
        _global.ChannelEnable = 0x01ff;
        _global.ChannelMaskTrue = 0x01ff;
        _global.ChannelMask = 0;
        _global.Pcm8Enabled = 0;

        // FM 0〜7 と PCM（トラック9）の状態を初期化する。
        for (int i = 0; i < 9; i++)
        {
            var ch = _channelsFm[i];
            ch.ChannelNumber = i < 8 ? (byte)i : (byte)0x80;
            ch.Volume = 8;
            ch.LastVolume = 0xff;
            ch.PanFlCon = i < 8 ? (byte)0xc0 : (byte)0x10;
            ch.QValue = 8;
            ch.StepLength = 1;
            ch.GateLength = 0;
            ch.KeyonSlotMask = 0;
            ch.Flags16 = 0;
            ch.Flags17 = 0;
            ch.Detune = 0;
            ch.BendDelta = 0;
            ch.BendOffset = 0;
            ch.KeyDelay = 0;
            ch.KeyDelayCounter = 0;
            ch.CurrentNote = null;
            ch.CurrentVoice = 0;

            if (i < trackCount)
            {
                int trkRel = ReadBigWord(_mdxData, baseOffset + 2 + i * 2);
                ch.Ptr = baseOffset + trkRel;
            }
            else
            {
                ch.Ptr = 0;
            }
        }

        // PCM8 の追加の7トラック（トラック10〜16）を初期化する。
        for (int i = 0; i < 7; i++)
        {
            var ch = _channelsPcm[i];
            ch.ChannelNumber = (byte)((i + 9) & 0x07 | 0x80);
            ch.Volume = 8;
            ch.LastVolume = 0xff;
            ch.PanFlCon = 0x10;
            ch.QValue = 8;
            ch.StepLength = 1;
            ch.GateLength = 0;
            ch.KeyonSlotMask = 0;
            ch.Flags16 = 0;
            ch.Flags17 = 0;
            ch.Detune = 0;
            ch.BendDelta = 0;
            ch.BendOffset = 0;
            ch.KeyDelay = 0;
            ch.KeyDelayCounter = 0;
            ch.CurrentNote = null;
            ch.CurrentVoice = 0;

            int trackIdx = i + 9;
            if (trackIdx < trackCount)
            {
                int trkRel = ReadBigWord(_mdxData, baseOffset + 2 + trackIdx * 2);
                ch.Ptr = baseOffset + trkRel;
            }
            else
            {
                ch.Ptr = 0;
            }
        }

        // YM2151 のレジスタを初期化する。テンポの既定値は200で、タイマー B を動かし始める。
        WriteOpm(0x01, 0x00);
        WriteOpm(0x0f, 0x00);
        WriteOpm(0x19, 0x00);
        WriteOpm(0x1b, 0x80);
        WriteOpm(0x12, 0xc8);
        _global.TempoTimer = 0xc8;
        _global.MusicTimer = 0xc8;
        WriteOpm(0x14, 0x3a);
    }

    /// <summary>
    /// MDX の基準位置を求める。
    /// タイトル（0x0d 0x0a または 0x0a まで、続く EOF の印は任意）と、0終端の PDX 名を読み飛ばす。
    /// </summary>
    private static int FindMdxBaseOffset(byte[] data)
    {
        int p = 0;
        while (p < data.Length)
        {
            byte c = data[p++];
            if (c == 0x0d || c == 0x0a) break;
        }
        if (p < data.Length && data[p] == 0x0a) p++;
        if (p < data.Length && data[p] == 0x1a) p++;

        while (p < data.Length)
        {
            if (data[p++] == 0x00) break;
        }
        return p;
    }

    /// <summary>
    /// ビッグエンディアンの16ビット値を読む。
    /// 範囲外なら0を返す。
    /// </summary>
    private static ushort ReadBigWord(byte[] data, int offset)
    {
        if (offset + 1 >= data.Length) return 0;
        return (ushort)((data[offset] << 8) | data[offset + 1]);
    }

    /// <summary>YM2151 へ書き込み、レジスタの控えを更新して書き込み先へ伝える。</summary>
    private void WriteOpm(int reg, byte val)
    {
        reg &= 0xff;
        _opmRegisters[reg] = val;
        WriteOpmCallback?.Invoke(reg, val);
    }

    /// <summary>YM2151 の割り込み1回分（タイマー B / L_OPMINT）を進める。</summary>
    public void Tick()
    {
        if (_global.Ended != 0 || _global.Paused != 0) return;

        // フェードアウトを進める。減衰が上限に達したら曲を終える。
        if (_global.FadeoutEnable != 0)
        {
            if ((sbyte)_global.FadeoutEnable < 0)
            {
                _global.FadeoutEnable = 0x7f;
                _global.FadeoutSpeed[1] = _global.FadeoutSpeed[0];
            }
            else
            {
                if ((short)_global.FadeoutSpeed[1] < 0)
                {
                    if (_global.FadeoutOffset >= 0x3e)
                    {
                        _global.FadeoutEnable = 0;
                        _global.Ended = 1;
                        AllChannelsOff();
                        return;
                    }
                    _global.FadeoutOffset++;
                    _global.FadeoutSpeed[1] = _global.FadeoutSpeed[0];
                }
                else
                {
                    _global.FadeoutSpeed[1] -= 2;
                }
            }
        }

        // タイマーを書き直し、ティック数と演奏時間を進める。
        WriteOpm(0x14, 0x1b);
        _global.TickCount++;
        _global.PlayTime += (ulong)(256 - _global.MusicTimer);

        // FM 0〜7 と PCM（チャンネル8）を進める。
        for (int i = 0; i < 9; i++)
        {
            var ch = _channelsFm[i];
            if (ch.Ptr == 0) continue;
            StepChannel(ch, i);
        }

        // PCM8 が有効なら、チャンネル9〜15も進める。
        if (_global.Pcm8Enabled != 0)
        {
            for (int i = 0; i < 7; i++)
            {
                var ch = _channelsPcm[i];
                if (ch.Ptr == 0) continue;
                StepChannel(ch, i + 9);
            }
        }

        // 割り込みを受け付けてタイマーを再設定する。
        WriteOpm(0x14, 0x3a);
    }

    /// <summary>
    /// 1トラックを1ティック進める。
    /// ベンド・LFO、ゲートとキーオフ、次のイベントの順に処理する。
    /// </summary>
    private void StepChannel(ChannelWork ch, int chIndex)
    {
        // L001050: ピッチベンド・ポルタメントと LFO。
        L001050(ch);

        // L0011b4: ゲートの確認とキーオフ。
        L0011b4(ch, chIndex);

        // マスクされていなければ、L000c66 で次のイベントを処理する。
        if ((_global.ChannelMask & (1 << chIndex)) == 0)
        {
            L000c66(ch, chIndex);
        }
    }

    /// <summary>
    /// L001050: ポルタメントと LFO を進める。
    /// PCM のトラックは扱わない。
    /// </summary>
    private void L001050(ChannelWork ch)
    {
        if ((sbyte)ch.ChannelNumber < 0) return;

        // ポルタメント。キーオンの遅延が終わっていればベンド量を足す。
        if ((ch.Flags16 & 0x80) != 0 && ch.KeyDelayCounter == 0)
        {
            ch.BendOffset += ch.BendDelta;
        }

        // LFO の開始遅延。遅延が終わったら LFO を初期位置から始める。
        if (ch.LfoDelay != 0)
        {
            if (ch.KeyDelayCounter != 0) return;
            if (ch.KeyDelayCounter == 0 && ch.LfoDelayCounter != 0)
            {
                if (--ch.LfoDelayCounter == 0)
                {
                    if ((ch.Flags16 & 0x20) != 0)
                    {
                        ch.PitchLfoCounter = ch.PitchLfoCookedLen;
                        ch.PitchLfoDelta = ch.PitchLfoDeltaStart;
                        ch.PitchLfoOffset = ch.PitchLfoOffsetStart;
                    }
                    if ((ch.Flags16 & 0x40) != 0)
                    {
                        ch.VolLfoCounter = ch.VolLfoLen;
                        ch.VolLfoDelta = ch.VolLfoDeltaStart;
                        ch.VolLfoOffset = ch.VolLfoCookedDelta;
                    }
                }
                return;
            }
        }

        // ピッチ LFO。1=のこぎり波、2=矩形波、3=三角波、4=ランダム。
        if ((ch.Flags16 & 0x20) != 0)
        {
            switch (ch.PitchLfoType)
            {
                case 1:
                    ch.PitchLfoOffset += ch.PitchLfoDelta;
                    if (--ch.PitchLfoCounter == 0)
                    {
                        ch.PitchLfoCounter = ch.PitchLfoLen;
                        ch.PitchLfoOffset = -ch.PitchLfoOffset;
                    }
                    break;
                case 2:
                    ch.PitchLfoOffset = ch.PitchLfoDelta;
                    if (--ch.PitchLfoCounter == 0)
                    {
                        ch.PitchLfoCounter = ch.PitchLfoLen;
                        ch.PitchLfoDelta = -ch.PitchLfoDelta;
                    }
                    break;
                case 3:
                    ch.PitchLfoOffset += ch.PitchLfoDelta;
                    if (--ch.PitchLfoCounter == 0)
                    {
                        ch.PitchLfoCounter = ch.PitchLfoLen;
                        ch.PitchLfoDelta = -ch.PitchLfoDelta;
                    }
                    break;
                case 4:
                    if (--ch.PitchLfoCounter == 0)
                    {
                        ch.PitchLfoOffset = NextLfoRandom() * (short)ch.PitchLfoDelta;
                        ch.PitchLfoCounter = ch.PitchLfoLen;
                    }
                    break;
                default: break;
            }
        }

        // 音量 LFO。1=のこぎり波、2=矩形波、3=三角波、4=ランダム。
        if ((ch.Flags16 & 0x40) != 0)
        {
            switch (ch.VolLfoType)
            {
                case 1:
                    ch.VolLfoOffset += ch.VolLfoDelta;
                    if (--ch.VolLfoCounter == 0)
                    {
                        ch.VolLfoCounter = ch.VolLfoLen;
                        ch.VolLfoOffset = ch.VolLfoCookedDelta;
                    }
                    break;
                case 2:
                    if (--ch.VolLfoCounter == 0)
                    {
                        ch.VolLfoCounter = ch.VolLfoLen;
                        ch.VolLfoOffset += ch.VolLfoDelta;
                        ch.VolLfoDelta = (ushort)-ch.VolLfoDelta;
                    }
                    break;
                case 3:
                    ch.VolLfoOffset += ch.VolLfoDelta;
                    if (--ch.VolLfoCounter == 0)
                    {
                        ch.VolLfoCounter = ch.VolLfoLen;
                        ch.VolLfoDelta = (ushort)-ch.VolLfoDelta;
                    }
                    break;
                case 4:
                    if (--ch.VolLfoCounter == 0)
                    {
                        ch.VolLfoOffset = (ushort)(NextLfoRandom() * (short)ch.VolLfoDelta);
                        ch.VolLfoCounter = ch.VolLfoLen;
                    }
                    break;
                default: break;
            }
        }
    }

    /// <summary>LFO のランダム波形に使う疑似乱数を次へ進める。</summary>
    private short NextLfoRandom()
    {
        uint value = (uint)_randomSeed * 0xc549 + 12;
        _randomSeed = (ushort)value;
        return (short)(value >> 8);
    }

    /// <summary>L0011b4: 同期待ち、タイ、ゲートを確かめ、ゲートが尽きたらキーオフする。</summary>
    private void L0011b4(ChannelWork ch, int chIndex)
    {
        // 同期待ち中は、合図が来たら次のイベントへ進む。
        if ((ch.Flags17 & 0x08) != 0)
        {
            if (_global.SyncFlags[chIndex] != 0)
            {
                _global.SyncFlags[chIndex] = 0;
                ch.Flags17 &= 0xf7;
                L0011d4(ch);
            }
            return;
        }

        // タイが有効ならキーオフしない。
        if ((ch.Flags16 & 0x04) != 0)
        {
            L0011ce(ch);
            return;
        }

        // ゲートが尽きたらキーオフする。
        ch.GateLength = unchecked((byte)(ch.GateLength - 1));
        if (ch.GateLength == 0)
        {
            L000fe6(ch);
        }

        L0011ce(ch);
    }

    /// <summary>L0011ce: 音の長さを数え、尽きたら次のイベントを読む。</summary>
    private void L0011ce(ChannelWork ch)
    {
        ch.StepLength = unchecked((byte)(ch.StepLength - 1));
        if (ch.StepLength == 0)
        {
            L0011d4(ch);
        }
    }

    // 壊れたデータでも演奏スレッドを止めない。命令の途中でデータが尽きた場合と、音符・休符のないまま命令が続く場合（自分自身へ戻るループなど）は、そのトラックを終了する。
    private const int MaxCommandsWithoutNote = 100000;

    /// <summary>
    /// L0011d4: 次の音符か休符まで読む。
    /// データが壊れていたらそのトラックを終える。
    /// </summary>
    private void L0011d4(ChannelWork ch)
    {
        try { ReadTrackUntilNote(ch); }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException) { EndBrokenTrack(ch); }
    }

    /// <summary>壊れたトラックを止めてキーオフし、全トラックが終わったか確かめる。</summary>
    private void EndBrokenTrack(ChannelWork ch)
    {
        ch.Ptr = 0;
        L000fe6(ch);
        ch.CurrentNote = null;
        CheckAllTracksEnded();
    }

    /// <summary>次の音符か休符まで、コマンドを順に実行する。</summary>
    private void ReadTrackUntilNote(ChannelWork ch)
    {
        // タイとポルタメントを解除する。
        ch.Flags16 &= 0x7b;

        int budget = MaxCommandsWithoutNote;
        while (ch.Ptr > 0 && ch.Ptr < _mdxData.Length)
        {
            if (--budget < 0) { EndBrokenTrack(ch); return; }
            byte cmd = _mdxData[ch.Ptr++];

            // 0x00〜0x7f は休符。値＋1が長さ。
            if (cmd < 0x80)
            {
                ch.CurrentNote = null;
                ch.StepLength = (byte)(cmd + 1);
                ch.GateLength = (byte)(cmd + 1);
                return;
            }

            // 0xe0 以上はコマンド。
            if (cmd >= 0xe0)
            {
                if (ExecuteCommand(ch, cmd)) return;
                continue;
            }

            // 0x80〜0xdf は音符。音程にディチューンを足し、キーオン待ちにして長さとゲートを読む。
            int note = cmd - 0x80;
            ch.CurrentNote = note;
            ch.NoteDetune = (ushort)((note << 6) + 5 + ch.Detune);
            ch.Flags16 |= 0x01;
            ch.KeyDelayCounter = ch.KeyDelay;

            byte dur = _mdxData[ch.Ptr++];
            // ゲートは q が負なら長さから引き、正なら長さ×q/8 にする。
            int gate;
            if ((sbyte)ch.QValue < 0)
            {
                gate = dur + (sbyte)ch.QValue;
                if (gate < 0) gate = 0;
                gate++;
            }
            else
            {
                gate = (dur * ch.QValue >> 3) + 1;
            }

            ch.GateLength = (byte)Math.Clamp(gate, 1, dur + 1);
            ch.StepLength = (byte)(dur + 1);
            return;
        }

        // 終了命令のないままデータの末尾を越えた場合もトラックを終了する。
        if (ch.Ptr >= _mdxData.Length) EndBrokenTrack(ch);
    }

    /// <summary>
    /// トラックがループ位置に戻ったことを記録する。
    /// 動いている全トラックが戻ったらループ回数を1つ増やす。
    /// </summary>
    private void RecordTrackLoop(ChannelWork channel)
    {
        int index = (sbyte)channel.ChannelNumber >= 0 ? channel.ChannelNumber : 8 + (channel.ChannelNumber & 7);
        _loopMarks |= 1 << index;
        int active = 0;
        for (int i = 0; i < 9; i++) if (_channelsFm[i].Ptr != 0) active |= 1 << i;
        if (_global.Pcm8Enabled != 0)
            for (int i = 0; i < 7; i++) if (_channelsPcm[i].Ptr != 0) active |= 1 << (i + 9);
        if (active != 0 && (_loopMarks & active) == active) { _global.LoopCount++; _loopMarks = 0; }
    }

    /// <summary>
    /// コマンドを1つ実行する。
    /// トラックがここで止まる（終了）なら true を返す。
    /// </summary>
    private bool ExecuteCommand(ChannelWork ch, byte cmd)
    {
        switch (cmd)
        {
            // @t テンポ。タイマー B に書く。
            case 0xff:
                byte tempo = _mdxData[ch.Ptr++];
                _global.TempoTimer = tempo;
                _global.MusicTimer = tempo;
                WriteOpm(0x12, tempo);
                break;

            // YM2151 への直接書き込み。テンポのレジスタなら控えも変える。
            case 0xfe:
                byte reg = _mdxData[ch.Ptr++];
                byte val = _mdxData[ch.Ptr++];
                if (reg == 0x12)
                {
                    _global.TempoTimer = val;
                    _global.MusicTimer = val;
                }
                WriteOpm(reg, val);
                break;

            // @ 音色。PCM はバンク番号、FM は音色表から27バイトの音色を探して後で書き込む。
            case 0xfd:
                byte v = _mdxData[ch.Ptr++];
                ch.CurrentVoice = v;
                if ((sbyte)ch.ChannelNumber < 0)
                {
                    ch.PcmBank = v;
                }
                else
                {
                    int vPtr = _global.VoiceTableOffset;
                    while (vPtr + 27 <= _mdxData.Length)
                    {
                        if (_mdxData[vPtr] == v)
                        {
                            ch.VoiceOffset = vPtr + 1;
                            ch.Flags17 |= 0x02;
                            break;
                        }
                        vPtr += 27;
                    }
                }
                break;

            // p 左右。
            case 0xfc:
                byte pan = _mdxData[ch.Ptr++];
                if ((sbyte)ch.ChannelNumber < 0)
                {
                    // MXDRV は PCM の左右を 0 と 3 を入れ替えて持ち、キーオン時に PCM8 の出力マスクへ戻す。これを省くと p3 の曲が鳴らなくなる。
                    if (pan is 0 or 3) pan ^= 3;
                    ch.PanFlCon = (byte)((ch.PanFlCon & 0xfc) | (pan & 0x03));
                }
                else
                {
                    // FM は 0x20 のレジスタの上位2ビットに入れ、後で書き込む。
                    ch.PanFlCon = (byte)((ch.PanFlCon & 0x3f) | ((pan & 3) << 6));
                    ch.Flags17 |= 0x04;
                }
                break;

            // v 音量。
            case 0xfb:
                ch.Volume = _mdxData[ch.Ptr++];
                ch.Flags17 |= 0x01;
                break;

            // ( 音量を下げる。0x80 以上は減衰量を直接持つので値を増やす。
            // MXDRV データ資料は $FA を「音量増大」と書いているが、MXDRV 本体（L001328）は v を1下げるので本体に合わせる。
            case 0xfa:
                if ((sbyte)ch.Volume < 0)
                {
                    if (ch.Volume != 0xff) ch.Volume++;
                }
                else
                {
                    if (ch.Volume > 0) ch.Volume--;
                }
                ch.Flags17 |= 0x01;
                break;

            // ) 音量を上げる。0x80 以上は減衰量を直接持つので値を減らす（$F9。本体は L001344）。
            case 0xf9:
                if ((sbyte)ch.Volume < 0)
                {
                    if (ch.Volume != 0x80) ch.Volume--;
                }
                else
                {
                    if (ch.Volume < 15) ch.Volume++;
                }
                ch.Flags17 |= 0x01;
                break;

            // q / Q ゲート。
            case 0xf8:
                ch.QValue = _mdxData[ch.Ptr++];
                break;

            // & タイ。
            case 0xf7:
                ch.Flags16 |= 0x04;
                break;

            // [ 繰り返しの開始。回数を数えるための場所に回数を写す。
            case 0xf6:
                byte count = _mdxData[ch.Ptr++];
                _mdxData[ch.Ptr++] = count;
                break;

            // ] 繰り返しの終わり。回数を減らし、残っていれば開始位置へ戻る。
            case 0xf5:
                short backOffset = (short)ReadBigWord(_mdxData, ch.Ptr);
                ch.Ptr += 2;
                int countPos = ch.Ptr + backOffset - 1;
                if (countPos >= 0 && countPos < _mdxData.Length)
                {
                    _mdxData[countPos]--;
                    if (_mdxData[countPos] != 0)
                    {
                        ch.Ptr += backOffset;
                    }
                }
                break;

            // / 繰り返しからの脱出。最後の回なら繰り返しの後ろへ飛ぶ。
            case 0xf4:
                ushort fwdOffset = ReadBigWord(_mdxData, ch.Ptr);
                ch.Ptr += 2;
                int afterLoop = ch.Ptr + fwdOffset;
                if (afterLoop + 2 <= _mdxData.Length)
                {
                    short loopBack = (short)ReadBigWord(_mdxData, afterLoop);
                    int loopCountPos = afterLoop + 2 + loopBack - 1;
                    if (loopCountPos >= 0 && loopCountPos < _mdxData.Length && _mdxData[loopCountPos] == 1)
                    {
                        ch.Ptr = afterLoop + 2;
                    }
                }
                break;

            // D ディチューン。
            case 0xf3:
                ch.Detune = (short)ReadBigWord(_mdxData, ch.Ptr);
                ch.Ptr += 2;
                break;

            // s ポルタメント。
            case 0xf2:
                short bend = (short)ReadBigWord(_mdxData, ch.Ptr);
                ch.Ptr += 2;
                ch.BendDelta = bend << 8;
                ch.Flags16 |= 0x80;
                break;

            // L ループ位置へ戻るか、0ならトラックを終える。
            case 0xf1:
                if (_mdxData[ch.Ptr] == 0) return EndTrack(ch);
                short loopRel = (short)ReadBigWord(_mdxData, ch.Ptr);
                ch.Ptr = ch.Ptr + 2 + loopRel;
                RecordTrackLoop(ch);
                break;

            // k キーオンの遅延。
            case 0xf0:
                ch.KeyDelay = _mdxData[ch.Ptr++];
                break;

            // 同期の合図を送る。
            case 0xef:
                byte target = _mdxData[ch.Ptr++];
                if (target < 16) _global.SyncFlags[target] = 0xff;
                break;

            // 同期の合図を待つ。合図が来ていなければ待ち状態にする。
            case 0xee:
                int chIdx = (sbyte)ch.ChannelNumber >= 0 ? ch.ChannelNumber : (ch.ChannelNumber & 0x07) + 8;
                if (_global.SyncFlags[chIdx] != 0)
                {
                    _global.SyncFlags[chIdx] = 0;
                }
                else
                {
                    ch.Flags17 |= 0x08;
                    return true;
                }
                break;

            // ADPCM/ノイズ周波数設定。チャンネル P では F コマンド（PCM の周波数）、FM チャンネル（MXDRV ではチャンネル H）では
            // w コマンドのノイズ周波数（レジスタ 0x0F。ビット7がノイズの ON/OFF、ON は 128＋周波数）。
            case 0xed:
                byte freq = _mdxData[ch.Ptr++];
                if ((sbyte)ch.ChannelNumber < 0)
                {
                    ch.PanFlCon = (byte)((ch.PanFlCon & 0x03) | ((freq & 7) << 2));
                }
                else
                {
                    _global.NoiseFrequency = freq;
                    WriteOpm(0x0f, freq);
                }
                break;

            // MP ピッチ LFO。0x80 で止め、0x81 で再開し、それ以外は波形・周期・振幅を設定する。
            // MXDRV データ資料は $80 を MPON、$81 を MPOF と書いているが、MXDRV 本体（L0014fc）は $80 で止めるので本体に合わせる。
            case 0xec:
                byte pMode = _mdxData[ch.Ptr++];
                if (pMode == 0x80)
                {
                    ch.Flags16 &= 0xdf;
                    ch.PitchLfoOffset = 0;
                }
                else if (pMode == 0x81)
                {
                    ch.Flags16 |= 0x20;
                    ch.PitchLfoCounter = ch.PitchLfoCookedLen;
                    ch.PitchLfoDelta = ch.PitchLfoDeltaStart;
                    ch.PitchLfoOffset = ch.PitchLfoOffsetStart;
                }
                else
                {
                    ch.Flags16 |= 0x20;
                    ch.PitchLfoType = (byte)((pMode & 3) + 1);
                    // 周期と振幅を読む。モード4以上は振幅を256倍にする。三角波は振幅から始める。
                    ushort pLen = ReadBigWord(_mdxData, ch.Ptr); ch.Ptr += 2;
                    ch.PitchLfoLen = pLen;
                    ch.PitchLfoCookedLen = (ushort)(ch.PitchLfoType == 2 ? pLen : ch.PitchLfoType == 4 ? 1 : pLen >> 1);
                    short pAmp = (short)ReadBigWord(_mdxData, ch.Ptr); ch.Ptr += 2;
                    int pDelta = pAmp << 8;
                    if (pMode >= 4) pDelta <<= 8;
                    ch.PitchLfoDeltaStart = pDelta;
                    ch.PitchLfoOffsetStart = ch.PitchLfoType == 3 ? pDelta : 0;
                    ch.PitchLfoCounter = ch.PitchLfoCookedLen;
                    ch.PitchLfoDelta = ch.PitchLfoDeltaStart;
                    ch.PitchLfoOffset = ch.PitchLfoOffsetStart;
                }
                break;

            // MA 音量 LFO。0x80 で止め、0x81 で再開し、それ以外は波形・周期・振幅を設定する。
            // 資料の MAON/MAOF の割り当ても MXDRV 本体と逆なので、本体に合わせる。
            case 0xeb:
                byte vMode = _mdxData[ch.Ptr++];
                if (vMode == 0x80)
                {
                    ch.Flags16 &= 0xbf;
                    ch.VolLfoOffset = 0;
                }
                else if (vMode == 0x81)
                {
                    ch.Flags16 |= 0x40;
                    ch.VolLfoCounter = ch.VolLfoLen;
                    ch.VolLfoDelta = ch.VolLfoDeltaStart;
                    ch.VolLfoOffset = ch.VolLfoCookedDelta;
                }
                else
                {
                    ch.Flags16 |= 0x40;
                    ch.VolLfoType = (byte)((vMode & 3) + 1);
                    // 周期と振幅を読み、波形に応じた初期の減衰量を求める。
                    ushort vLen = ReadBigWord(_mdxData, ch.Ptr); ch.Ptr += 2;
                    ch.VolLfoLen = vLen;
                    ushort vAmp = ReadBigWord(_mdxData, ch.Ptr); ch.Ptr += 2;
                    ch.VolLfoDeltaStart = vAmp;
                    short initialVolume = unchecked((short)-((vMode & 1) == 0 ? (short)vAmp * (short)vLen : (short)vAmp));
                    ch.VolLfoCookedDelta = (ushort)Math.Max(0, (int)initialVolume);
                    ch.VolLfoCounter = vLen;
                    ch.VolLfoDelta = vAmp;
                    ch.VolLfoOffset = ch.VolLfoCookedDelta;
                }
                break;

            // MH ハードウェア LFO。負の値なら PMS/AMS を書き直すだけ、それ以外は周波数・PMD・AMD・PMS/AMS を設定する。ビット6はキーオン時の LFO リセット。
            case 0xea:
                byte hSync = _mdxData[ch.Ptr++];
                if ((sbyte)hSync < 0)
                {
                    if ((hSync & 1) != 0) WriteOpm(0x38 + ch.ChannelNumber, ch.PmsAms);
                }
                else
                {
                    byte hFreq = _mdxData[ch.Ptr++];
                    byte hPmd = _mdxData[ch.Ptr++];
                    byte hAmd = _mdxData[ch.Ptr++];
                    byte hPmsAms = _mdxData[ch.Ptr++];
                    ch.PmsAms = hPmsAms;
                    ch.Flags16 = (byte)((ch.Flags16 & ~2) | ((hSync & 0x40) != 0 ? 2 : 0));
                    WriteOpm(0x1b, (byte)((_opmRegisters[0x1b] & 0xc0) | (hSync & 0x3f)));
                    WriteOpm(0x18, hFreq);
                    WriteOpm(0x19, hPmd);
                    WriteOpm(0x19, hAmd);
                    WriteOpm(0x38 + ch.ChannelNumber, hPmsAms);
                }
                break;

            // LFO ディレイ設定（MD コマンド）。LFO が効き始めるまでの遅延。
            case 0xe9:
                ch.LfoDelay = _mdxData[ch.Ptr++];
                break;

            // PCM8 モードにして、PCM の追加7トラックを有効にする。
            case 0xe8:
                _global.Pcm8Enabled = 0xff;
                _global.ChannelEnable |= 0xfe00;
                _global.ChannelMaskTrue |= 0xfe00;
                break;

            // 拡張コマンド（$E7 ＋ 副コマンド）。MXDRV 2.06+16/+17 の副コマンドは 0〜6。
            // 0=ERROR（トラックを終える）、1=フェードアウト、2=PCM8 の直接駆動（6バイト。ここでは読み飛ばす）、
            // 3=キーオフする/しない、4=他チャンネルの操作（ここでは引数を読み飛ばすだけ）、5=音長加算、6=フラグ。
            // 引数を読み損なうと後ろのデータを命令として読んでしまうため、扱わない副コマンドも長さだけは合わせる。
            case 0xe7:
                byte ext = _mdxData[ch.Ptr++];
                if (ext == 0) return EndTrack(ch);
                if (ext == 1)
                {
                    byte speed = _mdxData[ch.Ptr++];
                    _global.FadeoutSpeed[0] = speed;
                    _global.FadeoutEnable = 0x7f;
                }
                else if (ext == 2)
                {
                    ch.Ptr += 6;
                }
                else if (ext == 3)
                {
                    byte leg = _mdxData[ch.Ptr++];
                    if (leg != 0) ch.Flags16 |= 0x10;
                    else ch.Flags16 &= 0xef;
                }
                else if (ext == 4)
                {
                    // チャンネル番号と、休符1バイトまたは音符＋音長の2バイト。
                    ch.Ptr++;
                    byte other = _mdxData[ch.Ptr++];
                    if (other is >= 0x80 and < 0xe0) ch.Ptr++;
                }
                else if (ext is 5 or 6)
                {
                    ch.Ptr++;
                }
                else
                {
                    return EndTrack(ch);
                }
                break;

            // $E0〜$E6 は未定義。MXDRV と同じく、そのトラックの演奏を終える。
            case >= 0xe0 and <= 0xe6:
                return EndTrack(ch);
        }

        return false;
    }

    /// <summary>トラックの演奏を終える（$F1 $00、拡張コマンドの ERROR、未定義のコマンド）。</summary>
    private bool EndTrack(ChannelWork ch)
    {
        ch.Ptr = 0;
        L000fe6(ch);
        ch.CurrentNote = null;
        CheckAllTracksEnded();
        return true;
    }

    /// <summary>L000c66: キーオン待ちなら遅延を数えてからキーオンし、そうでなければ音程と音量だけ更新する。</summary>
    private void L000c66(ChannelWork ch, int chIndex)
    {
        // キーオン待ち。キーオンの遅延が残っていれば数える。
        if ((ch.Flags16 & 0x01) != 0)
        {
            if (ch.KeyDelayCounter > 0)
            {
                ch.KeyDelayCounter--;
            }
            else
            {
                // FM は音色と左右を書き、新しく鳴らすときは LFO を初めからにする。ビット1が立っていれば LFO をリセットする。
                if ((sbyte)ch.ChannelNumber >= 0)
                {
                    L000d84(ch);
                    L000e66(ch);
                    if ((ch.Flags16 & 0x08) == 0)
                    {
                        ch.LfoDelayCounter = ch.LfoDelay;
                        if (ch.LfoDelayCounter != 0)
                        {
                            ch.PitchLfoOffset = 0;
                            ch.VolLfoOffset = 0;
                            L001050(ch);
                        }
                        if ((ch.Flags16 & 2) != 0)
                        {
                            WriteOpm(0x01, 0x02);
                            WriteOpm(0x01, 0x00);
                        }
                    }
                    // ベンドを戻して音程と音量を書き、キーオンしてキーオン待ちを解除する。
                    ch.BendOffset = 0;
                    L000cdc(ch);
                    L000dfe(ch);
                }
                L000e7e(ch, chIndex);
                ch.Flags16 &= 0xfe;
            }
        }
        else
        {
            // キーオン待ちでなければ、FM の音程と音量だけ更新する。
            if ((sbyte)ch.ChannelNumber >= 0)
            {
                L000cdc(ch);
                L000dfe(ch);
            }
        }
    }

    /// <summary>
    /// L000cdc: 音程（音符＋ディチューン＋ベンド＋ピッチ LFO）を YM2151 の KC/KF に直して書く。
    /// 変わっていなければ書かない。
    /// </summary>
    private void L000cdc(ChannelWork ch)
    {
        int pitch = ch.NoteDetune + (ch.BendOffset >> 16) + (ch.PitchLfoOffset >> 16);
        ushort fullPitch = (ushort)Math.Clamp(pitch, 0, 0x17ff);
        if (fullPitch == ch.NoteFullPitch) return;
        ch.NoteFullPitch = fullPitch;

        int kfKc = fullPitch * 4;
        WriteOpm(0x30 + ch.ChannelNumber, (byte)(kfKc & 0xfc));
        int kcIdx = Math.Clamp(kfKc >> 8, 0, KeyCodeTable.Length - 1);
        WriteOpm(0x28 + ch.ChannelNumber, KeyCodeTable[kcIdx]);
    }

    /// <summary>
    /// L000d84: 音色が変わっていれば27バイトの音色を YM2151 に書き込む。
    /// キャリアの TL は音量の計算で書くので、ここでは消音にしておく。
    /// </summary>
    private void L000d84(ChannelWork ch)
    {
        if ((ch.Flags17 & 0x02) == 0) return;
        ch.Flags17 &= 0xfd;

        int vPtr = ch.VoiceOffset;
        if (vPtr <= 0 || vPtr + 26 > _mdxData.Length) return;

        // 先頭の FL/CON からアルゴリズムとキャリアを、次のバイトからキーオンするスロットを決める。
        byte b1 = _mdxData[vPtr];
        ch.PanFlCon = (byte)((ch.PanFlCon & 0xc0) | (b1 & 0x3f));
        ch.CarrierSlotMask = CarrierSlotTable[b1 & 0x07];
        byte b2 = _mdxData[vPtr + 1];
        ch.KeyonSlotMask = (byte)((b2 << 3) | ch.ChannelNumber);

        // DT1/MUL（4オペレーター）。
        for (int i = 0; i < 4; i++)
            WriteOpm(0x40 + i * 8 + ch.ChannelNumber, _mdxData[vPtr + 2 + i]);

        // TL（4オペレーター）。キャリアは消音にしておく。
        for (int i = 0; i < 4; i++)
        {
            byte tl = _mdxData[vPtr + 6 + i];
            if ((ch.CarrierSlotMask & (1 << i)) != 0) tl = 0x7f;
            WriteOpm(0x60 + i * 8 + ch.ChannelNumber, tl);
        }

        // AR/KS（4オペレーター）。
        for (int i = 0; i < 4; i++)
            WriteOpm(0x80 + i * 8 + ch.ChannelNumber, _mdxData[vPtr + 10 + i]);

        // D1R/AMS-EN（4オペレーター）。
        for (int i = 0; i < 4; i++)
            WriteOpm(0xa0 + i * 8 + ch.ChannelNumber, _mdxData[vPtr + 14 + i]);

        // D2R/DT2（4オペレーター）。
        for (int i = 0; i < 4; i++)
            WriteOpm(0xc0 + i * 8 + ch.ChannelNumber, _mdxData[vPtr + 18 + i]);

        // D1L/RR（4オペレーター）。
        for (int i = 0; i < 4; i++)
            WriteOpm(0xe0 + i * 8 + ch.ChannelNumber, _mdxData[vPtr + 22 + i]);

        // 左右と音量を書き直す印を付ける。
        ch.LastVolume = 0xff;
        ch.Flags17 |= 0x05;
    }

    /// <summary>L000e66: 左右が変わっていればレジスタ 0x20 に書く。</summary>
    private void L000e66(ChannelWork ch)
    {
        if ((ch.Flags17 & 0x04) == 0) return;
        ch.Flags17 &= 0xfb;
        WriteOpm(0x20 + ch.ChannelNumber, ch.PanFlCon);
    }

    /// <summary>L000dfe: 音量・フェードアウト・音量 LFO から減衰量を求め、キャリアの TL に足して書く。</summary>
    private void L000dfe(ChannelWork ch)
    {
        int vol = ch.Volume;
        int att;
        if ((vol & 0x80) != 0) att = vol & 0x7f;
        else att = VolumeTable[vol & 0x0f];

        att += _global.FadeoutOffset;
        att = Math.Clamp(att, 0, 0x7f);
        att += (ch.VolLfoOffset >> 8);
        att = Math.Clamp(att, 0, 0x7f);

        if (ch.LastVolume == att) return;
        ch.LastVolume = (byte)att;

        int vPtr = ch.VoiceOffset;
        if (vPtr <= 0 || vPtr + 26 > _mdxData.Length) return;

        for (int i = 0; i < 4; i++)
        {
            if ((ch.CarrierSlotMask & (1 << i)) != 0)
            {
                byte baseTl = (byte)(_mdxData[vPtr + 6 + i] & 0x7f);
                int tl = Math.Clamp(baseTl + att, 0, 0x7f);
                WriteOpm(0x60 + i * 8 + ch.ChannelNumber, (byte)tl);
            }
        }
    }

    /// <summary>
    /// L000e7e: キーオンする。
    /// FM はスロットをキーオンし、PCM は PDX のサンプルを PCM8 で鳴らす。
    /// </summary>
    private void L000e7e(ChannelWork ch, int chIndex)
    {
        bool wasKeyedOn = (ch.Flags16 & 0x08) != 0;
        ch.Flags16 |= 0x08;

        // すでに鳴っている音（タイ）は、FM も PCM も2回目のアタックなしで音程だけ変える。
        if (wasKeyedOn) return;
        // レガートなら前の音を止めてから鳴らす。
        if ((ch.Flags16 & 0x10) != 0)
        {
            if ((sbyte)ch.ChannelNumber >= 0)
                WriteOpm(0x08, (byte)ch.ChannelNumber);
            else
                PcmStopCallback?.Invoke(ch.ChannelNumber & 7);
        }

        // FM のキーオン。
        if ((sbyte)ch.ChannelNumber >= 0)
        {
            WriteOpm(0x08, ch.KeyonSlotMask);
        }
        else
        {
            // PCM の発音。バンク×96＋ノートで PDX のサンプルを選ぶ。
            if (_pdxData == null || _global.HasPdx == 0) return;

            int note = ch.CurrentNote ?? (ch.NoteDetune >> 6);
            int pcmChannel = chIndex >= 9 ? chIndex - 8 : 0;
            int bank = ch.PcmBank;
            int sampleIndex = note + bank * 96;
            ch.PcmSampleIndex = sampleIndex;

            // サンプル表から位置と長さを読み、周波数モード・左右・音量を PCM8 に渡す。
            int sampleTablePos = _global.PdxTableOffset + sampleIndex * 8;
            if (sampleTablePos + 8 <= _pdxData.Length)
            {
                uint offset = BinaryPrimitives.ReadUInt32BigEndian(_pdxData.AsSpan(sampleTablePos, 4));
                uint length = BinaryPrimitives.ReadUInt32BigEndian(_pdxData.AsSpan(sampleTablePos + 4, 4));
                if (length > 0 && offset + length <= (uint)_pdxData.Length)
                {
                    int mode = (ch.PanFlCon >> 2) & 7;
                    int pan = ch.PanFlCon & 3;
                    int vol = ch.Volume;
                    int att = (vol & 0x80) != 0 ? vol & 0x7f : VolumeTable[Math.Clamp(vol, 0, 15)];
                    att = Math.Clamp(att + _global.FadeoutOffset, 0, 0x7f);

                    // 左右を PCM8 の出力マスクに戻し、減衰量を PCM8 の音量に直す。減衰が大きすぎれば鳴らさない。
                    pan = pan is 0 or 3 ? pan ^ 3 : pan;
                    int pcmVolume = att < 43 ? PcmVolumeTable[att] : 0;
                    if (att >= 43) pan = 0;
                    PcmOutCallback?.Invoke(pcmChannel, (int)offset, (int)length, mode, (pan << 8) | pcmVolume);
                }
            }
        }
    }

    /// <summary>
    /// L000fe6: キーオフする。
    /// レガート中はキーオフしない。
    /// FM は全スロットをキーオフし、PCM は発音を止める。
    /// </summary>
    private void L000fe6(ChannelWork ch)
    {
        if ((ch.Flags16 & 0x08) == 0) return;
        ch.Flags16 &= 0xf7;

        if ((ch.Flags16 & 0x10) != 0) return;

        if ((sbyte)ch.ChannelNumber >= 0)
        {
            WriteOpm(0x08, (byte)ch.ChannelNumber);
        }
        else
        {
            int pcmChannel = (ch.ChannelNumber & 0x07);
            PcmStopCallback?.Invoke(pcmChannel);
        }
    }

    /// <summary>動いているトラックが残っていなければ曲を終える。</summary>
    private void CheckAllTracksEnded()
    {
        bool anyActive = false;
        for (int i = 0; i < 9; i++)
        {
            if (_channelsFm[i].Ptr != 0) { anyActive = true; break; }
        }
        if (!anyActive && _global.Pcm8Enabled != 0)
        {
            for (int i = 0; i < 7; i++)
            {
                if (_channelsPcm[i].Ptr != 0) { anyActive = true; break; }
            }
        }
        if (!anyActive) _global.Ended = 1;
    }

    /// <summary>すべての FM をキーオフして TL を消音にし、PCM8 を止める。</summary>
    private void AllChannelsOff()
    {
        for (int i = 0; i < 8; i++)
        {
            WriteOpm(0x08, (byte)i);
            for (int op = 0; op < 4; op++)
                WriteOpm(0x60 + op * 8 + i, 0x7f);
        }
        for (int i = 0; i < 8; i++)
        {
            PcmStopCallback?.Invoke(i);
        }
    }
}
