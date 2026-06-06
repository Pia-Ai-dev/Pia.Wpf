using System;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Pia.Emoji;
using Xunit;

namespace Pia.Tests.Emoji;

/// <summary>
/// Guards the inline bottom-clip fix: the production <see cref="EmojiImageRenderer.Render(string,int,Color)"/>
/// path must emit the whole glyph with a transparent margin on every side (no edge is cut) and the ink must
/// be vertically centred. Segoe UI Emoji's glyph ink is ~0.98·em and sits a few percent below the centred
/// line, so drawing at fontSize == box clipped ~1-2px off the bottom inline; the renderer now draws smaller
/// and crops a square centred on the measured ink. Runs on an STA thread (the native pipeline needs an apartment).
/// </summary>
public class EmojiInkBoundsTests
{
    private static readonly string[] Samples = ["😀", "🌍", "👍🏽", "❤️", "🎉", "👨‍👩‍👧"];
    private static readonly int[] Sizes = [11, 12, 14, 16, 24, 32, 48, 64, 96, 128];

    [Fact]
    public void Render_KeepsWholeGlyph_Centered_NoClip()
    {
        var failures = RunSta(() =>
        {
            var renderer = new EmojiImageRenderer();
            var msgs = new System.Collections.Generic.List<string>();

            // Probe availability once; if the host has no color emoji stack, skip (mirrors sibling tests).
            if (renderer.Render("🌍", 64, Colors.Black) is null)
                return (string[]?)null;

            foreach (var size in Sizes)
            foreach (var emoji in Samples)
            {
                var image = renderer.Render(emoji, size, Colors.Black);
                if (image is null)
                {
                    msgs.Add($"{emoji}@{size}: null image");
                    continue;
                }

                var b = InkBounds(image);
                if (b.Opaque == 0)
                {
                    msgs.Add($"{emoji}@{size}: no ink");
                    continue;
                }

                int top = b.MinRow, bottom = size - 1 - b.MaxRow, left = b.MinCol, right = size - 1 - b.MaxCol;

                // Comfortable >=1px transparent border at body-text sizes and up. Below 14px the glyph is
                // ~10px and a 1px edge-touch is invisible; the crop still fully contains the body (it can't
                // cut, since it crops a box-sized window around an ink body that is < box), so the only
                // meaningful check there is centering, below.
                int minMargin = size >= 14 ? 1 : 0;
                if (top < minMargin || bottom < minMargin || left < minMargin || right < minMargin)
                    msgs.Add($"{emoji}@{size}: clipped (T{top} B{bottom} L{left} R{right}, need >={minMargin})");

                // Vertically centred by the crop: top and bottom margins match within rounding slack. This
                // is the real guard against the original bug, whose signature was a growing top margin with
                // a pinned bottom margin of 0 (e.g. T5 B0 at 64px) — a large top/bottom asymmetry.
                int slack = 1 + size / 32;
                if (Math.Abs(top - bottom) > slack)
                    msgs.Add($"{emoji}@{size}: off-centre vertically (T{top} B{bottom}, slack {slack})");
            }

            return msgs.ToArray();
        });

        if (failures is null)
        {
            Assert.Skip("Color emoji rendering unavailable on this host.");
            return;
        }

        Assert.True(failures.Length == 0, "Inline glyph clip/centering regressions:\n" + string.Join("\n", failures));
    }

    private readonly record struct Bounds(int Opaque, int MinRow, int MaxRow, int MinCol, int MaxCol);

    /// <summary>Bounding box of opaque (a&gt;128) pixels in a BitmapSource (premultiplied BGRA).</summary>
    private static Bounds InkBounds(BitmapSource bitmap)
    {
        int width = bitmap.PixelWidth, height = bitmap.PixelHeight, stride = width * 4;
        var px = new byte[stride * height];
        bitmap.CopyPixels(px, stride, 0);

        int minRow = int.MaxValue, maxRow = -1, minCol = int.MaxValue, maxCol = -1, opaque = 0;
        for (int y = 0; y < height; y++)
        {
            int row = y * stride;
            for (int x = 0; x < width; x++)
            {
                if (px[row + x * 4 + 3] <= 128)
                    continue;
                opaque++;
                if (y < minRow) minRow = y;
                if (y > maxRow) maxRow = y;
                if (x < minCol) minCol = x;
                if (x > maxCol) maxCol = x;
            }
        }
        return opaque == 0 ? default : new Bounds(opaque, minRow, maxRow, minCol, maxCol);
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
