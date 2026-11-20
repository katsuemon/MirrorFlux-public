using MirrorFlux.Services.Hardware;
using MirrorFlux.Services.Vgm;

namespace MirrorFlux.Services.Music;

/// <summary>
/// MDX を NanoDrive 8 で鳴らすためのストリームを作る。
/// 演奏は再生用とは別の <see cref="MxdrvCore"/> で進め、YM2151 への書き込みはその時刻のイベントとして、PCM8 の最大8音は1本に混ぜて OKIM6258 の ADPCM に作り直して送る。
/// OKIM6258 は X68000 と同じ 8 MHz・分周 512（15.625 kHz）。
/// 左右の振り分けは1本に混ぜるため失われる。
/// </summary>
internal sealed class MdxNd8Renderer : INd8AudioRenderer
{
    // OKIM6258 のクロックと分周。サンプルレートは 15625 Hz、1バイト（2サンプル）は128マイクロ秒。TailUs は曲の終わりのあと、PCM の余韻を鳴らす長さ。
    internal const uint OkiClock = 8_000_000;
    internal const ushort OkiDivider = 512;
    private const double SampleRate = OkiClock / (double)OkiDivider;
    private const ulong ByteUs = 128;
    private const ulong ChunkUs = 5120;
    private const int ChunkBytes = 250;
    private const ulong TailUs = 8_000_000;

    private readonly MxdrvCore _core;
    private readonly byte[]? _pdx;
    private readonly PdxPcmMixer _mixer = new(SampleRate);
    private readonly int[][] _pcm = [new int[2], new int[2]];
    private readonly List<NdsifOkiRenderer.AudioEvent> _events = [];
    private readonly byte[] _ymRegisters = new byte[256];
    private readonly bool[] _ymSeen = new bool[256];
    private readonly byte[] _ymKeyOn = new byte[8];
    private readonly bool[] _ymKeySeen = new bool[8];
    private readonly List<byte> _initialYm = [];
    private OkiAdpcmCodec _output;
    private bool _collectingInitial = true, _seeked, _finished;
    private ulong _timeUs, _nextTickUs, _endUs = ulong.MaxValue;
    private uint _position;

    /// <summary>調査用。1バイトごとに、符号化する2つのサンプルの目標値を知らせる。</summary>
    internal Action<int, int>? EncodeTrace { get; set; }

    public uint InitialClock => OkiClock;
    public ushort InitialDivider => OkiDivider;
    public ulong TimeUs => _timeUs;
    public bool Finished => _finished;

    /// <summary>演奏用の MXDRV を用意し、最初のキーオンまでの準備の書き込みを集める。</summary>
    internal MdxNd8Renderer(byte[] mdx, byte[]? pdx)
    {
        _pdx = pdx;
        _core = new MxdrvCore(mdx, pdx)
        {
            WriteOpmCallback = OnWriteOpm,
            PcmOutCallback = OnPcmOut,
            PcmStopCallback = _mixer.Stop
        };
        _output.Reset();
        // 最初のキーオンより前の書き込み（音色などの準備）は AUDIO_START の前に送る。
        _core.Tick();
        _collectingInitial = false;
        _nextTickUs = TickUs;
    }

    /// <summary>
    /// MXDRV の1ティックの長さ（マイクロ秒）。
    /// テンポ値から求める。
    /// </summary>
    private ulong TickUs => (ulong)Math.Max(1, 256 * (256 - _core.Tempo));

    /// <summary>
    /// YM2151 への書き込みを受ける。
    /// 途中から始めるときのために値を控え、準備中なら初期設定に、それ以外はその位置のイベントにする。
    /// </summary>
    private void OnWriteOpm(int register, byte value)
    // キーオン（0x08）はチャンネルごと、それ以外はレジスタごとに最後の値を控える。
    {
        if (register == 8) { _ymKeyOn[value & 7] = value; _ymKeySeen[value & 7] = true; }
        else { _ymRegisters[register & 0xff] = value; _ymSeen[register & 0xff] = true; }
        // 最初のキーオンまでは初期設定として集める。
        if (_collectingInitial && !(register == 8 && (value & 0x78) != 0))
        {
            _initialYm.Add((byte)register); _initialYm.Add(value);
            return;
        }
        _collectingInitial = false;
        _events.Add(new NdsifOkiRenderer.AudioEvent(_position, 0, [(byte)register, value]));
    }

    /// <summary>
    /// PCM8 の発音を受け、ミキサーで鳴らし始める。
    /// panVolume の上位バイトが左右、下位バイトが音量。
    /// </summary>
    private void OnPcmOut(int channel, int offset, int length, int mode, int panVolume)
    {
        if (_pdx == null || channel is < 0 or >= 8) return;
        _mixer.Start(channel, _pdx, offset, length, mode, (panVolume >> 8) & 3, panVolume & 0xff);
    }

    /// <summary>
    /// AUDIO_START より前に書く YM2151 の初期設定を返す。
    /// 途中から始めるときは、控えておいた全レジスタとキーオンの状態を返す。
    /// </summary>
    public byte[] InitialYmWrites()
    {
        if (!_seeked) return _initialYm.ToArray();
        var pairs = new List<byte>();
        for (int address = 0; address < 256; address++)
            if (address != 8 && _ymSeen[address]) { pairs.Add((byte)address); pairs.Add(_ymRegisters[address]); }
        for (int channel = 0; channel < 8; channel++)
            if (_ymKeySeen[channel]) { pairs.Add(8); pairs.Add(_ymKeyOn[channel]); }
        return pairs.ToArray();
    }

    /// <summary>44.1 kHz の再生位置 sample から始める（途中から実機出力を始めるとき）。</summary>
    internal void SeekToSample(long sample)
    {
        if (sample <= 0) return;
        // その位置まで演奏を空回しし、時刻と位置を0に戻す。
        _seeked = true;
        ulong target = (ulong)(sample * 1_000_000 / 44_100);
        while (_timeUs < target && !_finished) Next();
        ulong skipped = _timeUs;
        _nextTickUs = _nextTickUs > skipped ? _nextTickUs - skipped : 0;
        if (_endUs != ulong.MaxValue) _endUs = _endUs > skipped ? _endUs - skipped : 0;
        _timeUs = 0; _position = 0; _output.Reset(); _events.Clear(); _passChannel = -1;
    }

    /// <summary>
    /// 次の一塊（最大250バイト・約5ms）を作る。
    /// MXDRV のティックを進めながら PCM8 をミックスし、1バイトに2サンプルずつ ADPCM にする。
    /// </summary>
    public NdsifOkiRenderer.Chunk Next()
    {
        if (_finished) return new NdsifOkiRenderer.Chunk(_position, [], [], _timeUs, true);
        uint start = _position;
        ulong until = _timeUs + ChunkUs;
        var data = new List<byte>(48);
        // 前回の Next の後（または最初の準備の Tick）で出た書き込みも、この塊の先頭のイベントとして送る。
        while (data.Count < ChunkBytes && _timeUs < until && !_finished)
        {
            // 時刻が来た分だけ MXDRV のティックを進める。曲が終わったら余韻の長さを決める。
            while (_nextTickUs <= _timeUs && !_core.Ended)
            {
                _core.Tick();
                _nextTickUs += TickUs;
            }
            if (_core.Ended && _endUs == ulong.MaxValue) _endUs = _timeUs + TailUs;

            // PCM8 を2サンプル分ミックスし、ADPCM の1バイトにする。
            // 1音だけが鳴っていて、その音の最初から元の ADPCM をそのまま送れるときは、作り直さずに送る（作り直しの雑音が出ない）。
            bool pass = UpdatePassThrough(out int channel, out int position);
            _mixer.Mix(_pcm, 2);
            int first = Mono(0), second = Mono(1);
            byte lo, hi;
            if (pass)
            {
                lo = (byte)_mixer.AdpcmNibble(channel, position); hi = (byte)_mixer.AdpcmNibble(channel, position + 1);
                _output.Decode(lo); _output.Decode(hi);
                PassedSamples += 2;
            }
            else { lo = _output.Encode(first); hi = _output.Encode(second); }
            EncodeTrace?.Invoke(first, second);
            data.Add((byte)(lo | hi << 4));
            _position++;
            _timeUs += ByteUs;

            // 余韻が終わって予測器が落ち着いたら、終端イベントを付けて終える。
            if (_timeUs >= _endUs && Converged(out byte tail))
            {
                _finished = true;
                _events.Add(new NdsifOkiRenderer.AudioEvent(_position, 3, [tail]));
            }
        }
        var chunk = new NdsifOkiRenderer.Chunk(start, data.ToArray(), _events.ToArray(), _timeUs, _finished);
        _events.Clear();
        return chunk;
    }

    // 元の ADPCM をそのまま送っている音（チャンネルと、次に送るサンプルの番号）。送っていなければ -1。
    private int _passChannel = -1, _passNext;

    /// <summary>調査用。元の ADPCM をそのまま送ったサンプル数。</summary>
    internal long PassedSamples { get; private set; }

    /// <summary>
    /// この2サンプルを、鳴っている1音の元の ADPCM のまま送れるか決める。
    /// 始められるのは、その音の最初のサンプルで、こちらの予測器が無音に近い状態（ステップ最小・値がほぼ0）のときだけ。
    /// 実機の OKIM6258 も同じ状態から元のデータを復号するので、X68000 で単音を鳴らしたときと同じ値になる。
    /// 別の音が重なったり音が変わったりしたら、作り直しに戻り、その音の間は戻らない（予測器の状態が元のデータとずれるため）。
    /// </summary>
    private bool UpdatePassThrough(out int channel, out int position)
    {
        if (!_mixer.TryGetSoloAdpcm(out channel, out position, out int count) || position + 1 >= count)
        { _passChannel = -1; return false; }
        if (position == 0 && _output.Index == 0 && Math.Abs(_output.Signal) <= 4) _passChannel = channel;
        else if (channel != _passChannel || position != _passNext) _passChannel = -1;
        if (_passChannel < 0) return false;
        // 元のデータでも12ビットの範囲を越えるところは、作り直しに戻す（実機が端で止めるか折り返すかに左右されないように）。
        var probe = _output;
        int lo = _mixer.AdpcmNibble(channel, position), hi = _mixer.AdpcmNibble(channel, position + 1);
        if (probe.Unclamped(lo) is < -2048 or > 2047) { _passChannel = -1; return false; }
        probe.Decode(lo);
        if (probe.Unclamped(hi) is < -2048 or > 2047) { _passChannel = -1; return false; }
        _passNext = position + 2;
        return true;
    }

    // PCM8 の左右を1本にまとめ、OKIM6258 の12ビットの範囲に合わせる。
    private int Mono(int index) => Math.Clamp((_pcm[0][index] + _pcm[1][index]) / 2 >> 4, -2048, 2047);

    /// <summary>無音を送り続けても予測器が動かない状態になったら終端にする（VGM の ND8 出力と同じ条件）。</summary>
    private bool Converged(out byte tail)
    {
        var next = _output;
        byte lo = next.Encode(0), hi = next.Encode(0);
        tail = (byte)(lo | hi << 4);
        return next.Index == 0 && next.Signal == _output.Signal;
    }
}
