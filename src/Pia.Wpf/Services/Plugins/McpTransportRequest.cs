using Pia.Services.Plugins.Sandbox;

namespace Pia.Services.Plugins;

/// <param name="Sandbox">Null starts the server as an ordinary process; a policy contains it.</param>
/// <param name="IsProbe">A Test connection, which must not share a container with the running server.</param>
public sealed record McpTransportRequest(
    string Name,
    string Command,
    IReadOnlyList<string> Args,
    IReadOnlyDictionary<string, string>? Env,
    string? WorkingDirectory,
    McpSandboxPolicy? Sandbox = null,
    string? ToolPrefix = null,
    bool IsProbe = false);
