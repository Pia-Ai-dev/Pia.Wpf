using System.Windows.Controls;
using System.Windows.Media;
using Pia.Helpers;

namespace Pia.Controls.Vault;

public partial class PiaInspectorHeader : UserControl
{
    public PiaInspectorHeader() => InitializeComponent();

    /// <summary>The installed Obsidian's own icon, or null — the XAML then falls back to a glyph.</summary>
    public ImageSource? ObsidianIcon => ObsidianLauncher.TryGetIcon();
}
