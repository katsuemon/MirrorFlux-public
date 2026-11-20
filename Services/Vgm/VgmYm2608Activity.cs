using System.Buffers.Binary;
using System.IO;
using MirrorFlux.Services.Music;

namespace MirrorFlux.Services.Vgm;

/// <summary>YM2608のDelta-T（ADPCM-B）のレジスタと再生位置を、表示用に追う。FM合成やROMの複製はしない。</summary>
internal sealed class VgmYm2608Activity(uint clock, int outputRate)
{
    private readonly byte[] _registers = new byte[16];
    private bool _used, _active;
    private double _remaining;
    private int Delta => _registers[9] | (_registers[10] << 8);
    /// <summary>Delta-Tのレジスタ書込みを記録し、開始・リセットで再生状態を切り替える。</summary>
    public void Write(int port, byte register, byte value)
    {
        if (port != 1 || register >= _registers.Length) return;
        _registers[register] = value;
        if (register <= 0x0d) _used = true;
        if (register != 0) return;
        if ((value & 1) != 0) _active = false;
        else if ((value & 0x80) != 0) { _remaining = SampleNibbles; _active = _remaining > 0; }
    }
    /// <summary>開始・終了アドレスから求めた、サンプルの長さ（4ビット単位の数）。</summary>
    private int SampleNibbles
    {
        get
        {
            int start = _registers[2] | (_registers[3] << 8);
            int end = _registers[4] | (_registers[5] << 8);
            // OPNAの8ビットRAM・ROMは32バイト単位、1ビットRAMは4バイト単位のアドレスを使う。
            int shift = (_registers[1] & 3) != 0 ? 5 : 2;
            return Math.Max(0, end + 1 - start) * (1 << shift) * 2;
        }
    }
    /// <summary>再生位置を進め、終わりに来たらリピート指定に応じて先頭へ戻すか止める。</summary>
    public void Advance(int frames)
    {
        if (!_active || Delta == 0) return;
        _remaining -= frames * (clock / 144.0) * (Delta / 65536.0) / outputRate;
        if (_remaining > 0) return;
        if ((_registers[0] & 0x10) != 0 && SampleNibbles > 0)
            _remaining = SampleNibbles + _remaining % SampleNibbles;
        else _active = false;
    }
    /// <summary>Delta-T用のメモリブロックのヘッダーを検証する。</summary>
    public void LoadMemory(ReadOnlySpan<byte> block)
    {
        if (block.Length < 8) throw new InvalidDataException("Truncated YM2608 Delta-T memory block.");
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(block);
        uint offset = BinaryPrimitives.ReadUInt32LittleEndian(block[4..]);
        if (size == 0 || offset > size || block.Length - 8 > size - offset)
            throw new InvalidDataException("Invalid YM2608 Delta-T memory range.");
    }
    /// <summary>表示用のチャンネル情報を作る。再生速度から音高を求める。</summary>
    public MusicChannelSnapshot Capture(bool playing, string name = "YM2608")
    {
        int pan = _registers[1] >> 6, level = _registers[11];
        bool active = playing && _active && Delta > 0 && pan != 0 && level > 0;
        int? note = Delta > 0 ? Math.Clamp((int)Math.Round(60 + 12 * Math.Log2(Delta / 65536.0)), 0, 127) : null;
        return new() { Id = $"{name}:ADPCM-B", ChipName = name, Label = "ADPCM-B",
            Kind = MusicChannelKind.Adpcm, IsInUse = _used, IsKeyOn = active,
            NoteNumber = note, Pitch = Delta > 0 ? MusicPitch.FromRatio(Delta / 65536.0) : null, Level = active ? level / 255.0 : 0,
            Volume = level, Pan = pan, DataString = $"DELTA:{Delta:X4}" };
    }
}
