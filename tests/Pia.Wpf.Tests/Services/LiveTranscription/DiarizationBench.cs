using System.Globalization;
using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using NAudio.Wave;
using Pia.Services.LiveTranscription;

namespace Pia.Tests.Services.LiveTranscription;

/// <summary>
/// Runs the production segmentation + diarization over a recorded WAV with no transcription, no UI and
/// no real-time pacing, so a 50-minute meeting is measurable in minutes instead of an hour. The types
/// under test are the shipping ones; what the bench replaces is only the clock and the audio device.
///
/// <para>Known differences from a live run, which belong in any report built on this: there is no
/// speech-to-text, so nothing is dropped to transcription backpressure and no segment is discarded for
/// producing empty text; and the diarizer's 30 s pass trigger fires on stream time here, where the app
/// measures wall clock between identify calls.</para>
/// </summary>
internal sealed class DiarizationBench
{
    // The engine gates diarization at 1.5 s before the service ever sees a segment.
    private const int MinDiarizationSamples = 16000 * 3 / 2;
    private const int BubbleWindowSeconds = Pia.ViewModels.TranscriptOverlayViewModel.BubbleWindowSeconds;
    private static readonly DateTimeOffset ClockEpoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Decodes the recording through the same reader and hop resampler the replay path uses,
    /// then segments it with the production VAD.</summary>
    public static List<BenchSegment> Segment(string wavPath)
    {
        var segments = new List<BenchSegment>();
        using var vad = new SileroVadDetector(modelPath: string.Empty, NullLogger.Instance);
        vad.OnSegment += s => segments.Add(new BenchSegment
        {
            StartSample = s.StartSample,
            SampleCount = s.Samples.Length,
            Samples = s.Samples,
        });

        using var reader = new MediaFoundationReader(wavPath);
        var resampler = new AudioHopResampler(reader.WaveFormat);
        var buffer = new byte[8192];
        while (true)
        {
            int read = reader.Read(buffer, 0, buffer.Length);
            if (read <= 0) break;
            foreach (var hop in resampler.ProcessAvailable(buffer, read)) vad.Process(hop);
        }
        vad.Drain();
        return segments;
    }

    /// <summary>Replays the segments through the real diarizer on a stream-time clock, applying every
    /// correction a pass emits. Returns the log lines, which carry the pass sequence. Clears the labels
    /// first, so one segment list can be replayed under several option sets.</summary>
    public static IReadOnlyList<string> Identify(
        List<BenchSegment> segments, int rosterSize, CachedEmbeddingExtractor extractor,
        AdaptiveSpeakerOptions? options = null)
    {
        foreach (var segment in segments) { segment.Label = null; segment.FinalLabel = null; }

        var logger = new CapturingLogger<AdaptiveSpeakerIdentificationService>();
        var streamNow = ClockEpoch;
        // The service disposes the extractor it is handed, and a sweep shares one warm cache across
        // every policy, so it gets a borrowed view instead.
        using var service = new AdaptiveSpeakerIdentificationService(
            new BorrowedExtractor(extractor), logger, () => streamNow,
            AdaptiveSpeakerIdentificationService.DefaultMaxJournaledSegments,
            clusterer: null, options: options);
        if (rosterSize > 0) service.SetExpectedSpeakers(rosterSize);

        var bySegmentId = new Dictionary<long, BenchSegment>();
        // A pass runs inside the identify call that triggered it and can reassign that very segment,
        // which the loop below has not registered yet. Parking it here is what keeps a correction from
        // being silently overwritten by the provisional label.
        var pending = new Dictionary<long, string?>();
        service.SpeakersReassigned += (_, batch) =>
        {
            foreach (var r in batch)
            {
                if (bySegmentId.TryGetValue(r.SegmentId, out var target)) target.FinalLabel = r.NewLabel;
                else pending[r.SegmentId] = r.NewLabel;
            }
        };

        foreach (var segment in segments)
        {
            if (segment.SampleCount < MinDiarizationSamples) continue;

            // The pass's 30 s latency trigger is time-based, so the clock has to advance with the
            // stream or a fast run visits a different pass sequence than a real-time one.
            streamNow = ClockEpoch.AddSeconds(segment.StartSeconds + segment.DurationSeconds);
            extractor.Current = (segment.StartSample, segment.SampleCount);

            var result = service.IdentifyOrRegisterSegment(segment.Samples, 16000);
            bySegmentId[result.SegmentId] = segment;
            segment.Label = result.Label;
            segment.FinalLabel = pending.Remove(result.SegmentId, out var corrected)
                ? corrected
                : result.Label;
        }

        return [.. logger.Entries.Select(e => e.Message)];
    }

    private sealed class BorrowedExtractor(IEmbeddingExtractor inner) : IEmbeddingExtractor
    {
        public int Dim => inner.Dim;
        public float[] Compute(float[] samples, int sampleRate) => inner.Compute(samples, sampleRate);
        public void Dispose() { }
    }

    public static void WriteSegments(
        string path, IEnumerable<BenchSegment> segments, Func<BenchSegment, string?>? label = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var writer = new StreamWriter(path);
        foreach (var s in segments)
        {
            // A caller supplying its own label is asking about what the UI renders, which includes the
            // segments the diarizer never saw.
            if (label is null && s.SampleCount < MinDiarizationSamples) continue;
            writer.WriteLine(string.Format(
                CultureInfo.InvariantCulture,
                "{{\"startSeconds\":{0:F3},\"durationSeconds\":{1:F3},\"label\":{2},\"finalLabel\":{3}}}",
                s.StartSeconds, s.DurationSeconds, Quote(s.Label), Quote(label is null ? s.FinalLabel : label(s))));
        }
    }

    /// <summary>
    /// The label the transcript actually shows for each segment. TranscriptOverlayViewModel merges an
    /// unlabelled utterance into the in-window bubble before it, so a segment the diarizer refused to
    /// place still renders under whoever spoke last — an attribution no label-based metric can see.
    /// Modelled in stream time, where the ViewModel uses utterance arrival, so this merges at least as
    /// eagerly as the app does.
    /// </summary>
    public static Dictionary<BenchSegment, string?> RenderedLabels(
        IEnumerable<BenchSegment> segments, double windowSeconds = BubbleWindowSeconds)
    {
        var rendered = new Dictionary<BenchSegment, string?>();
        double bubbleStart = 0;
        string? bubbleLabel = null;
        var open = false;
        foreach (var s in segments)
        {
            var inWindow = open && s.StartSeconds - bubbleStart < windowSeconds;
            if (inWindow && (string.Equals(s.FinalLabel, bubbleLabel, StringComparison.Ordinal)
                             || (string.IsNullOrWhiteSpace(s.FinalLabel) && !string.IsNullOrWhiteSpace(bubbleLabel))))
            {
                rendered[s] = bubbleLabel;
                continue;
            }
            bubbleStart = s.StartSeconds;
            bubbleLabel = s.FinalLabel;
            open = true;
            rendered[s] = bubbleLabel;
        }
        return rendered;
    }

    private static string Quote(string? value) => value is null ? "null" : $"\"{value}\"";
}
