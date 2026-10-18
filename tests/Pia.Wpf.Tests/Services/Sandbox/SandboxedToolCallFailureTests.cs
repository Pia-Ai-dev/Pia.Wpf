using System.IO;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using NSubstitute;
using Pia.Services.Plugins;
using Pia.Services.Plugins.Sandbox;
using Pia.Services.Plugins.Sandbox.Runtimes;
using Xunit;

namespace Pia.Tests.Services.Sandbox;

/// <summary>The stderr tail carries uv's install output, package names and host paths among it: it may reach the
/// settings row, never the release log or the model.</summary>
public sealed class SandboxedToolCallFailureTests
{
    internal const string InstallLine = @"Installed 1 executable: secret-pkg==1.2.3 into C:\Users\someone\AppData\Local\Pia";

    private static readonly SandboxLaunchSpec Spec = new(
        "pia-mcp-fake", @"C:\Tools\fake.exe", [], new Dictionary<string, string>(), @"C:\box",
        [@"C:\box"], [], AllowNetwork: false, GrantedCommandDirectory: null,
        RuntimeStateDirectory: Path.Combine(@"C:\pia-tool-failure-tests", Guid.NewGuid().ToString("N") + ".runtime"),
        RunnerKind: RunnerKind.Uvx);

    /// <summary>Answers the handshake, then exits, so the next request finds its pipes gone.</summary>
    internal static async Task AnswersTheHandshakeThenExits(StreamReader input, StreamWriter output, StreamWriter errors)
    {
        while (await input.ReadLineAsync() is { } line)
        {
            var message = JsonNode.Parse(line)!.AsObject();
            if (message["id"] is not { } id) continue;

            var method = message["method"]!.GetValue<string>();
            if (method is not ("initialize" or "tools/list"))
            {
                await output.WriteLineAsync(new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = id.DeepClone(),
                    ["error"] = new JsonObject { ["code"] = -32601, ["message"] = "Method not found" }
                }.ToJsonString());
                continue;
            }

            JsonNode result = method == "initialize"
                ? new JsonObject
                {
                    ["protocolVersion"] = message["params"]!["protocolVersion"]!.GetValue<string>(),
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                    ["serverInfo"] = new JsonObject { ["name"] = "fake", ["version"] = "1" }
                }
                : new JsonObject
                {
                    ["tools"] = new JsonArray(new JsonObject
                    {
                        ["name"] = "echo",
                        ["description"] = "Echoes",
                        ["inputSchema"] = new JsonObject { ["type"] = "object" }
                    })
                };

            await output.WriteLineAsync(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id.DeepClone(),
                ["result"] = result
            }.ToJsonString());

            if (method == "tools/list")
                return;
        }
    }

    internal static MxcClientTransport TransportWithInstallOutput(FakeMxcProcessLauncher launcher, ILogger? logger = null)
    {
        var verifier = Substitute.For<ISandboxGrantVerifier>();
        verifier.Verify(Arg.Any<SandboxLaunchSpec>()).Returns(new SandboxGrantResult(true, 0, null));
        return new MxcClientTransport(
            Spec, launcher, verifier, logger ?? NullLogger.Instance, NullLoggerFactory.Instance, new InstallingProvisioner());
    }

    private sealed class InstallingProvisioner : IRuntimeStateProvisioner
    {
        public Task<SandboxLaunchSpec> PrepareAsync(SandboxLaunchSpec spec, Action<string> stderrLine, CancellationToken cancellationToken)
        {
            stderrLine(InstallLine);
            return Task.FromResult(spec);
        }
    }

    private sealed class SingleTransportFactory(IClientTransport transport) : IMcpTransportFactory
    {
        public IClientTransport Create(McpTransportRequest request) => transport;
    }

    [Fact]
    public async Task AToolCallToAServerThatDied_TellsTheModelNothingOfTheInstallOutput_AndLogsNoneOfItInRelease()
    {
        var ct = TestContext.Current.CancellationToken;
        var logger = new CapturingLogger<SandboxedToolCallFailureTests>();
        var launcher = new FakeMxcProcessLauncher(AnswersTheHandshakeThenExits);
        var transport = TransportWithInstallOutput(launcher);
        var handler = new McpPluginToolHandler(
            Guid.NewGuid(), "fake", @"C:\Tools\fake.exe", [], systemPromptAddition: null, logger,
            transportFactory: new SingleTransportFactory(transport), sandbox: McpSandboxPolicy.Empty);
        try
        {
            await handler.InitializeAsync(ct);
            Assert.Null(handler.LastError);
            Assert.Contains(InstallLine, transport.StderrTail);

            var (_, pending) = await handler.HandleToolCallAsync(new FunctionCallContent("c1", "echo"), ct);
            var text = Assert.IsType<string>(await handler.ExecutePendingActionAsync(pending!).WaitAsync(TimeSpan.FromSeconds(30), ct));

            Assert.StartsWith("Tool call failed", text);
            Assert.DoesNotContain("secret-pkg", text);
            Assert.DoesNotContain(@"C:\Users", text);
            var failure = Assert.Single(logger.Entries, e => e.Level == LogLevel.Error);
            Assert.Null(failure.Exception);
            Assert.DoesNotContain(logger.Entries, e => e.Level >= LogLevel.Information
                && (e.Message.Contains("secret-pkg") || e.Exception?.ToString().Contains("secret-pkg") == true));
        }
        finally
        {
            await handler.ShutdownAsync();
        }
    }

    [Fact]
    public void TheServersOwnErrorAnswer_StillReachesTheModel()
    {
        var text = McpPluginToolHandler.ModelFacingFailure(new McpProtocolException("Request failed (remote): 'path' is required"));

        Assert.Equal("Tool call failed: Request failed (remote): 'path' is required", text);
    }

    [Fact]
    public void ATransportFailure_ReachesTheModelAsAFixedHint()
    {
        var text = McpPluginToolHandler.ModelFacingFailure(
            new IOException($"MCP server process exited unexpectedly{Environment.NewLine}Server's stderr tail:{Environment.NewLine}{InstallLine}"));

        Assert.Equal("Tool call failed: the MCP server did not answer.", text);
    }

    [Fact]
    public async Task AStartThatFails_LogsTheExceptionTypeOnly()
    {
        var logger = new CapturingLogger<SandboxedToolCallFailureTests>();
        var transport = TransportWithInstallOutput(new FakeMxcProcessLauncher(FakeMxcProcessLauncher.DiesAtBoot));
        var handler = new McpPluginToolHandler(
            Guid.NewGuid(), "fake", @"C:\Tools\fake.exe", [], systemPromptAddition: null, logger,
            transportFactory: new SingleTransportFactory(transport), sandbox: McpSandboxPolicy.Empty);

        await handler.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(handler.LastError);
        var failure = Assert.Single(logger.Entries, e => e.Level == LogLevel.Error);
        Assert.Null(failure.Exception);
        Assert.DoesNotContain(logger.Entries, e => e.Level >= LogLevel.Information && e.Message.Contains("boot line"));
        await handler.ShutdownAsync();
    }
}
