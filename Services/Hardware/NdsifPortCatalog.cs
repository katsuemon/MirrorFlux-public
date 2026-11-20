using System.IO.Ports;

namespace MirrorFlux.Services.Hardware;

/// <summary>
/// どのCOMポートがどのNanoDriveかを、機種をまたいで覚えておく。
/// 複数台を同時に使うとき、他機種のポートを曲ごとに開閉して探し直さないためのもの。
/// 記録はポート構成が変わるまで有効で、名前を返した機器と、応答しなかったUSB以外のポートを覚える。
/// </summary>
internal static class NdsifPortCatalog
{
    private const string UsbId = "VID_303A&PID_1001";
    private static readonly object Sync = new();
    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase);
    private static string _signature = "";

    /// <summary>ポート構成を比べるための署名（ポート名を並べて連結した文字列）を作る。</summary>
    internal static string Signature(IEnumerable<string> ports) =>
        string.Join("|", ports.OrderBy(p => p, StringComparer.OrdinalIgnoreCase));

    internal static bool IsModel(string? name, string target) =>
        name != null && name.Contains(target, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// NanoDriveのUSB IDを持ち、現在存在するCOMポートをすべて返す。
    /// ND6/ND7/ND8はIDが共通。
    /// </summary>
    internal static string[] UsbPorts()
    {
        var found = new List<string>();
        if (!OperatingSystem.IsWindows()) return [];
        try
        {
            using var commKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DEVICEMAP\SERIALCOMM");
            using var usbKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB");
            if (commKey == null || usbKey == null) return [];
            // 今つながっているCOMポートの一覧を集める。
            var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string value in commKey.GetValueNames())
                if (commKey.GetValue(value) is string port && !string.IsNullOrWhiteSpace(port)) active.Add(port);
            // USB配下からNanoDriveのIDを持つ機器を探し、そのポート名を拾う。
            foreach (string device in usbKey.GetSubKeyNames())
            {
                if (!device.StartsWith(UsbId, StringComparison.OrdinalIgnoreCase)) continue;
                using var deviceKey = usbKey.OpenSubKey(device);
                if (deviceKey == null) continue;
                foreach (string instance in deviceKey.GetSubKeyNames())
                {
                    using var parameters = deviceKey.OpenSubKey(instance + @"\Device Parameters");
                    if (parameters?.GetValue("PortName") is string port && active.Contains(port) &&
                        !found.Contains(port, StringComparer.OrdinalIgnoreCase)) found.Add(port);
                }
            }
        }
        catch { }
        return [.. found];
    }

    /// <summary>
    /// Bluetoothの仮想COMポート（BTHENUM配下）を返す。
    /// NanoDriveはUSB接続なので探さない。
    /// これらのポートは開くと相手機器への接続を試み、つながらないと十数秒止まり、初回演奏が重くなる原因になる。
    /// </summary>
    internal static string[] BluetoothPorts()
    {
        var found = new List<string>();
        if (!OperatingSystem.IsWindows()) return [];
        try
        {
            using var bluetooth = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\BTHENUM");
            if (bluetooth == null) return [];
            foreach (string service in bluetooth.GetSubKeyNames())
            {
                using var serviceKey = bluetooth.OpenSubKey(service);
                if (serviceKey == null) continue;
                foreach (string instance in serviceKey.GetSubKeyNames())
                {
                    using var parameters = serviceKey.OpenSubKey(instance + @"\Device Parameters");
                    if (parameters?.GetValue("PortName") is string port && !string.IsNullOrWhiteSpace(port) &&
                        !found.Contains(port, StringComparer.OrdinalIgnoreCase)) found.Add(port);
                }
            }
        }
        catch { }
        return [.. found];
    }

    /// <summary>
    /// 開かないポートを返す。
    /// 使用中のポートと、Bluetoothのポート（NanoDriveのUSB IDを持つものを除く）。
    /// </summary>
    internal static HashSet<string> SkipSet(IEnumerable<string> excluded, IEnumerable<string> usbPorts, IEnumerable<string> bluetoothPorts)
    {
        var skip = new HashSet<string>(excluded, StringComparer.OrdinalIgnoreCase);
        var usb = new HashSet<string>(usbPorts, StringComparer.OrdinalIgnoreCase);
        foreach (string port in bluetoothPorts) if (!usb.Contains(port)) skip.Add(port);
        return skip;
    }

    /// <summary>
    /// 探す順を決める。
    /// 対象機種と分かっているポート、未確認のUSBポート、未確認のその他の順に並べる。
    /// 別の機器と分かっているポートは開かない。
    /// knownTargetsは先頭に並べた「対象機種と分かっているポート」の数。
    /// </summary>
    internal static string[] Plan(string target, IEnumerable<string> ports, IEnumerable<string> usbPorts,
        IEnumerable<string> excluded, IReadOnlyDictionary<string, string> known, out int knownTargets)
    {
        var usb = new HashSet<string>(usbPorts, StringComparer.OrdinalIgnoreCase);
        var skip = new HashSet<string>(excluded, StringComparer.OrdinalIgnoreCase);
        // 3つの組に振り分ける（0=対象機種、1=未確認USB、2=未確認その他）。
        var groups = new List<string>[] { [], [], [] };
        foreach (string port in ports.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (skip.Contains(port)) continue;
            if (known.TryGetValue(port, out string? name))
            {
                if (IsModel(name, target)) groups[0].Add(port);
            }
            else groups[usb.Contains(port) ? 1 : 2].Add(port);
        }
        knownTargets = groups[0].Count;
        return [.. groups[0], .. groups[1], .. groups[2]];
    }

    /// <summary>
    /// 記録済みの探査結果を使って探す順を決める。
    /// Bluetoothと使用中のポートは除く。
    /// </summary>
    internal static string[] Plan(string target, string[] ports, string[] usbPorts, IEnumerable<string> excluded, out int knownTargets)
    {
        var skip = SkipSet(excluded, usbPorts, BluetoothPorts());
        lock (Sync)
        {
            UsePortSet(ports);
            return Plan(target, ports, usbPorts, skip, new Dictionary<string, string>(Names, StringComparer.OrdinalIgnoreCase), out knownTargets);
        }
    }

    /// <summary>
    /// 探査結果を記録する。
    /// nameがnullなら応答なしで、USBのNanoDrive候補は次回も探査する。
    /// </summary>
    internal static void Remember(string[] ports, string port, string? name, bool isUsb)
    {
        lock (Sync)
        {
            UsePortSet(ports);
            if (name != null) Names[port] = name;
            else if (isUsb) Names.Remove(port);
            else Names[port] = "";
        }
    }

    internal static void Reset() { lock (Sync) { Names.Clear(); _signature = ""; } }

    /// <summary>ポート構成が変わっていたら記録を捨てる。</summary>
    private static void UsePortSet(string[] ports)
    {
        string signature = Signature(ports);
        if (signature == _signature) return;
        Names.Clear(); _signature = signature;
    }

    /// <summary>
    /// NDSIF用の設定でポートを開く。
    /// 閉じた直後のポートは使用中と報告されることがあるので、その場合だけ少し待って開き直す。
    /// </summary>
    internal static SerialPort Open(string portName, int readTimeout)
    {
        for (int attempt = 0; ; attempt++)
        {
            var port = new SerialPort(portName, 1500000, Parity.None, 8, StopBits.One)
            { ReadTimeout = readTimeout, WriteTimeout = 500, ReadBufferSize = 65536, WriteBufferSize = 65536 };
            try
            {
                // 開いてDTRを立て、少し待ってから送受信バッファを空にする。
                port.Open(); port.DtrEnable = true; port.RtsEnable = false;
                Thread.Sleep(50); port.DiscardInBuffer(); port.DiscardOutBuffer();
                return port;
            }
            catch (UnauthorizedAccessException) when (attempt < 2) { port.Dispose(); Thread.Sleep(150); }
            catch { port.Dispose(); throw; }
        }
    }
}
