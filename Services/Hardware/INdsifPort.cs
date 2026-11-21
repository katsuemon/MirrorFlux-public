using System.IO.Ports;

namespace MirrorFlux.Services.Hardware;

/// <summary>基板との通信。停止時の応答待ちも実機なしで検証できるように分離する。</summary>
internal interface INdsifPort : IDisposable
{
    string PortName { get; }
    bool IsOpen { get; }
    void Write(byte[] buffer, int offset, int count);
    int ReadByte();
}

internal sealed class SerialNdsifPort(SerialPort port) : INdsifPort
{
    public string PortName => port.PortName;
    public bool IsOpen => port.IsOpen;
    public void Write(byte[] buffer, int offset, int count) => port.Write(buffer, offset, count);
    public int ReadByte() => port.ReadByte();
    public void Dispose() => port.Dispose();
}
