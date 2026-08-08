using CommunityToolkit.Mvvm.ComponentModel;

namespace Pia.ViewModels;

/// <summary>
/// Read-only row for one recorded tool decision (Batch 03). Everything here is metadata — the store holds no
/// tool arguments, no results and no paths, so there is nothing else to project and nothing here to reveal.
/// A property that named a file or carried a payload would fail the reflection assert in
/// <c>RunProgressViewModelTimelineTests</c>.
/// </summary>
public sealed class TimelineRowViewModel : ObservableObject
{
    /// <summary>Schema, not user content: a built-in constant or an MCP server's declared tool name.</summary>
    public string ToolName { get; init; } = string.Empty;

    /// <summary>One of five localized categories over the eleven persisted decision ordinals.</summary>
    public string DecisionLabel { get; init; } = string.Empty;

    /// <summary>Localized "failed" when the authorized call threw; null otherwise.</summary>
    public string? OutcomeSuffix { get; init; }

    /// <summary>"Step N" when the row's step is still in the projected plan; null when it is not (a replan
    /// deletes step rows, and the trail deliberately outlives them).</summary>
    public string? StepLabel { get; init; }

    public string TimeLabel { get; init; } = string.Empty;

    /// <summary>How loudly the row reads. Metadata about the DECISION, not about the call's target — the two
    /// properties below are presentation, which is why they are allowed past the no-payload guard.</summary>
    public RunDecisionSeverity Severity { get; init; }

    public bool IsException => Severity != RunDecisionSeverity.Routine;

    /// <summary>Set on the first row BELOW the exception block, which draws the rule that separates the two
    /// halves — cheaper than a separator item, which the trace's row-shape guard would have to allow for.</summary>
    public bool ShowGroupSeparator { get; init; }

    /// <summary>See <c>RunProgressViewModel.RefreshThemeBrushes</c>.</summary>
    internal void RefreshThemeBrushes() => OnPropertyChanged(nameof(Severity));
}
