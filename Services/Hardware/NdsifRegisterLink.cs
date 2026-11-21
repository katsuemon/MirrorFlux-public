using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Text;
using MirrorFlux.Services.Vgm;

namespace MirrorFlux.Services.Hardware;

/// <summary>
/// NanoDrive 7 / 8 へレジスタ書き込みを送って演奏する接続。
/// 基板ごとに自分のポートと演奏用の時計を持つ。
/// ND8 の OKIM6258 ストリームは NdsifRegisterLink.Audio.cs 側で扱う。
/// </summary>
internal sealed partial class NdsifRegisterLink : IDisposable
{
    /// <summary>基板が報告する1音源分の対応情報（書き込みID・インスタンス・ページ・機能ビット・クロックの扱い・固定クロック）。</summary>
    internal readonly record struct Capability(byte WriteId, byte Instance, byte Page, byte Features, byte ClockMode, uint FixedClock);
    /// <summary>
    /// 送信待ちのレジスタ書き込み1件。
    /// Sample は 44100Hz 基準の発音時刻。
    /// </summary>
    internal readonly record struct RegisterWrite(long Sample, byte WriteId, byte Instance, byte Page, byte Address, byte Value);

    private readonly INdsifPort _port;
    private readonly object _ioSync = new();
    private int _disposing;
    private bool _shutdownRequired;
    private readonly List<Capability> _capabilities = [];
    private readonly ConcurrentQueue<RegisterWrite> _queue = new();
    private readonly AutoResetEvent _signal = new(false);
    private readonly Stopwatch _clock = new();
    private static readonly object DetectLock = new();
    private static readonly Dictionary<int, (string Signature, DateTime Time)> FailedDetect = [];
    private ushort _controlFeatures;
    private uint _audioDataCapacity;
    private ushort _audioEventCapacity;
    private volatile uint _audioPlayed;
    private volatile string _audioStage = "idle";
    private Func<long, INd8AudioRenderer>? _audioFactory;
    private (byte WriteId, byte Instance, uint Clock)[] _configuredClocks = [];
    private readonly ManualResetEventSlim _audioStopped = new(false);
    private Thread? _worker;
    private volatile bool _running, _paused, _connected;
    private ushort _transaction = 1;

    internal string Name { get; }
    internal string PortName => _port.PortName;
    internal bool IsConnected => _connected;
    internal bool IsAudioActive => _connected && _audioFactory != null;
    internal uint AudioPlayed => _audioPlayed;
    internal string AudioStage => _audioStage;
    internal string AudioDiagnostics => _audioDiagnostics;

    /// <summary>開いたポートと機種名から接続を作る。</summary>
    private NdsifRegisterLink(SerialPort port, string name) : this(new SerialNdsifPort(port), name) { }
    internal NdsifRegisterLink(INdsifPort port, string name) { _port = port; Name = name; _connected = true; }

    /// <summary>
    /// 指定番号の NanoDrive を探して開く。
    /// 見つからなければ null を返す。
    /// 直前に失敗したときと同じポート構成なら、一定時間は探し直さない。
    /// </summary>
    internal static NdsifRegisterLink? TryOpen(int number, ISet<string> excludedPorts)
    {
        // 直前の失敗と同じポート構成なら、再探査の間隔が空くまで探さない。
        if (!OperatingSystem.IsWindows()) return null;
        string target = $"NanoDrive {number}";
        string[] ports;
        try { ports = SerialPort.GetPortNames(); } catch { return null; }
        string signature = string.Join("|", ports.OrderBy(p => p, StringComparer.OrdinalIgnoreCase)) +
            ":" + string.Join("|", excludedPorts.OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
        lock (DetectLock)
            if (FailedDetect.TryGetValue(number, out var last) && last.Signature == signature &&
                DateTime.UtcNow - last.Time < NdsifService.DetectRetryInterval) return null;
        string[] usbPorts = NdsifPortCatalog.UsbPorts();
        // 1周目は覚えている機種に従って探す。対象機種と覚えていたポートが違っていたら、記録を捨てて全ポートを探し直す。
        for (int pass = 0; pass < 2; pass++)
        {
            string[] order = NdsifPortCatalog.Plan(target, ports, usbPorts, excludedPorts, out int knownTargets);
            foreach (string portName in order)
            {
                bool isUsb = usbPorts.Contains(portName, StringComparer.OrdinalIgnoreCase);
                SerialPort? port = null;
                NdsifRegisterLink? probe = null;
                bool transferred = false, identified = false;
                try
                {
                    // ポートを開いて機種名を問い合わせ、結果を記録する。
                    port = NdsifPortCatalog.Open(portName, 50);
                    probe = new NdsifRegisterLink(port, target);
                    byte[] info = probe.Request(0x02, []);
                    string? name = info.Length >= 1 && info.Length >= info[0] + 1 ? Encoding.UTF8.GetString(info, 1, info[0]) : null;
                    NdsifPortCatalog.Remember(ports, portName, name, isUsb);
                    identified = name != null;
                    if (!NdsifPortCatalog.IsModel(name, target)) continue;
                    // 対象機種なら対応情報を読み、この接続を返す。
                    probe.ReadCapabilities();
                    transferred = true;
                    lock (DetectLock) FailedDetect.Remove(number);
                    return probe;
                }
                catch (Exception ex)
                {
                    // 開けない・応答しないポート。機種が分かった後の失敗では、分かった機種の記録を残す。
                    if (!identified) NdsifPortCatalog.Remember(ports, portName, null, isUsb);
                    Debug.WriteLine($"[NDSIF] {portName}: {ex.Message}");
                }
                finally
                {
                    // 返さなかったポートは閉じる。
                    if (!transferred)
                    {
                        try { if (probe != null) { probe._connected = false; probe.Dispose(); } else port?.Dispose(); }
                        catch { }
                    }
                }
            }
            if (knownTargets == 0) break;
            NdsifPortCatalog.Reset();
        }
        // 見つからなかったポート構成を覚えておく。
        lock (DetectLock) FailedDetect[number] = (signature, DateTime.UtcNow);
        return null;
    }

    /// <summary>
    /// 応答を求めるコマンドを送り、同じトランザクションIDの応答を最大1秒待つ。
    /// 拒否ならIOExceptionを投げる。
    /// </summary>
    private byte[] Request(byte opcode, ReadOnlySpan<byte> payload, bool shutdown = false)
    {
        lock (_ioSync) return RequestLocked(opcode, payload, shutdown);
    }

    private byte[] RequestLocked(byte opcode, ReadOnlySpan<byte> payload, bool shutdown)
    {
        if (!shutdown && Volatile.Read(ref _disposing) != 0) throw new OperationCanceledException();
        // コマンドを枠に包んで送る。
        ushort id = _transaction++;
        byte[] packet = NdsifService.BuildFramedPacket(1, opcode, id, payload);
        _port.Write(packet, 0, packet.Length);
        // 区切りの0までを1枠として集め、対応する応答が来るまで読み続ける。
        var frame = new List<byte>(270);
        long until = Environment.TickCount64 + 1000;
        while (Environment.TickCount64 < until)
        {
            if (!shutdown && Volatile.Read(ref _disposing) != 0) throw new OperationCanceledException();
            int next;
            try { next = _port.ReadByte(); } catch (TimeoutException) { continue; }
            if (next < 0) continue;
            if (next != 0) { if (frame.Count < 269) frame.Add((byte)next); else frame.Clear(); continue; }
            if (frame.Count == 0) continue;
            bool valid = NdsifService.TryParseFrame(frame.ToArray(), out var reply);
            frame.Clear();
            if (!valid || reply.MessageType != 2 || reply.CommandId != opcode || reply.TransactionId != id) continue;
            // 先頭の状態バイトが0以外なら拒否として扱う。
            if (reply.Payload.Length == 0 || reply.Payload[0] != 0)
                throw new IOException($"NDSIF 0x{opcode:X2} rejected");
            return reply.Payload[1..];
        }
        throw new TimeoutException($"NDSIF 0x{opcode:X2} timeout");
    }

    /// <summary>対応情報（0x05）をページ単位で読み、音源ごとの対応とストリームの容量を集める。</summary>
    private void ReadCapabilities()
    {
        ushort offset = 0;
        var seen = new HashSet<ushort>();
        // 次ページの位置をたどり、同じ位置に戻ったら壊れた応答として止める。
        do
        {
            if (!seen.Add(offset)) throw new InvalidDataException("Cyclic NDSIF capabilities");
            byte[] request = new byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(request, offset);
            byte[] page = Request(0x05, request);
            var entries = ParseCapabilityPage(page, out ushort next, out ushort controls);
            uint dataCapacity = BinaryPrimitives.ReadUInt32LittleEndian(page.AsSpan(2));
            ushort eventCapacity = BinaryPrimitives.ReadUInt16LittleEndian(page.AsSpan(6));
            // 制御機能と容量は最初のページで覚え、以降のページで変わったら異常とする。
            if (seen.Count == 1)
            {
                _controlFeatures = controls; _audioDataCapacity = dataCapacity; _audioEventCapacity = eventCapacity;
            }
            else if (_controlFeatures != controls) throw new InvalidDataException("NDSIF control capabilities changed");
            else if (_audioDataCapacity != dataCapacity || _audioEventCapacity != eventCapacity)
                throw new InvalidDataException("NDSIF stream capabilities changed");
            // 同じ音源の重複がないか確かめて登録する。
            foreach (var entry in entries)
            {
                if (_capabilities.Any(c => c.WriteId == entry.WriteId && c.Instance == entry.Instance && c.Page == entry.Page))
                    throw new InvalidDataException("Duplicate NDSIF capability");
                _capabilities.Add(entry);
            }
            offset = next;
        } while (offset != 0xffff);
    }

    /// <summary>
    /// 対応情報の1ページを解析する。
    /// 版数256、制御機能、次ページ位置、件数に続けて9バイトずつの項目が並ぶ。
    /// </summary>
    internal static Capability[] ParseCapabilityPage(ReadOnlySpan<byte> page, out ushort next, out ushort controls)
    {
        next = controls = 0;
        if (page.Length < 13 || page.Length != 13 + page[12] * 9 ||
            BinaryPrimitives.ReadUInt16LittleEndian(page) != 256)
            throw new InvalidDataException("Invalid NDSIF capabilities");
        controls = BinaryPrimitives.ReadUInt16LittleEndian(page[8..]);
        next = BinaryPrimitives.ReadUInt16LittleEndian(page[10..]);
        var entries = new Capability[page[12]];
        // 各項目のクロックの扱い（0=なし、1=固定、2=可変）と機能ビットの組み合わせが矛盾しないか確かめる。
        for (int i = 0, p = 13; i < entries.Length; i++, p += 9)
        {
            var entry = new Capability(page[p], page[p + 1], page[p + 2], page[p + 3], page[p + 4],
                BinaryPrimitives.ReadUInt32LittleEndian(page[(p + 5)..]));
            if (entry.ClockMode > 2 || entry.ClockMode == 1 && (entry.FixedClock == 0 || (entry.Features & 4) != 0) ||
                entry.ClockMode != 1 && entry.FixedClock != 0 || entry.ClockMode == 2 && (entry.Features & 4) == 0)
                throw new InvalidDataException("Invalid NDSIF clock capability");
            entries[i] = entry;
        }
        return entries;
    }

    /// <summary>
    /// 指定の音源とクロックで書き込めるか判定する。
    /// YM2413（0x51）は 3579540Hz と 3579545Hz を同じクロックとして扱う。
    /// </summary>
    internal bool Supports(byte writeId, byte instance, byte page, uint clock)
    {
        var entry = _capabilities.FirstOrDefault(c => c.WriteId == writeId && c.Instance == instance && c.Page == page);
        if (entry.WriteId != writeId || (entry.Features & 1) == 0) return false;
        return entry.ClockMode switch
        {
            1 => entry.FixedClock == clock || writeId == 0x51 &&
                ((entry.FixedClock == 3579540 && clock == 3579545) || (entry.FixedClock == 3579545 && clock == 3579540)),
            2 => (entry.Features & 4) != 0,
            _ => false
        };
    }

    /// <summary>
    /// ND8 の OKIM6258（0xb7）ストリームを指定クロックで使えるか判定する。
    /// データ容量 875 バイト以上、イベント容量1件以上を求める。
    /// </summary>
    internal bool SupportsAudio(uint clock)
    {
        var entry = _capabilities.FirstOrDefault(c => c.WriteId == 0xb7 && c.Instance == 0 && c.Page == 0);
        return entry.WriteId == 0xb7 && (entry.Features & 8) != 0 &&
            _audioDataCapacity >= 875 && _audioEventCapacity >= 1 &&
            (entry.ClockMode == 1 && entry.FixedClock == clock ||
             entry.ClockMode == 2 && (entry.Features & 4) != 0);
    }

    /// <summary>OKIM6258 ストリーム用の作り手を登録し、この接続をストリーム演奏にする。</summary>
    internal void EnableAudio(Func<long, INd8AudioRenderer> rendererFactory)
    {
        if (!_connected) throw new InvalidOperationException("NDSIF link is disconnected");
        _audioFactory = rendererFactory;
    }

    /// <summary>
    /// 基板をリセットし、可変クロックの音源に曲のクロックを設定する。
    /// 失敗したら切断扱いにする。
    /// </summary>
    internal bool Configure(IEnumerable<(byte WriteId, byte Instance, uint Clock)> clocks)
    {
        try
        {
            _shutdownRequired = true;
            var requested = clocks.Distinct().ToArray();
            // リセットし、対応していれば出力の消音を解除する。
            Request(0x00, []);
            if ((_controlFeatures & 1) != 0) Request(0x03, [0]);
            // 固定クロックの音源は設定せず、可変クロックの音源だけクロックを送る。
            foreach (var (id, instance, clock) in requested)
            {
                var cap = _capabilities.First(c => c.WriteId == id && c.Instance == instance && c.Page == 0);
                if (cap.ClockMode == 1) continue;
                byte[] payload = new byte[6]; payload[0] = id; payload[1] = instance;
                BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(2), clock);
                Request(0x04, payload);
            }
            _configuredClocks = requested;
            return true;
        }
        catch (Exception ex) { Debug.WriteLine($"[NDSIF] configure: {ex.Message}"); _connected = false; return false; }
    }

    /// <summary>レジスタ書き込みを送信待ちに積み、送信スレッドを起こす。</summary>
    internal void Queue(RegisterWrite write)
    {
        if (!_connected || Volatile.Read(ref _disposing) != 0) return;
        _queue.Enqueue(write); _signal.Set();
    }

    /// <summary>
    /// 送信スレッドを開始する。
    /// ストリーム演奏では、先読みを済ませてから AUDIO_START を送る。
    /// </summary>
    internal void Start()
    {
        if (!_connected || _running || Volatile.Read(ref _disposing) != 0) return;
        if (_audioFactory != null && !_audioPrepared) return;
        if (_audioFactory != null) StartAudioStream();
        else _clock.Restart();
        if (!_connected) return;
        _running = true;
        _worker = new Thread(_audioFactory == null ? Worker : WorkerAudio)
        { IsBackground = true, Name = $"{Name} NDSIF", Priority = ThreadPriority.AboveNormal };
        _worker.Start();
    }

    /// <summary>
    /// 一時停止と再開。
    /// ストリーム演奏では停止時に基板の FIFO を止め、再開時に指定位置から先読みし直す。
    /// </summary>
    internal void Pause(bool paused, long sample = 0)
    {
        if (!_running || _paused == paused) return;
        // ストリーム演奏の一時停止は送信スレッドのリセット完了を待ち、再開は先読みからやり直す。
        if (_audioFactory != null)
        {
            if (paused)
            {
                _paused = true; _clock.Stop(); _signal.Set();
                if (!_audioStopped.Wait(1200)) { _connected = false; _audioStage = "pause reset timeout"; }
            }
            else
            {
                if (!_connected || !PrepareAudio(sample)) return;
                StartAudioStream();
                if (!_connected) return;
                _paused = false; _audioStopped.Reset(); _signal.Set();
            }
            return;
        }
        // レジスタ演奏は時計を止めるだけでよい。
        _paused = paused;
        if (paused) _clock.Stop(); else _clock.Start();
        _signal.Set();
    }

    /// <summary>
    /// レジスタ演奏の送信スレッド。
    /// 発音時刻が来た書き込みを、同じ音源ごとにまとめて送る。
    /// </summary>
    private void Worker()
    {
        try
        {
            while (_running && _connected)
            {
                // 停止中や送るものがないときは待つ。
                if (_paused || !_queue.TryPeek(out var write)) { _signal.WaitOne(2); continue; }
                // 先頭の書き込みの時刻がまだなら待つ。
                long now = _clock.ElapsedTicks * 44100 / Stopwatch.Frequency;
                if (write.Sample > now) { _signal.WaitOne(1); continue; }
                if (!_queue.TryDequeue(out write)) continue;
                // 同じ音源・ページで時刻が来ている書き込みを1パケットにまとめる。
                byte[] payload = new byte[256]; payload[0] = write.Instance; payload[1] = write.Page;
                payload[2] = write.Address; payload[3] = write.Value;
                int length = 4;
                while (length <= 254 && _queue.TryPeek(out var next) && next.Sample <= now &&
                    next.WriteId == write.WriteId && next.Instance == write.Instance && next.Page == write.Page)
                {
                    if (!_queue.TryDequeue(out next)) break;
                    payload[length++] = next.Address; payload[length++] = next.Value;
                }
                byte[] packet = NdsifService.BuildFramedPacket(0, write.WriteId, 0, payload.AsSpan(0, length));
                WritePacket(packet);
            }
        }
        catch (Exception ex) { _connected = false; Debug.WriteLine($"[NDSIF] {Name} disconnected: {ex.Message}"); }
    }

    private void WritePacket(byte[] packet)
    {
        lock (_ioSync)
        {
            if (Volatile.Read(ref _disposing) != 0) throw new OperationCanceledException();
            _port.Write(packet, 0, packet.Length);
        }
    }

    /// <summary>送信スレッドを止め、基板をリセットしてからポートを閉じる。</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposing, 1) != 0) return;
        _running = false; _signal.Set();
        // Request は読み取りタイムアウトごとに中止を確認する。通常の送信とは同時にリセットしない。
        bool joined = _worker == null || Thread.CurrentThread == _worker || _worker.Join(1500);
        try
        {
            // 通信エラーで IsConnected が落ちても、演奏済みの基板には停止を試みる。
            // 機種の探査だけで開いたポートへはリセットを送らない。
            if (_shutdownRequired && _port.IsOpen)
            {
                try { Request(0x00, [], shutdown: true); }
                catch (Exception ex) { Debug.WriteLine($"[NDSIF] reset: {ex.Message}"); }
                if ((_controlFeatures & 1) != 0)
                    try { Request(0x03, [1], shutdown: true); }
                    catch (Exception ex) { Debug.WriteLine($"[NDSIF] mute: {ex.Message}"); }
            }
        }
        finally
        {
            _connected = false;
            _port.Dispose();
            if (!joined && _worker != null) joined = _worker.Join(1500);
            if (joined) { _signal.Dispose(); _audioStopped.Dispose(); }
        }
    }
}
