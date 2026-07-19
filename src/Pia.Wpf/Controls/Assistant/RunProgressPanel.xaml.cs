using System.Windows.Controls;

namespace Pia.Controls.Assistant;

/// <summary>
/// Read-only run-progress panel (§15.1). Its DataContext is a <see cref="Pia.ViewModels.RunProgressViewModel"/>;
/// visibility is gated by the host (embedded only when the active chat has a live/selected run). No commands.
/// </summary>
public partial class RunProgressPanel : UserControl
{
    public RunProgressPanel() => InitializeComponent();
}
