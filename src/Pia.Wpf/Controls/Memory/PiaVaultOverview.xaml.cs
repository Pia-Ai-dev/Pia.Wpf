using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Pia.Controls.Memory;

/// <summary>
/// Vault Overview: a composition-by-category visualization (proportional segmented bar + legend) shown
/// in the Memory right pane when nothing is selected and the vault is non-empty. Inherits the
/// <c>MemoryViewModel</c> DataContext and binds <c>VaultComposition</c>.
/// </summary>
public partial class PiaVaultOverview : UserControl
{
    public PiaVaultOverview() => InitializeComponent();

    // Round the bar's corners by clipping to a rounded rectangle. ClipToBounds ignores corner radii and a
    // rounded Border does not clip child Rectangles, so the clip geometry is maintained here as the bar
    // host resizes. Purely visual — no view-model logic.
    private void BarHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is FrameworkElement host)
        {
            host.Clip = new RectangleGeometry(new Rect(new Point(0, 0), e.NewSize), 8, 8);
        }
    }
}
