using System.IO;
using System.IO.Compression;
using MirrorFlux.Models;
using MirrorFlux.Services;


if (args.Length >= 2 && args[0] == "--external-args-probe")
{
    File.WriteAllText(args[1], System.Text.Json.JsonSerializer.Serialize(args.Skip(2).ToArray()));
    return;
}

if (args.Length == 1 && args[0] == "--ndsif")
{
    int ndsifPassed = 0;
    MirrorFlux.Tests.NdsifRegression.Run((ok, name) => { if (!ok) throw new Exception(name); ndsifPassed++; Console.WriteLine("PASS " + name); });
    Console.WriteLine($"Passed {ndsifPassed} NDSIF regression checks.");
    return;
}

if (args.Length == 4 && args[0] == "--ndsif-audit")
{
    using var archive = ZipFile.OpenRead(args[1]);
    var entry = archive.Entries.First(e => e.Name.StartsWith(args[2], StringComparison.OrdinalIgnoreCase));
    using var stream = entry.Open(); using var data = new MemoryStream(); stream.CopyTo(data);
    var vgm = MirrorFlux.Services.Vgm.VgmFile.Load(data.ToArray(), entry.Name) ?? throw new Exception("VGM load failed");
    Console.WriteLine($"ND8 audit: {entry.Name}, OKI clock {vgm.OkiM6258Clock}");
    using var player = new MirrorFlux.Services.Vgm.IndependentVgmWaveProvider(vgm,
        new MirrorFlux.Models.AppSettings { Nd8Ndsif = true });
    int seconds = int.Parse(args[3]); byte[] buffer = new byte[1024 * 4];
    var timer = System.Diagnostics.Stopwatch.StartNew();
    int frames = seconds * 44100 / 1024;
    for (int i = 0; i < frames; i++)
    {
        player.Read(buffer, 0, buffer.Length);
        if (i % 43 == 0) Console.WriteLine($"{i * 1024 / 44100.0:F1}s {player.Nd8AudioStage}: {player.Nd8AudioDiagnostics}");
        int wait = (int)((i + 1L) * 1024 * 1000 / 44100 - timer.ElapsedMilliseconds);
        if (wait > 0) System.Threading.Thread.Sleep(wait);
    }
    Console.WriteLine($"final {player.Nd8AudioStage}: {player.Nd8AudioDiagnostics}");
    if (player.Nd8AudioStage != "running" ||
        !player.Nd8AudioDiagnostics.Contains("underflows=0 overflows=0 rejected=0", StringComparison.Ordinal))
        throw new Exception("ND8 audio stream fault: " + player.Nd8AudioStage + ": " + player.Nd8AudioDiagnostics);
    return;
}

if (args.Length == 1 && args[0] == "--ndsif-hardware")
{
    using (var nd8 = MirrorFlux.Services.Hardware.NdsifRegisterLink.TryOpen(8,
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)))
    {
        if (nd8 == null) throw new Exception("NanoDrive 8 was not detected");
        Console.WriteLine($"Detected {nd8.Name} on {nd8.PortName}");
        Console.WriteLine($"YM2151 4 MHz: {nd8.Supports(0x54, 0, 0, 4000000)}");
        if (!nd8.Supports(0x54, 0, 0, 4000000) || !nd8.Configure([(0x54, (byte)0, 4000000u)]))
            throw new Exception("ND8 YM2151 setup failed");
        nd8.Queue(new MirrorFlux.Services.Hardware.NdsifRegisterLink.RegisterWrite(0, 0x54, 0, 0, 0x08, 0));
        nd8.Start();
        System.Threading.Thread.Sleep(40);
        if (!nd8.IsConnected) throw new Exception("ND8 YM2151 key-off write failed");
        Console.WriteLine("YM2151 key-off register sent");
    }
    byte[] vgm = new byte[256 + 7];
    "Vgm "u8.CopyTo(vgm);
    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(vgm.AsSpan(4), (uint)vgm.Length - 4);
    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(vgm.AsSpan(8), 0x172);
    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(vgm.AsSpan(0x30), 4000000);
    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(vgm.AsSpan(0x34), 256 - 0x34);
    new byte[] { 0x54, 0x08, 0, 0x61, 0x10, 0, 0x66 }.CopyTo(vgm, 256);
    using (var player = new MirrorFlux.Services.Vgm.IndependentVgmWaveProvider(
        MirrorFlux.Services.Vgm.VgmFile.Load(vgm)!, new MirrorFlux.Models.AppSettings { Nd8Ndsif = true }))
    {
        player.Read(new byte[4096], 0, 4096);
        if (!player.GetSnapshot().ExtDeviceName.Contains("ND8", StringComparison.Ordinal))
            throw new Exception("VGM did not select ND8 hardware route");
        Console.WriteLine("VGM YM2151 route selected NanoDrive 8");
    }
    byte[] audioVgm = new byte[256 + 21];
    "Vgm "u8.CopyTo(audioVgm);
    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(audioVgm.AsSpan(4), (uint)audioVgm.Length - 4);
    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(audioVgm.AsSpan(8), 0x172);
    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(audioVgm.AsSpan(0x30), 4000000);
    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(audioVgm.AsSpan(0x90), 4000000);
    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(audioVgm.AsSpan(0x34), 256 - 0x34);
    audioVgm[0x94] = 2;
    new byte[] { 0x54, 8, 0, 0xb7, 0, 2, 0xb7, 1, 0xf7,
        0x61, 0, 1, 0xb7, 0, 1, 0x61, 0, 0x50, 0x66 }.CopyTo(audioVgm, 256);
    using (var player = new MirrorFlux.Services.Vgm.IndependentVgmWaveProvider(
        MirrorFlux.Services.Vgm.VgmFile.Load(audioVgm)!,
        new MirrorFlux.Models.AppSettings { Nd6Ndsif = true, Nd7Ndsif = true, Nd8Ndsif = true }))
    {
        for (int i = 0; i < 8; i++) { player.Read(new byte[1024 * 4], 0, 1024 * 4); System.Threading.Thread.Sleep(23); }
        var snapshot = player.GetSnapshot();
        if (!snapshot.MusicChannels.Any(c => c.ChipName == "OKIM6258" && c.IsHardwareOutput))
            throw new Exception("VGM OKIM6258 did not select ND8 hardware audio");
        if (player.Nd8AudioPlayed == 0) throw new Exception("ND8 AUDIO_STATUS reports no PCM playback: " + player.Nd8AudioStage);
        Console.WriteLine("VGM OKIM6258 routed to ND8 while all ND flags are enabled");
        player.IsPaused = true;
        System.Threading.Thread.Sleep(70);
        player.IsPaused = false;
        for (int i = 0; i < 8; i++) { player.Read(new byte[1024 * 4], 0, 1024 * 4); System.Threading.Thread.Sleep(23); }
        snapshot = player.GetSnapshot();
        if (!snapshot.MusicChannels.Any(c => c.ChipName == "OKIM6258" && c.IsHardwareOutput))
            throw new Exception("ND8 audio did not resume after pause");
        if (player.Nd8AudioPlayed == 0) throw new Exception("ND8 AUDIO_STATUS reports no resumed playback: " + player.Nd8AudioStage);
        Console.WriteLine("ND8 OKIM6258 audio resumed after pause");
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        while (!player.Nd8AudioDiagnostics.Contains("flags=0x3", StringComparison.Ordinal) && deadline.ElapsedMilliseconds < 1200)
        {
            player.Read(new byte[1024 * 4], 0, 1024 * 4);
            System.Threading.Thread.Sleep(23);
        }
        if (!player.Nd8AudioDiagnostics.Contains("flags=0x3", StringComparison.Ordinal) ||
            !player.Nd8AudioDiagnostics.Contains("underflows=0 overflows=0 rejected=0", StringComparison.Ordinal))
            throw new Exception("ND8 PCM end marker failed: " + player.Nd8AudioStage + ": " + player.Nd8AudioDiagnostics);
        Console.WriteLine("ND8 OKIM6258 stream ended without rejected packets or underruns");
    }
    return;
}

if (args.Length is 1 or 2 && args[0] == "--charset")
{
    CharsetDetectorRegression.Run((ok, name) => { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); });
    if (args.Length == 2) CharsetDetectorRegression.AuditUtf8(args[1]);
    return;
}

if (args.Length == 3 && args[0] == "--heic-codec-probe") { WindowsImageCodecRegression.Audit(args[1], args[2]); return; }
if (args.Length == 2 && args[0] == "--image-native-probe") { ImageNativeRoundtripRegression.Run(args[1]); return; }
if (args.Length == 2 && args[0] == "--rar-compare") { RarRegression.Compare(args[1]); return; }
if (args.Length == 1 && args[0] == "--rar")
{
    int failures=0;
    RarRegression.Run((ok,name)=>{Console.WriteLine((ok?"PASS ":"FAIL ")+name);if(!ok)failures++;});
    if(failures>0)throw new Exception("RAR regression failed");
    return;
}

if (args.Length == 2 && args[0] == "--ai-audit")
{
    try { IllustratorViewerRegression.Audit(args[1]); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Length == 1 && args[0] == "--mini-subsong-render")
{
    try { MiniSubsongRendering.Run(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}
if (args.Length == 1 && args[0] == "--jump-list-input")
{
    try { JumpListRegression.KeyboardInput(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Length == 1 && args[0] == "--jump-list-render")
{
    try { JumpListRegression.Render(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}
if (args.Length == 2 && args[0] == "--scenario-audit")
{
    try { ScenarioHighlightRegression.Audit(args[1]); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}
if (args.Length == 1 && args[0] == "--dialog-footers")
{
    try { DialogFooterRendering.Run(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}
if (args.Length == 2 && args[0] == "--archive-doc-audit")
{
    try { ArchiveViewerRegression.Audit(args[1]); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Length == 1 && args[0] == "--image-library-info")
{
    Console.WriteLine(ImageMagick.MagickNET.Version);
    Console.WriteLine(ImageMagick.MagickNET.Delegates);
    Console.WriteLine(ImageMagick.MagickNET.Features);
    return;
}

if (args.Length == 1 && args[0] == "--standalone-chips")
{
    StandaloneChipRegression.Run();return;
}

if (args.Length == 1 && args[0] == "--independent-vgm")
{
    try { IndependentVgmRegression.Run(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode=1; }
    return;
}

if (args.Length == 1 && args[0] == "--audio-migration")
{
    int failures = 0;
    void Verify(bool ok, string name) { if (!ok) failures++; Console.WriteLine((ok ? "PASS " : "FAIL ") + name); }
    VgmAudioRegression.Run(Verify);
    MdxRegression.Run(Verify);
    S98Regression.Run(Verify);
    if (failures > 0) throw new Exception($"Audio migration: {failures} failed checks");
    return;
}

if (args.Length == 1 && args[0] == "--fm-core")
{
    try { FmCoreRegression.Run(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Length == 2 && args[0] == "--ymfm-audit")
{
    try { FmCoreRegression.Audit(args[1]); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Length >= 1 && args[0] == "--probe-com")
{
    string? targetPort = args.Length >= 2 ? args[1] : null;
    Console.WriteLine($"Probing NDSIF via NdsifService (preferred: {targetPort ?? "auto"})...");
    var ndsif = MirrorFlux.Services.Hardware.NdsifService.Instance;
    bool detected = ndsif.EnsureConnected(targetPort);
    Console.WriteLine($"EnsureConnected result: {detected}, Port: {ndsif.PortName}, Device: {ndsif.DeviceName}");
    if (detected)
    {
        Console.WriteLine("NanoDrive connected! Playing 440Hz test tone on YM2612...");
        ndsif.SetChipClock(0x52, 0, 7670453);
        (byte reg, byte val)[] writes = [
            (0x28, 0x00),
            (0x30, 0x01), (0x34, 0x01), (0x38, 0x01), (0x3C, 0x01),
            (0x40, 0x00), (0x44, 0x00), (0x48, 0x00), (0x4C, 0x00),
            (0x50, 0x1F), (0x54, 0x1F), (0x58, 0x1F), (0x5C, 0x1F),
            (0x60, 0x00), (0x64, 0x00), (0x68, 0x00), (0x6C, 0x00),
            (0x70, 0x00), (0x74, 0x00), (0x78, 0x00), (0x7C, 0x00),
            (0x80, 0x00), (0x84, 0x00), (0x88, 0x00), (0x8C, 0x00),
            (0xB0, 0x3F), (0xB4, 0xC0), (0xA4, 0x22), (0xA0, 0x69), (0x28, 0xF0)
        ];
        foreach (var (r, v) in writes) ndsif.WriteYm2612(0, r, v);
        System.Threading.Thread.Sleep(300);
        ndsif.WriteYm2612(0, 0x28, 0x00);

        Console.WriteLine("Testing SN76489 chip 1 (0x50)...");
        ndsif.SetChipClock(0x50, 0, 3579545);
        ndsif.WriteSn76489(0, 0x8E); // Ch0 freq low
        ndsif.WriteSn76489(0, 0x01); // Ch0 freq high (~440Hz)
        ndsif.WriteSn76489(0, 0x90); // Ch0 vol max
        System.Threading.Thread.Sleep(200);
        ndsif.WriteSn76489(0, 0x9F); // Mute Ch0

        Console.WriteLine("Testing SN76489 chip 2 (0x30)...");
        ndsif.SetChipClock(0x30, 1, 3579545);
        ndsif.WriteSn76489(1, 0x85); // Ch0 freq low (~587Hz / D5)
        ndsif.WriteSn76489(1, 0x01); // Ch0 freq high
        ndsif.WriteSn76489(1, 0x90); // Ch0 vol max
        System.Threading.Thread.Sleep(200);
        ndsif.WriteSn76489(1, 0x9F); // Mute Ch0

        ndsif.Reset();
        Console.WriteLine("Tones played successfully and reset sent!");
    }
    return;
}

if (args.Length == 2 && args[0] == "--chip-route")
{
    try { VgmChipRegression.AuditRoute(int.Parse(args[1])); }
    catch (Exception ex) { Console.Error.WriteLine(ex.Message); Environment.ExitCode = 1; }
    return;
}

if (args.Length == 3 && args[0] == "--display-audit")
{
    try { VgmChipRegression.AuditDisplay(args[1], args[2]); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Length > 0 && args[0] == "--vgm-chips")
{
    try { if (args.Length == 2) VgmChipRegression.Audit(args[1]); else VgmChipRegression.Run(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Length == 2 && args[0] == "--s98-audit")
{
    try { S98Regression.Audit(args[1]); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}
if (args.Length is 2 or 3 && args[0] == "--mdx-audit")
{
    try { MdxRegression.Audit(args[1], args.Length == 3 ? args[2] : null); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}
if (args.Length == 2 && args[0] == "--archive-encoding")
{
    try { await ArchiveEncodingRegression.Audit(args[1]); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Length == 2 && args[0] == "--gme-audit")
{
    try { GmeAudioRegression.Audit(args[1]); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Length == 1 && args[0] == "--music-album-art")
{
    try { MusicAlbumArtRegression.Run(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Length == 1 && args[0] == "--music-playall")
{
    try { MusicPlayAllRegression.Run(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--music-file-list"))
{
    try { MusicFileListRendering.Run(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--music-dpi-render"))
{
    try { MusicDpiRendering.Run(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Length == 2 && args[0] == "--audio-archive")
{
    try { AudioArchiveAudit.Run(args[1]); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--music-render"))
{
    try { MusicRendering.Run(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--rename-render"))
{
    try { RenameDialogRendering.Run(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Length >= 3 && args[0] == "--mdx-nd8-wav")
{
    foreach (string path in args.Skip(2)) MdxNd8Stats.WriteWaves(path, args[1], 60);
    return;
}

if (args.Length >= 2 && args[0] == "--mdx-nd8-stats")
{
    MdxNd8Stats.Run(args.Skip(1), 90);
    return;
}

if (args.Contains("--operation-render"))
{
    try { FileOperationPanelRendering.Run(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--header-render"))
{
    try { HeaderRendering.Run(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--hardware-stop"))
{
    int count = 0;
    void CheckHardware(bool ok, string name) { if (!ok) throw new Exception(name); count++; Console.WriteLine("PASS " + name); }
    NdsifShutdownRegression.Run(CheckHardware);
    KeyboardPitchRenderRegression.Run(CheckHardware);
    MdxNd8Regression.Run(CheckHardware);
    Console.WriteLine($"Passed {count} hardware shutdown and MDX checks.");
    return;
}

if (args.Contains("--pane-navigation"))
{
    await PaneNavigationRegression.Run();
    return;
}

if (args.Contains("--window-resume"))
{
    int checks = 0;
    void CheckWindow(bool ok, string name) { if (!ok) throw new Exception(name); checks++; Console.WriteLine("PASS " + name); }
    StartupWindowPlacementRegression.Run(CheckWindow);
    SuspendPlaybackRegression.Run(CheckWindow);
    Console.WriteLine($"Passed {checks} window and resume checks.");
    return;
}

var root = Path.Combine(Path.GetTempPath(), "MirrorFlux-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
int passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception(name);
    Console.WriteLine("PASS " + name);
    passed++;
}
FileEntry Entry(string path) => new(path, Path.GetFileName(path), Directory.Exists(path), false,
    File.Exists(path) ? new FileInfo(path).Length : 0, DateTime.Now);
Task<ConflictResolution> Overwrite(ConflictInfo _) => Task.FromResult(new ConflictResolution(ConflictAction.Overwrite, false));
void Log(string text, string tag) { }
try
{
    await SafetyRegression.Run(root, Check);
    SettingsPreservationRegression.Run(root, Check);
    PublicAssetsRegression.Run(Check);
    await CopyResultRegression.Run(root, Check);
    await ArchiveUpdateRegression.Run(root, Check);
    await LhaWriterRegression.Run(root, Check);
    if (args.Contains("--refactor"))
    {
        Console.WriteLine($"Passed {passed} refactoring checks.");
        return;
    }
    FunctionKeyBindingRegression.Run(root, Check);
    ViewerFunctionBindingRegression.Run(root, Check);
    if (args.Contains("--function-keys"))
    {
        ShiftFunctionLabelRegression.Run(root, Check);
        SoundFunctionLabelRegression.Run(Check);
        Console.WriteLine($"Passed {passed} function key checks.");
        return;
    }
    MusicRegression.Run(Check);
    VgmAudioRegression.Run(Check);
    GmeAudioRegression.Run(Check);
    RarRegression.Run(Check);
    WindowsImageCodecRegression.Run(Check);
    S98Regression.Run(Check);
    MdxRegression.Run(Check);
    PdxPcmRegression.Run(Check);
    MdxNd8Regression.Run(Check);
    NdsifShutdownRegression.Run(Check);
    MdxCommandRegression.Run(Check);
    RhythmRomPathRegression.Run(root, Check);
    VgmDisplayTimingRegression.Run(Check);
    StereoPanRegression.Run(Check);
    FileOperationQueueRegression.Run(root, Check);
    StartupWindowPlacementRegression.Run(Check);
    SuspendPlaybackRegression.Run(Check);
    CharsetDetectorRegression.Run(Check);
    await ExplorerFolderRegression.Run(root, Check);
    await ExternalProgramRegression.Run(root, Check);
    TextViewerRegression.Run(Check);
    TextViewerFeaturesRegression.Run(Check);
    SoundFunctionLabelRegression.Run(Check);
    await ArchiveEncodingRegression.Run(root, Check);
    await TarArchiveRegression.Run(root, Check);
    await ArchiveCreateRegression.Run(root, Check);
    ArchiveViewerRegression.Run(root, Check);
    IllustratorViewerRegression.Run(root, Check);
    ViewerViewportRegression.Run(Check);
    JumpListRegression.Run(root, Check);
    ShiftFunctionLabelRegression.Run(root, Check);
    var source = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
    var dest = Directory.CreateDirectory(Path.Combine(root, "dest")).FullName;
    File.WriteAllText(Path.Combine(source, "a.txt"), "source");
    var errors = new List<string>();
    await FileCopyService.CopyEntriesAsync([Entry(source)], source, Overwrite, (m, t) => errors.Add(t));
    Check(!Directory.Exists(Path.Combine(source, "source")) && errors.Contains("ERR"), "copy into self is rejected before creating directories");
    var child = Directory.CreateDirectory(Path.Combine(source, "child")).FullName;
    await FileCopyService.CopyEntriesAsync([Entry(source)], child, Overwrite, Log);
    Check(!Directory.Exists(Path.Combine(child, "source")), "copy into descendant is rejected");
    await FileCopyService.CopyEntriesAsync([Entry(source)], dest, Overwrite, Log);
    Check(File.ReadAllText(Path.Combine(dest, "source", "a.txt")) == "source", "ordinary recursive copy");

    foreach (bool move in new[] { false, true })
    {
        var name = move ? "move.txt" : "copy.txt";
        var src = Path.Combine(source, name);
        File.WriteAllText(src, "new");
        File.WriteAllText(Path.Combine(dest, name), "original");
        var existing = Path.Combine(dest, "existing.txt");
        File.WriteAllText(existing, "keep");
        int calls = 0;
        Task<ConflictResolution> Resolve(ConflictInfo info)
        {
            calls++;
            return Task.FromResult(calls == 1
                ? new ConflictResolution(ConflictAction.Rename, false, "existing.txt")
                : new ConflictResolution(ConflictAction.Skip, false));
        }
        if (move) await FileMoveService.MoveEntriesAsync([Entry(src)], dest, Resolve, Log);
        else await FileCopyService.CopyEntriesAsync([Entry(src)], dest, Resolve, Log);
        Check(calls == 2 && File.ReadAllText(existing) == "keep" && File.Exists(src), $"rename collision rechecked (move={move})");
        bool canceled = false;
        Task<ConflictResolution> Cancel(ConflictInfo _) => Task.FromResult(new ConflictResolution(ConflictAction.Cancel, false));
        try
        {
            if (move) await FileMoveService.MoveEntriesAsync([Entry(src)], dest, Cancel, Log);
            else await FileCopyService.CopyEntriesAsync([Entry(src)], dest, Cancel, Log);
        }
        catch (OperationCanceledException) { canceled = true; }
        Check(canceled, $"cancel reaches caller (move={move})");
    }
    foreach (var name in new[] { "../escape.txt", "C:\\escape.txt", "a:b", "..", "trailing." })
    {
        bool rejected = false;
        try { FileOperationPaths.RenameTarget(Path.Combine(dest, "a.txt"), name); }
        catch (IOException) { rejected = true; }
        Check(rejected, "reject rename " + name);
    }
    var zip = Path.Combine(root, "bad.zip");
    using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
    {
        using var writer = new StreamWriter(archive.CreateEntry("folder/../../escape.txt").Open());
        writer.Write("bad");
    }
    bool blocked = false;
    try
    {
        await ArchiveService.ExtractEntriesAsync(zip,
            [new FileEntry(zip + "\\folder", "folder", true, false, 0, DateTime.Now)], dest, Overwrite, Log);
    }
    catch (IOException) { blocked = true; }
    Check(blocked && !File.Exists(Path.Combine(root, "escape.txt")), "ZIP traversal rejected");

    var safeZip = Path.Combine(root, "safe.zip");
    using (var archive = ZipFile.Open(safeZip, ZipArchiveMode.Create))
    {
        using var writer = new StreamWriter(archive.CreateEntry("a.txt").Open());
        writer.Write("archive content");
    }
    var virtualEntry = new FileEntry(safeZip + "\\a.txt", "a.txt", false, false, 15, DateTime.Now);
    await ArchiveService.ExtractEntriesAsync(safeZip, [virtualEntry], dest, Overwrite, Log);
    Check(File.ReadAllText(Path.Combine(dest, "a.txt")) == "archive content", "ordinary ZIP extraction");
    File.WriteAllText(Path.Combine(dest, "existing.txt"), "keep");
    int archiveCalls = 0;
    await ArchiveService.ExtractEntriesAsync(safeZip, [virtualEntry], dest, info => Task.FromResult(++archiveCalls == 1
        ? new ConflictResolution(ConflictAction.Rename, false, "existing.txt") : new ConflictResolution(ConflictAction.Skip, false)), Log);
    Check(archiveCalls == 2 && File.ReadAllText(Path.Combine(dest, "existing.txt")) == "keep", "ZIP rename collision rechecked");

    var lha = Path.Combine(root, "safe.lha");
    File.WriteAllBytes(lha, [0]);
    await ArchiveService.AddEntriesToArchiveAsync(lha, "", [Entry(Path.Combine(source, "a.txt"))], Overwrite, Log);
    var lhaEntry = new FileEntry(lha + "\\a.txt", "a.txt", false, false, 6, DateTime.Now);
    await ArchiveService.ExtractEntriesAsync(lha, [lhaEntry], dest, Overwrite, Log);
    Check(File.ReadAllText(Path.Combine(dest, "a.txt")) == "source", "ordinary LHA extraction");
    int lhaCalls = 0;
    await ArchiveService.ExtractEntriesAsync(lha, [lhaEntry], dest, info => Task.FromResult(++lhaCalls == 1
        ? new ConflictResolution(ConflictAction.Rename, false, "existing.txt") : new ConflictResolution(ConflictAction.Skip, false)), Log);
    Check(lhaCalls == 2 && File.ReadAllText(Path.Combine(dest, "existing.txt")) == "keep", "LHA rename collision rechecked");

    // Extracting one of two names that differ only by case takes the exact name; a folder brings its empty subfolder.
    var caseZip = Path.Combine(root, "case.zip");
    using (var archive = ZipFile.Open(caseZip, ZipArchiveMode.Create))
    {
        using (var upper = new StreamWriter(archive.CreateEntry("Song.txt").Open())) upper.Write("upper");
        using (var lower = new StreamWriter(archive.CreateEntry("song.txt").Open())) lower.Write("lower");
        archive.CreateEntry("pack/empty/");
        using (var inner = new StreamWriter(archive.CreateEntry("pack/in.txt").Open())) inner.Write("inner");
    }
    var caseDest = Path.Combine(root, "case-out");
    Directory.CreateDirectory(caseDest);
    var caseResult = await ArchiveService.ExtractEntriesAsync(caseZip,
        [new FileEntry(caseZip + "\\song.txt", "song.txt", false, false, 5, DateTime.Now),
         new FileEntry(caseZip + "\\pack", "pack", true, false, 0, DateTime.Now)], caseDest, Overwrite, Log);
    Check(File.ReadAllText(Path.Combine(caseDest, "song.txt")) == "lower" &&
        File.ReadAllText(Path.Combine(caseDest, "pack", "in.txt")) == "inner" &&
        Directory.Exists(Path.Combine(caseDest, "pack", "empty")) && caseResult.CopiedCount == 2 && caseResult.SuccessfulPaths.Count == 2,
        "extraction prefers the exact name and keeps empty folders inside a selected folder");
    FileOperationPaths.ValidateDirectoryTransfer(Path.GetFullPath("Services"), Path.Combine(root, "cloud-copy"));
    Check(true, "cloud-synced source directory accepted");

    var renameSource = Path.Combine(root, "rename-source.txt");
    File.WriteAllText(renameSource, "unchanged content");
    var originalTime = new DateTime(2024, 4, 1, 16, 26, 6, DateTimeKind.Utc).AddTicks(1234567);
    File.SetLastWriteTimeUtc(renameSource, originalTime);
    var renamed = FileRenameService.Apply(renameSource, new FileRenameOptions("Renamed.txt", false, false, false, null));
    Check(!File.Exists(renameSource) && File.ReadAllText(renamed) == "unchanged content", "single file rename preserves content");
    Check(File.GetLastWriteTimeUtc(renamed) == originalTime, "rename preserves full timestamp precision");
    renamed = FileRenameService.Apply(renamed, new FileRenameOptions("RENAMED.TXT", false, false, false, null));
    Check(Directory.GetFiles(root).Any(path => Path.GetFileName(path) == "RENAMED.TXT"), "case-only rename");
    var editedTime = new DateTime(2025, 5, 6, 7, 8, 9, DateTimeKind.Local);
    renamed = FileRenameService.Apply(renamed, new FileRenameOptions("RENAMED.TXT", true, true, true, editedTime));
    Check((File.GetAttributes(renamed) & (FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System)) ==
        (FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System), "file attributes applied");
    Check(File.GetLastWriteTime(renamed) == editedTime, "last-write date/time applied");
    renamed = FileRenameService.Apply(renamed, new FileRenameOptions("renamed.txt", false, false, false, null));
    Check(!File.GetAttributes(renamed).HasFlag(FileAttributes.ReadOnly), "read-only file can be renamed and attribute cleared");
    var beforeFailure = File.GetLastWriteTimeUtc(renamed);
    bool lockedRejected = false;
    using (var locked = new FileStream(renamed, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
    {
        try { FileRenameService.Apply(renamed, new FileRenameOptions("locked-target.txt", false, false, false, editedTime.AddDays(1))); }
        catch (IOException) { lockedRejected = true; }
    }
    Check(lockedRejected && File.Exists(renamed) && File.GetLastWriteTimeUtc(renamed) == beforeFailure,
        "failed rename rolls timestamp back");
    var collision = Path.Combine(root, "collision.txt");
    File.WriteAllText(collision, "keep");
    bool collisionRejected = false;
    try { FileRenameService.Apply(renamed, new FileRenameOptions("collision.txt", false, false, false, null)); }
    catch (IOException) { collisionRejected = true; }
    Check(collisionRejected && File.ReadAllText(collision) == "keep" && File.Exists(renamed), "rename never overwrites existing file");
    foreach (var badName in new[] { "../escape.txt", "CON.txt", "LPT1", "end.", "" })
    {
        bool rejected = false;
        try { FileRenameService.Apply(renamed, new FileRenameOptions(badName, false, false, false, null)); }
        catch (IOException) { rejected = true; }
        Check(rejected && File.Exists(renamed), "rename rejects invalid name: " + badName);
    }
    var renamedFolder = FileRenameService.Apply(source, new FileRenameOptions("folder-new", false, false, false, null));
    Check(!Directory.Exists(source) && Directory.Exists(renamedFolder) &&
        File.ReadAllText(Path.Combine(renamedFolder, "a.txt")) == "source", "folder rename preserves contents");
    renamedFolder = FileRenameService.Apply(renamedFolder, new FileRenameOptions("FOLDER-NEW", false, false, false, null));
    Check(Directory.EnumerateDirectories(root).Any(path => Path.GetFileName(path) == "FOLDER-NEW"), "folder case-only rename");
    Check(FileRenameService.ConvertCase("ChangeLog.TXT", true, "lower") == "ChangeLog.txt", "extension-only case conversion");
    Check(FileRenameService.ConvertCase("日本語.txt", false, "upper") == "日本語.TXT", "case conversion preserves Japanese");
    Check(MirrorFlux.Input.IniKeyParser.TryParseLine(">KEY_R ---- &filerename;", out var renameBinding) &&
        renameBinding?.Command == MirrorFlux.Input.AppCommand.FileRename, "filerename key binding parsed");
    Check(MirrorFlux.Input.IniKeyParser.TryParseLine(">KEY_ENTER ---- &extexec;", out var enterBinding) &&
        enterBinding?.Command == MirrorFlux.Input.AppCommand.ExtExec, "extexec key binding parsed");
    Check(MirrorFlux.Input.IniKeyParser.TryParseLine(">KEY_ENTER -s-- &extexecwin;", out var windowsEnterBinding) &&
        windowsEnterBinding?.Command == MirrorFlux.Input.AppCommand.ExtExecWindows &&
        windowsEnterBinding.Modifiers == System.Windows.Input.ModifierKeys.Shift, "extexecwin shift key binding parsed");
    Check(MirrorFlux.Input.IniKeyParser.TryParseLine(">KEY_D ---- &filedelete;", out var dDeleteBinding) &&
        dDeleteBinding?.Command == MirrorFlux.Input.AppCommand.Delete, "filedelete D key binding parsed");
    Check(MirrorFlux.Input.IniKeyParser.TryParseLine(">KEY_G ---- &graphicviewer;", out var gviewBinding) &&
        gviewBinding?.Command == MirrorFlux.Input.AppCommand.GraphicViewer, "graphicviewer key binding parsed");
    Check(MirrorFlux.Input.IniKeyParser.TryParseExtBinding(".txt ---- &textviewer;", out var txtExt) &&
        txtExt?.Extension == ".txt" && txtExt.Command == MirrorFlux.Input.AppCommand.TextViewer, "txt ext binding parsed");
    Check(MirrorFlux.Input.IniKeyParser.TryParseExtBinding(".png ---- &graphicviewer;", out var pngExt) &&
        pngExt?.Extension == ".png" && pngExt.Command == MirrorFlux.Input.AppCommand.GraphicViewer, "png ext binding parsed");
    Check(MirrorFlux.Input.IniKeyParser.TryParseExtBinding(".vgm ---- &miniplayer;", out var vgmExt) &&
        vgmExt?.Extension == ".vgm" && vgmExt.Command == MirrorFlux.Input.AppCommand.MiniPlayer, "vgm miniplayer ext binding parsed");
    Check(MirrorFlux.Input.IniKeyParser.TryParseExtBinding(".vgz ---- &miniplayer;", out var vgzExt) &&
        vgzExt?.Extension == ".vgz" && vgzExt.Command == MirrorFlux.Input.AppCommand.MiniPlayer, "vgz miniplayer ext binding parsed");

    Check(MirrorFlux.Input.IniKeyParser.TryParseLine(">KEY_S ---- &sort;", out var sortBinding) &&
        sortBinding?.Command == MirrorFlux.Input.AppCommand.Sort, "sort S key binding parsed");
    Check(MirrorFlux.Input.IniKeyParser.TryParseLine(">KEY_F10 ---- &sort;", out var f10SortBinding) &&
        f10SortBinding?.Command == MirrorFlux.Input.AppCommand.Sort &&
        f10SortBinding.Key == System.Windows.Input.Key.F10, "sort F10 key binding parsed");
    Check(MirrorFlux.Input.IniKeyParser.TryParseLine(">KEY_- -t-- &samepath;", out var syncBinding) &&
        syncBinding?.Command == MirrorFlux.Input.AppCommand.SyncPanePath &&
        syncBinding.Key == System.Windows.Input.Key.OemMinus &&
        syncBinding.Modifiers == System.Windows.Input.ModifierKeys.None, "sync pane path KEY_- -t-- key binding parsed");
    Check(MirrorFlux.Input.IniKeyParser.TryParseLine(">KEY_- ---- &syncpath;", out var syncBinding2) &&
        syncBinding2?.Command == MirrorFlux.Input.AppCommand.SyncPanePath, "sync pane path &syncpath; parsed");

    // Same-folder copy & read-only overwrite tests
    var sameCopyDir = Directory.CreateDirectory(Path.Combine(root, "same-copy")).FullName;
    var sameFile = Path.Combine(sameCopyDir, "item.txt");
    File.WriteAllText(sameFile, "same-folder-content");
    // 競合ダイアログ不要（FailIfConflict）でも同一フォルダー内なら自動連番で複製される
    Func<ConflictInfo, Task<ConflictResolution>> failIfConflict = _ => Task.FromException<ConflictResolution>(new InvalidOperationException("conflict resolver should not be invoked for same-folder copy"));
    await FileCopyService.CopyEntriesAsync([Entry(sameFile)], sameCopyDir, failIfConflict, Log);
    Check(File.Exists(Path.Combine(sameCopyDir, "item_1.txt")) &&
        File.ReadAllText(Path.Combine(sameCopyDir, "item_1.txt")) == "same-folder-content",
        "same-folder file copy auto-numbers without invoking conflict dialog");

    var sameSubDir = Directory.CreateDirectory(Path.Combine(sameCopyDir, "sub")).FullName;
    File.WriteAllText(Path.Combine(sameSubDir, "inside.txt"), "inside-dir");
    await FileCopyService.CopyEntriesAsync([Entry(sameSubDir)], sameCopyDir, failIfConflict, Log);
    Check(File.Exists(Path.Combine(sameCopyDir, "sub_1", "inside.txt")) &&
        File.ReadAllText(Path.Combine(sameCopyDir, "sub_1", "inside.txt")) == "inside-dir",
        "same-folder directory copy auto-numbers destination directory");

    // 空フォルダーのコピー
    var emptySubDir = Directory.CreateDirectory(Path.Combine(sameCopyDir, "empty_dir")).FullName;
    var (emptyCopied, _, emptySuccessPaths) = await FileCopyService.CopyEntriesAsync([Entry(emptySubDir)], Path.Combine(root, "empty-dest"), failIfConflict, Log);
    Check(emptyCopied == 1 && emptySuccessPaths.Count == 1 && Directory.Exists(Path.Combine(root, "empty-dest", "empty_dir")),
        "copying empty directory succeeds and counts as 1 copied item");

    var roDestDir = Directory.CreateDirectory(Path.Combine(root, "ro-dest")).FullName;
    var roTarget = Path.Combine(roDestDir, "item.txt");
    File.WriteAllText(roTarget, "old-readonly");
    File.SetAttributes(roTarget, FileAttributes.ReadOnly);
    await FileCopyService.CopyEntriesAsync([Entry(sameFile)], roDestDir, Overwrite, Log);
    Check(File.ReadAllText(roTarget) == "same-folder-content", "copy overwrite clears ReadOnly attribute on destination");

    // Hidden / System ファイルの上書き拒否テスト
    var hiddenSrc = Path.Combine(sameCopyDir, "hidden.txt");
    File.WriteAllText(hiddenSrc, "new-content");
    var hiddenTarget = Path.Combine(roDestDir, "hidden.txt");
    File.WriteAllText(hiddenTarget, "keep-hidden");
    File.SetAttributes(hiddenTarget, FileAttributes.Hidden);
    string? copyErr = null;
    await FileCopyService.CopyEntriesAsync([Entry(hiddenSrc)], roDestDir, Overwrite, (msg, tag) => { if (tag == "ERR") copyErr = msg; });
    Check(copyErr != null && copyErr.Contains("隠し") && copyErr.Contains("原因") && File.ReadAllText(hiddenTarget) == "keep-hidden",
        "copy overwrite rejects overwriting Hidden files and logs error");

    // 他プロセス排他ロック時のエラーログ出力テスト
    var lockedSrc = Path.Combine(sameCopyDir, "locked.txt");
    File.WriteAllText(lockedSrc, "new-locked-data");
    var lockedTarget = Path.Combine(roDestDir, "locked.txt");
    File.WriteAllText(lockedTarget, "locked-data");
    string? lockErr = null;
    using (var lockFs = new FileStream(lockedTarget, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
    {
        await FileCopyService.CopyEntriesAsync([Entry(lockedSrc)], roDestDir, Overwrite, (msg, tag) => { if (tag == "ERR") lockErr = msg; });
    }
    Check(lockErr != null && lockErr.Contains("原因") && lockErr.Contains("プロセス") && File.ReadAllText(lockedTarget) == "locked-data",
        "copy overwrite does not force-overwrite locked files and logs error with reason");

    // FileMoveService の Hidden 上書き拒否と理由ログ
    var moveHiddenSrc = Path.Combine(sameCopyDir, "move-hidden-src.txt");
    File.WriteAllText(moveHiddenSrc, "move-new");
    var moveHiddenTarget = Path.Combine(roDestDir, "move-hidden-src.txt");
    File.WriteAllText(moveHiddenTarget, "keep-move-hidden");
    File.SetAttributes(moveHiddenTarget, FileAttributes.Hidden);
    string? moveHiddenErr = null;
    await FileMoveService.MoveEntriesAsync([Entry(moveHiddenSrc)], roDestDir, Overwrite, (msg, tag) => { if (tag == "ERR") moveHiddenErr = msg; });
    Check(moveHiddenErr != null && moveHiddenErr.Contains("原因") && moveHiddenErr.Contains("隠し") && File.ReadAllText(moveHiddenTarget) == "keep-move-hidden",
        "move overwrite rejects overwriting Hidden files and logs reason");

    // FileMoveService のロック時エラー理由ログ
    var moveLockedSrc = Path.Combine(sameCopyDir, "move-locked-src.txt");
    File.WriteAllText(moveLockedSrc, "locked-move-content");
    var moveLockedTarget = Path.Combine(roDestDir, "move-locked-src.txt");
    File.WriteAllText(moveLockedTarget, "locked-move-dest");
    string? moveLockErr = null;
    using (var lockFs = new FileStream(moveLockedTarget, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
    {
        await FileMoveService.MoveEntriesAsync([Entry(moveLockedSrc)], roDestDir, Overwrite, (msg, tag) => { if (tag == "ERR") moveLockErr = msg; });
    }
    Check(moveLockErr != null && moveLockErr.Contains("原因") && (moveLockErr.Contains("プロセス") || moveLockErr.Contains("アクセスが拒否されました")),
        "move overwrite logs explicit reason when target is locked");

    // RecycleBinService.DeleteToRecycleBinWithResult の失敗理由テスト
    var (delRes1, delReason1) = RecycleBinService.DeleteToRecycleBinWithResult(Path.Combine(root, "non-existent-sample.txt"));
    Check(!delRes1 && delReason1 != null && delReason1.Contains("見つかりません"),
        "delete non-existent file returns failure with reason");

    var lockForDel = Path.Combine(root, "lock-del.txt");
    File.WriteAllText(lockForDel, "cannot delete me");
    string? delReason2;
    bool delRes2;
    using (var lockFs = new FileStream(lockForDel, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
    {
        (delRes2, delReason2) = RecycleBinService.DeleteToRecycleBinWithResult(lockForDel);
    }
    Check(!delRes2 && delReason2 != null && delReason2.Length > 0,
        "delete locked file returns failure with reason");

    // エラーログ分割テスト（画面右端はみ出し防止のためエラー原因は次の行へ）
    var dummyStore = new SettingsStore(Path.Combine(root, "test-vm-settings.ini"));
    var testVm = new MirrorFlux.ViewModels.MainViewModel(dummyStore);
    int logCountBefore = testVm.Logs.Count;
    testVm.AddLog("コピー失敗: C:\\src.txt -> D:\\dst.txt [原因: テスト理由]", "ERR");
    Check(testVm.Logs.Count == logCountBefore + 2 &&
          testVm.Logs[^2].Message == "コピー失敗: C:\\src.txt -> D:\\dst.txt" &&
          testVm.Logs[^1].Message.Contains("[原因: テスト理由]"),
          "error log with inline reason is placed on the next line");

    testVm.AddLog("移動失敗: C:\\src.txt -> D:\\dst.txt\n  [原因: 改行テスト]", "ERR");
    Check(testVm.Logs[^2].Message == "移動失敗: C:\\src.txt -> D:\\dst.txt" &&
          testVm.Logs[^1].Message.Contains("[原因: 改行テスト]"),
          "error log with explicit newline is placed on the next line");

    // A destination can appear between the initial existence check and the streamed retry.
    string fallbackDir = Directory.CreateDirectory(Path.Combine(root, "stream-move")).FullName;
    string moveSource = Path.Combine(fallbackDir, "source.bin"), occupied = Path.Combine(fallbackDir, "occupied.bin");
    File.WriteAllText(moveSource, "new");
    File.WriteAllText(occupied, "original");
    Check(!await FileMoveService.TryFallbackMoveViaStreamAsync(moveSource, occupied, false, default) &&
        File.ReadAllText(occupied) == "original" && File.ReadAllText(moveSource) == "new",
        "streamed move without overwrite preserves a competing destination");
    bool canceledMove = false;
    try { await FileMoveService.TryFallbackMoveViaStreamAsync(moveSource, occupied, true, new CancellationToken(true)); }
    catch (OperationCanceledException) { canceledMove = true; }
    Check(canceledMove && File.ReadAllText(occupied) == "original" && File.Exists(moveSource) &&
        Directory.GetFiles(fallbackDir).Length == 2, "canceling a streamed replacement preserves both files and removes partial output");
    Check(await FileMoveService.TryFallbackMoveViaStreamAsync(moveSource, occupied, true, default) &&
        File.ReadAllText(occupied) == "new" && !File.Exists(moveSource),
        "completed streamed replacement publishes the copy before removing its source");

    // Cursor wrap-around & independent sort tests
    var sortTestDir = Directory.CreateDirectory(Path.Combine(root, "sort-pane")).FullName;
    File.WriteAllText(Path.Combine(sortTestDir, "b.txt"), new string('x', 10));
    File.WriteAllText(Path.Combine(sortTestDir, "a.log"), new string('x', 100));
    File.WriteAllText(Path.Combine(sortTestDir, "c.abc"), new string('x', 50));
    File.SetLastWriteTime(Path.Combine(sortTestDir, "b.txt"), new DateTime(2023, 1, 1));
    File.SetLastWriteTime(Path.Combine(sortTestDir, "a.log"), new DateTime(2025, 1, 1));
    File.SetLastWriteTime(Path.Combine(sortTestDir, "c.abc"), new DateTime(2024, 1, 1));

    var leftPaneVm = new MirrorFlux.ViewModels.FilePaneViewModel("左");
    var rightPaneVm = new MirrorFlux.ViewModels.FilePaneViewModel("右");
    leftPaneVm.SetSort(MirrorFlux.ViewModels.PaneSortMode.Name, ascending: true);
    rightPaneVm.SetSort(MirrorFlux.ViewModels.PaneSortMode.Size, ascending: false);
    await leftPaneVm.LoadAsync(sortTestDir);
    await rightPaneVm.LoadAsync(sortTestDir);

    Check(leftPaneVm.Entries[0].IsParent && leftPaneVm.Entries[1].Name == "a.log" && leftPaneVm.Entries[3].Name == "c.abc",
        "left pane sorted by Name ascending");
    Check(rightPaneVm.Entries[0].IsParent && rightPaneVm.Entries[1].Name == "a.log" && rightPaneVm.Entries[2].Name == "c.abc" && rightPaneVm.Entries[3].Name == "b.txt",
        "right pane independently sorted by Size descending");

    rightPaneVm.SetSort(MirrorFlux.ViewModels.PaneSortMode.Size, ascending: true);
    Check(rightPaneVm.Entries[1].Name == "b.txt" && rightPaneVm.Entries[3].Name == "a.log" && rightPaneVm.SortBadgeText == "[サイズ▲]",
        "right pane sorted by Size ascending without affecting left pane");
    Check(leftPaneVm.Entries[1].Name == "a.log" && leftPaneVm.SortBadgeText == "[名前▲]",
        "left pane sort remains unchanged when right pane sort changes");

    Directory.CreateDirectory(Path.Combine(sortTestDir, "z_folder"));
    await leftPaneVm.LoadAsync(sortTestDir);
    Check(leftPaneVm.SortDirectoriesFirst && leftPaneVm.Entries[1].Name == "z_folder" && leftPaneVm.Entries[2].Name == "a.log",
        "by default directories are grouped at the top");
    leftPaneVm.SetSort(MirrorFlux.ViewModels.PaneSortMode.Name, ascending: true, directoriesFirst: false);
    Check(!leftPaneVm.SortDirectoriesFirst && leftPaneVm.Entries[1].Name == "a.log" && leftPaneVm.Entries[^1].Name == "z_folder",
        "when SortDirectoriesFirst is false, directories and files are sorted together");
    var parsedMix = MirrorFlux.ViewModels.FilePaneViewModel.ParseSortSetting(leftPaneVm.FormatSortSetting());
    Check(parsedMix.Mode == MirrorFlux.ViewModels.PaneSortMode.Name && parsedMix.Ascending && !parsedMix.DirectoriesFirst,
        "FormatSortSetting/ParseSortSetting roundtrips SortDirectoriesFirst=false");
    leftPaneVm.SetSort(MirrorFlux.ViewModels.PaneSortMode.Name, ascending: true, directoriesFirst: true);

    // Cursor wrap-around check
    leftPaneVm.SelectedItem = leftPaneVm.Entries[0];
    leftPaneVm.MoveSelection(-1, wrap: true);
    Check(ReferenceEquals(leftPaneVm.SelectedItem, leftPaneVm.Entries[^1]), "cursor wraps from top item to bottom item");
    leftPaneVm.MoveSelection(1, wrap: true);
    Check(ReferenceEquals(leftPaneVm.SelectedItem, leftPaneVm.Entries[0]), "cursor wraps from bottom item to top item");

    var settingsPath = Path.Combine(root, "mf.ini");
    var store = new SettingsStore(settingsPath);
    var settings = new AppSettings { ColMarkFg = "#123456", LogRows = 3, LeftSortMode = "type_desc", RightSortMode = "date_asc" };
    settings.ViewerFunctionLabels[1] = "ラップ切替";
    settings.GraphicViewerFunctionLabels[4] = "AdobePhotoshop";
    settings.ThemeColors["DirectoryText"] = "#ABCDEF";
    settings.ExtBindings.Add(new MirrorFlux.Models.ExtBindingItem(".custom", MirrorFlux.Input.AppCommand.TextViewer, "textviewer"));
    Check(store.TrySave(settings, out var error), "save settings: " + error);
    string savedSettingsText = File.ReadAllText(settingsPath);
    Check(savedSettingsText.Contains("%theme_ScreenBackground\t#000000\t# アプリ全体の画面背景") &&
        !savedSettingsText.Contains("%col_screen", StringComparison.OrdinalIgnoreCase),
        "settings save uses named theme keys with purpose comments");
    Check(savedSettingsText.IndexOf(">KEY_C ", StringComparison.Ordinal) < savedSettingsText.IndexOf(">KEY_D ", StringComparison.Ordinal) &&
        savedSettingsText.IndexOf(">KEY_V ", StringComparison.Ordinal) < savedSettingsText.IndexOf("# 特殊キー", StringComparison.Ordinal) &&
        savedSettingsText.IndexOf("# 特殊キー", StringComparison.Ordinal) < savedSettingsText.IndexOf(">KEY_ENTER ", StringComparison.Ordinal),
        "settings save sorts letter keys before a separate special-key block");
    Check(store.Load().Settings.ColMarkFg == "#123456", "mark foreground survives save/load");
    Check(store.Load().Settings.LeftSortMode == "type_desc" && store.Load().Settings.RightSortMode == "date_asc", "independent left/right sort settings survive save/load");
    Check(store.Load().Settings.ViewerFunctionLabels[1] == "ラップ切替", "viewer fn label survives save/load");
    Check(store.Load().Settings.GraphicViewerFunctionLabels[4] == "AdobePhotoshop", "graphic viewer fn label survives save/load");
    string labelText = File.ReadAllText(settingsPath);
    Check(labelText.Contains("%vfn02\tラップ切替") && labelText.Contains("%fn01\t") && labelText.Contains("%mfn12\t") &&
        labelText.Contains("%gvfn10\t") && !labelText.Contains("%gvfn11"), "function labels are written per key and skip the unused graphic F11");
    string labelAliasPath = Path.Combine(root, "label-alias.ini");
    File.WriteAllText(labelAliasPath, "%fn1\t別名\n%gvfn11\t無効\n%mfn12\t閉じる\n%fn13\t範囲外\n");
    var labelAlias = new SettingsStore(labelAliasPath).Load().Settings;
    Check(labelAlias.FileFunctionLabels[0] == "別名" && labelAlias.GraphicViewerFunctionLabels[10] == "" &&
        labelAlias.MusicFunctionLabels[11] == "閉じる" && labelAlias.FileFunctionLabels.Length == 12,
        "function label keys accept one-digit numbers and ignore graphic F11 and out-of-range keys");
    Check(FileEntry.FormatFileSize(0) == "0 Byte" && FileEntry.FormatFileSize(1023) == "1023 Byte" &&
        FileEntry.FormatFileSize(1024) == "1.00 KByte" && FileEntry.FormatFileSize(51712) == "50.5 KByte" &&
        FileEntry.FormatFileSize(200 * 1024) == "200 KByte" && FileEntry.FormatFileSize(1572864) == "1.50 MByte" &&
        FileEntry.FormatFileSize(3L << 40) == "3.00 TByte" && FileEntry.FormatSizeParts(5L << 30) == ("5.00", "GByte"),
        "file sizes use Byte/KByte/MByte/GByte/TByte rounded to about three digits");
    var viewerZoom = new MirrorFlux.Controls.ViewerViewport(0.05, 20, false);
    viewerZoom.SetSourceSize(200, 100);
    viewerZoom.FitWidth(new System.Windows.Size(424, 400), 24);
    Check(MirrorFlux.Controls.ViewerVisuals.PositionText("Img", 3, 10) == "Img 3/10  [30%]" &&
        MirrorFlux.Controls.ViewerVisuals.PositionText("Page", 1, 1) == "Page 1/1  [100%]" &&
        new MirrorFlux.Controls.ViewerViewport(0.05, 20, false).ZoomLabel == "[Fit: 100%]" && viewerZoom.ZoomLabel == "[Width: 200%]",
        "image and PDF viewers share the zoom and position labels");
    Check(store.Load().Settings.LogRows == 3 && store.Load().Settings.LogHeight == 60, "log row count survives save/load");
    Check(store.Load().Settings.ThemeColors.TryGetValue("directorytext", out var directoryTheme) && directoryTheme == "#ABCDEF", "generic theme color survives save/load case-insensitively");
    Check(store.Load().Settings.ExtBindings.Any(b => b.Extension == ".custom"), "ext binding survives save/load");
    var commentedThemePath = Path.Combine(root, "commented-theme.ini");
    File.WriteAllText(commentedThemePath, "%theme_ScreenBackground #112233 # アプリ背景\n%theme_MarkForeground auto # 自動文字色\n");
    var commentedTheme = new SettingsStore(commentedThemePath).Load().Settings;
    Check(commentedTheme.ColScreen == "#112233" && commentedTheme.ColMarkFg == "auto", "theme values ignore trailing purpose comments");
    var legacyThemePath = Path.Combine(root, "legacy-theme.ini");
    File.WriteAllText(legacyThemePath, "%col_screen #445566\n");
    Check(new SettingsStore(legacyThemePath).Load().Settings.ColScreen == "#445566", "legacy col theme name remains readable");
    var legacyMusicSettingsPath = Path.Combine(root, "legacy-music.ini");
    File.WriteAllText(legacyMusicSettingsPath, ".txt ---- &textviewer;\n.vgm ---- &vgmplayer;\n.vgz ---- &vgmplayer;\n>KEY_ENTER ---- &extexec;\n");
    var upgradedMusicSettings = new SettingsStore(legacyMusicSettingsPath).Load().Settings;
    Check(upgradedMusicSettings.ExtBindings.Any(b => b.Extension == ".txt" && b.Command == MirrorFlux.Input.AppCommand.TextViewer) &&
        upgradedMusicSettings.ExtBindings.Any(b => b.Extension == ".vgm" && b.Modifiers == System.Windows.Input.ModifierKeys.None && b.Command == MirrorFlux.Input.AppCommand.MiniPlayer) &&
        upgradedMusicSettings.ExtBindings.Any(b => b.Extension == ".vgm" && b.Modifiers == System.Windows.Input.ModifierKeys.Shift && b.Command == MirrorFlux.Input.AppCommand.VgmPlayer) &&
        upgradedMusicSettings.ExtBindings.Any(b => b.Extension == ".vgz" && b.Modifiers == System.Windows.Input.ModifierKeys.None && b.Command == MirrorFlux.Input.AppCommand.MiniPlayer) &&
        upgradedMusicSettings.ExtBindings.Any(b => b.Extension == ".vgz" && b.Modifiers == System.Windows.Input.ModifierKeys.Shift && b.Command == MirrorFlux.Input.AppCommand.VgmPlayer),
        "legacy VGM defaults migrate to plain mini and shifted full player");
    Check(upgradedMusicSettings.KeyBindings.Any(b => b.Key == System.Windows.Input.Key.Return &&
        b.Modifiers == System.Windows.Input.ModifierKeys.Shift && b.Command == MirrorFlux.Input.AppCommand.ExtExecWindows),
        "legacy key settings gain Shift+Enter extension execution");
    var template = new SettingsStore(Path.GetFullPath("mf.default.ini")).Load().Settings;
    Check(template.LogRows == 6 && new AppSettings().LogRows == 6 && template.ThemeColors.ContainsKey("MusicMeterLit"), "default template and settings use six log rows and expose named theme colors");
    string[] defaultIniLines = File.ReadAllLines(Path.GetFullPath("mf.default.ini"));
    Check(!defaultIniLines.Any(line => line.StartsWith("%col_", StringComparison.OrdinalIgnoreCase)) &&
        defaultIniLines.Where(line => line.StartsWith("%theme_", StringComparison.OrdinalIgnoreCase)).All(line =>
            System.Text.RegularExpressions.Regex.IsMatch(line, @"\s# \S")),
        "every default theme setting has a purpose comment");
    // 配布用INIは空白で桁を揃える。タブと行末の空白を含めず、種類ごとに値・コメントの開始桁が揃っていること。
    int Column(string line, string pattern) => System.Text.RegularExpressions.Regex.Match(line, pattern).Groups[1].Index;
    var themeLines = defaultIniLines.Where(line => line.StartsWith("%theme_", StringComparison.OrdinalIgnoreCase)).ToArray();
    Check(!defaultIniLines.Any(line => line.Contains('\t') || line != line.TrimEnd()) &&
        themeLines.Select(line => Column(line, @"^\S+\s+(\S)")).Distinct().Count() == 1 &&
        themeLines.Select(line => Column(line, @"\s(# )")).Distinct().Count() == 1 &&
        defaultIniLines.Where(line => line.StartsWith('.')).Select(line => Column(line, @"^\S+\s+(\S)")).Distinct().Count() == 1 &&
        defaultIniLines.Where(line => line.StartsWith('.')).Select(line => Column(line, @"^\S+\s+\S+\s+(&)")).Distinct().Count() == 1,
        "default INI aligns theme and extension columns with spaces only");
    string keySection = "";
    var keyColumns = new Dictionary<string, HashSet<(int Flags, int Comment)>>();
    foreach (string line in defaultIniLines)
    {
        if (line.StartsWith('[')) { keySection = line; continue; }
        if (!line.StartsWith('>')) continue;
        if (!keyColumns.TryGetValue(keySection, out var columns)) keyColumns[keySection] = columns = [];
        columns.Add((Column(line, @"^\S+\s+(\S)"), line.Contains(" # ") ? Column(line, @"\s(# )") : -1));
    }
    Check(keyColumns.Values.All(columns => columns.Select(c => c.Flags).Distinct().Count() == 1 &&
        columns.Where(c => c.Comment >= 0).Select(c => c.Comment).Distinct().Count() <= 1),
        "default INI aligns key flags and trailing comments within each key section");
    string defaultIniText = string.Join('\n', defaultIniLines);
    Check(defaultIniText.IndexOf(">KEY_C ", StringComparison.Ordinal) < defaultIniText.IndexOf(">KEY_D ", StringComparison.Ordinal) &&
        defaultIniText.IndexOf(">KEY_V ", StringComparison.Ordinal) < defaultIniText.IndexOf("# [ 特殊キー ]", StringComparison.Ordinal) &&
        defaultIniText.IndexOf("# [ 特殊キー ]", StringComparison.Ordinal) < defaultIniText.IndexOf(">KEY_ENTER ", StringComparison.Ordinal),
        "default key settings are alphabetized with special keys separated");
    Check(NaturalStringComparer.Instance.Compare("track2.vgm", "track10.vgm") < 0, "Windows Explorer natural name ordering");
    Check(template.KeyBindings.Count == new AppSettings().KeyBindings.Count, "default template bindings");
    Check(template.KeyBindings.Any(b => b.Key == System.Windows.Input.Key.V && b.Command == MirrorFlux.Input.AppCommand.TextViewer) && template.KeyBindings.Any(b => b.Key == System.Windows.Input.Key.V && b.Modifiers == System.Windows.Input.ModifierKeys.Shift && b.Command == MirrorFlux.Input.AppCommand.HexViewer), "V and Shift V open text and hex viewers");
    Check(template.KeyBindings.Any(b => b.Key == System.Windows.Input.Key.Return && b.Modifiers == System.Windows.Input.ModifierKeys.None && b.Command == MirrorFlux.Input.AppCommand.ExtExec), "KEY_ENTER is extexec in default template");
    Check(template.ExtBindings.Any(b => b.Extension == ".txt" && b.Command == MirrorFlux.Input.AppCommand.TextViewer), "default template has txt ext binding");
    Check(template.ExtBindings.Any(b => b.Extension == ".vgm" && b.Command == MirrorFlux.Input.AppCommand.MiniPlayer), "default template has vgm miniplayer ext binding");
    Check(template.ExtBindings.Any(b => b.Extension == ".vgz" && b.Command == MirrorFlux.Input.AppCommand.MiniPlayer), "default template has vgz miniplayer ext binding");
    Check(template.KeyBindings.Any(b => b.Key == System.Windows.Input.Key.Return && b.Modifiers == System.Windows.Input.ModifierKeys.Shift && b.Command == MirrorFlux.Input.AppCommand.ExtExecWindows), "default template has Shift+Enter Windows fallback binding");
    Check(template.KeyBindings.Any(b => b.Key == System.Windows.Input.Key.D && b.Modifiers == System.Windows.Input.ModifierKeys.None && b.Command == MirrorFlux.Input.AppCommand.Delete), "default template has D filedelete binding");
    Check(template.KeyBindings.Any(b => b.Key == System.Windows.Input.Key.S && b.Modifiers == System.Windows.Input.ModifierKeys.None && b.Command == MirrorFlux.Input.AppCommand.Sort), "default template has S sort binding");
    Check(template.KeyBindings.Any(b => b.Key == System.Windows.Input.Key.F10 && b.Modifiers == System.Windows.Input.ModifierKeys.None && b.Command == MirrorFlux.Input.AppCommand.JumpList), "default template has F10 jump list binding");
    Check(new AppSettings().KeyBindings.Last(b => b.Key == System.Windows.Input.Key.F10 && b.Modifiers == System.Windows.Input.ModifierKeys.None).Command == MirrorFlux.Input.AppCommand.JumpList &&
        new AppSettings().FileFunctionLabels[9] == template.FileFunctionLabels[9] && template.FileFunctionLabels[9] == MirrorFlux.Input.FunctionKeyBindings.Label(MirrorFlux.Input.AppCommand.JumpList) &&
        MirrorFlux.Services.SettingsIniReader.Parse("[keys.file]\n>KEY_F10 ---- &sort;\n").KeyBindings.Last(b => b.Key == System.Windows.Input.Key.F10).Command == MirrorFlux.Input.AppCommand.Sort,
        "F10 defaults to the jump list with a matching label, and an explicit sort assignment is kept");
    Check(template.KeyBindings.Any(b => b.Key == System.Windows.Input.Key.OemMinus && b.Modifiers == System.Windows.Input.ModifierKeys.None && b.Command == MirrorFlux.Input.AppCommand.SyncPanePath), "default template has KEY_- samepath binding");
    foreach (string extension in new[] { ".vgm", ".vgz", ".nsf", ".nsfe", ".hes", ".spc", ".gbs", ".ay", ".gym", ".kss", ".sap", ".s98", ".mdx" })
    {
        Check(template.ExtBindings.Any(b => b.Extension == extension && b.Modifiers == System.Windows.Input.ModifierKeys.Shift && b.Command == MirrorFlux.Input.AppCommand.VgmPlayer), $"default template has shifted {extension} full-player binding");
        Check(template.ExtBindings.Any(b => b.Extension == extension && b.Modifiers == System.Windows.Input.ModifierKeys.None && b.Command == MirrorFlux.Input.AppCommand.MiniPlayer), $"default template has plain {extension} mini-player binding");
    }
    Check(template.ViewerFunctionLabels[0] == "ヘルプ" && template.ViewerFunctionLabels[11] == "閉じる", "default template viewer function keys");
    Check(template.GraphicViewerFunctionLabels[0] == "ヘルプ" && template.GraphicViewerFunctionLabels[4] == "PhotoShop" && template.GraphicViewerFunctionLabels[11] == "終了", "default template graphic viewer function keys");
    Check(MirrorFlux.Controls.GraphicViewerControl.IsSupportedExtension(".webp") &&
          MirrorFlux.Controls.GraphicViewerControl.IsSupportedExtension(".heif") &&
          MirrorFlux.Controls.GraphicViewerControl.IsSupportedExtension(".png"), "graphic viewer supports webp, heif, png");

    // VGM ヘッダパース検証
    byte[] dummyVgm = new byte[128];
    dummyVgm[0] = 0x56; dummyVgm[1] = 0x67; dummyVgm[2] = 0x6d; dummyVgm[3] = 0x20; // "Vgm "
    dummyVgm[8] = 0x60; dummyVgm[9] = 0x01; // Version 1.60
    dummyVgm[0x2C] = 0x80; dummyVgm[0x2D] = 0x0A; dummyVgm[0x2E] = 0x75; dummyVgm[0x2F] = 0x00; // YM2612 Clock 7670400 (0x00750A80)
    dummyVgm[0x30] = 0x00; dummyVgm[0x31] = 0x09; dummyVgm[0x32] = 0x3D; dummyVgm[0x33] = 0x00; // YM2151 Clock 4000000
    var parsedVgm = MirrorFlux.Services.Vgm.VgmFile.Load(dummyVgm, "Test Track");
    Check(parsedVgm != null && parsedVgm.TrackName == "Test Track" && parsedVgm.Ym2151Clock == 4000000 && parsedVgm.Ym2612Clock == 7670400, "VgmFile parsed header successfully");

    // VGZ (GZip 圧縮) パース検証
    byte[] compressedVgz;
    using (var ms = new MemoryStream())
    {
        using (var gz = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionLevel.Optimal))
        {
            gz.Write(dummyVgm, 0, dummyVgm.Length);
        }
        compressedVgz = ms.ToArray();
    }
    var parsedVgz = MirrorFlux.Services.Vgm.VgmFile.Load(compressedVgz, "Gzip VGZ Track");
    Check(parsedVgz != null && parsedVgz.TrackName == "Gzip VGZ Track" && parsedVgz.Ym2151Clock == 4000000, "VGZ GZip decompressed and parsed successfully");

    // 日本語エンコーディング検証
    Check(CharsetDetector.ShiftJis.CodePage == 932, "ShiftJis CodePage is 932");
    byte[] sjisBytes = CharsetDetector.ShiftJis.GetBytes("// 日本語テスト Shift_JIS エンコーディング");
    var sjisDetected = CharsetDetector.Detect(sjisBytes, sjisBytes.Length);
    Check(sjisDetected.Encoding.CodePage == 932, "detect Shift_JIS");
    Check(sjisDetected.Encoding.GetString(sjisBytes) == "// 日本語テスト Shift_JIS エンコーディング", "decode Shift_JIS text");

    byte[] utf8Bytes = System.Text.Encoding.UTF8.GetBytes("// 日本語テスト UTF-8 エンコーディング");
    var utf8Detected = CharsetDetector.Detect(utf8Bytes, utf8Bytes.Length);
    Check(utf8Detected.Encoding.CodePage == 65001, "detect UTF-8");
    Check(utf8Detected.Encoding.GetString(utf8Bytes) == "// 日本語テスト UTF-8 エンコーディング", "decode UTF-8 text");

    // Markdown フロードキュメント生成テスト
    string sampleMd = "# 見出し1\n\n段落テキスト **太字** `code`\n\n- リスト項目1\n- リスト項目2\n\n| 列A | 列B |\n|---|---|\n| 1 | 2 |\n";
    var mdDoc = MarkdownFlowDocumentBuilder.Build(sampleMd, new System.Windows.Media.FontFamily("Consolas"), 14.0);
    Check(mdDoc != null && mdDoc.Blocks.Count > 0, "Markdown FlowDocument built successfully");

    // publish/win-x64/mf.ini の検証
    string publishIniPath = Path.GetFullPath("publish/win-x64/mf.ini");
    if (File.Exists(publishIniPath))
    {
        var publishSettings = new SettingsStore(publishIniPath).Load().Settings;
        Check(publishSettings.KeyBindings.Any(b => b.Key == System.Windows.Input.Key.Return && b.Command == MirrorFlux.Input.AppCommand.ExtExec), "publish ini has KEY_ENTER = extexec");
        Check(publishSettings.ExtBindings.Any(b => b.Extension == ".txt" && b.Command == MirrorFlux.Input.AppCommand.TextViewer), "publish ini has .txt binding");
        Check(publishSettings.ExtBindings.Any(b => b.Extension == ".md" && b.Command == MirrorFlux.Input.AppCommand.TextViewer), "publish ini has .md binding");
        Check(publishSettings.ExtBindings.Any(b => b.Extension == ".png" && b.Command == MirrorFlux.Input.AppCommand.GraphicViewer), "publish ini has .png binding");
        Check(publishSettings.ExtBindings.Any(b => b.Extension == ".vgm" && b.Modifiers == System.Windows.Input.ModifierKeys.None && b.Command == MirrorFlux.Input.AppCommand.MiniPlayer) &&
            publishSettings.ExtBindings.Any(b => b.Extension == ".vgm" && b.Modifiers == System.Windows.Input.ModifierKeys.Shift && b.Command == MirrorFlux.Input.AppCommand.VgmPlayer),
            "publish ini resolves plain VGM to mini and shifted VGM to full player");
    }

    // アーカイブ内ファイルの一時抽出と内蔵ビューアー検証
    var testArchiveZip = Path.Combine(root, "test_viewer.zip");
    using (var archive = ZipFile.Open(testArchiveZip, ZipArchiveMode.Create))
    {
        using var w = new StreamWriter(archive.CreateEntry("doc/readme.md").Open());
        w.Write("# Hello from Archive\n\nThis is a test markdown file inside archive.");
    }
    string virtualEntryPath = Path.Combine(testArchiveZip, "doc", "readme.md");
    Check(ArchiveService.IsSupportedArchive(testArchiveZip), "testArchiveZip is supported archive");
    Check(ArchiveService.IsInsideArchive(testArchiveZip), "testArchiveZip path is inside archive browsing context");
    Check(!ArchiveService.IsArchiveEntry(testArchiveZip), "testArchiveZip is not an entry");
    var archivePane = new MirrorFlux.ViewModels.FilePaneViewModel("test");
    await archivePane.LoadAsync(Path.Combine(testArchiveZip, "doc"));
    await archivePane.LoadAsync(testArchiveZip);
    Check(archivePane.SelectedItem?.Name == "doc", "leaving archive subfolder selects that folder");
    await archivePane.LoadAsync(root);
    Check(archivePane.SelectedItem?.FullPath == testArchiveZip, "leaving archive selects the archive file");
    Check(ArchiveService.IsInsideArchive(virtualEntryPath), "virtualEntryPath is inside archive");
    Check(ArchiveService.IsArchiveEntry(virtualEntryPath), "virtualEntryPath is an archive entry");
    string? tempExtracted = await ArchiveService.ExtractEntryToTempFileAsync(virtualEntryPath);
    Check(!string.IsNullOrEmpty(tempExtracted) && File.Exists(tempExtracted), "archive entry extracted to temp file");
    Check(File.ReadAllText(tempExtracted!).Contains("Hello from Archive"), "extracted content matches archive content");
    ArchiveService.CleanupTempFiles();

    // ゴミ箱移動テスト
    string testTrashFile = Path.Combine(root, "test_trash.txt");
    File.WriteAllText(testTrashFile, "Trash file content");
    Check(File.Exists(testTrashFile), "test trash file created");
    bool trashResult = RecycleBinService.DeleteToRecycleBin(testTrashFile);
    Check(trashResult && !File.Exists(testTrashFile), "file sent to recycle bin successfully");

    // IndependentVgmWaveProvider の PCM 合成実動テスト (SN76489 PSG 単体/併用)
    byte[] testVgmData = new byte[0x80];
    testVgmData[0] = 0x56; testVgmData[1] = 0x67; testVgmData[2] = 0x6d; testVgmData[3] = 0x20; // "Vgm "
    testVgmData[8] = 0x60; testVgmData[9] = 0x01; // v1.60
    testVgmData[0x0C] = 0x99; testVgmData[0x0D] = 0x9E; testVgmData[0x0E] = 0x36; testVgmData[0x0F] = 0x00; // SN76489 Clock 3579545 (0x00369E99)
    testVgmData[0x2C] = 0x80; testVgmData[0x2D] = 0x0A; testVgmData[0x2E] = 0x75; testVgmData[0x2F] = 0x00; // YM2612 Clock 7670400
    testVgmData[0x34] = 0x0C; // DataOffset = 0x34 + 0x0C = 0x40
    int p = 0x40;
    // SN76489 音出し (0x50 1バイトオペランド)
    testVgmData[p++] = 0x50; testVgmData[p++] = 0x80; // Tone 0 freq low
    testVgmData[p++] = 0x50; testVgmData[p++] = 0x01; // Tone 0 freq high
    testVgmData[p++] = 0x50; testVgmData[p++] = 0x90; // Tone 0 vol max
    testVgmData[p++] = 0x61; testVgmData[p++] = 0x00; testVgmData[p++] = 0x04; // wait 1024 samples
    testVgmData[p++] = 0x66; // End of sound data
    var testVgm = MirrorFlux.Services.Vgm.VgmFile.Load(testVgmData, "PsgTest");
    Check(testVgm != null && testVgm.Sn76489Clock == 3579545, "testVgm loaded with SN76489 clock");
    using (var provider = new MirrorFlux.Services.Vgm.IndependentVgmWaveProvider(testVgm!))
    {
        byte[] pcmBuf = new byte[2048 * 4];
        int readBytes = provider.Read(pcmBuf, 0, pcmBuf.Length);
        Check(readBytes > 0 && readBytes <= pcmBuf.Length, "IndependentVgmWaveProvider synthesized PSG PCM buffer");
        bool hasSound = pcmBuf.Any(b => b != 0);
        Check(hasSound, "IndependentVgmWaveProvider PSG produced non-silent PCM waveform");
    }

    // FM音源 (YM2612 / OPNA コア) 単体合成テスト (PSGクロック=0でFMのみ検証)
    byte[] testFmVgm = new byte[0x100];
    testFmVgm[0] = 0x56; testFmVgm[1] = 0x67; testFmVgm[2] = 0x6d; testFmVgm[3] = 0x20;
    testFmVgm[8] = 0x60; testFmVgm[9] = 0x01;
    testFmVgm[0x0C] = 0x00; testFmVgm[0x0D] = 0x00; testFmVgm[0x0E] = 0x00; testFmVgm[0x0F] = 0x00; // SN76489 Clock = 0 (PSGなし)
    testFmVgm[0x2C] = 0x80; testFmVgm[0x2D] = 0x0A; testFmVgm[0x2E] = 0x75; testFmVgm[0x2F] = 0x00; // YM2612 Clock 7670400
    testFmVgm[0x34] = 0x0C;
    int pFm = 0x40;
    // YM2612 FM Ch1 セットアップ (Alg 7, Pan L+R, KeyOn)
    testFmVgm[pFm++] = 0x52; testFmVgm[pFm++] = 0x30; testFmVgm[pFm++] = 0x01; // DT/MUL Op1
    testFmVgm[pFm++] = 0x52; testFmVgm[pFm++] = 0x34; testFmVgm[pFm++] = 0x01; // DT/MUL Op2
    testFmVgm[pFm++] = 0x52; testFmVgm[pFm++] = 0x38; testFmVgm[pFm++] = 0x01; // DT/MUL Op3
    testFmVgm[pFm++] = 0x52; testFmVgm[pFm++] = 0x3C; testFmVgm[pFm++] = 0x01; // DT/MUL Op4
    testFmVgm[pFm++] = 0x52; testFmVgm[pFm++] = 0x40; testFmVgm[pFm++] = 0x00; // TL Op1 (max vol)
    testFmVgm[pFm++] = 0x52; testFmVgm[pFm++] = 0x44; testFmVgm[pFm++] = 0x00; // TL Op2
    testFmVgm[pFm++] = 0x52; testFmVgm[pFm++] = 0x48; testFmVgm[pFm++] = 0x00; // TL Op3
    testFmVgm[pFm++] = 0x52; testFmVgm[pFm++] = 0x4C; testFmVgm[pFm++] = 0x00; // TL Op4
    testFmVgm[pFm++] = 0x52; testFmVgm[pFm++] = 0x50; testFmVgm[pFm++] = 0x1F; // AR Op1 (31)
    testFmVgm[pFm++] = 0x52; testFmVgm[pFm++] = 0x54; testFmVgm[pFm++] = 0x1F; // AR Op2
    testFmVgm[pFm++] = 0x52; testFmVgm[pFm++] = 0x58; testFmVgm[pFm++] = 0x1F; // AR Op3
    testFmVgm[pFm++] = 0x52; testFmVgm[pFm++] = 0x5C; testFmVgm[pFm++] = 0x1F; // AR Op4
    testFmVgm[pFm++] = 0x52; testFmVgm[pFm++] = 0x80; testFmVgm[pFm++] = 0x0F; // RR Op1
    testFmVgm[pFm++] = 0x52; testFmVgm[pFm++] = 0x84; testFmVgm[pFm++] = 0x0F; // RR Op2
    testFmVgm[pFm++] = 0x52; testFmVgm[pFm++] = 0x88; testFmVgm[pFm++] = 0x0F; // RR Op3
    testFmVgm[pFm++] = 0x52; testFmVgm[pFm++] = 0x8C; testFmVgm[pFm++] = 0x0F; // RR Op4
    testFmVgm[pFm++] = 0x52; testFmVgm[pFm++] = 0xB0; testFmVgm[pFm++] = 0x07; // Alg 7
    testFmVgm[pFm++] = 0x52; testFmVgm[pFm++] = 0xB4; testFmVgm[pFm++] = 0xC0; // Pan L+R
    testFmVgm[pFm++] = 0x52; testFmVgm[pFm++] = 0xA4; testFmVgm[pFm++] = 0x22; // Block 4 / F-Num high
    testFmVgm[pFm++] = 0x52; testFmVgm[pFm++] = 0xA0; testFmVgm[pFm++] = 0x60; // F-Num low
    testFmVgm[pFm++] = 0x52; testFmVgm[pFm++] = 0x28; testFmVgm[pFm++] = 0xF0; // KeyOn Ch1
    testFmVgm[pFm++] = 0x61; testFmVgm[pFm++] = 0x00; testFmVgm[pFm++] = 0x04; // wait 1024 samples
    testFmVgm[pFm++] = 0x66; // End
    var fmVgm = MirrorFlux.Services.Vgm.VgmFile.Load(testFmVgm, "FmAloneTest");
    Check(fmVgm != null && fmVgm.Ym2612Clock == 7670400 && fmVgm.Sn76489Clock == 0, "fmVgm loaded with YM2612 clock only");
    using (var fmProvider = new MirrorFlux.Services.Vgm.IndependentVgmWaveProvider(fmVgm!))
    {
        byte[] pcmBuf = new byte[2048 * 4];
        int readBytes = fmProvider.Read(pcmBuf, 0, pcmBuf.Length);
        Check(readBytes > 0 && readBytes <= pcmBuf.Length, "IndependentVgmWaveProvider synthesized pure FM PCM buffer");
        bool hasFmSound = pcmBuf.Any(b => b != 0);
        Check(hasFmSound, "IndependentVgmWaveProvider YM2612 FM synthesis produced non-silent PCM waveform");
    }

    // YM2612 DAC PCM: VGM data block + DAC Stream Control commands must reach the audio core.
    byte[] testDacVgm = new byte[0x90];
    testDacVgm[0] = 0x56; testDacVgm[1] = 0x67; testDacVgm[2] = 0x6d; testDacVgm[3] = 0x20;
    testDacVgm[8] = 0x60; testDacVgm[9] = 0x01;
    testDacVgm[0x2C] = 0x80; testDacVgm[0x2D] = 0x0A; testDacVgm[0x2E] = 0x75; // YM2612 7670400 Hz
    testDacVgm[0x34] = 0x0C;
    int pDac = 0x40;
    testDacVgm[pDac++] = 0x52; testDacVgm[pDac++] = 0x2B; testDacVgm[pDac++] = 0x80; // DAC enable
    testDacVgm[pDac++] = 0x67; testDacVgm[pDac++] = 0x66; testDacVgm[pDac++] = 0x00; // YM2612 PCM data block
    testDacVgm[pDac++] = 0x04; testDacVgm[pDac++] = 0x00; testDacVgm[pDac++] = 0x00; testDacVgm[pDac++] = 0x00;
    testDacVgm[pDac++] = 0x00; testDacVgm[pDac++] = 0x20; testDacVgm[pDac++] = 0xE0; testDacVgm[pDac++] = 0xFF;
    testDacVgm[pDac++] = 0x90; testDacVgm[pDac++] = 0x00; testDacVgm[pDac++] = 0x02; testDacVgm[pDac++] = 0x00; testDacVgm[pDac++] = 0x2A;
    testDacVgm[pDac++] = 0x91; testDacVgm[pDac++] = 0x00; testDacVgm[pDac++] = 0x00; testDacVgm[pDac++] = 0x01; testDacVgm[pDac++] = 0x00;
    testDacVgm[pDac++] = 0x92; testDacVgm[pDac++] = 0x00; testDacVgm[pDac++] = 0x44; testDacVgm[pDac++] = 0xAC; testDacVgm[pDac++] = 0x00; testDacVgm[pDac++] = 0x00; // 44.1kHz
    testDacVgm[pDac++] = 0x93; testDacVgm[pDac++] = 0x00; testDacVgm[pDac++] = 0x00; testDacVgm[pDac++] = 0x00; testDacVgm[pDac++] = 0x00; testDacVgm[pDac++] = 0x00;
    testDacVgm[pDac++] = 0x01; testDacVgm[pDac++] = 0x04; testDacVgm[pDac++] = 0x00; testDacVgm[pDac++] = 0x00; testDacVgm[pDac++] = 0x00;
    testDacVgm[pDac++] = 0x61; testDacVgm[pDac++] = 0x00; testDacVgm[pDac++] = 0x04; testDacVgm[pDac++] = 0x66;
    var dacVgm = MirrorFlux.Services.Vgm.VgmFile.Load(testDacVgm, "DacTest");
    using (var dacProvider = new MirrorFlux.Services.Vgm.IndependentVgmWaveProvider(dacVgm!))
    {
        byte[] pcmBuf = new byte[2048 * 4];
        dacProvider.Read(pcmBuf, 0, pcmBuf.Length);
        Check(pcmBuf.Any(b => b != 0), "IndependentVgmWaveProvider YM2612 DAC Stream Control produced non-silent PCM waveform");
        Check(dacProvider.GetSnapshot().MusicChannels.Any(channel => channel.Kind == MirrorFlux.Services.Music.MusicChannelKind.Pcm && channel.Label == "DAC"),
            "IndependentVgmWaveProvider reports YM2612 DAC as a PCM channel");
    }

    // FM音源 (YM2151 / OPM) 合成テスト
    byte[] testOpmVgm = new byte[0x100];
    testOpmVgm[0] = 0x56; testOpmVgm[1] = 0x67; testOpmVgm[2] = 0x6d; testOpmVgm[3] = 0x20;
    testOpmVgm[8] = 0x60; testOpmVgm[9] = 0x01;
    testOpmVgm[0x30] = 0x00; testOpmVgm[0x31] = 0x09; testOpmVgm[0x32] = 0x3D; testOpmVgm[0x33] = 0x00; // YM2151 Clock 4000000 (0x003D0900)
    testOpmVgm[0x34] = 0x0C;
    int pOpm = 0x40;
    // YM2151 Ch0 セットアップ
    testOpmVgm[pOpm++] = 0x54; testOpmVgm[pOpm++] = 0x20; testOpmVgm[pOpm++] = 0xC7; // Pan L+R, Alg 7
    testOpmVgm[pOpm++] = 0x54; testOpmVgm[pOpm++] = 0x40; testOpmVgm[pOpm++] = 0x01; // DT1/MUL
    testOpmVgm[pOpm++] = 0x54; testOpmVgm[pOpm++] = 0x48; testOpmVgm[pOpm++] = 0x01;
    testOpmVgm[pOpm++] = 0x54; testOpmVgm[pOpm++] = 0x50; testOpmVgm[pOpm++] = 0x01;
    testOpmVgm[pOpm++] = 0x54; testOpmVgm[pOpm++] = 0x58; testOpmVgm[pOpm++] = 0x01;
    testOpmVgm[pOpm++] = 0x54; testOpmVgm[pOpm++] = 0x60; testOpmVgm[pOpm++] = 0x00; // TL = 0
    testOpmVgm[pOpm++] = 0x54; testOpmVgm[pOpm++] = 0x68; testOpmVgm[pOpm++] = 0x00;
    testOpmVgm[pOpm++] = 0x54; testOpmVgm[pOpm++] = 0x70; testOpmVgm[pOpm++] = 0x00;
    testOpmVgm[pOpm++] = 0x54; testOpmVgm[pOpm++] = 0x78; testOpmVgm[pOpm++] = 0x00;
    testOpmVgm[pOpm++] = 0x54; testOpmVgm[pOpm++] = 0x80; testOpmVgm[pOpm++] = 0x1F; // AR = 31
    testOpmVgm[pOpm++] = 0x54; testOpmVgm[pOpm++] = 0x88; testOpmVgm[pOpm++] = 0x1F;
    testOpmVgm[pOpm++] = 0x54; testOpmVgm[pOpm++] = 0x90; testOpmVgm[pOpm++] = 0x1F;
    testOpmVgm[pOpm++] = 0x54; testOpmVgm[pOpm++] = 0x98; testOpmVgm[pOpm++] = 0x1F;
    testOpmVgm[pOpm++] = 0x54; testOpmVgm[pOpm++] = 0x28; testOpmVgm[pOpm++] = 0x45; // Note
    testOpmVgm[pOpm++] = 0x54; testOpmVgm[pOpm++] = 0x08; testOpmVgm[pOpm++] = 0x78; // KeyOn Ch0
    testOpmVgm[pOpm++] = 0x61; testOpmVgm[pOpm++] = 0x00; testOpmVgm[pOpm++] = 0x04; // wait 1024 samples
    testOpmVgm[pOpm++] = 0x66;
    var opmVgm = MirrorFlux.Services.Vgm.VgmFile.Load(testOpmVgm, "OpmTest");
    Check(opmVgm != null && opmVgm.Ym2151Clock == 4000000, "opmVgm loaded with YM2151 clock");
    using (var opmProvider = new MirrorFlux.Services.Vgm.IndependentVgmWaveProvider(opmVgm!))
    {
        byte[] pcmBuf = new byte[2048 * 4];
        int readBytes = opmProvider.Read(pcmBuf, 0, pcmBuf.Length);
        Check(readBytes > 0 && readBytes <= pcmBuf.Length, "IndependentVgmWaveProvider synthesized OPM PCM buffer");
        bool hasOpmSound = pcmBuf.Any(b => b != 0);
        Check(hasOpmSound, "IndependentVgmWaveProvider YM2151 (OPM) produced non-silent PCM waveform");
    }

    // MiniPlayer / ExtBinding & KeyBinding テスト
    var defaultSettings = new MirrorFlux.Models.AppSettings();
    var vgmItem = defaultSettings.ExtBindings.FirstOrDefault(b => b.Extension.Equals(".vgm", StringComparison.OrdinalIgnoreCase));
    Check(vgmItem != null && vgmItem.Command == MirrorFlux.Input.AppCommand.MiniPlayer,
        "default ExtBindings maps .vgm to MiniPlayer");
    var vgzItem = defaultSettings.ExtBindings.FirstOrDefault(b => b.Extension.Equals(".vgz", StringComparison.OrdinalIgnoreCase));
    Check(vgzItem != null && vgzItem.Command == MirrorFlux.Input.AppCommand.MiniPlayer,
        "default ExtBindings maps .vgz to MiniPlayer");
    var shiftEnterBinding = defaultSettings.KeyBindings.FirstOrDefault(b => b.Key == System.Windows.Input.Key.Return && b.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Shift));
    Check(shiftEnterBinding?.Command == MirrorFlux.Input.AppCommand.ExtExecWindows, "default KeyBindings maps Shift+Enter to Windows fallback execution");
    var keys = new MirrorFlux.Input.KeyBindingService();
    Check(keys.TryResolve(System.Windows.Input.Key.Return, System.Windows.Input.ModifierKeys.Shift, out var shifted)
        && shifted.Command == MirrorFlux.Input.AppCommand.ExtExecWindows && shifted.Modifiers == System.Windows.Input.ModifierKeys.Shift,
        "Shift+Enter reaches modifier-aware Windows fallback execution");
    Check(keys.TryResolve(System.Windows.Input.Key.D, System.Windows.Input.ModifierKeys.None, out var dDelete)
        && dDelete.Command == MirrorFlux.Input.AppCommand.Delete, "D reaches filedelete");
    Check(MirrorFlux.Input.IniKeyParser.TryParseExtBinding(".vgm -s-- &vgmplayer;", out var extShift)
        && extShift!.Modifiers == System.Windows.Input.ModifierKeys.Shift
        && MirrorFlux.Input.IniKeyParser.TryParseExtBinding(MirrorFlux.Input.IniKeyParser.FormatExtBinding(extShift), out var extRoundtrip)
        && extRoundtrip == extShift, "extension modifiers survive parse and save roundtrip");

    Check(MirrorFlux.Input.IniKeyParser.TryParseCommand("miniplayer", out var parsedCmd1) && parsedCmd1 == MirrorFlux.Input.AppCommand.MiniPlayer,
        "IniKeyParser parses 'miniplayer'");
    Check(MirrorFlux.Input.IniKeyParser.TryParseCommand("vgmplay", out var parsedCmd2) && parsedCmd2 == MirrorFlux.Input.AppCommand.MiniPlayer,
        "IniKeyParser parses 'vgmplay' as MiniPlayer");
    var miniItem = new MirrorFlux.Models.KeyBindingItem(System.Windows.Input.Key.Return, System.Windows.Input.ModifierKeys.None, MirrorFlux.Input.AppCommand.MiniPlayer, "miniplayer");
    Check(MirrorFlux.Input.IniKeyParser.FormatLine(miniItem).Contains("&miniplayer;"),
        "IniKeyParser formats MiniPlayer as '&miniplayer;'");

    // PDF ビューア (&pdfviewer;) テスト
    Check(MirrorFlux.Input.IniKeyParser.TryParseCommand("pdfviewer", out var parsedPdfCmd) && parsedPdfCmd == MirrorFlux.Input.AppCommand.PdfViewer,
        "IniKeyParser parses 'pdfviewer' as AppCommand.PdfViewer");
    Check(MirrorFlux.Input.IniKeyParser.TryParseExtBinding(".pdf\t----\t&pdfviewer;", out var parsedPdfExt)
        && parsedPdfExt != null
        && parsedPdfExt.Extension == ".pdf"
        && parsedPdfExt.Command == MirrorFlux.Input.AppCommand.PdfViewer,
        "IniKeyParser parses '.pdf ---- &pdfviewer;'");
    var pdfExtDefault = defaultSettings.ExtBindings.FirstOrDefault(b => b.Extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase));
    Check(pdfExtDefault != null && pdfExtDefault.Command == MirrorFlux.Input.AppCommand.PdfViewer,
        "default ExtBindings maps .pdf to PdfViewer");

    var samplePdfPath = Path.Combine(root, "sample.pdf");
    File.WriteAllText(samplePdfPath,
        "%PDF-1.4\n" +
        "1 0 obj << /Type /Catalog /Pages 2 0 R >> endobj\n" +
        "2 0 obj << /Type /Pages /Kids [3 0 R] /Count 1 >> endobj\n" +
        "3 0 obj << /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] >> endobj\n" +
        "xref\n0 4\n0000000000 65535 f \n0000000009 00000 n \n0000000058 00000 n \n0000000115 00000 n \n" +
        "trailer << /Size 4 /Root 1 0 R >>\nstartxref\n186\n%%EOF\n",
        System.Text.Encoding.ASCII);
    using (var pdfDoc = await MirrorFlux.Services.WindowsPdfDocument.OpenAsync(samplePdfPath))
    {
        Check(pdfDoc != null && pdfDoc.PageCount == 1, "WindowsPdfDocument opened 1-page PDF");
        var pageSize = pdfDoc!.GetPageSize(0);
        Check(pageSize.Width > 0 && pageSize.Height > 0 && Math.Abs((pageSize.Width / pageSize.Height) - 2.0) < 0.05, "WindowsPdfDocument read PDF MediaBox aspect ratio and size");
        var rendered = await pdfDoc.RenderPageAsync(0, 200, 100);
        Check(rendered.Bitmap != null && rendered.Bitmap.PixelWidth > 0 && rendered.Bitmap.PixelHeight > 0, "WindowsPdfDocument rendered PDF page to BitmapSource");
    }

    var kbs = new MirrorFlux.Input.KeyBindingService();
    Check(kbs.TryResolve(System.Windows.Input.Key.OemMinus, System.Windows.Input.ModifierKeys.None, out var cmdMinus) &&
        cmdMinus.Command == MirrorFlux.Input.AppCommand.SyncPanePath, "KeyBindingService resolves OemMinus as SyncPanePath");
    Check(kbs.TryResolve(System.Windows.Input.Key.Subtract, System.Windows.Input.ModifierKeys.None, out var cmdSub) &&
        cmdSub.Command == MirrorFlux.Input.AppCommand.SyncPanePath, "KeyBindingService resolves Subtract (numpad minus) as SyncPanePath");
    var syncItem = new MirrorFlux.Models.KeyBindingItem(System.Windows.Input.Key.OemMinus, System.Windows.Input.ModifierKeys.None, MirrorFlux.Input.AppCommand.SyncPanePath, "samepath");
    Check(MirrorFlux.Input.IniKeyParser.FormatLine(syncItem).Contains("KEY_-") && MirrorFlux.Input.IniKeyParser.FormatLine(syncItem).Contains("&samepath;"),
        "IniKeyParser formats SyncPanePath with KEY_- and &samepath;");

    MirrorFlux.Tests.NdsifRegression.Run(Check);

    // LogLevel (0..3) and Loading guard regression checks
    {
        var logIniPath = Path.Combine(root, "test-loglevel.ini");
        File.WriteAllText(logIniPath, "%logLevel\t1\n");
        var logStore = new SettingsStore(logIniPath);
        var loadedLogSettings = logStore.Load().Settings;
        Check(loadedLogSettings.LogLevel == 1, "SettingsStore loads %logLevel 1");
        loadedLogSettings.LogLevel = 3;
        Check(logStore.TrySave(loadedLogSettings, out _), "SettingsStore saves %logLevel 3");
        Check(File.ReadAllText(logIniPath).Contains("%logLevel\t3"), "Saved ini contains %logLevel 3");

        var logVm = new MirrorFlux.ViewModels.MainViewModel(new SettingsStore(Path.Combine(root, "test-logvm.ini")));
        Check(logVm.LogLevel == 2, "Default LogLevel is 2 (標準)");
        logVm.AddLog("重大エラー", "ERR"); // Lv0
        logVm.AddLog("アーカイブ内表示: C:\\test.zip", "ACT"); // Lv1
        logVm.AddLog("フォルダ移動: C:\\work", "ACT"); // Lv2
        logVm.AddLog("マーク ON: a.txt", "MARK"); // Lv3
        logVm.AddLog("パネル切替: 右パネル", "ACT"); // Lv3

        // At Lv2, Lv3 messages should be hidden
        Check(logVm.Logs.Any(l => l.Message == "重大エラー") &&
              logVm.Logs.Any(l => l.Message == "アーカイブ内表示: C:\\test.zip") &&
              logVm.Logs.Any(l => l.Message == "フォルダ移動: C:\\work") &&
              !logVm.Logs.Any(l => l.Message == "マーク ON: a.txt") &&
              !logVm.Logs.Any(l => l.Message == "パネル切替: 右パネル"),
              "LogLevel 2 shows Lv0..2 and hides Lv3");

        // Switch to Lv0: only ERR/WARN/CONF/HELP shown
        logVm.LogLevel = 0;
        Check(logVm.Logs.Any(l => l.Message == "重大エラー") &&
              !logVm.Logs.Any(l => l.Message == "アーカイブ内表示: C:\\test.zip") &&
              !logVm.Logs.Any(l => l.Message == "フォルダ移動: C:\\work"),
              "LogLevel 0 shows only Lv0 messages");

        // Switch to Lv1: Lv0 and Lv1 shown, Lv2+ hidden
        logVm.LogLevel = 1;
        Check(logVm.Logs.Any(l => l.Message == "アーカイブ内表示: C:\\test.zip") &&
              !logVm.Logs.Any(l => l.Message == "フォルダ移動: C:\\work"),
              "LogLevel 1 shows archive open and summary, hides folder step");

        // Switch to Lv3: all messages shown
        logVm.LogLevel = 3;
        Check(logVm.Logs.Any(l => l.Message == "マーク ON: a.txt") &&
              logVm.Logs.Any(l => l.Message == "パネル切替: 右パネル"),
              "LogLevel 3 reveals verbose messages");

        bool fastSawOverlay = false;
        await logVm.RunWithLoadingAsync("Fast task", async () =>
        {
            await Task.Delay(50);
            fastSawOverlay = logVm.IsLoadingOverlayVisible;
        });
        Check(!fastSawOverlay && !logVm.IsLoadingOverlayVisible && !logVm.IsLoading,
              "Loading overlay remains hidden for operations completing under 0.5s");

        bool slowSawOverlayBeforeThreshold = true;
        bool slowSawOverlayAfterThreshold = false;
        await logVm.RunWithLoadingAsync("Slow task", async () =>
        {
            await Task.Delay(100);
            slowSawOverlayBeforeThreshold = logVm.IsLoadingOverlayVisible;
            await Task.Delay(500);
            slowSawOverlayAfterThreshold = logVm.IsLoadingOverlayVisible;
            Check(logVm.LoadingTitle == "アーカイブ作成中...", "Archive progress shows its operation title");
            await logVm.RunWithLoadingAsync("Nested read", () => Task.CompletedTask);
            Check(logVm.LoadingTitle == "アーカイブ作成中..." && logVm.LoadingMessage == "Slow task",
                  "Nested loading restores the surrounding operation title and detail");
        }, loadingTitle: "アーカイブ作成中...");
        Check(logVm.LoadingTitle == "NOW LOADING...", "Archive progress title does not leak into later reads");
        Check(!slowSawOverlayBeforeThreshold && slowSawOverlayAfterThreshold && !logVm.IsLoadingOverlayVisible && !logVm.IsLoading,
              "Loading overlay becomes visible after 0.5s and hides on completion");
    }

    Console.WriteLine($"Passed {passed} regression checks.");
}
finally
{
    if (Path.GetDirectoryName(Path.GetFullPath(root)) == Path.TrimEndingDirectorySeparator(Path.GetTempPath()) &&
        Path.GetFileName(root).StartsWith("MirrorFlux-tests-", StringComparison.Ordinal))
        Directory.Delete(root, true);
}
