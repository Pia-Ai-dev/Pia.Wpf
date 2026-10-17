using System.Net.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using Pia.Services.Interfaces;
using Pia.Services.Plugins;
using Pia.Services.Plugins.Sandbox;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Services;

public class McpSandboxedToolCallTests
{
    // The fixture runs uncontained here: only the policy the pending call carries is under test.
    private sealed class FixtureFactory : IMcpTransportFactory
    {
        public IClientTransport Create(McpTransportRequest request) =>
            new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = "fixture",
                Command = TestMcpServerFixture.ExePath,
                ShutdownTimeout = TimeSpan.FromSeconds(1),
            });
    }

    private static async Task<PluginToolCall> PendingCallAsync(McpSandboxPolicy? sandbox)
    {
        var handler = new McpPluginToolHandler(
            Guid.NewGuid(), "files", TestMcpServerFixture.ExePath, [], systemPromptAddition: null,
            NullLogger.Instance, transportFactory: new FixtureFactory(), sandbox: sandbox);
        try
        {
            await handler.InitializeAsync(TestContext.Current.CancellationToken);
            var call = new FunctionCallContent("c1", "delete_file", new Dictionary<string, object?> { ["path"] = @"C:\x\a.txt" });

            var (result, pending) = await handler.HandleToolCallAsync(call, TestContext.Current.CancellationToken);

            Assert.Null(result);
            return Assert.IsType<PluginToolCall>(pending);
        }
        finally
        {
            await handler.ShutdownAsync();
        }
    }

    [Fact]
    public async Task ThePendingCallOfASandboxedServer_CarriesItsPolicy()
    {
        var policy = new McpSandboxPolicy([@"C:\work"], [@"C:\ref"], AllowNetwork: true);

        var pending = await PendingCallAsync(policy);

        Assert.NotNull(pending.Sandbox);
        Assert.Equal(policy.ReadwritePaths, pending.Sandbox!.ReadwritePaths);
        Assert.Equal(policy.ReadonlyPaths, pending.Sandbox.ReadonlyPaths);
        Assert.True(pending.Sandbox.AllowNetwork);
    }

    [Fact]
    public async Task ThePendingCallOfAnUncontainedServer_CarriesNoPolicy()
    {
        var pending = await PendingCallAsync(sandbox: null);

        Assert.Null(pending.Sandbox);
    }
}
