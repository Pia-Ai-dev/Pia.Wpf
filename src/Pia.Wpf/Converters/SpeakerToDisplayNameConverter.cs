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
        var speaker = values[0] is TranscriptSpeaker s ? s : TranscriptSpeaker.You;
        return Resolve(speaker, values[1] as string);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    /// <summary>
    /// Maps a speaker to the same display label the UI shows. Reused by the Markdown
    /// transcript export so both surfaces stay in sync.
    /// </summary>
    public static string Resolve(TranscriptSpeaker speaker, string? counterpartName)
    {
        if (speaker == TranscriptSpeaker.You) return "you";
        return string.IsNullOrWhiteSpace(counterpartName) ? "them" : counterpartName!;
    }
}
