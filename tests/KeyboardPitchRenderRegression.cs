using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MirrorFlux.Controls;
using MirrorFlux.Services.Music;

/// <summary>
/// サウンドビューア鍵盤の音程の棒を実際に描いて画素で確かめる。
/// 環境変数 MF_TEST_KEYBOARD_PNG にパスを入れると、描いた画像を保存する（目視用）。
/// </summary>
internal static class KeyboardPitchRenderRegression
{
    public static void Run(Action<bool, string> check)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { Verify(check); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure != null) throw failure;
    }

    private static void Verify(Action<bool, string> check)
    {
        // 1段目: 白鍵 A4（69）を +0.4 半音、2段目: 黒鍵 A#4（70）を -0.4 半音。
        var channels = new[]
        {
            new MusicChannelSnapshot { Id = "a", ChipName = "YM2151", Kind = MusicChannelKind.Fm, IsInUse = true, IsKeyOn = true, NoteNumber = 69, Pitch = 69.4 },
            new MusicChannelSnapshot { Id = "b", ChipName = "YM2151", Kind = MusicChannelKind.Fm, IsInUse = true, IsKeyOn = true, NoteNumber = 70, Pitch = 69.6 },
        };
        var view = new MusicKeyboardRowsDisplay();
        view.Resources["HighlightText"] = new SolidColorBrush(Color.FromRgb(0x84, 0x94, 0xfc));
        view.SetChannels(channels);
        const int width = 512, height = 100;
        view.Measure(new Size(width, height)); view.Arrange(new Rect(0, 0, width, height)); view.UpdateLayout();
        string? save = Environment.GetEnvironmentVariable("MF_TEST_KEYBOARD_PNG");
        // 100%・125%・150%のどれでも同じ見え方になることを、実際に描いた画素で確かめる。
        foreach (double scale in new[] { 1.0, 1.25, 1.5 })
        {
            VisualTreeHelper.SetRootDpi(view, new DpiScale(scale, scale));
            view.InvalidateVisual(); view.UpdateLayout();
            int pixelWidth = (int)(width * scale), pixelHeight = (int)(height * scale);
            var bitmap = new RenderTargetBitmap(pixelWidth, pixelHeight, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bitmap.Render(view);
            if (!string.IsNullOrEmpty(save))
            {
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = File.Create(scale == 1 ? save : Path.ChangeExtension(save, $".{scale * 100:0}.png")); encoder.Save(stream);
            }
            var pixels = new byte[pixelWidth * pixelHeight * 4];
            bitmap.CopyPixels(pixels, pixelWidth * 4, 0);
            // 座標はDIPで指定し、その位置の画素を読む。
            Color At(double x, double y)
            {
                int i = ((int)Math.Floor(y * scale) * pixelWidth + (int)Math.Floor(x * scale)) * 4;
                return Color.FromRgb(pixels[i + 2], pixels[i + 1], pixels[i]);
            }
            bool IsHighlight(Color c) => c.R == 0x84 && c.G == 0x94 && c.B == 0xfc;
            bool IsLit(Color c) => c.G > 0xc0 && c.R < 0x40;
            bool HighlightBetween(double from, double to, double y)
            {
                for (double x = from; x < to; x += 1 / scale) if (IsHighlight(At(x, y))) return true;
                return false;
            }
            string at = $" at {scale:P0}";

            // 行の位置: 1行目の鍵盤は y=14〜42、2行目は y=56〜84（文字14 + 鍵盤28）。黒鍵の段は上から62%。
            const double keyWidth = 8;
            double Center(double pitch) => MusicDisplayDrawing.KeyCenter(pitch + 12, 24, 132, keyWidth)!.Value;
            double whiteRowLower = 14 + 28 * .62 + 4, blackRowUpper = 56 + 6;

            // 白鍵: 光る鍵の右の縁のすぐ外に棒が見え、縁取りで隠れない。棒は鍵の面の幅（すきまの分だけ細い）。
            double litRight = Center(69) + keyWidth / 2;
            check(IsLit(At(Center(69), whiteRowLower)), "White lit key is drawn above the pitch bar" + at);
            check(HighlightBetween(litRight - 1, litRight + 2, whiteRowLower),
                "Pitch bar next to a lit white key is not hidden by the key outline" + at);
            check(!IsHighlight(At(Center(69.4) + keyWidth / 2 + .5, whiteRowLower)),
                "Pitch bar is narrower than a key by the gap between keys" + at);
            check(!Enumerable.Range(14, 16).Any(y => IsHighlight(At(Center(69.4), y))),
                "Pitch bar of a white key does not appear in the black-key band" + at);

            // 黒鍵: 光る鍵の左の外（黒鍵の段）に棒が見え、白鍵の段には出ない。
            double blackLeft = Center(70) - Math.Max(2, Math.Floor(keyWidth * .7)) / 2;
            check(IsLit(At(Center(70), blackRowUpper)), "Black lit key is drawn above the pitch bar" + at);
            check(HighlightBetween(blackLeft - 3, blackLeft + 1, blackRowUpper),
                "Pitch bar next to a lit black key is visible in the black-key band" + at);
            check(!Enumerable.Range(74, 10).Any(y => IsHighlight(At(Center(69.6), y))),
                "Pitch bar of a black key does not appear in the white-key band" + at);
            check(!IsLit(At(Center(70), 56.0)), "Top 1 dot of lit black key is shaved" + at);
            check(!IsHighlight(At(Center(69.6), 56.0)), "Top 1 dot of black key highlight bar is shaved" + at);

            // サウンドバーのコンパクト鍵盤でも連続ピッチの棒が描かれることを確かめる。
            var mini = new MusicKeyboardDisplay();
            mini.Resources["HighlightText"] = new SolidColorBrush(Color.FromRgb(0x84, 0x94, 0xfc));
            mini.SetChannels([channels[0]]);
            double miniKeyPitch = MusicKeyboardDisplay.KeyPitch(scale);
            int miniW = (int)Math.Ceiling(63 * miniKeyPitch), miniH = 18;
            mini.Measure(new Size(miniW, miniH)); mini.Arrange(new Rect(0, 0, miniW, miniH)); mini.UpdateLayout();
            VisualTreeHelper.SetRootDpi(mini, new DpiScale(scale, scale));
            mini.InvalidateVisual(); mini.UpdateLayout();
            int miniPxW = (int)Math.Ceiling(miniW * scale), miniPxH = (int)Math.Ceiling(miniH * scale);
            var miniBmp = new RenderTargetBitmap(miniPxW, miniPxH, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            miniBmp.Render(mini);
            var miniPixels = new byte[miniPxW * miniPxH * 4];
            miniBmp.CopyPixels(miniPixels, miniPxW * 4, 0);
            bool foundMiniHighlight = false;
            for (int idx = 0; idx < miniPixels.Length; idx += 4)
            {
                if (miniPixels[idx + 2] == 0x84 && miniPixels[idx + 1] == 0x94 && miniPixels[idx] == 0xfc)
                {
                    foundMiniHighlight = true;
                    break;
                }
            }
            check(foundMiniHighlight, "Mini player keyboard renders continuous pitch marker" + at);

            // 鍵盤より低い音（o-1c）の三角は、左端の縦の真ん中にキーONのランプの色で出る。
            var low = new MusicKeyboardRowsDisplay();
            low.Resources["HighlightText"] = new SolidColorBrush(Color.FromRgb(0x84, 0x94, 0xfc));
            low.SetChannels([new MusicChannelSnapshot { Id = "p", ChipName = "PCM8", Kind = MusicChannelKind.Pcm, IsInUse = true, IsKeyOn = true, NoteNumber = 0, Pitch = 0 }]);
            low.Measure(new Size(width, height)); low.Arrange(new Rect(0, 0, width, height)); low.UpdateLayout();
            VisualTreeHelper.SetRootDpi(low, new DpiScale(scale, scale));
            low.InvalidateVisual(); low.UpdateLayout();
            var lowBmp = new RenderTargetBitmap(pixelWidth, pixelHeight, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            lowBmp.Render(low);
            lowBmp.CopyPixels(pixels, pixelWidth * 4, 0);
            var lamp = ((SolidColorBrush)MusicDisplayDrawing.KeyboardActiveWhiteKey).Color;
            bool IsLamp(Color c) => Math.Abs(c.R - lamp.R) <= 2 && Math.Abs(c.G - lamp.G) <= 2 && Math.Abs(c.B - lamp.B) <= 2;
            check(IsLamp(At(4, 14 + 14)) && !IsLamp(At(4, 14.5)) && !IsLamp(At(4, 41.5)) && !IsHighlight(At(4, 14 + 14)),
                "Below-range marker is drawn in the key lamp color at the vertical middle of the keyboard" + at);
        }
    }
}
