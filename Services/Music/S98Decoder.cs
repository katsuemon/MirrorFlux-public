using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace MirrorFlux.Services.Music;

/// <summary>S98 の時刻付きレジスタ列を、共通の音源レンダラーが読む VGM のデータに変換する。</summary>
public static class S98Decoder
{
    /// <summary>
    /// S98 を VGM（1.71）に変換する。
    /// ヘッダーの音源表から VGM のクロック欄とコマンドを決め、待ちを 44.1 kHz のサンプル数に直し、タグを GD3 に移す。
    /// </summary>
    public static byte[] Convert(byte[] data, string fallbackTitle)
    {
        // ヘッダーを検査し、版数ごとのタイマー（分子・分母）とデータ・ループ・タグの位置を読む。圧縮された S98 は扱わない。
        if (data.Length < 32 || !data.AsSpan(0, 3).SequenceEqual("S98"u8) || data[3] is < (byte)'0' or > (byte)'3')
            throw new InvalidDataException("Invalid S98 header.");
        uint U32(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
        int version = data[3] - '0';
        if (U32(12) != 0) throw new NotSupportedException("Compressed S98 is not supported.");
        uint numerator = version == 0 ? 10 : U32(4), denominator = version < 2 ? 1000 : U32(8);
        if (numerator == 0) numerator = 10;
        if (denominator == 0) denominator = 1000;
        uint dump = U32(20), loop = U32(24), tag = U32(16);
        if (dump < 32 || dump >= data.Length) throw new InvalidDataException("Invalid S98 data offset.");
        // 版数2以降は音源表を読む。表がなければ OPNA（7987200Hz）1つとみなす。
        var devices = new List<(uint Type, uint Clock)>();
        if (version >= 2)
        {
            uint count = version == 3 ? U32(28) : 126;
            if (count > 126) throw new InvalidDataException("Too many S98 devices.");
            for (int i = 0; i < count; i++)
            {
                int p = 32 + i * 16;
                if (p + 16 > dump) throw new InvalidDataException("Truncated S98 device table.");
                uint type = U32(p);
                if (version == 2 && type == 0) break;
                devices.Add((type, U32(p + 4)));
            }
        }
        if (devices.Count == 0) devices.Add((4, 7987200));
        // VGM のヘッダーを用意する。
        byte[] header = new byte[256];
        "Vgm "u8.CopyTo(header);
        void Put(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(offset), value);
        Put(8, 0x171); Put(0x34, 256 - 0x34);
        // S98 の音源の種類ごとに、VGM のクロック欄・コマンド・2ポートかを決める。同じ音源が2つあれば2台目として扱う。
        var commands = new List<(byte Command, bool DualPort)>();
        var seen = new Dictionary<int, uint>();
        foreach (var device in devices)
        {
            (int field, int command, bool dual) = device.Type switch
            {
                1 or 15 => (0x74, 0xa0, false), 2 => (0x44, 0x55, false),
                3 => (0x2c, 0x52, true), 4 => (0x48, 0x56, true), 5 => (0x30, 0x54, false),
                6 => (0x10, 0x51, false), 7 => (0x54, 0x5b, false), 8 => (0x50, 0x5a, false),
                9 => (0x5c, 0x5e, true), 16 => (0x0c, 0x50, false),
                _ => throw new NotSupportedException($"S98 device {device.Type} is not supported.")
            };
            if (device.Clock == 0 || device.Clock >= 0x40000000) throw new InvalidDataException("Invalid S98 clock.");
            if (seen.TryGetValue(field, out uint previous))
            {
                if (device.Type is not (2 or 4 or 6 or 7 or 8) || previous != device.Clock) throw new NotSupportedException("Unsupported duplicate S98 device configuration.");
                command += 0x50; Put(field, device.Clock | 0x40000000); seen[field] = uint.MaxValue;
            }
            else { seen[field] = device.Clock; Put(field, device.Clock); }
            // 種類1は AY-3-8910 ではなく YM2149 として印を付ける。
            if (device.Type == 1) header[0x78] = 0x10;
            commands.Add(((byte)command, dual));
        }
        // PC-98 のソフトは記録を始める前に OPNA の拡張 FM（FM4〜6）を有効にしていることが多い。そうした S98 はポート1の音色の書き込みを含むのに、1回だけの 0x29 の初期化が抜けていて、リセットしたばかりのエミュレーターでは FM4〜6 が鳴らない。VGM ではふつうこの書き込みが入っているので、S98 を変換するときだけ補う。
        using var output = new MemoryStream(); output.Write(header);
        foreach (var device in commands.Where(device => device.Item1 is 0x56 or 0xa6))
            output.Write(new byte[] { device.Item1, 0x29, 0x80 });
        // コマンドを順に読み、ループ位置を覚えながら VGM のコマンドに置き換える。0xfd で終わる。
        int position = (int)dump, loopPosition = 0;
        ulong samples = 0, loopSample = 0;
        UInt128 remainder = 0;
        while (position < data.Length)
        {
            if (position == loop) { loopPosition = (int)output.Position; loopSample = samples; }
            byte command = data[position++];
            if (command == 0xfd) break;
            // 0xff は1ティック、0xfe は可変長の待ち（値＋2ティック）。端数を持ち越しながら 44.1 kHz のサンプル数に直し、65535 ずつの待ちコマンドにする。
            if (command is 0xff or 0xfe)
            {
                ulong ticks = 1;
                if (command == 0xfe)
                {
                    ticks = 0; int shift = 0; byte value;
                    do
                    {
                        if (position >= data.Length || shift > 28) throw new InvalidDataException("Invalid S98 wait.");
                        value = data[position++]; ticks |= (ulong)(value & 127) << shift; shift += 7;
                    } while ((value & 128) != 0);
                    ticks += 2;
                }
                UInt128 duration = remainder + (UInt128)ticks * numerator * 44100;
                ulong wait = checked((ulong)(duration / denominator)); remainder = duration % denominator;
                samples = checked(samples + wait);
                if (samples > uint.MaxValue) throw new InvalidDataException("S98 duration is too large.");
                while (wait > 0)
                {
                    ushort chunk = (ushort)Math.Min(wait, 65535);
                    output.WriteByte(0x61); output.WriteByte((byte)chunk); output.WriteByte((byte)(chunk >> 8)); wait -= chunk;
                }
                continue;
            }
            // それ以外は音源番号×2＋ポートのレジスタ書き込み。
            int index = command / 2, port = command & 1;
            // 古い実時間記録は書き込みの途中で止まっていることがある。その最後の欠けた書き込みだけを捨て、足りないレジスタや値を作り出さない。
            if (position + 2 > data.Length) break;
            if (index >= commands.Count) throw new InvalidDataException($"Invalid S98 device {index} at {position - 1:X}.");
            var target = commands[index];
            byte register = data[position++], valueByte = data[position++];
            if (port != 0 && !target.DualPort) throw new NotSupportedException("Unsupported S98 register port.");
            // SN76489 の 0x4f（ステレオ）と、それ以外の音源のレジスタ・値を書き出す。
            output.WriteByte(target.Command == 0x50 && register == 1 ? (byte)0x4f : (byte)(target.Command + port));
            if (target.Command != 0x50) output.WriteByte(register);
            output.WriteByte(valueByte);
            if (output.Length > 128 * 1024 * 1024) throw new InvalidDataException("S98 register data is too large.");
        }
        // 初期のデータには FD の印なしでコマンドの切れ目で終わるものがあるので、終端は常に書く。
        output.WriteByte(0x66);
        // タグを読む。版数3は「[S98]」に続く key=value の行で、UTF-8（BOM付き）か Shift_JIS。それより前はタイトルだけ。
        string title = fallbackTitle, artist = "", game = "", system = "";
        if (tag > 0 && tag < data.Length)
        {
            int end = Array.IndexOf(data, (byte)0, (int)tag); if (end < 0) end = data.Length;
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            byte[] text = data.AsSpan((int)tag, end - (int)tag).ToArray();
            string tags;
            int start = version >= 3 && text.AsSpan().StartsWith("[S98]"u8) ? 5 : 0;
            if (text.AsSpan(start).StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) tags = Encoding.UTF8.GetString(text, start + 3, text.Length - start - 3);
            else tags = Encoding.GetEncoding(932).GetString(text, start, text.Length - start);
            if (version < 3) title = tags;
            else foreach (string line in tags.Split('\n'))
            {
                int equal = line.IndexOf('='); if (equal < 0) continue;
                string value = line[(equal + 1)..].TrimEnd('\r');
                switch (line[..equal].Trim().ToLowerInvariant())
                { case "title": title = value; break; case "artist": artist = value; break; case "game": game = value; break; case "system": system = value; break; }
            }
        }
        // GD3 タグ、全体の長さ、サンプル数、ループ情報をヘッダーに書いて仕上げる。
        Put(0x14, (uint)output.Position - 0x14);
        byte[] gd3 = Encoding.Unicode.GetBytes(string.Join('\0', new[] { title, "", game, "", system, "", artist, "", "", "", "" }) + '\0');
        output.Write("Gd3 "u8); output.Write(BitConverter.GetBytes(0x100u)); output.Write(BitConverter.GetBytes(gd3.Length)); output.Write(gd3);
        Put(4, (uint)output.Length - 4); Put(0x18, (uint)samples);
        if (loopPosition != 0 && samples > loopSample) { Put(0x1c, (uint)loopPosition - 0x1c); Put(0x20, (uint)(samples - loopSample)); }
        byte[] result = output.ToArray(); header.CopyTo(result, 0); return result;
    }
}
