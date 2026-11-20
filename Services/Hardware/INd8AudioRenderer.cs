using MirrorFlux.Services.Vgm;

namespace MirrorFlux.Services.Hardware;

/// <summary>
/// NanoDrive 8 へ送る OKIM6258 ストリームと、同じ時間軸に乗せる YM2151 書き込みを作る側の共通窓口。
/// VGM（<see cref="NdsifOkiRenderer"/>）と MDX（Music.MdxNd8Renderer）が実装する。
/// 時刻はマイクロ秒、位置は送った ADPCM のバイト数（1バイト＝2サンプル）で数える。
/// </summary>
internal interface INd8AudioRenderer
{
    /// <summary>AUDIO_START で送るクロックと分周。</summary>
    uint InitialClock { get; }
    ushort InitialDivider { get; }
    /// <summary>ここまでに作った音の長さ（マイクロ秒）。</summary>
    ulong TimeUs { get; }
    bool Finished { get; }
    /// <summary>AUDIO_START より前に書く YM2151 の初期設定（アドレスと値の組を並べたもの）。</summary>
    byte[] InitialYmWrites();
    /// <summary>
    /// 次の一塊を作る。
    /// イベントの種類は 0=YM2151書き込み、1=クロック・分周、2=左右、3=終端。
    /// </summary>
    NdsifOkiRenderer.Chunk Next();
}

/// <summary>OKI ADPCM（OKIM6258 / MSM6258）の4ビット符号を復号・符号化する。</summary>
internal struct OkiAdpcmCodec
{
    private static readonly int[] Steps = [16, 17, 19, 21, 23, 25, 28, 31, 34, 37, 41, 45, 50, 55, 60, 66,
        73, 80, 88, 97, 107, 118, 130, 143, 157, 173, 190, 209, 230, 253, 279, 307, 337, 371,
        408, 449, 494, 544, 598, 658, 724, 796, 876, 963, 1060, 1166, 1282, 1411, 1552];
    private static readonly int[] Shifts = [-1, -1, -1, -1, 2, 4, 6, 8];

    internal int Signal, Index;

    internal void Reset() { Signal = -2; Index = 0; }

    /// <summary>4ビット符号を1つ復号し、信号値とステップ位置を進める。</summary>
    internal int Decode(int nibble)
    {
        // 符号の各ビットから差分を組み立てる。
        int step = Steps[Index], diff = step >> 3;
        if ((nibble & 1) != 0) diff += step >> 2;
        if ((nibble & 2) != 0) diff += step >> 1;
        if ((nibble & 4) != 0) diff += step;
        // 信号値を12ビットの範囲に収め、次のステップ位置へ進める。
        Signal = Math.Clamp(Signal + ((nibble & 8) == 0 ? diff : -diff), -2048, 2047);
        Index = Math.Clamp(Index + Shifts[nibble & 7], 0, 48);
        return Signal;
    }

    /// <summary>
    /// 符号を1つ当てたときの信号値（12ビットの範囲に収める前）。
    /// </summary>
    internal readonly int Unclamped(int nibble)
    {
        int step = Steps[Index], diff = step >> 3;
        if ((nibble & 1) != 0) diff += step >> 2;
        if ((nibble & 2) != 0) diff += step >> 1;
        if ((nibble & 4) != 0) diff += step;
        return Signal + ((nibble & 8) == 0 ? diff : -diff);
    }

    /// <summary>
    /// target（-2048〜2047）に最も近づく符号を選び、自分の状態も進める。
    /// 信号値が12ビットの範囲を越える符号は選ばず、差の大きさを1段ずつ下げる（それでも越えるなら逆向きの最小の差）。
    /// 実機の OKIM6258 が範囲の端で値を止めるか折り返すかに関係なく、こちらの予測と実機の値がずれないようにするため。
    /// </summary>
    internal byte Encode(int target)
    {
        // 目標との差を符号ビットと3段階のステップで表す。
        int diff = Math.Clamp(target, -2048, 2047) - Signal;
        int nibble = 0;
        if (diff < 0) { nibble = 8; diff = -diff; }
        int step = Steps[Index];
        if (diff >= step) { nibble |= 4; diff -= step; }
        if (diff >= step >> 1) { nibble |= 2; diff -= step >> 1; }
        if (diff >= step >> 2) nibble |= 1;
        // 範囲を越えるなら、同じ向きのまま差を小さくする。最小の差でも越えるなら向きを逆にする。
        while (Unclamped(nibble) is < -2048 or > 2047)
        {
            if ((nibble & 7) == 0) { nibble ^= 8; break; }
            nibble--;
        }
        // 選んだ符号で復号側と同じ状態に進めておく。
        Decode(nibble);
        return (byte)nibble;
    }
}
