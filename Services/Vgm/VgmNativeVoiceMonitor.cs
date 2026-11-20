using MirrorFlux.Services.Music;
namespace MirrorFlux.Services.Vgm;

/// <summary>音源チップのネイティブ実装が返す発音情報を、鍵盤・レベルメーター用のチャンネル情報に変換する。</summary>
internal sealed class VgmNativeVoiceMonitor
{
    private readonly VgmChip.Voice[] _voiceBuffer = new VgmChip.Voice[48];
    private MusicChannelSnapshot[] _nativeChannels = [];
    private readonly KeyboardActivity _nativeKeyboard = new();
    private readonly HashSet<string> _usedNativeChannels = [];
    internal MusicChannelSnapshot[] Channels => _nativeKeyboard.Apply(_nativeChannels);
    private readonly double[] _pitchWindow = new double[256];
    private readonly double[] _pwmPitchWindow = new double[512];
    private readonly Dictionary<string, int?> _sampleNotes = [];
    private int _pitchFrames;
    /// <summary>全音源チップの発音情報を集めて、表示用のチャンネル一覧を作り直す。</summary>
    internal void Capture(Dictionary<(int Type, int Instance), VgmChip> chips, bool isPlaying, int frames = 0)
    {
        // 音高解析は882フレーム（約20ms）ごとに行う。
        _pitchFrames += frames;
        bool analyzePitch = _pitchFrames >= 882;
        if (analyzePitch) _pitchFrames %= 882;
        // 各音源チップから発音情報を集める。
        var all = new List<VgmChip.Voice>();
        foreach (var entry in chips)
        {
            int n = entry.Value.Voices(entry.Key.Instance, _voiceBuffer);
            for (int j = 0; j < n; j++) all.Add(_voiceBuffer[j]);
        }
        int count = all.Count;
        if (count == 0)
        {
            if (_nativeChannels.Length != 0) _nativeChannels = [];
            return;
        }
        // 発音情報ごとに音源チップ名とIDを決め、使用済みとキーオンを判定する。
        var channels = new MusicChannelSnapshot[count];
        for (int i = 0; i < count; i++)
        {
            var voice = all[i];
            string chip = voice.Type switch { 0x11 => "PWM", 0x22 => "VSU", 0x2c => (voice.Flags & 16) != 0 ? "MSM6585" : "MSM5205", 0x25 => (voice.Flags & 16) != 0 ? "ES5506" : "ES5505", 0x0e => "YMF271", 0x27 => "C352", 0x1f => "QSound", 0x18 => "OKIM6295", 0x17 => "OKIM6258", _ => "YMZ280B" };
            string name = chip + (voice.Instance > 0 ? $" #{voice.Instance + 1}" : "");
            string id = $"{name}:{voice.Channel}";
            if ((voice.Flags & 9) != 0) _usedNativeChannels.Add(id);
            bool active = isPlaying && (voice.Flags & 1) != 0;
            bool estimated = false;
            int? note = voice.Note;
            // YMZ280B（0x0F）のADPCM音は、波形から音高を推定する。
            if (voice.Type == 0x0f && (voice.Flags & 2) != 0)
            {
                if (!active || (voice.Flags & 64) == 0) _sampleNotes.Remove(id);
                else if (analyzePitch || !_sampleNotes.ContainsKey(id))
                {
                    double rate = chips[((int)voice.Type, (int)voice.Instance)].Wave((int)voice.Channel, _pitchWindow);
                    if (rate > 0)
                    {
                        double mean = _pitchWindow.Average(), energy = 0;
                        for (int n = 0; n < _pitchWindow.Length; n++)
                        {
                            _pitchWindow[n] -= mean; energy += _pitchWindow[n] * _pitchWindow[n];
                        }
                        _sampleNotes[id] = energy > .000256 ? AudioPitchDetector.Estimate(_pitchWindow, rate, .77, Math.Max(4, (int)(rate / 1600)),
                            Math.Min(_pitchWindow.Length / 2, (int)(rate / 55))) : null;
                    }
                }
                if (_sampleNotes.TryGetValue(id, out var sampleNote)) { estimated = true; note = sampleNote; }
            }
            // PWM（0x11）は音高情報を持たないため、波形から音高を推定する。
            if (voice.Type == 0x11)
            {
                if (!active) _sampleNotes.Remove(id);
                else if (analyzePitch || !_sampleNotes.ContainsKey(id))
                {
                    double rate = chips[((int)voice.Type, (int)voice.Instance)].Wave((int)voice.Channel, _pwmPitchWindow);
                    if (rate > 0)
                    {
                        double mean = _pwmPitchWindow.Average(), energy = 0;
                        for (int n = 0; n < _pwmPitchWindow.Length; n++)
                        {
                            _pwmPitchWindow[n] -= mean;
                            energy += _pwmPitchWindow[n] * _pwmPitchWindow[n];
                        }
                        _sampleNotes[id] = energy > .000256 ? AudioPitchDetector.Estimate(_pwmPitchWindow, rate, .77,
                            Math.Max(4, (int)(rate / 1600)), Math.Min(_pwmPitchWindow.Length - 1, (int)(rate / 55))) : null;
                    }
                }
                if (_sampleNotes.TryGetValue(id, out var sampleNote) && sampleNote is int pitch)
                { estimated = true; note = pitch; }
            }
            // 種類を決めて表示用のチャンネル情報を作る。
            var kind = (voice.Flags & 32) != 0 ? MusicChannelKind.Fm : (voice.Flags & 4) != 0 ? MusicChannelKind.Noise :
                (voice.Flags & 2) != 0 ? MusicChannelKind.Adpcm : MusicChannelKind.Pcm;
            channels[i] = new()
            {
                Id = id,
                ChipName = name,
                Label = $"{kind.ToString().ToUpperInvariant()}{voice.Channel + 1:00}",
                Kind = kind,
                IsInUse = _usedNativeChannels.Contains(id),
                IsKeyOn = active,
                NoteNumber = note,
                IsNoteEstimated = estimated,
                Frequency = voice.Frequency,
                Level = isPlaying && (active || voice.Type == 0x0e) ? Math.Clamp(voice.Level, 0, 1) : 0,
                Pan = (voice.Left > 0 ? 2 : 0) | (voice.Right > 0 ? 1 : 0),
                PanLeftLevel = voice.Left,
                PanRightLevel = voice.Right,
                DataString = $"RATE:{voice.Frequency:F0}Hz"
            };
        }
        // 作った一覧を保存し、鍵盤の表示履歴に記録する。
        _nativeChannels = channels;
        _nativeKeyboard.Record(channels);
    }
}
