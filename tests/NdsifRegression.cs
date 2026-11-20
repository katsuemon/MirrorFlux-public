using System.IO;
using System.Text;
using MirrorFlux.Models;
using MirrorFlux.Services;
using MirrorFlux.Services.Hardware;
using MirrorFlux.Services.Music;
using MirrorFlux.Services.Vgm;

namespace MirrorFlux.Tests;

public static class NdsifRegression
{
    public static void Run(Action<bool, string> check)
    {
        // 1. CRC-16/CCITT-FALSE verification ("123456789" -> 0x29B1)
        byte[] testAscii = Encoding.ASCII.GetBytes("123456789");
        ushort crc = NdsifService.ComputeCrc16(testAscii);
        check(crc == 0x29B1, $"CRC-16/CCITT-FALSE matches 0x29B1 test vector (got 0x{crc:X4})");

        // 2. COBS encoding & decoding roundtrips
        byte[][] cobsTestVectors = [
            [0x00],
            [0x00, 0x00],
            [0x01, 0x02, 0x03, 0x04],
            [0x11, 0x22, 0x00, 0x33],
            [0x00, 0x55, 0xAA, 0x00, 0xFF],
            Enumerable.Range(1, 254).Select(i => (byte)i).ToArray(),
            Enumerable.Range(0, 256).Select(i => (byte)i).ToArray()
        ];

        Span<byte> decoded = stackalloc byte[512];
        foreach (var original in cobsTestVectors)
        {
            byte[] encoded = NdsifService.CobsEncode(original);
            check(!encoded.Contains((byte)0x00), "COBS encoded stream contains no zero bytes");
            bool ok = NdsifService.TryCobsDecode(encoded, decoded, out int decodedLen);
            check(ok && decodedLen == original.Length && original.SequenceEqual(decoded[..decodedLen].ToArray()),
                $"COBS roundtrip matches for length {original.Length}");
        }

        // 3. NDSIF frame creation & parsing (GET_INFO)
        byte[] getInfoPacket = NdsifService.BuildFramedPacket(0x01, 0x02, 100, ReadOnlySpan<byte>.Empty);
        check(getInfoPacket[0] == 0x00 && getInfoPacket[^1] == 0x00, "Framed packet delimited with 0x00 bytes");
        check(NdsifService.TryParseFrame(getInfoPacket, out var parsedGetInfo), "GET_INFO frame parses successfully");
        check(parsedGetInfo.MessageType == 0x01 && parsedGetInfo.CommandId == 0x02 && parsedGetInfo.TransactionId == 100 && parsedGetInfo.Payload.Length == 0,
            "GET_INFO header fields match request");

        // 4. Mock response parsing (NanoDrive 6.1)
        string deviceName = "NanoDrive 6.1";
        string fwVersion = "1.0.0";
        byte[] nameBytes = Encoding.UTF8.GetBytes(deviceName);
        byte[] fwBytes = Encoding.UTF8.GetBytes(fwVersion);
        byte[] respPayload = new byte[1 + 1 + nameBytes.Length + 1 + fwBytes.Length];
        respPayload[0] = 0x00; // Status OK
        respPayload[1] = (byte)nameBytes.Length;
        Buffer.BlockCopy(nameBytes, 0, respPayload, 2, nameBytes.Length);
        respPayload[2 + nameBytes.Length] = (byte)fwBytes.Length;
        Buffer.BlockCopy(fwBytes, 0, respPayload, 3 + nameBytes.Length, fwBytes.Length);

        byte[] respPacket = NdsifService.BuildFramedPacket(0x02, 0x02, 100, respPayload);
        check(NdsifService.TryParseFrame(respPacket, out var parsedResp), "Response packet parses successfully");
        check(parsedResp.MessageType == 0x02 && parsedResp.CommandId == 0x02, "Response message_type and command_id match");
        check(parsedResp.Payload.Length >= 2 && parsedResp.Payload[0] == 0x00, "Response status is 0x00 (OK)");
        string parsedName = Encoding.UTF8.GetString(parsedResp.Payload, 2, parsedResp.Payload[1]);
        check(parsedName == "NanoDrive 6.1", $"Parsed device name matches 'NanoDrive 6.1' (got '{parsedName}')");

        // 5. SET_CHIP_CLOCK (0x04) frame structure
        uint ymClock = 7670453;
        byte[] clockPayload = [
            0x52, // write_id (YM2612)
            0x00, // instance 0
            (byte)(ymClock & 0xFF),
            (byte)((ymClock >> 8) & 0xFF),
            (byte)((ymClock >> 16) & 0xFF),
            (byte)((ymClock >> 24) & 0xFF)
        ];
        byte[] clockPacket = NdsifService.BuildFramedPacket(0x00, 0x04, 0, clockPayload);
        check(NdsifService.TryParseFrame(clockPacket, out var parsedClock), "SET_CHIP_CLOCK packet parses successfully");
        check(parsedClock.CommandId == 0x04 && parsedClock.Payload.SequenceEqual(clockPayload),
            "SET_CHIP_CLOCK payload matches 7670453 Hz");

        // 6. YM2612 register writes (0x52 page 0, 0x53 page 1)
        byte[] writePage0Payload = [0x00, 0x00, 0x22, 0x08]; // instance 0, page 0, reg 0x22, val 0x08
        byte[] writePage0Packet = NdsifService.BuildFramedPacket(0x00, 0x52, 0, writePage0Payload);
        check(NdsifService.TryParseFrame(writePage0Packet, out var parsedWrite0), "YM2612 page 0 write parses successfully");
        check(parsedWrite0.CommandId == 0x52 && parsedWrite0.Payload.SequenceEqual(writePage0Payload),
            "YM2612 page 0 payload matches [0, 0, 0x22, 0x08]");

        byte[] writePage1Payload = [0x00, 0x01, 0xB4, 0xC0]; // instance 0, page 1, reg 0xB4, val 0xC0
        byte[] writePage1Packet = NdsifService.BuildFramedPacket(0x00, 0x53, 0, writePage1Payload);
        check(NdsifService.TryParseFrame(writePage1Packet, out var parsedWrite1), "YM2612 page 1 write parses successfully");
        check(parsedWrite1.CommandId == 0x53 && parsedWrite1.Payload.SequenceEqual(writePage1Payload),
            "YM2612 page 1 payload matches [0, 1, 0xB4, 0xC0]");

        // 7. Testing NdsifService with mock stream
        using var memoryStream = new MemoryStream();
        var ndsif = NdsifService.Instance;
        ndsif.AttachStreamForTesting(memoryStream, "NanoDrive 6.1", "COM_MOCK");
        check(ndsif.IsConnected && ndsif.DeviceName == "NanoDrive 6.1" && ndsif.PortName == "COM_MOCK",
            "NdsifService successfully attaches mock stream");

        ndsif.Reset();
        byte[] resetSent = memoryStream.ToArray();
        check(resetSent.Length > 0 && NdsifService.TryParseFrame(resetSent, out var parsedReset) && parsedReset.CommandId == 0x00,
            "NdsifService.Reset writes valid RESET packet");

        memoryStream.SetLength(0);
        ndsif.SetChipClock(0x52, 0, 7670453);
        byte[] clockSent = memoryStream.ToArray();
        check(clockSent.Length > 0 && NdsifService.TryParseFrame(clockSent, out var parsedClockSent) && parsedClockSent.CommandId == 0x04,
            "NdsifService.SetChipClock writes valid clock packet");

        memoryStream.SetLength(0);
        ndsif.WriteYm2612(0, 0x28, 0xF0);
        byte[] writeSent = memoryStream.ToArray();
        check(writeSent.Length > 0 && NdsifService.TryParseFrame(writeSent, out var parsedYmWrite) && parsedYmWrite.CommandId == 0x52 && parsedYmWrite.Payload[2] == 0x28,
            "NdsifService.WriteYm2612 writes valid 0x52 packet");

        memoryStream.SetLength(0);
        ndsif.WriteSn76489(0, 0x80);
        byte[] psg1Sent = memoryStream.ToArray();
        check(psg1Sent.Length > 0 && NdsifService.TryParseFrame(psg1Sent, out var parsedPsg1) && parsedPsg1.CommandId == 0x50 && parsedPsg1.Payload[0] == 0 && parsedPsg1.Payload[2] == 0x80,
            "NdsifService.WriteSn76489 chip 1 writes valid 0x50 packet");

        memoryStream.SetLength(0);
        ndsif.WriteSn76489(1, 0x90);
        byte[] psg2Sent = memoryStream.ToArray();
        check(psg2Sent.Length > 0 && NdsifService.TryParseFrame(psg2Sent, out var parsedPsg2) && parsedPsg2.CommandId == 0x30 && parsedPsg2.Payload[0] == 1 && parsedPsg2.Payload[2] == 0x90,
            "NdsifService.WriteSn76489 chip 2 writes valid 0x30 packet");

        ndsif.StartClock(0);
        ndsif.PauseClock();
        ndsif.ResumeClock();
        ndsif.ResetClock(0);
        check(true, "NdsifService clock lifecycle methods operate without error");

        ndsif.Disconnect();
        check(!ndsif.IsConnected, "NdsifService disconnects cleanly");

        // 8. Settings ini parsing & serialization
        string tempIni = Path.Combine(Path.GetTempPath(), $"mf_test_{Guid.NewGuid():N}.ini");
        try
        {
            File.WriteAllText(tempIni, "%ND6-NDSIF\ttrue\n");
            var loaded = new SettingsStore(tempIni).Load().Settings;
            check(loaded.Nd6Ndsif, "SettingsStore parses %ND6-NDSIF true");

            File.WriteAllText(tempIni, "%ND6-NDSIF\tfalse\n%ND7-NDSIF\ttrue\n%ND8-NDSIF\ttrue\n");
            loaded = new SettingsStore(tempIni).Load().Settings;
            check(!loaded.Nd6Ndsif, "SettingsStore parses %ND6-NDSIF false");
            check(loaded.Nd7Ndsif && loaded.Nd8Ndsif, "SettingsStore parses ND7 and ND8 flags");

            loaded.Nd6Ndsif = true;
            loaded.Nd7Ndsif = false;
            loaded.Nd8Ndsif = false;
            new SettingsStore(tempIni).TrySave(loaded, out _);
            string savedContent = File.ReadAllText(tempIni);
            check(savedContent.Contains("%ND6-NDSIF\ttrue"), "SettingsStore serializes %ND6-NDSIF true");
            check(savedContent.Contains("%ND7-NDSIF\tfalse") && savedContent.Contains("%ND8-NDSIF\tfalse"),
                "SettingsStore serializes ND7 and ND8 flags");
        }
        finally
        {
            if (File.Exists(tempIni)) File.Delete(tempIni);
        }

        // 9. ND channel snapshot & keyboard lamp colors
        var ndChannel = new MusicChannelSnapshot { Id = "YM2612:0", ChipName = "YM2612", IsHardwareOutput = true, IsKeyOn = true, NoteNumber = 60 };
        var swChannel = new MusicChannelSnapshot { Id = "OKIM6258:0", ChipName = "OKIM6258", IsHardwareOutput = false, IsKeyOn = true, NoteNumber = 72 };
        check(ndChannel.IsHardwareOutput && !swChannel.IsHardwareOutput, "MusicChannelSnapshot tracks hardware output status");

        // 10. Dual SN76489 Extra Header clock extraction (Sega System 1/2 arcade dual PSG)
        byte[] dualPsgVgm = new byte[0x100];
        dualPsgVgm[0] = 0x56; dualPsgVgm[1] = 0x67; dualPsgVgm[2] = 0x6d; dualPsgVgm[3] = 0x20; // "Vgm "
        dualPsgVgm[8] = 0x70; dualPsgVgm[9] = 0x01; // Version 1.70
        // Sn76489Clock = 2,000,000 Hz | Dual bit (0x40000000) = 0x401E8480
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(dualPsgVgm.AsSpan(0x0C, 4), 0x401E8480);
        // Data offset: at 0x80 (rel = 0x80 - 0x34 = 0x4C)
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(dualPsgVgm.AsSpan(0x34, 4), 0x4C);
        // Extra header at 0xC0 (rel = 0xC0 - 0xBC = 4)
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(dualPsgVgm.AsSpan(0xBC, 4), 0x04);
        // Extra header @ 0xC0:
        // Size = 8
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(dualPsgVgm.AsSpan(0xC0, 4), 0x08);
        // ChpClockOffset = 4 (points to 0xC0 + 4 + 4 = 0xC8)
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(dualPsgVgm.AsSpan(0xC4, 4), 0x04);
        // Clock table @ 0xC8:
        dualPsgVgm[0xC8] = 0x01; // entry count = 1
        dualPsgVgm[0xC9] = 0x00; // chip ID 0 (SN76489)
        // Clock = 4,000,000 Hz (0x003D0900)
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(dualPsgVgm.AsSpan(0xCA, 4), 4000000);
        dualPsgVgm[0x80] = 0x66; // End of sound data

        var loadedDualPsg = MirrorFlux.Services.Vgm.VgmFile.Load(dualPsgVgm, "DualPsgTest");
        check(loadedDualPsg != null && loadedDualPsg.HasSecondSn76489, "Dual SN76489 flag recognized");
        check((loadedDualPsg!.Sn76489Clock & 0x3FFFFFFF) == 2000000, "Primary SN76489 clock is 2,000,000 Hz");
        check(loadedDualPsg.SecondSn76489Clock == 4000000, $"Secondary SN76489 clock parsed from Extra Header is 4,000,000 Hz (got {loadedDualPsg.SecondSn76489Clock})");

        var monitor = new MirrorFlux.Services.Vgm.VgmChannelMonitor(loadedDualPsg);
        // Tone 0 freq divider = 100: low nibble 4, high 6 -> 0x064 = 100
        monitor.WritePsg(0x80 | 0x04, 0);
        monitor.WritePsg(0x06, 0);
        monitor.WritePsg(0x90, 0); // Vol max
        monitor.WritePsg(0x80 | 0x04, 1);
        monitor.WritePsg(0x06, 1);
        monitor.WritePsg(0x90, 1); // Vol max
        var snap = monitor.Capture(new VgmVisualSnapshot { IsPlaying = true });
        var ch0 = snap.FirstOrDefault(c => c.Id == "SN76489:0");
        var ch1 = snap.FirstOrDefault(c => c.Id == "SN76489 #2:0");
        check(ch0 != null && ch1 != null, "Channels exist for both PSG instances");
        // inst 0 freq: 2,000,000 / (32 * 100) = 625 Hz
        // inst 1 freq: 4,000,000 / (32 * 100) = 1250 Hz (1 octave higher, correct pitch for 4MHz chip 2)
        check(Math.Abs(ch0!.Frequency!.Value - 625.0) < 1.0, $"Inst 0 PSG frequency is ~625Hz (got {ch0.Frequency})");
        check(Math.Abs(ch1!.Frequency!.Value - 1250.0) < 1.0, $"Inst 1 PSG frequency is ~1250Hz (got {ch1.Frequency})");

        // 11. Timed DAC Stream (0x19 DAC_DATA, 0x1D REGISTER_EVENT, 0x1A DAC_START, 0x1B DAC_END)
        using var timedStream = new MemoryStream();
        ndsif.AttachStreamForTesting(timedStream, "NanoDrive 6.1", "COM_TIMED", supportsTimedDac: true);
        ndsif.StartClock(0);

        // Tick 0 setup: 0x2B = 0x8F (should mask low 7 bits to 0x80), 0x22 = 0x08 (immediate at tick 0)
        ndsif.WriteYm2612(0, 0x22, 0x08, 0);
        ndsif.WriteYm2612(0, 0x2B, 0x8F, 0);
        ndsif.WriteYm2612(0, 0x2A, 0x90, 0);
        // Interleaved DAC and FM writes
        ndsif.WriteYm2612(0, 0x2A, 0xA0, 4);
        ndsif.WriteYm2612(0, 0xA4, 0x22, 6);
        ndsif.WriteYm2612(0, 0xA0, 0x69, 6);
        ndsif.WriteSn76489(0, 0x9F, 6);
        ndsif.WriteYm2612(0, 0x2A, 0xB0, 8);
        ndsif.NotifySourceEnded(16);

        var waitSw = System.Diagnostics.Stopwatch.StartNew();
        while (waitSw.ElapsedMilliseconds < 250)
        {
            System.Threading.Thread.Sleep(5);
            lock (ndsif) { }
            byte[] snapBytes = timedStream.ToArray();
            var snapMsgs = ParseMultipleFrames(snapBytes);
            if (snapMsgs.Any(m => m.CommandId == 0x1B)) break;
        }

        var timedMsgs = ParseMultipleFrames(timedStream.ToArray());
        ndsif.Disconnect();

        check(timedMsgs.Any(m => m.CommandId == 0x52), "Tick-0 non-DAC YM2612 write sent as immediate 0x52 before stream starts");
        check(timedMsgs.Any(m => m.CommandId == 0x19), "Timed DAC emits 0x19 (DAC_DATA) frames");
        check(timedMsgs.Any(m => m.CommandId == 0x1D), "Timed DAC emits 0x1D (REGISTER_EVENT) frames for interleaved FM/PSG");
        check(timedMsgs.Any(m => m.CommandId == 0x1A), "Timed DAC emits 0x1A (DAC_START)");
        check(timedMsgs.Any(m => m.CommandId == 0x1B), "Timed DAC emits 0x1B (DAC_END) when source ends");

        // Validate ND6 dac_stream.cpp invariants across all emitted frames
        ValidateNd6DacStreamInvariants(timedMsgs, check, "Synthetic Timed DAC");

        // 12. Alien Soldier (03 - Flashback.vgz) Timed DAC Stream invariant & throughput test
        // Optional local sample: set MIRRORFLUX_ALIEN_SOLDIER_ZIP to the VGMRips "Alien Soldier" zip to run this check.
        string alienZip = Environment.GetEnvironmentVariable("MIRRORFLUX_ALIEN_SOLDIER_ZIP") ?? "";
        if (File.Exists(alienZip))
        {
            using var archive = System.IO.Compression.ZipFile.OpenRead(alienZip);
            var entry = archive.GetEntry("03 - Flashback.vgz") ?? archive.Entries.FirstOrDefault(e => e.Name.EndsWith(".vgz", StringComparison.OrdinalIgnoreCase));
            if (entry != null)
            {
                using var es = entry.Open();
                using var ms = new MemoryStream();
                es.CopyTo(ms);
                var alienVgm = VgmFile.Load(ms.ToArray(), entry.FullName);
                check(alienVgm != null, "Loaded Alien Soldier VGM from archive");

                using var alienStream = new MemoryStream();
                ndsif.AttachStreamForTesting(alienStream, "NanoDrive 6.1", "COM_ALIEN", supportsTimedDac: true);
                ndsif.StartClock(0);

                using var provider = new VgmRegisterPlaybackMonitor(alienVgm!, isNd6Active: true);
                // Render ~3500 samples (~80ms, within first prebuffer window)
                provider.Advance(1024);
                provider.Advance(1024);
                provider.Advance(1024);
                ndsif.NotifySourceEnded(3072);

                var sw2 = System.Diagnostics.Stopwatch.StartNew();
                while (sw2.ElapsedMilliseconds < 300)
                {
                    System.Threading.Thread.Sleep(5);
                    if (ParseMultipleFrames(alienStream.ToArray()).Any(m => m.CommandId == 0x1B))
                        break;
                }

                var alienMsgs = ParseMultipleFrames(alienStream.ToArray());
                ndsif.Disconnect();

                check(alienMsgs.Count(m => m.CommandId == 0x19) > 0, "Alien Soldier emits 0x19 DAC_DATA packets");
                ValidateNd6DacStreamInvariants(alienMsgs, check, "Alien Soldier (03 - Flashback)");
            }
        }

        // 13. High-density FM/PSG register burst (>700 writes in <500 samples) never overflows ND6 EVENT_CAPACITY (512)
        using var burstStream = new MemoryStream();
        ndsif.AttachStreamForTesting(burstStream, "NanoDrive 6.1", "COM_BURST", supportsTimedDac: true);
        ndsif.StartClock(0);

        for (uint s = 1; s <= 100; s++)
        {
            ndsif.WriteYm2612(0, 0x2A, (byte)(0x80 + (s & 0x1F)), s * 4);
            // 8 register writes per 4-sample step = 800 FM/PSG writes in 400 samples!
            for (byte r = 0x30; r < 0x37; r++)
            {
                ndsif.WriteYm2612(0, r, (byte)(s + r), s * 4);
            }
            ndsif.WriteSn76489(0, (byte)(0x80 | (s & 0x0F)), s * 4);
        }
        ndsif.NotifySourceEnded(404);

        var swBurst = System.Diagnostics.Stopwatch.StartNew();
        while (swBurst.ElapsedMilliseconds < 400)
        {
            System.Threading.Thread.Sleep(5);
            if (ParseMultipleFrames(burstStream.ToArray()).Any(m => m.CommandId == 0x1B))
                break;
        }

        var burstMsgs = ParseMultipleFrames(burstStream.ToArray());
        ndsif.Disconnect();

        ValidateNd6DacStreamInvariants(burstMsgs, check, "High-density 800-event burst");
        int totalEventsInPrebuffer = burstMsgs
            .TakeWhile(m => m.CommandId != 0x1A)
            .Where(m => m.CommandId == 0x1D)
            .Sum(m => m.Payload[9] is 0x50 or 0x30 ? (m.Payload.Length - 12) : (m.Payload.Length - 12) / 2);
        check(totalEventsInPrebuffer <= 512, $"Prebuffer 0x1D event count stays within ND6 EVENT_CAPACITY=512 (got {totalEventsInPrebuffer})");

        // 13. 書き込みに失敗した接続は切断扱いにする（機器を抜いた・応答しない場合）。
        using (var deadStream = new FailingStream(failAfterBytes: 0))
        {
            ndsif.AttachStreamForTesting(deadStream, "NanoDrive 6.1", "COM_DEAD");
            ndsif.WriteYm2612(0, 0x22, 0x08);
            check(!ndsif.IsConnected, "Immediate write failure marks the link as lost");
            int attempts = deadStream.WriteAttempts;
            ndsif.WriteYm2612(0, 0x22, 0x08);
            ndsif.WriteSn76489(0, 0x9F);
            check(deadStream.WriteAttempts == attempts, "No further writes are attempted on a lost link");
            ndsif.Disconnect();
        }

        // 送信スレッド上の書き込み失敗（DAC_STARTなど）でプロセスを落とさない。
        using (var dyingStream = new FailingStream(failAfterBytes: 64))
        {
            ndsif.AttachStreamForTesting(dyingStream, "NanoDrive 6.1", "COM_DYING", supportsTimedDac: true);
            ndsif.StartClock(0);
            for (uint i = 0; i < 4000; i++) ndsif.WriteYm2612(0, 0x2A, (byte)(0x80 + (i & 0x3F)), i * 2);
            ndsif.NotifySourceEnded(8000);
            var lostSw = System.Diagnostics.Stopwatch.StartNew();
            while (ndsif.IsConnected && lostSw.ElapsedMilliseconds < 3000) System.Threading.Thread.Sleep(5);
            check(!ndsif.IsConnected && dyingStream.WriteAttempts > 0, "TX worker write failure marks the link as lost without crashing");
            ndsif.Disconnect();
        }

        // 切断後も、つなぎ直せば通常どおり送信できる。
        using (var freshStream = new MemoryStream())
        {
            ndsif.AttachStreamForTesting(freshStream, "NanoDrive 6.1", "COM_FRESH");
            ndsif.WriteYm2612(0, 0x22, 0x08);
            check(ndsif.IsConnected && ParseMultipleFrames(freshStream.ToArray()).Any(m => m.CommandId == 0x52), "A new connection works after a lost link");
            ndsif.Disconnect();
        }

        // 機器が見つからなかった直後は、同じポート構成での再探査を繰り返さない。
        var failedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        check(NdsifService.ShouldSkipDetect("COM3|", "COM3|", failedAt, failedAt.AddSeconds(5)), "Detection is not repeated right after a failure on the same ports");
        check(!NdsifService.ShouldSkipDetect("COM3|COM7|", "COM3|", failedAt, failedAt.AddSeconds(5)), "A changed port list is probed immediately");
        check(!NdsifService.ShouldSkipDetect("COM3|", "COM3|", failedAt, failedAt + NdsifService.DetectRetryInterval), "Detection is retried after the interval");
        check(!NdsifService.ShouldSkipDetect("COM3|", null, default, failedAt), "The first detection is never skipped");
        check(!NdsifService.CanUseNd6UsbFallback(true, "NanoDrive 8") &&
            !NdsifService.CanUseNd6UsbFallback(true, "NanoDrive 7"),
            "ND6 VID/PID fallback does not seize identified ND7/ND8 ports");
        check(NdsifService.CanUseNd6UsbFallback(true, null) &&
            !NdsifService.CanUseNd6UsbFallback(false, null),
            "ND6 fallback only uses a matching unidentified USB port");

        // 複数台の同時利用。機種の分かったポートは、他機種の探査で開かない。
        string[] allPorts = ["COM3", "COM4", "COM6", "COM7", "COM8"];
        string[] usbPorts = ["COM6", "COM7", "COM8"];
        var known = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        check(NdsifPortCatalog.Plan("NanoDrive 8", allPorts, usbPorts, [], known, out int knownTargets)
                .SequenceEqual(["COM6", "COM7", "COM8", "COM3", "COM4"]) && knownTargets == 0,
            "Unidentified NanoDrive USB ports are probed before other serial ports");
        known["COM6"] = "NanoDrive 6.1"; known["COM7"] = "NanoDrive 8"; known["COM3"] = "";
        check(NdsifPortCatalog.Plan("NanoDrive 8", allPorts, usbPorts, [], known, out knownTargets)
                .SequenceEqual(["COM7", "COM8", "COM4"]) && knownTargets == 1,
            "A known ND8 port is tried first, and ports known as other devices are not opened");
        check(NdsifPortCatalog.Plan("NanoDrive 7", allPorts, usbPorts, ["com8"], known, out knownTargets)
                .SequenceEqual(["COM4"]) && knownTargets == 0,
            "ND7 detection leaves the ND6/ND8 ports and ports already in use alone");
        check(NdsifPortCatalog.Plan("NanoDrive 6", allPorts, usbPorts, [], known, out knownTargets)
                .SequenceEqual(["COM6", "COM8", "COM4"]) && knownTargets == 1,
            "ND6 detection goes to its known port without opening the ND8 port");

        NdsifPortCatalog.Reset();
        // 実在のポート名（このPCのBluetoothなど）と重ならない番号を使う。
        string[] fakePorts = ["COM93", "COM94", "COM96", "COM97", "COM98"], fakeUsb = ["COM96", "COM97", "COM98"];
        NdsifPortCatalog.Remember(fakePorts, "COM97", "NanoDrive 8", true);
        NdsifPortCatalog.Remember(fakePorts, "COM93", null, false);
        NdsifPortCatalog.Remember(fakePorts, "COM98", null, true);
        check(NdsifPortCatalog.Plan("NanoDrive 7", fakePorts, fakeUsb, [], out _).SequenceEqual(["COM96", "COM98", "COM94"]),
            "A silent non-USB port is skipped, while a silent NanoDrive USB port is probed again");
        check(NdsifPortCatalog.Plan("NanoDrive 7", ["COM93", "COM97"], ["COM97"], [], out _).SequenceEqual(["COM97", "COM93"]),
            "A changed port list discards the remembered devices");
        NdsifPortCatalog.Reset();
        // Bluetoothの仮想COMポートは開かない（開くと相手機器への接続を待って十数秒止まる）。
        var skipSet = NdsifPortCatalog.SkipSet(["COM9"], ["COM6", "COM7"], ["COM3", "com4", "COM7"]);
        check(skipSet.SetEquals(["COM9", "COM3", "COM4"]) && !skipSet.Contains("COM7"),
            "Bluetooth serial ports are never probed unless they carry the NanoDrive USB ID");
        check(NdsifPortCatalog.Plan("NanoDrive 8", ["COM3", "COM4", "COM6"], ["COM6"], skipSet,
                new Dictionary<string, string>(), out _).SequenceEqual(["COM6"]),
            "Probe order leaves out the Bluetooth ports");

        // ND7/ND8 register routing follows the write IDs, instances and pages in serialtestnd8.
        check(NdsifRegisterRouting.TryMap(new IndependentVgmProgram.Command(0xa5, 0, 3, 6, 1, 0, 0x28, 0xf0), out var ym2203) &&
            ym2203.WriteId == 0xa5 && ym2203.Instance == 1 && ym2203.Page == 0,
            "ND7 second YM2203 is routed as A5/instance 1");
        check(NdsifRegisterRouting.TryMap(new IndependentVgmProgram.Command(0xaf, 0, 3, 12, 1, 1, 0xb0, 0x20), out var opl3) &&
            opl3.WriteId == 0x5f && opl3.Instance == 1 && opl3.Page == 1,
            "ND7 second YMF262 port 1 uses 5F/instance 1/page 1");
        check(NdsifRegisterRouting.TryMap(new IndependentVgmProgram.Command(0xaa, 0, 3, 9, 1, 0, 0xb0, 0x20), out var opl2) &&
            opl2.WriteId == 0x5a && opl2.Instance == 1,
            "ND7 second YM3812 uses 5A/instance 1");
        check(NdsifRegisterRouting.TryMap(new IndependentVgmProgram.Command(0x54, 0, 3, 3, 0, 0, 8, 0x78), out var opm) &&
            opm.WriteId == 0x54 && opm.Page == 0,
            "ND8 YM2151 register is routed as 54/page 0");
        check(!NdsifRegisterRouting.TryMap(new IndependentVgmProgram.Command(0xa0, 0, 3, 18, 0, 0, 0x10, 0), out _),
            "ND7 AY out-of-range register is rejected");
        check(NdsifRegisterRouting.Nd7ClocksCompatible([
            new IndependentVgmProgram.Device(18, 0, 2000000, "AY"),
            new IndependentVgmProgram.Device(6, 1, 4000000, "YM2203")]),
            "ND7 compatible shared AY/YM2203 clocks are accepted");
        check(!NdsifRegisterRouting.Nd7ClocksCompatible([
            new IndependentVgmProgram.Device(9, 0, 3579545, "YM3812"),
            new IndependentVgmProgram.Device(12, 0, 14318180, "YMF262")]),
            "ND7 conflicting OPL chip modes are rejected");
        byte[] capabilities = [
            0x00, 0x01, 0x00, 0x08, 0x00, 0x00, 0x08, 0x00, 0x01, 0x00, 0xff, 0xff, 0x01,
            0x54, 0x00, 0x00, 0x05, 0x02, 0, 0, 0, 0 ];
        var decodedCapabilities = NdsifRegisterLink.ParseCapabilityPage(capabilities, out var nextCapability, out var controls);
        check(nextCapability == 0xffff && controls == 1 && decodedCapabilities.Length == 1 &&
            decodedCapabilities[0].WriteId == 0x54 && decodedCapabilities[0].ClockMode == 2,
            "ND8 capability page decodes write target and variable clock");
        capabilities[17] = 1;
        check(ThrowsInvalidCapability(capabilities), "Inconsistent fixed-clock capability is rejected");

        byte[] okiVgm = new byte[256 + 17];
        "Vgm "u8.CopyTo(okiVgm);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(okiVgm.AsSpan(4), (uint)okiVgm.Length - 4);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(okiVgm.AsSpan(8), 0x172);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(okiVgm.AsSpan(0x90), 4000000);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(okiVgm.AsSpan(0x34), 256 - 0x34);
        okiVgm[0x94] = 2;
        new byte[] { 0xb7, 0, 2, 0xb7, 2, 1, 0xb7, 1, 0xf7,
            0x61, 0, 1, 0xb7, 0, 1, 0x66 }.CopyTo(okiVgm, 256);
        var okiFile = VgmFile.Load(okiVgm)!;
        var okiProgram = IndependentVgmProgram.Parse(okiFile);
        var renderer = new NdsifOkiRenderer(okiFile, okiProgram, false);
        var streamBytes = new List<byte>(); var streamEvents = new List<NdsifOkiRenderer.AudioEvent>();
        for (int i = 0; i < 1000 && !renderer.Finished; i++)
        {
            var chunk = renderer.Next(); streamBytes.AddRange(chunk.Data); streamEvents.AddRange(chunk.Events);
        }
        check(renderer.Finished && streamBytes.Any(b => b != 0), "ND8 renderer produces finite audible OKIM6258 output");
        check(streamEvents.Any(e => e.Kind == 2 && e.Payload.SequenceEqual(new byte[] { 1 })) &&
            streamEvents.Any(e => e.Kind == 3), "ND8 renderer emits pan and terminal events");
        var seekRenderer = new NdsifOkiRenderer(okiFile, okiProgram, false);
        seekRenderer.SeekToSample(128);
        check(seekRenderer.BytePosition == 0 && seekRenderer.Next().Data.Length != 0,
            "ND8 renderer restarts its physical position after a paused-track seek");

        byte[] mixedVgm = new byte[okiVgm.Length + 6];
        Array.Copy(okiVgm, mixedVgm, 256);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(mixedVgm.AsSpan(4), (uint)mixedVgm.Length - 4);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(mixedVgm.AsSpan(0x30), 4000000);
        new byte[] { 0x54, 0x20, 0x7f, 0x54, 8, 0x78 }.CopyTo(mixedVgm, 256);
        Array.Copy(okiVgm, 256, mixedVgm, 262, okiVgm.Length - 256);
        var mixedFile = VgmFile.Load(mixedVgm)!;
        var mixedRenderer = new NdsifOkiRenderer(mixedFile, IndependentVgmProgram.Parse(mixedFile), true);
        check(mixedRenderer.InitialYmWrites().SequenceEqual(new byte[] { 0x20, 0x7f }),
            "ND8 applies pre-sound YM2151 setup before AUDIO_START");
        var mixedEvents = Enumerable.Range(0, 3).SelectMany(_ => mixedRenderer.Next().Events).ToArray();
        check(mixedEvents.Any(e => e.Kind == 0 && e.Payload.SequenceEqual(new byte[] { 8, 0x78 })) &&
            !mixedEvents.Any(e => e.Kind == 0 && e.Payload.SequenceEqual(new byte[] { 0x20, 0x7f })),
            "ND8 does not replay initial YM2151 setup after AUDIO_START");
    }

    private static bool ThrowsInvalidCapability(byte[] page)
    {
        try { NdsifRegisterLink.ParseCapabilityPage(page, out _, out _); return false; }
        catch (InvalidDataException) { return true; }
    }

    private sealed class FailingStream(int failAfterBytes) : MemoryStream
    {
        public int WriteAttempts;
        public override void Write(byte[] buffer, int offset, int count)
        {
            Interlocked.Increment(ref WriteAttempts);
            if (Length + count > failAfterBytes) throw new IOException("device removed");
            base.Write(buffer, offset, count);
        }
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Interlocked.Increment(ref WriteAttempts);
            if (Length + buffer.Length > failAfterBytes) throw new IOException("device removed");
            base.Write(buffer);
        }
    }

    private static List<NdsifMessage> ParseMultipleFrames(byte[] bytes)
    {
        var result = new List<NdsifMessage>();
        var current = new List<byte>();
        foreach (byte b in bytes)
        {
            if (b == 0x00)
            {
                if (current.Count > 0)
                {
                    if (NdsifService.TryParseFrame(current.ToArray(), out var msg))
                    {
                        result.Add(msg);
                    }
                    current.Clear();
                }
            }
            else
            {
                current.Add(b);
            }
        }
        return result;
    }

    private static void ValidateNd6DacStreamInvariants(List<NdsifMessage> messages, Action<bool, string> check, string label)
    {
        uint acceptedSeq = 0;
        ulong acceptedTick = 0;
        ulong lastEventTick = 0;
        bool hasEvent = false;
        bool invariantsOk = true;
        string failureDetail = "";

        foreach (var msg in messages)
        {
            if (msg.CommandId == 0x1D)
            {
                if (msg.Payload.Length < 13 || msg.Payload[0] != 0x01)
                {
                    invariantsOk = false;
                    failureDetail = "Invalid 0x1D header";
                    break;
                }
                ulong tick = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(msg.Payload.AsSpan(1, 8));
                byte target = msg.Payload[9];
                byte page = msg.Payload[11];

                if (tick < acceptedTick)
                {
                    invariantsOk = false;
                    failureDetail = $"0x1D tick ({tick}) < acceptedTick ({acceptedTick})";
                    break;
                }
                if (hasEvent && tick < lastEventTick)
                {
                    invariantsOk = false;
                    failureDetail = $"0x1D tick ({tick}) < lastEventTick ({lastEventTick})";
                    break;
                }
                if (target == 0x52 && page == 0)
                {
                    for (int i = 12; i + 1 < msg.Payload.Length; i += 2)
                    {
                        byte reg = msg.Payload[i];
                        if (reg is 0x2A or 0x2B)
                        {
                            invariantsOk = false;
                            failureDetail = $"0x1D illegally contained YM2612 DAC register 0x{reg:X2}";
                            break;
                        }
                    }
                }
                lastEventTick = tick;
                hasEvent = true;
            }
            else if (msg.CommandId == 0x19)
            {
                if (msg.Payload.Length < 19 || ((msg.Payload.Length - 15) % 4) != 0)
                {
                    invariantsOk = false;
                    failureDetail = $"Invalid 0x19 payload length {msg.Payload.Length}";
                    break;
                }
                uint seq = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(msg.Payload.AsSpan(3, 4));
                ulong pos = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(msg.Payload.AsSpan(7, 8));
                if (seq != acceptedSeq || pos != acceptedTick)
                {
                    invariantsOk = false;
                    failureDetail = $"0x19 OutOfSequence: got seq={seq}/pos={pos}, expected seq={acceptedSeq}/pos={acceptedTick}";
                    break;
                }
                int count = (msg.Payload.Length - 15) / 4;
                for (int i = 0; i < count; i++)
                {
                    int off = 15 + i * 4;
                    byte kind = msg.Payload[off];
                    byte val = msg.Payload[off + 1];
                    ushort wait = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(msg.Payload.AsSpan(off + 2, 2));

                    if (kind == 1 && (val & 0x7F) != 0)
                    {
                        invariantsOk = false;
                        failureDetail = $"0x19 KindSelect had non-zero low 7 bits: 0x{val:X2}";
                        break;
                    }
                    if (kind == 2 && (val != 0 || wait == 0))
                    {
                        invariantsOk = false;
                        failureDetail = $"0x19 KindWait invalid: val={val}, wait={wait}";
                        break;
                    }
                    acceptedTick += wait;
                    acceptedSeq++;
                }
                if (!invariantsOk) break;
            }
        }

        check(invariantsOk, $"{label}: ND6 dac_stream.cpp invariants satisfied ({failureDetail})");
    }
}
