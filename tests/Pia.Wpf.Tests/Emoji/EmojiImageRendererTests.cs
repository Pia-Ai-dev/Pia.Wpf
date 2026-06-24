using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Windows.Media.Imaging;
using Pia.Emoji;
using Xunit;

namespace Pia.Tests.Emoji;

/// <summary>
/// Proves the hand-rolled Direct2D/DirectWrite/WIC pipeline composites <em>color</em> emoji into a
/// bitmap on a software/WIC target (the plan's "lynchpin" risk). Runs the native work on an STA
/// thread. Set the <c>PIA_EMOJI_SPIKE_DIR</c> environment variable to also dump PNGs there for a
/// visual check; normal runs do no file I/O.
/// </summary>
public class EmojiImageRendererTests
{
    // Emoji that Segoe UI Emoji draws with COLR/CPAL color layers — must come out chromatic.
    private static readonly string[] ColorSamples =
    [
        "🌍",        // continents → blue/green, an easy color signal
        "👨‍👩‍👧",  // ZWJ family
        "👍🏽",       // skin-tone modifier
        "1️⃣",        // keycap sequence (blue square)
    ];

    // Windows has no color country-flag glyphs: it renders the two regional-indicator letters as a
    // monochrome glyph (filled by our fallback brush). It must still rasterize *something* (no tofu).
    private static readonly string[] MonochromeOnWindowsSamples = ["🇩🇪"];

    [Fact]
    public void Renders_ColorEmoji_OnSoftwareTarget()
    {
        const int size = 64;
        string[] all = [.. ColorSamples, .. MonochromeOnWindowsSamples];

        var images = RunSta(() =>
        {
            var renderer = new EmojiImageRenderer();
            var rendered = new Dictionary<string, BitmapSource?>();
            foreach (var emoji in all)
                rendered[emoji] = renderer.Render(emoji, size);

            DumpPngsIfRequested(rendered, size);
            return rendered;
        });

        if (images["🌍"] is null)
        {
            Assert.Skip("Color emoji rendering unavailable on this host (no Segoe UI Emoji / Direct2D).");
            return;
        }

        foreach (var emoji in all)
        {
            var image = images[emoji];
            Assert.NotNull(image);
            Assert.Equal(size, image!.PixelWidth);
            Assert.Equal(size, image.PixelHeight);

            var (opaque, hasColor) = Analyze(image);
            Assert.True(opaque > size * size / 20, $"'{emoji}' produced too few opaque pixels ({opaque}); nothing rendered.");

            if (Array.IndexOf(ColorSamples, emoji) >= 0)
                Assert.True(hasColor, $"'{emoji}' rendered monochrome — the ENABLE_COLOR_FONT path is not active.");
        }
    }

    [Fact]
    public void Caches_RepeatedRenders_ReturnSameInstance()
    {
        var (first, second) = RunSta(() =>
        {
            var renderer = new EmojiImageRenderer();
            return (renderer.Render("🌟", 32), renderer.Render("🌟", 32));
        });

        if (first is null)
        {
            Assert.Skip("Color emoji rendering unavailable on this host.");
            return;
        }

        Assert.Same(first, second);
    }

    /// <summary>Counts opaque pixels and detects whether any opaque pixel is chromatic (not gray/white).</summary>
    private static (int Opaque, bool HasColor) Analyze(BitmapSource bitmap)
    {
        int width = bitmap.PixelWidth, height = bitmap.PixelHeight, stride = width * 4;
        var pixels = new byte[stride * height];
        bitmap.CopyPixels(pixels, stride, 0);

        int opaque = 0;
        bool hasColor = false;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            byte b = pixels[i], g = pixels[i + 1], r = pixels[i + 2], a = pixels[i + 3];
            if (a <= 200)
                continue;

            opaque++;
            int max = Math.Max(r, Math.Max(g, b));
            int min = Math.Min(r, Math.Min(g, b));
            if (max - min > 40) // chromatic spread → genuine color, not a white/gray monochrome glyph
                hasColor = true;
        }

        return (opaque, hasColor);
    }

    private static void DumpPngsIfRequested(Dictionary<string, BitmapSource?> images, int size)
    {
        var dir = Environment.GetEnvironmentVariable("PIA_EMOJI_SPIKE_DIR");
        if (string.IsNullOrEmpty(dir))
            return;

        Directory.CreateDirectory(dir);
        int index = 0;
        foreach (var (emoji, image) in images)
        {
            index++;
            if (image is null)
                continue;

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var stream = File.Create(Path.Combine(dir, $"emoji-{index}-{size}px.png"));
            encoder.Save(stream);
        }
    }

    private static T RunSta<T>(Func<T> func)
    {
        T result = default!;
        Exception? error = null;

        var thread = new Thread(() =>
        {
            try { result = func(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (error is not null)
            throw new InvalidOperationException("STA render worker failed.", error);

        return result;
    }
}
