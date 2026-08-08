using CommunityToolkit.Mvvm.ComponentModel;

namespace Pia.ViewModels;

/// <summary>One decision category's count on the trace's summary row. Init-only and rebuilt with the rows it
/// summarizes, so it raises nothing — it derives <c>ObservableObject</c> only because the repo's MVVM rule
/// requires every <c>*ViewModel</c> to, exactly as <see cref="TimelineRowViewModel"/> does. No payload either.</summary>
public sealed class DecisionPillViewModel : ObservableObject
{
    public string Text { get; init; } = string.Empty;

    public RunDecisionSeverity Severity { get; init; }

    /// <summary>Drives the pill's weight — an exception category reads semibold, a routine one regular, which is
    /// half of what makes the summary row scannable at a glance rather than a row of equal chips.</summary>
    public bool IsException => Severity != RunDecisionSeverity.Routine;

    /// <summary>See <c>RunProgressViewModel.RefreshThemeBrushes</c>.</summary>
    internal void RefreshThemeBrushes() => OnPropertyChanged(nameof(Severity));
}
