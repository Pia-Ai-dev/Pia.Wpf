using System.Globalization;
using System.Windows.Data;
using Pia.Models.Flow;
using Wpf.Ui.Controls;

namespace Pia.Converters;

/// <summary>Maps a <see cref="FlowSource"/> to a monochrome source glyph for the calm/minimal card (design §4).</summary>
public class FlowSourceToSymbolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        FlowSource.BackgroundChat => SymbolRegular.Chat24,
        FlowSource.Reminder => SymbolRegular.Alert24,
        FlowSource.ScheduledJob => SymbolRegular.Search24,
        FlowSource.TodoDeadline => SymbolRegular.TaskListSquareLtr24,
        FlowSource.AgentRun => SymbolRegular.Bot24,
        FlowSource.Assignment => SymbolRegular.Rocket24,
        _ => SymbolRegular.Info24, // Snackbar / InAppToast
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
