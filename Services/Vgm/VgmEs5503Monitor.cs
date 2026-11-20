using MirrorFlux.Services.Music;

namespace MirrorFlux.Services.Vgm;

/// <summary>
/// ES5503の発音状態を表示用に追う。ES5503の音源コアには発音状態を問い合わせる手段がないため、
/// オシレーターのレジスタと波形表の終端・ゼロ値の規則を自前でたどり、短い単発音が点きっぱなしにならないようにする。
/// </summary>
internal sealed class VgmEs5503Monitor(uint rawClock, int instance, byte outputChannels)
{
    private readonly string _name = instance == 0 ? "ES5503" : $"ES5503 #{instance + 1}";
    private readonly uint _clock = rawClock & 0x3fffffff;
    private readonly int _outputs = outputChannels == 0 ? 2 : outputChannels;
    private readonly byte[] _registers = new byte[256];
    private readonly byte[] _ram = new byte[0x20000];
    private readonly bool[] _used = new bool[32];
    private readonly bool[] _halted = new bool[32];
    private readonly uint[] _accumulator = new uint[32];
    private double _nativeTicks;
    private int _enabled = 1;

    /// <summary>レジスタ書込みを記録し、使用中のボイスと停止状態を更新する。</summary>
    internal void Write(int address, byte value)
    {
        if ((uint)address >= 256) return;
        // 0xE1は有効なオシレーター数を決める。
        if (address == 0xe1) _enabled = ((value >> 1) & 31) + 1;
        if (address < 0xe0)
        {
            // ボイスごとのレジスタなら使用済みにし、制御レジスタ（0xA0〜）の停止ビットを反映する。停止から再開したら位置を戻す。
            int voice = address & 31;
            if ((address & 0xe0) != 0x60) _used[voice] = true;
            if ((address & 0xe0) == 0xa0)
            {
                if ((_registers[address] & 1) != 0 && (value & 1) == 0) _accumulator[voice] = 0;
                _halted[voice] = (value & 1) != 0;
            }
        }
        _registers[address] = value;
    }

    /// <summary>波形RAMにデータを読み込む。</summary>
    internal void LoadRam(int offset, ReadOnlySpan<byte> data)
    {
        if ((uint)offset >= _ram.Length) return;
        data[..Math.Min(data.Length, _ram.Length - offset)].CopyTo(_ram.AsSpan(offset));
    }

    /// <summary>オシレーターを指定フレーム数ぶん進め、波形の終端やゼロ値で止まるボイスを判定する。</summary>
    internal void Advance(int frames)
    {
        // 音源チップ本来の更新回数を44.1kHzのフレーム数から求める。
        int nativeRate = (int)(_clock / 8 / (uint)(_enabled + 2));
        if (nativeRate == 0) return;
        _nativeTicks += frames * (double)nativeRate / 44100;
        int ticks = (int)_nativeTicks;
        _nativeTicks -= ticks;
        for (int voice = 0; voice < _enabled; voice++)
        {
            // 周波数、波形表の大きさ、分解能から、読み出し位置と波形表の先頭アドレスを求める。
            if (_halted[voice]) continue;
            int freq = _registers[voice] | _registers[0x20 + voice] << 8;
            int sizeIndex = (_registers[0xc0 + voice] >> 3) & 7;
            int shift = 9 + (_registers[0xc0 + voice] & 7) - sizeIndex;
            int size = 256 << sizeIndex;
            int baseAddress = (_registers[0x80 + voice] << 8 | ((_registers[0xc0 + voice] & 0x40) << 10)) & ~(size - 1);
            for (int tick = 0; tick < ticks; tick++)
            {
                uint position = _accumulator[voice] >> shift;
                byte sample = _ram[(baseAddress + ((int)position & (size - 1))) & 0x1ffff];
                _accumulator[voice] += (uint)freq;
                // ゼロ値または波形表の終端に来たら、モードに応じて止めるか先頭へ戻す。モード3では相方のボイスを始動する。
                if (sample == 0 || position >= size - 1)
                {
                    int mode = (_registers[0xa0 + voice] >> 1) & 3;
                    if (sample == 0 || mode is 1 or 3) _halted[voice] = true;
                    else _accumulator[voice] -= (uint)((size - 1) << shift);
                    if (mode == 3)
                    {
                        int partner = voice ^ 1;
                        _halted[partner] = false;
                        _accumulator[partner] = 0;
                    }
                    if (_halted[voice]) break;
                }
            }
        }
    }

    /// <summary>全ボイスの表示用チャンネル情報を作る。</summary>
    internal IEnumerable<MusicChannelSnapshot> Capture(bool playing)
    {
        int nativeRate = (int)(_clock / 8 / (uint)(_enabled + 2));
        for (int voice = 0; voice < 32; voice++)
        {
            // レジスタから周波数、音量、波形表、出力先を読み、出力先から左右を決める。
            int freq = _registers[voice] | _registers[0x20 + voice] << 8;
            int volume = _registers[0x40 + voice];
            int resolution = _registers[0xc0 + voice] & 7;
            int sizeIndex = (_registers[0xc0 + voice] >> 3) & 7;
            int size = 256 << sizeIndex;
            int baseAddress = (_registers[0x80 + voice] << 8 | ((_registers[0xc0 + voice] & 0x40) << 10)) & ~(size - 1);
            int route = (_registers[0xa0 + voice] >> 4) & 15;
            int stereoOutputs = _outputs & ~1;
            int pan = route < stereoOutputs ? (route & 1) == 0 ? 2 : 1 : route < _outputs ? 3 : 0;
            bool active = playing && voice < _enabled && !_halted[voice] && freq > 0 && volume > 0 && pan != 0 &&
                !(((_registers[0xa0 + voice] >> 1) & 3) == 2 && (voice & 1) != 0);
            // 波形表1周を1周期とみなして音高を求める。録音されたサンプルには複数周期が入っていることがあるため推定値として扱う。
            double sampleRate = nativeRate * (double)freq / (1 << (9 + resolution - sizeIndex));
            double pitch = sampleRate / size;
            int? note = pitch > 0 ? Math.Clamp((int)Math.Round(69 + 12 * Math.Log2(pitch / 440.0)), 0, 127) : null;
            // 波形表示用に、波形表から32点を抜き出す。
            var wave = new byte[32];
            for (int i = 0; i < wave.Length; i++) wave[i] = _ram[(baseAddress + i * size / wave.Length) & 0x1ffff];
            yield return new()
            {
                Id = $"{_name}:{voice}", ChipName = _name, Label = $"PCM{voice + 1:00}",
                Kind = MusicChannelKind.Pcm, IsInUse = _used[voice], IsKeyOn = active,
                NoteNumber = note, IsNoteEstimated = true, Frequency = pitch, Level = active ? volume / 255.0 : 0,
                Volume = volume, Pan = pan, Waveform = wave,
                DataString = $"RATE:{sampleRate:F0}Hz F:{freq:X4}"
            };
        }
    }

    internal MusicRegisterBank Registers() => new() { ChipName = _name, Port = 0, Values = (byte[])_registers.Clone() };
}
