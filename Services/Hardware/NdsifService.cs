using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace MirrorFlux.Services.Hardware;

/// <summary>NDSIF で受け取った1枠分のメッセージ（種類・コマンド・トランザクションID・ペイロード）。</summary>
public readonly record struct NdsifMessage(
    byte MessageType,
    byte CommandId,
    ushort TransactionId,
    byte[] Payload);

/// <summary>
/// NanoDrive 6 との接続と送信を受け持つ。
/// ポートの探査、YM2612 / SN76489 の書き込み、Timed DAC Stream での時刻付き送信を行う。
/// 送信の本体は NdsifService.Transmission.cs にある。
/// </summary>
public sealed partial class NdsifService : IDisposable
{
    private static readonly Lazy<NdsifService> _instance = new(() => new NdsifService());
    public static NdsifService Instance => _instance.Value;

    /// <summary>
    /// Timed DAC Stream の1レコード（種類・値・待ち）。
    /// 種類は 0=DAC値、1=選択、2=待ち。
    /// </summary>
    public readonly record struct DacRecord(byte Kind, byte Value, ushort Wait)
    {
        public const byte KindDac = 0;
        public const byte KindSelect = 1;
        public const byte KindWait = 2;
    }

    /// <summary>
    /// 送信待ちのレジスタ書き込み1件。
    /// Cmd は 0x52/0x53（YM2612 port 0/1）、0x50/0x30（SN76489 の1台目/2台目）。
    /// Port は YM2612 のポート0/1 か SN76489 の番号、Reg は YM2612 のレジスタ（PSG では使わない）、Val は YM2612 の値か PSG のデータバイト。
    /// </summary>
    public readonly struct NdsifWrite
    {
        public readonly uint TargetSample;
        public readonly byte Cmd;
        public readonly byte Port;
        public readonly byte Reg;
        public readonly byte Val;

        public NdsifWrite(uint targetSample, byte cmd, byte port, byte reg, byte val)
        {
            TargetSample = targetSample;
            Cmd = cmd;
            Port = port;
            Reg = reg;
            Val = val;
        }
    }

    private readonly object _lock = new();
    private readonly ConcurrentQueue<NdsifWrite> _txQueue = new();
    private readonly AutoResetEvent _txSignal = new(false);
    private int _txSignaled;
    private Thread? _txThread;
    private volatile bool _txRunning;
    private readonly Stopwatch _playbackClock = new();
    private uint _baseSampleOffset;
    private uint _parsedSample;
    private volatile bool _sourceEnded;
    private volatile bool _clockRequested;
    private volatile bool _isPaused;
    private int _sessionGeneration;
    private bool _isCleanReset;
    private bool _timePeriodActive;
    private readonly byte[,] _ymRegs = new byte[2, 256];

    private uint _dacRecordCapacity = 8192;
    private ushort _dacEventCapacity = 512;
    private byte _dacMaxRecords = 60;

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod", SetLastError = true)]
    private static extern uint TimeBeginPeriod(uint uMilliseconds);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod", SetLastError = true)]
    private static extern uint TimeEndPeriod(uint uMilliseconds);

    private SerialPort? _serialPort;
    private Stream? _activeStream;
    private ushort _transactionCounter = 1;

    private volatile bool _isConnected;
    public bool IsConnected { get => _isConnected; private set => _isConnected = value; }

    // 機器が見つからなかったときの再探査の抑制。ポート構成が変わるか、この時間が過ぎるまで探査しない。
    internal static readonly TimeSpan DetectRetryInterval = TimeSpan.FromSeconds(30);
    private string? _failedDetectSignature;
    private DateTime _failedDetectUtc;

    /// <summary>前回失敗したのと同じポート構成で、まだ再探査の間隔が空いていなければ true を返す。</summary>
    internal static bool ShouldSkipDetect(string signature, string? failedSignature, DateTime failedUtc, DateTime nowUtc) =>
        failedSignature != null && failedSignature == signature && nowUtc >= failedUtc && nowUtc - failedUtc < DetectRetryInterval;

    /// <summary>
    /// 書き込みに失敗した接続を切断扱いにする。
    /// ポートは次の EnsureConnected が閉じて再検出する。
    /// 送信スレッドからも呼ばれるため、ここではスレッドを止めない。
    /// </summary>
    private void MarkLinkLost(string where, Exception ex)
    {
        System.Diagnostics.Debug.WriteLine($"[NDSIF] link lost ({where}): {ex.Message}");
        IsConnected = false;
    }
    public bool SupportsTimedDac { get; private set; }
    public string? DeviceName { get; private set; }
    public string? PortName { get; private set; }

    public static ushort ComputeCrc16(ReadOnlySpan<byte> data) => NdsifProtocol.ComputeCrc16(data);
    public static byte[] CobsEncode(ReadOnlySpan<byte> input) => NdsifProtocol.CobsEncode(input);
    public static bool TryCobsDecode(ReadOnlySpan<byte> input, Span<byte> output, out int bytesWritten) => NdsifProtocol.TryCobsDecode(input, output, out bytesWritten);
    public static byte[] BuildRawFrame(byte messageType, byte commandId, ushort transactionId, ReadOnlySpan<byte> payload) => NdsifProtocol.BuildRawFrame(messageType, commandId, transactionId, payload);
    public static byte[] BuildFramedPacket(byte messageType, byte commandId, ushort transactionId, ReadOnlySpan<byte> payload) => NdsifProtocol.BuildFramedPacket(messageType, commandId, transactionId, payload);
    public static byte[] BuildDacDataPayload(uint sequence, ulong position, ReadOnlySpan<DacRecord> records) => NdsifProtocol.BuildDacDataPayload(sequence, position, records);
    public static byte[] BuildRegisterEventPayload(ulong tick, byte targetId, byte instance, byte page, ReadOnlySpan<byte> registerData) => NdsifProtocol.BuildRegisterEventPayload(tick, targetId, instance, page, registerData);
    public static byte[] BuildDacStartPayload(uint minimumLeadTicks) => NdsifProtocol.BuildDacStartPayload(minimumLeadTicks);
    public static byte[] BuildDacEndPayload(uint nextSequence, ulong endTick) => NdsifProtocol.BuildDacEndPayload(nextSequence, endTick);
    public static bool TryParseFrame(ReadOnlySpan<byte> cobsSpan, out NdsifMessage message) => NdsifProtocol.TryParseFrame(cobsSpan, out message);

    /// <summary>
    /// 接続済みならそのまま使い、切れていれば探し直す。
    /// 同じポート構成での失敗は一定時間繰り返さない。
    /// </summary>
    public bool EnsureConnected(string? preferredPort = null)
    {
        lock (_lock)
        {
            // 実ポートが開いていることも確認する。抜かれた機器や書き込み失敗後の接続は再検出へ回す。
            bool portOpen = false;
            try { portOpen = _serialPort?.IsOpen ?? false; } catch { }
            if (IsConnected && (_serialPort != null ? portOpen : _activeStream != null))
                return true;

            // 機器なしで曲を開始するたびに全ポートを探査して固まらないよう、同じポート構成での失敗は一定時間繰り返さない。
            string[] names;
            try { names = SerialPort.GetPortNames(); } catch { names = []; }
            string signature = string.Join("|", names.OrderBy(name => name, StringComparer.OrdinalIgnoreCase)) + "|" + preferredPort;
            if (ShouldSkipDetect(signature, _failedDetectSignature, _failedDetectUtc, DateTime.UtcNow))
            {
                if (_serialPort != null || _activeStream != null) DisconnectInternal();
                return false;
            }

            // 探査し、失敗したらそのポート構成と時刻を覚える。
            bool detected = DetectDevice(preferredPort);
            _failedDetectSignature = detected ? null : signature;
            _failedDetectUtc = DateTime.UtcNow;
            return detected;
        }
    }

    /// <summary>
    /// いまの接続を閉じ、NanoDrive 6 を探して接続する。
    /// 指定ポートがあれば最初に試す。
    /// </summary>
    public bool DetectDevice(string? preferredPort = null)
    {
        lock (_lock)
        {
            DisconnectInternal();

            string[] ports;
            try { ports = SerialPort.GetPortNames(); }
            catch { ports = []; }
            if (ports.Length == 0 && string.IsNullOrEmpty(preferredPort)) return false;

            // ND7/ND8と同じUSB IDなので、機種は応答した名前で見分ける。ポートごとの機種は NdsifPortCatalog が覚える。
            string[] usbPorts = NdsifPortCatalog.UsbPorts();
            for (int pass = 0; pass < 2; pass++)
            {
                var portList = new List<string>(NdsifPortCatalog.Plan("NanoDrive 6", ports, usbPorts, [], out int knownTargets));
                // 指定ポートを先頭へ移す。
                if (!string.IsNullOrEmpty(preferredPort))
                {
                    portList.RemoveAll(p => string.Equals(p, preferredPort, StringComparison.OrdinalIgnoreCase));
                    portList.Insert(0, preferredPort);
                }
                if (TryDetectIn(portList, ports, usbPorts)) return true;
                // ND6と覚えていたポートが違っていたときだけ、記録を捨てて全ポートを探し直す。
                if (knownTargets == 0) break;
                NdsifPortCatalog.Reset();
            }
            return false;
        }
    }

    /// <summary>並べたポートを順に開いて名前を問い合わせ、NanoDrive 6 なら接続する。</summary>
    private bool TryDetectIn(List<string> portList, string[] ports, string[] usbPorts)
    {
        // 名前を返さないUSBのNanoDrive候補。ほかに候補がなければ古いファームのND6として使うので、開いたまま持っておく。
        SerialPort? silent = null;
        string? silentPort = null;
        bool ambiguous = false;
        try
        {
            foreach (string portName in portList)
            {
                bool isUsb = usbPorts.Contains(portName, StringComparer.OrdinalIgnoreCase);
                SerialPort? port = null;
                string? reportedName = null;
                try
                {
                    // ポートを開いて GET_INFO（0x02）を送り、返った機種名を記録する。
                    port = NdsifPortCatalog.Open(portName, 500);
                    ushort txId = _transactionCounter++;
                    byte[] getInfo = BuildFramedPacket(0x01, 0x02, txId, ReadOnlySpan<byte>.Empty);
                    port.Write(getInfo, 0, getInfo.Length);
                    System.Threading.Thread.Sleep(50);

                    if (TryReadFrame(port.BaseStream, 500, out var response) &&
                        response.CommandId == 0x02 && response.MessageType == 0x02 &&
                        response.Payload.Length >= 2 && response.Payload[0] == 0x00 &&
                        response.Payload.Length >= 2 + response.Payload[1])
                        reportedName = Encoding.UTF8.GetString(response.Payload, 2, response.Payload[1]);
                    Console.WriteLine($"[NDSIF] {portName}: {reportedName ?? "no reply"}");
                    NdsifPortCatalog.Remember(ports, portName, reportedName, isUsb);

                    // NanoDrive 6 と名乗ればすぐ接続する。
                    if (NdsifPortCatalog.IsModel(reportedName, "NanoDrive 6"))
                    {
                        Claim(port, portName, reportedName!);
                        port = null;
                        return true;
                    }
                    // 名前を返さないUSB候補は1つ目だけ取っておく。
                    if (CanUseNd6UsbFallback(isUsb, reportedName))
                    {
                        if (silent == null) { silent = port; silentPort = portName; port = null; }
                        else ambiguous = true;
                    }
                }
                catch (Exception ex)
                {
                    if (reportedName == null) NdsifPortCatalog.Remember(ports, portName, null, isUsb);
                    Console.WriteLine($"[NDSIF] Error on {portName}: {ex.Message}");
                }
                finally
                {
                    try { port?.Close(); port?.Dispose(); } catch { }
                }
            }

            // 名前を返さない候補が2つ以上あるときは、どれがND6か決められないので使わない。
            if (silent == null || ambiguous) return false;
            Console.WriteLine($"[NDSIF] {silentPort} taken as NanoDrive 6 via USB VID_303A&PID_1001.");
            Claim(silent, silentPort!, "NanoDrive 6");
            silent = null;
            return true;
        }
        finally
        {
            try { silent?.Close(); silent?.Dispose(); } catch { }
        }
    }

    /// <summary>
    /// 開いたポートをこの接続として使い始める。
    /// Timed DAC の対応を調べ、送信スレッドを起こして基板をリセットする。
    /// </summary>
    private void Claim(SerialPort port, string portName, string name)
    {
        _serialPort = port;
        _activeStream = port.BaseStream;
        PortName = portName;
        DeviceName = name;
        IsConnected = true;
        ProbeTimedDacCapabilities(port);
        StartTxWorker();
        Reset();
    }
    /// <summary>名前を返さない、または NanoDrive 6 と名乗る USB のポートを ND6 として使ってよいか判定する。</summary>
    internal static bool CanUseNd6UsbFallback(bool matchedUsbPort, string? reportedName) =>
        matchedUsbPort && (reportedName == null ||
            reportedName.StartsWith("NanoDrive 6", StringComparison.OrdinalIgnoreCase));

    /// <summary>Timed DAC Stream の容量を問い合わせる。</summary>
    private void ProbeTimedDacCapabilities(SerialPort port)
    {
        // NanoDrive 6 のファームは Timed DAC Stream（Profile = 1、0x18〜0x1D）に対応する。USBの応答が遅れたときに備えて ND6 ファームの容量を既定にしてから、0x18 で問い合わせる。
        SupportsTimedDac = true;
        _dacRecordCapacity = 8192;
        _dacEventCapacity = 512;
        _dacMaxRecords = 60;

        // 応答の値が妥当な範囲なら容量を置き換える。拒否されたら即時書き込みに切り替える。
        try
        {
            port.DiscardInBuffer();
            ushort txId = _transactionCounter++;
            byte[] getCaps = BuildFramedPacket(0x01, 0x18, txId, ReadOnlySpan<byte>.Empty);
            port.Write(getCaps, 0, getCaps.Length);

            if (TryReadFrame(port.BaseStream, 250, out var capsResp) &&
                capsResp.CommandId == 0x18 && capsResp.MessageType == 0x02)
            {
                if (capsResp.Payload.Length >= 22 && capsResp.Payload[0] == 0x00 && capsResp.Payload[1] == 0x01)
                {
                    uint cap = BinaryPrimitives.ReadUInt32LittleEndian(capsResp.Payload.AsSpan(10, 4));
                    ushort evCap = BinaryPrimitives.ReadUInt16LittleEndian(capsResp.Payload.AsSpan(14, 2));
                    byte maxRec = capsResp.Payload[16];
                    if (cap >= 256) _dacRecordCapacity = cap;
                    if (evCap >= 32) _dacEventCapacity = evCap;
                    if (maxRec >= 1 && maxRec <= 60) _dacMaxRecords = maxRec;
                    Console.WriteLine($"[NDSIF] Timed DAC capabilities: recordCap={_dacRecordCapacity}, eventCap={_dacEventCapacity}, maxRecords={_dacMaxRecords}");
                }
                else if (capsResp.Payload.Length >= 1 && capsResp.Payload[0] != 0x00)
                {
                    SupportsTimedDac = false;
                    Console.WriteLine($"[NDSIF] Timed DAC rejected with status 0x{capsResp.Payload[0]:X2}; falling back to immediate mode.");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[NDSIF] ProbeTimedDacCapabilities warning: {ex.Message}");
        }
        finally
        {
            try { port.ReadTimeout = 50; } catch { }
        }
    }

    /// <summary>テスト用に、ポートの代わりに任意のストリームをつないで接続済みにする。</summary>
    public void AttachStreamForTesting(Stream stream, string deviceName = "NanoDrive 6.1", string portName = "COM_TEST", bool supportsTimedDac = false)
    {
        lock (_lock)
        {
            DisconnectInternal();
            _activeStream = stream;
            PortName = portName;
            DeviceName = deviceName;
            IsConnected = true;
            SupportsTimedDac = supportsTimedDac;
            _dacRecordCapacity = 8192;
            _dacEventCapacity = 512;
            _dacMaxRecords = 60;
            if (supportsTimedDac)
            {
                StartTxWorker();
            }
        }
    }

    /// <summary>
    /// 区切りの0までを1枠として読み、正しく解析できた最初のメッセージを返す。
    /// 時間切れなら false。
    /// </summary>
    private static bool TryReadFrame(Stream stream, int timeoutMs, out NdsifMessage message)
    {
        message = default;
        var buffer = new List<byte>(64);
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

        while (DateTime.UtcNow < deadline)
        {
            int b;
            try
            {
                b = stream.ReadByte();
            }
            catch (TimeoutException)
            {
                continue;
            }
            catch (IOException ex)
            {
                Console.WriteLine($"[NDSIF] stream.ReadByte IOException: {ex.Message}");
                break;
            }

            // まだ届いていなければ少し待つ。
            if (b < 0)
            {
                System.Threading.Thread.Sleep(5);
                continue;
            }

            // 0は枠の区切り。たまった分を解析する。
            if (b == 0x00)
            {
                if (buffer.Count > 0)
                {
                    if (TryParseFrame(buffer.ToArray(), out message))
                        return true;
                    buffer.Clear();
                }
            }
            else
            {
                // 長すぎる枠は捨てる。
                buffer.Add((byte)b);
                if (buffer.Count > 512) buffer.Clear();
            }
        }
        return false;
    }

    /// <summary>
    /// 送信スレッドを起こす。
    /// すでに合図済みなら何もしない。
    /// </summary>
    private void SignalTxWorker()
    {
        if (Interlocked.Exchange(ref _txSignaled, 1) == 0)
        {
            _txSignal.Set();
        }
    }

    /// <summary>
    /// 送信スレッドを作り直して開始する。
    /// 待ちの精度を上げるため Windows のタイマー分解能を1msにする。
    /// </summary>
    private void StartTxWorker()
    {
        StopTxWorker();
        while (_txQueue.TryDequeue(out _)) { }

        if (OperatingSystem.IsWindows() && !_timePeriodActive)
        {
            TimeBeginPeriod(1);
            _timePeriodActive = true;
        }

        _txRunning = true;
        _txThread = new Thread(TxWorkerLoop)
        {
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
            Name = "NdsifTxWorker"
        };
        _txThread.Start();
    }

    /// <summary>送信スレッドを止め、タイマー分解能を戻して送信待ちを捨てる。</summary>
    private void StopTxWorker()
    {
        _txRunning = false;
        _txSignal.Set();
        if (_txThread != null)
        {
            try { _txThread.Join(200); } catch { }
            _txThread = null;
        }

        if (OperatingSystem.IsWindows() && _timePeriodActive)
        {
            TimeEndPeriod(1);
            _timePeriodActive = false;
        }

        while (_txQueue.TryDequeue(out _)) { }
    }

    /// <summary>
    /// 指定位置から演奏時計を動かし始める。
    /// Timed DAC では時計は基板側の開始に合わせて動かす。
    /// </summary>
    public void StartClock(uint initialSample = 0)
    {
        lock (_lock)
        {
            _baseSampleOffset = initialSample;
            if (_parsedSample < initialSample)
            {
                _parsedSample = initialSample;
            }
            _sourceEnded = false;
            _clockRequested = true;
            _isPaused = false;
            if (!SupportsTimedDac)
            {
                _playbackClock.Restart();
            }
        }
        SignalTxWorker();
    }

    /// <summary>演奏時計を止める。</summary>
    public void PauseClock()
    {
        lock (_lock)
        {
            _isPaused = true;
            _playbackClock.Stop();
        }
    }

    /// <summary>
    /// 演奏時計を再開する。
    /// Timed DAC では一度も動いていない時計は開始を待つ。
    /// </summary>
    public void ResumeClock()
    {
        lock (_lock)
        {
            _isPaused = false;
            if (_clockRequested && (!SupportsTimedDac || _playbackClock.ElapsedTicks > 0))
            {
                _playbackClock.Start();
            }
        }
        SignalTxWorker();
    }

    /// <summary>演奏位置を指定サンプルへ戻し、送信待ちを捨てて新しいセッションにする。</summary>
    public void ResetClock(uint sample = 0)
    {
        lock (_lock)
        {
            _baseSampleOffset = sample;
            _parsedSample = sample;
            _sourceEnded = false;
            _clockRequested = false;
            _isPaused = false;
            _isCleanReset = false;
            _sessionGeneration++;
            _playbackClock.Reset();
            while (_txQueue.TryDequeue(out _)) { }
        }
        SignalTxWorker();
    }

    /// <summary>音源側が作り終えた位置を進め、送信スレッドに知らせる。</summary>
    public void AdvanceSample(uint renderedSample)
    {
        if (!IsConnected) return;
        uint prev = Volatile.Read(ref _parsedSample);
        if (renderedSample > prev)
        {
            Volatile.Write(ref _parsedSample, renderedSample);
            SignalTxWorker();
        }
    }

    /// <summary>曲データの終わりを知らせる。</summary>
    public void NotifySourceEnded(uint finalSample)
    {
        if (!IsConnected) return;
        uint prev = Volatile.Read(ref _parsedSample);
        if (finalSample > prev)
        {
            Volatile.Write(ref _parsedSample, finalSample);
        }
        _sourceEnded = true;
        SignalTxWorker();
    }

    /// <summary>送信待ちが空になるまで最大50ms待つ。</summary>
    public void Flush()
    {
        if (!_txRunning) return;
        var deadline = DateTime.UtcNow.AddMilliseconds(50);
        while (!_txQueue.IsEmpty && DateTime.UtcNow < deadline)
        {
            SignalTxWorker();
            Thread.Sleep(1);
        }
    }

    /// <summary>演奏状態をすべて初期化し、基板にリセット（0x00）を送る。</summary>
    public void Reset()
    {
        lock (_lock)
        {
            // 時計・位置・送信待ち・YM2612 のレジスタ控えを初期化する。
            _playbackClock.Reset();
            _baseSampleOffset = 0;
            _parsedSample = 0;
            _sourceEnded = false;
            _clockRequested = false;
            _isPaused = false;
            _isCleanReset = true;
            _sessionGeneration++;
            Array.Clear(_ymRegs, 0, _ymRegs.Length);
            while (_txQueue.TryDequeue(out _)) { }

            // 基板にリセットを送り、応答を読み捨てる。
            if (!IsConnected || _activeStream == null) return;
            try
            {
                if (_serialPort != null) _serialPort.DiscardInBuffer();
                byte[] packet = BuildFramedPacket(0x01, 0x00, _transactionCounter++, ReadOnlySpan<byte>.Empty);
                _activeStream.Write(packet, 0, packet.Length);
                _activeStream.Flush();
                if (_serialPort != null)
                {
                    TryReadFrame(_activeStream, 100, out _);
                }
            }
            catch (Exception ex)
            {
                MarkLinkLost("reset", ex);
            }
        }
        SignalTxWorker();
    }

    /// <summary>
    /// 音源のクロックを設定する（0x04）。
    /// 先に送信待ちを流してから送る。
    /// </summary>
    public void SetChipClock(byte writeId, byte instance, uint hz)
    {
        Flush();

        lock (_lock)
        {
            if (!IsConnected || _activeStream == null) return;
            try
            {
                // 書き込みID・インスタンス・クロック（リトルエンディアン32ビット）を並べる。
                Span<byte> payload = stackalloc byte[6];
                payload[0] = writeId;
                payload[1] = instance;
                payload[2] = (byte)(hz & 0xFF);
                payload[3] = (byte)((hz >> 8) & 0xFF);
                payload[4] = (byte)((hz >> 16) & 0xFF);
                payload[5] = (byte)((hz >> 24) & 0xFF);
                byte[] packet = BuildFramedPacket(0x00, 0x04, 0, payload);
                _activeStream.Write(packet, 0, packet.Length);
                _activeStream.Flush();
            }
            catch (Exception ex)
            {
                MarkLinkLost("set clock", ex);
            }
        }
    }

    /// <summary>
    /// YM2612 に書き込む。
    /// 送信スレッドが動いていれば時刻付きで送信待ちに積み、そうでなければすぐ送る。
    /// </summary>
    public void WriteYm2612(byte port, byte reg, byte val, uint targetSample = 0)
    {
        if (!IsConnected) return;

        if (_txRunning)
        {
            byte cmd = (byte)(port == 0 ? 0x52 : 0x53);
            _txQueue.Enqueue(new NdsifWrite(targetSample, cmd, port, reg, val));
            if (!SupportsTimedDac) SignalTxWorker();
            return;
        }

        // 送信スレッドがないときは、レジスタの控えを更新してすぐ送る。ペイロードはインスタンス0、ページ（ポート0/1）、レジスタ、値。
        lock (_lock)
        {
            if (_activeStream == null) return;
            try
            {
                _ymRegs[port & 1, reg] = val;
                byte cmd = (byte)(port == 0 ? 0x52 : 0x53);
                Span<byte> payload = stackalloc byte[4];
                payload[0] = 0;
                payload[1] = port;
                payload[2] = reg;
                payload[3] = val;
                byte[] packet = BuildFramedPacket(0x00, cmd, 0, payload);
                _activeStream.Write(packet, 0, packet.Length);
            }
            catch (Exception ex)
            {
                MarkLinkLost("YM2612 write", ex);
            }
        }
    }

    /// <summary>
    /// SN76489 に書き込む。
    /// 1台目は 0x50、2台目は 0x30 で送る。
    /// </summary>
    public void WriteSn76489(byte instance, byte val, uint targetSample = 0)
    {
        if (!IsConnected) return;

        byte cmd = (byte)(instance == 0 ? 0x50 : 0x30);
        if (_txRunning)
        {
            _txQueue.Enqueue(new NdsifWrite(targetSample, cmd, instance, 0, val));
            if (!SupportsTimedDac) SignalTxWorker();
            return;
        }

        // 送信スレッドがないときはすぐ送る。ペイロードはインスタンス（0/1）、ページ0、データ。
        lock (_lock)
        {
            if (_activeStream == null) return;
            try
            {
                Span<byte> payload = stackalloc byte[3];
                payload[0] = instance;
                payload[1] = 0;
                payload[2] = val;
                byte[] packet = BuildFramedPacket(0x00, cmd, 0, payload);
                _activeStream.Write(packet, 0, packet.Length);
            }
            catch (Exception ex)
            {
                MarkLinkLost("SN76489 write", ex);
            }
        }
    }

    /// <summary>送信スレッドを止め、ポートを閉じて未接続にする。</summary>
    private void DisconnectInternal()
    {
        StopTxWorker();
        IsConnected = false;
        SupportsTimedDac = false;
        DeviceName = null;
        PortName = null;

        if (_serialPort != null)
        {
            try { _serialPort.Close(); } catch { }
            try { _serialPort.Dispose(); } catch { }
            _serialPort = null;
        }
        _activeStream = null;
    }

    public void Disconnect()
    {
        lock (_lock)
        {
            DisconnectInternal();
        }
    }

    public void Dispose()
    {
        Disconnect();
    }
}
