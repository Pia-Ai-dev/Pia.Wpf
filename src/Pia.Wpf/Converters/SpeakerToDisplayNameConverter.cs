using System.Globalization;
using System.Windows.Data;
using Pia.Models;

namespace Pia.Converters;

/// <summary>
/// Multi-binding: <c>{Speaker, SpeakerLabel, CounterpartName}</c> → display name.
/// <see cref="TranscriptSpeaker.You"/> always renders as "you"; otherwise a non-blank per-speaker
/// diarizer label ("Speaker 1", …) wins, then the user-editable counterpart name, falling back to
/// "them" when both are blank.
/// </summary>
public sealed class SpeakerToDisplayNameConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 3) return string.Empty;
        var speaker = values[0] is TranscriptSpeaker s ? s : TranscriptSpeaker.You;
        return Resolve(speaker, values[1] as string, values[2] as string);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    /// <summary>
    /// Maps a speaker to the same display label the UI shows. Reused by the Markdown
    /// transcript export so both surfaces stay in sync. A non-blank <paramref name="speakerLabel"/>
    /// takes precedence over <paramref name="counterpartName"/>.
    /// </summary>
    public static string Resolve(TranscriptSpeaker speaker, string? speakerLabel, string? counterpartName)
    {
        if (speaker == TranscriptSpeaker.You) return "you";
        if (!string.IsNullOrWhiteSpace(speakerLabel)) return speakerLabel!;
        return string.IsNullOrWhiteSpace(counterpartName) ? "them" : counterpartName!;
    }
}
