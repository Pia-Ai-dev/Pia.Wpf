using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
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
            // T2-7b: the server's own destructive declaration, read off the tool the lookup above resolved.
            // This is the ONLY producer of the flag — everything downstream (the gate's floor, the card's
            // warning, both grant-offer rules) reads it from the record.
            ServerDeclaredDestructive: IsServerDeclaredDestructive(tool.ProtocolTool.Annotations),
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

    /// <summary>
    /// T2-7b — does the server DECLARE this tool destructive? Consumes
    /// <c>ToolAnnotations.DestructiveHint</c>/<c>ReadOnlyHint</c> in the MORE-RESTRICTED DIRECTION ONLY.
    /// <para>
    /// The direction is not a preference: the MCP type's own remarks say "clients should never make tool use
    /// decisions based on <c>ToolAnnotations</c> received from untrusted servers", and every MCP server here is
    /// a stdio subprocess running with full user privileges outside <c>SafeFolderPath</c> entirely
    /// (<c>17-trust-model.md</c> §2). A hint that can only ever ADD friction is safe to believe; one that could
    /// remove it would let a server declare itself safe.
    /// </para>
    /// <para>
    /// <b>Only an EXPLICIT <c>DestructiveHint == true</c> counts — deliberately NOT the spec's
    /// "null ⇒ assume true" default.</b> Most servers send no annotations at all, so <c>Annotations</c> is null
    /// and every hint reads null; honouring the spec default here would reclassify EVERY tool of EVERY
    /// annotation-less server as delete-like, which refuses all unattended MCP outright
    /// (<c>ToolAutonomy</c>'s floor) and suppresses interactive auto-approval across the board. That is a
    /// different and much larger policy decision than "consume the hint", and it would make the safe direction
    /// unusable in practice.
    /// </para>
    /// <para>
    /// <b><c>ReadOnlyHint</c> is read and cannot move this answer.</b> That is the consumption, not an omission:
    /// <c>false</c> ("I modify my environment") tells us nothing new — Pia already defers EVERY MCP call to the
    /// write gate — and <c>true</c> is precisely the self-declaration of safety that must not be honoured, so a
    /// server sending <c>readOnlyHint: true</c> beside a destructive name or a <c>destructiveHint: true</c>
    /// changes nothing. Pinned by <c>McpToolAnnotationHintTests</c> rather than left to this paragraph.
    /// </para>
    /// </summary>
    internal static bool IsServerDeclaredDestructive(ToolAnnotations? annotations) =>
        annotations?.DestructiveHint == true;

    /// <summary>The same answer as above, per tool name and BEFORE any call, so a grant surface cannot offer a
    /// tier the gate's floor would then ignore. An unknown name reads as "no hint", never as a declaration.</summary>
    public bool DeclaresDestructive(string toolName) =>
        IsServerDeclaredDestructive(
            _tools.FirstOrDefault(t => t.Name == toolName)?.ProtocolTool.Annotations);

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
