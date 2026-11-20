using System;

namespace MirrorFlux.Services.Vgm
{
    /// <summary>YM2151のFM音源1チャンネル分の、表示用のリアルタイム状態。</summary>
    public sealed class OpmChannelState
    {
        public int ChannelIndex { get; set; }
        public bool IsKeyOn { get; set; }
        /// <summary>各スロット（M1・C1・M2・C2）のキーオン状態。ビット0〜3が対応する。</summary>
        public int SlotMask { get; set; }
        /// <summary>MIDI相当の音高番号（0〜127）。</summary>
        public int NoteNumber { get; set; }
        /// <summary>オクターブ（0〜7）。</summary>
        public int Octave { get; set; }
        /// <summary>OPMのKCレジスタ値（0x00〜0x7F）。</summary>
        public int KeyCode { get; set; }
        /// <summary>OPMのKFレジスタ値（0x00〜0xFC）。</summary>
        public int KeyFraction { get; set; }
        /// <summary>表示用の音名（"C 4"、"F#5" など）。</summary>
        public string NoteName { get; set; } = "--";
        /// <summary>描画用のピークレベル（0.0〜1.0）。</summary>
        public double Level { get; set; }
        /// <summary>エンベロープとTLから求めた目標レベル。</summary>
        public double TargetLevel { get; set; }
        /// <summary>出力先。0=なし、1=右、2=左、3=中央。</summary>
        public int Pan { get; set; } = 3;
        /// <summary>アルゴリズム（0〜7）。</summary>
        public int Algorithm { get; set; }
        /// <summary>フィードバック（0〜7）。</summary>
        public int Feedback { get; set; }
        /// <summary>音量（0〜127）。</summary>
        public int Volume { get; set; }
        /// <summary>サウンドビューアの "DATA" 欄に出す文字列（例: "08-78"）。</summary>
        public string DataString { get; set; } = "";
    }

    /// <summary>PCM（OKIM6258・PCM8）1チャンネル分の、表示用のリアルタイム状態。</summary>
    public sealed class PcmChannelState
    {
        public int ChannelIndex { get; set; }
        public bool IsActive { get; set; }
        /// <summary>描画用のレベル（0.0〜1.0）。</summary>
        public double Level { get; set; }
        public double TargetLevel { get; set; }
        public int Pan { get; set; } = 3;
    }

    /// <summary>プレイヤー画面全体に表示する、再生中のリアルタイムなスナップショット。</summary>
    public sealed class VgmVisualSnapshot
    {
        public bool IsPlaying { get; set; }
        public bool IsPaused { get; set; }
        public TimeSpan CurrentTime { get; set; }
        public TimeSpan TotalTime { get; set; }
        public TimeSpan LoopDuration { get; set; }
        public int LoopCount { get; set; }
        public string FmCoreName { get; set; } = "—";
        public string ExtDeviceName { get; set; } = "—";
        public string Title { get; set; } = "";
        public string GameTitle { get; set; } = "";
        public string Author { get; set; } = "";
        public string SystemName { get; set; } = "X68000 YM2151+PCM8";
        public OpmChannelState[] OpmChannels { get; set; } = new OpmChannelState[8];
        public PcmChannelState[] PcmChannels { get; set; } = new PcmChannelState[8];
        public float[] SpectrumLevels { get; set; } = new float[Music.AudioSpectrumAnalyzer.BandCount];
        public string SpectrumKind { get; set; } = "CHANNEL ACTIVITY";
        public MirrorFlux.Services.Music.MusicChannelSnapshot[] MusicChannels { get; set; } = [];
        public MirrorFlux.Services.Music.MusicRegisterBank[] RegisterBanks { get; set; } = [];

        public VgmVisualSnapshot()
        {
            // OPMとPCMの8チャンネル分の状態を用意する。
            for (int i = 0; i < 8; i++)
            {
                OpmChannels[i] = new OpmChannelState { ChannelIndex = i };
                PcmChannels[i] = new PcmChannelState { ChannelIndex = i };
            }
        }
    }
}
