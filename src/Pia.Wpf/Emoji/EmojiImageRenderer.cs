using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Pia.Emoji;

/// <summary>
/// Renders a single emoji grapheme cluster to a frozen <see cref="BitmapSource"/> through the OS
/// color-text stack (Direct2D + DirectWrite + WIC) via <see cref="EmojiInterop"/>. WPF's own text
/// stack ignores <c>COLR</c>/<c>CPAL</c> color layers, so we composite the emoji into a
/// premultiplied-BGRA WIC bitmap and hand WPF the resulting image instead.
/// </summary>
/// <remarks>
/// The three native factories are long-lived (created lazily, once). A <see cref="IDWriteTextFormat"/>
/// is cached per pixel size. Each rendered image is cached by (emoji, pixelSize). All native work is
/// serialized under <see cref="_gate"/> because the Direct2D factory is created single-threaded.
/// Failures (missing font, lost device, …) degrade to a cached <c>null</c> so callers can fall back to
/// a plain text glyph. Intended to be called from the UI thread (the WIC/DirectWrite objects are
/// thread-agnostic and access is lock-serialized, but the returned bitmaps are produced for UI use).
/// </remarks>
public sealed class EmojiImageRenderer
{
    private static readonly Lazy<EmojiImageRenderer> _shared = new(() => new EmojiImageRenderer());

    /// <summary>Process-wide instance; also registered as a DI singleton.</summary>
    public static EmojiImageRenderer Shared => _shared.Value;

    private const string EmojiFontFamily = "Segoe UI Emoji";
    private const int MaxPixelSize = 256;
    private const int CacheCapacity = 1024;

    /// <summary>
    /// Fraction of the bitmap box the glyph is drawn at. Below 1.0 so Segoe UI Emoji's line (taller
    /// than its em) fits inside the square box and the glyph isn't clipped at the edges when centered.
    /// Tuned empirically against the rendered ink bounds (see EmojiInkBoundsTests): at 1.0 the glyph ink
    /// is ~0.98·box and is clipped; 0.85 leaves a ~1px+ transparent margin on every side down to ~12px.
    /// </summary>
    private const float GlyphBoxFill = 0.85f;

    private readonly object _gate = new();
    private readonly Dictionary<int, IntPtr> _textFormats = new();
    private readonly Dictionary<(string Emoji, int Size, uint Foreground), BitmapSource?> _cache = new();
    private readonly Queue<(string Emoji, int Size, uint Foreground)> _cacheOrder = new();

    private IntPtr _d2dFactory;
    private IntPtr _dwriteFactory;
    private IntPtr _wicFactory;
    private bool _factoriesReady;

    [ThreadStatic] private static bool _comInitialized;

    /// <summary>Convenience overload: monochrome-fallback glyphs are drawn in black.</summary>
    public BitmapSource? Render(string emoji, int pixelSize) => Render(emoji, pixelSize, Colors.Black);

    /// <summary>
    /// Returns a frozen color image of <paramref name="emoji"/> sized <paramref name="pixelSize"/> px
    /// square, or <c>null</c> if rendering is unavailable (so the caller can fall back to text).
    /// Results are cached.
    /// </summary>
    /// <param name="foreground">
    /// Color for any glyph the font has no COLR/CPAL layers for (e.g. Windows renders country flags
    /// as monochrome two-letter region codes). True color emoji ignore it. Pass the surrounding text
    /// color so such fallbacks blend in instead of vanishing.
    /// </param>
    public BitmapSource? Render(string emoji, int pixelSize, Color foreground)
    {
        if (string.IsNullOrEmpty(emoji))
            return null;

        pixelSize = Math.Clamp(pixelSize, 1, MaxPixelSize);
        var key = (emoji, pixelSize, ToArgb(foreground));

        lock (_gate)
        {
            if (_cache.TryGetValue(key, out var cached))
                return cached;

            BitmapSource? image = null;
            try
            {
                image = RenderCore(emoji, pixelSize, foreground);
            }
            catch (Exception ex)
            {
                // Native render failed (font/device/COM). Cache the miss so we don't retry per layout.
                Debug.WriteLine($"[EmojiImageRenderer] render failed ({pixelSize}px): {ex.Message}");
            }

            _cache[key] = image;
            _cacheOrder.Enqueue(key);
            if (_cacheOrder.Count > CacheCapacity)
                _cache.Remove(_cacheOrder.Dequeue());

            return image;
        }
    }

    private BitmapSource RenderCore(string emoji, int pixelSize, Color foreground)
    {
        // Segoe UI Emoji's line box is taller than its em, and its glyph ink sits a few percent BELOW
        // the line centre, so drawing at fontSize == box and centring the line pushed the glyph past the
        // bitmap's bottom edge — the ~1-2px inline bottom-clip users saw. Fix: draw a bit smaller than
        // the box (GlyphBoxFill) into an over-tall canvas that can't clip, then crop a square centred on
        // the glyph's *actual measured* ink. That corrects both the line-gap overflow and the per-emoji
        // vertical bias with no font-metric magic, so every emoji ends up fully visible and centred.
        var fontSize = pixelSize * GlyphBoxFill;
        int tallHeight = pixelSize * 2;
        var tall = RenderToBuffer(emoji, pixelSize, tallHeight, fontSize, foreground);

        int stride = pixelSize * 4;
        var (inkMin, inkMax) = SolidInkRows(tall, pixelSize, tallHeight);
        int srcTop;
        if (inkMin < 0)
        {
            srcTop = (tallHeight - pixelSize) / 2; // no ink (e.g. font unavailable) — take the middle
        }
        else
        {
            // Crop so the glyph body gets balanced top/bottom margins (split the slack, floor on top),
            // which keeps it centred to within 1px regardless of even/odd body height — a floored centre
            // would systematically push it down and re-clip the bottom.
            int topMargin = Math.Max(0, (pixelSize - (inkMax - inkMin + 1)) / 2);
            srcTop = Math.Clamp(inkMin - topMargin, 0, tallHeight - pixelSize);
        }

        var square = new byte[stride * pixelSize];
        Buffer.BlockCopy(tall, srcTop * stride, square, 0, square.Length);

        var bitmap = new WriteableBitmap(pixelSize, pixelSize, 96, 96, PixelFormats.Pbgra32, null);
        bitmap.WritePixels(new Int32Rect(0, 0, pixelSize, pixelSize), square, stride, 0);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>
    /// First and last rows holding solid ink in a premultiplied-BGRA buffer (<c>(-1, -1)</c> when empty).
    /// The threshold ignores the faint anti-aliasing halo (which is vertically lop-sided and would bias
    /// the crop) so the square is centred on the glyph's *visible body*.
    /// </summary>
    private static (int Min, int Max) SolidInkRows(byte[] buffer, int width, int height)
    {
        const int solid = 128;
        int stride = width * 4;
        int minRow = -1, maxRow = -1;
        for (int y = 0; y < height; y++)
        {
            int row = y * stride;
            bool any = false;
            for (int a = row + 3; a < row + stride; a += 4)
            {
                if (buffer[a] > solid) { any = true; break; }
            }
            if (!any)
                continue;
            if (minRow < 0)
                minRow = y;
            maxRow = y;
        }
        return (minRow, maxRow);
    }

    /// <summary>
    /// Draws <paramref name="emoji"/> at <paramref name="fontSize"/> DIPs, centered into a
    /// <paramref name="width"/>×<paramref name="height"/> premultiplied-BGRA buffer (stride =
    /// width*4). Exposed internally so tests can measure the glyph's true ink bounds at an explicit
    /// canvas/font size (e.g. an over-tall canvas that can't clip).
    /// </summary>
    internal byte[] RenderToBuffer(string emoji, int width, int height, float fontSize, Color foreground)
    {
        EnsureFactories();

        IntPtr wicBitmap = IntPtr.Zero, renderTarget = IntPtr.Zero, deviceContext = IntPtr.Zero, brush = IntPtr.Zero;
        try
        {
            Check(EmojiInterop.CreateBitmap(
                _wicFactory, (uint)width, (uint)height,
                EmojiInterop.GUID_WICPixelFormat32bppPBGRA, EmojiInterop.WICBitmapCacheOnLoad, out wicBitmap), "CreateBitmap");

            var props = new EmojiInterop.D2D1_RENDER_TARGET_PROPERTIES
            {
                type = EmojiInterop.D2D1_RENDER_TARGET_TYPE_DEFAULT,
                pixelFormat = new EmojiInterop.D2D1_PIXEL_FORMAT
                {
                    format = EmojiInterop.DXGI_FORMAT_B8G8R8A8_UNORM,
                    alphaMode = EmojiInterop.D2D1_ALPHA_MODE_PREMULTIPLIED,
                },
                dpiX = 96f,
                dpiY = 96f,
                usage = 0,
                minLevel = 0,
            };
            Check(EmojiInterop.CreateWicBitmapRenderTarget(_d2dFactory, wicBitmap, props, out renderTarget), "CreateWicBitmapRenderTarget");

            // The COLR/CPAL color path is most reliable on the D2D 1.1 device context; QI for it and
            // draw there. Falls back to the base render target if the QI ever fails (it won't on our floor).
            var drawTarget = renderTarget;
            if (EmojiInterop.QueryInterface(renderTarget, EmojiInterop.IID_ID2D1DeviceContext, out deviceContext) >= 0 && deviceContext != IntPtr.Zero)
                drawTarget = deviceContext;

            var textFormat = GetTextFormat(fontSize);

            // Foreground brush only colors monochrome fallback glyphs; COLR layers use the font palette.
            Check(EmojiInterop.CreateSolidColorBrush(renderTarget, ToColorF(foreground), out brush), "CreateSolidColorBrush");

            var layoutRect = new EmojiInterop.D2D1_RECT_F { left = 0, top = 0, right = width, bottom = height };

            EmojiInterop.BeginDraw(renderTarget);
            EmojiInterop.Clear(renderTarget, default); // transparent
            EmojiInterop.DrawText(drawTarget, emoji, textFormat, layoutRect, brush, EmojiInterop.D2D1_DRAW_TEXT_OPTIONS_ENABLE_COLOR_FONT);

            int endHr = EmojiInterop.EndDraw(renderTarget);
            if (endHr == EmojiInterop.D2DERR_RECREATE_TARGET)
            {
                ResetFactories();
                throw new InvalidOperationException("Direct2D device lost (D2DERR_RECREATE_TARGET).");
            }

            Check(endHr, "EndDraw");

            int stride = width * 4;
            var buffer = new byte[stride * height];
            Check(EmojiInterop.CopyPixels(wicBitmap, (uint)stride, buffer), "CopyPixels");
            return buffer;
        }
        finally
        {
            // Release in reverse acquisition order; the text format is cached and released only on reset.
            EmojiInterop.Release(brush);
            EmojiInterop.Release(deviceContext);
            EmojiInterop.Release(renderTarget);
            EmojiInterop.Release(wicBitmap);
        }
    }

    private IntPtr GetTextFormat(float fontSize)
    {
        // Cache by rounded size; the production path derives one size per pixelSize, so this is a hit.
        int key = (int)MathF.Round(fontSize);
        if (_textFormats.TryGetValue(key, out var fmt))
            return fmt;

        // dpi is 96 on the render target, so 1 DIP == 1 px.
        Check(EmojiInterop.CreateTextFormat(_dwriteFactory, EmojiFontFamily, fontSize, out fmt), "CreateTextFormat");
        EmojiInterop.SetTextAlignment(fmt, EmojiInterop.DWRITE_TEXT_ALIGNMENT_CENTER);
        EmojiInterop.SetParagraphAlignment(fmt, EmojiInterop.DWRITE_PARAGRAPH_ALIGNMENT_CENTER);
        _textFormats[key] = fmt;
        return fmt;
    }

    private void EnsureFactories()
    {
        if (_factoriesReady)
            return;

        EnsureComInitialized();

        // Build into locals and publish only once all three succeed, releasing on partial failure —
        // otherwise a retry would overwrite a populated field and leak the earlier factory.
        IntPtr d2d = IntPtr.Zero, dwrite = IntPtr.Zero, wic = IntPtr.Zero;
        try
        {
            Check(EmojiInterop.D2D1CreateFactory(EmojiInterop.D2D1_FACTORY_TYPE_SINGLE_THREADED, EmojiInterop.IID_ID2D1Factory, IntPtr.Zero, out d2d), "D2D1CreateFactory");
            Check(EmojiInterop.DWriteCreateFactory(EmojiInterop.DWRITE_FACTORY_TYPE_SHARED, EmojiInterop.IID_IDWriteFactory, out dwrite), "DWriteCreateFactory");
            Check(EmojiInterop.CoCreateInstance(EmojiInterop.CLSID_WICImagingFactory, IntPtr.Zero, EmojiInterop.CLSCTX_INPROC_SERVER, EmojiInterop.IID_IWICImagingFactory, out wic), "CoCreateInstance(WIC)");
        }
        catch
        {
            EmojiInterop.Release(wic);
            EmojiInterop.Release(dwrite);
            EmojiInterop.Release(d2d);
            throw;
        }

        _d2dFactory = d2d;
        _dwriteFactory = dwrite;
        _wicFactory = wic;
        _factoriesReady = true;
    }

    private void ResetFactories()
    {
        foreach (var fmt in _textFormats.Values)
            EmojiInterop.Release(fmt);
        _textFormats.Clear();

        EmojiInterop.Release(_wicFactory);
        EmojiInterop.Release(_dwriteFactory);
        EmojiInterop.Release(_d2dFactory);
        _wicFactory = _dwriteFactory = _d2dFactory = IntPtr.Zero;
        _factoriesReady = false;
    }

    private static void EnsureComInitialized()
    {
        if (_comInitialized)
            return;

        // We only need *an* apartment so CoCreateInstance(WIC) works; the WIC factory is free-threaded.
        // Ignore S_FALSE (already initialized) and RPC_E_CHANGED_MODE (thread already in another mode).
        EmojiInterop.CoInitializeEx(IntPtr.Zero, EmojiInterop.COINIT_APARTMENTTHREADED);
        _comInitialized = true;
    }

    private static void Check(int hr, string what)
    {
        if (hr < 0)
            throw new InvalidOperationException($"{what} failed (HRESULT 0x{hr:X8}).");
    }

    private static uint ToArgb(Color c) => ((uint)c.A << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;

    private static EmojiInterop.D2D1_COLOR_F ToColorF(Color c) => new()
    {
        r = c.R / 255f,
        g = c.G / 255f,
        b = c.B / 255f,
        a = c.A / 255f,
    };
}
