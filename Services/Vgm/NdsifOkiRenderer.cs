using System.Buffers.Binary;
using System.IO;

namespace MirrorFlux.Services.Vgm;

/// <summary>
/// ND8へ送るOKIM6258の連続した音声ストリームを作る。必要ならYM2151の書込みも同じ時間軸のイベントとして載せる。
/// PLAY・STOPでは元データ側の予測値をリセットするが、実機出力側の予測値は無音区間やVGMのループ境界をまたいで保つ。
/// </summary>
internal sealed class NdsifOkiRenderer : MirrorFlux.Services.Hardware.INd8AudioRenderer
{
    /// <summary>ストリーム中の位置に付けるイベント。種類0はYM2151の書込み、1はクロック・分周の変更、2はパン、3は終了時の最後の1バイト。</summary>
    internal readonly record struct AudioEvent(uint Position, byte Kind, byte[] Payload);
    /// <summary>一度に送るADPCMデータと、その間のイベント。</summary>
    internal readonly record struct Chunk(uint Position, byte[] Data, AudioEvent[] Events, ulong EndTimeUs, bool Finished);

    private readonly IndependentVgmProgram _program;
    private readonly bool _includeYm;
    private readonly Queue<byte> _raw = new();
    private readonly List<byte> _bank = [];
    private readonly List<(int Start, int Size)> _blocks = [];
    private readonly HashSet<int> _loadedBlockOffsets = [];
    private readonly List<AudioEvent> _events = [];
    private readonly byte[] _ymRegisters = new byte[256];
    private readonly bool[] _ymSeen = new bool[256];
    private readonly byte[] _ymKeyOn = new byte[8];
    private readonly bool[] _ymKeySeen = new bool[8];
    private readonly byte[] _initialYm;
    private readonly int _firstSoundIndex;
    private MirrorFlux.Services.Hardware.OkiAdpcmCodec _input, _output;
    private int _index, _streamId = -1;
    private readonly VgmWaitTimer _waits = new();
    private int _streamBank, _step = 1, _base, _streamFrequency, _streamStart, _streamCount, _streamIndex;
    private bool _playing, _streamActive, _streamLoop, _streamReverse, _looped, _ended, _finished;
    private uint _clock, _clockShadow, _position;
    private ushort _divider;
    private byte _pan;
    private bool _seeked;
    // 時間は1/441マイクロ秒単位で数える。VGMの1サンプルは10000単位。命令の処理は5120マイクロ秒先行させる。
    private ulong _timeUnits, _commandUnits = 5120ul * 441, _endUnits;

    internal uint Clock => _clock;
    internal ushort Divider => _divider;
    public uint InitialClock { get; }
    public ushort InitialDivider { get; }
    internal uint BytePosition => _position;
    public ulong TimeUs => _timeUnits / 441;
    public bool Finished => _finished;

    /// <summary>曲のヘッダーからOKIM6258のクロックと分周を設定する。ND8は4MHzか8MHzの1個だけに対応する。</summary>
    internal NdsifOkiRenderer(VgmFile file, IndependentVgmProgram program, bool includeYm)
    {
        _program = program; _includeYm = includeYm;
        _clock = file.OkiM6258Clock & 0x3fffffff; _clockShadow = _clock;
        _divider = (file.OkiM6258Flags & 3) switch { 0 => (ushort)1024, 1 => (ushort)768, _ => (ushort)512 };
        if (_clock is not (4000000 or 8000000) || (file.OkiM6258Clock & 0xc0000000) != 0)
            throw new NotSupportedException("ND8 OKIM6258 requires a single 4 or 8 MHz chip");
        _input.Reset(); _output.Reset();
        InitialClock = _clock; InitialDivider = _divider;
        (_initialYm, _firstSoundIndex) = FindInitialYm();
    }

    /// <summary>最初に音が出るまでのYM2151の書込みを集め、再生前にまとめて送れるようにする。音が出始める命令の番号も返す。</summary>
    private (byte[] Writes, int FirstSoundIndex) FindInitialYm()
    {
        if (!_includeYm) return ([], 0);
        var writes = new List<byte>(); byte[] data = _program.Data;
        for (int i = 0; i < _program.Commands.Count; i++)
        {
            // 終了、ストリーム開始、OKIM6258の再生開始、スロット付きのキーオンのどれかが来たら、そこで音が出始めるとみなす。
            var command = _program.Commands[i]; int p = command.Offset;
            if (command.Code == 0x66 || command.Code is 0x93 or 0x95 ||
                command.Code == 0xb7 && data[p + 1] == 0 && (data[p + 2] & 2) != 0)
                return (writes.ToArray(), i);
            if (command.Code != 0x54) continue;
            byte address = data[p + 1], value = data[p + 2];
            if (address == 8 && (value & 0x78) != 0) return (writes.ToArray(), i);
            writes.Add(address); writes.Add(value);
        }
        return (writes.ToArray(), _program.Commands.Count);
    }

    /// <summary>指定サンプル位置まで空回しして、そこからストリームを始められるようにする。</summary>
    internal void SeekToSample(long sample)
    {
        if (sample <= 0) return;
        _seeked = true;
        ulong target = (ulong)sample * 10000 + 5120ul * 441;
        while (_timeUnits < target && !_finished) Next();
        // 進めた分の時間を差し引き、位置と出力側の予測値をリセットする。
        ulong skipped = _timeUnits;
        _commandUnits = _commandUnits > skipped ? _commandUnits - skipped : 0;
        if (_ended) _endUnits = _endUnits > skipped ? _endUnits - skipped : 0;
        _timeUnits = 0; _position = 0; _output.Reset(); _events.Clear();
    }

    /// <summary>再生前にまとめて送るYM2151の書込みを返す。途中から始めた場合は、そこまでに書かれたレジスタとキーオンの状態を返す。</summary>
    public byte[] InitialYmWrites()
    {
        if (!_includeYm) return [];
        if (!_seeked) return _initialYm;
        var pairs = new List<byte>();
        for (int address = 0; address < 256; address++)
            if (address != 8 && _ymSeen[address]) { pairs.Add((byte)address); pairs.Add(_ymRegisters[address]); }
        for (int channel = 0; channel < 8; channel++)
            if (_ymKeySeen[channel]) { pairs.Add(8); pairs.Add(_ymKeyOn[channel]); }
        return pairs.ToArray();
    }

    /// <summary>次のADPCMデータを最大250バイトまたは5120マイクロ秒ぶん作る。</summary>
    public Chunk Next()
    {
        if (_finished) return new Chunk(_position, [], [], TimeUs, true);
        uint startPosition = _position;
        ulong until = _timeUnits + 5120ul * 441;
        var data = new List<byte>(64);
        _events.Clear();
        // 時刻が来た命令を処理し、元データを復号して実機出力用に符号化し直す。データがなく再生中なら直前の値を保つ。
        while (data.Count < 250 && _timeUnits < until && !_finished)
        {
            ParseDue();
            int first = 0, second = 0;
            if (SourceByte(out byte source))
            {
                first = _input.Decode(source & 15); second = _input.Decode(source >> 4);
            }
            else if (_playing) first = second = _input.Signal;
            byte lo = _output.Encode(first), hi = _output.Encode(second);
            data.Add((byte)(lo | hi << 4));
            if (_position == uint.MaxValue) throw new InvalidDataException("ND8 ADPCM position overflow");
            _position++;
            _timeUnits += (ulong)_divider * 2_000_000 / _clock * 441;
            if (_ended && _timeUnits >= _endUnits) _finished = true;
        }
        // 最後に無音へ戻す1バイトを付け、予測値が収束することを確かめる。
        if (_finished)
        {
            var next = _output;
            byte lo = next.Encode(0), hi = next.Encode(0);
            if (next.Index != 0 || next.Signal != _output.Signal)
                throw new InvalidDataException("ND8 ADPCM tail did not converge");
            _events.Add(new AudioEvent(_position, 3, [(byte)(lo | hi << 4)]));
        }
        return new Chunk(startPosition, data.ToArray(), _events.ToArray(), TimeUs, _finished);
    }

    /// <summary>時刻が来たVGM命令を処理する。待ち時間のない無限ループは拒否する。</summary>
    private void ParseDue()
    {
        int operations = 0;
        byte[] bytes = _program.Data;
        while (!_ended && _commandUnits <= _timeUnits)
        {
            if (++operations > 100000 || _index >= _program.Commands.Count)
                throw new InvalidDataException("ND8 ADPCM VGM has no advancing wait or end");
            var command = _program.Commands[_index++]; int p = command.Offset;
            switch (command.Code)
            {
                // 0x61〜0x64・0x70〜0x8F: 待ち時間。
                case >= 0x61 and <= 0x64 or >= 0x70 and <= 0x8f:
                    _waits.TryWait(bytes.AsSpan(p, command.Length), out int waited);
                    _commandUnits += (ulong)waited * 10000; break;
                // 0x54: YM2151の書込み。状態を記録し、音が出始めた後の書込みはイベントにする。
                case 0x54:
                    if (_includeYm)
                    {
                        byte register = bytes[p + 1], value = bytes[p + 2];
                        if (register == 8) { _ymKeyOn[value & 7] = value; _ymKeySeen[value & 7] = true; }
                        else { _ymRegisters[register] = value; _ymSeen[register] = true; }
                        if (_looped || _index - 1 >= _firstSoundIndex)
                            _events.Add(new AudioEvent(_position, 0, [register, value]));
                    }
                    break;
                // 0x67: データブロック。0xB7: OKIM6258の書込み。
                case 0x67: ReadBlock(command); break;
                case 0xb7: ReadOki(bytes[p + 1], bytes[p + 2]); break;
                // 0x90: OKIM6258のデータストリームの設定。ND8はポート0・レジスタ1宛ての1本だけに対応する。
                case 0x90:
                    if ((bytes[p + 2] & 0x7f) == 23)
                    {
                        if (bytes[p + 2] != 23 || bytes[p + 3] != 0 || bytes[p + 4] != 1 ||
                            _streamId >= 0 && _streamId != bytes[p + 1])
                            throw new NotSupportedException("ND8 supports one OKIM6258 data stream");
                        _streamId = bytes[p + 1];
                    }
                    break;
                // 0x91: データバンクと間隔。0x92: 周波数。
                case 0x91: if (bytes[p + 1] == _streamId) { _streamBank = bytes[p + 2]; _step = bytes[p + 3]; _base = bytes[p + 4]; } break;
                case 0x92:
                    if (bytes[p + 1] == _streamId)
                    {
                        uint frequency = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(p + 2));
                        if (frequency > int.MaxValue) throw new NotSupportedException("ND8 OKI stream frequency is too large");
                        _streamFrequency = (int)frequency;
                    }
                    break;
                // 0x93: 指定位置から再生する。長さは命令数、ミリ秒、バンク末尾までのいずれかで指定する。
                case 0x93:
                    if (bytes[p + 1] == _streamId)
                    {
                        uint offset = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(p + 2));
                        byte mode = bytes[p + 6]; uint count = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(p + 7));
                        if (offset == uint.MaxValue) offset = (uint)_streamStart;
                        count = (mode & 15) switch
                        {
                            1 => count,
                            2 => (uint)Math.Min(uint.MaxValue, (ulong)count * (uint)_streamFrequency / 1000),
                            3 => (uint)Math.Max(0, _bank.Count - (long)offset),
                            _ => throw new NotSupportedException("ND8 OKI stream length mode")
                        };
                        StartStream(offset, count, (mode & 0x80) != 0, (mode & 0x10) != 0);
                    }
                    break;
                // 0x94: 停止。0x95: データブロック単位の再生。フラグはループと逆再生だけを受け付ける。
                case 0x94: if (bytes[p + 1] == 255 || bytes[p + 1] == _streamId) StopSource(); break;
                case 0x95:
                    if (bytes[p + 1] == _streamId)
                    {
                        int block = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(p + 2)); byte flags = bytes[p + 4];
                        if (block >= _blocks.Count || (flags & 0xee) != 0)
                            throw new InvalidDataException("ND8 OKI stream block or flags invalid");
                        var location = _blocks[block];
                        StartStream((uint)location.Start, (uint)location.Size, (flags & 1) != 0, (flags & 0x10) != 0);
                    }
                    break;
                // 0x66: 終了。ループがあれば1回だけループ位置へ戻り、なければ50ミリ秒の余白を置いて終える。
                case 0x66:
                    if (_program.LoopIndex >= 0 && !_looped) { _index = _program.LoopIndex; _looped = true; }
                    else { _ended = true; StopSource(); _endUnits = _timeUnits + 50000ul * 441; }
                    break;
            }
        }
    }

    /// <summary>OKIM6258用のデータブロック（種類0x04）をバンクに読み込む。圧縮形式（0x44）には対応しない。</summary>
    private void ReadBlock(IndependentVgmProgram.Command command)
    {
        byte[] bytes = _program.Data; int p = command.Offset;
        byte kind = bytes[p + 2];
        if (kind == 0x44) throw new NotSupportedException("Compressed OKI banks are not supported on ND8");
        if (kind != 4 || !_loadedBlockOffsets.Add(p)) return;
        int size = command.Length - 7;
        if (_bank.Count > 64 * 1024 * 1024 - size) throw new InvalidDataException("ND8 OKI bank too large");
        _blocks.Add((_bank.Count, size));
        _bank.AddRange(bytes.AsSpan(p + 7, size).ToArray());
    }

    /// <summary>OKIM6258のレジスタ書込みを処理する。0は再生・停止、1はデータ、2はパン、8〜11はクロック、12は分周。</summary>
    private void ReadOki(byte register, byte value)
    {
        if ((register & 0x80) != 0) throw new NotSupportedException("Dual OKIM6258 is unavailable on ND8");
        switch (register)
        {
            case 0:
                if ((value & 1) != 0) StopSource();
                else if ((value & 2) != 0) StartSource();
                if ((value & 4) != 0) throw new NotSupportedException("OKIM6258 recording is unavailable");
                break;
            case 1: if (_playing && !_streamActive) _raw.Enqueue(value); break;
            case 2:
                byte pan = (byte)(value & 3);
                if (pan != _pan) { _pan = pan; _events.Add(new AudioEvent(_position, 2, [pan])); }
                break;
            case >= 8 and <= 11:
                int shift = (register - 8) * 8;
                _clockShadow = (_clockShadow & ~(0xffu << shift)) | ((uint)value << shift);
                if (_clockShadow is 4000000 or 8000000 && _clockShadow != _clock)
                { _clock = _clockShadow; AddRate(); }
                break;
            case 12:
                ushort divider = (value & 3) switch { 0 => (ushort)1024, 1 => (ushort)768, _ => (ushort)512 };
                if (divider != _divider) { _divider = divider; AddRate(); }
                break;
        }
    }

    /// <summary>クロックか分周が変わったことをイベントとして記録する。</summary>
    private void AddRate()
    {
        byte[] payload = new byte[6];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, _clock);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(4), _divider);
        _events.Add(new AudioEvent(_position, 1, payload));
    }

    /// <summary>元データ側の再生を始める。止まっていた場合は予測値をリセットする。</summary>
    private void StartSource()
    {
        if (!_playing) { _input.Reset(); _raw.Clear(); }
        _playing = true;
    }
    /// <summary>元データ側の再生を止め、予測値とデータの待ち行列をリセットする。</summary>
    private void StopSource()
    {
        _playing = false; _streamActive = false; _input.Reset(); _raw.Clear();
    }

    /// <summary>データストリームを始める。バンク4、間隔1、オフセット0で、周波数がクロック÷分周÷2と一致する場合だけ受け付ける。</summary>
    private void StartStream(uint offset, uint count, bool looping, bool reverse)
    {
        if (_streamBank != 4 || _step != 1 || _base != 0 || count == 0 ||
            (ulong)offset + count > (ulong)_bank.Count ||
            Math.Abs(_streamFrequency - _clock / (double)_divider / 2) > 1)
            throw new NotSupportedException("ND8 OKI stream bank, rate or bounds are unsupported");
        StartSource(); _streamStart = (int)offset; _streamCount = (int)count; _streamIndex = 0;
        _streamLoop = looping; _streamReverse = reverse; _streamActive = true; _raw.Clear();
    }

    /// <summary>次の元データ1バイトを、データストリームか直接書込みの待ち行列から取り出す。</summary>
    private bool SourceByte(out byte value)
    {
        value = 0;
        if (!_playing) return false;
        if (_streamActive)
        {
            if (_streamIndex >= _streamCount)
            {
                if (_streamLoop) _streamIndex = 0;
                else _streamActive = false;
            }
            if (_streamActive)
            {
                int i = _streamReverse ? _streamCount - 1 - _streamIndex : _streamIndex;
                value = _bank[_streamStart + i]; _streamIndex++; return true;
            }
        }
        if (_raw.Count == 0) return false;
        value = _raw.Dequeue(); return true;
    }
}
