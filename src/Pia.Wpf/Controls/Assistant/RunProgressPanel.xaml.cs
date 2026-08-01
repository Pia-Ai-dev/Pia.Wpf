using System.Windows.Controls;

namespace Pia.Controls.Assistant;

/// <summary>
/// Run-progress panel (§15.1). Its DataContext is a <see cref="Pia.ViewModels.RunProgressViewModel"/>;
/// visibility is gated by the host (embedded only when the active chat has a live/selected run).
/// <b>Not command-free</b> (W16 — this line went stale before Batch 08 touched it): Continue (resume a
/// parked run) and Publish (a settled run's retained workspace) already predate this batch; G8 adds a
/// header Pause command alongside them.
/// </summary>
public partial class RunProgressPanel : UserControl
{
    public RunProgressPanel() => InitializeComponent();
}
