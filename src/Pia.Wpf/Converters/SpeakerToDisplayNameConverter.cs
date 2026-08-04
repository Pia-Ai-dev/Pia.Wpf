using System.Globalization;
using System.Windows.Data;
using Pia.Localization;
using Pia.Models;

namespace Pia.Converters;

/// <summary>
/// Multi-binding: <c>{Speaker, SpeakerLabel, CounterpartName}</c> → display name.
/// <see cref="TranscriptSpeaker.You"/> always renders as the localized <c>Speaker_Me</c> resource
/// ("me" / "ich" / "moi"); otherwise a non-blank per-speaker diarizer label ("Speaker 1", …) wins, then
/// the user-editable counterpart name, falling back to the localized <c>Speaker_Them</c> resource when
/// both are blank.
/// </summary>
public sealed class SpeakerToDisplayNameConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 3) return string.Empty;
        // Any values[0] that isn't a TranscriptSpeaker (including a broken binding's
        // DependencyProperty.UnsetValue) falls through to You — so a broken binding renders the
        // localized "me", not an empty string. Intentional; see Resolve's caveat below.
        var speaker = values[0] is TranscriptSpeaker s ? s : TranscriptSpeaker.You;
        return Resolve(speaker, values[1] as string, values[2] as string);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    /// <summary>
    /// Maps a speaker to the same display label the UI shows. Reused by the Markdown
    /// transcript export so both surfaces stay in sync. A non-blank <paramref name="speakerLabel"/>
    /// takes precedence over <paramref name="counterpartName"/>.
    ///
    /// <para>This method is <c>static</c> and the converter itself is built by App.xaml with an
    /// implicit parameterless ctor, so <c>ILocalizationService</c> cannot be constructor-injected
    /// here — localization goes through <see cref="LocalizationSource.Instance"/> instead, the same
    /// precedent five other converters already use.</para>
    /// </summary>
    public static string Resolve(TranscriptSpeaker speaker, string? speakerLabel, string? counterpartName)
    {
        if (speaker == TranscriptSpeaker.You) return LocalizationSource.Instance["Speaker_Me"];
        if (!string.IsNullOrWhiteSpace(speakerLabel)) return speakerLabel!;
        return string.IsNullOrWhiteSpace(counterpartName) ? LocalizationSource.Instance["Speaker_Them"] : counterpartName!;
    }
}
