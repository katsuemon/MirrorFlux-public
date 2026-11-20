using MirrorFlux.Services.Music;

/// <summary>MXDRV データ資料・MXDRV 本体と照らし合わせた MDX コマンドの扱いの検査。</summary>
internal static class MdxCommandRegression
{
    public static void Run(Action<bool, string> check)
    {
        // 1音（音符 $A4、音長 10）を鳴らして終わる曲が、終わるまでに要する割り込み回数。
        byte[] plain = [0xfd, 0, 0xa4, 10, 0xf1, 0];
        int baseline = TicksUntilEnd(plain, out int baselineKeyOns);
        check(baseline > 10 && baselineKeyOns == 1, "MDX baseline note plays once and the track ends");

        // 拡張コマンドの引数を正しく読み飛ばせば、前に置いても演奏時間とキーオンの数は変わらない。
        foreach (var (prefix, name) in new (byte[], string)[]
        {
            ([0xe7, 2, 0, 0, 0, 0, 0, 0], "2 (PCM8 direct, 6 bytes)"),
            ([0xe7, 4, 1, 0x05], "4 with a rest"),
            ([0xe7, 4, 1, 0xa0, 0x08], "4 with a note and length"),
            ([0xe7, 5, 3], "5 (length add)"),
            ([0xe7, 6, 1], "6 (flag)"),
        })
        {
            int ticks = TicksUntilEnd([0xfd, 0, .. prefix, 0xa4, 10, 0xf1, 0], out int keyOns);
            check(ticks == baseline && keyOns == 1, $"MDX $E7 sub-command {name} consumes its parameters like MXDRV");
        }

        // ERROR（$E7 $00）と未定義の $E0〜$E6 は、MXDRV と同じくそのトラックを終える。
        foreach (var (commands, name) in new (byte[], string)[]
        {
            ([0xfd, 0, 0xe7, 0, 0xa4, 10, 0xf1, 0], "$E7 $00 (ERROR)"),
            ([0xfd, 0, 0xe0, 0xa4, 10, 0xf1, 0], "undefined $E0"),
            ([0xfd, 0, 0xe6, 0xa4, 10, 0xf1, 0], "undefined $E6"),
        })
        {
            int ticks = TicksUntilEnd(commands, out int keyOns);
            check(ticks < baseline && keyOns == 0, $"MDX {name} ends the track without playing the following note");
        }
    }

    private static int TicksUntilEnd(byte[] commands, out int keyOns)
    {
        int count = 0;
        var core = new MxdrvCore(MdxRegression.CreateScore(commands), null)
        {
            WriteOpmCallback = (register, value) => { if (register == 8 && (value & 7) == 0 && (value & 0x78) != 0) count++; }
        };
        int ticks = 0;
        while (!core.Ended && ticks < 5000) { core.Tick(); ticks++; }
        keyOns = count;
        return ticks;
    }
}
