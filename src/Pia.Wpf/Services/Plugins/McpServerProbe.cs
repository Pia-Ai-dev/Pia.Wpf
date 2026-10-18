using ModelContextProtocol.Client;

namespace Pia.Services.Plugins;

public sealed record McpProbeTool(string Name, string? Description, bool ServerDeclaredDestructive);

public sealed record McpProbeResult(bool Success, IReadOnlyList<McpProbeTool> Tools, string? Error)
{
    public static McpProbeResult Failed(string error) => new(false, [], error);
}

/// <summary>Connects, lists tools and shuts the process back down. Separate from
/// <see cref="McpPluginToolHandler"/>, which swallows its failure into a log line — a Test button needs the
/// text.</summary>
public static class McpServerProbe
{
    /// <summary>Generous because the commonest command is <c>npx -y &lt;package&gt;</c>, whose first run downloads
    /// the server before it says anything.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(90);

    public static async Task<McpProbeResult> ProbeAsync(
        LocalMcpDefinition definition,
        IMcpTransportFactory transportFactory,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(definition.Command))
            return McpProbeResult.Failed("No command specified.");

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutSource.CancelAfter(timeout ?? DefaultTimeout);

        McpClient? client = null;
        IClientTransport? transport = null;
        try
        {
            // Runtime detection reads ACLs and walks folders; the caller is usually the UI thread.
            transport = await Task.Run(() => transportFactory.Create(new McpTransportRequest(
                string.IsNullOrWhiteSpace(definition.Name) ? "probe" : definition.Name,
                definition.Command,
                definition.Args,
                definition.Env,
                definition.WorkingDirectory,
                definition.Sandbox,
                definition.ToolPrefix,
                IsProbe: true)), timeoutSource.Token);

            client = await McpClient.CreateAsync(transport, cancellationToken: timeoutSource.Token);
            var tools = await client.ListToolsAsync(cancellationToken: timeoutSource.Token);

            return new McpProbeResult(true,
                [.. tools.Select(t => new McpProbeTool(
                    t.Name,
                    t.Description,
                    McpPluginToolHandler.IsServerDeclaredDestructive(t.ProtocolTool.Annotations)))],
                null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return McpProbeResult.Failed($"The server did not respond within {(timeout ?? DefaultTimeout).TotalSeconds:0} seconds.");
        }
        catch (Exception ex)
        {
            return McpProbeResult.Failed(Describe(ex, transport));
        }
        finally
        {
            if (client is not null)
            {
                try
                {
                    await client.DisposeAsync();
                }
                catch (Exception)
                {
                    // A probe that cannot shut a half-started server down has nothing left to report.
                }
            }
        }
    }

    /// <summary>The SDK puts the server's stderr tail on the inner exception, which is usually the only text
    /// that names the real fault.</summary>
    private static string Describe(Exception ex, IClientTransport? transport)
    {
        var message = McpPluginToolHandler.DescribeFailure(transport, ex);
        for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
        {
            if (!message.Contains(inner.Message, StringComparison.Ordinal))
                message = $"{message} — {inner.Message}";
        }
        return message;
    }
}
