using Pia.Models;

namespace Pia.Services.Interfaces;

/// <summary>
/// Owns the voice-mode auto-approve allowlist and the persisted per-(PluginId, ToolName) "always allow"
/// grants. Singleton.
/// </summary>
public interface IToolPermissionService
{
    /// <summary>
    /// Deny-by-default: true only for the curated create-only allowlist, which authorizes on VOICE alone.
    /// Read at the gate, never trusted from the card.
    /// </summary>
    bool IsAutoApproveEligible(string toolName);

    /// <summary>True if the user has a standing grant for this exact (plugin, tool).</summary>
    bool IsGranted(Guid pluginId, string toolName);

    /// <summary>
    /// True if the user granted this exact (plugin, tool) for the rest of THIS APP SESSION — the middle tier,
    /// held in the process-scoped <see cref="ISessionToolGrantStore"/> and never persisted. Independent of
    /// <see cref="IsGranted"/>: neither implies the other, and only one of them has a durable row.
    /// </summary>
    bool IsGrantedForSession(Guid pluginId, string toolName);

    /// <summary>
    /// Record a session grant for this exact (plugin, tool). Writes NOTHING to
    /// <c>AppSettings.AlwaysAllowedTools</c> — that is the point of the tier — so it is synchronous and cannot
    /// fail. The caller must still have checked <see cref="ToolAutonomy.IsSessionGrantOfferable"/>; the store
    /// holds keys and enforces no policy.
    /// </summary>
    void GrantForSession(Guid pluginId, string toolName);

    /// <summary>Every live session grant, for the settings surface that lists all tiers in one place.</summary>
    IReadOnlyList<ToolGrant> ListSessionGrants();

    /// <summary>Forget a session grant. Synchronous and durable-state-free, like <see cref="GrantForSession"/>.</summary>
    void RevokeSessionGrant(Guid pluginId, string toolName);

    Task GrantAsync(Guid pluginId, string toolName);

    Task RevokeAsync(Guid pluginId, string toolName);

    IReadOnlyList<ToolGrant> List();

    /// <summary>
    /// Raised after a grant, revoke, or external settings change, in either tier — so a subscriber may be
    /// called on a run thread, which is where a session grant is minted.
    /// </summary>
    event EventHandler? Changed;
}
