using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MirrorFlux.Controls;
using MirrorFlux.Services.Music;

internal static class MusicRendering
{
    public static void Run()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var viewer = new MusicPlayerControl();
                viewer.Resources["PrimaryText"] = Brushes.LightGray;
                viewer.Resources["MutedText"] = new SolidColorBrush(Color.FromRgb(0x60, 0x58, 0x98));
                viewer.Resources["AccentText"] = new SolidColorBrush(Color.FromRgb(0x84, 0x94, 0xFC));
                var frame = new MusicPlaybackSnapshot
                {
                    Channels = Enumerable.Range(0, 12).Select(i => new MusicChannelSnapshot
                    {
                        ChipName = "YM2151", IsKeyOn = true, IsInUse = i < 8, NoteNumber = 48 + i,
                        PanLeftLevel = i == 0 ? .25 : null, PanRightLevel = i == 0 ? .75 : null
                    }).ToArray(),
                    RegisterBanks = [new() { ChipName = "YM2151", Values = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray() },
                                     new() { ChipName = "YM2612", Port = 1, Values = Enumerable.Repeat((byte)0xab, 256).ToArray() }],
                    SpectrumLevels = Enumerable.Range(0, AudioSpectrumAnalyzer.BandCount).Select(i => (float)((i % 9 + 1) / 10.0)).ToArray()
                };
                frame.Channels[8] = new() { ChipName = "HuC6280", Kind = MusicChannelKind.Psg, NoteNumber = 60, IsKeyOn = true, IsInUse = true, Waveform = Enumerable.Range(0, 32).Select(i => (byte)(i * 8)).ToArray() };
                frame.Channels[9] = new() { ChipName = "SN76489", Kind = MusicChannelKind.Psg, NoteNumber = 65, IsKeyOn = true };
                frame.Channels[10] = new() { ChipName = "SN76489", Kind = MusicChannelKind.Noise, IsKeyOn = true, Level = .5 };
                frame.Channels[11] = new() { ChipName = "SegaPCM", Kind = MusicChannelKind.Pcm, IsKeyOn = true, Level = .75 };
                frame.Channels[7] = new() { ChipName = "YM2151", NoteNumber = 55, Pan = 3, IsInUse = false };
                frame.Channels[6] = new() { ChipName = "YM2151", NoteNumber = 54, Level = .2, Pan = 3, IsInUse = true };
                viewer.RenderSnapshot(frame);
                var caption = (TextBlock)viewer.FindName("RegisterBankText");
                viewer.ExecuteFunctionKey(11);
                viewer.RenderSnapshot(frame);
                if (!caption.Text.Contains("YM2612")) throw new Exception("F11 register selection lost on refresh");
                viewer.ExecuteFunctionKey(11);
                if (!caption.Text.Contains("YM2151")) throw new Exception("F11 does not wrap");
                foreach (int width in new[] { 1340, 1000 })
                {
                    viewer.Measure(new Size(width, 540));
                    viewer.Arrange(new Rect(0, 0, width, 540));
                    viewer.UpdateLayout();
                    var registers = (FrameworkElement)viewer.FindName("RegisterDisplay");
                    var keys = (FrameworkElement)viewer.FindName("KeyboardRows");
                    var viewerSpectrum = (MusicMeterDisplay)viewer.FindName("SpectrumDisplay");
                    var spectrumScale = (MusicVerticalScaleDisplay)viewer.FindName("SpectrumDbScale");
                    var scaleOverlay = (Grid)viewer.FindName("ScaleOverlay");
                    var spectrumCaption = (TextBlock)viewer.FindName("SpectrumGraphicsText");
                    var spectrumRuler = (MusicFrequencyRulerDisplay)viewer.FindName("SpectrumFrequencyRuler");
                    var viewerMeter = (MusicMeterDisplay)viewer.FindName("ViewerLevelDisplay");
                    var velocityScale = (MusicVerticalScaleDisplay)viewer.FindName("VelocityValueScale");
                    var viewerNotes = (MusicNoteNumberDisplay)viewer.FindName("ViewerNoteDisplay");
                    var viewerPan = (MusicPanDisplay)viewer.FindName("ViewerPanDisplay");
                    var velocityHeader = (FrameworkElement)viewer.FindName("VelocityHeader");
                    var velocityCh01 = (TextBlock)viewer.FindName("VelocityCh01Text");
                    var velocityCh09 = (TextBlock)viewer.FindName("VelocityCh09Text");
                    var velocityGraphics = (TextBlock)viewer.FindName("VelocityGraphicsText");
                    var viewerGrid = (Grid)viewer.FindName("ViewerGrid");
                    double bottom = registers.TranslatePoint(new Point(0, registers.ActualHeight), viewer).Y;
                    double keyBottom = keys.TranslatePoint(new Point(0, 504), viewer).Y;
                    double fullMeterBottom = viewerMeter.TranslatePoint(new Point(0, viewerMeter.ActualHeight), viewer).Y;
                    double fullMeterTop = viewerMeter.TranslatePoint(new Point(0, 0), viewer).Y;
                    double noteTop = viewerNotes.TranslatePoint(new Point(0, 0), viewer).Y;
                    double noteBottom = viewerNotes.TranslatePoint(new Point(0, viewerNotes.ActualHeight), viewer).Y;
                    double tenthKeyboardTop = keys.TranslatePoint(new Point(0, 9 * 42 + 14), viewer).Y;
                    double spectrumTop = viewerSpectrum.TranslatePoint(new Point(0, 0), viewer).Y;
                    double spectrumBottom = viewerSpectrum.TranslatePoint(new Point(0, viewerSpectrum.ActualHeight), viewer).Y;
                    double spectrumCaptionBottom = spectrumCaption.TranslatePoint(new Point(0, spectrumCaption.ActualHeight), viewer).Y;
                    double spectrumCaptionTop = spectrumCaption.TranslatePoint(new Point(0, 0), viewer).Y;
                    double spectrumCaptionRight = spectrumCaption.TranslatePoint(new Point(spectrumCaption.ActualWidth, 0), viewer).X;
                    double spectrumRulerTop = spectrumRuler.TranslatePoint(new Point(0, 0), viewer).Y;
                    double seventhKeyboardTop = keys.TranslatePoint(new Point(0, 6 * 42 + 14), viewer).Y;
                    double registerCaptionTop = caption.TranslatePoint(new Point(0, 0), viewer).Y;
                    double fullPanTop = viewerPan.TranslatePoint(new Point(0, 0), viewer).Y;
                    double fullPanBottom = viewerPan.TranslatePoint(new Point(0, viewerPan.ActualHeight), viewer).Y;
                    double keyRight = keys.TranslatePoint(new Point(keys.ActualWidth, 0), viewer).X;
                    double meterLeft = viewerMeter.TranslatePoint(new Point(0, 0), viewer).X;
                    double spectrumScaleLeft = spectrumScale.TranslatePoint(new Point(0, 0), viewer).X;
                    double spectrumScaleRight = spectrumScale.TranslatePoint(new Point(spectrumScale.ActualWidth, 0), viewer).X;
                    double velocityScaleLeft = velocityScale.TranslatePoint(new Point(0, 0), viewer).X;
                    double velocityScaleRight = velocityScale.TranslatePoint(new Point(velocityScale.ActualWidth, 0), viewer).X;
                    double fullMeterRight = viewerMeter.TranslatePoint(new Point(viewerMeter.ActualWidth, 0), viewer).X;
                    double ch01Left = velocityCh01.TranslatePoint(new Point(0, 0), viewer).X;
                    double ch09Left = velocityCh09.TranslatePoint(new Point(0, 0), viewer).X;
                    double velocityRight = velocityGraphics.TranslatePoint(new Point(velocityGraphics.ActualWidth, 0), viewer).X;
                    double velocityBottom = velocityHeader.TranslatePoint(new Point(0, velocityHeader.ActualHeight), viewer).Y;
                    double registerLeft = registers.TranslatePoint(new Point(0, 0), viewer).X;
                    double registerRight = registers.TranslatePoint(new Point(registers.ActualWidth, 0), viewer).X;
                    double gridRight = viewerGrid.TranslatePoint(new Point(viewerGrid.ActualWidth, 0), viewer).X;
                    double verticalPixel = 1 / VisualTreeHelper.GetDpi(viewer).DpiScaleY;
                    double horizontalPixel = 1 / VisualTreeHelper.GetDpi(viewer).DpiScaleX;
                    if (Math.Abs(bottom - keyBottom) > verticalPixel * 2 + .01 || Math.Abs(fullMeterTop - (tenthKeyboardTop - 13)) > verticalPixel * 3 + .01 ||
                        Math.Abs(spectrumTop - seventhKeyboardTop) > verticalPixel * 2 + .01 ||
                        Math.Abs(spectrumCaptionTop - registerCaptionTop) > verticalPixel + .01 ||
                        Math.Abs(spectrumCaptionBottom - spectrumTop) > verticalPixel + .01 ||
                        Math.Abs(spectrumRulerTop - spectrumBottom) > verticalPixel + .01 ||
                        Math.Abs(viewerSpectrum.ActualHeight - viewerMeter.ActualHeight) > .01 ||
                        spectrumBottom >= fullMeterTop || Math.Abs(velocityBottom - fullMeterTop) > verticalPixel + .01 ||
                        Math.Abs(ch01Left - (meterLeft + viewerMeter.GetBarLeft(0))) > horizontalPixel + .01 ||
                        Math.Abs(ch09Left - (meterLeft + viewerMeter.GetBarLeft(8))) > horizontalPixel + .01 ||
                        Math.Abs(velocityRight - (meterLeft + viewerMeter.GetBarRight(15))) > horizontalPixel + .01 ||
                        Math.Abs(fullMeterBottom - fullPanTop) > verticalPixel + .01 || Math.Abs(fullPanBottom - noteTop) > verticalPixel + .01 ||
                        Math.Abs(spectrumCaptionRight - (meterLeft + viewerMeter.GetBarRight(15))) > horizontalPixel + .01 ||
                        Math.Abs(noteBottom - keyBottom) > verticalPixel + .01 || Math.Abs(meterLeft - keyRight - 16) > .01 ||
                        Math.Abs(spectrumScaleLeft - (keyRight - 16)) > .01 || Math.Abs(spectrumScaleRight - meterLeft) > .01 ||
                        Math.Abs(velocityScaleLeft - (keyRight - 16)) > .01 || Math.Abs(velocityScaleRight - meterLeft) > .01 ||
                        Math.Abs(registerLeft - fullMeterRight - 8) > .01 || Math.Abs(registerRight - gridRight) > .01)
                        throw new Exception($"Full-player order is not keyboard, meter/pan, then register table: " +
                            $"registerBottom={bottom}, keyBottom={keyBottom}, spectrumTop={spectrumTop}, expectedSpectrumTop={seventhKeyboardTop}, " +
                            $"spectrumBottom={spectrumBottom}, meterTop={fullMeterTop}, tenthKeyTop={tenthKeyboardTop}, " +
                            $"meterBottom={fullMeterBottom}, panTop={fullPanTop}, panBottom={fullPanBottom}, " +
                            $"velocityBottom={velocityBottom}, ch01Left={ch01Left}, ch09Left={ch09Left}, velocityRight={velocityRight}, " +
                            $"keyRight={keyRight}, meterLeft={meterLeft}, spectrumScale={spectrumScaleLeft}..{spectrumScaleRight}, " +
                            $"velocityScale={velocityScaleLeft}..{velocityScaleRight}, meterRight={fullMeterRight}, registerLeft={registerLeft}, " +
                            $"registerRight={registerRight}, gridRight={gridRight}, viewerWidth={viewer.ActualWidth}, dpi={1 / horizontalPixel}");
                    if (velocityCh01.Text != "CH01" || velocityCh09.Text != "CH09" || velocityGraphics.Text != "VELOCITY GRAPHICS" ||
                        velocityCh01.Foreground is not SolidColorBrush muted || muted.Color != Color.FromRgb(0x60, 0x58, 0x98) ||
                        velocityCh09.Foreground is not SolidColorBrush muted09 || muted09.Color != muted.Color ||
                        velocityGraphics.Foreground is not SolidColorBrush accent || accent.Color != Color.FromRgb(0x84, 0x94, 0xFC))
                        throw new Exception("Velocity headers do not follow col_muted and col_accent");
                    if (!spectrumScale.GetLabels().SequenceEqual(["0dB", "-10", "-20", "-30", "-40", "-50", "-60"]) ||
                        !velocityScale.GetLabels().SequenceEqual(["127", "63", "0"]) ||
                        MusicVerticalScaleDisplay.TextSize != 9 || MusicVerticalScaleDisplay.LabelLeftShiftPixels != 2 ||
                        MusicVerticalScaleDisplay.MajorTickPixels != 8 || MusicVerticalScaleDisplay.SectionTickPixels != 5 ||
                        MusicVerticalScaleDisplay.StrokePixels != 2 || spectrumScale.BottomInsetPixels != 0 || velocityScale.BottomInsetPixels != 0 ||
                        spectrumScale.DisplayScale != viewerSpectrum.DisplayScale || velocityScale.DisplayScale != viewerMeter.DisplayScale ||
                        spectrumScale.ActualHeight != viewerSpectrum.ActualHeight ||
                        velocityScale.ActualHeight != viewerMeter.ActualHeight || spectrumScale.ActualWidth != 32 || velocityScale.ActualWidth != 32 ||
                        scaleOverlay.ActualWidth != 32 || Panel.GetZIndex(scaleOverlay) != 3 || scaleOverlay.ClipToBounds)
                        throw new Exception("Full-player graph scales do not expose the full labels and ticks up to the graph edge");
                    double expectedMeterWidth = viewerGrid.ActualWidth - 520 - 8 - 16 - 272;
                    double widthTolerance = horizontalPixel + .01;
                    if (spectrumCaption.Text != "SPECTRUM ANALYZER" || spectrumCaption.Foreground is not SolidColorBrush spectrumAccent ||
                        spectrumAccent.Color != Color.FromRgb(0x84, 0x94, 0xFC) ||
                        spectrumRuler.MinimumFrequency != 10 || spectrumRuler.MaximumFrequency != 22050 ||
                        Math.Abs(spectrumRuler.GetLeftEdge() - viewerMeter.GetBarLeft(0)) > widthTolerance ||
                        Math.Abs(spectrumRuler.GetRightEdge() - viewerMeter.GetBarRight(15)) > widthTolerance)
                        throw new Exception("Spectrum caption or 10 Hz-22.05 kHz ruler is not aligned to the meter edges");
                    if (Math.Abs(viewerMeter.ActualWidth - expectedMeterWidth) > .01 || viewerMeter.ActualHeight is < 84.5 or > 85.5 || viewerMeter.DisplayScale != 1.5 ||
                        !viewerMeter.ShowPeakDockSegment || viewerMeter.HiddenTopSegments != 0 || !viewerMeter.FillPlotHeight || viewerMeter.ChannelGapPixels != 3 ||
                        viewerMeter.GetDisplayedLevel(11) < .74 ||
                        Math.Abs(viewerSpectrum.ActualWidth - expectedMeterWidth) > .01 || viewerSpectrum.ActualHeight is < 84.5 or > 85.5 ||
                        viewerSpectrum.DisplayScale != 1.5 || !viewerSpectrum.ShowPeakDockSegment || viewerSpectrum.HiddenTopSegments != 0 ||
                        !viewerSpectrum.FillPlotHeight || viewerSpectrum.ChannelGapPixels != 2 ||
                        viewerSpectrum.OuterEdgeReferenceChannels != 16 || viewerSpectrum.OuterEdgeReferenceGapPixels != 3 ||
                        viewerSpectrum.ChannelCount != AudioSpectrumAnalyzer.BandCount ||
                        Math.Abs(viewerSpectrum.GetBarLeft(0) - viewerMeter.GetBarLeft(0)) > widthTolerance ||
                        Math.Abs(viewerSpectrum.GetBarRight(viewerSpectrum.ChannelCount - 1) - viewerMeter.GetBarRight(15)) > widthTolerance ||
                        Math.Abs(viewerSpectrum.GetBarWidth(0) - viewerMeter.GetBarWidth(0) * 2 / 3) > widthTolerance ||
                        Math.Abs(viewerPan.ActualWidth - expectedMeterWidth) > .01 || viewerPan.ActualHeight is < 24 or > 28 ||
                        viewerPan.DisplayScale != 1.5 || viewerPan.ChannelCount != 16 || viewerNotes.ChannelCount != 16 ||
                        MusicNoteNumberDisplay.TextSize != 14 ||
                        viewerNotes.GetNoteText(0) != "048" || !viewerNotes.IsNoteActive(0) ||
                        viewerNotes.GetNoteText(6) != "054" || !viewerNotes.IsNoteActive(6) ||
                        viewerNotes.GetNoteText(7) != "000" || viewerNotes.IsNoteActive(7) ||
                        viewerNotes.GetNoteText(10) != "000" || viewerNotes.IsNoteActive(10) ||
                        !viewerPan.IsChannelActive(0) || viewerPan.IsChannelActive(7))
                        throw new Exception($"Full player spectrum/meter design mismatch: expectedWidth={expectedMeterWidth}, " +
                            $"spectrum={viewerSpectrum.ActualWidth}x{viewerSpectrum.ActualHeight}, spectrumScale={viewerSpectrum.DisplayScale}, " +
                            $"spectrumCount={viewerSpectrum.ChannelCount}, spectrumGap={viewerSpectrum.ChannelGapPixels}, spectrumPitch={viewerSpectrum.GetChannelWidth(0)}, " +
                            $"meter={viewerMeter.ActualWidth}x{viewerMeter.ActualHeight}, meterScale={viewerMeter.DisplayScale}, meterGap={viewerMeter.ChannelGapPixels}, " +
                            $"meterPitch={viewerMeter.GetChannelWidth(0)}, meter11={viewerMeter.GetDisplayedLevel(11)}, " +
                            $"pan={viewerPan.ActualWidth}x{viewerPan.ActualHeight}, panScale={viewerPan.DisplayScale}, panCount={viewerPan.ChannelCount}");
                    var gradedFullPan = viewerPan.GetPanLevels(0);
                    if (Math.Abs(gradedFullPan.Left - .25) > .001 || Math.Abs(gradedFullPan.Right - .75) > .001)
                        throw new Exception("Full player does not preserve graded pan levels");
                    for (int channel = 0; channel < 16; channel++)
                        if (Math.Abs(viewerMeter.GetChannelCenter(channel) - viewerPan.GetChannelCenter(channel)) > .01)
                            throw new Exception($"Full-player meter and pan centers differ at channel {channel + 1}");
                    var bitmap = new RenderTargetBitmap(width, 540, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(viewer);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    var path = Path.Combine(Path.GetTempPath(), $"mf-music-{width}.png");
                    using var output = File.Create(path);
                    encoder.Save(output);
                    Console.WriteLine($"PASS music layout, selection and bottom alignment: {path}");
                }
                var scaleHost = new Grid { Width = 32, Height = 85 };
                scaleHost.Resources["MutedText"] = Brushes.White;
                var scaleDisplay = new MusicVerticalScaleDisplay
                {
                    Width = 32, Height = 85, HorizontalAlignment = HorizontalAlignment.Right,
                    Labels = "127|63|0", DisplayScale = 1.5, BottomInsetPixels = 0
                };
                scaleHost.Children.Add(scaleDisplay);
                scaleHost.Measure(new Size(32, 85));
                scaleHost.Arrange(new Rect(0, 0, 32, 85));
                scaleHost.UpdateLayout();
                var scaleBitmap = new RenderTargetBitmap(32, 85, 96, 96, PixelFormats.Pbgra32);
                scaleBitmap.Render(scaleHost);
                var scalePath = Path.Combine(Path.GetTempPath(), "mf-graph-scale.png");
                var scaleEncoder = new PngBitmapEncoder();
                scaleEncoder.Frames.Add(BitmapFrame.Create(scaleBitmap));
                using (var output = File.Create(scalePath)) scaleEncoder.Save(output);
                var scalePixels = new byte[32 * 85 * 4];
                scaleBitmap.CopyPixels(scalePixels, 32 * 4, 0);
                bool ScaleInk(int x, int y)
                {
                    int offset = (y * 32 + x) * 4;
                    return scalePixels[offset] != 0 || scalePixels[offset + 1] != 0 || scalePixels[offset + 2] != 0;
                }
                int FirstScaleInkNear(int y) => Enumerable.Range(20, 12)
                    .FirstOrDefault(x => Enumerable.Range(Math.Max(0, y - 1), Math.Min(85, y + 2) - Math.Max(0, y - 1))
                        .Any(row => ScaleInk(x, row)), -1);
                bool hasVisibleNumbers = Enumerable.Range(0, 22).Any(x => Enumerable.Range(0, 9).Any(y => ScaleInk(x, y)));
                int firstSectionStart = FirstScaleInkNear(16), secondSectionStart = FirstScaleInkNear(21);
                // Validate the current block-aligned ruler without assuming an old fixed
                // segment pitch; the meter layout can legitimately change with scaling.
                bool bottomAligned = Enumerable.Range(24, 8).Any(x => ScaleInk(x, 80)) &&
                    !Enumerable.Range(24, 8).Any(x => Enumerable.Range(83, 2).Any(y => ScaleInk(x, y)));
                var (blockTops, _, _) = scaleDisplay.GetBlockLayout();
                int sectionInkRows = Enumerable.Range(0, 85).Count(y => ScaleInk(29, y));
                int axisInkRows = Enumerable.Range(0, 85).Count(y => ScaleInk(31, y));
                bool blockTicks = sectionInkRows >= Math.Max(4, blockTops.Length / 2) && axisInkRows >= 70;
                if (!hasVisibleNumbers || firstSectionStart < 0 || secondSectionStart < 0 ||
                    Math.Abs(firstSectionStart - secondSectionStart) > 2 || !bottomAligned || !blockTicks)
                    throw new Exception($"Graph ruler labels/ticks are clipped or misaligned: labels={hasVisibleNumbers}, " +
                        $"sections={firstSectionStart}/{secondSectionStart}, bottom={bottomAligned}, blocks={blockTicks}");
                Console.WriteLine($"PASS visible graph-scale numbers, dense section ticks and bottom-block alignment: {scalePath}");
                var keyboard = new MusicKeyboardDisplay();
                keyboard.Measure(new Size(235, 28));
                keyboard.Arrange(new Rect(0, 0, 235, 28));
                keyboard.SetChannels([new MusicChannelSnapshot
                { ChipName = "SegaPCM", Kind = MusicChannelKind.Pcm, IsKeyOn = true, IsInUse = true, Level = .75 }]);
                var unpitchedBitmap = new RenderTargetBitmap(235, 28, 96, 96, PixelFormats.Pbgra32);
                unpitchedBitmap.Render(keyboard);
                var unpitchedPixels = new byte[235 * 28 * 4];
                unpitchedBitmap.CopyPixels(unpitchedPixels, 235 * 4, 0);
                keyboard.SetChannels([]);
                var emptyBitmap = new RenderTargetBitmap(235, 28, 96, 96, PixelFormats.Pbgra32);
                emptyBitmap.Render(keyboard);
                var emptyPixels = new byte[235 * 28 * 4];
                emptyBitmap.CopyPixels(emptyPixels, 235 * 4, 0);
                if (!unpitchedPixels.SequenceEqual(emptyPixels)) throw new Exception("Unpitched horizontal keyboard meter is still visible");
                var whiteKeyPixel = new byte[4];
                emptyBitmap.CopyPixels(new Int32Rect(2, 20, 1, 1), whiteKeyPixel, 4, 0);
                if (whiteKeyPixel[0] >= 170 || whiteKeyPixel[1] >= 170 || whiteKeyPixel[2] >= 170)
                    throw new Exception("Keyboard idle keys are not dark enough");
                Console.WriteLine("PASS dark keyboard palette and no horizontal activity meter");
                var greenFrame = frame with
                {
                    IsPlaying = true,
                    Title = "MINI PLAYER TEST",
                    Channels = Enumerable.Range(0, 16).Select(index => new MusicChannelSnapshot
                    {
                        ChipName = "YM2151",
                        IsKeyOn = index == 0,
                        NoteNumber = index == 0 ? 119 : 36 + index,
                        Level = index is 0 or 15 ? 0 : (16 - index) / 16.0,
                        Pan = index % 4,
                        IsInUse = index < 15,
                        PanLeftLevel = index == 0 ? .34 : null,
                        PanRightLevel = index == 0 ? .67 : null
                    }).ToArray()
                };
                var mini = new MiniPlayerControl();
                mini.RenderSnapshot(greenFrame);
                mini.RenderSnapshot(greenFrame);
                VisualTreeHelper.SetRootDpi(mini, new DpiScale(1, 1));
                mini.Measure(new Size(323, 120));
                mini.Arrange(new Rect(0, 0, 323, 120));
                mini.UpdateLayout();
                var miniKeyboard = (FrameworkElement)mini.FindName("KeyboardDisplay");
                var miniMeter = (MusicMeterDisplay)mini.FindName("MeterDisplay");
                var miniPan = (MusicPanDisplay)mini.FindName("PanDisplay");
                var playText = (TextBlock)mini.FindName("PlayStatusText");
                var sevenSeg = (MusicSevenSegmentDisplay)mini.FindName("SevenSegmentTime");
                var titleText = (MarqueeTextBlock)mini.FindName("TitleText");
                var pauseButton = (Button)mini.FindName("PauseButton");
                double keyboardRight = miniKeyboard.TranslatePoint(new Point(miniKeyboard.ActualWidth, 0), mini).X;
                double meterRight = miniMeter.TranslatePoint(new Point(miniMeter.ActualWidth, 0), mini).X;
                double keyboardTop = miniKeyboard.TranslatePoint(new Point(0, 0), mini).Y;
                double meterTop = miniMeter.TranslatePoint(new Point(0, 0), mini).Y;
                double meterBottom = miniMeter.TranslatePoint(new Point(0, miniMeter.ActualHeight), mini).Y;
                double panTop = miniPan.TranslatePoint(new Point(0, 0), mini).Y;
                double panBottom = miniPan.TranslatePoint(new Point(0, miniPan.ActualHeight), mini).Y;
                double playTop = playText.TranslatePoint(new Point(0, 0), mini).Y;
                double titleTop = titleText.TranslatePoint(new Point(0, 0), mini).Y;
                double buttonTop = pauseButton.TranslatePoint(new Point(0, 0), mini).Y;
                if (Math.Abs(miniKeyboard.ActualWidth - 315) > .01 || Math.Abs(keyboardRight - 319) > .01)
                    throw new Exception("Mini keyboard is not sized to 315 pixels at the right edge");
                if (Math.Abs(miniMeter.ActualWidth - 315) > .01 || Math.Abs(meterRight - keyboardRight) > .01 ||
                    Math.Abs(miniPan.ActualWidth - 315) > .01 || miniPan.ChannelCount != 16 ||
                    !miniMeter.ShowPeakDockSegment || miniMeter.HiddenTopSegments != 2 || miniMeter.ChannelGapPixels != 2)
                    throw new Exception("Mini meter is not a right-aligned 315-pixel sixteen-channel display");
                if (!miniPan.IsChannelActive(0) || miniPan.IsChannelActive(15))
                    throw new Exception("Mini player pan rings do not gray out stopped channels");
                if (playText.FontSize < 10 || sevenSeg.Text != "00:00 / --:--" || titleText.Text != "MINI PLAYER TEST")
                    throw new Exception($"Mini player header mismatch: playSize={playText.FontSize}, time={sevenSeg.Text}, title={titleText.Text}");
                if (titleTop <= playTop || keyboardTop <= titleTop || miniKeyboard.ActualHeight > 24 || miniMeter.ActualHeight >= 51)
                    throw new Exception("Keyboard and meter height were not reduced to make room for title text above keyboard");
                double layoutPixel = 1 / VisualTreeHelper.GetDpi(mini).DpiScaleY;
                if (Math.Abs(panTop - meterBottom) > .01 || Math.Abs(panBottom - mini.ActualHeight) > layoutPixel + .01)
                    throw new Exception($"Pan dials are not tightly aligned: meterBottom={meterBottom}, panTop={panTop}, panBottom={panBottom}, miniHeight={mini.ActualHeight}");
                for (int channel = 0; channel < 16; channel++)
                {
                    if (Math.Abs(miniMeter.GetChannelWidth(channel) - 19.6875) > 1 / VisualTreeHelper.GetDpi(mini).DpiScaleX)
                        throw new Exception($"Meter channel {channel + 1} is not within one physical pixel of the expected slot width");
                    if (Math.Abs(miniMeter.GetChannelCenter(channel) - miniPan.GetChannelCenter(channel)) > .01)
                        throw new Exception($"Meter and pan centers differ at channel {channel + 1}");
                }
                if (mini.FindName("KeyboardLabel") is not null || mini.FindName("MetersLabel") is not null)
                    throw new Exception("Obsolete mini player captions are still present");
                if (((FrameworkElement)mini.FindName("SubsongPanel")).Visibility != Visibility.Collapsed)
                    throw new Exception("Subsong controls are visible for a non-NSF/HES track");
                var trackPlayer = new RecordingMusicPlayer
                {
                    Snapshot = greenFrame with { FormatName = "NSF", TrackIndex = 1, TrackCount = 12, Title = "NSF SUBSONG" }
                };
                var trackMini = new MiniPlayerControl(trackPlayer);
                trackMini.RenderSnapshot(trackPlayer.GetSnapshot());
                var subsongPanel = (FrameworkElement)trackMini.FindName("SubsongPanel");
                var subsongText = (MusicSevenSegmentDisplay)trackMini.FindName("SubsongText");
                var nextTrack = (Button)trackMini.FindName("NextTrackButton");
                var previousTrack = (Button)trackMini.FindName("PreviousTrackButton");
                if (subsongPanel.Visibility != Visibility.Visible || subsongText.Text != "02/0C")
                    throw new Exception("NSF subsong controls do not show the current track and count");
                nextTrack.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (trackPlayer.SelectedTrack != 2 || subsongText.Text != "03/0C")
                    throw new Exception("Mini-player next subsong control did not select track 3");
                previousTrack.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (trackPlayer.SelectedTrack != 1 || subsongText.Text != "02/0C")
                    throw new Exception("Mini-player previous subsong control did not return to track 2");
                trackPlayer.Snapshot = trackPlayer.Snapshot with { FormatName = "HES", TrackIndex = 255, TrackCount = 256 };
                trackMini.RenderSnapshot(trackPlayer.GetSnapshot());
                if (subsongPanel.Visibility != Visibility.Visible || subsongText.Text != "100/100")
                    throw new Exception("HES subsong controls are not available");
                trackMini.Measure(new Size(323, 120));
                trackMini.Arrange(new Rect(0, 0, 323, 120));
                trackMini.UpdateLayout();
                var subsongBitmap = new RenderTargetBitmap(323, 120, 96, 96, PixelFormats.Pbgra32);
                subsongBitmap.Render(trackMini);
                var subsongEncoder = new PngBitmapEncoder();
                subsongEncoder.Frames.Add(BitmapFrame.Create(subsongBitmap));
                using (var stream = File.Create(Path.Combine(Path.GetTempPath(), "mf-mini-subsong.png"))) subsongEncoder.Save(stream);

                // Verify stream playback mini player layout
                var streamSnapshot = new MusicPlaybackSnapshot
                {
                    IsPlaying = true,
                    UseSpectrumOnly = true,
                    Title = "Test Stream Audio",
                    CurrentTime = TimeSpan.FromSeconds(45),
                    TotalTime = TimeSpan.FromSeconds(180),
                    SpectrumLevels = new float[AudioSpectrumAnalyzer.BandCount]
                };
                var streamMini = new MiniPlayerControl(new RecordingMusicPlayer { Snapshot = streamSnapshot });
                streamMini.RenderSnapshot(streamSnapshot);
                streamMini.Measure(new Size(323, 120));
                streamMini.Arrange(new Rect(0, 0, 323, 120));
                streamMini.UpdateLayout();

                var streamHeader = (FrameworkElement)streamMini.FindName("StreamHeaderPanel");
                var chiptuneHeader = (FrameworkElement)streamMini.FindName("ChiptuneHeaderPanel");
                var streamTitle = (MarqueeTextBlock)streamMini.FindName("StreamTitleText");
                var miniSeek = (FrameworkElement)streamMini.FindName("MiniSeekBar");
                var miniSpectrum = (FrameworkElement)streamMini.FindName("MiniSpectrumDisplay");
                var streamSevenSeg = (MusicSevenSegmentDisplay)streamMini.FindName("StreamSevenSegmentTime");
                var streamPlay = (TextBlock)streamMini.FindName("StreamPlayStatusText");

                if (streamHeader.Visibility != Visibility.Visible || chiptuneHeader.Visibility != Visibility.Collapsed)
                    throw new Exception("Stream playback must show StreamHeaderPanel and hide ChiptuneHeaderPanel");
                if (streamPlay.FontSize < 10 || streamSevenSeg.Text != "00:45 / 03:00")
                    throw new Exception("Stream header must show large PLAY text and seven-segment time");
                if (streamTitle.Text != "Test Stream Audio")
                    throw new Exception("StreamTitleText must show the track title");
                double streamTitleY = streamTitle.TranslatePoint(new Point(0, 0), streamMini).Y;
                double streamSeekY = miniSeek.TranslatePoint(new Point(0, 0), streamMini).Y;
                double streamSpecY = miniSpectrum.TranslatePoint(new Point(0, 0), streamMini).Y;
                if (streamSeekY <= streamTitleY || streamSpecY <= streamSeekY)
                    throw new Exception($"Vertical ordering in SpectrumPanel: titleY={streamTitleY}, seekY={streamSeekY}, specY={streamSpecY}");
                var stagedLeft = new double[16];
                var stagedRight = new double[16];
                stagedLeft[0] = .34;
                stagedRight[0] = .67;
                var stagedPan = miniPan.GetPanLevels(0);
                if (Math.Abs(stagedPan.Left - .34) > .001 || Math.Abs(stagedPan.Right - .67) > .001)
                    throw new Exception("Mini player does not preserve snapshot graded pan levels");
                double expectedPanOffset = (.67 - .34) / (.67 + .34);
                if (Math.Abs(miniPan.GetIndicatorOffset(0) - expectedPanOffset) > .001)
                    throw new Exception("Pan direction dot does not follow graded left/right balance");
                var centerPan = Enumerable.Repeat(1.0, 16).ToArray();
                miniPan.SetPanLevels(centerPan, centerPan);
                Point centerDirection = miniPan.GetIndicatorDirection(0);
                if (MusicPanDisplay.RingDiameterPixels != 16 || MusicPanDisplay.RingStrokePixels != 1 || MusicPanDisplay.DotDiameterPixels != 4 ||
                    Math.Abs(centerDirection.X) > .001 || Math.Abs(centerDirection.Y + 1) > .001)
                    throw new Exception("The 16-pixel, 1-pixel-stroke pan dial does not put its larger center-position dot at the top");
                miniPan.SetPanLevels(stagedLeft, stagedRight);
                var miniBitmap = new RenderTargetBitmap(323, 120, 96, 96, PixelFormats.Pbgra32);
                miniBitmap.Render(mini);
                var miniPixels = new byte[323 * 120 * 4];
                miniBitmap.CopyPixels(miniPixels, 323 * 4, 0);
                bool hasGreenLamp = false;
                for (int offset = 0; offset < miniPixels.Length; offset += 4)
                {
                    if (miniPixels[offset + 1] > 150 && miniPixels[offset + 2] < 40 && miniPixels[offset] < 100)
                    {
                        hasGreenLamp = true;
                        break;
                    }
                }
                if (!hasGreenLamp) throw new Exception("Keyboard lamp is not green");
                bool hasLastOctaveLamp = false;
                for (int y = 24; y < 52 && !hasLastOctaveLamp; y++)
                    for (int x = 296; x < 319; x++)
                    {
                        int offset = (y * 323 + x) * 4;
                        if (miniPixels[offset + 1] > 150 && miniPixels[offset + 2] < 40 && miniPixels[offset] < 100)
                        {
                            hasLastOctaveLamp = true;
                            break;
                        }
                    }
                if (!hasLastOctaveLamp) throw new Exception("Mini keyboard does not retain the ninth octave after scaling");

                var blackKeyKeyboard = new MusicKeyboardDisplay();
                blackKeyKeyboard.SetChannels([new MusicChannelSnapshot
                { ChipName = "YM2151", IsKeyOn = true, IsInUse = true, NoteNumber = 49 }]);
                blackKeyKeyboard.Measure(new Size(315, 28));
                blackKeyKeyboard.Arrange(new Rect(0, 0, 315, 28));
                var blackKeyBitmap = new RenderTargetBitmap(315, 28, 96, 96, PixelFormats.Pbgra32);
                blackKeyBitmap.Render(blackKeyKeyboard);
                var blackKeyPixels = new byte[315 * 28 * 4];
                blackKeyBitmap.CopyPixels(blackKeyPixels, 315 * 4, 0);
                bool hasBrightBlackKeyLamp = false;
                for (int offset = 0; offset < blackKeyPixels.Length; offset += 4)
                    if (blackKeyPixels[offset] == 0x40 && blackKeyPixels[offset + 1] == 0xE8 && blackKeyPixels[offset + 2] == 0)
                    {
                        hasBrightBlackKeyLamp = true;
                        break;
                    }
                if (!hasBrightBlackKeyLamp) throw new Exception("Full-player black-key lamp is dimmer than the white-key lamp");
                if (((TextBlock)mini.FindName("PauseIcon")).Text != "\uE769" ||
                    ((TextBlock)mini.FindName("StopIcon")).Text != "\uE71A" ||
                    ((TextBlock)mini.FindName("ExpandIcon")).Text != "\uE740")
                    throw new Exception("Mini player control icons are not the Fluent icon set");
                bool hasOff = false, hasLit = false, hasPeak = false, hasEndpoint = false, hasTopEndpoint = false,
                    hasSilentKeyBottomEndpoint = false, hasSilentKeyBottomLit = false,
                    hasMovingLevelBottomEndpoint = false, hasMovingLevelBottomLit = false,
                    hasIdleBottomEndpoint = false, hasPanGray = false;
                bool IsMiniColor(int offset, byte b, byte g, byte r) => miniPixels[offset] == b && miniPixels[offset + 1] == g && miniPixels[offset + 2] == r;
                for (int offset = 0; offset < miniPixels.Length; offset += 4)
                {
                    hasOff |= IsMiniColor(offset, 0x32, 0x10, 0x10);
                    hasLit |= IsMiniColor(offset, 0xFA, 0x82, 0x82);
                    hasPeak |= IsMiniColor(offset, 0xC4, 0x60, 0x60);
                    hasEndpoint |= IsMiniColor(offset, 0xB4, 0x57, 0x57);
                }
                for (int y = Math.Max(0, (int)Math.Floor(meterTop) - 1); y < Math.Min(120, (int)Math.Ceiling(meterTop) + 6); y++)
                    for (int x = 4; x < 23; x++)
                        hasTopEndpoint |= IsMiniColor((y * 323 + x) * 4, 0xB4, 0x57, 0x57);
                for (int y = Math.Max(0, (int)Math.Floor(meterBottom) - 4); y < Math.Min(120, (int)Math.Ceiling(meterBottom) + 1); y++)
                    for (int x = 4; x < 23; x++)
                    {
                        hasSilentKeyBottomEndpoint |= IsMiniColor((y * 323 + x) * 4, 0xB4, 0x57, 0x57);
                        hasSilentKeyBottomLit |= IsMiniColor((y * 323 + x) * 4, 0xFA, 0x82, 0x82);
                    }
                for (int y = Math.Max(0, (int)Math.Floor(meterBottom) - 4); y < Math.Min(120, (int)Math.Ceiling(meterBottom) + 1); y++)
                    for (int x = 24; x < 42; x++)
                    {
                        hasMovingLevelBottomEndpoint |= IsMiniColor((y * 323 + x) * 4, 0xB4, 0x57, 0x57);
                        hasMovingLevelBottomLit |= IsMiniColor((y * 323 + x) * 4, 0xFA, 0x82, 0x82);
                    }
                for (int y = Math.Max(0, (int)Math.Floor(meterBottom) - 4); y < Math.Min(120, (int)Math.Ceiling(meterBottom) + 1); y++)
                    for (int x = 300; x < 319; x++)
                        hasIdleBottomEndpoint |= IsMiniColor((y * 323 + x) * 4, 0xB4, 0x57, 0x57);
                for (int y = 109; y < 120 && !hasPanGray; y++)
                    for (int x = 4; x < 319; x++)
                    {
                        int offset = (y * 323 + x) * 4;
                        int blue = miniPixels[offset], green = miniPixels[offset + 1], red = miniPixels[offset + 2];
                        if (red >= 45 && Math.Abs(red - green) <= 3 && blue >= red && blue - red <= 20)
                        {
                            hasPanGray = true;
                            break;
                        }
                    }
                if (!hasOff || !hasLit || !hasPeak || !hasEndpoint || hasTopEndpoint ||
                    !hasSilentKeyBottomEndpoint || hasSilentKeyBottomLit || hasMovingLevelBottomEndpoint ||
                    !hasMovingLevelBottomLit || !hasIdleBottomEndpoint || !hasPanGray)
                    throw new Exception($"Meter design mismatch: off={hasOff}, lit={hasLit}, peak={hasPeak}, endpoint={hasEndpoint}, " +
                        $"topEndpoint={hasTopEndpoint}, silentKeyEndpoint={hasSilentKeyBottomEndpoint}, silentKeyLit={hasSilentKeyBottomLit}, " +
                        $"movingEndpoint={hasMovingLevelBottomEndpoint}, movingLit={hasMovingLevelBottomLit}, " +
                        $"idleDock={hasIdleBottomEndpoint}, panGray={hasPanGray}");
                var miniPath = Path.Combine(Path.GetTempPath(), "mf-mini-music.png");
                var miniEncoder = new PngBitmapEncoder();
                miniEncoder.Frames.Add(BitmapFrame.Create(miniBitmap));
                using (var output = File.Create(miniPath)) miniEncoder.Save(output);
                Console.WriteLine($"PASS equal-width 16-channel meters and gray ring/direction-dot pan: {miniPath}");

                var meter = new MusicMeterDisplay();
                meter.Measure(new Size(160, 32));
                meter.Arrange(new Rect(0, 0, 160, 32));
                meter.SetLevels(Enumerable.Repeat(1.0, 16).ToArray(), [], false);
                meter.SetLevels(Enumerable.Repeat(1.0, 16).ToArray(), [], false);
                for (int frameIndex = 0; frameIndex < 10; frameIndex++) meter.SetLevels(new double[16], [], false);
                if (meter.GetDisplayedLevel(0) < .6)
                    throw new Exception("Meter decay drops immediately instead of smoothing");
                meter.SetLevels(Enumerable.Repeat(.25, 16).ToArray(), [], false, Enumerable.Repeat(true, 16).ToArray());
                if (Math.Abs(meter.GetDisplayedLevel(0) - .25) > .001)
                    throw new Exception("Display smoothing distorts an FM envelope-timed meter level");
                var holdMeter = new MusicMeterDisplay();
                bool[] timed = [true];
                holdMeter.SetLevels([0.0], [], false, timed);
                holdMeter.SetLevels([1.0], [], false, timed);
                for (int frameIndex = 0; frameIndex < MusicMeterDisplay.PeakHoldFrames; frameIndex++) holdMeter.SetLevels([0.0], [], false, timed);
                if (MusicMeterDisplay.PeakHoldFrames != 16 || holdMeter.GetPeak(0) < 1)
                    throw new Exception("Meter peak is not held for 16 frames");
                holdMeter.SetLevels([0.0], [], false, timed);
                if (Math.Abs(holdMeter.GetPeak(0) - .975) > .001)
                    throw new Exception("Meter peak does not start falling after the hold");

                var highDpiMeter = new MusicMeterDisplay { ShowPeakDockSegment = true, HiddenTopSegments = 2 };
                VisualTreeHelper.SetRootDpi(highDpiMeter, new DpiScale(1.5, 1.5));
                var highDpiLevels = new double[16];
                Array.Fill(highDpiLevels, .5);
                highDpiLevels[^1] = 0;
                highDpiMeter.SetLevels(highDpiLevels, [], true);
                highDpiMeter.Measure(new Size(256, 60));
                highDpiMeter.Arrange(new Rect(0, 0, 256, 60));
                var highDpiBitmap = new RenderTargetBitmap(384, 90, 144, 144, PixelFormats.Pbgra32);
                highDpiBitmap.Render(highDpiMeter);
                var highDpiPixels = new byte[384 * 90 * 4];
                highDpiBitmap.CopyPixels(highDpiPixels, 384 * 4, 0);
                int longestLitRun = 0, litRun = 0, longestEndpointRun = 0, endpointRun = 0;
                for (int y = 0; y < 90; y++)
                {
                    int offset = (y * 384 + 2) * 4;
                    if (highDpiPixels[offset] == 0xFA && highDpiPixels[offset + 1] == 0x82 && highDpiPixels[offset + 2] == 0x82)
                        longestLitRun = Math.Max(longestLitRun, ++litRun);
                    else
                        litRun = 0;
                    int idleOffset = (y * 384 + 378) * 4;
                    if (highDpiPixels[idleOffset] == 0xB4 && highDpiPixels[idleOffset + 1] == 0x57 && highDpiPixels[idleOffset + 2] == 0x57)
                        longestEndpointRun = Math.Max(longestEndpointRun, ++endpointRun);
                    else
                        endpointRun = 0;
                }
                if (longestLitRun != 2 || longestEndpointRun != 2)
                    throw new Exception($"Meter boxes are not 2 physical pixels high at 150% DPI: lit={longestLitRun}, endpoint={longestEndpointRun}");

                foreach (double dpiScale in new[] { 1.0, 1.25, 1.5, 1.75, 2.0 })
                {
                    var dpiMeter = new MusicMeterDisplay { ChannelGapPixels = 3 };
                    VisualTreeHelper.SetRootDpi(dpiMeter, new DpiScale(dpiScale, dpiScale));
                    dpiMeter.SetLevels(new double[16], []);
                    dpiMeter.Measure(new Size(517, 60));
                    dpiMeter.Arrange(new Rect(0, 0, 517, 60));
                    double expectedPhysicalWidth = Math.Floor(dpiMeter.GetChannelWidth(0) * dpiScale + .001);
                    for (int channel = 1; channel < 16; channel++)
                    {
                        double physicalWidth = Math.Floor(dpiMeter.GetChannelWidth(channel) * dpiScale + .001);
                        if (physicalWidth != expectedPhysicalWidth)
                            throw new Exception($"Full-player meter width varies at {dpiScale:P0} DPI on channel {channel + 1}");
                    }
                }

                foreach (double scale in new[] { 1.0, 1.25 }) VerifyMaskColors(scale);
                var keyPlayer = new RecordingMusicPlayer();
                var closeViewer = new MusicPlayerControl(keyPlayer);
                int closeRequests = 0;
                closeViewer.CloseRequested += (_, _) => closeRequests++;
                if (!closeViewer.HandleKey(Key.Return, ModifierKeys.None) || closeRequests != 0 || keyPlayer.PlayCalls != 0)
                    throw new Exception("Enter without a selection must neither close nor restart playback");
                closeViewer.RenderSnapshot(new() { Channels = Enumerable.Range(0, 32).Select(i => new MusicChannelSnapshot { Id = $"voice{i}" }).ToArray() });
                closeViewer.HandleKey(Key.D1, ModifierKeys.None);
                closeViewer.HandleKey(Key.D8, ModifierKeys.Shift);
                closeViewer.HandleKey(Key.D1, ModifierKeys.None);
                ((MusicKeyboardRowsDisplay)closeViewer.FindName("KeyboardRows")).ScrollChannels(12);
                closeViewer.HandleKey(Key.D2, ModifierKeys.None);
                closeViewer.Close();
                if (!keyPlayer.Masks.Take(4).SequenceEqual(new[]{("voice0",true),("voice15",true),("voice0",false),("voice13",true)})
                    || !keyPlayer.Masks.Skip(4).OrderBy(x=>x.Item1).SequenceEqual(new[]{("voice13",false),("voice15",false)}))
                    throw new Exception("Full player keys, scroll offset, toggle or close did not update the correct audio mask");
                Console.WriteLine("PASS full-player mask keys follow meter bank and release on close");
                Console.WriteLine("PASS DPI-stable equal meter spacing, bright black-key lamps, physical 2px boxes, smooth decay and Enter selection handling");
                viewer.RenderSnapshot(new());
                if (caption.Text != "NO YM REGISTERS") throw new Exception("Stale registers after stopping");
                var ymSegaFrame = frame with
                {
                    Channels = [.. Enumerable.Range(0, 8).Select(index => new MusicChannelSnapshot
                    {
                        ChipName = "YM2151", Label = $"FM{index + 1:00}", Kind = MusicChannelKind.Fm, IsInUse = true
                    }), .. Enumerable.Range(0, 16).Select(index => new MusicChannelSnapshot
                    {
                        ChipName = "SegaPCM", Label = $"PCM{index + 1:00}", Kind = MusicChannelKind.Pcm, IsInUse = index < 4
                    })],
                    RegisterBanks = [new() { ChipName = "YM2151", Values = new byte[256] }]
                };
                viewer.RenderSnapshot(ymSegaFrame);
                if (((Grid)viewer.FindName("ViewerGrid")).Height != 12 * 42 || !caption.Text.Contains("YM2151"))
                    throw new Exception("YM2151 + SegaPCM exceeds twelve displayed channels or hides the FM register table");

                var longTitleFrame = frame with
                {
                    Title = "[スクランブルフォーメーションの未使用曲１]  by superjarikun",
                    SystemName = "X68000 YM2151 + PCM8",
                    Channels = [.. Enumerable.Range(0, 8).Select(i => new MusicChannelSnapshot
                    {
                        ChipName = "YM2151", Label = $"FM{i + 1:00}", Kind = MusicChannelKind.Fm, IsInUse = true
                    }), new MusicChannelSnapshot
                    {
                        ChipName = "PCM8", Label = "PCM01", Kind = MusicChannelKind.Pcm, IsInUse = true
                    }]
                };
                viewer.RenderSnapshot(longTitleFrame);
                viewer.Measure(new Size(1340, 800));
                viewer.Arrange(new Rect(0, 0, 1340, 800));
                viewer.UpdateLayout();
                var vg = (FrameworkElement)viewer.FindName("ViewerGrid");
                double initialVgWidth = vg.ActualWidth;
                var trackTitleRow = (FrameworkElement)viewer.FindName("Gd3TrackTitleRow");
                var keysControl = (FrameworkElement)viewer.FindName("KeyboardRows");
                double firstKeyboardTop = keysControl.TranslatePoint(new Point(0, 14), viewer).Y;
                double gd3Top = trackTitleRow.TranslatePoint(new Point(0, 0), viewer).Y;
                double vPixel = 1 / VisualTreeHelper.GetDpi(viewer).DpiScaleY;
                if (Math.Abs(gd3Top - firstKeyboardTop) > vPixel + .01)
                    throw new Exception($"GD3 top row is not aligned with first keyboard top: gd3Top={gd3Top}, keyTop={firstKeyboardTop}");
                double initialRowWidth = trackTitleRow.ActualWidth;
                double initialLeftMargin = trackTitleRow.Margin.Left;
                for (int rep = 0; rep < 10; rep++)
                {
                    viewer.RenderSnapshot(longTitleFrame);
                    viewer.Measure(new Size(1340, 800));
                    viewer.Arrange(new Rect(0, 0, 1340, 800));
                    viewer.UpdateLayout();
                    if (Math.Abs(vg.ActualWidth - initialVgWidth) > 0.01 ||
                        Math.Abs(trackTitleRow.ActualWidth - initialRowWidth) > 0.01 ||
                        Math.Abs(trackTitleRow.Margin.Left - initialLeftMargin) > 0.01)
                    {
                        throw new Exception($"Long MDX title caused layout jitter: VG width {initialVgWidth} -> {vg.ActualWidth}, row width {initialRowWidth} -> {trackTitleRow.ActualWidth}");
                    }
                }

                // Verify Album Artwork display behavior
                var artContainer = (FrameworkElement)viewer.FindName("AlbumArtContainer");
                var artBorder = (FrameworkElement)viewer.FindName("AlbumArtBorder");
                var artImage = (System.Windows.Controls.Image)viewer.FindName("AlbumArtImage");
                if (artContainer == null || artBorder == null || artImage == null)
                    throw new Exception("Album art elements not found in MusicPlayerControl template");
                if (artBorder.Width != 272 || artBorder.Height != 232)
                    throw new Exception("AlbumArtBorder size is not 272x232");
                var borderElement = (System.Windows.Controls.Border)artBorder;
                if (borderElement.Padding.Left <= 0 || borderElement.Padding.Top <= 0)
                    throw new Exception("AlbumArtBorder should have inner padding so artwork does not touch the border");
                if (artImage.HorizontalAlignment != HorizontalAlignment.Center || artImage.VerticalAlignment != VerticalAlignment.Center)
                    throw new Exception("AlbumArtImage should be centered inside AlbumArtBorder");
                if (artContainer.Visibility != Visibility.Visible)
                    throw new Exception("AlbumArtContainer should be Visible even when no image is present");
                var registerDisplay = (FrameworkElement)viewer.FindName("RegisterDisplay");
                if (Math.Abs(artBorder.TranslatePoint(new Point(0, 0), viewer).X - registerDisplay.TranslatePoint(new Point(0, 0), viewer).X) > .01)
                    throw new Exception("AlbumArtBorder left edge does not align with register display left edge");
                if (Math.Abs(artBorder.TranslatePoint(new Point(0, 0), viewer).Y - firstKeyboardTop) > .01)
                    throw new Exception("AlbumArtBorder top edge does not align with first keyboard keys top edge");

                string testDir = Path.Combine(Path.GetTempPath(), "mf-art-test-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(testDir);
                try
                {
                    string testTrack = Path.Combine(testDir, "test.vgm");
                    File.WriteAllBytes(testTrack, [0x56, 0x67, 0x6d, 0x20]);
                    string coverPath = Path.Combine(testDir, "cover.png");
                    byte[] png1x1 = [
                        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
                        0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4,
                        0x89, 0x00, 0x00, 0x00, 0x0A, 0x49, 0x44, 0x41, 0x54, 0x78, 0x9C, 0x63, 0x00, 0x01, 0x00, 0x00,
                        0x05, 0x00, 0x01, 0x0D, 0x0A, 0x2D, 0xB4, 0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE,
                        0x42, 0x60, 0x82
                    ];
                    File.WriteAllBytes(coverPath, png1x1);
                    viewer.NavigateToDirectory(testDir);
                    viewer.UpdateAlbumArtAsync().GetAwaiter().GetResult();
                    if (artContainer.Visibility != Visibility.Visible || artImage.Source == null)
                        throw new Exception("AlbumArtImage did not receive image source when cover.png is present in folder");
                }
                finally
                {
                    if (Directory.Exists(testDir)) Directory.Delete(testDir, true);
                }

                // Verify TransportDisplay below GD3 tags
                var transport = (MusicTransportDisplay)viewer.FindName("TransportDisplay");
                if (transport == null)
                    throw new Exception("TransportDisplay not found in MusicPlayerControl template");
                if (transport.Height != 60)
                    throw new Exception("TransportDisplay height must be 60");
                var sevenSegment = (FrameworkElement)viewer.FindName("CurrentClockSevenSegment");
                double sevenSegmentRight = sevenSegment.TranslatePoint(new Point(sevenSegment.ActualWidth, 0), viewer).X;
                double transportRight = transport.TranslatePoint(new Point(transport.ActualWidth, 0), viewer).X;
                if (Math.Abs(transportRight - sevenSegmentRight) > 1.0)
                    throw new Exception("TransportDisplay right edge should align with 7-segment display right edge");
                var authorRow = (FrameworkElement)viewer.FindName("Gd3AuthorRow");
                double authorLeft = authorRow.TranslatePoint(new Point(0, 0), viewer).X;
                double transportLeft = transport.TranslatePoint(new Point(0, 0), viewer).X;
                if (Math.Abs(transportLeft - authorLeft) > 1.0)
                    throw new Exception("TransportDisplay left edge should align with GD3 tag box left edge");

                double transportTop = transport.TranslatePoint(new Point(0, 0), viewer).Y;
                double authorBottom = authorRow.TranslatePoint(new Point(0, authorRow.ActualHeight), viewer).Y;
                if (transportTop < authorBottom)
                    throw new Exception("TransportDisplay should be located in the area below GD3 tags");

                // Verify GD3 FILE TYPE row below AUTHOR NAME, and SOUND SOURCE row below FILE TYPE
                var fileTypeRow = (FrameworkElement)viewer.FindName("Gd3FileTypeRow");
                if (fileTypeRow == null) throw new Exception("Gd3FileTypeRow element not found");
                double fileTypeTop = fileTypeRow.TranslatePoint(new Point(0, 0), viewer).Y;
                double authorTop = authorRow.TranslatePoint(new Point(0, 0), viewer).Y;
                if (fileTypeTop <= authorTop)
                    throw new Exception("Gd3FileTypeRow should sit below Gd3AuthorRow");
                var soundSourceRow = (FrameworkElement)viewer.FindName("Gd3SoundSourceRow");
                if (soundSourceRow == null) throw new Exception("Gd3SoundSourceRow element not found");
                double soundSourceTop = soundSourceRow.TranslatePoint(new Point(0, 0), viewer).Y;
                if (soundSourceTop <= fileTypeTop)
                    throw new Exception("Gd3SoundSourceRow should sit below Gd3FileTypeRow");
                double soundSourceBottom = soundSourceRow.TranslatePoint(new Point(0, soundSourceRow.ActualHeight), viewer).Y;
                if (transportTop < soundSourceBottom)
                    throw new Exception("TransportDisplay should be located in the area below Gd3SoundSourceRow");
                var fileTypeText = (MarqueeTextBlock)viewer.FindName("Gd3FileTypeText");
                if (fileTypeText == null) throw new Exception("Gd3FileTypeText marquee control not found");
                var soundSourceText = (MarqueeTextBlock)viewer.FindName("Gd3SoundSourceText");
                if (soundSourceText == null) throw new Exception("Gd3SoundSourceText marquee control not found");
                double soundSourceRowRight = soundSourceRow.TranslatePoint(new Point(soundSourceRow.ActualWidth, 0), viewer).X;
                var clockDisplay = (FrameworkElement)viewer.FindName("CurrentClockSevenSegment");
                if (clockDisplay != null)
                {
                    double clockRight = clockDisplay.TranslatePoint(new Point(clockDisplay.ActualWidth, 0), viewer).X;
                    if (Math.Abs(soundSourceRowRight - clockRight) > 1.0)
                        throw new Exception($"Gd3SoundSourceRow right edge ({soundSourceRowRight}) should align with 7-segment right edge ({clockRight})");
                }

                // Verify ControllerGraphicsText ("CONTROLLER") above TransportDisplay, aligned with SPECTRUM ANALYZER
                var controllerText = (System.Windows.Controls.TextBlock)viewer.FindName("ControllerGraphicsText");
                if (controllerText == null || controllerText.Text != "CONTROLLER")
                    throw new Exception("ControllerGraphicsText not found or does not have text 'CONTROLLER'");
                var spectrumText = (System.Windows.Controls.TextBlock)viewer.FindName("SpectrumGraphicsText");
                double controllerRight = controllerText.TranslatePoint(new Point(controllerText.ActualWidth, 0), viewer).X;
                double spectrumRight = spectrumText.TranslatePoint(new Point(spectrumText.ActualWidth, 0), viewer).X;
                if (Math.Abs(controllerRight - spectrumRight) > 1.0)
                    throw new Exception($"Controller text right edge ({controllerRight}) does not align with SPECTRUM ANALYZER right edge ({spectrumRight})");

                double controllerBottom = controllerText.TranslatePoint(new Point(0, controllerText.ActualHeight), viewer).Y;
                double seekbarTop = transportTop + 7.0; // Seekbar top edge inside TransportDisplay
                if (Math.Abs(controllerBottom - seekbarTop) > 1.0)
                    throw new Exception($"Controller text bottom ({controllerBottom}) should align with seekbar top ({seekbarTop})");

                double specTextTop = spectrumText.TranslatePoint(new Point(0, 0), viewer).Y;
                double transportBottom = transport.TranslatePoint(new Point(0, transport.ActualHeight), viewer).Y;
                if (transportBottom > specTextTop + 0.1)
                    throw new Exception($"TransportDisplay bottom ({transportBottom}) should sit above SPECTRUM ANALYZER top ({specTextTop})");

                transport.UpdateState(true, false, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(120), 0);
                if (!transport.IsPlaying || transport.IsPaused || transport.CurrentTime != TimeSpan.FromSeconds(30) || transport.TotalTime != TimeSpan.FromSeconds(120) || transport.LoopCount != 0)
                    throw new Exception("TransportDisplay state was not updated correctly");
                if (Math.Abs(transport.SeekBarRatio - 0.25) > 0.001)
                    throw new Exception($"Initial seekbar ratio expected 0.25, got {transport.SeekBarRatio}");

                // Complete first loop (60s total, 50s loop duration)
                transport.UpdateState(true, false, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60), 0, TimeSpan.FromSeconds(50));
                if (Math.Abs(transport.SeekBarRatio - 1.0) > 0.001)
                    throw new Exception($"End of first play seekbar ratio expected 1.0, got {transport.SeekBarRatio}");

                // Loop 1 starts: seekbar should reset to 0
                transport.UpdateState(true, false, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60), 1, TimeSpan.FromSeconds(50));
                if (Math.Abs(transport.SeekBarRatio - 0.0) > 0.001)
                    throw new Exception($"On loop 1 seekbar ratio should reset to 0.0, got {transport.SeekBarRatio}");

                // Halfway through loop 1: +25s on 50s loop duration -> ratio 0.5
                transport.UpdateState(true, false, TimeSpan.FromSeconds(85), TimeSpan.FromSeconds(60), 1, TimeSpan.FromSeconds(50));
                if (Math.Abs(transport.SeekBarRatio - 0.5) > 0.001)
                    throw new Exception($"Mid loop 1 seekbar ratio expected 0.5, got {transport.SeekBarRatio}");

                // Loop 2 starts: seekbar resets to 0 again
                transport.UpdateState(true, false, TimeSpan.FromSeconds(110), TimeSpan.FromSeconds(60), 2, TimeSpan.FromSeconds(50));
                if (Math.Abs(transport.SeekBarRatio - 0.0) > 0.001)
                    throw new Exception($"On loop 2 seekbar ratio should reset to 0.0, got {transport.SeekBarRatio}");

                if (transport.TargetLoops != 0)
                    throw new Exception("Default TargetLoops should be infinite");
                transport.TargetLoops = 1;
                if (transport.TargetLoops != 1)
                    throw new Exception("TargetLoops should toggle to 1");
                transport.TargetLoops = 2;
                if (transport.TargetLoops != 2)
                    throw new Exception("TargetLoops should toggle back to 2");
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null) throw new Exception("Music rendering failed", failure);
    }

    private static void VerifyMaskColors(double scale)
    {
        var viewer = new MusicPlayerControl(new RecordingMusicPlayer());
        VisualTreeHelper.SetRootDpi(viewer, new DpiScale(scale, scale));
        viewer.RenderSnapshot(new() { Channels = Enumerable.Range(0, 32).Select(i => new MusicChannelSnapshot {
            Id = $"ch{i}", IsInUse = true, IsKeyOn = true, NoteNumber = 12, KeyboardNotes = [12, 13], Level = 1, Pan = 3
        }).ToArray() });
        viewer.Measure(new Size(1340, 900));
        viewer.Arrange(new Rect(0, 0, 1340, 900));
        var keys = (MusicKeyboardRowsDisplay)viewer.FindName("KeyboardRows");
        var meter = (MusicMeterDisplay)viewer.FindName("ViewerLevelDisplay");
        var pan = (MusicPanDisplay)viewer.FindName("ViewerPanDisplay");

        HashSet<int> Colors(FrameworkElement item, Rect area)
        {
            viewer.UpdateLayout();
            int width = (int)Math.Ceiling(viewer.ActualWidth * scale);
            int height = (int)Math.Ceiling(viewer.ActualHeight * scale);
            area.Offset((Vector)item.TranslatePoint(new Point(), viewer));
            var bitmap = new RenderTargetBitmap(width, height, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bitmap.Render(viewer);
            int x = (int)Math.Ceiling(area.Left * scale), y = (int)Math.Ceiling(area.Top * scale);
            var region = new Int32Rect(x, y, (int)Math.Floor(area.Right * scale) - x, (int)Math.Floor(area.Bottom * scale) - y);
            int[] pixels = new int[region.Width * region.Height];
            bitmap.CopyPixels(region, pixels, region.Width * 4, 0);
            return pixels.Select(value => value & 0xffffff).ToHashSet();
        }

        var meterColumn = new Rect(meter.GetChannelCenter(0), 0, 2, meter.ActualHeight);
        var firstKeys = new Rect(1, 16, 15, 23);
        viewer.HandleKey(Key.D1, ModifierKeys.None);
        if (!Colors(meter, meterColumn).SetEquals(new[] { 0, 0x202028 }) || pan.IsChannelActive(0) || !pan.IsChannelActive(1))
            throw new Exception($"Masked meter at {scale:P0}: {string.Join(",", Colors(meter, meterColumn).Select(c => c.ToString("X6")))}; pan={pan.IsChannelActive(0)}/{pan.IsChannelActive(1)}, width={meter.ActualWidth}, column={meterColumn}");
        var keyColors = Colors(keys, firstKeys);
        if (!keyColors.Contains(0x303038) || keyColors.Contains(0x00e840))
            throw new Exception($"Masked keyboard still has a lamp or lacks gray keys at {scale:P0}");
        keys.ScrollChannels(12);
        if (!pan.IsChannelActive(0) || !Colors(keys, firstKeys).Contains(0x00e840))
            throw new Exception("Scrolled mask leaked onto a different channel");
        keys.ScrollChannels(-12);
        if (pan.IsChannelActive(0)) throw new Exception("Mask disappeared after scrolling back");
        viewer.HandleKey(Key.D1, ModifierKeys.None);
        if (!Colors(meter, meterColumn).Contains(0x8282fa) || !pan.IsChannelActive(0) || !Colors(keys, firstKeys).Contains(0x00e840))
            throw new Exception($"Unmasked column did not restore at {scale:P0}");
        viewer.Close();
        Console.WriteLine($"PASS masked meter segments, pan and keyboard restore at {scale:P0}");
    }

    private sealed class RecordingMusicPlayer : IMusicPlaybackService
    {
        public List<(string, bool)> Masks { get; } = [];
        public bool SetChannelMuted(MusicChannelSnapshot channel, bool muted) { Masks.Add((channel.Id, muted)); return true; }
        public int PlayCalls { get; private set; }
        public int SelectedTrack { get; private set; } = -1;
        public MusicPlaybackSnapshot Snapshot { get; set; } = new() { IsPlaying = true };
        public string? CurrentFilePath => null;
        public bool IsPlaying => true;
        public bool IsPaused => false;
        public event Action? PlaybackStarted { add { } remove { } }
        public event Action? PlaybackStopped { add { } remove { } }
        public event Action? PlaybackPaused { add { } remove { } }
        public event Action? PlaybackResumed { add { } remove { } }
        public bool CanPlay(string filePath) => true;
        public bool Play(string filePath) { PlayCalls++; return true; }
        public bool SelectTrack(int index)
        {
            if (index < 0 || index >= Snapshot.TrackCount) return false;
            SelectedTrack = index;
            Snapshot = Snapshot with { TrackIndex = index, CurrentTime = TimeSpan.Zero };
            return true;
        }
        public void TogglePause() { }
        public void Stop() { }
        public MusicPlaybackSnapshot GetSnapshot() => Snapshot;
    }
}
