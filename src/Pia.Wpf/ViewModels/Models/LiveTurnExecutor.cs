using Microsoft.Extensions.AI;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;

namespace Pia.ViewModels.Models;

/// <summary>
/// Interactive act-step executor bound to a live <see cref="ChatSession"/>. Constructed on the UI
/// thread by <c>ChatSessionManager</c>; captures the UI <see cref="SynchronizationContext"/> at
/// construction and <c>Post</c>s each step onto it, awaiting the result off-thread (§13.1/§B.4).
/// Streams into the transcript via the session's existing machinery (action cards + gate unchanged).
/// The orchestrator drives it thread-agnostically.
/// </summary>
public sealed class LiveTurnExecutor : IAgentTurnExecutor
{
    private readonly ChatSession _session;
    private readonly SynchronizationContext _ui;
    private readonly Func<ChatSession, bool> _isActive;
    private readonly PersonaAttribution _persona;
    private readonly AiProvider _provider;
    private readonly AssistantTurnSetup _turnSetup;
    private readonly bool _tokenizationEnabled;
    private readonly RunAutonomyPolicy? _policy;
    private readonly IAgentTimelineService? _timeline;
    private readonly string? _workspaceRoot;

    /// <summary>
    /// Per-step persona/provider/prompt resolution (Batch 07 G6); null ⇒ every step runs on the run default,
    /// i.e. the pre-Batch-07 behaviour even for a step that carries an <c>AssignedPersonaId</c>.
    /// </summary>
    private readonly StepPersonaResolver? _stepPersonas;

    /// <summary>
    /// The run-level triple the resolver falls back to, or null when this executor was built without a run
    /// persona (then no step ever resolves and <see cref="BuildSpec"/> reads the fields as before).
    /// <para>
    /// It exists because <see cref="_persona"/> is a <see cref="PersonaAttribution"/> — a projection, not the
    /// <see cref="Persona"/> — and the resolver's fallback contract is "hand back the very triple you were
    /// given". The manager passes the same persona object it projected the attribution from, so the two cannot
    /// disagree.
    /// </para>
    /// </summary>
    private readonly StepPersonaSetup? _runDefault;

    /// <param name="policy">The run's autonomy policy (Batch 04); null ⇒ no per-run policy, today's
    /// behaviour. Trailing and defaulted on purpose: this type is hand-constructed with a POSITIONAL argument
    /// list, so a required parameter would force edits into every existing call site and test.</param>
    /// <param name="timeline">The audit-timeline store (Batch 03); null ⇒ this run records nothing. Trailing
    /// and defaulted for the same reason as <paramref name="policy"/>.</param>
    /// <param name="workspaceRoot">The run's isolated workspace root (Batch 06 D4), as provisioned by
    /// <c>ChatSessionManager</c>; null ⇒ no isolation, i.e. the steps write into the assistant files folder
    /// exactly as they did before Batch 06 (the degrade every provisioning fault takes). Non-null confines
    /// every read, write, delete, list and search this run performs to that directory and is the normal case.
    /// Trailing and defaulted for the same reason as <paramref name="policy"/>.</param>
    /// <param name="stepPersonas">Per-step persona resolution (Batch 07 G6); null ⇒ every step runs on the run
    /// persona, today's behaviour. Trailing and defaulted for the same reason as <paramref name="policy"/>.</param>
    /// <param name="runPersona">The run persona as a whole <see cref="Persona"/>, which
    /// <paramref name="persona"/> is only a projection of. Required for per-step resolution and for nothing
    /// else: null ⇒ no step is ever resolved, whatever <paramref name="stepPersonas"/> says. Pass the same
    /// object <paramref name="persona"/> was projected from.</param>
    public LiveTurnExecutor(
        ChatSession session,
        Func<ChatSession, bool> isActive,
        PersonaAttribution persona,
        AiProvider provider,
        AssistantTurnSetup turnSetup,
        bool tokenizationEnabled,
        RunAutonomyPolicy? policy = null,
        IAgentTimelineService? timeline = null,
        string? workspaceRoot = null,
        StepPersonaResolver? stepPersonas = null,
        Persona? runPersona = null)
    {
        _session = session;
        _ui = SynchronizationContext.Current
              ?? throw new InvalidOperationException("LiveTurnExecutor must be constructed on the UI thread.");
        _isActive = isActive;
        _persona = persona;
        _provider = provider;
        _turnSetup = turnSetup;
        _tokenizationEnabled = tokenizationEnabled;
        _policy = policy;
        _timeline = timeline;
        _workspaceRoot = workspaceRoot;
        _stepPersonas = stepPersonas;
        _runDefault = runPersona is null ? null : new StepPersonaSetup(runPersona, provider, turnSetup);
    }

    public Task BeginRunAsync(AgentRun run, RunContext ctx, CancellationToken ct) =>
        PostAsync(() =>
        {
            // The manager pre-added an empty streaming placeholder AssistantMessage; remove it so the
            // transcript starts as [user: goal] and each step adds its own persona-attributed reply.
            var placeholder = _session.Messages.LastOrDefault(m => !m.IsUser && m.IsStreaming);
            if (placeholder is not null)
                _session.Messages.Remove(placeholder);
            // Batch 06 D4: an isolated interactive run's steps write here, not into the assistant files
            // folder. This assignment is also what makes PROMOTION executor-agnostic — the orchestrator's
            // SafePromote early-returns on an empty ctx.WorkspaceRoot, so without this line an interactive
            // run would isolate and then never promote.
            ctx.WorkspaceRoot = _workspaceRoot;

            // The chat's working subpath is what narrows this run's file sandbox (ChatSession passes it as
            // TaskContext.WorkingSubpath per step), but that ambient never reaches the orchestrator thread —
            // hand it to the context here so the verifier's artifact probe stats the root the steps really
            // wrote into. Read on the UI thread, where WorkingDirectory is owned.
            // An isolated run's workspace root IS the already-narrowed source root (B6: it was provisioned
            // FROM <folder>\<subpath>), so narrowing a SECOND time would probe <runRoot>\<subpath>, which
            // does not exist. Stated as an explicit assignment rather than left to ResolveEffectiveRoot's
            // fail-safe fallback — the same shape HeadlessTurnExecutor uses at its own BeginRunAsync.
            ctx.WorkingSubpath = _workspaceRoot is null ? _session.WorkingDirectory : null;
            // The session is already Running (the manager flipped it before dispatch); stays Running
            // across all steps (§16 R12).
            return Task.CompletedTask;
        });

    public async Task<StepTurnResult> ExecuteStepAsync(AgentRun run, AgentStep step, RunContext ctx, CancellationToken ct)
    {
        // Batch 07 G6, and the placement is the point: resolve BEFORE the Post, not inside it. ResolveAsync
        // awaits IPersonaService/IProviderService/ISettingsService I/O, and PostAsync marshals onto the captured
        // UI SynchronizationContext — resolving inside would put a settings read and two store reads on the
        // dispatcher for every step. This method is called from the orchestrator's loop, already off the UI
        // thread, so the await here costs nothing the UI can feel.
        var setup = _stepPersonas is null || _runDefault is null
            ? null
            : await _stepPersonas.ResolveAsync(step.AssignedPersonaId, _runDefault, _tokenizationEnabled, ct);

        return await PostAsync(async () =>
        {
            var result = await _session.RunStepTurnAsync(BuildSpec(run, step.Ordinal, step.Intent ?? string.Empty,
                // hermes #9: the ONE entry point whose result becomes an AgentStep's Done/Failed, so the one
                // that offers emit_step_result. Headless draws the same line at the same place.
                step.ExpectedArtifact, useGoalVerbatim: false, stepId: step.Id, setup,
                offerStepResultTool: true), ctx, ct);

            // E2 (parity with HeadlessTurnExecutor's per-step write): make this step's assistant message
            // DURABLE now. The interactive path otherwise persists only via TurnCompleted → the manager's
            // PersistAsync, which nothing but the terminal EndRunAsync raises — so a run parked at its
            // budget (OnPausedAsync raises nothing, by design) left the stored chat holding just the goal,
            // and a crash mid-run lost every step reply. Persist-ONLY: no terminal settle, no
            // TurnCompleted — a parked or mid-flight run is not finished. Runs on the UI thread inside
            // this Post (the manager's PersistAsync snapshots Messages synchronously, so it cannot race
            // the next step's streaming) and swallows its own faults (guardrail 1).
            _session.RequestPersist();
            return result;
        });
    }

    // No interim persist here (same call as HeadlessTurnExecutor's fallback): the R10 degrade path runs
    // EndRunAsync immediately on every branch, and that raises TurnCompleted → the manager persists.
    public Task<StepTurnResult> RunSingleTurnFallbackAsync(AgentRun run, RunContext ctx, CancellationToken ct) =>
        // stepId: null — the R10 degrade turn belongs to the run but to no step.
        PostAsync(() => _session.RunStepTurnAsync(
            BuildSpec(run, 0, ctx.Goal, null, useGoalVerbatim: true, stepId: null), ctx, ct));

    public Task EndRunAsync(AgentRun run, RunContext ctx, bool cancelled, bool failed, CancellationToken ct) =>
        PostAsync(() =>
        {
            // Per-run terminal finalize mirror (§13.5 step 2 / §16 R4): dispose the session CTS, settle
            // terminal state, raise TurnCompleted — the equivalents RunTurnAsync runs inline per turn.
            _session.DisposeCts();

            // A cancelled OR failed run never counts as producing content: a Failed step's catch handler
            // writes error text (e.g. "Error: boom") into its assistant message, so keying purely off the
            // last message's Content would settle a Failed run as Completed/Succeeded (§13.5.2/§16 R4).
            var lastAssistant = _session.Messages.LastOrDefault(m => !m.IsUser);
            var producedContent = !cancelled && !failed && !string.IsNullOrEmpty(lastAssistant?.Content);
            if (_session.State != ChatState.Error)
            {
                _session.SetState(producedContent && !_isActive(_session)
                    ? ChatState.Completed
                    : ChatState.Idle);
            }

            _session.RaiseTurnCompleted(new TurnCompletedEventArgs { Succeeded = producedContent });
            return Task.CompletedTask;
        });

    public Task OnPausedAsync(AgentRun run, RunContext ctx, CancellationToken ct) =>
        PostAsync(() =>
        {
            // Non-terminal budget pause (guardrail 5): release the live session so it is USABLE while the
            // run sits WaitingForInput. Dispose the CTS and drop the session back to Idle — this clears
            // IsStreaming (ChatState.Running/WaitingForTool → Idle) so Send/RunInBackground re-enable.
            // Crucially, NO terminal settle here: we do NOT set ChatState.Completed/Error and do NOT raise
            // TurnCompleted (the run is not finished — it is parked awaiting the user's Continue).
            _session.DisposeCts();
            if (_session.State != ChatState.Error)
                _session.SetState(ChatState.Idle);
            return Task.CompletedTask;
        });

    /// <summary>Mirrors a clarification question the orchestrator wrote directly to storage into this session's own transcript, so it renders immediately and survives the next full-replace persist.</summary>
    public Task MirrorClarificationQuestionAsync(
        AgentRun run, RunContext ctx, Persona persona, Guid messageId, string question, CancellationToken ct) =>
        PostAsync(() =>
        {
            _session.Messages.Add(new AssistantMessage(messageId, ChatRole.Assistant, question, DateTime.Now)
            {
                Persona = PersonaAttribution.From(persona),
            });
            _session.RequestPersist();
            return Task.CompletedTask;
        });

    /// <param name="stepPersona">This step's resolved triple (Batch 07 G6), or null for the run's. The six
    /// persona-derived members below are the ONLY thing it changes — which is why
    /// <c>ChatSession.RunStepTurnAsync</c> needed no change at all: it already reads <c>spec.Persona</c> for
    /// attribution and <c>spec.Provider</c> for the exchange.</param>
    /// <param name="offerStepResultTool">hermes #9: append <c>emit_step_result</c> to this turn's tool list.
    /// True only for <see cref="ExecuteStepAsync"/>; the R10 degrade turn leaves it false (no
    /// <c>AgentStep</c> row, so there is no Done/Failed for a declaration to decide).</param>
    private StepTurnSpec BuildSpec(
        AgentRun run, int ordinal, string intent, string? expectedArtifact, bool useGoalVerbatim, Guid? stepId,
        StepPersonaSetup? stepPersona = null, bool offerStepResultTool = false)
    {
        // hermes #9. The setup ternary is hoisted out of the six member initializers below precisely so the
        // augmentation has ONE choke point, and it sits AFTER the ternary resolves: a step carrying an
        // AssignedPersonaId runs on stepPersona.TurnSetup, so augmenting _turnSetup alone would silently
        // withhold the tool from exactly those steps and leave them on the text heuristic forever.
        // WithStepResultTool copies the tool list — mutating it would leak a step tool into this session's
        // ordinary chat turns, which share this very _turnSetup instance.
        var turnSetup = stepPersona?.TurnSetup ?? _turnSetup;
        if (offerStepResultTool)
            turnSetup = AgentStepTools.WithStepResultTool(turnSetup);

        // Gated on offerStepResultTool (so the R10 degrade turn never sees it) and on CanRequestUserInput so a
        // delegated run is not offered a park no surface would show.
        if (offerStepResultTool && AgentStepTools.CanRequestUserInput(run.ParentRunId))
            turnSetup = AgentStepTools.WithRequestUserInputTool(turnSetup);

        return new(
            RunId: run.Id,
            Ordinal: ordinal,
            Intent: intent,
            ExpectedArtifact: expectedArtifact,
            SystemPrompt: turnSetup.SystemPrompt,
            Persona: stepPersona is null ? _persona : PersonaAttribution.From(stepPersona.Persona),
            Provider: stepPersona?.Provider ?? _provider,
            Tools: turnSetup.Tools,
            SupportsTools: turnSetup.SupportsTools,
            WebSearchActive: turnSetup.WebSearchActive,
            TokenizationEnabled: _tokenizationEnabled,
            UseGoalVerbatim: useGoalVerbatim,
            Policy: _policy,
            // No store ⇒ no scope ⇒ no rows. That is the whole opt-in mechanism: nothing downstream has to
            // reason about a null service. The SCOPE is the only carrier of the step id — a second
            // spec-level StepId field existed here and was read by nobody, so a later executor could have set
            // it, built a run-level scope, and got NULL step attribution with nothing failing.
            Timeline: _timeline is null ? null : new AgentTimelineScope(_timeline, run.Id, stepId),
            // Batch 06 D4. This is the ONLY producer of StepTurnSpec.WorkspaceRoot, and it is what actually
            // isolates an interactive step: ChatSession turns it into the ambient TaskContext the file tools
            // read. Trailing and defaulted on the record, so deleting this line COMPILES and silently
            // un-isolates every interactive run — keep it while rewriting the members around it.
            WorkspaceRoot: _workspaceRoot);
    }

    /// <summary>Marshals <paramref name="work"/> onto the captured UI context and bridges it back to an awaitable.</summary>
    private Task PostAsync(Func<Task> work) => PostAsync(async () => { await work(); return true; });

    private Task<T> PostAsync<T>(Func<Task<T>> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ui.Post(async _ =>
        {
            try { tcs.SetResult(await work()); }
            catch (OperationCanceledException) { tcs.SetCanceled(); }
            catch (Exception ex) { tcs.SetException(ex); }
        }, null);
        return tcs.Task;
    }
}
