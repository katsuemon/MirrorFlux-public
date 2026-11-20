using System.IO;
using System.Buffers.Binary;
using MirrorFlux.Services.Music;

namespace MirrorFlux.Services.Vgm;

/// <summary>YM2608（OPNA）の6種類のリズム音のサンプルを用意し、固定小数点の再生位置を表示用に追う。</summary>
internal sealed class VgmYm2608Rhythm
{
    private static readonly string[] Names = ["BD", "SD", "TOP", "HH", "TOM", "RIM"];
    private readonly long[] _end = new long[6], _position = new long[6];
    private readonly int[] _step = new int[6];
    private readonly bool[] _used = new bool[6], _key = new bool[6];
    private readonly byte[] _volumePan = new byte[6];
    private readonly byte[]? _rom = Ym2608RhythmRom.Load();
    private int _total;

    /// <summary>リズム音ROM内の各音の長さと、出力レートに合わせた再生速度を求める。BD〜HHは18.5kHz、TOM・RIMは9.25kHzで再生する。</summary>
    public VgmYm2608Rhythm(int outputRate)
    {
        int[] boundaries = [0, 0x1c0, 0x440, 0x1b80, 0x1d00, 0x1f80, 0x2000];
        for (int i = 0; i < 6; i++)
        {
            _end[i] = _rom == null ? 0 : (boundaries[i + 1] - boundaries[i]) * 2L * 1024;
            _position[i] = _end[i];
            _step[i] = (i < 4 ? 18500 : 9250) * 1024 / outputRate;
        }
    }

    /// <summary>リズム音のキーオン・キーオフ、全体音量、個別の音量と出力先の書込みを記録する。</summary>
    public void Write(byte register, byte value)
    {
        if (register == 0x10)
            for (int i = 0; i < 6; i++)
                if ((value & (1 << i)) != 0)
                {
                    _used[i] = true; _key[i] = (value & 0x80) == 0;
                    if (_key[i]) _position[i] = 0;
                }
        if (register == 0x11) _total = value & 63;
        if (register is >= 0x18 and <= 0x1d) { _volumePan[register - 0x18] = value; _used[register - 0x18] = true; }
    }

    /// <summary>キーオン中の音の再生位置を進める。</summary>
    public void Advance(int frames)
    {
        for (int i = 0; i < 6; i++)
            if (_key[i]) _position[i] = Math.Min(_end[i], _position[i] + (long)_step[i] * frames);
    }

    /// <summary>6種類のリズム音の表示用チャンネル情報を作る。音量は全体と個別の減衰量を合わせて求める。</summary>
    public IEnumerable<MusicChannelSnapshot> Capture(bool playing, string name = "YM2608")
    {
        for (int i = 0; i < 6; i++)
        {
            int pan = _volumePan[i] >> 6;
            bool active = playing && _key[i] && _position[i] < _end[i] && pan != 0;
            double level = Math.Pow(10, -((63 - _total) + (31 - (_volumePan[i] & 31))) * .75 / 20);
            yield return new() { Id = $"{name}:RHYTHM:{i}", ChipName = name, Label = Names[i],
                Kind = MusicChannelKind.Rhythm, IsInUse = _used[i], IsKeyOn = active, NoteNumber = 60,
                Level = active ? level : 0, Pan = pan };
        }
    }
}
