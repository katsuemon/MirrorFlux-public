using MirrorFlux.Services.Hardware;
using MirrorFlux.Services.Music;
using MirrorFlux.Services.Vgm;

/// <summary>MDX を NanoDrive 8 へ送るストリーム（YM2151 イベントと OKIM6258 ADPCM）の検査。実機は使わない。</summary>
internal static class MdxNd8Regression
{
    public static void Run(Action<bool, string> check)
    {
        // PDX の 36 番に、大きく振れる ADPCM を 64 バイト置く。
        byte[] bank = new byte[768 + 64];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bank.AsSpan(36 * 8), 768);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bank.AsSpan(36 * 8 + 4), 64);
        for (int i = 768; i < bank.Length; i++) bank[i] = (byte)((i & 1) == 0 ? 0x77 : 0xff);
        byte[] score = MdxRegression.CreateScore([0xfd, 0, 0xfb, 15, 0xfc, 3, 0xa4, 60, 0xf1, 0], 9);

        var renderer = new MdxNd8Renderer(score, bank);
        byte[] initial = renderer.InitialYmWrites();
        bool initialHasKeyOn = false;
        for (int i = 0; i + 1 < initial.Length; i += 2) initialHasKeyOn |= initial[i] == 8 && (initial[i + 1] & 0x78) != 0;
        check(initial.Length > 0 && initial.Length % 2 == 0 && !initialHasKeyOn,
            "MDX ND8 sends voice setup before AUDIO_START and keeps key-ons in the timed stream");
        check(renderer.InitialClock == 8_000_000 && renderer.InitialDivider == 512,
            "MDX ND8 drives OKIM6258 at the X68000 8 MHz / 512 rate");

        var data = new List<byte>(); var events = new List<NdsifOkiRenderer.AudioEvent>();
        uint expected = 0; bool contiguous = true, timed = true;
        for (int guard = 0; guard < 20000 && !renderer.Finished; guard++)
        {
            var chunk = renderer.Next();
            contiguous &= chunk.Position == expected;
            expected += (uint)chunk.Data.Length;
            data.AddRange(chunk.Data); events.AddRange(chunk.Events);
            timed &= chunk.EndTimeUs == (ulong)expected * 128;
        }
        check(renderer.Finished && contiguous && timed, "MDX ND8 stream is contiguous, 128 us per byte, and ends after the score");
        check(events.Any(e => e.Kind == 0 && e.Payload[0] == 8 && (e.Payload[1] & 0x78) != 0),
            "MDX ND8 sends YM2151 key-on events at their stream positions");
        check(events.Count(e => e.Kind == 3) == 1 && events.Last().Kind == 3 && events.All(e => e.Position <= expected),
            "MDX ND8 ends with one terminal event after all other events");

        var decoder = new OkiAdpcmCodec(); decoder.Reset();
        int peak = 0;
        foreach (byte value in data) { peak = Math.Max(peak, Math.Abs(decoder.Decode(value & 15))); peak = Math.Max(peak, Math.Abs(decoder.Decode(value >> 4))); }
        check(peak > 200, "MDX ND8 re-encodes the PCM8 mix into audible OKIM6258 ADPCM");

        var silent = new MdxNd8Renderer(score, null);
        var silentData = new List<byte>();
        for (int guard = 0; guard < 20000 && !silent.Finished; guard++) silentData.AddRange(silent.Next().Data);
        var silentDecoder = new OkiAdpcmCodec(); silentDecoder.Reset();
        int silentPeak = 0;
        foreach (byte value in silentData) { silentPeak = Math.Max(silentPeak, Math.Abs(silentDecoder.Decode(value & 15))); silentPeak = Math.Max(silentPeak, Math.Abs(silentDecoder.Decode(value >> 4))); }
        check(silent.Finished && silentPeak < 16, "MDX ND8 without PDX keeps the OKIM6258 stream silent while YM2151 plays");

        var seeked = new MdxNd8Renderer(score, bank);
        seeked.SeekToSample(4410);
        byte[] state = seeked.InitialYmWrites();
        bool hasVoice = false;
        for (int i = 0; i + 1 < state.Length; i += 2) hasVoice |= state[i] is >= 0x20 and <= 0x27;
        check(seeked.TimeUs == 0 && hasVoice && seeked.Next().Position == 0,
            "MDX ND8 started mid-song restarts its stream at zero and replays the current YM2151 state");

        // 1音だけ・15.625 kHz・等倍・左右両方の PCM8 は、元の ADPCM をそのまま送る（作り直しの雑音を出さない）。
        var mixer = new PdxPcmMixer(15625);
        byte[] gentle = new byte[768 + 64];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(gentle.AsSpan(36 * 8), 768);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(gentle.AsSpan(36 * 8 + 4), 64);
        for (int i = 768; i < gentle.Length; i++) gentle[i] = 0x81;
        mixer.Start(2, gentle, 768, 64, 4, 3, 8);
        check(mixer.TryGetSoloAdpcm(out int soloChannel, out int soloPosition, out int soloCount) && soloChannel == 2 && soloPosition == 0 && soloCount == 128 &&
            mixer.AdpcmNibble(2, 0) == 1 && mixer.AdpcmNibble(2, 1) == 8, "PCM8 mixer reports a lone native-rate unity voice and its original ADPCM nibbles");
        mixer.Start(3, gentle, 768, 64, 4, 3, 8);
        check(!mixer.TryGetSoloAdpcm(out _, out _, out _), "Two PCM8 voices are not passed through");
        mixer.Stop(3); mixer.Start(2, gentle, 768, 64, 3, 3, 8);
        check(!mixer.TryGetSoloAdpcm(out _, out _, out _), "A PCM8 voice at another sample rate is re-encoded");
        bool passedAny = false, passedExact = false;
        for (int volume = 0; volume < 16 && !passedAny; volume++)
        {
            var solo = new MdxNd8Renderer(MdxRegression.CreateScore([0xfd, 0, 0xfb, (byte)volume, 0xfc, 3, 0xa4, 60, 0xf1, 0], 9), gentle);
            var soloData = new List<byte>();
            while (!solo.Finished && solo.TimeUs < 2_000_000) soloData.AddRange(solo.Next().Data);
            if (solo.PassedSamples == 0) continue;
            passedAny = true;
            int run = 0, best = 0;
            foreach (byte value in soloData) { run = value == 0x81 ? run + 1 : 0; best = Math.Max(best, run); }
            passedExact = best >= 63;
        }
        check(passedAny && passedExact, "MDX ND8 sends a lone PCM8 voice's original ADPCM bytes unchanged");

        // 全振幅の矩形波・のこぎり波でも、ADPCM の信号値が12ビットの範囲を越える符号を出さない
        // （実機が端で値を止めても折り返しても、こちらの予測と同じ値になる）。
        var codec = new OkiAdpcmCodec(); codec.Reset();
        int wrapSignal = -2; var wrapIndexCodec = new OkiAdpcmCodec(); wrapIndexCodec.Reset();
        bool inRange = true, matches = true; double error = 0, power = 0;
        for (int i = 0; i < 20000; i++)
        {
            int target = (i / 40 % 2 == 0 ? 2047 : -2048) * (i < 10000 ? 1 : 0) + (i >= 10000 ? (i % 97) * 42 - 2048 : 0);
            var before = codec;
            int nibble = codec.Encode(target);
            int raw = before.Unclamped(nibble);
            inRange &= raw is >= -2048 and <= 2047;
            // 折り返す復号器でも同じ値になること。
            wrapSignal = ((wrapIndexCodec.Unclamped(nibble) + 2048) & 4095) - 2048; wrapIndexCodec.Decode(nibble);
            matches &= wrapSignal == codec.Signal;
            error += (double)(target - codec.Signal) * (target - codec.Signal); power += (double)target * target;
        }
        // 全振幅の急な段差は ADPCM の追従が間に合わないので、SN比は約5 dB。音を追っていることだけ確かめる。
        check(inRange && matches && 10 * Math.Log10(power / error) > 3,
            "OKIM6258 ADPCM encoder never relies on the decoder clamping at full scale (wrapping and clamping decoders agree)");
    }
}
