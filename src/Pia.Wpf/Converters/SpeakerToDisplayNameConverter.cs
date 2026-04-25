using System.Globalization;
using System.Windows.Data;
using Pia.Models;

namespace Pia.Converters;

/// <summary>
/// Multi-binding: <c>{Speaker, CounterpartName}</c> → display name. <see cref="TranscriptSpeaker.You"/>
/// always renders as "you"; the counterpart name is used otherwise, falling back to "them" when blank.
/// </summary>
public sealed class SpeakerToDisplayNameConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 2) return string.Empty;
        if (values[0] is TranscriptSpeaker.You) return "you";

        var counterpart = values[1] as string;
        return string.IsNullOrWhiteSpace(counterpart) ? "them" : counterpart;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
