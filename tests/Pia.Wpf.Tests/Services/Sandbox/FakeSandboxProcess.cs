using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using Pia.Services.Plugins.Sandbox;

namespace Pia.Tests.Services.Sandbox;

/// <summary>An in-process stand-in for a contained MCP server: real pipes, a scripted JSON-RPC peer, and the
/// same stdin-EOF-then-exit behaviour a well-behaved stdio server has.</summary>
internal sealed class FakeMxcProcessLauncher : IMxcProcessLauncher
{
    private readonly Func<StreamReader, StreamWriter, StreamWriter, Task> _server;

    public FakeMxcProcessLauncher(Func<StreamReader, StreamWriter, StreamWriter, Task>? server = null) =>
        _server = server ?? ScriptedServer;

    public List<SandboxLaunchSpec> Launched { get; } = [];

    public int ShutdownCalls { get; private set; }

    public bool StdinWasClosed { get; private set; }

    public Exception? FailWith { get; set; }

    public Task<LaunchedMxcProcess> LaunchAsync(SandboxLaunchSpec spec, CancellationToken cancellationToken)
    {
        if (FailWith is not null) throw FailWith;
        Launched.Add(spec);

        var clientToServer = new AnonymousPipeServerStream(PipeDirection.Out);
        var serverReads = new AnonymousPipeClientStream(PipeDirection.In, clientToServer.ClientSafePipeHandle);
        var serverToClient = new AnonymousPipeServerStream(PipeDirection.In);
        var serverWrites = new AnonymousPipeClientStream(PipeDirection.Out, serverToClient.ClientSafePipeHandle);
        var stderrPipe = new AnonymousPipeServerStream(PipeDirection.In);
        var stderrWrites = new AnonymousPipeClientStream(PipeDirection.Out, stderrPipe.ClientSafePipeHandle);

        var task = Task.Run(async () =>
        {
            using var reader = new StreamReader(serverReads, new UTF8Encoding(false));
            using var writer = new StreamWriter(serverWrites, new UTF8Encoding(false)) { AutoFlush = true };
            using var errors = new StreamWriter(stderrWrites, new UTF8Encoding(false)) { AutoFlush = true };
            try { await _server(reader, writer, errors); } catch (IOException) { }
        }, CancellationToken.None);

        var stdin = new ObservedStream(clientToServer, () => StdinWasClosed = true);
        return Task.FromResult(new LaunchedMxcProcess(stdin, serverToClient, stderrPipe, 4242, async () =>
        {
            ShutdownCalls++;
            await stdin.DisposeAsync();
            await task.WaitAsync(TimeSpan.FromSeconds(5));
        }));
    }

    /// <summary>Answers initialize and tools/list, ignores notifications, exits on stdin EOF.</summary>
    public static async Task ScriptedServer(StreamReader input, StreamWriter output, StreamWriter errors)
    {
        await errors.WriteLineAsync("server booting");
        while (await input.ReadLineAsync() is { } line)
        {
            var message = JsonNode.Parse(line)!.AsObject();
            var id = message["id"];
            if (id is null) continue;

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

            JsonNode result = method switch
            {
                "initialize" => new JsonObject
                {
                    ["protocolVersion"] = message["params"]!["protocolVersion"]!.GetValue<string>(),
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                    ["serverInfo"] = new JsonObject { ["name"] = "fake", ["version"] = "1" }
                },
                "tools/list" => new JsonObject
                {
                    ["tools"] = new JsonArray(new JsonObject
                    {
                        ["name"] = "echo",
                        ["description"] = "Echoes",
                        ["inputSchema"] = new JsonObject { ["type"] = "object" }
                    })
                },
                _ => throw new InvalidOperationException(method)
            };

            await output.WriteLineAsync(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id.DeepClone(),
                ["result"] = result
            }.ToJsonString());
        }
    }

    /// <summary>A server that prints to stderr and dies before it ever answers.</summary>
    public static async Task DiesAtBoot(StreamReader input, StreamWriter output, StreamWriter errors)
    {
        for (var i = 1; i <= 25; i++)
            await errors.WriteLineAsync($"boot line {i}");
        await errors.WriteLineAsync("fatal: cannot load config");
    }

    private sealed class ObservedStream(Stream inner, Action onClosed) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.WriteAsync(buffer, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                onClosed();
                inner.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
