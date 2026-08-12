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
        // labels are treated as the same "no label" group. Materialized because grandTotal
        // needs a second read and samples is only enumerable once.
        var byKey = samples
            .Select(s => (
                s.Speaker,
                Label: string.IsNullOrEmpty(s.SpeakerLabel) ? null : s.SpeakerLabel,
                // defensive: a caller must never observe negative speech time.
                Duration: Math.Max(0, s.DurationSeconds)))
            .GroupBy(x => (x.Speaker, x.Label))
            .Select(g => (g.Key, Count: g.Count(), Total: g.Sum(x => x.Duration)))
            .ToList();

        var grandTotal = byKey.Sum(g => g.Total);

        // Deterministic order: total speech desc, then label ordinal asc, then speaker —
        // this is rendered into a saved file, so re-running Compute on the same input must
        // always emit the same order.
        return byKey
            .Select(g => new SpeakerVoiceStats(
                g.Key.Speaker,
                g.Key.Label,
                g.Count,
                g.Total,
                g.Count == 0 ? 0 : g.Total / g.Count,
                grandTotal == 0 ? 0 : g.Total / grandTotal))
            .OrderByDescending(s => s.TotalSpeechSeconds)
            .ThenBy(s => s.SpeakerLabel ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(s => (int)s.Speaker)
            .ToList();
    }
}
