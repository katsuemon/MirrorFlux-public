using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MirrorFlux.Controls;
using MirrorFlux.Services.Music;

internal static class MusicDpiRendering
{
    public static void Run()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                foreach (double scale in new[] { 1.0, 1.25, 1.5, 2.0 })
                {
                    var mini = new MiniPlayerControl();
                    mini.RenderSnapshot(new()
                    {
                        Title = "DPI keyboard / meters", IsPlaying = true,
                        Channels = Enumerable.Range(0, 16).Select(i => new MusicChannelSnapshot
                        { NoteNumber = 49 + i, IsKeyOn = true, Level = .65, Pan = 3 }).ToArray()
                    });
                    mini.Measure(new Size(mini.Width, 120));
                    VisualTreeHelper.SetRootDpi(mini, new DpiScale(scale, scale));
                    mini.InvalidateMeasure();
                    mini.Measure(new Size(mini.Width, 120));
                    mini.Arrange(new Rect(0, 0, mini.Width, 120));
                    mini.UpdateLayout();
                    var miniKeys = (MusicKeyboardDisplay)mini.FindName("KeyboardDisplay");
                    var miniMeter = (MusicMeterDisplay)mini.FindName("MeterDisplay");
                    var miniPan = (MusicPanDisplay)mini.FindName("PanDisplay");
                    if (Math.Abs(miniKeys.ActualWidth - 63 * MusicKeyboardDisplay.KeyPitch(scale)) > .5 / scale || miniMeter.ActualWidth != miniKeys.ActualWidth || miniPan.ActualWidth != miniKeys.ActualWidth)
                        throw new Exception("Mini keyboard, meter and pan widths do not match");
                    if (Math.Abs(miniMeter.GetBarLeft(0)) > .001 || Math.Abs(miniMeter.GetBarRight(15) - (miniKeys.ActualWidth - 1 / scale)) > .001)
                        throw new Exception("Mini meter painted edges do not match the keyboard");
                    for (int channel = 0; channel < 16; channel++)
                    {
                        if (Math.Abs(miniMeter.GetChannelCenter(channel) - miniPan.GetChannelCenter(channel)) > .001)
                            throw new Exception("Mini meter and pan centers differ");
                        if (channel > 0 && Math.Abs((miniMeter.GetBarLeft(channel) - miniMeter.GetBarRight(channel - 1)) * scale - 2) > .001)
                            throw new Exception("Mini meter horizontal gaps differ");
                    }
                    var rows = new MusicKeyboardRowsDisplay();
                    rows.SetChannels(Enumerable.Range(0, 40).Select(i => new MusicChannelSnapshot { Id = i.ToString() }).ToArray());
                    rows.ScrollChannels(36);
                    if (rows.FirstChannel != 24 || rows.MaximumFirstChannel != 24) throw new Exception("Keyboard scroll does not stop at channel 36");
                    rows.SetChannels(Enumerable.Range(0, 40).Select(i => new MusicChannelSnapshot { Id = i.ToString() }).ToArray());
                    if (rows.FirstChannel != 24) throw new Exception("Playback update reset keyboard scroll");
                    rows.ScrollChannels(-12);
                    if (rows.FirstChannel != 12) throw new Exception("Keyboard page up failed");
                    rows.SetChannels(new MusicChannelSnapshot[0]);
                    if (rows.FirstChannel != 0) throw new Exception("Keyboard scroll was not clamped after track change");
                    if (new MusicChannelSnapshot { Kind = MusicChannelKind.Adpcm }.KindLabel != "ADPCM") throw new Exception("ADPCM label missing");
                    var viewer = new MusicPlayerControl();
                    var frame = new MusicPlaybackSnapshot
                    {
                        Channels = Enumerable.Range(0, 40).Select(i => new MusicChannelSnapshot
                        {
                            Id = i.ToString(), ChipName = "TEST", Kind = MusicChannelKind.Fm,
                            Level = (i + 1) / 40.0, NoteNumber = i, IsKeyOn = true
                        }).ToArray(),
                        RegisterBanks = [new() { ChipName = "TEST" }]
                    };
                    viewer.RenderSnapshot(frame);
                    var viewerRows = (MusicKeyboardRowsDisplay)viewer.FindName("KeyboardRows");
                    var viewerMeter = (MusicMeterDisplay)viewer.FindName("ViewerLevelDisplay");
                    var viewerNotes = (MusicNoteNumberDisplay)viewer.FindName("ViewerNoteDisplay");
                    var firstHeader = (System.Windows.Controls.TextBlock)viewer.FindName("VelocityCh01Text");
                    var ninthHeader = (System.Windows.Controls.TextBlock)viewer.FindName("VelocityCh09Text");
                    viewer.HandleKey(System.Windows.Input.Key.PageDown, System.Windows.Input.ModifierKeys.Shift);
                    if (viewerRows.FirstChannel != 12) throw new Exception("Viewer PageDown did not reach channel 13");
                    if (Math.Abs(viewerMeter.GetDisplayedLevel(0) - 13 / 40.0) > .001 || viewerNotes.GetNoteText(0) != "012" ||
                        firstHeader.Text != "CH13" || ninthHeader.Text != "CH21")
                        throw new Exception("Level, note and channel headings did not follow the keyboard to channel 13");
                    viewer.RenderSnapshot(frame);
                    viewer.HandleKey(System.Windows.Input.Key.PageDown, System.Windows.Input.ModifierKeys.Shift);
                    if (viewerRows.FirstChannel != 24) throw new Exception("Viewer PageDown did not reach channel 25");
                    if (Math.Abs(viewerMeter.GetDisplayedLevel(0) - 25 / 40.0) > .001 || viewerNotes.GetNoteText(0) != "024" ||
                        firstHeader.Text != "CH25" || ninthHeader.Text != "CH33")
                        throw new Exception("Level, note and channel headings did not follow the keyboard to channel 25");
                    if (((System.Windows.Controls.TextBlock)viewer.FindName("RegisterBankText")).Text.Contains("[F12]"))
                        throw new Exception("Register shortcut caption remains visible");
                    var image = new RenderTargetBitmap((int)Math.Ceiling(mini.Width * scale), (int)(120 * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                    image.Render(mini);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                    string path = Path.Combine(Path.GetTempPath(), $"mf-mini-dpi-{scale * 100:0}.png");
                    using (var stream = File.Create(path)) encoder.Save(stream);
                    var keyboard = new MusicKeyboardDisplay();
                    keyboard.SetChannels([new() { NoteNumber = 49, IsKeyOn = true }]); // displayed C#4
                    VisualTreeHelper.SetRootDpi(keyboard, new DpiScale(scale, scale));
                    keyboard.Measure(new Size(63 * MusicKeyboardDisplay.KeyPitch(scale), 28)); keyboard.Arrange(new Rect(0, 0, 63 * MusicKeyboardDisplay.KeyPitch(scale), 28));
                    int keyboardWidth = (int)Math.Round(63 * MusicKeyboardDisplay.KeyPitch(scale) * scale);
                    var keyImage = new RenderTargetBitmap(keyboardWidth, (int)(28 * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                    keyImage.Render(keyboard);
                    var keyPixels = new byte[keyboardWidth * (int)(28 * scale) * 4];
                    keyImage.CopyPixels(keyPixels, keyboardWidth * 4, 0);
                    double blackLeft = Math.Floor(22 * MusicKeyboardDisplay.KeyPitch(scale) * scale -
                        (Math.Round(2 * scale, MidpointRounding.AwayFromZero) + 1) / 2) / scale;
                    for (int x = (int)Math.Round(blackLeft * scale); x < (int)Math.Round(blackLeft * scale) + (int)Math.Round(2 * scale, MidpointRounding.AwayFromZero) + 1; x++)
                    {
                        int p = (2 * keyboardWidth + x) * 4;
                        if (keyPixels[p] != 0x40 || keyPixels[p + 1] != 0xe8 || keyPixels[p + 2] != 0)
                            throw new Exception($"Mini black key border does not light at {scale:P0}, x={x}");
                    }
                    keyboard.SetChannels([]);
                    keyboard.UpdateLayout();
                    var uniformImage = new RenderTargetBitmap(keyboardWidth, (int)(28 * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                    uniformImage.Render(keyboard);
                    uniformImage.CopyPixels(keyPixels, keyboardWidth * 4, 0);
                    foreach (bool black in new[] { false, true })
                    {
                        int y = black ? 2 : (int)(22 * scale);
                        var widths = new List<int>();
                        var starts = new List<int>();
                        bool IsKey(int x)
                        {
                            int p = (y * keyboardWidth + x) * 4;
                            return keyPixels[p] == (black ? 0x28 : 0x94) && keyPixels[p + 1] == (black ? 0x20 : 0x88) && keyPixels[p + 2] == (black ? 0x20 : 0x88);
                        }
                        for (int x = 0; x < keyboardWidth; x++)
                        {
                            if (!IsKey(x)) continue;
                            int start = x;
                            starts.Add(start);
                            while (x + 1 < keyboardWidth && IsKey(x + 1)) x++;
                            widths.Add(x - start + 1);
                        }
                        int expectedWidth = black ? (int)Math.Round(2 * scale, MidpointRounding.AwayFromZero) + 1 : (int)Math.Round(4 * scale);
                        if (widths.Count != (black ? 45 : 63) || widths.Distinct().Count() != 1 || widths[0] != expectedWidth)
                            throw new Exception($"Unequal key widths at {scale:P0}, black={black}: {string.Join(',', widths)}");
                        if (black)
                        {
                            int[] boundaries = [1, 2, 4, 5, 6];
                            for (int i = 0; i < starts.Count; i++)
                            {
                                double boundary = (i / 5 * 7 + boundaries[i % 5]) * MusicKeyboardDisplay.KeyPitch(scale) * scale;
                                double center = starts[i] + widths[i] / 2.0;
                                if (Math.Abs(center - (boundary - .5)) > .501)
                                    throw new Exception($"Black key {i} is off its white-key boundary at {scale:P0}");
                            }
                        }
                    }
                    foreach (double displayScale in new[] { 1.0, 1.5 })
                    {
                        var meter = new MusicMeterDisplay { DisplayScale = displayScale, FillPlotHeight = displayScale > 1, ShowPeakDockSegment = true, HiddenTopSegments = displayScale > 1 ? 0 : 2 };
                        VisualTreeHelper.SetRootDpi(meter, new DpiScale(scale, scale));
                        meter.SetLevels([1.0], [], true);
                        meter.Measure(new Size(32, 85)); meter.Arrange(new Rect(0, 0, 32, 85));
                        int w = (int)(32 * scale), h = (int)Math.Ceiling(85 * scale);
                        var bitmap = new RenderTargetBitmap(w, h, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                        bitmap.Render(meter);
                        var pixels = new byte[w * h * 4]; bitmap.CopyPixels(pixels, w * 4, 0);
                        var runs = new List<(int Start, int Length)>();
                        for (int y = 0; y < h; y++)
                        {
                            bool Lit(int row) { int p = (row * w + w / 2) * 4; return pixels[p] != 0 || pixels[p + 1] != 0 || pixels[p + 2] != 0; }
                            if (!Lit(y)) continue;
                            int start = y;
                            while (y + 1 < h && Lit(y + 1)) y++;
                            runs.Add((start, y - start + 1));
                        }
                        if (displayScale > 1 && runs[0].Start != 0) throw new Exception("Full meter has extra space beneath its heading");
                        if (runs.Count < 4 || runs.Select(r => r.Length).Distinct().Count() != 1 ||
                            runs.Zip(runs.Skip(1), (a, b) => b.Start - a.Start - a.Length).Distinct().Count() != 1)
                            throw new Exception($"Unequal meter bars/gaps at {scale:P0}, display={displayScale}: {string.Join(",", runs)}");
                    }
                    Console.WriteLine($"PASS DPI {scale:P0}: equal physical bar heights/gaps; {path}");

                    var spectrumMini = new MiniPlayerControl();
                    spectrumMini.RenderSnapshot(new()
                    {
                        Title = "AUDIO SPECTRUM TEST",
                        IsPlaying = true,
                        UseSpectrumOnly = true,
                        CurrentTime = TimeSpan.FromSeconds(45),
                        TotalTime = TimeSpan.FromSeconds(180),
                        SpectrumLevels = Enumerable.Range(0, AudioSpectrumAnalyzer.BandCount)
                            .Select(i => (float)((i + 1.0) / AudioSpectrumAnalyzer.BandCount)).ToArray()
                    });
                    spectrumMini.Measure(new Size(spectrumMini.Width, 120));
                    VisualTreeHelper.SetRootDpi(spectrumMini, new DpiScale(scale, scale));
                    spectrumMini.InvalidateMeasure();
                    spectrumMini.Measure(new Size(spectrumMini.Width, 120));
                    spectrumMini.Arrange(new Rect(0, 0, spectrumMini.Width, 120));
                    spectrumMini.UpdateLayout();
                    var chPanel = (FrameworkElement)spectrumMini.FindName("ChannelMonitorPanel");
                    var specPanel = (FrameworkElement)spectrumMini.FindName("SpectrumPanel");
                    var expandBtn = (FrameworkElement)spectrumMini.FindName("ExpandButton");
                    var miniSeek = (MusicSeekBarDisplay)spectrumMini.FindName("MiniSeekBar");
                    var miniSpec = (MusicMeterDisplay)spectrumMini.FindName("MiniSpectrumDisplay");
                    var miniRuler = (MusicFrequencyRulerDisplay)spectrumMini.FindName("MiniSpectrumRuler");
                    if (chPanel.Visibility != Visibility.Collapsed || expandBtn.Visibility != Visibility.Collapsed ||
                        specPanel.Visibility != Visibility.Visible || miniSpec.ChannelCount != AudioSpectrumAnalyzer.BandCount ||
                        Math.Abs(miniSeek.Progress - 0.25) > .001 ||
                        Math.Abs(miniSeek.ActualWidth - miniKeys.ActualWidth) > .5 / scale ||
                        Math.Abs(miniSpec.ActualWidth - miniKeys.ActualWidth) > .5 / scale ||
                        Math.Abs(miniRuler.ActualWidth - miniKeys.ActualWidth) > .5 / scale ||
                        Math.Abs(miniSeek.GetLeftEdge() - miniSpec.GetBarLeft(0)) > .001 ||
                        Math.Abs(miniSeek.GetRightEdge() - miniSpec.GetBarRight(AudioSpectrumAnalyzer.BandCount - 1)) > .001 ||
                        Math.Abs(miniSpec.GetBarLeft(0) - miniRuler.GetLeftEdge()) > .001 ||
                        Math.Abs(miniSpec.GetBarRight(AudioSpectrumAnalyzer.BandCount - 1) - miniRuler.GetRightEdge()) > .001)
                        throw new Exception($"Mini spectrum layout mismatch at {scale:P0}");
                }
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure != null) throw failure;
    }
}
