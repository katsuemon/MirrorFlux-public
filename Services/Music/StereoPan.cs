namespace MirrorFlux.Services.Music;

/// <summary>
/// 表示用の左右の値（ビット1＝左、ビット0＝右。3は両方、0は出力なし）への変換。
/// 音源によって左右のビットの並びが違うので、レジスタの値をそのまま使わずにここで直す。
/// </summary>
internal static class StereoPan
{
    /// <summary>YM2151 の 0x20〜0x27（ビット7＝右、ビット6＝左。ymfm の出力0＝左と同じ）から。</summary>
    internal static int FromOpm(int register) => ((register >> 5) & 2) | ((register >> 7) & 1);

    /// <summary>ビット0＝左、ビット1＝右の値（MXDRV の p、PCM8 の出力マスク）から。</summary>
    internal static int FromLeftInBit0(int value) => ((value & 1) << 1) | ((value >> 1) & 1);
}