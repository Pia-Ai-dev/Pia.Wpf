namespace Pia.Models;

/// <summary>
/// The user's decision for a gated tool call; cancel is a separate cancellation path, not a member here. Not
/// persisted, but still APPEND-only: <c>ChatSession.HandleToolCall</c>'s <c>default:</c> arm is the DECLINE
/// arm, so a reorder would silently change meaning.
/// </summary>
public enum ToolDecision
{
    AllowOnce,
    AlwaysAllow,
    Decline,

    /// <summary>
    /// The middle tier: run it now AND for the rest of this app session, without writing anything to
    /// <c>AppSettings.AlwaysAllowedTools</c>. Offered on every card, like <see cref="AlwaysAllow"/>.
    /// </summary>
    AllowForSession
}
