using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Services.Screen;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Services.Screen;

/// <summary>The real on-disk behaviour: one JSON line per capture, the title present only as a hash, nothing
/// written until something is captured, and a drop that is loud rather than silent.</summary>
public sealed class ScreenCaptureAuditLogTests : IDisposable
{
    private readonly string _tmpDir = Path.Combine(Path.GetTempPath(), "PiaTests_" + Guid.NewGuid().ToString("N"));

    public ScreenCaptureAuditLogTests() => Directory.CreateDirectory(_tmpDir);

    public void Dispose() => TempPath.Remove(_tmpDir);

    private static ScreenCaptureAuditEvent WindowEvent(string title = "Inbox - Outlook") => new(
        ScreenCaptureSurfaces.Unattended,
        Guid.NewGuid(),
        "routine:1234",
        ScreenCaptureTargetKinds.Window,
        "outlook",
        title,
        1568,
        880);

    [Fact]
    public async Task Record_WritesOneCamelCaseJsonLinePerEvent()
    {
        var path = Path.Combine(_tmpDir, "captures.jsonl");
        await using (var sut = new ScreenCaptureAuditLog(path, NullLogger<ScreenCaptureAuditLog>.Instance))
        {
            sut.Record(WindowEvent());
            sut.Record(WindowEvent("Calendar - Outlook"));
        }

        var lines = await File.ReadAllLinesAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal(2, lines.Length);
        foreach (var line in lines)
        {
            using var doc = JsonDocument.Parse(line);
            Assert.True(doc.RootElement.TryGetProperty("timestamp", out _));
            Assert.True(doc.RootElement.TryGetProperty("surface", out _));
            Assert.True(doc.RootElement.TryGetProperty("targetKind", out _));
            Assert.True(doc.RootElement.TryGetProperty("processName", out _));
            Assert.True(doc.RootElement.TryGetProperty("width", out _));
            Assert.True(doc.RootElement.TryGetProperty("height", out _));
            Assert.True(doc.RootElement.TryGetProperty("titleHash", out _));
            Assert.False(doc.RootElement.TryGetProperty("windowTitle", out _));
        }
    }

    [Fact]
    public async Task Record_NeverWritesTheTitle()
    {
        var path = Path.Combine(_tmpDir, "captures.jsonl");
        await using (var sut = new ScreenCaptureAuditLog(path, NullLogger<ScreenCaptureAuditLog>.Instance))
        {
            sut.Record(WindowEvent("Quarterly numbers - Excel"));
        }

        var content = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        Assert.DoesNotContain("Quarterly", content, StringComparison.OrdinalIgnoreCase);

        using var doc = JsonDocument.Parse(content.Trim());
        var hash = doc.RootElement.GetProperty("titleHash").GetString();
        Assert.Equal(16, hash!.Length);
        Assert.All(hash, c => Assert.Contains(c, "0123456789abcdef"));
    }

    /// <summary>A positional record prints every member, so one "{Evt}" in a log line would leak the title.</summary>
    [Fact]
    public void Event_ToString_NeverContainsTheTitle()
    {
        var text = WindowEvent("Q3 payroll.xlsx - Excel").ToString();

        Assert.DoesNotContain("payroll", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("outlook", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TitleHash_IsStableWithinALaunch_AndDiffersAcrossLaunches()
    {
        var first = ScreenCaptureTitleHash.NewKey();
        var second = ScreenCaptureTitleHash.NewKey();

        Assert.Equal(
            ScreenCaptureTitleHash.Compute(first, "Inbox - Outlook"),
            ScreenCaptureTitleHash.Compute(first, "Inbox - Outlook"));
        Assert.NotEqual(
            ScreenCaptureTitleHash.Compute(first, "Inbox - Outlook"),
            ScreenCaptureTitleHash.Compute(second, "Inbox - Outlook"));

        Assert.Equal(string.Empty, ScreenCaptureTitleHash.Compute(first, null));
        Assert.Equal(string.Empty, ScreenCaptureTitleHash.Compute(first, ""));
    }

    [Fact]
    public async Task MonitorEvent_HasEmptyProcessAndEmptyTitleHash()
    {
        var path = Path.Combine(_tmpDir, "captures.jsonl");
        await using (var sut = new ScreenCaptureAuditLog(path, NullLogger<ScreenCaptureAuditLog>.Instance))
        {
            sut.Record(new ScreenCaptureAuditEvent(
                ScreenCaptureSurfaces.Picker, Guid.NewGuid(), null,
                ScreenCaptureTargetKinds.Monitor, "", null, 2560, 1440));
        }

        var content = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(content.Trim());
        Assert.Equal("", doc.RootElement.GetProperty("processName").GetString());
        Assert.Equal("", doc.RootElement.GetProperty("titleHash").GetString());
        Assert.Equal("monitor", doc.RootElement.GetProperty("targetKind").GetString());
    }

    /// <summary>The singleton is resolved at startup whether or not anything is ever captured, so a launch that
    /// never captures must leave nothing behind.</summary>
    [Fact]
    public async Task NoRecord_CreatesNoFileAndNoDirectory()
    {
        var path = Path.Combine(_tmpDir, "nested", "captures.jsonl");
        var sut = new ScreenCaptureAuditLog(path, NullLogger<ScreenCaptureAuditLog>.Instance);

        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.False(File.Exists(path));
        Assert.False(Directory.Exists(Path.GetDirectoryName(path)!));

        await sut.DisposeAsync();
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task FirstRecord_CreatesTheDirectoryAndTheFile()
    {
        var path = Path.Combine(_tmpDir, "nested", "captures.jsonl");
        await using (var sut = new ScreenCaptureAuditLog(path, NullLogger<ScreenCaptureAuditLog>.Instance))
        {
            sut.Record(WindowEvent());
        }

        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task DisposeAsync_FlushesQueuedEvents()
    {
        var path = Path.Combine(_tmpDir, "captures.jsonl");
        var sut = new ScreenCaptureAuditLog(path, NullLogger<ScreenCaptureAuditLog>.Instance);
        sut.Record(WindowEvent());
        sut.Record(WindowEvent());

        await sut.DisposeAsync();

        Assert.Equal(2, (await File.ReadAllLinesAsync(path, TestContext.Current.CancellationToken)).Length);
    }

    [Fact]
    public async Task Overflow_DropsAndLogsAWarning()
    {
        var path = Path.Combine(_tmpDir, "captures.jsonl");
        var logger = new CapturingLogger<ScreenCaptureAuditLog>();
        using var gate = new SemaphoreSlim(0, 1);
        var sut = new ScreenCaptureAuditLog(
            path, logger, capacity: 2, openStream: _ => new BlockingStream(gate));

        for (var i = 0; i < 5; i++)
            sut.Record(WindowEvent());

        var warned = false;
        for (var attempt = 0; attempt < 100 && !warned; attempt++)
        {
            warned = logger.Entries.Any(e =>
                e.Level == LogLevel.Warning && e.Message.Contains("overflow", StringComparison.OrdinalIgnoreCase));
            if (!warned) await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.True(warned, "a full queue must drop loudly, not silently");

        gate.Release();
        await sut.DisposeAsync();
    }

    /// <summary>Non-vacuity for the test above: the same five events through a queue that fits them warn about
    /// nothing, so the warning is the overflow and not the fixture.</summary>
    [Fact]
    public async Task ARoomyQueue_DropsNothing()
    {
        var path = Path.Combine(_tmpDir, "captures.jsonl");
        var logger = new CapturingLogger<ScreenCaptureAuditLog>();
        var sut = new ScreenCaptureAuditLog(path, logger, capacity: 64);

        for (var i = 0; i < 5; i++)
            sut.Record(WindowEvent());
        await sut.DisposeAsync();

        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Equal(5, (await File.ReadAllLinesAsync(path, TestContext.Current.CancellationToken)).Length);
    }

    [Fact]
    public async Task Record_NeverThrows_WhenTheStreamFactoryThrows()
    {
        var logger = new CapturingLogger<ScreenCaptureAuditLog>();
        var sut = new ScreenCaptureAuditLog(
            Path.Combine(_tmpDir, "captures.jsonl"), logger, capacity: 8,
            openStream: _ => throw new IOException("no"));

        sut.Record(WindowEvent());
        await sut.DisposeAsync();

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Exception is IOException);
    }

    /// <summary>Blocks the overload <c>StreamWriter</c> actually calls; overriding only the synchronous
    /// <c>Write</c> would let the drain run and empty the queue.</summary>
    private sealed class BlockingStream(SemaphoreSlim gate) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => 0;
        public override long Position { get => 0; set { } }

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await gate.WaitAsync(cancellationToken);
            gate.Release();
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) { }
    }
}
