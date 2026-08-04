using Pia.Models;

namespace Pia.Services.LiveTranscription;

/// <summary>
/// Pure aggregation of <see cref="VoiceSample"/>s into per-speaker <see cref="SpeakerVoiceStats"/>.
/// No I/O, no logging (samples carry no text, so there is nothing sensitive to guard here), and
/// deterministic ordering — the output is rendered directly into a saved transcript.
/// </summary>
public static class VoiceStatsCalculator
{
    public static IReadOnlyList<SpeakerVoiceStats> Compute(IEnumerable<VoiceSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);

        // Group by (Speaker, SpeakerLabel) using ordinal string comparison; null and empty
        // labels are treated as the same "no label" group.
        var groups = new Dictionary<(TranscriptSpeaker Speaker, string? Label), (int Count, double Total)>();
        double grandTotal = 0;

        foreach (var sample in samples)
        {
            var duration = Math.Max(0, sample.DurationSeconds); // defensive: a caller must never
                                                                 // observe negative speech time.
            var label = string.IsNullOrEmpty(sample.SpeakerLabel) ? null : sample.SpeakerLabel;
            var key = (sample.Speaker, label);

            if (groups.TryGetValue(key, out var existing))
                groups[key] = (existing.Count + 1, existing.Total + duration);
            else
                groups[key] = (1, duration);

            grandTotal += duration;
        }

        if (groups.Count == 0) return Array.Empty<SpeakerVoiceStats>();

        var result = new List<SpeakerVoiceStats>(groups.Count);
        foreach (var (key, agg) in groups)
        {
            var mean = agg.Count == 0 ? 0 : agg.Total / agg.Count;
            var share = grandTotal == 0 ? 0 : agg.Total / grandTotal;
            result.Add(new SpeakerVoiceStats(key.Speaker, key.Label, agg.Count, agg.Total, mean, share));
        }

        // Deterministic order: total speech desc, then label ordinal asc, then speaker —
        // this is rendered into a saved file, so re-running Compute on the same input must
        // always emit the same order.
        result.Sort((a, b) =>
        {
            var byTotal = b.TotalSpeechSeconds.CompareTo(a.TotalSpeechSeconds);
            if (byTotal != 0) return byTotal;

            var byLabel = string.CompareOrdinal(a.SpeakerLabel ?? string.Empty, b.SpeakerLabel ?? string.Empty);
            if (byLabel != 0) return byLabel;

            return ((int)a.Speaker).CompareTo((int)b.Speaker);
        });

        return result;
    }
}
