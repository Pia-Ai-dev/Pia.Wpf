namespace Pia.Models;

/// <summary>
/// The user's decision for a gated tool call. Cancel is handled as a separate
/// cancellation path (<see cref="System.Threading.Tasks.TaskCompletionSource{T}.TrySetCanceled()"/>),
/// not as a member here.
/// </summary>
public enum ToolDecision
{
    AllowOnce,
    AlwaysAllow,
    Decline
}
