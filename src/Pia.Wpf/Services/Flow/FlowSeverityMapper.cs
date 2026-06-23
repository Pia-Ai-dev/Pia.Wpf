using Pia.Models;
using Pia.Models.Flow;
using Wpf.Ui.Controls;

namespace Pia.Services.Flow;

/// <summary>
/// Maps each source's severity vocabulary onto the single <see cref="FlowSeverity"/> target (design §8, §11).
/// </summary>
public static class FlowSeverityMapper
{
    /// <summary>WPF-UI snackbar appearance → Flow severity.</summary>
    public static FlowSeverity FromSnackbar(ControlAppearance appearance) => appearance switch
    {
        ControlAppearance.Success => FlowSeverity.Success,
        ControlAppearance.Caution => FlowSeverity.Warning,
        ControlAppearance.Danger => FlowSeverity.Error,
        _ => FlowSeverity.Info,
    };

    /// <summary>Background-chat state → Flow severity (only the surface-worthy states have a meaningful mapping).</summary>
    public static FlowSeverity FromChatState(ChatState state) => state switch
    {
        ChatState.WaitingForTool => FlowSeverity.ActionRequired,
        ChatState.Completed => FlowSeverity.Success,
        ChatState.Error => FlowSeverity.Error,
        _ => FlowSeverity.Info,
    };
}
