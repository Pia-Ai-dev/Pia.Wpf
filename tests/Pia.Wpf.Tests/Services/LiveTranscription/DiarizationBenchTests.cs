using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Pia.Services.LiveTranscription;
using Pia.Tests.TestInfrastructure;
using Pia.ViewModels;
using Xunit;

namespace Pia.Tests.Services.LiveTranscription;

/// <summary>
/// Entry point for the diarization bench. Explicit, so it never runs in the gate.
///
/// <code>
/// $env:PIA_BENCH_WAV       = 'artifacts\wav\lsp-replay.wav'   # teed by the replay, 16 kHz mono
/// $env:PIA_BENCH_ROSTER    = '5'
/// $env:PIA_BENCH_REFERENCE = 'scripts\speaker-reference\lsp.reference.json'   # optional
/// $env:PIA_BENCH_MATCH     = '0.30,0.345'                      # optional, fixed thresholds to sweep
/// dotnet test -- --explicit only --filter-method "*Bench_MeasuresARecording*"
/// </code>
/// </summary>
public class DiarizationBenchTests
{
    private static readonly Regex PassLine = new(
        @"Adaptive pass: \d+/\d+ segments . (\d+) clusters cut=([\d.,]+) expected=\d+ changed=\d+ match=([\d.,]+)",
        RegexOptions.Compiled);

    [BenchFact]
    public void Bench_MeasuresARecording()
    {
        var wav = Environment.GetEnvironmentVariable("PIA_BENCH_WAV");
        if (string.IsNullOrWhiteSpace(wav) || !File.Exists(wav))
            Assert.Skip("Set PIA_BENCH_WAV to a 16 kHz mono WAV teed from a replay.");

        var outDir = Environment.GetEnvironmentVariable("PIA_BENCH_OUT")
            ?? Path.Combine(Path.GetDirectoryName(wav)!, "bench");
        var roster = int.TryParse(
            Environment.GetEnvironmentVariable("PIA_BENCH_ROSTER"), CultureInfo.InvariantCulture, out var r) ? r : 0;
        var modelPath = Environment.GetEnvironmentVariable("PIA_BENCH_MODEL")
            ?? LiveTranscriptionModels.SpeakerEmbeddingModelPath;
        var referencePath = Environment.GetEnvironmentVariable("PIA_BENCH_REFERENCE");
        var settings = ParseThresholds(Environment.GetEnvironmentVariable("PIA_BENCH_MATCH"));

        Directory.CreateDirectory(outDir);
        var name = Path.GetFileNameWithoutExtension(wav);
        var cachePath = Path.Combine(outDir, $"{name}.embeddings.bin");
        var report = new StringBuilder();

        void Say(string line)
        {
            report.AppendLine(line);
            TestContext.Current.SendDiagnosticMessage(line);
        }

        var started = Environment.TickCount64;
        var segments = DiarizationBench.Segment(wav);
        var segmentedAt = Environment.TickCount64;
        Say($"wav        : {wav}");
        Say($"segments   : {segments.Count} closed, "
            + $"{segments.Count(s => s.SampleCount >= 16000 * 3 / 2)} above the 1.5 s diarization gate");
        Say($"audio      : {(segments.Count > 0 ? segments[^1].StartSeconds + segments[^1].DurationSeconds : 0):F1} s "
            + $"covered, {segments.Sum(s => s.DurationSeconds):F1} s of speech");
        Say($"settings   : {settings.Count} — {string.Join(", ", settings.Select(s => s.Tag))}");

        var cache = EmbeddingCache.Load(cachePath);
        using var extractor = new CachedEmbeddingExtractor(
            cache, () => new SherpaEmbeddingExtractor(modelPath));

        var written = new List<string>();
        var cutTraces = new Dictionary<string, string>(StringComparer.Ordinal);
        var misses = -1;
        foreach (var (tag, options) in settings)
        {
            var policyStarted = Environment.TickCount64;
            var passLines = DiarizationBench.Identify(segments, roster, extractor, options);
            var elapsed = (Environment.TickCount64 - policyStarted) / 1000.0;
            if (cache.Dirty) cache.Save(cachePath);
            if (misses < 0) misses = extractor.Misses;

            var segmentsPath = Path.Combine(outDir, $"{name}.{tag}.segments.jsonl");
            DiarizationBench.WriteSegments(segmentsPath, segments);
            File.WriteAllLines(Path.Combine(outDir, $"{name}.{tag}.passes.log"), passLines);
            written.Add(segmentsPath);

            var rendered = DiarizationBench.RenderedLabels(segments);
            var renderedPath = Path.Combine(outDir, $"{name}.{tag}.rendered.segments.jsonl");
            DiarizationBench.WriteSegments(renderedPath, segments, s => rendered[s]);
            written.Add(renderedPath);

            var passes = passLines.Select(l => PassLine.Match(l)).Where(m => m.Success).ToList();
            var thresholds = passes.Select(m => Number(m.Groups[3].Value)).ToList();
            // A parse miss would report a threshold trace of zeros, which reads like a stuck policy.
            Assert.Equal(
                passLines.Count(l => l.StartsWith("Adaptive pass:", StringComparison.Ordinal)), passes.Count);
            cutTraces[tag] = string.Join(",", passes.Select(
                m => Number(m.Groups[2].Value).ToString("F2", CultureInfo.InvariantCulture)));

            var gated = segments.Where(s => s.SampleCount >= 16000 * 3 / 2).ToList();
            Say(string.Empty);
            Say($"SETTING {tag}  ({elapsed:F1} s)");
            Say($"  passes   : {passes.Count}, clusters {string.Join(",", passes.Select(m => m.Groups[1].Value))}");
            Say($"  match    : {Describe(thresholds)}, "
                + $"{Rail(thresholds, options.MatchSimilarityMin)} passes on the low rail, "
                + $"{Rail(thresholds, options.MatchSimilarityMax)} on the high rail");
            Say($"  labels   : {gated.Select(s => s.Label).Where(l => l is not null).Distinct().Count()} ever minted, "
                + $"{gated.Select(s => s.FinalLabel).Where(l => l is not null).Distinct().Count()} in the final state, "
                + $"{gated.Count(s => s.FinalLabel is null)} unlabelled");
            Say($"  corrected: {gated.Count(s => s.Label != s.FinalLabel)} segments whose final label "
                + "differs from the one shown live");
            var inherited = segments.Count(s => s.FinalLabel is null && rendered[s] is not null);
            Say($"  rendered : {inherited} unlabelled segments inherit the previous bubble's speaker, "
                + $"{segments.Count(s => rendered[s] is null)} render unattributed");
        }

        Assert.Equal(written.Count, written.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(written, p => Assert.True(File.Exists(p), $"{p} was not written"));

        // The clusterer never sees a label or the match threshold, so an identical cut trace across
        // settings is the evidence that matching cannot feed back into clustering.
        if (cutTraces.Count > 1)
        {
            var distinct = cutTraces.Values.Distinct(StringComparer.Ordinal).Count();
            Say(string.Empty);
            Say(distinct == 1
                ? $"cut trace  : identical across all {cutTraces.Count} settings, so clustering is independent of the threshold"
                : $"cut trace  : {distinct} distinct traces across {cutTraces.Count} settings, so clustering is NOT independent");
        }

        // The service computes embeddings internally; the cache is the only place they surface, and
        // the oracle needs them.
        foreach (var segment in segments)
            if (cache.TryGet(segment.StartSample, segment.SampleCount, out var vector))
                segment.Embedding = vector;

        Say($"embeddings : {cache.Count} cached, {misses} computed this run");
        Say($"timing     : {(segmentedAt - started) / 1000.0:F1} s segmenting");

        if (!string.IsNullOrWhiteSpace(referencePath) && File.Exists(referencePath))
            AppendOracle(Say, segments, referencePath);

        File.WriteAllText(Path.Combine(outDir, $"{name}.report.txt"), report.ToString());
        Assert.NotEmpty(segments);
    }

    private static float Number(string raw)
        => float.Parse(raw.Replace(',', '.'), CultureInfo.InvariantCulture);

    private static int Rail(List<float> values, float rail) => values.Count(v => Math.Abs(v - rail) < 0.0005f);

    private static string Describe(List<float> values)
    {
        if (values.Count == 0) return "no passes";
        var mean = values.Average();
        var sd = values.Count > 1
            ? Math.Sqrt(values.Sum(v => (v - mean) * (double)(v - mean)) / (values.Count - 1))
            : 0;
        return string.Format(
            CultureInfo.InvariantCulture,
            "min {0:F3} max {1:F3} mean {2:F3} sd {3:F3}", values.Min(), values.Max(), mean, sd);
    }

    /// <summary>
    /// Comma-separated fixed thresholds to hold the instant match at, one bench run each; unset means
    /// one run of the shipping derivation. Malformed throws rather than skips, because a sweep that
    /// silently shrinks reads like a result.
    /// </summary>
    private static List<(string Tag, AdaptiveSpeakerOptions Options)> ParseThresholds(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec))
            return [("derived", AdaptiveSpeakerOptions.Default)];

        var parsed = new List<(string, AdaptiveSpeakerOptions)>();
        foreach (var entry in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!float.TryParse(entry, CultureInfo.InvariantCulture, out var threshold))
                throw new ArgumentException($"PIA_BENCH_MATCH: {entry} is not a similarity threshold.");
            parsed.Add(($"fixed-{entry}", new AdaptiveSpeakerOptions { FixedMatchSimilarity = threshold }));
        }
        return parsed;
    }

    private static void AppendOracle(
        Action<string> say, List<BenchSegment> segments, string referencePath)
    {
        var reference = DiarizationOracle.LoadReference(referencePath);
        var truthful = new List<LabelledSegment>();
        foreach (var segment in segments)
        {
            if (segment.Embedding is null) continue;
            var who = DiarizationOracle.TruthAt(reference, segment.MidSeconds);
            if (who is null) continue;
            truthful.Add(new LabelledSegment(who, segment.Embedding, segment.DurationSeconds));
        }

        say(string.Empty);
        say($"reference  : {reference.Speakers.Length} talkers, {reference.DurationSeconds:F1} s");
        say($"scoreable  : {truthful.Count} segments the reference attributes to exactly one person");
        if (truthful.Count == 0) return;

        var similarity = DiarizationOracle.Similarity(truthful);
        say($"embedding  : intra {similarity.IntraMean:F3} ± {similarity.IntraStdDev:F3}, "
            + $"inter {similarity.InterMean:F3} ± {similarity.InterStdDev:F3}, d' {similarity.DPrime:F2}");
        say($"best fixed threshold {similarity.BestThreshold:F3}, pair-decision error {similarity.PairErrorRate:P1}");
        AppendInheritance(say, segments, reference);
        AppendPairSeparation(say, truthful, reference.Speakers);

        var enrollSeconds = double.TryParse(
            Environment.GetEnvironmentVariable("PIA_BENCH_ENROLL"), CultureInfo.InvariantCulture, out var e)
            ? e : 30;
        var enrolled = DiarizationOracle.NearestCentroid(truthful, enrollSeconds);
        say($"ORACLE enrollment ({enrollSeconds:F0} s/speaker): {enrolled.BySegment:P1} by segment, "
            + $"{enrolled.ByDuration:P1} by duration, over {enrolled.Total} segments");
        foreach (var tally in enrolled.PerSpeaker ?? [])
            say($"  {tally.Speaker,-4} enrolled {tally.EnrolledSeconds,6:F1} s | scored {tally.Scored,4} seg "
                + $"{tally.ScoredSeconds,7:F1} s | "
                + (tally.Scored == 0 ? "untested: enrollment took every segment" : $"{tally.BySegment:P1}"));

        // The true talker count, not the roster: this separates "inferring k is the problem" from
        // "matching is the problem", and on a mostly-muted recording the roster is neither.
        var pinned = DiarizationOracle.PinnedClusterer(truthful, reference.Speakers.Length);
        say($"ORACLE clusterer (k = {reference.Speakers.Length} true talkers): {pinned.BySegment:P1} by segment, "
            + $"{pinned.ByDuration:P1} by duration");
    }

    /// <summary>
    /// Scores the bubble inheritance the attribution metric structurally cannot see: an unlabelled
    /// segment leaves the metric's denominator but still renders under the previous speaker's name.
    /// Needs the true label the inherited one stands in for, so it maps each cluster label to the
    /// speaker it covers most, exactly as the scorer's confusion matrix does.
    /// </summary>
    private static void AppendInheritance(
        Action<string> say, List<BenchSegment> segments, SpeakerReference reference)
    {
        say(string.Empty);
        say($"AS RENDERED: the bubble window is wall clock in the app and stream time here, so the "
            + "shorter window bounds the modelling gap on a replay");
        // 10 s is well under the ~21 s of stream the app's 25 s covers at the fixture's 0.83x replay
        // rate; agreement across the two is what makes this independent of the approximation.
        foreach (var window in new[] { (double)TranscriptOverlayViewModel.BubbleWindowSeconds, 10d })
            AppendInheritanceAt(say, segments, reference, window);
    }

    private static void AppendInheritanceAt(
        Action<string> say, List<BenchSegment> segments, SpeakerReference reference, double window)
    {
        var rendered = DiarizationBench.RenderedLabels(segments, window);
        var inherited = segments.Where(s => s.FinalLabel is null && rendered[s] is not null).ToList();
        say($"  window {window:F0} s: {inherited.Count} of {segments.Count} segments carry no label of "
            + "their own and inherit the previous bubble's speaker");
        if (inherited.Count == 0) return;

        // Greedy label → speaker, most seconds first, one speaker per label.
        var seconds = new Dictionary<(string Label, string Speaker), double>();
        foreach (var s in segments)
        {
            if (s.FinalLabel is null) continue;
            if (DiarizationOracle.TruthAt(reference, s.MidSeconds) is not { } who) continue;
            var key = (s.FinalLabel, who);
            seconds[key] = seconds.GetValueOrDefault(key) + s.DurationSeconds;
        }
        var speakerByLabel = new Dictionary<string, string>(StringComparer.Ordinal);
        var taken = new HashSet<string>(StringComparer.Ordinal);
        foreach (var ((label, who), _) in seconds.OrderByDescending(kv => kv.Value))
        {
            if (speakerByLabel.ContainsKey(label) || taken.Contains(who)) continue;
            speakerByLabel[label] = who;
            taken.Add(who);
        }

        int right = 0, wrong = 0, unscoreable = 0;
        double rightSeconds = 0, wrongSeconds = 0;
        foreach (var s in inherited)
        {
            var who = DiarizationOracle.TruthAt(reference, s.MidSeconds);
            if (who is null || !speakerByLabel.TryGetValue(rendered[s]!, out var claimed))
            {
                unscoreable++;
                continue;
            }
            if (claimed == who) { right++; rightSeconds += s.DurationSeconds; }
            else { wrong++; wrongSeconds += s.DurationSeconds; }
        }
        var scored = right + wrong;
        say($"    scoreable {scored}: {(scored == 0 ? 0 : (double)right / scored):P1} inherit the right "
            + $"speaker ({right} right / {wrong} wrong, {rightSeconds:F1} s / {wrongSeconds:F1} s), "
            + $"{unscoreable} unscoreable. A wrong one is an attribution error the label metric never "
            + "counts — it leaves the denominator and still appears in the transcript.");
    }

    private static void AppendPairSeparation(
        Action<string> say, List<LabelledSegment> truthful, string[] speakers)
    {
        var rows = DiarizationOracle.PerPair(truthful);
        var present = speakers.Where(s => rows.Any(r => r.A == s || r.B == s)).ToArray();
        if (present.Length < 2) return;

        double? At(string a, string b) => rows
            .FirstOrDefault(r => (r.A == a && r.B == b) || (r.A == b && r.B == a))?.Mean;

        say("separation : mean cosine, diagonal is a speaker against themselves");
        say("       " + string.Concat(present.Select(s => $"{s,8}")));
        foreach (var a in present)
            say($"  {a,-5}" + string.Concat(present.Select(b =>
                At(a, b) is { } m ? $"{m,8:F3}" : $"{".",8}")));

        // The gap between a pair's cross-similarity and the tighter speaker's self-similarity is the
        // room a threshold has to fit into; at zero no threshold separates them at all. Ranked on
        // that rather than on raw cross-similarity, which two of the three fixtures tie on.
        var worst = present
            .SelectMany(a => present, (a, b) => (a, b))
            .Where(p => string.CompareOrdinal(p.a, p.b) < 0)
            .Select(p => (p.a, p.b, Cross: At(p.a, p.b), Self: Math.Min(At(p.a, p.a) ?? 0, At(p.b, p.b) ?? 0)))
            .Where(p => p.Cross.HasValue)
            .OrderBy(p => p.Self - p.Cross!.Value)
            .FirstOrDefault();
        if (!worst.Cross.HasValue) return;
        say($"closest pair {worst.a}/{worst.b} at {worst.Cross.Value:F3} against self-similarity "
            + $"{worst.Self:F3} — margin {worst.Self - worst.Cross.Value:F3}");

        // Hand the production clusterer only that pair's segments and pin k=2. This separates "the
        // signal is not there" from "the global problem swamps it": a high score here means the
        // embedding and the linkage can both tell these two apart, and only the policy cannot.
        var isolated = truthful.Where(s => s.Speaker == worst.a || s.Speaker == worst.b).ToList();
        var pinned = DiarizationOracle.PinnedClusterer(isolated, 2);
        say($"  isolated to just {worst.a}+{worst.b}, k=2: {pinned.BySegment:P1} by segment, "
            + $"{pinned.ByDuration:P1} by duration, over {pinned.Total} segments");
    }
}
