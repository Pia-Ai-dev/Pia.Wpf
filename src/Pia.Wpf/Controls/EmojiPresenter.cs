using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Pia.Emoji;

namespace Pia.Controls;

/// <summary>
/// An <see cref="Image"/> that shows a single emoji rendered in color through the OS text stack
/// (<see cref="EmojiImageRenderer"/>), sidestepping WPF's monochrome-glyph / tofu limitation. Set
/// <see cref="Emoji"/> and <see cref="GlyphSize"/>; the bitmap is produced at device-pixel resolution
/// for the current DPI so it stays crisp, and re-rendered when the DPI changes.
/// </summary>
public sealed class EmojiPresenter : Image
{
    public EmojiPresenter()
    {
        Stretch = Stretch.Uniform;
        SnapsToDevicePixels = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
        Loaded += (_, _) => UpdateImage();
    }

    public static readonly DependencyProperty EmojiProperty = DependencyProperty.Register(
        nameof(Emoji), typeof(string), typeof(EmojiPresenter),
        new PropertyMetadata(string.Empty, OnVisualChanged));

    public static readonly DependencyProperty GlyphSizeProperty = DependencyProperty.Register(
        nameof(GlyphSize), typeof(double), typeof(EmojiPresenter),
        new PropertyMetadata(16.0, OnVisualChanged));

    /// <summary>The emoji grapheme cluster to display.</summary>
    public string Emoji
    {
        get => (string)GetValue(EmojiProperty);
        set => SetValue(EmojiProperty, value);
    }

    /// <summary>Display size of the glyph in DIPs (the image is laid out as a square of this size).</summary>
    public double GlyphSize
    {
        get => (double)GetValue(GlyphSizeProperty);
        set => SetValue(GlyphSizeProperty, value);
    }

    private static void OnVisualChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((EmojiPresenter)d).UpdateImage();

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        UpdateImage();
    }

    private void UpdateImage()
    {
        var glyphSize = GlyphSize;
        Width = Height = glyphSize;

        var emoji = Emoji;
        if (string.IsNullOrEmpty(emoji) || glyphSize <= 0)
        {
            Source = null;
            return;
        }

        // Defer the first render until the element is connected: VisualTreeHelper.GetDpi returns the
        // system DPI (not the real monitor's) for a disconnected element, so rendering now could pick
        // the wrong size. The Loaded handler re-runs this once connected, at the correct DPI.
        if (!IsLoaded)
            return;

        var dpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (dpiScale <= 0)
            dpiScale = 1.0;

        var pixelSize = (int)Math.Ceiling(glyphSize * dpiScale);
        Source = EmojiImageRenderer.Shared.Render(emoji, pixelSize, ResolveForeground());
    }

    /// <summary>
    /// The inherited text color, used to fill monochrome fallback glyphs (e.g. Windows draws country
    /// flags as two-letter region codes) so they match the surrounding text instead of vanishing.
    /// </summary>
    private Color ResolveForeground() =>
        TextElement.GetForeground(this) is SolidColorBrush brush ? brush.Color : Colors.Black;
}
