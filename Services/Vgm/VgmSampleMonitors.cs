using System.IO;

namespace MirrorFlux.Services.Vgm;

/// <summary>YM2608（OPNA）のADPCMとリズム音の発音状態を、レジスタ監視の表示用に追う。聞こえる音の合成は単体の音源チップコアが受け持つ。</summary>
internal sealed class VgmOpnaActivityMonitor
{
    private readonly List<(string Name, VgmYm2608Activity Core, VgmYm2608Rhythm Rhythm)> _opna = [];
    /// <summary>YM2608の個数（ビット30があれば2個）だけ、ADPCMとリズム音の監視を用意する。</summary>
    public VgmOpnaActivityMonitor(VgmFile file, int rate)
    {
        if ((file.Ym2608Clock & 0x3fffffff) == 0) return;
        int count = (file.Ym2608Clock & 0x40000000) != 0 ? 2 : 1;
        for (int i = 0; i < count; i++)
            _opna.Add((i == 0 ? "YM2608" : "YM2608 #2",
                new VgmYm2608Activity(file.Ym2608Clock & 0x3fffffff, rate), new VgmYm2608Rhythm(rate)));
    }
    /// <summary>YM2608のFMチャンネルの直後に、ADPCMとリズム音のチャンネルを差し込む。</summary>
    public void InsertOpnaSamples(List<Music.MusicChannelSnapshot> channels, bool playing)
    {
        foreach (var chip in _opna)
        {
            int first = channels.FindLastIndex(channel => channel.ChipName == chip.Name) + 1;
            channels.Insert(first, chip.Core.Capture(playing, chip.Name));
            channels.InsertRange(first + 1, chip.Rhythm.Capture(playing, chip.Name));
        }
    }
    /// <summary>Delta-T用のメモリブロックを検証する。</summary>
    public void LoadOpnaMemory(int instance, ReadOnlySpan<byte> block)
    {
        if ((uint)instance < _opna.Count) _opna[instance].Core.LoadMemory(block);
    }
    /// <summary>YM2608への書込み（0x56・0x57・0xA6・0xA7）を監視に送る。リズム音はポート0だけ。</summary>
    public void Write(byte command, int port, byte register, byte value)
    {
        if (command is not (0x56 or 0x57 or 0xa6 or 0xa7)) return;
        int instance = command >= 0xa6 ? 1 : 0;
        if (instance >= _opna.Count) return;
        var chip = _opna[instance];
        chip.Core.Write(port, register, value);
        if (port == 0) chip.Rhythm.Write(register, value);
    }
    /// <summary>再生位置を進める。</summary>
    public void Advance(int frames)
    {
        foreach (var chip in _opna) { chip.Core.Advance(frames); chip.Rhythm.Advance(frames); }
    }
}

/// <summary>FM以外の音源チップの鍵盤表示とレジスタ表示に使う、レジスタの写しを持つ。音を二重に作ることはしない。</summary>
internal sealed class VgmExtraRegisterMonitor
{
    /// <summary>音源チップ1個分のレジスタの写しと、ボイスごとの使用・発音状態。</summary>
    private sealed class Chip
    {
        public required string Name;
        public VgmYm2610Activity? Opnb;
        public VgmMultiPcmAudio? MultiPcm;
        public uint Clock;
        public readonly byte[] Registers = new byte[1024];
        public readonly byte[] FdsWave = new byte[64];
        public readonly ushort[] WideRegisters = new ushort[1024];
        public readonly byte[][] VoiceRegisters = Enumerable.Range(0, 32).Select(_ => new byte[16]).ToArray();
        public readonly bool[] VoiceUsed = new bool[32];
        public readonly bool[] VoiceActive = new bool[32];
        public int SelectedVoice;
        public int SelectedRegister;
        public bool Used, Enabled = true;
        public byte[] RomA = [], RomB = [];
        public uint NativeRate;
    }

    private readonly List<Chip> _chips = [];
    private readonly Dictionary<byte, (Chip Chip, int Port)> _commands = [];
    private readonly VgmFile _file;
    private readonly int _outputRate;
    private Chip? _aySecond;

    /// <summary>ヘッダーでクロックが設定されている音源チップを登録し、VGM命令コードと対応付ける。</summary>
    public VgmExtraRegisterMonitor(VgmFile file, int rate)
    {
        _file = file;
        _outputRate = rate;
        Add("YM2610", file.Ym2610Clock, rate, (0x58, 0), (0x59, 1));
        Add("Y8950", file.Y8950Clock, rate, (0x5c, 0));
        Add("YMZ280B", file.Ymz280BClock, rate, (0x5d, 0));
        Add("AY8910", file.Ay8910Clock, rate, (0xa0, 0));
        if ((file.Ay8910Clock & 0x40000000) != 0)
        {
            _aySecond = EnsureSecondAy();
        }
        Add("PWM", file.PwmClock, rate, (0xb2, 0));
        Add("GameBoy", file.GameBoyClock, rate, (0xb3, 0));
        Add("NES APU", file.NesApuClock, rate, (0xb4, 0));
        Add("OKIM6295", file.OkiM6295Clock, rate, (0xb8, 0));
        Add("K053260", file.K053260Clock, rate, (0xba, 0));
        Add("K054539", file.K054539Clock, rate);
        Add("C140", file.C140Clock, rate);
        Add("C352", file.C352Clock, rate);
        Add("YMF278B", file.Ymf278BClock, rate);
        Add("YMF271", file.Ymf271Clock, rate);
        Add("RF5C68", file.Rf5C68Clock, rate, (0xb0, 0));
        Add("RF5C164", file.Rf5C164Clock, rate, (0xb1, 0));
        AddMultiPcm(file.MultiPcmClock, rate);
        Add("QSound", file.QSoundClock, rate);
        Add("IremGA20", file.IremGa20Clock, rate, (0xbf, 0));
    }

    /// <summary>MultiPCMを登録する。表示用にMultiPCMのコアでレジスタを解釈する。</summary>
    private void AddMultiPcm(uint rawClock, int rate)
    {
        uint clock = rawClock & 0x3fffffff;
        if (clock == 0) return;
        var core = new VgmMultiPcmAudio(clock);
        var chip = new Chip { Name = "MultiPCM", MultiPcm = core, Clock = clock, NativeRate = core.NativeRate };
        _chips.Add(chip);
        _commands[0xb5] = (chip, 0);
    }

    /// <summary>音源チップを登録する。RF5C68・RF5C164・K054539の本来のレートはクロック÷384とする。</summary>
    private void Add(string name, uint rawClock, int rate, params (byte Command, int Port)[] commands)
    {
        uint clock = rawClock & 0x3fffffff;
        if (clock == 0) return;
        var chip = new Chip { Name = name, Clock = clock, NativeRate = name is "RF5C68" or "RF5C164" or "K054539" ? clock / 384 : (uint)rate,
            Opnb = name == "YM2610" ? new VgmYm2610Activity(clock, rate) : null };
        _chips.Add(chip);
        foreach (var command in commands) _commands[command.Command] = (chip, command.Port);
    }

    /// <summary>2個目のAY8910を用意する。クロックがなければ1.78975MHzとする。</summary>
    private Chip? EnsureSecondAy()
    {
        var existing = Find("AY8910 #2");
        if (existing != null) return existing;
        var first = Find("AY8910");
        uint clock = first?.Clock ?? (_file.Ay8910Clock & 0x3fffffff);
        if (clock == 0) clock = 1789750;
        Add("AY8910 #2", clock, _outputRate);
        return Find("AY8910 #2");
    }

    /// <summary>1バイトのアドレスと値を持つ命令の書込みを記録する。失敗した音源チップは以後無効にする。</summary>
    public bool Write(byte command, byte register, byte value)
    {
        if (!_commands.TryGetValue(command, out var target) || !target.Chip.Enabled) return false;
        var chip = target.Chip;
        int port = target.Port;
        int address = register;
        int data = value;
        // AY8910はアドレスのビット7で2個目を選ぶ。Game Boyもビット7を外す。
        if (command == 0xa0 && (register & 0x80) != 0)
        {
            address = register & 0x7f;
            var second = _aySecond ??= EnsureSecondAy();
            if (second != null && second.Enabled) chip = second;
        }
        else if (command is 0xa0 or 0xb3) address &= 0x7f;
        else if (command == 0xb4)
        {
            address &= 0x7f;
            // VGMはFDSの$4080〜$409FをB4の$20〜$3Fに詰めている。$3Fは$4023のI/O有効化レジスタに使う。
            address = address == 0x3f ? 0x23 :
                (address & 0xe0) == 0x20 ? 0x80 | (address & 0x1f) : address;
        }
        // PWMはアドレス4ビットと値12ビット。
        if (command == 0xb2)
        {
            address = register >> 4;
            data = ((register & 15) << 8) | value;
        }
        // レジスタを記録し、FDSの波形RAMへの書込みは別に保存する。
        try
        {
            if (chip.MultiPcm != null) chip.MultiPcm.Write(address, (byte)data);
            else chip.Opnb?.Write(port, address, data);
            chip.Registers[Math.Clamp(port * 256 + address, 0, chip.Registers.Length - 1)] = value;
            if (chip.Name == "NES APU" && address is >= 0x40 and <= 0x7f &&
                (chip.Registers[0x89] & 0x80) != 0)
                chip.FdsWave[address - 0x40] = (byte)(value & 0x3f);
            chip.Used = true;
            TrackVoice(chip, port, address, value);
            return true;
        }
        catch
        {
            chip.Enabled = false;
            return false;
        }
    }

    /// <summary>ポート付きの書込みを記録する。16ビットの値も別に保持する。</summary>
    public bool Write(string chipName, int port, int address, int data)
    {
        var chip = Find(chipName);
        if (chip == null) return false;
        try
        {
            chip.Opnb?.Write(port, address, data);
            chip.Registers[Math.Clamp(port * 256 + address, 0, chip.Registers.Length - 1)] = (byte)data;
            chip.WideRegisters[Math.Clamp(port * 256 + address, 0, chip.WideRegisters.Length - 1)] = (ushort)data;
            chip.Used = true;
            TrackVoice(chip, port, address, (byte)data);
            TrackWideVoice(chip, port, address, (ushort)data);
            return true;
        }
        catch
        {
            chip.Enabled = false;
            return false;
        }
    }

    /// <summary>RF5C68・RF5C164のRAMへの書込みを記録する。メモリは音声側だけが使い、この表示には影響しないので使用済みにするだけ。</summary>
    public bool WriteRam(string chipName, int address, byte value)
    {
        var chip = Find(chipName);
        if (chip == null) return false;
        if (chip.Name is not ("RF5C68" or "RF5C164")) return false;
        chip.Used = true;
        return true;
    }

    /// <summary>MultiPCMのバンク指定を記録する。</summary>
    public bool WriteMultiPcmBank(byte bank, ushort value)
    {
        var chip = Find("MultiPCM");
        if (chip == null) return false;
        try
        {
            chip.MultiPcm!.WriteBank(bank, value);
            chip.Used = true;
            return true;
        }
        catch
        {
            chip.Enabled = false;
            return false;
        }
    }

    /// <summary>PCM RAMへの転送を記録する。音声側は元のVGMのRAMブロックを使うので、ここでは使用済みにするだけ。</summary>
    public bool LoadRam(byte chipType, int address, ReadOnlySpan<byte> data)
    {
        string chipName = (chipType & 0x7f) switch
        {
            0x01 => "RF5C68",
            0x02 => "RF5C164",
            0x07 => "NES APU",
            _ => ""
        };
        var chip = Find(chipName);
        if (chip == null || data.IsEmpty) return false;
        chip.Used = true;
        return true;
    }

    /// <summary>データブロックのROM・RAMを読み込む。YM2610のADPCM-A・ADPCM-BとMultiPCMのROMだけを保持する。</summary>
    public void LoadRom(byte type, ReadOnlySpan<byte> block)
    {
        // 0xC0〜0xC2はRAM向けで、先頭2バイトがアドレス。
        if (type is 0xc0 or 0xc1 or 0xc2)
        {
            if (block.Length >= 2)
            {
                int address = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(block);
                LoadRam(type switch { 0xc0 => (byte)0x01, 0xc1 => (byte)0x02, _ => (byte)0x07 }, address, block[2..]);
            }
            return;
        }
        // ROMは先頭8バイトが全体の大きさと開始位置。範囲を確かめてから読み込む。
        if (block.Length < 8) return;
        uint size = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(block);
        uint offset = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(block[4..]);
        if (size == 0 || size > 64 * 1024 * 1024 || offset > size || block.Length - 8 > size - offset) return;
        byte[] payload = block[8..].ToArray();
        try
        {
            if (type == 0x82 && Find("YM2610") is { } a)
            {
                a.RomA = MergeRom(a.RomA, size, offset, payload);
                a.Opnb!.SetAdpcmA(a.RomA);
            }
            if (type == 0x83 && Find("YM2610") is { } b)
            {
                b.RomB = MergeRom(b.RomB, size, offset, payload);
                b.Opnb!.SetAdpcmB(b.RomB);
            }
            if (type == 0x89 && Find("MultiPCM") is { } multi)
                multi.MultiPcm!.LoadRom((int)size, (int)offset, payload);
        }
        // 壊れたROMブロックは無視し、レジスタだけの表示は続ける。
        catch
        {
        }
    }

    /// <summary>有効な音源チップを名前で探す。</summary>
    private Chip? Find(string name) => _chips.Find(chip => chip.Name == name && chip.Enabled);

    /// <summary>レジスタ書込みから、ボイスごとの使用・発音状態を更新する。</summary>
    private static void TrackVoice(Chip chip, int port, int address, byte value)
    {
        // アドレスからボイス番号を決め、使用済みにする。
        int voice = chip.Name switch
        {
            "AY8910" or "AY8910 #2" when address is >= 0 and <= 5 => address / 2,
            "AY8910" or "AY8910 #2" when address is >= 8 and <= 10 => address - 8,
            "GameBoy" when address is >= 0x00 and <= 0x04 => 0,
            "GameBoy" when address is >= 0x06 and <= 0x09 => 1,
            "GameBoy" when address is >= 0x0a and <= 0x0e => 2,
            "GameBoy" when address is >= 0x10 and <= 0x13 => 3,
            "NES APU" when address is >= 0x00 and <= 0x13 => address / 4,
            "NES APU" when address is >= 0x40 and <= 0x7f => 5,
            "NES APU" when address is >= 0x80 and <= 0x9f => 5,
            _ => -1
        };
        if (voice >= 0) chip.VoiceUsed[voice] = true;

        // 音源チップごとに、キーオン・キーオフに当たる書込みを発音状態に反映する。
        if ((chip.Name == "AY8910" || chip.Name == "AY8910 #2") && address == 7)
            for (int channel = 0; channel < 3; channel++) chip.VoiceUsed[channel] = true;
        else if (chip.Name == "GameBoy")
        {
            if ((address is 0x04 or 0x09 or 0x0e or 0x13) && (value & 0x80) != 0) chip.VoiceActive[voice] = true;
            if ((address is 0x02 or 0x07 or 0x11) && (value & 0xf8) == 0) chip.VoiceActive[voice] = false;
            if (address == 0x0a && (value & 0x80) == 0) chip.VoiceActive[2] = false;
            if (address == 0x16 && (value & 0x80) == 0) Array.Clear(chip.VoiceActive);
        }
        else if (chip.Name == "NES APU" && address == 0x15)
        {
            for (int channel = 0; channel < 5; channel++)
            {
                chip.VoiceUsed[channel] = true;
                chip.VoiceActive[channel] = (value & (1 << channel)) != 0;
            }
        }
        else if (chip.Name is "RF5C68" or "RF5C164")
        {
            if (address == 7 && (value & 0x40) != 0)
            {
                chip.SelectedVoice = value & 7;
                chip.VoiceUsed[chip.SelectedVoice] = true;
            }
            else if (address is >= 0 and <= 6)
            {
                chip.VoiceRegisters[chip.SelectedVoice][address] = value;
                chip.VoiceUsed[chip.SelectedVoice] = true;
            }
            else if (address == 8)
            {
                for (int channel = 0; channel < 8; channel++)
                {
                    chip.VoiceUsed[channel] |= (value & (1 << channel)) == 0;
                    chip.VoiceActive[channel] = (value & (1 << channel)) == 0;
                }
            }
        }
        else if (chip.Name == "MultiPCM")
        {
            if (address == 1)
            {
                int selector = value & 0x1f;
                chip.SelectedVoice = (selector & 7) == 7 ? -1 : selector - selector / 8;
            }
            else if (address == 2)
                chip.SelectedRegister = Math.Min(value, (byte)10);
            else if (address == 0 && chip.SelectedVoice is >= 0 and < 28)
            {
                int channel = chip.SelectedVoice;
                int register = chip.SelectedRegister;
                chip.VoiceRegisters[channel][register] = value;
                chip.VoiceUsed[channel] = true;
                if (register == 4) chip.VoiceActive[channel] = (value & 0x80) != 0;
            }
        }
        else if (chip.Name == "C140" && address is >= 0 and < 0x180)
        {
            int channel = address >> 4;
            chip.VoiceUsed[channel] = true;
            if ((address & 15) == 5) chip.VoiceActive[channel] = (value & 0xc0) != 0;
        }
        else if (chip.Name == "K053260")
        {
            if (address is >= 0x08 and <= 0x27) chip.VoiceUsed[(address - 8) / 8] = true;
            else if (address == 0x28)
                for (int channel = 0; channel < 4; channel++)
                {
                    chip.VoiceUsed[channel] = true;
                    chip.VoiceActive[channel] = (value & (1 << channel)) != 0;
                }
        }
        else if (chip.Name == "K054539")
        {
            if (address is >= 0 and < 0x100) chip.VoiceUsed[address / 0x20] = true;
            else if (address is 0x214 or 0x215)
                for (int channel = 0; channel < 8; channel++)
                {
                    chip.VoiceUsed[channel] |= (value & (1 << channel)) != 0;
                    if ((value & (1 << channel)) != 0) chip.VoiceActive[channel] = address == 0x214;
                }
        }
    }

    /// <summary>C352の16ビットレジスタから、ボイスの使用・発音状態を更新する。0x202への書込みでキーオン・キーオフのフラグを反映する。</summary>
    private static void TrackWideVoice(Chip chip, int port, int address, ushort value)
    {
        if (chip.Name != "C352") return;
        if (address is >= 0 and < 0x100)
        {
            chip.VoiceUsed[address / 8] = true;
            return;
        }
        if (address != 0x202) return;
        for (int channel = 0; channel < 32; channel++)
        {
            ushort flags = chip.WideRegisters[channel * 8 + 3];
            if ((flags & 0x4000) != 0) chip.VoiceActive[channel] = true;
            if ((flags & 0x2000) != 0) chip.VoiceActive[channel] = false;
        }
    }

    /// <summary>ROMの一部を、全体の大きさに合わせた配列へ書き込む。</summary>
    private static byte[] MergeRom(byte[] current, uint size, uint offset, byte[] payload)
    {
        if (current.Length != (int)size) Array.Resize(ref current, (int)size);
        payload.CopyTo(current, (int)offset);
        return current;
    }

    /// <summary>YM2610のADPCMの再生位置を進める。</summary>
    public void Advance(int frames)
    {
        foreach (var chip in _chips) chip.Opnb?.Advance(frames);
    }

    /// <summary>表示用のチャンネル情報を作る。自前の発音情報を持つ音源チップは除く。</summary>
    public Music.MusicChannelSnapshot[] Capture(bool playing) => _chips.Where(chip => chip.Enabled &&
        chip.Name is not ("C352" or "QSound" or "YMZ280B" or "OKIM6295" or "YMF271"))
        .SelectMany(chip => CaptureChip(chip, playing)).ToArray();

    /// <summary>音源チップごとの表示用チャンネル情報を作る。個別に表示できない音源チップはMIXチャンネル1個にまとめる。</summary>
    private static IEnumerable<Music.MusicChannelSnapshot> CaptureChip(Chip chip, bool playing)
    {
        return chip.Name switch
        {
            "YM2610" => chip.Opnb!.CaptureAdpcm(playing),
            "AY8910" or "AY8910 #2" => CaptureAy(chip, playing),
            "GameBoy" => CaptureGameBoy(chip, playing),
            "NES APU" => CaptureNes(chip, playing),
            "RF5C68" or "RF5C164" => CaptureRf5C(chip, playing),
            "MultiPCM" => CaptureMultiPcm(chip, playing),
            "C140" => CaptureC140(chip, playing),
            "K053260" => CaptureK053260(chip, playing),
            "K054539" => CaptureK054539(chip, playing),
            _ => [MixedChannel(chip, playing)]
        };
    }

    /// <summary>チャンネルごとの情報がない音源チップのMIXチャンネルを作る。</summary>
    private static Music.MusicChannelSnapshot MixedChannel(Chip chip, bool playing) => new()
    {
        Id = $"{chip.Name}:MIX", ChipName = chip.Name, Label = "MIX", Kind = Music.MusicChannelKind.Other,
        IsInUse = chip.Used, IsKeyOn = false, Level = 0,
        Pan = 3, PanLeftLevel = 1, PanRightLevel = 1
    };

    private static int? MidiNote(double frequency) => frequency > 0
        ? Math.Clamp((int)Math.Round(69 + 12 * Math.Log2(frequency / 440.0)), 0, 127) : null;

    /// <summary>基準値に対する再生速度の比から音高を求める。基準値は中央のCに当たる。</summary>
    private static int? RelativePcmNote(int pitch, int unity) => pitch > 0 && unity > 0
        ? Math.Clamp(60 + (int)Math.Round(12 * Math.Log2(pitch / (double)unity)), 0, 127) : null;

    private static double? RelativePcmPitch(int pitch, int unity) => unity > 0 ? Music.MusicPitch.FromRatio(pitch / (double)unity) : null;

    /// <summary>指定範囲に0以外の値があるか。</summary>
    private static bool HasNonZero(byte[] array, int offset, int length)
    {
        if (array == null || offset + length > array.Length) return false;
        for (int i = 0; i < length; i++)
            if (array[offset + i] != 0) return true;
        return false;
    }

    /// <summary>AY8910の3チャンネルの表示用情報を作る。</summary>
    private static IEnumerable<Music.MusicChannelSnapshot> CaptureAy(Chip chip, bool playing)
    {
        for (int channel = 0; channel < 3; channel++)
        {
            int period = chip.Registers[channel * 2] | ((chip.Registers[channel * 2 + 1] & 15) << 8);
            bool tone = (chip.Registers[7] & (1 << channel)) == 0;
            bool noise = (chip.Registers[7] & (8 << channel)) == 0;
            int volume = chip.Registers[8 + channel] & 15;
            bool envelope = (chip.Registers[8 + channel] & 16) != 0;
            double frequency = period > 0 ? chip.Clock / (16.0 * period) : 0;
            bool active = playing && (tone || noise) && (volume > 0 || envelope);
            yield return new()
            {
                Id = $"{chip.Name}:{channel}", ChipName = chip.Name, Label = $"PSG{channel + 1:00}",
                Kind = tone ? Music.MusicChannelKind.Psg : Music.MusicChannelKind.Noise,
                IsInUse = chip.VoiceUsed[channel], IsKeyOn = active, NoteNumber = tone ? MidiNote(frequency) : null,
                Frequency = tone ? frequency : null, Level = active ? (envelope ? .6 : volume / 15.0) : 0,
                Volume = envelope ? null : volume, Pan = 3, DataString = envelope ? "ENV" : $"T:{period:X3}"
            };
        }
    }

    /// <summary>Game Boyの4チャンネルの表示用情報を作る。</summary>
    private static IEnumerable<Music.MusicChannelSnapshot> CaptureGameBoy(Chip chip, bool playing)
    {
        byte[] r = chip.Registers;
        int[] low = [0x03, 0x08, 0x0d];
        int[] high = [0x04, 0x09, 0x0e];
        int[] volumeRegister = [0x02, 0x07, 0x0c, 0x11];
        byte routing = r[0x15];
        bool master = (r[0x16] & 0x80) != 0;
        for (int channel = 0; channel < 4; channel++)
        {
            bool pitched = channel < 3;
            int period = pitched ? r[low[channel]] | ((r[high[channel]] & 7) << 8) : 0;
            double divisor = channel == 2 ? 64.0 : 32.0;
            double frequency = pitched && period < 2048 ? chip.Clock / (divisor * (2048 - period)) : 0;
            int volume = channel == 2 ? (r[volumeRegister[channel]] >> 5) & 3 : (r[volumeRegister[channel]] >> 4) & 15;
            double level = channel == 2 ? volume switch { 1 => 1, 2 => .5, 3 => .25, _ => 0 } : volume / 15.0;
            int pan = (((routing >> (channel + 4)) & 1) << 1) | ((routing >> channel) & 1);
            bool active = playing && master && chip.VoiceActive[channel] && level > 0 && pan != 0;
            // 波形メモリチャンネルは、表示用に16バイトの波形を32点に展開する。
            byte[]? waveform = null;
            if (channel == 2)
            {
                int waveBase = r.Length > 0x3f && HasNonZero(r, 0x30, 16) ? 0x30 : 0x20;
                waveform = new byte[32];
                for (int i = 0; i < 16; i++)
                {
                    byte b = (waveBase + i < r.Length) ? r[waveBase + i] : (byte)0;
                    waveform[i * 2] = (byte)(((b >> 4) & 0x0f) * 255 / 15);
                    waveform[i * 2 + 1] = (byte)((b & 0x0f) * 255 / 15);
                }
            }
            yield return new()
            {
                Id = $"GameBoy:{channel}", ChipName = "GameBoy", Label = channel switch { 0 => "PULSE1", 1 => "PULSE2", 2 => "WAVE", _ => "NOISE" },
                Kind = pitched ? Music.MusicChannelKind.Psg : Music.MusicChannelKind.Noise,
                IsInUse = chip.VoiceUsed[channel], IsKeyOn = active, NoteNumber = pitched ? MidiNote(frequency) : null,
                Frequency = pitched ? frequency : null, Level = active ? level : 0, Volume = volume, Pan = pan,
                DataString = pitched ? $"T:{period:X3}" : $"N:{r[0x12]:X2}",
                Waveform = waveform
            };
        }
    }

    /// <summary>NES APUの5チャンネルと、使われていればFDSの表示用情報を作る。</summary>
    private static IEnumerable<Music.MusicChannelSnapshot> CaptureNes(Chip chip, bool playing)
    {
        byte[] r = chip.Registers;
        for (int channel = 0; channel < 5; channel++)
        {
            bool pitched = channel < 3;
            int timer = channel switch
            {
                0 => r[2] | ((r[3] & 7) << 8),
                1 => r[6] | ((r[7] & 7) << 8),
                2 => r[0x0a] | ((r[0x0b] & 7) << 8),
                _ => 0
            };
            double frequency = pitched ? chip.Clock / ((channel == 2 ? 32.0 : 16.0) * (timer + 1)) : 0;
            int volume = channel switch { 0 => r[0] & 15, 1 => r[4] & 15, 2 => 12, 3 => r[0x0c] & 15, _ => 10 };
            bool active = playing && chip.VoiceActive[channel] && volume > 0;
            int duty = channel < 2 ? r[channel * 4] >> 6 : 0;
            yield return new()
            {
                Id = $"NES APU:{channel}", ChipName = "NES APU",
                Label = channel switch { 0 => "PULSE1", 1 => "PULSE2", 2 => "TRIANGLE", 3 => "NOISE", _ => "DPCM" },
                Kind = pitched ? Music.MusicChannelKind.Psg : channel == 3 ? Music.MusicChannelKind.Noise : Music.MusicChannelKind.Pcm,
                IsInUse = chip.VoiceUsed[channel], IsKeyOn = active, NoteNumber = pitched ? MidiNote(frequency) : null,
                Frequency = pitched ? frequency : null, Level = active ? volume / 15.0 : 0, Volume = volume, Pan = 3,
                DataString = pitched ? $"T:{timer:X3}" : channel == 3 ? $"N:{r[0x0e]:X2}" : $"D:{r[0x10]:X2}",
                Waveform = channel < 2 ? Music.NesWaveform.Pulse(duty) : null,
                WaveformLabel = channel < 2 ? Music.NesWaveform.DutyLabel(duty) : "Wavetable : "
            };
        }

        // FDSの波形メモリチャンネル。
        if (chip.VoiceUsed[5])
        {
            int pitch = r[0x82] | ((r[0x83] & 0x0f) << 8);
            double frequency = chip.Clock * pitch / (65536.0 * 64.0);
            int envelopeVolume = Math.Min(32, r[0x80] & 0x3f);
            bool directVolume = (r[0x80] & 0x80) != 0;
            bool active = playing && pitch > 0 && (r[0x83] & 0x80) == 0 &&
                (r[0x89] & 0x80) == 0 && (!directVolume || envelopeVolume > 0);
            yield return new()
            {
                Id = "NES APU:5", ChipName = "NES FDS", Label = "FDS",
                Kind = Music.MusicChannelKind.Wavetable, IsInUse = true,
                IsKeyOn = active, NoteNumber = active ? MidiNote(frequency) : null,
                Frequency = frequency > 0 ? frequency : null,
                Level = active ? (directVolume ? envelopeVolume / 32.0 : 0.6) : 0,
                Volume = directVolume ? envelopeVolume : null, Pan = 3,
                DataString = $"F:{pitch:X3}",
                Waveform = Music.NesWaveform.Fds(chip.FdsWave)
            };
        }
    }
    /// <summary>RF5C68・RF5C164の8チャンネルの表示用情報を作る。0x800が等倍の再生速度。</summary>
    private static IEnumerable<Music.MusicChannelSnapshot> CaptureRf5C(Chip chip, bool playing)
    {
        for (int channel = 0; channel < 8; channel++)
        {
            if (!chip.VoiceUsed[channel]) continue;
            byte[] r = chip.VoiceRegisters[channel];
            int step = r[2] | (r[3] << 8);
            int envelope = r[0];
            int leftVolume = r[1] & 15;
            int rightVolume = (r[1] >> 4) & 15;
            double sampleRate = chip.NativeRate * step / 2048.0;
            bool active = playing && chip.VoiceActive[channel] && envelope > 0 && (leftVolume > 0 || rightVolume > 0);
            double left = envelope / 255.0 * leftVolume / 15.0;
            double right = envelope / 255.0 * rightVolume / 15.0;
            yield return new()
            {
                Id = $"{chip.Name}:{channel}", ChipName = chip.Name, Label = $"PCM{channel + 1:00}",
                Kind = Music.MusicChannelKind.Pcm, IsInUse = true, IsKeyOn = active,
                NoteNumber = RelativePcmNote(step, 0x0800), Pitch = RelativePcmPitch(step, 0x0800), Frequency = sampleRate,
                Level = active ? Math.Max(left, right) : 0, Volume = envelope,
                Pan = (left > 0 ? 2 : 0) | (right > 0 ? 1 : 0), PanLeftLevel = left, PanRightLevel = right,
                DataString = $"RATE:{sampleRate:F0}Hz STEP:{step:X4}"
            };
        }
    }

    /// <summary>C140の24チャンネルの表示用情報を作る。0x8000が等倍の再生速度。</summary>
    private static IEnumerable<Music.MusicChannelSnapshot> CaptureC140(Chip chip, bool playing)
    {
        for (int channel = 0; channel < 24; channel++)
        {
            int address = channel * 16;
            int rightVolume = chip.Registers[address];
            int leftVolume = chip.Registers[address + 1];
            int pitch = (chip.Registers[address + 2] << 8) | chip.Registers[address + 3];
            double sampleRate = chip.Clock / 576.0 * 2.0 * pitch / 65536.0;
            bool active = playing && chip.VoiceActive[channel] && pitch > 0 && (leftVolume > 0 || rightVolume > 0);
            double left = leftVolume / 255.0;
            double right = rightVolume / 255.0;
            yield return new()
            {
                Id = $"C140:{channel}", ChipName = "C140", Label = $"PCM{channel + 1:00}",
                Kind = Music.MusicChannelKind.Pcm, IsInUse = chip.VoiceUsed[channel], IsKeyOn = active,
                NoteNumber = RelativePcmNote(pitch, 0x8000), Pitch = RelativePcmPitch(pitch, 0x8000), Frequency = sampleRate,
                Level = active ? Math.Max(left, right) : 0, Volume = Math.Max(leftVolume, rightVolume),
                Pan = (left > 0 ? 2 : 0) | (right > 0 ? 1 : 0), PanLeftLevel = left, PanRightLevel = right,
                DataString = $"RATE:{sampleRate:F0}Hz P:{pitch:X4}"
            };
        }
    }

    /// <summary>MultiPCMの28スロットの表示用情報を作る。音程は等倍に対する比で表示する。</summary>
    private static IEnumerable<Music.MusicChannelSnapshot> CaptureMultiPcm(Chip chip, bool playing)
    {
        for (int channel = 0; channel < 28; channel++)
        {
            if (!chip.VoiceUsed[channel]) continue;
            byte[] r = chip.VoiceRegisters[channel];
            int octave = ((r[3] >> 4) - 1) & 15;
            if (octave >= 8) octave -= 16;
            int pitch = ((r[3] & 15) << 6) | (r[2] >> 2);
            double ratio = (1024 + pitch) / 1024.0 * Math.Pow(2, octave);
            double sampleRate = chip.NativeRate * ratio;
            int attenuation = (r[5] >> 1) & 0x7f;
            double level = Math.Pow(10, attenuation * (-24.0 / 64) / 20.0);
            // パンの値から左右の音量を求める。0は中央、8は無音、7と9は片側のみ。
            int rawPan = (r[0] >> 4) & 15;
            double left, right;
            if (rawPan == 0) left = right = 1;
            else if (rawPan == 8) left = right = 0;
            else if (rawPan < 8)
            {
                left = rawPan == 7 ? 0 : Math.Pow(10, rawPan * -3.0 / 20.0);
                right = 1;
            }
            else
            {
                int inverted = 16 - rawPan;
                left = 1;
                right = inverted == 7 ? 0 : Math.Pow(10, inverted * -3.0 / 20.0);
            }
            bool active = playing && chip.VoiceActive[channel] && ratio > 0 && (left > 0 || right > 0);
            yield return new()
            {
                Id = $"MultiPCM:{channel}", ChipName = "MultiPCM", Label = $"PCM{channel + 1:00}",
                Kind = Music.MusicChannelKind.Pcm, IsInUse = true, IsKeyOn = active,
                NoteNumber = Math.Clamp(60 + (int)Math.Round(12 * Math.Log2(ratio)), 0, 127), Pitch = Music.MusicPitch.FromRatio(ratio), Frequency = sampleRate,
                Level = active ? level * Math.Max(left, right) : 0, Volume = 127 - attenuation,
                Pan = (left > 0 ? 2 : 0) | (right > 0 ? 1 : 0), PanLeftLevel = left, PanRightLevel = right,
                DataString = $"RATE:{sampleRate:F0}Hz S:{r[1]:X2} O:{octave:+0;-0;0} P:{pitch:X3}"
            };
        }
    }

    /// <summary>K053260の4チャンネルの表示用情報を作る。</summary>
    private static IEnumerable<Music.MusicChannelSnapshot> CaptureK053260(Chip chip, bool playing)
    {
        int[,] panTable = { { 0, 0 }, { 127, 0 }, { 116, 52 }, { 104, 73 }, { 90, 90 }, { 73, 104 }, { 52, 116 }, { 0, 127 } };
        for (int channel = 0; channel < 4; channel++)
        {
            if (!chip.VoiceUsed[channel]) continue;
            int address = 8 + channel * 8;
            int pitch = chip.Registers[address] | ((chip.Registers[address + 1] & 15) << 8);
            int volume = chip.Registers[address + 7] & 127;
            int panRegister = chip.Registers[channel < 2 ? 0x2c : 0x2d];
            int pan = channel % 2 == 0 ? panRegister & 7 : (panRegister >> 3) & 7;
            double left = panTable[pan, 0] / 127.0;
            double right = panTable[pan, 1] / 127.0;
            double sampleRate = pitch < 0x1000 ? chip.Clock / (double)(0x1000 - pitch) : 0;
            bool active = playing && chip.VoiceActive[channel] && (chip.Registers[0x2f] & 2) != 0 && volume > 0 && pan != 0;
            yield return new()
            {
                Id = $"K053260:{channel}", ChipName = "K053260", Label = $"PCM{channel + 1:00}",
                Kind = Music.MusicChannelKind.Pcm, IsInUse = true, IsKeyOn = active,
                NoteNumber = pitch < 0x1000 ? RelativePcmNote(0x40, 0x1000 - pitch) : null, Pitch = pitch < 0x1000 ? RelativePcmPitch(0x40, 0x1000 - pitch) : null, Frequency = sampleRate,
                Level = active ? volume / 127.0 : 0, Volume = volume,
                Pan = (left > 0 ? 2 : 0) | (right > 0 ? 1 : 0), PanLeftLevel = left, PanRightLevel = right,
                DataString = $"RATE:{sampleRate:F0}Hz P:{pitch:X3}"
            };
        }
    }

    /// <summary>K054539の8チャンネルの表示用情報を作る。0x10000が等倍の再生速度。</summary>
    private static IEnumerable<Music.MusicChannelSnapshot> CaptureK054539(Chip chip, bool playing)
    {
        for (int channel = 0; channel < 8; channel++)
        {
            if (!chip.VoiceUsed[channel]) continue;
            int address = channel * 0x20;
            int pitch = chip.Registers[address] | (chip.Registers[address + 1] << 8) | (chip.Registers[address + 2] << 16);
            int attenuation = chip.Registers[address + 3];
            int rawPan = chip.Registers[address + 5];
            int normalizedPan = rawPan is >= 1 and <= 0x1f ? rawPan : 0x10;
            double balance = Math.Clamp((0x10 - normalizedPan) / 15.0, -1, 1);
            double left = Math.Sqrt((1 - balance) / 2);
            double right = Math.Sqrt((1 + balance) / 2);
            double level = Math.Pow(10, (-36.0 * attenuation / 0x40) / 20.0);
            double sampleRate = chip.NativeRate * pitch / 65536.0;
            bool active = playing && chip.VoiceActive[channel] && pitch > 0;
            yield return new()
            {
                Id = $"K054539:{channel}", ChipName = "K054539", Label = $"PCM{channel + 1:00}",
                Kind = Music.MusicChannelKind.Pcm, IsInUse = true, IsKeyOn = active,
                NoteNumber = RelativePcmNote(pitch, 0x10000), Pitch = RelativePcmPitch(pitch, 0x10000), Frequency = sampleRate,
                Level = active ? level : 0, Volume = 255 - attenuation,
                Pan = (left > .001 ? 2 : 0) | (right > .001 ? 1 : 0), PanLeftLevel = left, PanRightLevel = right,
                DataString = $"RATE:{sampleRate:F0}Hz P:{pitch:X6}"
            };
        }
    }

    /// <summary>表示用のレジスタ一覧を返す。別の監視が出す音源チップは除く。</summary>
    public Music.MusicRegisterBank[] CaptureRegisters() => _chips.Where(chip => chip.Enabled && chip.Name is not ("YM2610" or "K051649" or "YM2413" or "YM3812" or "YM3526")).Select(chip => new Music.MusicRegisterBank
        {
            ChipName = chip.Name, Port = 0, Values = chip.Registers.AsSpan(0, 256).ToArray()
        }).ToArray();
}

/// <summary>SegaPCMの表示用の監視。SegaPCMはクロック÷128で動く。</summary>
internal sealed class VgmSegaPcmMonitor
{
    private readonly byte[] _registers = Enumerable.Repeat((byte)0xff, 2048).ToArray();
    private readonly uint _nativeRate;
    private readonly bool[] _used = new bool[16];
    public VgmSegaPcmMonitor(VgmFile file, int rate) => _nativeRate = (file.SegaPcmClock & 0x3fffffff) / 128;
    /// <summary>レジスタ書込みを記録し、書き込まれたチャンネルを使用済みにする。</summary>
    public void Write(int address, byte value)
    {
        _registers[address & 0x7ff] = value;
        _used[(address & 0x7f) / 8] = true;
    }
    /// <summary>SegaPCMのROMブロックのヘッダーを検証する。</summary>
    public void LoadRom(ReadOnlySpan<byte> block)
    {
        if (block.Length < 8) throw new InvalidDataException("Truncated SegaPCM ROM block.");
        uint size = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(block);
        uint offset = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(block[4..]);
        if (size == 0 || size > 64 * 1024 * 1024 || offset > size || block.Length - 8 > size - offset)
            throw new InvalidDataException("Invalid SegaPCM ROM range.");
    }

    /// <summary>書込みのあったチャンネルの表示用情報を作る。</summary>
    public IEnumerable<Music.MusicChannelSnapshot> Capture(bool playing)
    {
        for (int ch = 0; ch < 16; ch++)
        {
            // 触れていない実チャンネルのせいで、使っているチャンネルが表示の12行の上限より後ろに隠れないようにする。ラベルには実際のチャンネル番号を残す。
            if (!_used[ch]) continue;
            bool active = playing && (_registers[ch * 8 + 0x86] & 1) == 0;
            int left = _registers[ch * 8 + 2] & 127;
            int right = _registers[ch * 8 + 3] & 127;
            int delta = _registers[ch * 8 + 7] & 255;
            // SegaPCMには音高の情報がないため、再生速度の比を鍵盤に出す。標準の0x80を中央のCとする。
            int? note = delta > 0
                ? Math.Clamp(60 + (int)Math.Round(12 * Math.Log2(delta / 128.0)), 0, 127)
                : null;
            yield return new() { Id = $"SegaPCM:{ch}", ChipName = "SegaPCM", Label = $"PCM{ch + 1:00}",
                Kind = Music.MusicChannelKind.Pcm, IsInUse = true, IsKeyOn = active && (left > 0 || right > 0),
                NoteNumber = note, Pitch = delta > 0 ? Music.MusicPitch.FromRatio(delta / 128.0) : null, Frequency = delta > 0 ? _nativeRate * delta / 256.0 : null,
                Level = active ? Math.Max(left, right) / 127.0 : 0, Volume = Math.Max(left, right),
                Pan = (left > 0 ? 2 : 0) | (right > 0 ? 1 : 0), DataString = $"D:{delta:X2}" };
        }
    }
}

/// <summary>PCエンジン（TurboGrafx-16）のVGMで使うHuC6280の6チャンネルPSGの表示用の監視。</summary>
internal sealed class VgmHuC6280Monitor
{
    private readonly byte[] _registers = new byte[256];
    private readonly byte[][] _waves = Enumerable.Range(0, 6).Select(_ => new byte[32]).ToArray();
    private readonly int[] _wavePositions = new int[6];
    private readonly bool[] _used = new bool[6];
    private readonly uint _clock;
    private int _selected;

    public VgmHuC6280Monitor(VgmFile file, int rate)
    {
        _clock = file.HuC6280Clock & 0x3fffffff;
    }

    /// <summary>レジスタ書込みを記録する。選択中のチャンネルのレジスタは、チャンネルごとの写しにも保存する。</summary>
    public void Write(byte register, byte value)
    {
        register &= 0x0f;
        _registers[register] = value;
        if (register == 0)
        {
            _selected = Math.Min(value & 7, 5);
            return;
        }
        if (register is >= 2 and <= 7)
        {
            _used[_selected] = true;
            TrackChannelRegister(register, value);
        }
        // 波形の書込み位置をリセットし、DDAでなければ波形データを記録する。
        if (register == 4 && (value & 0x40) == 0)
        {
            _wavePositions[_selected] = 0;
        }
        if (register == 6 && (_registers[0x20 + _selected * 8 + 2] & 0x40) == 0)
        {
            _waves[_selected][_wavePositions[_selected]++ & 31] = (byte)(value & 31);
        }
    }

    /// <summary>表示用のレジスタ一覧を返す。</summary>
    public Music.MusicRegisterBank CaptureRegisters() => new()
    {
        ChipName = "HuC6280",
        Values = _registers.ToArray()
    };

    /// <summary>6チャンネルの表示用情報を作る。</summary>
    public Music.MusicChannelSnapshot[] Capture(bool playing)
    {
        int mainLeft = (_registers[1] >> 4) & 15;
        int mainRight = _registers[1] & 15;
        var result = new Music.MusicChannelSnapshot[6];
        for (int channel = 0; channel < 6; channel++)
        {
            // HuC6280のレジスタ窓は全チャンネル共通の選択式なので、チャンネルごとの写しから最後の値を読む。
            int baseIndex = 0x20 + channel * 8;
            int period = _registers[baseIndex] | ((_registers[baseIndex + 1] & 15) << 8);
            int control = _registers[baseIndex + 2];
            int balance = _registers[baseIndex + 3];
            int noise = _registers[baseIndex + 5];
            bool enabled = (control & 0x80) != 0;
            bool dda = (control & 0x40) != 0;
            bool noisy = channel >= 4 && (noise & 0x80) != 0;
            double frequency = period > 0 ? _clock / (32.0 * period) : 0;
            int? note = !dda && !noisy && frequency > 0
                ? Math.Clamp((int)Math.Round(69 + 12 * Math.Log2(frequency / 440)), 0, 127)
                : null;
            double volume = (control & 31) / 31.0;
            double left = volume * mainLeft / 15.0 * ((balance >> 4) & 15) / 15.0;
            double right = volume * mainRight / 15.0 * (balance & 15) / 15.0;
            bool active = playing && enabled && (left > 0 || right > 0);
            byte[]? waveform = null;
            if (!noisy && !dda)
            {
                waveform = new byte[32];
                for (int i = 0; i < 32; i++)
                {
                    waveform[i] = (byte)(_waves[channel][i] * 255 / 31);
                }
            }
            result[channel] = new()
            {
                Id = $"HuC6280:{channel}", ChipName = "HuC6280", Label = $"PSG{channel + 1:00}",
                Kind = noisy ? Music.MusicChannelKind.Noise : dda ? Music.MusicChannelKind.Pcm : Music.MusicChannelKind.Psg,
                IsInUse = _used[channel], IsKeyOn = active, NoteNumber = note,
                Frequency = note.HasValue ? frequency : null, Level = active ? Math.Max(left, right) : 0,
                Volume = control & 31, Pan = (left > 0 ? 2 : 0) | (right > 0 ? 1 : 0),
                PanLeftLevel = left, PanRightLevel = right,
                DataString = noisy ? $"N:{noise & 31:X2}" : dda ? "DDA" : $"T:{period:X3}",
                Waveform = waveform
            };
        }
        return result;
    }

    /// <summary>選択中のチャンネルのレジスタを、表示用のチャンネルごとの写しに保存する。</summary>
    public void TrackChannelRegister(byte register, byte value)
    {
        if (register is < 2 or > 7) return;
        int index = 0x20 + _selected * 8 + register - 2;
        _registers[index] = value;
    }
}
