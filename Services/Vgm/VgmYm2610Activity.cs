namespace MirrorFlux.Services.Vgm;

/// <summary>YM2610（OPNB）のADPCMのアドレスと再生速度を鍵盤表示用に追う。実際に聞こえる音の展開は再生処理側で行う。</summary>
internal sealed class VgmYm2610Activity(uint clock, int outputRate)
{
    private readonly byte[] _registers = new byte[512];
    private readonly bool[] _used = new bool[7], _active = new bool[7];
    private readonly double[] _remaining = new double[7];
    private int _romA, _romB;
    /// <summary>レジスタ書込みを記録し、ADPCM-A（6チャンネル）とADPCM-Bのキーオン・停止を反映する。</summary>
    public void Write(int port, int address, int data)
    {
        _registers[port * 256 + address] = (byte)data;
        // ADPCM-Aのキーオン・キーオフ。ROMが読み込まれているときだけ発音中にする。
        if (port == 1 && address == 0)
            for (int i = 0; i < 6; i++) if ((data & (1 << i)) != 0)
            {
                _used[i] = true;
                _remaining[i] = SampleNibbles(i);
                _active[i] = (data & 0x80) == 0 && _remaining[i] > 0 && _romA > 0;
            }
        // ADPCM-Bの開始とリセット。
        if (port == 0 && address == 0x10)
        {
            _used[6] = true;
            if ((data & 1) != 0) _active[6] = false;
            else if ((data & 0x80) != 0)
            {
                _remaining[6] = SampleNibbles(6);
                _active[6] = _remaining[6] > 0 && _romB > 0;
            }
        }
    }
    /// <summary>開始・終了アドレスから求めた、サンプルの長さ（4ビット単位の数）。</summary>
    private int SampleNibbles(int i)
    {
        int start = i == 6 ? _registers[0x12] | (_registers[0x13] << 8) : _registers[0x110+i] | (_registers[0x118+i] << 8);
        int end = i == 6 ? _registers[0x14] | (_registers[0x15] << 8) : _registers[0x120+i] | (_registers[0x128+i] << 8);
        return Math.Max(0, end + 1 - start) * 512;
    }
    /// <summary>再生位置を進める。ADPCM-Aは固定レート、ADPCM-Bは指定した速度で進み、ADPCM-Bはリピート指定があれば先頭へ戻る。</summary>
    public void Advance(int frames)
    {
        for (int i = 0; i < 7; i++)
        {
            if (!_active[i]) continue;
            double rate = i == 6 ? clock / 144.0 * ((_registers[0x19] | (_registers[0x1a] << 8)) / 65536.0) : clock / 432.0;
            _remaining[i] -= frames * rate / outputRate;
            if (_remaining[i] > 0) continue;
            if (i == 6 && (_registers[0x10] & 0x10) != 0 && SampleNibbles(i) > 0)
                _remaining[i] = SampleNibbles(i) + _remaining[i] % SampleNibbles(i);
            else _active[i] = false;
        }
    }
    /// <summary>ADPCMの表示用チャンネル情報を作る。</summary>
    public IEnumerable<Music.MusicChannelSnapshot> CaptureAdpcm(bool playing)
    {
        for (int i = 0; i < 7; i++)
        {
            // 出力先と音量を求める。ADPCM-Aは全体と個別の減衰量から、ADPCM-Bは音量レジスタから求める。
            bool isB = i == 6;
            int pan = (_registers[isB ? 0x11 : 0x108 + i] >> 6) & 3;
            bool active = playing && pan != 0 && _active[i];
            int attenuation = (63 - (_registers[0x101] & 63)) + (31 - (_registers[0x108 + Math.Min(i, 5)] & 31));
            double level = isB ? _registers[0x1b] / 255.0 : Math.Pow(10, -attenuation * .75 / 20);
            int delta = _registers[0x19] | (_registers[0x1a] << 8);
            // ADPCM-Aはサンプルレートが固定なので、Cの表示は発音を示すランプで音高の推定ではない。
            // ADPCM-Bは同じ基準音に対する相対的な再生速度から音高を求める。
            int? note = !isB ? 60 : delta > 0 ? Math.Clamp((int)Math.Round(60 + 12 * Math.Log2(delta / 65536.0)), 0, 127) : null;
            yield return new()
            {
                Id = $"YM2610:ADPCM:{i}", ChipName = "YM2610", Label = isB ? "ADPCM-B" : $"ADPCM-A{i + 1}",
                Kind = Music.MusicChannelKind.Adpcm, IsInUse = _used[i], IsKeyOn = active && level > 0,
                NoteNumber = note, Pitch = isB && delta > 0 ? Music.MusicPitch.FromRatio(delta / 65536.0) : null,
                Level = active ? level : 0, Pan = pan
            };
        }
    }
    // ADPCM-A・ADPCM-BのROMの大きさを記録する。0なら発音中にしない。
    public void SetAdpcmA(byte[] rom) => _romA = rom.Length;
    public void SetAdpcmB(byte[] rom) => _romB = rom.Length;
}
