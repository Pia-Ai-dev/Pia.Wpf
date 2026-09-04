using System.Text.RegularExpressions;

namespace Pia.Services.LiveTranscription;

/// <summary>
/// Renumbers the mint counter's <c>Speaker 17</c> by first appearance; a renamed label passes through.
/// A number is assigned once and never reused, so a dropped label leaves a gap instead of shifting
/// everyone after it down. One instance per transcript.
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
