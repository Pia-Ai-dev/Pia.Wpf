using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using Pia.Services.Interfaces;
using Pia.Shared.Models;

namespace Pia.Services.Plugins;

public class McpPluginToolHandler : IPluginToolHandler, IDisposable
{
    private readonly ILogger _logger;
    private readonly string _command;
    private readonly string[] _args;

    private McpClient? _client;
    private StdioClientTransport? _transport;
    private IList<McpClientTool> _tools = [];
    private string? _systemPromptAddition;
    private bool _disposed;

    public Guid PluginId { get; }
    public string PluginName { get; private set; }

    public McpPluginToolHandler(
        Guid pluginId,
        string pluginName,
        string command,
        string[] args,
        string? systemPromptAddition,
        ILogger logger)
    {
        PluginId = pluginId;
        PluginName = pluginName;
        _command = command;
        _args = args;
        _systemPromptAddition = systemPromptAddition;
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("MCP plugin {Name}: starting '{Command} {Args}'",
            PluginName, _command, string.Join(" ", _args));
        try
        {
            _transport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = PluginName,
                Command = _command,
                Arguments = _args
            });

            _client = await McpClient.CreateAsync(_transport, cancellationToken: ct);
            _tools = await _client.ListToolsAsync(cancellationToken: ct);

            _logger.LogInformation("MCP plugin {Name} initialized with {ToolCount} tools: {Tools}",
                PluginName, _tools.Count,
                string.Join(", ", _tools.Select(t => t.Name)));

            if (_tools.Count == 0)
                _logger.LogWarning("MCP plugin {Name} initialized but reported 0 tools — process may have failed silently", PluginName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize MCP plugin {Name} ({Command} {Args})",
                PluginName, _command, string.Join(" ", _args));
            _tools = [];
        }
    }

    public IList<AITool> GetTools()
    {
        // McpClientTool inherits from AIFunction (which is AITool)
        return _tools.Cast<AITool>().ToList();
    }

    public string? GetSystemPromptAddition() => _systemPromptAddition;

    public async Task<(object? Result, PluginToolCall? PendingAction)> HandleToolCallAsync(
        FunctionCallContent toolCall, CancellationToken ct = default)
    {
        if (_client is null)
            _logger.LogWarning("MCP plugin {Name}: client is null (not initialized or connection lost), tool {ToolName} will likely fail",
                PluginName, toolCall.Name);

        var tool = _tools.FirstOrDefault(t => t.Name == toolCall.Name);
        if (tool is null)
        {
            _logger.LogWarning("MCP tool '{ToolName}' not found in plugin {Name}. Available tools: [{Available}]",
                toolCall.Name, PluginName,
                string.Join(", ", _tools.Select(t => t.Name)));
            return ($"Tool '{toolCall.Name}' not found in plugin '{PluginName}'.", null);
        }

        try
        {
            var argsJson = toolCall.Arguments is not null
                ? JsonSerializer.Serialize(toolCall.Arguments) : "<null>";
            _logger.LogDebug("MCP tool {ToolName} invocation on plugin {Name}, args: {Args}",
                toolCall.Name, PluginName,
                argsJson.Length > 500 ? argsJson[..500] + "..." : argsJson);

            // McpClientTool.InvokeAsync handles the MCP protocol call internally
            var funcArgs = toolCall.Arguments is not null
                ? new AIFunctionArguments(toolCall.Arguments)
                : null;
            var result = await tool.InvokeAsync(funcArgs, ct);
            var resultText = result?.ToString() ?? "Tool completed with no output.";

            _logger.LogDebug("MCP tool {ToolName} result ({Length} chars): {Preview}",
                toolCall.Name, resultText.Length,
                resultText.Length > 500 ? resultText[..500] + "..." : resultText);

            return (resultText, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MCP tool call {Tool} failed on plugin {Name}",
                toolCall.Name, PluginName);
            return ($"Tool call failed: {ex.Message}", null);
        }
    }

    public Task<object?> ExecutePendingActionAsync(PluginToolCall pendingAction)
    {
        // MCP tools execute immediately — no pending actions
        return pendingAction.Execute();
    }

    public async Task ShutdownAsync()
    {
        if (_client is not null)
        {
            try
            {
                await _client.DisposeAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error disposing MCP client for plugin {Name}", PluginName);
            }
            _client = null;
        }

        _transport = null;

        _tools = [];
    }

    public void ApplyServerMetadata(SyncPlugin plugin)
    {
        PluginName = plugin.Name;
        try
        {
            using var doc = JsonDocument.Parse(plugin.ConfigJson);
            if (doc.RootElement.TryGetProperty("systemPromptAddition", out var spaEl))
                _systemPromptAddition = spaEl.GetString();
        }
        catch { /* ignore parse errors */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ShutdownAsync().GetAwaiter().GetResult();
    }
}
