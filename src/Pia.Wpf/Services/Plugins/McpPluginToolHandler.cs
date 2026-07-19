using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using Pia.Logging;
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
        _logger.LogInformation("MCP plugin {Name}: starting", PluginName);
        _logger.SensitiveDebug("MCP plugin {Name} command: '{Command} {Args}'",
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
            _logger.LogError(ex, "Failed to initialize MCP plugin {Name}", PluginName);
            _logger.SensitiveDebug("Failed plugin {Name} command was: '{Command} {Args}'",
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

    public Task<(object? Result, PluginToolCall? PendingAction)> HandleToolCallAsync(
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
            return Task.FromResult<(object?, PluginToolCall?)>(
                ($"Tool '{toolCall.Name}' not found in plugin '{PluginName}'.", null));
        }

        // Phase-2 MCP gate: MCP is stdio and cannot be classified read-vs-write, so it no longer runs
        // inline. Return a DEFERRED PluginToolCall so every MCP call flows through the same gate as a
        // built-in write — the interactive action card, or (unattended) the write-grant gate. The actual
        // InvokeAsync happens only inside Execute(), after approval/grant.
        var pending = new PluginToolCall(
            ToolName: toolCall.Name,
            PluginId: PluginId,
            PluginName: PluginName,
            Description: $"{PluginName}: {toolCall.Name}",
            Details: toolCall.Arguments is { Count: > 0 }
                ? JsonSerializer.Serialize(toolCall.Arguments)
                : null,
            Execute: async () =>
            {
                _logger.SensitiveDebug("MCP tool {ToolName} invocation on plugin {Name}, args: {Args}",
                    toolCall.Name,
                    PluginName,
                    toolCall.Arguments is not null
                        ? TruncateText(JsonSerializer.Serialize(toolCall.Arguments), 500)
                        : "<null>");
                try
                {
                    // McpClientTool.InvokeAsync handles the MCP protocol call internally.
                    var funcArgs = toolCall.Arguments is not null
                        ? new AIFunctionArguments(toolCall.Arguments)
                        : null;
                    var result = await tool.InvokeAsync(funcArgs, ct);
                    var resultText = result?.ToString() ?? "Tool completed with no output.";

                    _logger.SensitiveDebug("MCP tool {ToolName} result ({Length} chars): {Preview}",
                        toolCall.Name, resultText.Length, TruncateText(resultText, 500));

                    return resultText;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "MCP tool call {Tool} failed on plugin {Name}",
                        toolCall.Name, PluginName);
                    return $"Tool call failed: {ex.Message}";
                }
            });

        return Task.FromResult<(object?, PluginToolCall?)>((null, pending));
    }

    public Task<object?> ExecutePendingActionAsync(PluginToolCall pendingAction)
    {
        // The deferred call built in HandleToolCallAsync — runs the real MCP invocation now that the
        // gate (approval or grant) has cleared it.
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

    private static string TruncateText(string value, int max)
        => value.Length > max ? value[..max] + "..." : value;
}
