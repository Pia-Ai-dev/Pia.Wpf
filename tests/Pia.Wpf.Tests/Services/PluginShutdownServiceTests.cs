using System.Diagnostics;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Pia.Services.Interfaces;
using Pia.Services.Plugins;
using Pia.Shared.Models;
using Xunit;

namespace Pia.Tests.Services;

public class PluginShutdownServiceTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromMilliseconds(300);

    private static (PluginShutdownService Service, IPluginService Plugins, CapturingLogger<PluginShutdownService> Log) Create()
    {
        var plugins = Substitute.For<IPluginService>();
        var log = new CapturingLogger<PluginShutdownService>();
        return (new PluginShutdownService(plugins, log, Bound), plugins, log);
    }

    private static SyncPlugin Row(Guid id) => new() { Id = id, Kind = "mcp_server", Name = id.ToString("N"), UserEnabled = true };

    [Fact]
    public void AShutdownThatHangs_ReturnsWithinTheBound_AndSaysItTimedOut()
    {
        var (service, plugins, log) = Create();
        plugins.ShutdownAllAsync().Returns(new TaskCompletionSource().Task);

        var watch = Stopwatch.StartNew();
        service.ShutDownBounded();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"took {watch.Elapsed}");
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("timed out", StringComparison.Ordinal));
    }

    [Fact]
    public void AShutdownThatCompletes_LogsHowManyServersWereRunning()
    {
        var (service, plugins, log) = Create();
        var running = Guid.NewGuid();
        var stopped = Guid.NewGuid();
        plugins.GetLocalMcpPlugins().Returns([Row(running), Row(stopped)]);
        plugins.GetLocalMcpStatus(running).Returns(new LocalMcpStatus(true, [], [], null));
        plugins.GetLocalMcpStatus(stopped).Returns(new LocalMcpStatus(false, [], [], null));
        plugins.ShutdownAllAsync().Returns(Task.CompletedTask);

        service.ShutDownBounded();

        plugins.Received(1).ShutdownAllAsync();
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Information && e.Message.StartsWith("Stopped 1 MCP server(s) in ", StringComparison.Ordinal));
    }

    [Fact]
    public void ASecondCall_DoesNothing()
    {
        var (service, plugins, _) = Create();
        plugins.ShutdownAllAsync().Returns(Task.CompletedTask);

        service.ShutDownBounded();
        service.ShutDownBounded();

        plugins.Received(1).ShutdownAllAsync();
    }

    [Fact]
    public void AShutdownThatThrows_IsSwallowedAndLogged()
    {
        var (service, plugins, log) = Create();
        plugins.ShutdownAllAsync().Returns(Task.FromException(new InvalidOperationException("boom")));

        service.ShutDownBounded();

        Assert.Contains(log.Entries, e => e.Level == LogLevel.Error && e.Exception is InvalidOperationException or AggregateException);
    }

    [Fact]
    public void AShutdownThatThrowsSynchronously_IsSwallowedToo()
    {
        var (service, plugins, log) = Create();
        plugins.ShutdownAllAsync().Returns<Task>(_ => throw new InvalidOperationException("boom"));

        service.ShutDownBounded();

        Assert.Contains(log.Entries, e => e.Level == LogLevel.Error);
    }

    [Fact]
    public void AFailingServerCount_DoesNotStopTheShutdown()
    {
        var (service, plugins, _) = Create();
        plugins.GetLocalMcpPlugins().Returns<IReadOnlyList<SyncPlugin>>(_ => throw new InvalidOperationException("no list"));
        plugins.ShutdownAllAsync().Returns(Task.CompletedTask);

        service.ShutDownBounded();

        plugins.Received(1).ShutdownAllAsync();
    }

    // The UI thread's context never runs a posted continuation while it waits, which is what a bare Wait over a
    // handler's awaits would deadlock on.
    private sealed class NeverPumpedContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state)
        {
        }
    }

    [Fact]
    public void ShuttingDownFromAThreadWhoseContextIsNeverPumped_DoesNotDeadlock()
    {
        var (service, plugins, log) = Create();
        plugins.ShutdownAllAsync().Returns(_ => Delayed());
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new NeverPumpedContext());
        try
        {
            var watch = Stopwatch.StartNew();
            service.ShutDownBounded();

            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"took {watch.Elapsed}");
            Assert.DoesNotContain(log.Entries, e => e.Message.Contains("timed out", StringComparison.Ordinal));
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        static async Task Delayed() => await Task.Delay(30);
    }
}
