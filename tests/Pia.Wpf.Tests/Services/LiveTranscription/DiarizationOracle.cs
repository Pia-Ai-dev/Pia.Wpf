using System.IO;
using System.Text.Json;
using Pia.Services.LiveTranscription;

namespace Pia.Tests.Services.LiveTranscription;

internal readonly record struct RefInterval(double Start, double End, string[] Speakers);
internal readonly record struct RefRange(double Start, double End);

internal sealed record SpeakerReference(
    double DurationSeconds, string[] Speakers, RefInterval[] Intervals, RefRange[] InvalidRanges);

/// <summary>One segment the reference could attribute to exactly one person.</summary>
internal sealed record LabelledSegment(string Speaker, float[] Embedding, double DurationSeconds);

internal sealed record SimilarityStats(
    double IntraMean, double IntraStdDev, double InterMean, double InterStdDev,
    double BestThreshold, double PairErrorRate, long IntraPairs, long InterPairs)
{
    /// <summary>Separation of the two similarity distributions in pooled standard deviations.</summary>
    public double DPrime =>
        (IntraMean - InterMean) / Math.Sqrt((IntraStdDev * IntraStdDev + InterStdDev * InterStdDev) / 2.0);
}

/// <summary>One true speaker's share of an oracle run. Zero <see cref="Scored"/> means the enrollment
/// budget swallowed every segment they had, so the pooled figure says nothing at all about them.</summary>
internal sealed record SpeakerTally(
    string Speaker, double EnrolledSeconds, int Scored, int Correct, double ScoredSeconds)
{
    public double BySegment => Scored == 0 ? 0 : (double)Correct / Scored;
}

internal sealed record OracleResult(
    int Correct, int Total, double CorrectSeconds, double TotalSeconds, SpeakerTally[]? PerSpeaker = null)
{
    public double BySegment => Total == 0 ? 0 : (double)Correct / Total;
    public double ByDuration => TotalSeconds <= 0 ? 0 : CorrectSeconds / TotalSeconds;
}

/// <summary>
/// What the current embedding model can do on this recording with the answer key in hand. It bounds
/// every clustering policy: if perfect enrollment scores no better than the live run, the accuracy
/// ceiling is the embedding, and tuning the matcher cannot reach it.
/// </summary>
internal static class DiarizationOracle
{
    public static SpeakerReference LoadReference(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;

        var speakers = root.GetProperty("speakers").EnumerateArray().Select(e => e.GetString()!).ToArray();
        var intervals = root.GetProperty("intervals").EnumerateArray().Select(e => new RefInterval(
            e.GetProperty("start").GetDouble(),
            e.GetProperty("end").GetDouble(),
            e.GetProperty("speakers").EnumerateArray().Select(s => s.GetString()!).ToArray())).ToArray();

        var invalid = root.TryGetProperty("invalidRanges", out var ranges)
            ? ranges.EnumerateArray()
                .Select(e => new RefRange(e.GetProperty("start").GetDouble(), e.GetProperty("end").GetDouble()))
                .ToArray()
            : [];

        return new SpeakerReference(root.GetProperty("durationSeconds").GetDouble(), speakers, intervals, invalid);
    }

    /// <summary>The single speaker the reference puts at <paramref name="seconds"/>, or null where it
    /// says nothing, says two people at once, or was unreadable — the buckets that can neither flatter
    /// nor damn a result.</summary>
    public static string? TruthAt(SpeakerReference reference, double seconds)
    {
        foreach (var range in reference.InvalidRanges)
            if (seconds >= range.Start && seconds < range.End) return null;

        foreach (var interval in reference.Intervals)
            if (seconds >= interval.Start && seconds < interval.End)
                return interval.Speakers.Length == 1 ? interval.Speakers[0] : null;

        return null;
    }

    public static SimilarityStats Similarity(IReadOnlyList<LabelledSegment> segments)
    {
        var intra = new List<double>();
        var inter = new List<double>();
        var vectors = segments.Select(s => Normalize(s.Embedding)).ToArray();

        for (int i = 0; i < segments.Count; i++)
        {
            for (int j = i + 1; j < segments.Count; j++)
            {
                var similarity = Dot(vectors[i], vectors[j]);
                if (string.Equals(segments[i].Speaker, segments[j].Speaker, StringComparison.Ordinal))
                    intra.Add(similarity);
                else
                    inter.Add(similarity);
            }
        }

        var (intraMean, intraSd) = MeanAndStdDev(intra);
        var (interMean, interSd) = MeanAndStdDev(inter);
        var (threshold, errorRate) = BestSplit(intra, inter);
        return new SimilarityStats(
            intraMean, intraSd, interMean, interSd, threshold, errorRate, intra.Count, inter.Count);
    }

    /// <summary>Enrolls each speaker from their earliest <paramref name="enrollSeconds"/> of speech and
    /// classifies everything after by nearest centroid. Segments used for enrollment are excluded from
    /// the score, so it never marks its own homework.</summary>
    public static OracleResult NearestCentroid(IReadOnlyList<LabelledSegment> segments, double enrollSeconds)
    {
        var enrolledSeconds = new Dictionary<string, double>(StringComparer.Ordinal);
        var sums = new Dictionary<string, float[]>(StringComparer.Ordinal);
        var scored = new List<LabelledSegment>();

        foreach (var segment in segments)
        {
            enrolledSeconds.TryGetValue(segment.Speaker, out var already);
            if (already < enrollSeconds)
            {
                enrolledSeconds[segment.Speaker] = already + segment.DurationSeconds;
                var vector = Normalize(segment.Embedding);
                if (!sums.TryGetValue(segment.Speaker, out var sum)) sums[segment.Speaker] = (float[])vector.Clone();
                else for (int d = 0; d < sum.Length; d++) sum[d] += vector[d];
                continue;
            }
            scored.Add(segment);
        }

        var centroids = sums.ToDictionary(kv => kv.Key, kv => Normalize(kv.Value), StringComparer.Ordinal);
        int correct = 0;
        double correctSeconds = 0, totalSeconds = 0;
        var scoredCount = new Dictionary<string, int>(StringComparer.Ordinal);
        var correctCount = new Dictionary<string, int>(StringComparer.Ordinal);
        var scoredSeconds = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var segment in scored)
        {
            totalSeconds += segment.DurationSeconds;
            scoredCount[segment.Speaker] = scoredCount.GetValueOrDefault(segment.Speaker) + 1;
            scoredSeconds[segment.Speaker] =
                scoredSeconds.GetValueOrDefault(segment.Speaker) + segment.DurationSeconds;
            var vector = Normalize(segment.Embedding);
            string? best = null;
            var bestSimilarity = double.NegativeInfinity;
            foreach (var (speaker, centroid) in centroids)
            {
                var similarity = Dot(vector, centroid);
                if (similarity > bestSimilarity) { bestSimilarity = similarity; best = speaker; }
            }
            if (string.Equals(best, segment.Speaker, StringComparison.Ordinal))
            {
                correct++;
                correctSeconds += segment.DurationSeconds;
                correctCount[segment.Speaker] = correctCount.GetValueOrDefault(segment.Speaker) + 1;
            }
        }

        var tallies = enrolledSeconds.Keys
            .OrderByDescending(s => scoredSeconds.GetValueOrDefault(s) + enrolledSeconds[s])
            .Select(s => new SpeakerTally(
                s, enrolledSeconds[s], scoredCount.GetValueOrDefault(s),
                correctCount.GetValueOrDefault(s), scoredSeconds.GetValueOrDefault(s)))
            .ToArray();
        return new OracleResult(correct, scored.Count, correctSeconds, totalSeconds, tallies);
    }

    /// <summary>The production clusterer with the talker count known, scored by the same greedy
    /// one-to-one cluster→speaker assignment the metric script uses — so it is the best case for the
    /// clusterer, and a low number cannot be blamed on the pairing.</summary>
    public static OracleResult PinnedClusterer(IReadOnlyList<LabelledSegment> segments, int expectedSpeakers)
    {
        if (segments.Count == 0) return new OracleResult(0, 0, 0, 0);

        var embeddings = segments.Select(s => Normalize(s.Embedding)).ToArray();
        var result = new SpeakerClusterer().Cluster(embeddings, previousClusterCount: 0, expectedSpeakers);

        var cells = new Dictionary<(int Cluster, string Speaker), (int Count, double Seconds)>();
        for (int i = 0; i < segments.Count; i++)
        {
            var key = (result.AssignmentPerSegment[i], segments[i].Speaker);
            cells.TryGetValue(key, out var cell);
            cells[key] = (cell.Count + 1, cell.Seconds + segments[i].DurationSeconds);
        }

        var takenCluster = new HashSet<int>();
        var takenSpeaker = new HashSet<string>(StringComparer.Ordinal);
        int correct = 0;
        double correctSeconds = 0;
        foreach (var cell in cells.OrderByDescending(c => c.Value.Seconds))
        {
            if (!takenCluster.Add(cell.Key.Cluster)) continue;
            if (!takenSpeaker.Add(cell.Key.Speaker)) { takenCluster.Remove(cell.Key.Cluster); continue; }
            correct += cell.Value.Count;
            correctSeconds += cell.Value.Seconds;
        }

        return new OracleResult(correct, segments.Count, correctSeconds, segments.Sum(s => s.DurationSeconds));
    }

    /// <summary>The fixed similarity threshold that minimises same/different pair-decision errors, and
    /// the error rate there. It is the best any single threshold can do on this audio.</summary>
    private static (double Threshold, double ErrorRate) BestSplit(List<double> intra, List<double> inter)
    {
        if (intra.Count == 0 || inter.Count == 0) return (0, 0);

        var best = (Threshold: 0.0, ErrorRate: double.MaxValue);
        for (int step = 0; step <= 200; step++)
        {
            var threshold = step / 200.0;
            var missed = intra.Count(v => v < threshold) / (double)intra.Count;
            var confused = inter.Count(v => v >= threshold) / (double)inter.Count;
            var rate = (missed + confused) / 2.0;
            if (rate < best.ErrorRate) best = (threshold, rate);
        }
        return best;
    }

    private static (double Mean, double StdDev) MeanAndStdDev(List<double> values)
    {
        if (values.Count == 0) return (0, 0);
        var mean = values.Average();
        var variance = values.Sum(v => (v - mean) * (v - mean)) / values.Count;
        return (mean, Math.Sqrt(variance));
    }

    private static float[] Normalize(float[] vector)
    {
        double sum = 0;
        foreach (var v in vector) sum += v * v;
        var norm = Math.Sqrt(sum);
        if (norm <= 1e-9) return (float[])vector.Clone();
        var result = new float[vector.Length];
        for (int i = 0; i < vector.Length; i++) result[i] = (float)(vector[i] / norm);
        return result;
    }

    private static double Dot(float[] a, float[] b)
    {
        double sum = 0;
        for (int i = 0; i < a.Length; i++) sum += a[i] * b[i];
        return sum;
    }
}
