using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Services;

public sealed class TestMcpServerFixtureTests(TestMcpServerFixtureTests.Session session)
    : IClassFixture<TestMcpServerFixtureTests.Session>
{
    // The server exits on stdin EOF, but the client transport waits out its whole shutdown budget before it
    // looks, so one shared process keeps the class at one wait instead of one per test.
    public sealed class Session : IAsyncLifetime
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "pia-testmcp-" + Guid.NewGuid().ToString("N"));

        public McpClient Client { get; private set; } = null!;

        public async ValueTask InitializeAsync()
        {
            Directory.CreateDirectory(Root);
            var transport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = "test-mcp",
                Command = TestMcpServerFixture.ExePath,
                WorkingDirectory = Root,
                ShutdownTimeout = TimeSpan.FromSeconds(1),
            });
            Client = await McpClient.CreateAsync(transport, cancellationToken: TestContext.Current.CancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            TempPath.Remove(Root);
        }
    }

    [Fact]
    public void FixtureExeIsStagedUnderTheTestBin()
        => Assert.True(File.Exists(TestMcpServerFixture.ExePath), TestMcpServerFixture.ExePath);

    [Fact]
    public async Task ListsTheFileToolsAndTheEgressProbe()
    {
        var tools = await session.Client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(
            ["delete_directory", "delete_file", "export", "get_server_info", "list_directory", "read_file", "write_file"],
            tools.Select(t => t.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task WritesListsReadsAndDeletesAFileWithoutAnyPathCheck()
    {
        var file = Path.Combine(session.Root, "roundtrip", "note.txt");

        var write = await CallAsync("write_file", new() { ["path"] = file, ["content"] = "hello" });
        var list = await CallAsync("list_directory", new() { ["path"] = Path.GetDirectoryName(file) });
        var read = await CallAsync("read_file", new() { ["path"] = file });
        var delete = await CallAsync("delete_file", new() { ["path"] = file });

        Assert.All(new[] { write, list, read, delete }, r => Assert.NotEqual(true, r.IsError));
        Assert.Contains("[F] note.txt (5 bytes)", Text(list));
        Assert.Equal("hello", Text(read));
        Assert.False(File.Exists(file));
    }

    [Fact]
    public async Task ReportsFailuresAsAnErrorResultWithTheExceptionType()
    {
        var missing = Path.Combine(session.Root, "missing.txt");

        var read = await CallAsync("read_file", new() { ["path"] = missing });
        var delete = await CallAsync("delete_file", new() { ["path"] = missing });

        Assert.True(read.IsError);
        Assert.StartsWith("ERROR FileNotFoundException: ", Text(read));
        Assert.True(delete.IsError);
        Assert.StartsWith("ERROR FileNotFoundException: ", Text(delete));
    }

    [Fact]
    public async Task DeletesADirectoryOnlyRecursivelyWhenAsked()
    {
        var dir = Path.Combine(session.Root, "tree");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "a.txt"), "a", TestContext.Current.CancellationToken);

        var refused = await CallAsync("delete_directory", new() { ["path"] = dir });
        var deleted = await CallAsync("delete_directory", new() { ["path"] = dir, ["recursive"] = true });

        Assert.True(refused.IsError);
        Assert.NotEqual(true, deleted.IsError);
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public async Task ExportPostsTheFileContentToTheUrl()
    {
        var file = Path.Combine(session.Root, "payload.txt");
        await File.WriteAllTextAsync(file, "secret-payload", TestContext.Current.CancellationToken);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var received = AcceptOnePostAsync(listener);
        var url = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/import";

        var export = await CallAsync("export", new() { ["url"] = url, ["path"] = file });

        Assert.NotEqual(true, export.IsError);
        Assert.Contains("HTTP 204", Text(export));
        Assert.Equal("secret-payload", await received);
    }

    [Fact]
    public async Task ExportToAnUnreachableHostNamesTheSocketFailure()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var export = await CallAsync("export", new() { ["url"] = $"http://127.0.0.1:{port}/" });

        Assert.True(export.IsError);
        Assert.StartsWith("ERROR HttpRequestException: ", Text(export));
        Assert.Contains("(inner SocketException: ", Text(export));
    }

    private async Task<CallToolResult> CallAsync(string tool, Dictionary<string, object?> arguments)
        => await session.Client.CallToolAsync(tool, arguments, cancellationToken: TestContext.Current.CancellationToken);

    private static string Text(CallToolResult result)
        => string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text));

    private static async Task<string> AcceptOnePostAsync(TcpListener listener)
    {
        using var peer = await listener.AcceptTcpClientAsync();
        var stream = peer.GetStream();
        var buffer = new List<byte>();
        var chunk = new byte[1024];
        var headerEnd = -1;
        while (headerEnd < 0)
        {
            var read = await stream.ReadAsync(chunk);
            buffer.AddRange(chunk.AsSpan(0, read).ToArray());
            headerEnd = Encoding.ASCII.GetString([.. buffer]).IndexOf("\r\n\r\n", StringComparison.Ordinal);
        }

        var head = Encoding.ASCII.GetString([.. buffer]);
        var length = int.Parse(head.Split("\r\n").First(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)).Split(':')[1]);
        while (buffer.Count < headerEnd + 4 + length)
        {
            var read = await stream.ReadAsync(chunk);
            buffer.AddRange(chunk.AsSpan(0, read).ToArray());
        }

        await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 204 No Content\r\nConnection: close\r\n\r\n"));
        return Encoding.UTF8.GetString([.. buffer.Skip(headerEnd + 4).Take(length)]);
    }
}
