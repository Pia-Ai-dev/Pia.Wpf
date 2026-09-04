using System.Text.RegularExpressions;

namespace Pia.Services.LiveTranscription;

/// <summary>
/// Renumbers auto-generated speaker labels by first appearance. <c>Speaker 17</c> for the fourth voice
/// is the identification service's mint counter leaking out; a user-renamed label carries a real name
/// and passes through untouched. A number is assigned once and never re-derived or reused, so a label
/// the diarizer drops leaves a gap rather than shifting everyone after it down. Stateful — one
/// instance per transcript.
/// </summary>
public sealed partial class SpeakerDisplayNumbering
{
    [GeneratedRegex(@"^Speaker \d+$")]
    private static partial Regex AutoLabel();

    private readonly Dictionary<string, int> _numberByLabel = new(StringComparer.Ordinal);

    /// <summary>Starts a fresh transcript. Never call this on a rebuild — that is what makes it sticky.</summary>
    public void Reset() => _numberByLabel.Clear();

    public string? Resolve(string? speakerLabel, bool suppressLabels)
    {
        if (string.IsNullOrWhiteSpace(speakerLabel)) return speakerLabel;
        if (suppressLabels) return null;
        if (!AutoLabel().IsMatch(speakerLabel)) return speakerLabel;

        if (!_numberByLabel.TryGetValue(speakerLabel, out var number))
        {
            number = _numberByLabel.Count + 1;
            _numberByLabel[speakerLabel] = number;
        }
        return $"Speaker {number}";
    }
}
