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
            AppendOracle(Say, segments, referencePath, roster);

        File.WriteAllText(Path.Combine(outDir, $"{name}.report.txt"), report.ToString());
        Assert.NotEmpty(segments);
    }

    private static void AppendOracle(
        Action<string> say, List<BenchSegment> segments, string referencePath, int roster)
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

        var enrolled = DiarizationOracle.NearestCentroid(truthful, enrollSeconds: 30);
        say($"ORACLE enrollment (30 s/speaker): {enrolled.BySegment:P1} by segment, "
            + $"{enrolled.ByDuration:P1} by duration, over {enrolled.Total} segments");

        var pinned = DiarizationOracle.PinnedClusterer(truthful, roster);
        say($"ORACLE clusterer (k from the roster): {pinned.BySegment:P1} by segment, "
            + $"{pinned.ByDuration:P1} by duration");
        say("Read the enrollment number against the live run: if it is not clearly higher, the ceiling "
            + "is the embedding model, not the matching policy.");
    }
}
