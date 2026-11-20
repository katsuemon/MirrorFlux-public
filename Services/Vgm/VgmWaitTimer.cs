using System.Buffers.Binary;

namespace MirrorFlux.Services.Vgm;

/// <summary>
/// VGMの待ち時間命令（0x61〜0x63、0x64による上書き、0x7n、0x8n）の共通処理。
/// 0x8nのDAC書込みと命令の検証は呼び出し側が受け持つ。
/// </summary>
internal sealed class VgmWaitTimer
{
    public int Wait60 { get; private set; } = 735;
    public int Wait50 { get; private set; } = 882;

    /// <summary>待ち時間命令のバイト数を返す。待ち時間命令でなければ0。</summary>
    public static int CommandLength(byte op) => op switch
    {
        0x61 => 3,
        0x64 => 4,
        0x62 or 0x63 or >= 0x70 and <= 0x8f => 1,
        _ => 0
    };

    /// <summary>
    /// commandが待ち時間命令ならtrueを返し、進めるサンプル数をsamplesに入れる。
    /// 0x64は待ち時間を上書きして0を返す。対象が0x62・0x63以外の0x64は無視する。
    /// </summary>
    public bool TryWait(ReadOnlySpan<byte> command, out int samples)
    {
        samples = 0;
        if (command.IsEmpty) return false;
        byte op = command[0];
        int length = CommandLength(op);
        if (length == 0 || command.Length < length) return false;
        switch (op)
        {
            // 0x61は指定サンプル数、0x62・0x63は1/60秒・1/50秒、0x7nはn+1、0x8nはnサンプル待つ。
            case 0x61: samples = BinaryPrimitives.ReadUInt16LittleEndian(command[1..]); break;
            case 0x62: samples = Wait60; break;
            case 0x63: samples = Wait50; break;
            case 0x64:
                int duration = BinaryPrimitives.ReadUInt16LittleEndian(command[2..]);
                if (command[1] == 0x62) Wait60 = duration;
                else if (command[1] == 0x63) Wait50 = duration;
                break;
            case >= 0x70 and <= 0x7f: samples = (op & 15) + 1; break;
            default: samples = op & 15; break;
        }
        return true;
    }

    /// <summary>ループの待ち時間を数え直すため、上書き値を含めて複製する。</summary>
    public VgmWaitTimer Copy() => (VgmWaitTimer)MemberwiseClone();
}
