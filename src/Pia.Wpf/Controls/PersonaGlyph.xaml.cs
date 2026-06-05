using System.Windows;
using System.Windows.Controls;
using Pia.Shared;

namespace Pia.Controls;

/// <summary>
/// Renders a persona's glyph: the shared Pia app icon for the two built-in Pia personas
/// (<see cref="BuiltInPersonas.PiaPersonalId"/> / <see cref="BuiltInPersonas.PiaBusinessId"/>) —
/// matching the assistant-chat avatar — and the persona's emoji for everyone else. Reused by the
/// settings persona list and the in-chat persona picker.
/// </summary>
public partial class PersonaGlyph : UserControl
{
    public PersonaGlyph()
    {
        InitializeComponent();
        UpdateGlyph();
    }

    public static readonly DependencyProperty PersonaIdProperty = DependencyProperty.Register(
        nameof(PersonaId), typeof(Guid), typeof(PersonaGlyph),
        new PropertyMetadata(Guid.Empty, OnGlyphChanged));

    public static readonly DependencyProperty EmojiProperty = DependencyProperty.Register(
        nameof(Emoji), typeof(string), typeof(PersonaGlyph),
        new PropertyMetadata(string.Empty, OnGlyphChanged));

    public static readonly DependencyProperty GlyphSizeProperty = DependencyProperty.Register(
        nameof(GlyphSize), typeof(double), typeof(PersonaGlyph),
        new PropertyMetadata(16.0, OnGlyphChanged));

    public Guid PersonaId
    {
        get => (Guid)GetValue(PersonaIdProperty);
        set => SetValue(PersonaIdProperty, value);
    }

    public string Emoji
    {
        get => (string)GetValue(EmojiProperty);
        set => SetValue(EmojiProperty, value);
    }

    /// <summary>Emoji font size; the Pia icon is rendered a few pixels larger so the two read as the same size.</summary>
    public double GlyphSize
    {
        get => (double)GetValue(GlyphSizeProperty);
        set => SetValue(GlyphSizeProperty, value);
    }

    private static void OnGlyphChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((PersonaGlyph)d).UpdateGlyph();

    private void UpdateGlyph()
    {
        if (PiaIcon is null || EmojiText is null)
            return;

        var isPia = PersonaId == BuiltInPersonas.PiaPersonalId || PersonaId == BuiltInPersonas.PiaBusinessId;

        PiaIcon.Visibility = isPia ? Visibility.Visible : Visibility.Collapsed;
        PiaIcon.Width = PiaIcon.Height = GlyphSize + 4;

        EmojiText.Visibility = isPia ? Visibility.Collapsed : Visibility.Visible;
        EmojiText.FontSize = GlyphSize;
        EmojiText.Text = Emoji;
    }
}
