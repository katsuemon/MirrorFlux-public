using System.Buffers.Binary;
using DacRecord = MirrorFlux.Services.Hardware.NdsifService.DacRecord;

namespace MirrorFlux.Services.Hardware;

/// <summary>NDSIF のシリアル通信で使う枠（ヘッダ・CRC・COBS）と、各コマンドのペイロードを組み立て・解析する。</summary>
internal static class NdsifProtocol
{
    /// <summary>CRC-16/CCITT-FALSE（初期値0xFFFF、多項式0x1021）を計算する。</summary>
    public static ushort ComputeCrc16(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (byte b in data)
        {
            crc ^= (ushort)(b << 8);
            for (int i = 0; i < 8; i++)
            {
                if ((crc & 0x8000) != 0)
                    crc = (ushort)((crc << 1) ^ 0x1021);
                else
                    crc = (ushort)(crc << 1);
            }
        }
        return crc;
    }

    /// <summary>
    /// COBS で符号化し、データ中の0をなくす。
    /// 0はパケットの区切りに使う。
    /// </summary>
    public static byte[] CobsEncode(ReadOnlySpan<byte> input)
    {
        byte[] output = new byte[input.Length + (input.Length / 254) + 2];
        int writeIndex = 0;
        int codeIndex = writeIndex++;
        byte code = 1;

        // 0以外はそのまま写し、254バイト続いたら区切りを入れる。
        for (int i = 0; i < input.Length; i++)
        {
            byte b = input[i];
            if (b != 0)
            {
                output[writeIndex++] = b;
                code++;
                if (code == 0xFF)
                {
                    output[codeIndex] = code;
                    code = 1;
                    codeIndex = writeIndex++;
                }
            }
            else
            {
                // 0の位置で区切りの長さを確定する。
                output[codeIndex] = code;
                code = 1;
                codeIndex = writeIndex++;
            }
        }
        output[codeIndex] = code;
        Array.Resize(ref output, writeIndex);
        return output;
    }

    /// <summary>
    /// COBS を復号する。
    /// 壊れた入力や出力先の不足なら false を返す。
    /// </summary>
    public static bool TryCobsDecode(ReadOnlySpan<byte> input, Span<byte> output, out int bytesWritten)
    {
        bytesWritten = 0;
        int readIndex = 0;
        while (readIndex < input.Length)
        {
            byte code = input[readIndex++];
            if (code == 0) return false;
            int copyLen = code - 1;
            if (readIndex + copyLen > input.Length) return false;
            for (int i = 0; i < copyLen; i++)
            {
                if (bytesWritten >= output.Length) return false;
                output[bytesWritten++] = input[readIndex++];
            }
            if (code < 0xFF && readIndex < input.Length)
            {
                if (bytesWritten >= output.Length) return false;
                output[bytesWritten++] = 0;
            }
        }
        return true;
    }

    /// <summary>
    /// 枠の生データを作る。
    /// 'N' 'D'、ワイヤ版数1、種類、コマンド、トランザクションID、長さ、ペイロード、CRC16（すべてリトルエンディアン）の順に並ぶ。
    /// </summary>
    public static byte[] BuildRawFrame(byte messageType, byte commandId, ushort transactionId, ReadOnlySpan<byte> payload)
    {
        int rawLen = 9 + payload.Length + 2;
        byte[] raw = new byte[rawLen];
        // 先頭の識別子 'N' 'D' とワイヤ版数1。
        raw[0] = 0x4E;
        raw[1] = 0x44;
        raw[2] = 0x01;
        // 種類・コマンド・トランザクションID・ペイロード長。
        raw[3] = messageType;
        raw[4] = commandId;
        raw[5] = (byte)(transactionId & 0xFF);
        raw[6] = (byte)((transactionId >> 8) & 0xFF);
        raw[7] = (byte)(payload.Length & 0xFF);
        raw[8] = (byte)((payload.Length >> 8) & 0xFF);
        if (!payload.IsEmpty)
        {
            payload.CopyTo(raw.AsSpan(9));
        }
        // 先頭からペイロード末尾までの CRC を付ける。
        ushort crc = ComputeCrc16(raw.AsSpan(0, 9 + payload.Length));
        raw[9 + payload.Length] = (byte)(crc & 0xFF);
        raw[10 + payload.Length] = (byte)((crc >> 8) & 0xFF);
        return raw;
    }

    /// <summary>生データを COBS で包み、前後に区切りの0を付けた送信パケットを作る。</summary>
    public static byte[] BuildFramedPacket(byte messageType, byte commandId, ushort transactionId, ReadOnlySpan<byte> payload)
    {
        byte[] raw = BuildRawFrame(messageType, commandId, transactionId, payload);
        byte[] cobs = CobsEncode(raw);
        byte[] packet = new byte[cobs.Length + 2];
        packet[0] = 0x00;
        Buffer.BlockCopy(cobs, 0, packet, 1, cobs.Length);
        packet[^1] = 0x00;
        return packet;
    }

    /// <summary>
    /// DAC_DATA のペイロードを作る。
    /// PROFILE=1、write_id=0x52（YM2612 port 0）、instance=0、通し番号、位置に続けて、4バイトずつのレコードを並べる。
    /// </summary>
    public static byte[] BuildDacDataPayload(uint sequence, ulong position, ReadOnlySpan<DacRecord> records)
    {
        byte[] payload = new byte[15 + records.Length * 4];
        payload[0] = 0x01;
        payload[1] = 0x52;
        payload[2] = 0x00;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(3, 4), sequence);
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(7, 8), position);
        // 各レコードは種類・値・待ち（リトルエンディアン16ビット）。
        for (int i = 0; i < records.Length; i++)
        {
            int offset = 15 + i * 4;
            payload[offset] = records[i].Kind;
            payload[offset + 1] = records[i].Value;
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(offset + 2, 2), records[i].Wait);
        }
        return payload;
    }

    /// <summary>
    /// レジスタイベントのペイロードを作る。
    /// PROFILE=1、ティック、対象ID、インスタンス、ページ、レジスタデータの順。
    /// </summary>
    public static byte[] BuildRegisterEventPayload(ulong tick, byte targetId, byte instance, byte page, ReadOnlySpan<byte> registerData)
    {
        byte[] payload = new byte[12 + registerData.Length];
        payload[0] = 0x01;
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(1, 8), tick);
        payload[9] = targetId;
        payload[10] = instance;
        payload[11] = page;
        registerData.CopyTo(payload.AsSpan(12));
        return payload;
    }

    /// <summary>
    /// DAC_START のペイロードを作る。
    /// PROFILE=1 と最低の先行ティック数。
    /// </summary>
    public static byte[] BuildDacStartPayload(uint minimumLeadTicks)
    {
        byte[] payload = new byte[5];
        payload[0] = 0x01;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(1, 4), minimumLeadTicks);
        return payload;
    }

    /// <summary>
    /// DAC_END のペイロードを作る。
    /// PROFILE=1、次の通し番号、終了ティック。
    /// </summary>
    public static byte[] BuildDacEndPayload(uint nextSequence, ulong endTick)
    {
        byte[] payload = new byte[13];
        payload[0] = 0x01;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(1, 4), nextSequence);
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(5, 8), endTick);
        return payload;
    }

    /// <summary>受信した COBS 枠を復号し、識別子・長さ・CRC を確かめてメッセージにする。</summary>
    public static bool TryParseFrame(ReadOnlySpan<byte> cobsSpan, out NdsifMessage message)
    {
        // 前後の区切りの0を取り除く。
        message = default;
        while (!cobsSpan.IsEmpty && cobsSpan[0] == 0) cobsSpan = cobsSpan[1..];
        while (!cobsSpan.IsEmpty && cobsSpan[^1] == 0) cobsSpan = cobsSpan[..^1];
        if (cobsSpan.IsEmpty) return false;

        // COBS を復号し、識別子とワイヤ版数を確かめる。
        Span<byte> raw = stackalloc byte[512];
        if (!TryCobsDecode(cobsSpan, raw, out int rawLen) || rawLen < 11)
            return false;

        if (raw[0] != 0x4E || raw[1] != 0x44 || raw[2] != 0x01)
            return false;

        // ヘッダを読み、長さと CRC が合うか確かめる。
        byte messageType = raw[3];
        byte commandId = raw[4];
        ushort txId = (ushort)(raw[5] | (raw[6] << 8));
        ushort payloadLen = (ushort)(raw[7] | (raw[8] << 8));

        if (rawLen != 11 + payloadLen)
            return false;

        ushort expectedCrc = ComputeCrc16(raw[0..(9 + payloadLen)]);
        ushort actualCrc = (ushort)(raw[9 + payloadLen] | (raw[10 + payloadLen] << 8));
        if (expectedCrc != actualCrc)
            return false;

        byte[] payload = raw.Slice(9, payloadLen).ToArray();
        message = new NdsifMessage(messageType, commandId, txId, payload);
        return true;
    }

}
