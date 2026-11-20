using MirrorFlux.Models;
using MirrorFlux.Services.Hardware;
using MirrorFlux.Services.Vgm;

namespace MirrorFlux.Services.Music;

/// <summary>
/// MDX を NanoDrive 8 で鳴らすときの接続。
/// `%ND8-NDSIF` が有効で基板が見つかったときだけ作る。
/// OKIM6258 のストリームに対応していれば YM2151 と PCM8 をまとめて <see cref="MdxNd8Renderer"/> で送り、対応していなければ YM2151 だけを書き込みごとに送る（PCM8 はPCで鳴らす）。
/// </summary>
internal sealed class MdxHardwareSession : IDisposable
{
    private const uint OpmClock = 4_000_000;
    private readonly NdsifRegisterLink _link;
    private bool _started;

    /// <summary>
    /// PCM8 も基板で鳴らしているか。
    /// false なら YM2151 だけ。
    /// </summary>
    internal bool SendsPcm { get; }
    internal bool IsActive => _link.IsConnected;
    internal string BoardName => _link.Name;

    private MdxHardwareSession(NdsifRegisterLink link, bool sendsPcm) { _link = link; SendsPcm = sendsPcm; }

    /// <summary>
    /// 設定が有効なら NanoDrive 8 を探して開く。
    /// YM2151 を 4MHz で鳴らせない基板では使わない。
    /// </summary>
    internal static MdxHardwareSession? TryOpen(MdxFile file, AppSettings settings)
    {
        if (!settings.Nd8Ndsif) return null;
        var link = NdsifRegisterLink.TryOpen(8, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        if (link == null) return null;
        try
        {
            // YM2151 と書き込みID 0x54 が 4MHz で使えるか確かめ、OKIM6258 ストリームが使えるか調べる。
            byte opm = NdsifRegisterRouting.ClockId(3);
            if (!link.Supports(opm, 0, 0, OpmClock) || !link.Supports(0x54, 0, 0, OpmClock)) return null;
            bool audio = link.SupportsAudio(MdxNd8Renderer.OkiClock);
            // 使う音源のクロックを設定する。
            var clocks = new List<(byte, byte, uint)> { (opm, 0, OpmClock) };
            if (audio) clocks.Add((0xb7, 0, MdxNd8Renderer.OkiClock));
            if (!link.Configure(clocks)) return null;
            // ストリームが使えるなら、指定位置から作り直せる作り手を登録して先読みしておく。
            if (audio)
            {
                link.EnableAudio(sample =>
                {
                    var renderer = new MdxNd8Renderer(file.Data, file.PdxData);
                    renderer.SeekToSample(sample);
                    return renderer;
                });
                if (!link.PrepareAudio(0)) return null;
            }
            // うまく開けたら接続を渡し、途中で失敗したら閉じる。
            var session = new MdxHardwareSession(link, audio);
            link = null;
            return session;
        }
        finally { link?.Dispose(); }
    }

    /// <summary>YM2151 だけを送る接続で、再生側の書き込みを sample（44.1 kHz）の時刻で送る。</summary>
    internal void WriteOpm(int register, byte value, long sample)
    {
        if (SendsPcm || !IsActive) return;
        _link.Queue(new NdsifRegisterLink.RegisterWrite(sample, 0x54, 0, 0, (byte)register, value));
    }

    /// <summary>最初の音を作ったあとに1回だけ呼び、基板の時計を動かし始める。</summary>
    internal void StartOnce()
    {
        if (_started) return;
        _started = true;
        _link.Start();
    }

    internal void Pause(bool paused, long sample) => _link.Pause(paused, sample);

    public void Dispose() => _link.Dispose();
}
