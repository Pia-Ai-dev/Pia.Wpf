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
    /// <summary>
    /// Bare marks, not circled ones: the run-progress row draws Pending and Running as geometry (a hollow ring
    /// and a pulsing dot), so a circled checkmark beside them would read as a third kind of ring. Running has an
    /// arm anyway — the row hides this icon for it, and a missing arm would fall through to Pending's ring.
    /// </summary>
    internal static SymbolRegular Glyph(AgentStepStatus status) => status switch
    {
        AgentStepStatus.Running => SymbolRegular.Circle24,
        AgentStepStatus.Done => SymbolRegular.Checkmark24,
        AgentStepStatus.Failed => SymbolRegular.Dismiss24,
        AgentStepStatus.Skipped => SymbolRegular.Prohibited24,
        _ => SymbolRegular.Circle24, // Pending
    };

    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
        => value is AgentStepStatus status ? Glyph(status) : Glyph(AgentStepStatus.Pending);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Which of a step row's three colored surfaces a <see cref="StepStatusToBrushConverter"/> instance
/// resolves for.</summary>
public enum StepBrushKind
{
    Glyph,
    Title,
    Segment,
}

/// <summary>Maps an <see cref="AgentStepStatus"/> to a Pia design-token brush for one of the step row's three
/// colored surfaces (<see cref="StepBrushKind"/>).</summary>
public sealed class StepStatusToBrushConverter : IValueConverter
{
    public StepBrushKind Kind { get; set; } = StepBrushKind.Glyph;

    /// <summary>Extracted so a theory can pin every (kind, status) pair without a live
    /// <see cref="Application"/> — the precedent <see cref="RunStateToBrushConverter.BrushKey"/> sets.</summary>
    internal static string BrushKey(StepBrushKind kind, AgentStepStatus status) => kind switch
    {
        StepBrushKind.Title => status switch
        {
            AgentStepStatus.Running or AgentStepStatus.Failed => "TextDefaultBrush",
            AgentStepStatus.Done => "TextMutedBrush",
            _ => "TextSubtleBrush", // Pending / Skipped — recessive, never the reading line
        },
        // The whole-plan strip. Pending is a visible rail rather than the row's fainter ring: it is 3px tall
        // and a ghost-weight fill would leave the strip looking like it ends at the running segment.
        StepBrushKind.Segment => status switch
        {
            AgentStepStatus.Running => "PiaAccentBrush",
            AgentStepStatus.Done => "PiaSuccessBrush",
            AgentStepStatus.Failed => "PiaDangerBrush",
            AgentStepStatus.Skipped => "BorderBrush_",
            _ => "BorderStrongBrush", // Pending
        },
        _ => status switch
        {
            AgentStepStatus.Running => "PiaAccentBrush",
            AgentStepStatus.Done => "PiaSuccessBrush",
            AgentStepStatus.Failed => "PiaDangerBrush",
            AgentStepStatus.Skipped => "TextSubtleBrush",
            _ => "TextGhostBrush", // Pending
        },
    };

    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = BrushKey(Kind, value is AgentStepStatus status ? status : AgentStepStatus.Pending);
        return Application.Current?.TryFindResource(key) as Brush ?? DependencyProperty.UnsetValue;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>The step title's weight: the row the user is meant to read (Running) and the row that broke
/// (Failed) are semibold; everything else is regular.</summary>
public sealed class StepStatusToTitleWeightConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
        => value is AgentStepStatus.Running or AgentStepStatus.Failed ? FontWeights.SemiBold : FontWeights.Normal;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>SemiBold for true, Normal for false — the audit trail's exception rows against its quiet ones.</summary>
public sealed class BoolToFontWeightConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? FontWeights.SemiBold : FontWeights.Normal;

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

/// <summary>Highlights the running step row (a soft accent wash) vs. a transparent idle row.</summary>
public sealed class BoolToRunningRowBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is true)
            return Application.Current?.TryFindResource("PiaAccentSoftBrush") as Brush ?? (object)DependencyProperty.UnsetValue;
        return Brushes.Transparent;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Which half of the signal band a <see cref="RunStateToBandBrushConverter"/> instance resolves for.</summary>
public enum BandBrushKind
{
    Background,
    Border,
}

/// <summary>
/// The signal band's tint and its hairline. The band — not a 12px colored word among four greys — is what
/// carries run state, so this mapping is the panel's loudest statement and every state gets an EXPLICIT arm:
/// the fall-through is the quiet neutral tint, which is the safe answer for an appended state (a run still
/// working would read as "nothing to report" rather than borrowing green or red).
/// </summary>
public sealed class RunStateToBandBrushConverter : IValueConverter
{
    public BandBrushKind Kind { get; set; } = BandBrushKind.Background;

    internal static string BrushKey(BandBrushKind kind, RunProgressState state) => state switch
    {
        // Work in flight: the same soft accent for both, because Delegating IS running — in a child.
        RunProgressState.Running or RunProgressState.WaitingForChildren =>
            kind == BandBrushKind.Background ? "PiaAccentSoftBrush" : "AccentBorderSoftBrush",
        // One step louder than Running, and bordered: these two are blocking prompts, not progress.
        RunProgressState.WaitingForInput or RunProgressState.Paused =>
            kind == BandBrushKind.Background ? "BandAccentBrush" : "AccentBorderBrush",
        RunProgressState.Completed =>
            kind == BandBrushKind.Background ? "SuccessSoftBrush" : "SuccessBorderBrush",
        RunProgressState.Failed =>
            kind == BandBrushKind.Background ? "DangerSoftBrush" : "DangerBorderBrush",
        // Truncated-Completed shares Planning's neutral tint deliberately: it is NOT a success and it is not a
        // failure, and the reason chip beside the label is what says which (R5 — never the danger palette).
        _ => kind == BandBrushKind.Background ? "SurfaceMutedBrush" : "BorderBrush_",
    };

    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = BrushKey(Kind, value is RunProgressState state ? state : RunProgressState.Planning);
        return Application.Current?.TryFindResource(key) as Brush ?? DependencyProperty.UnsetValue;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>The WHOLE card's border, not just the band's: a run that needs an answer and a run that failed have
/// to read as such in scrollback, where the band may be scrolled past.</summary>
public sealed class RunStateToCardBorderBrushConverter : IValueConverter
{
    internal static string BrushKey(RunProgressState state) => state switch
    {
        RunProgressState.WaitingForInput or RunProgressState.Paused => "AccentBorderBrush",
        RunProgressState.Failed => "DangerBorderBrush",
        _ => "BorderBrush_",
    };

    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = BrushKey(value is RunProgressState state ? state : RunProgressState.Planning);
        return Application.Current?.TryFindResource(key) as Brush ?? DependencyProperty.UnsetValue;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>The band's state glyph — shown only where the spinner is not (see
/// <see cref="RunStateToStateIconVisibilityConverter"/>), so the live arms are unreachable in practice and
/// exist only to keep the fall-through from having to answer for them.</summary>
public sealed class RunStateToIconConverter : IValueConverter
{
    internal static SymbolRegular Icon(RunProgressState state) => state switch
    {
        RunProgressState.WaitingForInput => SymbolRegular.ErrorCircle24,
        RunProgressState.Paused => SymbolRegular.PauseCircle24,
        RunProgressState.Completed => SymbolRegular.CheckmarkCircle24,
        RunProgressState.Failed => SymbolRegular.ErrorCircle24,
        _ => SymbolRegular.Circle24, // TruncatedCompleted (a hollow ring) + the spinner-covered live states
    };

    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
        => value is RunProgressState state ? Icon(state) : Icon(RunProgressState.Planning);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Filled for every settled or parked state except Truncated-Completed, whose hollow ring is the whole
/// point: it reads "finished" without claiming "finished well".</summary>
public sealed class RunStateToIconFilledConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
        => value is not RunProgressState.TruncatedCompleted;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>The exact complement of <see cref="RunStateToSpinnerVisibilityConverter"/>: the band shows a
/// spinner or a state glyph, never both and never neither.</summary>
public sealed class RunStateToStateIconVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
        => value is RunProgressState.Planning or RunProgressState.Running or RunProgressState.WaitingForChildren
            ? Visibility.Collapsed
            : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// The band's sub-line tints WITH its band: on an accent band (work in flight, or a blocking prompt) the metadata
/// line reads in accent, so the band is one signal rather than a coloured strip with a grey line in it. Everything
/// else — including the terminal states, where the lead line already carries the verdict's colour — stays muted.
/// </summary>
public sealed class RunStateToSubLineBrushConverter : IValueConverter
{
    internal static string BrushKey(RunProgressState state) => state switch
    {
        RunProgressState.Running or RunProgressState.WaitingForChildren
            or RunProgressState.WaitingForInput or RunProgressState.Paused => "PiaAccentBrush",
        _ => "TextMutedBrush",
    };

    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = BrushKey(value is RunProgressState state ? state : RunProgressState.Planning);
        return Application.Current?.TryFindResource(key) as Brush ?? DependencyProperty.UnsetValue;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>The band's lead line is a SENTENCE while work is in flight ("Building a plan…", the running step's
/// title) and a VERDICT once it stops ("Completed", "Failed", why it parked). Only the verdict is semibold.</summary>
public sealed class RunStateToLeadWeightConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
        => value is RunProgressState.Planning or RunProgressState.Running or RunProgressState.WaitingForChildren
            ? FontWeights.Normal
            : FontWeights.SemiBold;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Which surface of a decision pill or trace row a <see cref="DecisionSeverityToBrushConverter"/>
/// instance resolves for.</summary>
public enum DecisionBrushKind
{
    Foreground,
    Background,
    Border,
}

/// <summary>
/// The audit trail's three-tier palette. Awaiting is WARNING, not danger: the call was not refused, it is
/// waiting for the person reading this — and a red row would misreport why their run stopped.
/// </summary>
public sealed class DecisionSeverityToBrushConverter : IValueConverter
{
    public DecisionBrushKind Kind { get; set; } = DecisionBrushKind.Foreground;

    internal static string BrushKey(DecisionBrushKind kind, RunDecisionSeverity severity) => severity switch
    {
        RunDecisionSeverity.Awaiting => kind switch
        {
            DecisionBrushKind.Background => "WarnSoftBrush",
            DecisionBrushKind.Border => "WarnBorderBrush",
            _ => "WarnBrush",
        },
        RunDecisionSeverity.Refused => kind switch
        {
            DecisionBrushKind.Background => "DangerSoftBrush",
            DecisionBrushKind.Border => "DangerBorderBrush",
            _ => "PiaDangerBrush",
        },
        _ => kind switch
        {
            DecisionBrushKind.Background => "SurfaceBrush",
            DecisionBrushKind.Border => "BorderBrush_",
            _ => "TextSubtleBrush", // recessive: the rows nobody needs to act on
        },
    };

    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = BrushKey(Kind, value is RunDecisionSeverity severity ? severity : RunDecisionSeverity.Routine);
        return Application.Current?.TryFindResource(key) as Brush ?? DependencyProperty.UnsetValue;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
