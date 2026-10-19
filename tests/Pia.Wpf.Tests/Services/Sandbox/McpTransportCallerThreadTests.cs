using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using Pia.Services.Exceptions;
using Pia.Services.Plugins;
using Xunit;

namespace Pia.Tests.Services.Sandbox;

/// <summary>Creating a transport detects runtimes and reads ACLs, so neither caller may run it on its own thread.</summary>
public sealed class McpTransportCallerThreadTests
{
    private static readonly TimeSpan CallerBudget = TimeSpan.FromSeconds(10);

    private static readonly LocalMcpDefinition Definition =
        new("Files", "npx", ["-y", "@x/y"], new Dictionary<string, string>(), null, "fs", null);

    [Fact]
    public async Task TheProbe_ReturnsToItsCaller_WhileCreateIsStillRunning()
    {
        using var factory = new GatedFactory();

        var probe = CallOnOwnThread(() => McpServerProbe.ProbeAsync(Definition, factory, TimeSpan.FromMinutes(1),
            ct: TestContext.Current.CancellationToken), factory);

        Assert.True(factory.Entered.Wait(CallerBudget, TestContext.Current.CancellationToken), "Create was never called");
        Assert.False(probe.IsCompleted);
        Assert.NotEqual(factory.CallerThread, factory.CreateThread);

        factory.Gate.Set();
        var result = await probe;
        Assert.False(result.Success);
        Assert.Equal("stop", result.Error);
    }

    [Fact]
    public async Task TheHandler_ReturnsToItsCaller_WhileCreateIsStillRunning()
    {
        using var factory = new GatedFactory();
        using var handler = new McpPluginToolHandler(Guid.NewGuid(), "Files", "npx", ["-y", "@x/y"], systemPromptAddition: null,
            NullLogger.Instance, transportFactory: factory);

        var initialize = CallOnOwnThread(() => handler.InitializeAsync(TestContext.Current.CancellationToken), factory);

        Assert.True(factory.Entered.Wait(CallerBudget, TestContext.Current.CancellationToken), "Create was never called");
        Assert.False(initialize.IsCompleted);
        Assert.NotEqual(factory.CallerThread, factory.CreateThread);

        factory.Gate.Set();
        await initialize;
        Assert.Equal("stop", handler.LastError);
    }

    // A caller that ran Create inline would block here on the gate; the budget turns that into a failure, not a hang.
    private static T CallOnOwnThread<T>(Func<T> call, GatedFactory factory) where T : Task
    {
        T? task = null;
        var caller = new Thread(() =>
        {
            factory.CallerThread = Environment.CurrentManagedThreadId;
            task = call();
        });
        caller.Start();
        if (!caller.Join(CallerBudget))
        {
            factory.Gate.Set();
            caller.Join();
            Assert.Fail("The caller was blocked until Create returned.");
        }

        return task!;
    }

    private sealed class GatedFactory : IMcpTransportFactory, IDisposable
    {
        public ManualResetEventSlim Gate { get; } = new();

        public ManualResetEventSlim Entered { get; } = new();

        public int CallerThread { get; set; }

        public int CreateThread { get; private set; }

        public IClientTransport Create(McpTransportRequest request)
        {
            CreateThread = Environment.CurrentManagedThreadId;
            Entered.Set();
            Gate.Wait(TimeSpan.FromMinutes(1));
            throw new SandboxStartException("stop");
        }

        public void Dispose()
        {
            Gate.Set();
            Gate.Dispose();
            Entered.Dispose();
        }
    }
}
