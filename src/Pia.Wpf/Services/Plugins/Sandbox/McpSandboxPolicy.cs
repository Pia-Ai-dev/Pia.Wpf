namespace Pia.Services.Plugins.Sandbox;

public sealed record McpSandboxPolicy(
    IReadOnlyList<string> ReadwritePaths,
    IReadOnlyList<string> ReadonlyPaths,
    bool AllowNetwork)
{
    public static McpSandboxPolicy Empty { get; } = new([], [], false);
}
