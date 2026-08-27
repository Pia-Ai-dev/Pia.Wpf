using System.Text.RegularExpressions;

namespace Pia.Services.LiveTranscription;

/// <summary>
/// Renumbers auto-generated speaker labels 1..k by first appearance. <c>Speaker 17</c> for the fourth
/// voice is the identification service's mint counter leaking out; a user-renamed label carries a real
/// name and passes through untouched. Stateful — one instance per transcript.
/// </summary>
public sealed partial class SpeakerDisplayNumbering
{
    [GeneratedRegex(@"^Speaker \d+$")]
    private static partial Regex AutoLabel();

    private readonly Dictionary<string, int> _numberByLabel = new(StringComparer.Ordinal);

    /// <summary>Drops the numbering so a stale label cannot keep a number and leave a gap behind it.</summary>
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
