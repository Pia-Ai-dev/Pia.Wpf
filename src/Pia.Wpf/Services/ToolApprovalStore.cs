using Pia.Services.Interfaces;

namespace Pia.Services;

/// <summary>
/// hermes #16. The per-step sink the unattended gate writes into when it decides to PARK a tool call for a
/// human decision instead of refusing it. One store is created per step turn by the executor, and its
/// lifetime is exactly that step's exchange — the same in-process, non-persisted shape
/// <see cref="StepOutcomeStore"/>, <c>RunSteeringStore</c> and <c>ExecutingRunStore</c> have.
/// <para>
/// ARMED IFF THE RUN MAY PARK. The gate reads <see cref="CanPark"/> and hands it to
/// <c>ToolAutonomy.Resolve</c>; a null store (the background SINGLE-TURN path, which has no run loop to park
/// and no Continue affordance) therefore resolves <c>CanPark: false</c> and keeps today's hard denial
/// byte-for-byte. A store with <see cref="CanPark"/> false does the same, which is how a CHILD run is pinned
/// to default-deny without the gate needing to know what a child is.
/// </para>
/// <para>
/// A parked call is recorded but NOT executed. The step is abandoned, its text discarded and its row put back
/// to <c>Pending</c>, so nothing here is durable — the only thing that survives is the tool NAME, which the
/// orchestrator writes into the run's pause envelope.
/// </para>
/// </summary>
public sealed class ToolApprovalStore
{
    private readonly object _lock = new();

    private readonly ISessionToolGrantStore? _sessionGrants;

    /// <param name="canPark">
    /// False ⇒ this store records nothing and the gate resolves exactly as it did before hermes #16. Passed
    /// rather than assumed so a caller that builds a store still has to state the answer.
    /// </param>
    /// <param name="sessionGrants">
    /// hermes #15. The process-scoped session grants, or null for "this turn has no session tier" — which is
    /// what the background SINGLE-TURN path gets, since it builds no store at all. Carried HERE rather than
    /// injected into <c>BackgroundAssistantTurnRunner</c> on purpose: that file has no notion of root-vs-child,
    /// and an ambient reader there would hand every CHILD run a capability its parent deliberately narrowed
    /// away (<c>HeadlessRunLauncher.CanParkForApproval</c> exists because "a park ACQUIRES authority" — so does
    /// a session grant). Threaded through the same per-step store, the child answer is the safe one for free.
    /// </param>
    public ToolApprovalStore(bool canPark, ISessionToolGrantStore? sessionGrants = null)
    {
        CanPark = canPark;
        _sessionGrants = sessionGrants;
    }

    /// <summary>May this run stop and ask a human? Relayed verbatim into <c>ToolGateInput.CanPark</c>.</summary>
    public bool CanPark { get; }

    /// <summary>
    /// hermes #15. Does the user hold a SESSION grant for this call? Relayed into
    /// <c>ToolGateInput.HasSessionGrant</c> by the unattended gate.
    /// <para>
    /// ARMED ON <see cref="CanPark"/>, which is the whole reason this lives on the store: the session tier
    /// reaches an unattended run exactly where the park does — a ROOT run's real, re-runnable planned step —
    /// and nowhere else. The symmetry is the argument, not a coincidence. Both are ways for a run to use
    /// authority that belongs to a human rather than to its own grant envelope: the park acquires it by asking
    /// now, a session grant by having been asked earlier in the same process. A child run must have neither
    /// (it is a delegate running a strict subset of its parent's grants), and the R10 degrade turn and the
    /// single-turn background path have no step to re-run, so both keep the pre-#15 denial.
    /// </para>
    /// </summary>
    public bool HasSessionGrant(Guid pluginId, string toolName)
        => CanPark && _sessionGrants?.IsGranted(pluginId, toolName) == true;

    /// <summary>
    /// The tool the run is parked on, or null when nothing parked. FIRST call wins — the opposite of
    /// <see cref="StepOutcomeStore.Claim"/>'s last-wins rule, and for a reason that is not symmetry: the pause
    /// envelope names ONE tool, that name is what the Continue card shows the human, and what they are shown
    /// must be the call that actually stopped the run. A later call in the same exchange is a call the model
    /// made AFTER it was told the run was parking, so it cannot be the thing being approved.
    /// </summary>
    public string? PendingToolName { get; private set; }

    /// <summary>How many calls were parked in this step. &gt;1 means the model kept going after being told to
    /// stop; only the first is in the envelope. Count only — a scalar, safe to log.</summary>
    public int ParkedCalls { get; private set; }

    /// <summary>
    /// Record a parked call. Returns true when THIS call is the one the run parked on (the first), false for
    /// any subsequent one — the caller uses that to tell the model "already waiting" instead of announcing a
    /// second park. A blank name is ignored entirely: an envelope naming an empty tool would produce a
    /// Continue card asking the human to approve nothing.
    /// </summary>
    public bool Park(string toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName))
            return false;

        lock (_lock)
        {
            ParkedCalls++;
            if (PendingToolName is not null)
                return false;
            PendingToolName = toolName;
            return true;
        }
    }
}
