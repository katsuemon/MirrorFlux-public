namespace MirrorFlux.Services.Music;

/// <summary>
/// 半音より細かい音程（MIDIノート番号の小数、60.0 = C4）の計算。
/// 鍵盤は半音単位で光らせ、この値はディチューン・ポルタメント・ソフトLFOなどの細かい位置の表示に使う。
/// </summary>
public static class MusicPitch
{
    /// <summary>音の周波数（Hz）から求める。</summary>
    public static double? FromFrequency(double? hz) => hz is > 0 and < 1e6 ? 69 + 12 * Math.Log2(hz.Value / 440) : null;

    /// <summary>
    /// PCMの再生レート比から求める。
    /// レート1倍をC4とみなす目安。
    /// </summary>
    public static double? FromRatio(double ratio) => ratio > 0 && double.IsFinite(ratio) ? 60 + 12 * Math.Log2(ratio) : null;

    /// <summary>
    /// YM2151のキーコード（KC）とキーフラクション（KF、1/64半音）から求める。
    /// クロックが3.579545MHzと違えばその分ずれる。
    /// </summary>
    public static double FromOpm(int keyCode, int keyFraction, double clock)
    {
        // KCの音名コードは C# D D# - E F F# - G G# A - A# B C - の順で、4・8・12・15は前の音と同じ。
        ReadOnlySpan<int> notes = [1, 2, 3, 3, 4, 5, 6, 6, 7, 8, 9, 9, 10, 11, 12, 12];
        double note = ((keyCode >> 4) & 7) * 12 + 12 + notes[keyCode & 15] + ((keyFraction >> 2) & 63) / 64.0;
        return note + 12 * Math.Log2(clock / 3579545.0);
    }

    /// <summary>
    /// ymfmのOPM（YM2151）が出力する10.17固定小数点のチャンネル基本位相増分（LFO PM込み）から連続ピッチを求める。
    /// </summary>
    public static double? FromOpmPhaseStep(uint phaseStep, double clock = 3579545.0) =>
        phaseStep > 0 && clock > 0
            ? 69.0 + 12.0 * Math.Log2(phaseStep / (65984.0 * 16.0) * (clock / 3579545.0))
            : null;

    /// <summary>
    /// ymfmのOPN/OPL/OPLLが出力する10.17固定小数点のチャンネル基本位相増分（LFO PM込み）とFM分周値から連続ピッチを求める。
    /// </summary>
    public static double? FromFmPhaseStep(uint phaseStep, double clock, uint fmDivider) =>
        phaseStep > 0 && clock > 0 && fmDivider > 0
            ? FromFrequency((clock / fmDivider) * (phaseStep / 134217728.0))
            : null;

    /// <summary>連続ピッチと基準ノート番号からセント偏差（-99〜+99）を求める。</summary>
    public static int Cents(double? pitch, int? referenceNote = null)
    {
        if (pitch is not double p || !double.IsFinite(p)) return 0;
        double baseNote = referenceNote is int n && Math.Abs(p - n) < 0.995 ? n : Math.Round(p);
        return Math.Clamp((int)Math.Round((p - baseNote) * 100.0), -99, 99);
    }

    /// <summary>セント偏差を4文字固定幅（例: " +0¢", "+23¢", " -8¢"）に整形する。</summary>
    public static string FormatCents(int cents)
    {
        int clamped = Math.Clamp(cents, -99, 99);
        string signed = $"{(clamped >= 0 ? "+" : "-")}{Math.Abs(clamped)}";
        return $"{signed,3}¢";
    }

    /// <summary>
    /// 音程をオクターブ単位で動かし、光らせる鍵keyとの差を±6半音以内にする。
    /// 表記上のオクターブがずれている形式で使う。
    /// </summary>
    public static double AlignOctave(double pitch, int key) => pitch - 12 * Math.Round((pitch - key) / 12);

    /// <summary>
    /// 鍵盤に線を描く音程。
    /// チャンネルが明示した値をそのまま使う（ポルタメントで光る鍵から離れても追う）。
    /// なければFM・PSG・波形メモリ音源の音の周波数から求め、光らせる鍵（NoteNumber）と半音以上ずれる値は使わない。
    /// 音声からの推定音程には線を出さない。
    /// </summary>
    public static double? Display(MusicChannelSnapshot channel)
    {
        if (channel.Pitch is double explicitPitch) return double.IsFinite(explicitPitch) ? explicitPitch : null;
        if (channel.IsNoteEstimated || channel.Kind is not (MusicChannelKind.Fm or MusicChannelKind.Psg or MusicChannelKind.Wavetable))
            return null;
        if (FromFrequency(channel.Frequency) is not double value) return null;
        if (channel.NoteNumber is int note && Math.Abs(value - note) > .5 + 1e-9) return null;
        return value;
    }
}
