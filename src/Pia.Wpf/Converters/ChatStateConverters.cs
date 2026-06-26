using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Pia.Localization;
using Pia.Models;
using Wpf.Ui.Controls;

namespace Pia.Converters;

/// <summary>
/// Maps a <see cref="ChatState"/> to a soft fill (<see cref="ChatStateBrushKind.Background"/>)
/// or accent (<see cref="ChatStateBrushKind.Foreground"/>) Pia design-token brush.
/// Resolves via <c>Application.Current.TryFindResource</c> so theme swaps flow through.
/// Mirrors <see cref="ReminderStatusToBrushConverter"/>.
/// </summary>
public class ChatStateToBrushConverter : IValueConverter
{
    public enum ChatStateBrushKind
    {
        Background,
        Foreground,
    }

    public ChatStateBrushKind Kind { get; set; } = ChatStateBrushKind.Background;

    public object? Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not ChatState state) return DependencyProperty.UnsetValue;

        // Token names verified against PiaTokens.{Dark,Light}.xaml. Error has its own
        // soft fill (DangerSoftBrush) so it reads as a distinct error rather than sharing
        // WaitingForTool's amber WarnSoftBrush.
        var (bgKey, fgKey) = state switch
        {
            ChatState.Running        => ("PiaAccentSoftBrush", "PiaAccentBrush"),
            ChatState.WaitingForTool => ("WarnSoftBrush",       "WarnBrush"),
            ChatState.Completed      => ("SuccessSoftBrush",    "PiaSuccessBrush"),
            ChatState.Error          => ("DangerSoftBrush",     "PiaDangerBrush"),
            _                        => ("SurfaceMutedBrush",   "TextMutedBrush"),
        };

        var key = Kind == ChatStateBrushKind.Background ? bgKey : fgKey;
        return Application.Current?.TryFindResource(key) as Brush ?? DependencyProperty.UnsetValue;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Maps a <see cref="ChatState"/> to a WPF-UI <see cref="SymbolRegular"/> glyph.</summary>
public class ChatStateToGlyphConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not ChatState state) return SymbolRegular.Circle24;

        return state switch
        {
            ChatState.Running        => SymbolRegular.ArrowSync24,
            ChatState.WaitingForTool => SymbolRegular.HandRight24,
            ChatState.Completed      => SymbolRegular.CheckmarkCircle24,
            ChatState.Error          => SymbolRegular.ErrorCircle24,
            _                        => SymbolRegular.Circle24,
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Maps a <see cref="ChatState"/> to a localized label via <c>ChatState_&lt;Name&gt;</c> keys.</summary>
public class ChatStateToLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not ChatState state) return string.Empty;
        return LocalizationSource.Instance[$"ChatState_{state}"];
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>True (and Visible, via the bool→visibility chain) when the state is anything other than Idle.</summary>
public class ChatStateIsBadgeVisibleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        var visible = value is ChatState and not ChatState.Idle;
        return targetType == typeof(Visibility)
            ? (visible ? Visibility.Visible : Visibility.Collapsed)
            : visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
