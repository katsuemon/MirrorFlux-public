using MirrorFlux.Models;
using MirrorFlux.Services.Hardware;
namespace MirrorFlux.Services.Vgm;

/// <summary>NanoDrive 6・7・8への実機出力をまとめて管理する。接続、クロック設定、レジスタ書込みの送信、一時停止と開始の同期を受け持つ。</summary>
internal sealed class VgmHardwareSession : IDisposable
{
    internal bool IsNd6Active { get; private set; }
    private readonly Dictionary<(int Type, int Instance), NdsifRegisterLink> _registerRoutes = [];
    private readonly List<NdsifRegisterLink> _registerLinks = [];
    /// <summary>この音源チップを実機で鳴らしているか。ND6はSN76489と1個目のYM2612を受け持つ。</summary>
    internal bool IsHardwareChip(int type, int instance) => IsNd6Active && (type == 0 || type == 2 && instance == 0) ||
        _registerRoutes.TryGetValue((type, instance), out var link) && link.IsConnected;
    /// <summary>この音源チップを実機へ出力している基板の名前。ソフトで鳴らしているならnull。</summary>
    internal string? HardwareName(int type, int instance) =>
        IsNd6Active && (type == 0 || type == 2 && instance == 0) ? "NanoDrive6" :
        _registerRoutes.TryGetValue((type, instance), out var link) && link.IsConnected ? link.Name : null;
    // ND8のOKIM6258音声ストリームの再生量と状態。診断表示に使う。
    internal uint Nd8AudioPlayed => _registerLinks.FirstOrDefault(l => l.IsAudioActive)?.AudioPlayed ?? 0;
    internal string Nd8AudioStage => _registerLinks.FirstOrDefault(l => l.Name == "NanoDrive 8")?.AudioStage ?? "no link";
    internal string Nd8AudioDiagnostics => _registerLinks.FirstOrDefault(l => l.Name == "NanoDrive 8")?.AudioDiagnostics ?? "no link";
    private bool _clockPending;
    /// <summary>設定と曲の音源チップに合わせて、使えるNanoDriveとの接続を開く。</summary>
    internal VgmHardwareSession(VgmFile file, IndependentVgmProgram program, AppSettings appSettings)
    {
        // ND6: YM2612かSN76489があれば接続し、各音源チップのクロックを設定する。クロック値が0なら標準値を使う。
        if (appSettings.Nd6Ndsif && (file.Ym2612Clock != 0 || file.Sn76489Clock != 0) && NdsifService.Instance.EnsureConnected())
        {
            IsNd6Active = true;
            NdsifService.Instance.Reset();
            if (file.Ym2612Clock != 0)
            {
                uint ymClock = file.Ym2612Clock & 0x3FFFFFFF;
                if (ymClock == 0) ymClock = 7670453;
                NdsifService.Instance.SetChipClock(0x52, 0, ymClock);
            }
            if (file.Sn76489Clock != 0)
            {
                uint psgClock = file.Sn76489Clock & 0x3FFFFFFF;
                if (psgClock == 0) psgClock = 3579545;
                NdsifService.Instance.SetChipClock(0x50, 0, psgClock);

                // ND6ではCLK0をYM2612（0x52・0x53）とSN76489 #2（0x30）が共有し、SN76489 #1（0x50）はCLK1を使う。
                // YM2612を使うときは0x30のクロックを絶対に設定しない。CLK0が約3.58MHzで上書きされ、YM2612が半分の速さで動いて音程が大きく下がるため。
                bool hasDualPsg = (file.Sn76489Clock & 0x40000000) != 0;
                if (file.Ym2612Clock == 0 && hasDualPsg)
                {
                    uint secondPsgClock = file.SecondSn76489Clock != 0 ? file.SecondSn76489Clock : psgClock;
                    NdsifService.Instance.SetChipClock(0x30, 1, secondPsgClock);
                }
            }
            NdsifService.Instance.ResetClock(0);
            // 準備中に機器が応答しなくなった場合は実機出力を使わず、ソフト音源で再生する。
            IsNd6Active = NdsifService.Instance.IsConnected;
            _clockPending = IsNd6Active;
        }

        // ND7・ND8はレジスタ書込み方式の接続を開く。
        if (appSettings.Nd7Ndsif) OpenRegisterLink(7, file, program);
        if (appSettings.Nd8Ndsif) OpenRegisterLink(8, file, program);

    }
    /// <summary>
    /// ND7またはND8の接続を開き、送れる音源チップを決めてクロックを設定する。
    /// ND7はYM2413・YM2203・YM3812・YM3526・YMF262・AY8910、ND8はYM2151を受け持つ。
    /// ND8は1個目のOKIM6258があり、そのADPCMやDACストリームを使う曲では音声ストリームも送る。
    /// </summary>
    private void OpenRegisterLink(int number, VgmFile file, IndependentVgmProgram program)
    {
        // 対象の音源チップのうち、曲中で実際に書込みがあるものを候補にする。VRC7（YM2413のビット31）は除く。
        int[] types = number == 7 ? [1, 6, 9, 10, 12, 18] : [3];
        var candidates = program.Devices.Where(d => types.Contains(d.Type) &&
            !(d.Type == 1 && (d.Clock & 0x80000000) != 0) &&
            program.Commands.Any(c => c.Type == d.Type && c.Instance == d.Instance)).ToArray();
        bool wantsAudio = number == 8 && program.Devices.Any(d => d.Type == 23 && d.Instance == 0) &&
            program.Commands.Any(c => c.Code == 0xb7 && c.Type == 23 || c.Code is 0x90 or 0x93 or 0x95);
        if (candidates.Length == 0 && !wantsAudio) return;
        // ND6や他の接続で使っているポートを除いて、該当する基板を探す。
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (IsNd6Active && NdsifService.Instance.PortName is { } nd6Port) excluded.Add(nd6Port);
        foreach (var link in _registerLinks) if (link.PortName is { } port) excluded.Add(port);
        var connection = NdsifRegisterLink.TryOpen(number, excluded);
        if (connection == null) return;
        try
        {
            // 基板が受け付けるクロックで、全書込みを変換できる音源チップだけを採用する。
            var accepted = candidates.Where(device =>
            {
                uint clock = device.Clock & 0x3fffffff;
                byte id = NdsifRegisterRouting.ClockId(device.Type);
                return connection.Supports(id, (byte)device.Instance, 0, clock) &&
                    program.Commands.Where(c => c.Type == device.Type && c.Instance == device.Instance)
                        .All(c => NdsifRegisterRouting.TryMap(c, out var write) &&
                            connection.Supports(write.WriteId, write.Instance, write.Page, clock));
            }).ToArray();
            // ND8の音声ストリームは、OKIM6258が1個だけでクロックが4MHzか8MHzのときに使う。
            bool useAudio = wantsAudio && connection.SupportsAudio(file.OkiM6258Clock & 0x3fffffff) &&
                (file.OkiM6258Clock & 0xc0000000) == 0 &&
                (file.OkiM6258Clock & 0x3fffffff) is 4000000 or 8000000;
            // ND7では、クロックを共有する音源チップ同士のクロックがそろっている必要がある。
            if (accepted.Length == 0 && !useAudio || number == 7 && !NdsifRegisterRouting.Nd7ClocksCompatible(accepted)) return;
            // クロックを設定し、音声ストリームを使うならOKIM6258とYM2151の音を作る処理を登録する。
            var clocks = accepted.Select(d => (NdsifRegisterRouting.ClockId(d.Type), (byte)d.Instance, d.Clock & 0x3fffffff));
            if (useAudio) clocks = clocks.Append(((byte)0xb7, (byte)0, file.OkiM6258Clock & 0x3fffffff));
            if (!connection.Configure(clocks)) return;
            if (useAudio)
            {
                bool includeYm = accepted.Any(d => d.Type == 3);
                connection.EnableAudio(sample =>
                {
                    var renderer = new NdsifOkiRenderer(file, program, includeYm);
                    renderer.SeekToSample(sample);
                    return renderer;
                });
                if (!connection.PrepareAudio(0)) return;
            }
            // 接続を登録する。ここまで来たら破棄しない。
            _registerLinks.Add(connection);
            foreach (var d in accepted) _registerRoutes[(d.Type, d.Instance)] = connection;
            if (useAudio) _registerRoutes[(23, 0)] = connection;
            connection = null;
            _clockPending = true;
        }
        finally { connection?.Dispose(); }
    }
    /// <summary>実機へ送る音源チップ宛ての書込みを、再生位置付きで送信待ちに入れる。音声ストリームで鳴らしているときはYM2151の書込みを送らない。</summary>
    internal void Write(IndependentVgmProgram.Command command, long sample)
    {
        if (!_registerRoutes.TryGetValue((command.Type, command.Instance), out var link) ||
            link.IsAudioActive && command.Type == 3 ||
            !NdsifRegisterRouting.TryMap(command, out var write)) return;
        link.Queue(write with { Sample = sample });
    }
    /// <summary>実機出力の一時停止と再開をそろえて行う。</summary>
    internal void Pause(bool paused, long sample)
    {
        if (paused)
        {
            foreach (var link in _registerLinks) link.Pause(true, sample);
            if (IsNd6Active) NdsifService.Instance.PauseClock();
        }
        else
        {
            foreach (var link in _registerLinks) link.Pause(false, sample);
            if (IsNd6Active) NdsifService.Instance.ResumeClock();
        }
    }
    /// <summary>
    /// 最初の再生位置まで進んだ後に、実機出力を一斉に開始する。
    /// 音声ストリームの基板、ND6のクロック、残りの基板の順に始める。ND6を使うときは表示を2048フレーム進めてそろえる。
    /// </summary>
    internal void AfterAdvance(VgmRegisterPlaybackMonitor? monitor)
    {
        if (!_clockPending) return;
        _clockPending = false;
        if (IsNd6Active) monitor?.Advance(2048);
        foreach (var link in _registerLinks.Where(link => link.IsAudioActive)) link.Start();
        if (IsNd6Active) NdsifService.Instance.StartClock(0);
        foreach (var link in _registerLinks.Where(link => !link.IsAudioActive)) link.Start();
    }
    /// <summary>接続を閉じ、ND6のクロックと音源チップを初期状態に戻す。</summary>
    public void Dispose()
    {
        foreach (var link in _registerLinks)
            try { link.Dispose(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[NDSIF] stop: {ex.Message}"); }
        _registerLinks.Clear(); _registerRoutes.Clear();
        if (IsNd6Active) { NdsifService.Instance.ResetClock(0); NdsifService.Instance.Reset(); IsNd6Active = false; }
    }
}
