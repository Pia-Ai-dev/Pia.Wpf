using System.IO;
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
    private readonly IReadOnlyDictionary<string, string>? _env;
    private readonly string? _workingDirectory;
    private readonly string? _toolPrefix;
    private readonly IReadOnlyList<string>? _allowedTools;

    private McpClient? _client;
    private StdioClientTransport? _transport;
    private IList<McpClientTool> _tools = [];
    private string? _systemPromptAddition;
    private bool _disposed;

    public Guid PluginId { get; }
    public string PluginName { get; private set; }

    /// <summary>Every tool the server reported, before the allowlist — the edit surface has to offer the
    /// ones currently withheld.</summary>
    public IReadOnlyList<string> DiscoveredToolNames { get; private set; } = [];

    public McpPluginToolHandler(
        Guid pluginId,
        string pluginName,
        string command,
        string[] args,
        string? systemPromptAddition,
        ILogger logger,
        IReadOnlyDictionary<string, string>? env = null,
        string? workingDirectory = null,
        string? toolPrefix = null,
        IReadOnlyList<string>? allowedTools = null)
    {
        PluginId = pluginId;
        PluginName = pluginName;
        _command = command;
        _args = args;
        _systemPromptAddition = systemPromptAddition;
        _logger = logger;
        _env = env;
        _workingDirectory = workingDirectory;
        _toolPrefix = string.IsNullOrWhiteSpace(toolPrefix) ? null : toolPrefix;
        _allowedTools = allowedTools;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("MCP plugin {Name}: starting", PluginName);
        _logger.SensitiveDebug("MCP plugin {Name} command: '{Command} {Args}'",
            PluginName, _command, string.Join(" ", _args));
        if (_env is { Count: > 0 })
            _logger.LogInformation("MCP plugin {Name}: {Count} environment variable(s) set: {Names}",
                PluginName, _env.Count, string.Join(", ", _env.Keys));
        try
        {
            _transport = new StdioClientTransport(BuildTransportOptions(PluginName, _command, _args, _env, _workingDirectory));

            _client = await McpClient.CreateAsync(_transport, cancellationToken: ct);
            var discovered = await _client.ListToolsAsync(cancellationToken: ct);
            DiscoveredToolNames = [.. discovered.Select(t => t.Name)];
            _tools = Project(discovered, _allowedTools, _toolPrefix);

            _logger.LogInformation("MCP plugin {Name} initialized with {ToolCount} of {Discovered} tools: {Tools}",
                PluginName, _tools.Count, discovered.Count,
                string.Join(", ", _tools.Select(t => t.Name)));

            if (discovered.Count == 0)
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

    internal static StdioClientTransportOptions BuildTransportOptions(
        string name,
        string command,
        IEnumerable<string> args,
        IReadOnlyDictionary<string, string>? env,
        string? workingDirectory)
    {
        var options = new StdioClientTransportOptions
        {
            Name = name,
            Command = command,
            Arguments = [.. args]
        };

        if (env is { Count: > 0 })
            options.EnvironmentVariables = env.ToDictionary(e => e.Key, e => (string?)e.Value);

        if (!string.IsNullOrWhiteSpace(workingDirectory) && Directory.Exists(workingDirectory))
            options.WorkingDirectory = workingDirectory;

        return options;
    }

    /// <summary>A null allowlist is "every tool"; an empty one is "none", which is a server the user has
    /// deliberately muted rather than a missing setting.</summary>
    internal static bool IsExposed(string toolName, IReadOnlyList<string>? allowedTools) =>
        allowedTools is null || allowedTools.Contains(toolName, StringComparer.Ordinal);

    /// <summary>The prefix is what keeps a local server's <c>read_file</c> off the built-in of the same name.
    /// <c>WithName</c> leaves the wire call on the server's own name.</summary>
    internal static string ExposedName(string toolName, string? toolPrefix) =>
        string.IsNullOrEmpty(toolPrefix) ? toolName : $"{toolPrefix}__{toolName}";

    /// <summary>The single filter: <see cref="GetTools"/> feeds both tool routing and the grant catalogue, so a
    /// withheld tool is unreachable and uncatalogued without a second list to keep in step.</summary>
    internal static IList<McpClientTool> Project(
        IEnumerable<McpClientTool> discovered,
        IReadOnlyList<string>? allowedTools,
        string? toolPrefix) =>
        [.. discovered
            .Where(tool => IsExposed(tool.Name, allowedTools))
            .Select(tool => toolPrefix is null ? tool : tool.WithName(ExposedName(tool.Name, toolPrefix)))];

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
            // The ONLY producer of the flag — the card's warning and every rule that reads delete-likeness
            // take it from this record.
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
    /// Does the server DECLARE this tool destructive? The hint widens
    /// <c>ToolPermissionService.IsDeleteLike</c>, which excludes the tool from the session tier, from the
    /// autonomy policy and from the unattended park. It cannot narrow anything.
    /// </summary>
    /// <remarks>
    /// That direction is not a preference: the MCP type's own remarks warn against making tool-use decisions on
    /// annotations from untrusted servers, and every MCP server here is a stdio subprocess with full user
    /// privileges. Only an EXPLICIT <c>DestructiveHint == true</c> counts, deliberately not the spec's
    /// "null ⇒ assume true" default — most servers send no annotations, so that default would reclassify every
    /// tool of every annotation-less server as destructive. <c>ReadOnlyHint</c> is read and cannot move this
    /// answer, since <c>true</c> is exactly the self-declaration of safety that must not be honoured.
    /// </remarks>
    internal static bool IsServerDeclaredDestructive(ToolAnnotations? annotations) =>
        annotations?.DestructiveHint == true;

    /// <summary>The same answer per tool name and BEFORE any call, so a grant surface can narrow what it offers
    /// to match. An unknown name reads as "no hint", never as a declaration.</summary>
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
