namespace Pia.Shared.Models;

/// <summary>
/// Sync DTO for plugin catalog entries.
/// Plugins are global (admin-managed), not per-user.
/// User preferences (enable/disable) are merged into UserEnabled.
/// </summary>
public class SyncPlugin
{
    public Guid Id { get; set; }

    /// <summary>"mcp_server" | "builtin_tool_pack" | "rest_api"</summary>
    public string Kind { get; set; } = null!;

    public string Name { get; set; } = null!;
    public string? Description { get; set; }
    public string? IconUrl { get; set; }

    /// <summary>JSON: kind-specific configuration manifest.</summary>
    public string ConfigJson { get; set; } = "{}";

    public string Version { get; set; } = "1.0.0";

    /// <summary>True for built-in plugins that ship with the client.</summary>
    public bool IsPreloaded { get; set; }

    /// <summary>Admin-level active state (false = disabled for everyone).</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>User's personal preference (null = use plugin's defaultEnabled from ConfigJson).</summary>
    public bool? UserEnabled { get; set; }

    public DateTime UpdatedAt { get; set; }

    /// <summary>SHA-256 hash of the .cab file, hex-encoded. Null if no cab.</summary>
    public string? CabHash { get; set; }

    /// <summary>Size of the .cab file in bytes. Null if no cab.</summary>
    public long? CabSize { get; set; }
}
