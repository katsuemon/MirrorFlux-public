namespace MirrorFlux.Services.Music;

/// <summary>
/// チャンネルの音源の種類。
/// 鍵盤やチャンネル表示の色分けとラベルに使う。
/// </summary>
public enum MusicChannelKind { Fm, Psg, Pcm, Noise, Midi, Other, Adpcm, Rhythm, Wavetable }

/// <summary>
/// 256バイトのレジスタポート1つ分の写し（最後に書いた値）。
/// 再生側から切り離して持つ。
/// </summary>
public sealed record MusicRegisterBank
{
    public string ChipName { get; init; } = "";
    public int Port { get; init; }
    public string Label => $"{ChipName}  PORT {Port}";
    public byte[] Values { get; init; } = new byte[256];
}

/// <summary>
/// 1チャンネル分の表示用の値。
/// 再生側から切り離して持ち、分からない・当てはまらない値は null のままにする。
/// </summary>
public sealed record MusicChannelSnapshot
{
    public string Id { get; init; } = "";
    public string Label { get; init; } = "";
    public string ChipName { get; init; } = "";
    public MusicChannelKind Kind { get; init; }
    /// <summary>
    /// 種類の表示名。
    /// その他の音源は音源名から ADPCM・FM・PCM などに振り分ける。
    /// </summary>
    public string KindLabel => Kind == MusicChannelKind.Rhythm ? $"RHYTHM {Label}" : Kind == MusicChannelKind.Other
        ? ChipName switch
        {
            "OKIM6295" => "ADPCM",
            "YM2413" or "YM3526" or "YM3812" or "YMF262" => "FM",
            "C140" or "Namco C140" or "C352" or "Namco C352" or "MultiPCM" or "RF5C68" or "RF5C164" or "K053260" or "K054539" or "SegaPCM" => "PCM",
            _ => "MIX"
        }
        : Kind.ToString().ToUpperInvariant();
    public int? NoteNumber { get; init; }
    /// <summary>
    /// 半音より細かい音程（MIDIノート番号の小数）。
    /// 音源のレジスタから分かる場合だけ入れる。
    /// nullのときはFM・PSG・波形メモリ音源の Frequency から求める（MusicPitch.Display）。
    /// </summary>
    public double? Pitch { get; init; }
    public bool IsNoteEstimated { get; init; }
    public int[] KeyboardNotes { get; init; } = [];
    public bool IsKeyOn { get; init; }
    /// <summary>キーオフからキーオンに変わるたびに増える回数。</summary>
    public int KeyGeneration { get; init; }
    /// <summary>音源のピッチ LFO（PMS/VIB）がこのチャンネルで有効か。</summary>
    public bool HasHardwarePitchLfo { get; init; }
    /// <summary>いまの曲でこの音が使われたか。</summary>
    public bool IsInUse { get; init; } = true;
    public double Level { get; init; }
    /// <summary>Level がすでに音源のエンベロープに沿っていて、表示側でアタック・リリースのなめらかさを足してはいけないか。</summary>
    public bool UsesEnvelopeTiming { get; init; }
    public double? AttackSeconds { get; init; }
    public double? ReleaseSeconds { get; init; }
    public bool IsReleasing { get; init; }
    // 0: 出力なし、1: 右、2: 左、3: 両方。
    public int Pan { get; init; } = 3;
    /// <summary>左右の音量を段階的に持つ音源向けの、左右それぞれの音量。</summary>
    public double? PanLeftLevel { get; init; }
    public double? PanRightLevel { get; init; }
    public int? Volume { get; init; }
    public int? Algorithm { get; init; }
    public int? Feedback { get; init; }
    public int? SlotMask { get; init; }
    public double? Frequency { get; init; }
    public string DataString { get; init; } = "";
    /// <summary>波形メモリ音源の波形（ふつうは32バイト、0〜255）。</summary>
    public byte[]? Waveform { get; init; }
    public string WaveformLabel { get; init; } = "Wavetable : ";
    /// <summary>このチャンネルを実機（NanoDrive など）へ出力しているか。</summary>
    public bool IsHardwareOutput { get; init; }
}

/// <summary>
/// 形式によらない表示用の1コマ。
/// サウンドビューアとサウンドバーが共有する。
/// </summary>
public sealed record MusicPlaybackSnapshot
{
    public int TrackIndex { get; init; }
    public int TrackCount { get; init; } = 1;
    public bool IsPlaying { get; init; }
    public bool IsPaused { get; init; }
    public TimeSpan CurrentTime { get; init; }
    public TimeSpan TotalTime { get; init; }
    public TimeSpan LoopDuration { get; init; }
    public int LoopCount { get; init; }
    public string FmCoreName { get; init; } = "—";
    // 実機（NDSIF）へ出力しているボード。ND6 / ND7 / ND8 の短い名前を空白区切りで並べる。使っていなければ「—」。
    public string ExtDeviceName { get; init; } = "—";
    public string Title { get; init; } = "";
    public string GameTitle { get; init; } = "";
    public string Author { get; init; } = "";
    public string SystemName { get; init; } = "";
    public string FormatName { get; init; } = "";
    public string FileTypeDisplay { get; init; } = "";
    /// <summary>
    /// ファイル形式の表示名。
    /// 再生側が指定しなければ形式名から決める。
    /// </summary>
    public string ResolvedFileTypeDisplay => !string.IsNullOrWhiteSpace(FileTypeDisplay)
        ? FileTypeDisplay
        : FormatName switch
        {
            "VGM" => "VGM",
            "VGZ" => "VGZ",
            "S98" => "S98",
            "MDX" => "MDX (MXDRV 2.06)",
            "NSF" => "NSF (NES Sound Format)",
            "NSFE" => "NSFE (Extended NES)",
            "HES" => "HES (PC Engine Sound)",
            "SPC" => "SPC (Super NES Sound)",
            "GBS" => "GBS (Game Boy Sound)",
            "AY" => "AY (ZX Spectrum)",
            "WAV" => "WAV Audio",
            "MP3" => "MP3 Audio",
            "FLAC" => "FLAC Audio",
            "OGG" => "OGG Audio",
            _ => !string.IsNullOrWhiteSpace(FormatName) ? $"{FormatName} Audio" : "—"
        };
    public string AuxiliaryFileStatus { get; init; } = "";
    public string SoundSourceDisplay { get; init; } = "";
    /// <summary>
    /// 音源の表示名。
    /// 再生側が指定しなければ形式名、チャンネルの音源名、レジスタバンクの音源名の順に決める。
    /// </summary>
    public string ResolvedSoundSourceDisplay => !string.IsNullOrWhiteSpace(SoundSourceDisplay)
        ? SoundSourceDisplay
        : FormatName switch
        {
            "MDX" => "YM2151 (OPM) + PCM8",
            "NSF" or "NSFE" => "NES APU (2A03)",
            "HES" => "HuC6280 (PSG)",
            "SPC" => "SPC700 / S-DSP",
            "GBS" => "Game Boy DMG",
            "AY" => "AY-3-8910",
            "KSS" => "PSG + SCC + OPLL",
            "WAV" or "MP3" or "FLAC" or "OGG" or "M4A" => "PCM (Stream)",
            _ => Channels.Any(ch => !string.IsNullOrWhiteSpace(ch.ChipName) && ch.ChipName != "MIX")
                ? string.Join(" + ", Channels.Select(ch => ch.ChipName).Where(c => !string.IsNullOrWhiteSpace(c) && c != "MIX").Distinct())
                : RegisterBanks.Length > 0
                    ? string.Join(" + ", RegisterBanks.Select(b => b.ChipName).Where(c => !string.IsNullOrWhiteSpace(c)).Distinct())
                    : !string.IsNullOrWhiteSpace(FormatName) ? FormatName : "—"
        };
    public MusicChannelSnapshot[] Channels { get; init; } = [];
    public MusicRegisterBank[] RegisterBanks { get; init; } = [];
    public float[] SpectrumLevels { get; init; } = new float[AudioSpectrumAnalyzer.BandCount];
    public string SpectrumKind { get; init; } = "CHANNEL ACTIVITY";
    public bool UseSpectrumOnly { get; init; }
}
