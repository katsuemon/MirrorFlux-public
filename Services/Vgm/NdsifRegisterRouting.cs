using MirrorFlux.Services.Hardware;

namespace MirrorFlux.Services.Vgm;

/// <summary>VGMのレジスタ書込みを、NanoDriveへ送るレジスタ書込みに変換する。</summary>
internal static class NdsifRegisterRouting
{
    /// <summary>NanoDriveが直接受け取れる音源チップへの書込みなら、送信用の形式に変換する。</summary>
    internal static bool TryMap(IndependentVgmProgram.Command command, out NdsifRegisterLink.RegisterWrite write)
    {
        // VGM命令コードからNanoDrive側の音源チップIDを決める。2個目（0xAn）も同じIDにまとめるが、YM2203（0x55・0xA5）は命令コードをそのまま使う。
        byte id = command.Code switch
        {
            0x51 or 0xa1 => 0x51,
            0x54 => 0x54,
            0x55 or 0xa5 => command.Code,
            0x5a or 0xaa => 0x5a,
            0x5b or 0xab => 0x5b,
            0x5e or 0xae => 0x5e,
            0x5f or 0xaf => 0x5f,
            0xa0 => 0xa0,
            _ => 0
        };
        // 対象外の音源チップと、アドレス・値が範囲外の書込みは送らない。
        byte address = (byte)command.Address;
        if (id == 0 || command.Value > 255 || command.Address > 255 ||
            id == 0x51 && address > 0x3f || id == 0xa0 && address > 0x0f)
        { write = default; return false; }
        // ポート番号はYMF262（0x5F）だけ1にする。
        write = new NdsifRegisterLink.RegisterWrite(0, id, (byte)command.Instance,
            (byte)(id is 0x5f ? 1 : 0), address, (byte)command.Value);
        return true;
    }

    /// <summary>NanoDriveのクロック設定で使う音源チップIDを返す。</summary>
    internal static byte ClockId(int type) => type switch
    {
        1 => 0x51, 3 => 0x54, 6 => 0x55, 9 => 0x5a, 10 => 0x5b, 12 => 0x5e, 18 => 0xa0, _ => 0
    };

    /// <summary>
    /// ND7でクロックを共有する音源チップ同士が、同じ実クロックで動けるか確かめる。
    /// YM2203とAY8910は同じ番号同士、YM3812・YM3526・YMF262は種類違い同士を同時に置けない。
    /// 同じ系統に残る組合せは、実クロックが一致している必要がある。
    /// </summary>
    internal static bool Nd7ClocksCompatible(IEnumerable<IndependentVgmProgram.Device> devices)
    {
        var items = devices.ToArray();
        for (int i = 0; i < items.Length; i++)
            for (int j = 0; j < i; j++)
            {
                var a = items[i]; var b = items[j];
                int groupA = a.Type is 6 or 18 ? 0 : a.Type is 9 or 10 or 12 ? 1 : -1;
                int groupB = b.Type is 6 or 18 ? 0 : b.Type is 9 or 10 or 12 ? 1 : -1;
                if (groupA < 0 || groupA != groupB) continue;
                if (groupA == 0 && a.Instance == b.Instance && a.Type != b.Type) return false;
                if (groupA == 1 && a.Type != b.Type) return false;
                if (PhysicalClock(a) != PhysicalClock(b)) return false;
            }
        return true;
    }

    /// <summary>
    /// 基板に実際に与えるクロックに換算する。AY8910は2倍、YM3812・YM3526は4倍にする。
    /// 近い値は基板の水晶の値（3.579545MHz・14.31818MHz）にそろえ、YM2203・AY8910の3.9936MHzは4MHzとみなす。
    /// </summary>
    private static ulong PhysicalClock(IndependentVgmProgram.Device device)
    {
        ulong value = device.Clock & 0x3fffffff;
        if (device.Type == 18) value *= 2;
        if (device.Type is 9 or 10) value *= 4;
        if (value >= 3579000 && value <= 3580000) value = 3579545;
        if (value >= 14316000 && value <= 14320000) value = 14318180;
        if ((device.Type is 6 or 18) && value == 3993600) value = 4000000;
        return value;
    }
}
