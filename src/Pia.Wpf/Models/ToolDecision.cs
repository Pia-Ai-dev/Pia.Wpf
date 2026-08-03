namespace Pia.Models;

/// <summary>
/// The user's decision for a gated tool call. Cancel is handled as a separate
/// cancellation path (<see cref="System.Threading.Tasks.TaskCompletionSource{T}.TrySetCanceled()"/>),
/// not as a member here.
/// <para>
/// NOT persisted and not serialized anywhere (the persisted audit vocabulary is
/// <see cref="ToolGateDecision"/>, which IS append-only). A new member is nevertheless APPENDED rather than
/// inserted, because the sole consumer switch fails closed on anything it does not recognize
/// (<c>ChatSession.HandleToolCall</c>'s <c>default:</c> arm is the DECLINE arm) and a reordering would be a
/// silent change of meaning for no gain.
/// </para>
/// </summary>
public enum ToolDecision
{
    AllowOnce,
    AlwaysAllow,
    Decline,

    /// <summary>
    /// hermes #15, the middle tier: run it now AND for the rest of this app session, without writing anything
    /// to <c>AppSettings.AlwaysAllowedTools</c>. Honoured only for a tool
    /// <c>ToolAutonomy.IsSessionGrantOfferable</c> admits; on anything else the gate degrades it to
    /// <see cref="AllowOnce"/> (execute once, record no grant) exactly as it already does for
    /// <see cref="AlwaysAllow"/>.
    /// </summary>
    AllowForSession
}
