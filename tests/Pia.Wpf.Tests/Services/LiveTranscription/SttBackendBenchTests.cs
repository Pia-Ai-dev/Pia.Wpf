using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NAudio.Wave;
using Pia.Models;
using Pia.Services.Assets;
using Pia.Services.Interfaces;
using Pia.Services.LiveTranscription;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Services.LiveTranscription;

/// <summary>
/// Entry point for the speech-to-text backend bench. Explicit, so it never runs in the gate. It
/// downloads real models and decodes real audio.
///
/// <code>
/// $env:PIA_LOCAL_DATA_DIR   = 'G:\PiaBench'        # models land here, not on the system drive
/// $env:PIA_STT_BENCH_MEDIA  = 'G:\tmp\rec.mp4'     # anything Media Foundation can open
/// $env:PIA_STT_BENCH_SECONDS = '120'               # optional cap, default 120
/// $env:PIA_STT_BENCH_BACKENDS = 'Nemotron,Parakeet' # optional, default all three
/// $env:PIA_STT_BENCH_LANGUAGE = 'de'               # optional, default auto
/// dotnet test -- --explicit only --filter-method "*Bench_ComparesEveryBackend*"
/// </code>
/// </summary>
public class SttBackendBenchTests
{
    private const int SampleRate = 16000;

    [BenchFact]
    public async Task Bench_ComparesEveryBackendOnOneRecording()
    {
        var media = Environment.GetEnvironmentVariable("PIA_STT_BENCH_MEDIA");
        if (string.IsNullOrWhiteSpace(media) || !File.Exists(media))
            Assert.Skip("Set PIA_STT_BENCH_MEDIA to an audio or video file Media Foundation can open.");

        var maxSeconds = int.TryParse(
            Environment.GetEnvironmentVariable("PIA_STT_BENCH_SECONDS"),
            CultureInfo.InvariantCulture, out var s) ? s : 120;

        var backends = ParseBackends(Environment.GetEnvironmentVariable("PIA_STT_BENCH_BACKENDS"));
        var extraPadMs = int.TryParse(
            Environment.GetEnvironmentVariable("PIA_STT_BENCH_PADMS"),
            CultureInfo.InvariantCulture, out var pad) ? pad : 0;
        var language = ParseLanguage(Environment.GetEnvironmentVariable("PIA_STT_BENCH_LANGUAGE"));

        var outDir = Environment.GetEnvironmentVariable("PIA_STT_BENCH_OUT")
            ?? Path.Combine(Path.GetDirectoryName(media)!, "stt-bench");
        Directory.CreateDirectory(outDir);

        var report = new StringBuilder();
        void Say(string line)
        {
            report.AppendLine(line);
            TestContext.Current.SendDiagnosticMessage(line);
        }

        Say($"media    : {Path.GetFileName(media)}");
        Say($"cap      : {maxSeconds}s");
        Say($"language : {language}");
        Say($"models   : {LiveTranscriptionModels.ModelsDirectory}");

        var samples = Decode(media, maxSeconds);
        Say($"decoded  : {samples.Length / (double)SampleRate:F1}s of 16 kHz mono");

        var downloader = BuildDownloader();
        var logger = NullLogger.Instance;

        var vadPath = await LiveTranscriptionModels
            .EnsureSileroVadAsync(downloader, logger, TestContext.Current.CancellationToken)
            .ConfigureAwait(false);

        var segments = Segment(vadPath, samples, logger);
        Say($"segments : {segments.Count}");
        Assert.NotEmpty(segments);

        // sherpa's offline Whisper decodes a fixed 30 s window; anything longer is silently truncated.
        var overlong = segments.Count(seg => seg.Length > 30 * SampleRate);
        if (overlong > 0) Say($"WARNING  : {overlong} segment(s) exceed Whisper's 30 s window");

        foreach (var backend in backends)
        {
            Say("");
            Say($"### {backend}");

            var settings = new AppSettings
            {
                SttBackend = backend,
                WhisperModel = WhisperModelSize.Medium,
                TargetSpeechLanguage = language,
            };

            var loadWatch = Stopwatch.StartNew();
            await using var engine = await TranscriptionEngineFactory
                .CreateAsync(settings, downloader, null, logger, TestContext.Current.CancellationToken)
                .ConfigureAwait(false);
            loadWatch.Stop();
            Say($"load     : {loadWatch.Elapsed.TotalSeconds:F1}s");

            var transcript = new StringBuilder();
            var decodeMs = 0d;
            var audioSeconds = 0d;

            foreach (var raw in segments)
            {
                var segment = extraPadMs == 0 ? raw : Pad(raw, extraPadMs);
                var watch = Stopwatch.StartNew();
                var text = await engine
                    .TranscribeAsync(segment, TestContext.Current.CancellationToken)
                    .ConfigureAwait(false);
                watch.Stop();

                decodeMs += watch.Elapsed.TotalMilliseconds;
                audioSeconds += raw.Length / (double)SampleRate;
                transcript.AppendLine(text);
            }

            var body = transcript.ToString();
            Say($"decode   : {decodeMs / 1000:F1}s over {audioSeconds:F1}s of speech "
                + $"(RTF {decodeMs / 1000 / Math.Max(audioSeconds, 0.001):F2})");
            Say($"per seg  : {decodeMs / segments.Count:F0}ms");
            Say($"punctuat.: {(body.Any(c => c is '.' or '?' or '!' or ',') ? "yes" : "NO")}");
            Say($"casing   : {(body.Any(char.IsUpper) ? "yes" : "NO")}");
            Say($"chars    : {body.Trim().Length}");
            Say($"empty    : {body.Split('\n').Count(l => l.Trim().Length == 0) - 1} of {segments.Count} segments");

            var path = Path.Combine(outDir, $"{Path.GetFileNameWithoutExtension(media)}.{backend}.txt");
            await File.WriteAllTextAsync(path, body, TestContext.Current.CancellationToken).ConfigureAwait(false);
            Say($"written  : {path}");
        }

        var reportPath = Path.Combine(
            outDir, $"{Path.GetFileNameWithoutExtension(media)}.bench.txt");
        await File.WriteAllTextAsync(reportPath, report.ToString(), TestContext.Current.CancellationToken)
            .ConfigureAwait(false);
    }

    private static float[] Pad(float[] samples, int padMs)
    {
        var n = SampleRate * padMs / 1000;
        var padded = new float[n + samples.Length + n];
        samples.CopyTo(padded, n);
        return padded;
    }

    private static SttBackend[] ParseBackends(string? spec) =>
        string.IsNullOrWhiteSpace(spec)
            ? Enum.GetValues<SttBackend>()
            : [.. spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                      .Select(Enum.Parse<SttBackend>)];

    private static TargetSpeechLanguage ParseLanguage(string? spec) =>
        string.IsNullOrWhiteSpace(spec)
            ? TargetSpeechLanguage.Auto
            : Enum.Parse<TargetSpeechLanguage>(spec, ignoreCase: true);

    private static IAssetDownloader BuildDownloader()
    {
        var services = new ServiceCollection();
        services.AddHttpClient();
        services.AddLogging();
        services.Configure<AssetMirrorOptions>(_ => { });
        services.AddSingleton<IAssetDownloader, AssetDownloader>();
        return services.BuildServiceProvider().GetRequiredService<IAssetDownloader>();
    }

    /// <summary>Media Foundation opens mp4/m4a/wav alike; the resampler is a no-op when the source
    /// is already 16 kHz mono, which Teams recordings are.</summary>
    private static float[] Decode(string media, int maxSeconds)
    {
        using var reader = new MediaFoundationReader(media);
        using var resampler = new MediaFoundationResampler(reader, new WaveFormat(SampleRate, 16, 1))
        {
            ResamplerQuality = 60,
        };

        var wanted = maxSeconds * SampleRate;
        var samples = new List<float>(wanted);
        var buffer = new byte[SampleRate * 2];

        while (samples.Count < wanted)
        {
            var read = resampler.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            for (var i = 0; i + 1 < read; i += 2)
                samples.Add(BitConverter.ToInt16(buffer, i) / 32768f);
        }

        return [.. samples.Take(wanted)];
    }

    private static List<float[]> Segment(string vadModelPath, float[] samples, ILogger logger)
    {
        var segments = new List<float[]>();
        using var vad = new SileroVadDetector(vadModelPath, logger);
        vad.OnSegment += seg => segments.Add(seg.Samples);

        // The live path hands the detector 30 ms of audio at a time; matching it keeps the segment
        // boundaries comparable to what a real meeting produces.
        const int frame = SampleRate / 100 * 3;
        for (var offset = 0; offset < samples.Length; offset += frame)
            vad.Process(samples.AsSpan(offset, Math.Min(frame, samples.Length - offset)));

        vad.Drain();
        return segments;
    }
}
