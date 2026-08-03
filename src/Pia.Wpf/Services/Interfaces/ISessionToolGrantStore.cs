namespace Pia.Services.Interfaces;

/// <summary>
/// hermes #15, THE MIDDLE TIER. The process-scoped tool grants: "allow this tool until Pia closes", sitting
/// between the one-shot <c>ToolDecision.AllowOnce</c> and the persisted <c>AppSettings.AlwaysAllowedTools</c>
/// standing grant. Singleton, in-process, and deliberately <b>never serialized</b> — the same shape
/// <c>ToolApprovalStore</c>, <c>StepOutcomeStore</c>, <c>RunSteeringStore</c> and <c>ExecutingRunStore</c>
/// have.
/// <para>
/// NOT PERSISTING IS THE FEATURE, not an omission. The gap this closes is that a user who does not want to
/// answer the same card forty times had only one alternative: a grant that outlives the session, the app
/// restart and the reason they granted it. So there is no save path here, no settings member, and no
/// migration — a grant is gone when the process is.
/// </para>
/// <para>
/// SCOPE = THE APP PROCESS, not the chat and not the run. A per-chat scope would barely beat AllowOnce (a new
/// chat is one click away and the model reopens tools per chat), and a per-run scope already exists as the
/// run's own grant envelope (<c>HeadlessRunLauncher</c>'s <c>grantedWrites</c>). "This session" is what a user
/// means by "until I close the app", and only a process scope lets a decision taken in a chat card reach the
/// background run that is waiting on the same capability.
/// </para>
/// <para>
/// It is NOT an authority by itself. Both gates read it through <c>ToolAutonomy.Resolve</c>, which evaluates
/// the destructive-external FLOOR first and honours a session grant only for a tool
/// <c>ToolAutonomy.IsSessionGrantOfferable</c> admits — so a store entry for a delete-like tool authorizes
/// nothing.
/// </para>
/// </summary>
public interface ISessionToolGrantStore
{
    /// <summary>
    /// Is this exact (plugin, tool) granted for the remainder of this process? Keyed the same way the
    /// persisted tier is keyed — see <see cref="Grant"/> for the comparer, which is load-bearing.
    /// </summary>
    bool IsGranted(Guid pluginId, string toolName);

    /// <summary>
    /// Record a session grant. Idempotent, and there is deliberately no revoke: the only way out is closing
    /// the app, which is exactly the promise the button makes. A blank tool name is ignored — a grant on an
    /// empty name would be a wildcard nothing could see.
    /// <para>
    /// Keyed on <c>(PluginId, ToolName)</c> with ORDINAL, CASE-SENSITIVE name equality — identical to
    /// <c>ToolPermissionService</c>'s persisted grant keys, so this tier can never match a name the standing
    /// tier would not. (The run-level <c>grantedWrites</c> set is <c>OrdinalIgnoreCase</c>; that third
    /// comparer is documented on <c>ToolGateInput</c> and is not changed here.)
    /// </para>
    /// </summary>
    void Grant(Guid pluginId, string toolName);
}
