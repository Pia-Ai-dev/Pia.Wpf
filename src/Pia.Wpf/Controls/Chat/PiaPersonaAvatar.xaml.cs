using System.Windows;
using System.Windows.Controls;

namespace Pia.Controls.Chat;

/// <summary>
/// The assistant-chat avatar box (rounded, shadowed) showing a persona's glyph — the Pia app icon
/// for the built-in Pia personas, the persona's emoji otherwise. Used in the live chat and the
/// history inspector.
/// </summary>
public partial class PiaPersonaAvatar : UserControl
{
    public PiaPersonaAvatar() => InitializeComponent();

    public static readonly DependencyProperty PersonaIdProperty = DependencyProperty.Register(
        nameof(PersonaId), typeof(Guid), typeof(PiaPersonaAvatar), new PropertyMetadata(Guid.Empty));

    public static readonly DependencyProperty EmojiProperty = DependencyProperty.Register(
        nameof(Emoji), typeof(string), typeof(PiaPersonaAvatar), new PropertyMetadata(string.Empty));

    /// <summary>Batch 07 §0.8/§4.5: a #RRGGBB hex accent, straight into the already-registered
    /// <c>HexToBrushConverter</c> (returns Transparent for null/blank/unparseable — an unset accent
    /// renders exactly as before this DP existed). Additive, null default: every other consumer of this
    /// control (live chat, history inspector) is unaffected.</summary>
    public static readonly DependencyProperty AccentColorProperty = DependencyProperty.Register(
        nameof(AccentColor), typeof(string), typeof(PiaPersonaAvatar), new PropertyMetadata(null));

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

    public string? AccentColor
    {
        get => (string?)GetValue(AccentColorProperty);
        set => SetValue(AccentColorProperty, value);
    }
}
