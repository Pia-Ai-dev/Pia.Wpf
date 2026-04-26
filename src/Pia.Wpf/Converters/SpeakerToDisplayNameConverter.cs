using System.Globalization;
using System.Windows.Data;
using Pia.Models;

namespace Pia.Converters;

/// <summary>
/// Multi-binding: <c>{Speaker, CounterpartName, SpeakerLabel}</c> → display name.
/// <see cref="TranscriptSpeaker.You"/> always renders as "you". For the counterpart side, a
/// non-null per-utterance <c>SpeakerLabel</c> (set by live diarization) wins; otherwise the
/// session-wide counterpart name is used, falling back to "them" when blank.
/// </summary>
public sealed class SpeakerToDisplayNameConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 2) return string.Empty;
        if (values[0] is TranscriptSpeaker.You) return "you";

        if (values.Length >= 3 && values[2] is string label && !string.IsNullOrWhiteSpace(label))
            return label;

        var counterpart = values[1] as string;
        return string.IsNullOrWhiteSpace(counterpart) ? "them" : counterpart;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
