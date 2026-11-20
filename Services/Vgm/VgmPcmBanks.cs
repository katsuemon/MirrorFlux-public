using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;

namespace MirrorFlux.Services.Vgm;

/// <summary>データブロックで記録されたPCMバンクを保持し、公開VGM仕様のnビット圧縮・DPCM形式を展開する。</summary>
internal sealed class VgmPcmBanks
{
    // 1バンクの上限は64MiB、全バンク合計の上限は128MiB。
    internal const int BankLimit = 64 * 1024 * 1024;
    private const int TotalLimit = 128 * 1024 * 1024;
    /// <summary>PCMバンク1個分。連結したデータと、読み込んだブロックごとの位置と長さを持つ。</summary>
    internal sealed class Bank
    {
        internal readonly List<byte> Data = [];
        internal readonly List<(int Offset, int Length)> Blocks = [];
        internal ReadOnlySpan<byte> Bytes => CollectionsMarshal.AsSpan(Data);
    }
    private sealed record Table(byte DecodedBits, byte EncodedBits, ushort[] Values);
    private readonly Dictionary<byte, Bank> _banks = [];
    private readonly Dictionary<(byte Type, byte Subtype), Table> _tables = [];
    private readonly HashSet<int> _loaded = [];
    private int _total;
    internal Bank? Get(byte id) => _banks.GetValueOrDefault(id);
    /// <summary>データブロックをPCMバンクに読み込む。0x7Fは展開表、0x40以降は圧縮データとして扱う。</summary>
    internal void Load(byte kind, ReadOnlySpan<byte> payload, int sourceOffset)
    {
        if (kind > 0x7f) throw new ArgumentOutOfRangeException(nameof(kind));
        if (kind == 0x7f) { LoadTable(payload); return; }
        // 同じブロックを二度読み込まないよう、データ上の位置で判定する。
        if (_loaded.Contains(sourceOffset)) return;
        // 種類の下位6ビットがバンク番号。圧縮データは展開後の長さをヘッダーから読む。
        byte bankId = (byte)(kind & 0x3f);
        int expanded = payload.Length;
        if (kind >= 0x40)
        {
            if (payload.Length < 10) throw new InvalidDataException("VGM圧縮PCMのヘッダーが不足しています。");
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(payload[1..]);
            if (size > BankLimit) throw new NotSupportedException("decoded PCM exceeds 64 MiB");
            expanded = (int)size;
        }
        // 容量の上限を確かめてから展開し、バンクの末尾に追加する。
        var bank = Get(bankId);
        if (expanded > BankLimit - (bank?.Data.Count ?? 0) || expanded > TotalLimit - _total)
            throw new NotSupportedException("PCM bank memory budget exceeded");
        byte[] bytes = kind < 0x40 ? payload.ToArray() : Decode(payload, expanded);
        if (bank == null) _banks[bankId] = bank = new Bank();
        bank.Blocks.Add((bank.Data.Count, bytes.Length)); bank.Data.AddRange(bytes);
        _total += bytes.Length; _loaded.Add(sourceOffset);
    }
    /// <summary>展開後は8・16ビット、圧縮側は1〜8ビットだけを受け付ける。</summary>
    private static void ValidateBits(byte decoded, byte encoded)
    {
        if (decoded is not (8 or 16) || encoded is < 1 or > 8)
            throw new NotSupportedException("PCM compression bit width outside verified 8/16-bit output, 1–8-bit input");
    }
    /// <summary>展開表（データブロック種別0x7F）を読み込む。対応するのはnビット圧縮の表引きとDPCMだけ。</summary>
    private void LoadTable(ReadOnlySpan<byte> data)
    {
        if (data.Length < 6) throw new InvalidDataException("VGM PCM展開表が途中で切れています。");
        byte type = data[0], subtype = data[1], decoded = data[2], encoded = data[3];
        ValidateBits(decoded, encoded);
        if (!(type == 0 && subtype == 2 || type == 1 && subtype == 0))
            throw new NotSupportedException("PCM table compression type");
        int count = BinaryPrimitives.ReadUInt16LittleEndian(data[4..]), width = decoded / 8;
        if (count > 1 << encoded || data.Length != 6 + count * width)
            throw new InvalidDataException("VGM PCM展開表の長さが不正です。");
        var values = new ushort[count];
        for (int i = 0; i < count; i++) values[i] = width == 1 ? data[6+i] : BinaryPrimitives.ReadUInt16LittleEndian(data[(6+i*2)..]);
        _tables[(type,subtype)] = new(decoded,encoded,values);
    }
    /// <summary>圧縮データを展開する。種類0はnビット圧縮（副種別0: そのまま、1: 左シフト、2: 表引き）、種類1はDPCM。</summary>
    private byte[] Decode(ReadOnlySpan<byte> data, int length)
    {
        // 形式と出力長、ビット列の長さを確かめる。
        byte type = data[0], decoded = data[5], encoded = data[6], subtype = data[7];
        ValidateBits(decoded,encoded);
        if (!(type == 0 && subtype <= 2 || type == 1 && subtype == 0))
            throw new NotSupportedException("PCM compression method");
        int width = decoded / 8, count = length / width;
        if (length % width != 0 || (long)count * encoded > (long)(data.Length-10)*8)
            throw new InvalidDataException("VGM圧縮PCMの出力長またはビット列が不正です。");
        // 表引きとDPCMでは、ビット幅が一致する展開表が必要。
        Table? table = null;
        if (type == 1 || subtype == 2)
        {
            table = _tables.GetValueOrDefault((type,subtype));
            if (table == null || table.DecodedBits != decoded || table.EncodedBits != encoded)
                throw new InvalidDataException("VGM圧縮PCMに対応する展開表がありません。");
        }
        // 先頭から指定ビット数ずつ読み、値に変換して出力する。DPCMは差分を積み上げる。
        int bias = BinaryPrimitives.ReadUInt16LittleEndian(data[8..]), state = bias;
        int mask = (1 << decoded)-1;
        long bit = 0;
        byte[] output = new byte[length];
        for (int i = 0; i < count; i++)
        {
            int code = 0;
            for (int n = 0; n < encoded; n++, bit++) code = (code << 1) | ((data[10+(int)(bit/8)] >> (7-(int)(bit%8))) & 1);
            int value;
            if (table != null)
            {
                if (code >= table.Values.Length) throw new InvalidDataException("VGM PCM展開表の参照範囲が不正です。");
                value = table.Values[code];
                if (type == 1) { state = (state+value) & mask; value = state; }
            }
            else value = ((subtype == 1 ? code << (decoded-encoded) : code)+bias) & mask;
            output[i*width] = (byte)value;
            if (width == 2) output[i*width+1] = (byte)(value >> 8);
        }
        return output;
    }
}
