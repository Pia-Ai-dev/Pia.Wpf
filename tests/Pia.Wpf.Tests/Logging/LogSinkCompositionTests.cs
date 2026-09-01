using System.IO;
using Microsoft.Extensions.Logging;
using NReco.Logging.File;
using Pia.Logging;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Logging;

/// <summary>NReco has no scope support, so "BeginScope works" and "the scope is in pia-*.log" are different claims; this asserts
/// the second, against the real file sink.</summary>
public sealed class LogSinkCompositionTests : IDisposable
{
    private readonly string _dir;

    public LogSinkCompositionTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "PiaLogSink_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    /// <summary><paramref name="cap"/> is explicit because the DEBUG build default is unlimited.</summary>
    private (ILoggerFactory Factory, string Path) BuildSink(int cap)
    {
        // A file per fact, so the second test never meets the first's still-open handle.
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

    /// <summary>NReco writes from a background queue, so disposal does not put the bytes on disk — hence the wait for
    /// <paramref name="expected"/>, and the read of whatever file the sink actually named.</summary>
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

    /// <summary><c>File.ReadAllText</c> throws on a log file the sink still holds a handle on.</summary>
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

        // Waiting on the LAST line written puts the two before it on disk too.
        var text = ReadAfter(factory, path, expected: "after the run");

        Assert.Contains($"[run {runId}] dispatching", text);
        Assert.Contains($"[run {runId} step 2] executing", text);
        Assert.Contains("after the run", text);
        Assert.DoesNotContain($"[run {runId}] after the run", text);
    }

    /// <summary>Scope must wrap cap, not the reverse, so the run prefix sits inside the text truncation keeps.</summary>
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
        TempPath.Remove(_dir);
        GC.SuppressFinalize(this);
    }
}
