using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Pia.Localization;
using Pia.Models;
using Pia.ViewModels;
using Wpf.Ui.Controls;

namespace Pia.Converters;

/// <summary>Maps an <see cref="AgentStepStatus"/> to a WPF-UI <see cref="SymbolRegular"/> glyph
/// (Segoe Fluent, never emoji — mirrors <see cref="ChatStateToGlyphConverter"/>).</summary>
public sealed class StepStatusToGlyphConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        AgentStepStatus.Running => SymbolRegular.ArrowSync24,
        AgentStepStatus.Done => SymbolRegular.CheckmarkCircle24,
        AgentStepStatus.Failed => SymbolRegular.ErrorCircle24,
        AgentStepStatus.Skipped => SymbolRegular.DismissCircle20,
        _ => SymbolRegular.Circle24, // Pending
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Maps an <see cref="AgentStepStatus"/> to a Pia design-token accent brush.</summary>
public sealed class StepStatusToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value switch
        {
            AgentStepStatus.Running => "PiaAccentBrush",
            AgentStepStatus.Done => "PiaSuccessBrush",
            AgentStepStatus.Failed => "PiaDangerBrush",
            _ => "TextMutedBrush", // Pending / Skipped
        };
        return Application.Current?.TryFindResource(key) as Brush ?? DependencyProperty.UnsetValue;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Maps a <see cref="RunProgressState"/> to a localized label (truncated-Completed reads "Completed").</summary>
public sealed class RunStateToLabelConverter : IValueConverter
{
    /// <summary>
    /// The state → resx-key mapping, extracted so a theory can pin every member (precedent:
    /// <c>RunProgressViewModel.DecisionLabelKey</c>). Worth extracting because the fall-through arm here is
    /// the most expensive default in the panel: a member with no arm renders a run that is still working as
    /// <b>"Completed"</b>. Every non-terminal state therefore gets an EXPLICIT arm, and the default is
    /// reached only by <see cref="RunProgressState.Completed"/> and
    /// <see cref="RunProgressState.TruncatedCompleted"/>, which share the key deliberately.
    /// </summary>
    internal static string LabelKey(RunProgressState state) => state switch
    {
        RunProgressState.Planning => "Run_State_Planning",
        RunProgressState.Running => "Run_State_Running",
        RunProgressState.Failed => "Run_State_Failed",
        RunProgressState.WaitingForInput => "Run_State_WaitingForInput",
        RunProgressState.Paused => "Run_State_Paused",
        RunProgressState.WaitingForChildren => "Run_State_WaitingForChildren", // 07 G8 — never "Completed"
        _ => "Run_State_Completed", // Completed + TruncatedCompleted both read "Completed"
    };

    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not RunProgressState state) return string.Empty;
        return LocalizationSource.Instance[LabelKey(state)];
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Maps a <see cref="RunProgressState"/> to the state-label brush. Truncated-Completed is MUTED
/// (TextMutedBrush) — never <c>PiaDangerBrush</c>; a clean Completed is <c>PiaSuccessBrush</c>.</summary>
public sealed class RunStateToBrushConverter : IValueConverter
{
    /// <summary>
    /// The state → resource-key mapping, extracted (precedent: <see cref="RunStateToLabelConverter.LabelKey"/>)
    /// so a theory can pin it without resolving a WPF resource, which needs a live <see cref="Application"/>.
    /// </summary>
    internal static string BrushKey(RunProgressState state) => state switch
    {
        RunProgressState.Completed => "PiaSuccessBrush",
        RunProgressState.TruncatedCompleted => "TextMutedBrush", // muted truncation note, never danger (R5)
        RunProgressState.Failed => "PiaDangerBrush",
        RunProgressState.WaitingForInput => "PiaAccentBrush", // action-needed accent — invites the Continue
        // Batch 08 G8: a paused run now carries the SAME action-needed affordance WaitingForInput does —
        // both offer the identical Continue command. Do NOT touch Run_State_Paused in any locale and do
        // not add a spinner arm for Paused (RunStateToSpinnerVisibilityConverter deliberately excludes it):
        // that pairing is what makes the German participle "Pausiert" safe (967d761's "Delegiert" ->
        // "Verteilt Arbeit" lesson) — a lit spinner beside a past-participle label reads as still moving.
        RunProgressState.Paused => "PiaAccentBrush",
        // 07 G8: TextDefaultBrush is also what the default arm gives it — made EXPLICIT so the next
        // appended member is a decision rather than an accident.
        RunProgressState.WaitingForChildren => "TextDefaultBrush",
        _ => "TextDefaultBrush", // Planning / Running
    };

    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        // Byte-identical to the pre-extraction fall-through: a non-RunProgressState value (the binding engine
        // hands one across during a transient unresolved pass) still resolves "TextDefaultBrush", exactly as
        // the old inline `value switch { …, _ => "TextDefaultBrush" }` did.
        var key = value is RunProgressState state ? BrushKey(state) : "TextDefaultBrush";
        return Application.Current?.TryFindResource(key) as Brush ?? DependencyProperty.UnsetValue;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Visible whenever work is happening: Planning, Running, or — 07 G8 — WaitingForChildren, where the
/// parent is parked but its child runs are working, so a still spinner would read as a stalled run.</summary>
public sealed class RunStateToSpinnerVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
        => value is RunProgressState.Planning or RunProgressState.Running or RunProgressState.WaitingForChildren
            ? Visibility.Visible
            : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Highlights the running step row (SurfaceMutedBrush) vs. a transparent idle row.</summary>
public sealed class BoolToRunningRowBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is true)
            return Application.Current?.TryFindResource("SurfaceMutedBrush") as Brush ?? (object)DependencyProperty.UnsetValue;
        return Brushes.Transparent;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
