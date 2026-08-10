using Pia.Models;

namespace Pia.Services.Interfaces;

/// <summary>
/// THE MIDDLE TIER. Process-scoped tool grants — "allow this tool until Pia closes" — between the one-shot
/// <c>ToolDecision.AllowOnce</c> and the persisted <c>AppSettings.AlwaysAllowedTools</c> standing grant.
/// </summary>
/// <remarks>
/// NOT PERSISTING IS THE FEATURE: the gap it closes is that a user who does not want to answer the same card
/// forty times previously had only a grant that outlives the session and the reason for it. Scope is the APP
/// PROCESS, so a decision taken on a chat card reaches the background run waiting on the same capability.
/// It is not an authority by itself — <c>ToolAutonomy.Resolve</c> honours an entry only for a tool
/// <c>ToolAutonomy.IsSessionGrantOfferable</c> admits, so an entry for a delete-like tool authorizes nothing.
/// </remarks>
public interface ISessionToolGrantStore
{
    /// <summary>
    /// Is this exact (plugin, tool) granted for the remainder of this process? Keyed the same way the
    /// persisted tier is keyed — see <see cref="Grant"/> for the comparer, which is load-bearing.
    /// </summary>
    bool IsGranted(Guid pluginId, string toolName);

    /// <summary>
    /// Record a session grant. Idempotent — a repeat keeps the original timestamp; a blank name is ignored.
    /// The promise is "gone when Pia closes, or when you forget it in settings" (see <see cref="Revoke"/>).
    /// Keyed ORDINAL and CASE-SENSITIVE like <c>ToolPermissionService</c>'s persisted keys, so this tier can
    /// never match a name the standing tier would not.
    /// </summary>
    void Grant(Guid pluginId, string toolName);

    /// <summary>Every live session grant, for the settings surface that lists them.</summary>
    IReadOnlyList<ToolGrant> List();

    /// <summary>Drop a session grant. A blank or unknown name is ignored; strictly narrows what may run.</summary>
    void Revoke(Guid pluginId, string toolName);

    /// <summary>
    /// Raised after a grant or revoke that actually changed the set. Raised outside the store lock, so a
    /// handler can run on whichever thread minted the grant.
    /// </summary>
    event EventHandler? Changed;
}
