using System.Globalization;
using System.Windows.Data;
using Pia.Models;

namespace Pia.Converters;

/// <summary>
/// Multi-binding: <c>{Speaker, SpeakerLabel}</c> → display name.
/// <see cref="TranscriptSpeaker.You"/> always renders as "you". For the counterpart side, a
/// non-null per-utterance <c>SpeakerLabel</c> (set by live diarization or by user rename)
/// wins; otherwise we fall back to a generic "Speaker" label.
/// </summary>
public sealed class SpeakerToDisplayNameConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 1) return string.Empty;
        var speaker = values[0] is TranscriptSpeaker s ? s : TranscriptSpeaker.You;
        var label = values.Length >= 2 ? values[1] as string : null;
        return Resolve(speaker, label);
        return string.IsNullOrWhiteSpace(counterpart) ? "them" : counterpart;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)

    /// <summary>
    /// Maps a speaker to the same display label the UI shows. Reused by the Markdown
    /// transcript export so both surfaces stay in sync.
    /// </summary>
    public static string Resolve(TranscriptSpeaker speaker, string? speakerLabel = null)
    {
        if (speaker == TranscriptSpeaker.You) return "you";
        if (!string.IsNullOrWhiteSpace(speakerLabel)) return speakerLabel!;
        return "Speaker";
    }
        => throw new NotSupportedException();
}
