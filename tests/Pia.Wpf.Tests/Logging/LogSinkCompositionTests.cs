using System.IO;
using Microsoft.Extensions.Logging;
using NReco.Logging.File;
using Pia.Logging;
using Xunit;

namespace Pia.Tests.Logging;

/// <summary>
/// T2-18, END TO END: the run scope reaches the FILE. Every other logging fact in this folder asserts against a
/// stand-in sink, which is exactly the gap that let the item look done while shipping dead — <c>NReco</c> has no
/// scope support, so "BeginScope works" and "the scope is in pia-*.log" are different claims and only the second
/// one is the item.
/// <para>
/// This drives the real <c>FileLoggerProvider</c> in the same composition <c>Bootstrapper</c> builds
/// (scope OUTSIDE cap) against a temp file, and reads the bytes back.
/// </para>
/// </summary>
public sealed class LogSinkCompositionTests : IDisposable
{
    private readonly string _dir;

    public LogSinkCompositionTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "PiaLogSink_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    /// <summary>The Bootstrapper composition, pointed at a temp file. <paramref name="cap"/> is explicit so the
    /// truncation half runs in a DEBUG test run too (the build default there is unlimited).</summary>
    private (ILoggerFactory Factory, string Path) BuildSink(int cap)
    {
        // A file per fact: two facts in one class would otherwise append to one file, and the second would meet
        // the first's still-open handle.
        var path = Path.Combine(_dir, "pia-" + Guid.NewGuid().ToString("N") + ".log");
        var options = new FileLoggerOptions
        {
            Append = true,
            MinLevel = LogLevel.Information,
            // No date suffix: FormatLogFileName is left at its default so the file is exactly `path`.
        };

        var provider = new ScopeRenderingLoggerProvider(
            new LogMessageCapLoggerProvider(new FileLoggerProvider(path, options), cap));

        var factory = LoggerFactory.Create(b => b.AddProvider(provider));
        return (factory, path);
    }

    /// <summary>
    /// Reads the log after the factory (and with it the provider) has been disposed, waiting for the writer to
    /// land.
    /// <para>
    /// THE WAIT IS NOT DECORATION, and it is why this method takes <paramref name="expected"/>: NReco writes from
    /// a background queue, so disposal does not guarantee the bytes are on disk by the time the next statement
    /// runs. Reading once made this fact pass alone and fail inside the full suite — a load-sensitive flake,
    /// authored here, not inherited. The bound is generous and the assertion is unchanged: if the composition is
    /// wrong the text never arrives and the fact still fails, just a second later.
    /// </para>
    /// <para>
    /// It reads whatever file the sink actually created rather than assuming the name — NReco applies its own
    /// rolling-file convention to the path it is given, and pinning the literal name would make this a fact about
    /// file naming instead of about the log's CONTENT.
    /// </para>
    /// </summary>
    private string ReadAfter(ILoggerFactory factory, string path, string expected)
    {
        factory.Dispose();

        var deadline = DateTime.UtcNow.AddSeconds(10);
        var text = string.Empty;
        while (true)
        {
            var candidates = File.Exists(path) ? [path] : Directory.GetFiles(_dir);
            if (candidates.Length > 0)
                text = string.Join(Environment.NewLine, candidates.Select(ReadShared));

            if (text.Contains(expected, StringComparison.Ordinal) || DateTime.UtcNow > deadline)
                return text;

            Thread.Sleep(25);
        }
    }

    /// <summary>
    /// Reads a file the sink may still hold a handle on. <c>File.ReadAllText</c> asks for exclusive-ish sharing
    /// and throws on a live log file, which is a property of the reader, not a fact about the log.
    /// </summary>
    private static string ReadShared(string path)
    {
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [Fact]
    public void TheRunAndStepScope_ReachTheFile()
    {
        var (factory, path) = BuildSink(cap: int.MaxValue);
        var runId = Guid.NewGuid();

        var logger = factory.CreateLogger("Pia.Services.AgentRunOrchestrator");
        using (logger.BeginScope("run {RunId}", runId))
        {
            logger.LogInformation("dispatching");
            using (logger.BeginScope("step {StepOrdinal}", 2))
                logger.LogInformation("executing");
        }
        logger.LogInformation("after the run");

        // The LAST line written is what we wait for, so the two before it are certainly on disk as well.
        var text = ReadAfter(factory, path, expected: "after the run");

        Assert.Contains($"[run {runId}] dispatching", text);
        Assert.Contains($"[run {runId} step 2] executing", text);
        // And the scope really closed: the last line carries no prefix at all.
        Assert.Contains("after the run", text);
        Assert.DoesNotContain($"[run {runId}] after the run", text);
    }

    /// <summary>
    /// The composition ORDER, which the Bootstrapper comment claims: scope outside cap, so the prefix is inside
    /// the capped text and survives truncation (which keeps the head). A capped line still says which run it
    /// belongs to — that is the whole reason the order is not the other way round.
    /// </summary>
    [Fact]
    public void ACappedLine_StillCarriesItsRunScope()
    {
        var (factory, path) = BuildSink(cap: 60);
        var runId = Guid.NewGuid();

        var logger = factory.CreateLogger("Pia.Services.Test");
        using (logger.BeginScope("run {RunId}", runId))
            logger.LogInformation("{Payload}", new string('x', 4000));

        var text = ReadAfter(factory, path, expected: "chars withheld from the release log");

        Assert.Contains($"[run {runId}]", text);
        Assert.Contains("chars withheld from the release log", text);
        Assert.DoesNotContain(new string('x', 200), text);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp dir */ }
        GC.SuppressFinalize(this);
    }
}
