using Pia.Localization;
using Pia.Views.Controls;

namespace Pia.Views.Dialogs.Overlay;

public partial class PolicyRestartOverlayPanel : OverlayDialogPanel
{
    public PolicyRestartOverlayPanel()
    {
        InitializeComponent();
    }

    public event EventHandler? RestartRequested;

    /// <summary>Deliberately empty: the base raises Close, and this overlay has no dismiss.</summary>
    public override void OnEscapePressed()
    {
    }

    /// <summary>No result ever leaves this panel: the host collapses its content the moment one does, which
    /// would hand back a live app for the whole pre-exit sequence.</summary>
    protected override void RaiseResultChosen(object result)
    {
        if (result is not OverlayDialogResult.Primary || !IsPrimaryButtonEnabled)
            return;

        IsPrimaryButtonEnabled = false;
        PrimaryButtonText = LocalizationSource.Instance["PolicyRestart_Restarting"];
        RestartRequested?.Invoke(this, EventArgs.Empty);
    }
}
