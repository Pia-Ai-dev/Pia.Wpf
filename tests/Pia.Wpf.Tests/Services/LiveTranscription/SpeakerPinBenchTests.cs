using System.Globalization;
using System.IO;
using System.Text;
using Pia.Services.LiveTranscription;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Services.LiveTranscription;

/// <summary>
/// The evidence behind "the pin is reliable": a real recording driven through the real diarizer, with a
/// correction fired mid-meeting from the same seam a right-click would use, and hundreds of re-cluster
/// passes after it. Explicit, so it never runs in the gate.
///
/// <code>
/// $env:PIA_BENCH_WAV    = 'G:\tmp\Recordings\...mp4'   # MediaFoundationReader decodes mp4 too
/// $env:PIA_BENCH_ROSTER = '6'
/// dotnet test -- --explicit only --filter-method "*Pin_*"
/// </code>
/// </summary>
public class SpeakerPinBenchTests
{
    private static (List<BenchSegment> Segments, CachedEmbeddingExtractor Extractor, int Roster, string OutDir)?
        Load(Action<string> say)
    {
        var wav = Environment.GetEnvironmentVariable("PIA_BENCH_WAV");
        if (string.IsNullOrWhiteSpace(wav) || !File.Exists(wav))
        {
            Assert.Skip("Set PIA_BENCH_WAV to a recording.");
            return null;
        }

        var outDir = Environment.GetEnvironmentVariable("PIA_BENCH_OUT")
            ?? Path.Combine(Path.GetDirectoryName(wav)!, "bench");
        Directory.CreateDirectory(outDir);
        var roster = int.TryParse(
            Environment.GetEnvironmentVariable("PIA_BENCH_ROSTER"), CultureInfo.InvariantCulture, out var r) ? r : 0;
        var modelPath = Environment.GetEnvironmentVariable("PIA_BENCH_MODEL")
            ?? LiveTranscriptionModels.SpeakerEmbeddingModelPath;

        var segments = DiarizationBench.Segment(wav);
        say($"recording  : {Path.GetFileName(wav)}");
        say($"segments   : {segments.Count} closed, "
            + $"{segments.Count(s => s.SampleCount >= 16000 * 3 / 2)} above the 1.5 s gate, roster={roster}");

        var cachePath = Path.Combine(outDir, $"{Path.GetFileNameWithoutExtension(wav)}.embeddings.bin");
        var extractor = new CachedEmbeddingExtractor(
            EmbeddingCache.Load(cachePath), () => new SherpaEmbeddingExtractor(modelPath));
        return (segments, extractor, roster, outDir);
    }

    private static void Say(StringBuilder report, string line)
    {
        report.AppendLine(line);
        TestContext.Current.SendDiagnosticMessage(line);
    }

    /// <summary>
    /// Gate G-A. A pin fired at the meeting's midpoint must not be moved by any later pass — and the
    /// assertion is the silence, not the final label: a segment that merely happens to end up right
    /// proves nothing about exclusion.
    /// </summary>
    [BenchFact]
    public void Pin_SurvivesEveryLaterPass_OnARealRecording()
    {
        var report = new StringBuilder();
        var loaded = Load(l => Say(report, l));
        if (loaded is not var (segments, extractor, roster, outDir)) return;
        using var _ = extractor;

        foreach (var pinCount in new[] { 1, 5 })
        {
            var eligible = segments.Where(s => s.SampleCount >= 16000 * 3 / 2).ToList();
            // A quarter in, not halfway: a user corrects as soon as they notice, and it leaves three
            // quarters of the meeting's passes for the pin to survive.
            var firesAt = eligible[eligible.Count / 4].StartSeconds;

            var pinnedIds = new List<long>();
            string? pinnedTo = null;
            var movedAfterPin = new List<long>();
            var passesAfterPin = 0;
            var fired = false;

            var log = DiarizationBench.Identify(segments, roster, extractor, options: null,
                afterIdentify: (service, segmentId, atSeconds) =>
                {
                    if (fired || atSeconds < firesAt) return;

                    // Move the last few segments onto a voice that is not the one they carry — the
                    // correction a user makes when a bubble is on the wrong person.
                    var candidates = segments
                        .Where(s => s.SampleCount >= 16000 * 3 / 2 && s.StartSeconds <= atSeconds)
                        .TakeLast(pinCount).ToList();
                    var here = candidates[^1].FinalLabel ?? candidates[^1].Label;
                    pinnedTo = service.KnownLabels.FirstOrDefault(l => l != here);
                    if (pinnedTo is null) return;

                    // Ids are the diarizer's own, and only the current call's is handed to the seam, so
                    // resolve the rest the way the ViewModel does — by position in the eligible stream.
                    var firstId = segmentId - (pinCount - 1);
                    for (var id = firstId; id <= segmentId; id++) pinnedIds.Add(id);

                    Assert.True(service.AssignSegments(pinnedIds, pinnedTo));
                    fired = true;
                });

            Assert.True(fired, "the correction never fired — the recording is too short");

            // Every reassignment the log recorded after the pin, and whether any names a pinned id.
            var pinLine = -1;
            for (var i = 0; i < log.Count && pinLine < 0; i++)
                if (log[i].StartsWith("Speaker correction", StringComparison.Ordinal)) pinLine = i;
            Assert.True(pinLine >= 0, "the correction produced no log line");
            for (var i = pinLine + 1; i < log.Count; i++)
            {
                if (log[i].StartsWith("Adaptive pass:", StringComparison.Ordinal)) passesAfterPin++;
                if (!log[i].StartsWith("Adaptive pass reassigned:", StringComparison.Ordinal)) continue;
                foreach (var id in pinnedIds)
                    if (log[i].Contains($"{id}=", StringComparison.Ordinal)) movedAfterPin.Add(id);
            }

            Say(report, $"pin({pinCount})    : ids [{string.Join(",", pinnedIds)}] → '{pinnedTo}' "
                + $"at {firesAt:F0}s, {passesAfterPin} passes followed, {movedAfterPin.Count} moved");

            Assert.True(passesAfterPin >= 20,
                $"only {passesAfterPin} passes ran after the pin — not enough to call it survived");
            Assert.Empty(movedAfterPin);
        }

        File.WriteAllText(Path.Combine(outDir, "pin-gate.txt"), report.ToString());
    }

    /// <summary>
    /// Gate G-B, the half a single checkout can assert: with no correction fired, the pass surgery must
    /// leave every segment where it was. Writes segments.jsonl for the against-main diff, which is the
    /// other half and is a shell step.
    /// </summary>
    [BenchFact]
    public void Pin_IsInert_WhenNoCorrectionIsMade()
    {
        var report = new StringBuilder();
        var loaded = Load(l => Say(report, l));
        if (loaded is not var (segments, extractor, roster, outDir)) return;
        using var _ = extractor;

        var log = DiarizationBench.Identify(segments, roster, extractor);

        Assert.DoesNotContain(log, l => l.StartsWith("Speaker correction", StringComparison.Ordinal));
        var passes = log.Count(l => l.StartsWith("Adaptive pass:", StringComparison.Ordinal));
        var labels = segments.Select(s => s.FinalLabel).Where(l => l is not null).Distinct().Count();
        Say(report, $"clean run  : {passes} passes, {labels} distinct labels survived");

        DiarizationBench.WriteSegments(Path.Combine(outDir, "segments.jsonl"), segments);
        File.WriteAllText(Path.Combine(outDir, "inert.txt"), report.ToString());
        Assert.True(passes > 0);
    }
}
