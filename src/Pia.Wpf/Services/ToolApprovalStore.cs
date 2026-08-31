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
/// A parked call is recorded but NOT executed: the step is abandoned, its text discarded and its row put back
/// to <c>Pending</c>. What outlives the step is what the executor drains out of here — the tool name into the
/// run's pause envelope, and <see cref="RecordedCalls"/> into the replayable exchange rows.
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
    /// <param name="isTopLevelUserRun">
    /// The run is the one a person started from the composer and is not a delegate. Resolved by the executor
    /// from the run row, never from a tool name; relayed into <c>ToolGateInput.IsTopLevelUserRun</c>, which is
    /// what lets the park ask about a delete-like tool rather than refusing outright.
    /// </param>
    public ToolApprovalStore(bool canPark, ISessionToolGrantStore? sessionGrants = null, bool isTopLevelUserRun = false)
    {
        CanPark = canPark;
        _sessionGrants = sessionGrants;
        IsTopLevelUserRun = isTopLevelUserRun;
    }

    /// <summary>May this run stop and ask a human? Relayed verbatim into <c>ToolGateInput.CanPark</c>.</summary>
    public bool CanPark { get; }

    /// <summary>Is somebody expected at the machine for THIS run? Relayed into
    /// <c>ToolGateInput.IsTopLevelUserRun</c>.</summary>
    public bool IsTopLevelUserRun { get; }

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
    /// What the parked calls asked to act on, in call order, for the affordances to render. Accumulated across
    /// every parked call of the SAME tool: a model that asks to delete four files issues four calls in one
    /// round, and a card naming only the first would understate what Continue is about to allow.
    /// User content — render it, never log it.
    /// </summary>
    public IReadOnlyList<string> PendingToolArguments => _pendingArguments;

    private readonly List<string> _pendingArguments = new();

    /// <summary>
    /// Record a parked call. Returns true when THIS call is the one the run parked on (the first), false for
    /// any subsequent one — the caller uses that to tell the model "already waiting" instead of announcing a
    /// second park. A blank name is ignored entirely: an envelope naming an empty tool would produce a
    /// Continue card asking the human to approve nothing.
    /// </summary>
    public bool Park(string toolName, string? arguments = null)
    {
        if (string.IsNullOrWhiteSpace(toolName))
            return false;

        lock (_lock)
        {
            ParkedCalls++;
            var first = PendingToolName is null;
            if (first)
                PendingToolName = toolName;

            // Only this tool's own calls. A LATER call of a DIFFERENT tool was withheld, not parked, and
            // listing its target under this tool's name would misdescribe what Continue allows.
            if (!string.IsNullOrWhiteSpace(arguments)
                && string.Equals(PendingToolName, toolName, StringComparison.OrdinalIgnoreCase)
                && !_pendingArguments.Contains(arguments, StringComparer.Ordinal))
            {
                _pendingArguments.Add(arguments);
            }

            return first;
        }
    }

    /// <summary>Parked and withheld calls per step, past which a record is dropped whole.</summary>
    public const int MaxRecordedCalls = 8;

    /// <summary>Serialized argument chars per record, past which it is dropped whole.</summary>
    public const int MaxRecordedArgumentChars = 1_048_576;

    /// <summary>One gate-side call, DETOKENIZED (the gate sits below <c>TokenizingAiClientService</c>'s wrapper):
    /// <c>ArgumentsJson</c> is verbatim and replayable, <c>DisplayArgs</c> the capped line the surfaces render.</summary>
    /// <param name="Withheld">Withheld behind an earlier park rather than the call that parked the run.</param>
    public sealed record ParkedCall(
        string ToolName,
        string? CallId,
        int Round,
        Guid? PluginId,
        string? ArgumentsJson,
        string? DisplayArgs,
        bool Withheld);

    /// <summary>Every parked and withheld call of this step, in call order — the rows a Continue press replays.
    /// User content: persist it, never log it.</summary>
    public IReadOnlyList<ParkedCall> RecordedCalls => _recordedCalls;

    /// <summary>Records refused by a cap. Scalar, safe to log.</summary>
    public int DroppedRecords { get; private set; }

    private readonly List<ParkedCall> _recordedCalls = new();

    /// <summary>
    /// Keep a parked or withheld call for the replay. Over either cap the record is dropped WHOLE rather than
    /// truncated: half a payload is unreplayable.
    /// </summary>
    public void Record(ParkedCall call)
    {
        lock (_lock)
        {
            if (_recordedCalls.Count >= MaxRecordedCalls
                || call.ArgumentsJson?.Length > MaxRecordedArgumentChars)
            {
                DroppedRecords++;
                return;
            }

            _recordedCalls.Add(call);
        }
    }
}
