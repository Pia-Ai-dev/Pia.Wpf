using System.IO;

namespace Pia.Services.Plugins.Sandbox;

public static class SandboxCommandRules
{
    private static readonly HashSet<string> ShimNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "npx", "npm", "pnpm", "pnpx", "yarn", "uvx", "bunx", "pipx"
    };

    private static readonly HashSet<string> ScriptExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cmd", ".bat", ".ps1"
    };

    /// <summary>Package-runner shims and script launchers fetch and run code from caches the container cannot reach.</summary>
    public static bool IsShimCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return false;

        var file = Path.GetFileName(command.Trim().Trim('"'));
        return ScriptExtensions.Contains(Path.GetExtension(file))
            || ShimNames.Contains(Path.GetFileNameWithoutExtension(file));
    }
}
