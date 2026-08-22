using System.Globalization;
using System.IO;
using System.Text;
using Pia.Services.LiveTranscription;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Services.LiveTranscription;

/// <summary>
/// Entry point for the diarization bench. Explicit, so it never runs in the gate.
///
/// <code>
/// $env:PIA_BENCH_WAV       = 'artifacts\wav\lsp.wav'          # teed by the replay, 16 kHz mono
/// $env:PIA_BENCH_ROSTER    = '5'
/// $env:PIA_BENCH_REFERENCE = 'scripts\speaker-reference\lsp.reference.json'   # optional
/// dotnet test -- --explicit only --filter-method "*Bench_MeasuresARecording*"
/// </code>
/// </summary>
public class DiarizationBenchTests
{
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

        var cache = EmbeddingCache.Load(cachePath);
        using var extractor = new CachedEmbeddingExtractor(
            cache, () => new SherpaEmbeddingExtractor(modelPath));

        var passLines = DiarizationBench.Identify(segments, roster, extractor);
        var identifiedAt = Environment.TickCount64;
        if (cache.Dirty) cache.Save(cachePath);

        // The service computes embeddings internally; the cache is the only place they surface, and
        // the oracle needs them.
        foreach (var segment in segments)
            if (cache.TryGet(segment.StartSample, segment.SampleCount, out var vector))
                segment.Embedding = vector;

        Say($"embeddings : {cache.Count} cached, {extractor.Misses} computed this run");
        Say($"timing     : {(segmentedAt - started) / 1000.0:F1} s segmenting, "
            + $"{(identifiedAt - segmentedAt) / 1000.0:F1} s identifying");

        var labelled = segments.Where(s => s.FinalLabel is not null).ToList();
        Say($"labels     : {segments.Select(s => s.Label).Where(l => l is not null).Distinct().Count()} ever minted, "
            + $"{labelled.Select(s => s.FinalLabel).Distinct().Count()} in the final state, "
            + $"{segments.Count(s => s.SampleCount >= 16000 * 3 / 2 && s.FinalLabel is null)} segments unlabelled");

        DiarizationBench.WriteSegments(Path.Combine(outDir, $"{name}.segments.jsonl"), segments);
        File.WriteAllLines(Path.Combine(outDir, $"{name}.passes.log"), passLines);

        if (!string.IsNullOrWhiteSpace(referencePath) && File.Exists(referencePath))
            AppendOracle(Say, segments, referencePath);

        File.WriteAllText(Path.Combine(outDir, $"{name}.report.txt"), report.ToString());
        Assert.NotEmpty(segments);
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
        say("Read the enrollment number against the live run: if it is not clearly higher, the ceiling "
            + "is the embedding model, not the matching policy.");
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
