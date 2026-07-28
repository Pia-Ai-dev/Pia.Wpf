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
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not RunProgressState state) return string.Empty;
        var key = state switch
        {
            RunProgressState.Planning => "Run_State_Planning",
            RunProgressState.Running => "Run_State_Running",
            RunProgressState.Failed => "Run_State_Failed",
            RunProgressState.WaitingForInput => "Run_State_WaitingForInput",
            RunProgressState.Paused => "Run_State_Paused",
            _ => "Run_State_Completed", // Completed + TruncatedCompleted both read "Completed"
        };
        return LocalizationSource.Instance[key];
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Maps a <see cref="RunProgressState"/> to the state-label brush. Truncated-Completed is MUTED
/// (TextMutedBrush) — never <c>PiaDangerBrush</c>; a clean Completed is <c>PiaSuccessBrush</c>.</summary>
public sealed class RunStateToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value switch
        {
            RunProgressState.Completed => "PiaSuccessBrush",
            RunProgressState.TruncatedCompleted => "TextMutedBrush", // muted truncation note, never danger (R5)
            RunProgressState.Failed => "PiaDangerBrush",
            RunProgressState.WaitingForInput => "PiaAccentBrush", // action-needed accent — invites the Continue
            RunProgressState.Paused => "TextMutedBrush",
            _ => "TextDefaultBrush", // Planning / Running
        };
        return Application.Current?.TryFindResource(key) as Brush ?? DependencyProperty.UnsetValue;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Visible only while the run is Planning or Running (drives the header spinner).</summary>
public sealed class RunStateToSpinnerVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
        => value is RunProgressState.Planning or RunProgressState.Running
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
