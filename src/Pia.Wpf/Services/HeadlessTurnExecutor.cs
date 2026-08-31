using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Pia.Helpers;
using Pia.Logging;
using Pia.Models;
using Pia.Services.Interfaces;
using Pia.Services.Operators;
using Pia.Shared.Models;

namespace Pia.Services;

/// <summary>
/// Off-thread act-step executor (§13.1/§B.5). Wraps the refactored
/// <see cref="BackgroundAssistantTurnRunner.RunExchangeAsync"/> exchange engine, accumulating
/// messages across steps, making each completed step DURABLE as it lands (E2) and persisting the
/// final chat once at <see cref="EndRunAsync"/> (title precedence unchanged). Created in a fresh DI
/// scope per run. No streaming, no action-card UI — writes run only if granted. Sets
/// <c>TaskAmbient.Current</c> with the run-stable TaskId for the whole run so file tools key per-run
/// state (§16 R9); the file-chip sink is null (headless has no message UI — TaskId correctness is the
/// point).
/// </summary>
public sealed class HeadlessTurnExecutor : IAgentTurnExecutor
{
    private readonly BackgroundAssistantTurnRunner _engine;
    private readonly IAssistantChatService _chatService;
    private readonly ISettingsService _settingsService;
    private readonly IPersonaService _personaService;
    private readonly IProviderService _providerService;
    private readonly IAssistantPromptComposer _promptComposer;
    private readonly IChatTitleService _titleService;
    private readonly Func<ITokenMapService> _tokenMapFactory;
    private readonly ILogger<HeadlessTurnExecutor> _logger;

    /// <summary>The audit-timeline store (Batch 03); null ⇒ this run records nothing.</summary>
    private readonly IAgentTimelineService? _timelineService;

    /// <summary>
    /// Per-step persona/provider/prompt resolution (Batch 07 G6); null ⇒ every step runs on the run default,
    /// i.e. exactly the pre-Batch-07 behaviour even for a step that carries an <c>AssignedPersonaId</c>.
    /// </summary>
    private readonly StepPersonaResolver? _stepPersonas;

    // Per-run accumulating state.
    private readonly List<ChatMessage> _messages = new();
    private readonly List<SyncAssistantChatMessage> _persisted = new();
    private readonly HashSet<string> _grantedWrites = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _deniedWrites = new(StringComparer.OrdinalIgnoreCase);
    private AssistantTurnSetup _setup = default!;
    private Persona _persona = default!;
    private AiProvider _provider = default!;

    /// <summary>
    /// The run-level triple, i.e. the three fields above bundled for <see cref="StepPersonaResolver"/>.
    /// <para>
    /// Batch 07 kept the run-level resolution rather than replacing it, and three things need it: the
    /// orchestrator's plan/replan/verify turns are run-level by decision (one decomposition, one critic
    /// verdict); <see cref="RunSingleTurnFallbackAsync"/> belongs to the run and passes no step persona; and
    /// <see cref="BuildChatSnapshot"/>'s <c>ProviderId</c> is the CHAT ROW's provider, not a step's. It is also
    /// what every fallback arm of the resolver returns.
    /// </para>
    /// </summary>
    private StepPersonaSetup _runDefault = default!;
    private ITokenMapService? _tokenMap;
    private bool _tokenizationEnabled;
    private Guid _chatId;
    private Guid _runId;
    private string _grantedBy = string.Empty;

    // Captured at BeginRunAsync so the interim (per-step) and the terminal chat write build the SAME
    // row from one place — an interim save must never drop a column the terminal save preserves.
    private DateTime _runCreatedAt;
    private string _goal = string.Empty;
    private string? _existingTitle;
    private string? _existingWorkingDirectory;

    // Seeded by the launcher via Initialize before the orchestrator runs.
    private string? _workspaceRoot;
    private AiProvider? _providerOverride;
    private Persona? _personaOverride;

    /// <summary>This run's autonomy policy (Batch 04); null ⇒ no per-run policy, i.e. today's behaviour.</summary>
    private RunAutonomyPolicy? _policy;

    /// <summary>
    /// hermes #16: may this run park a promptable-but-ungranted tool call at <c>WaitingForInput</c> and ask a
    /// human, instead of hard-denying it? Seeded by the launcher, which owns the one fact that decides it (a
    /// ROOT run may, a CHILD run may not). False ⇒ the pre-#16 behaviour, byte-for-byte.
    /// </summary>
    private bool _canPark;

    /// <summary>Whether this run may pause mid-plan to ask the user a question; false for a delegated run.</summary>
    private bool _canAskUser;

    /// <summary>Is somebody expected at the machine for this run? A person started it themselves and it is
    /// nobody's delegate — the one shape whose park may ask about a delete-like tool.</summary>
    private bool _isTopLevelUserRun;

    /// <summary>
    /// hermes #15: the process-scoped session grants, or null when none were injected (⇒ no session tier).
    /// Read only through the per-step <see cref="ToolApprovalStore"/>, which arms it on the same condition as
    /// the park — see <see cref="ToolApprovalStore.HasSessionGrant"/>.
    /// </summary>
    private readonly ISessionToolGrantStore? _sessionGrants;

    /// <summary>The run's durable tool context, or null (⇒ record nothing, re-seed nothing).</summary>
    private readonly IAgentToolExchangeStore? _exchangeStore;

    /// <summary>The tool a person approved on THIS resume, or null. The whole replay predicate — never the grant
    /// set, which also holds tools whose withheld calls the re-run will make itself.</summary>
    private string? _approvedTool;

    private bool _replayAttempted;

    public HeadlessTurnExecutor(
        BackgroundAssistantTurnRunner engine,
        IAssistantChatService chatService,
        ISettingsService settingsService,
        IPersonaService personaService,
        IProviderService providerService,
        IAssistantPromptComposer promptComposer,
        IChatTitleService titleService,
        Func<ITokenMapService> tokenMapFactory,
        ILogger<HeadlessTurnExecutor> logger,
        // Trailing and defaulted so the existing positional construction in tests keeps compiling; the
        // container resolves it because it is registered.
        IAgentTimelineService? timelineService = null,
        // Batch 07 G6, trailing and defaulted for the same reason.
        StepPersonaResolver? stepPersonas = null,
        // hermes #15, trailing and defaulted for the same reason — and null is the RESTRICTIVE answer here
        // (no session tier at all, i.e. the pre-#15 gate), so a test or a caller that omits it never widens a
        // run. The container resolves the registered singleton.
        ISessionToolGrantStore? sessionGrants = null,
        // Trailing and defaulted for the same reason, and null is again the conservative answer: no rows are
        // written and a resume seeds prose alone, which is the pre-store behaviour.
        IAgentToolExchangeStore? exchangeStore = null)
    {
        _engine = engine;
        _chatService = chatService;
        _settingsService = settingsService;
        _personaService = personaService;
        _providerService = providerService;
        _promptComposer = promptComposer;
        _titleService = titleService;
        _tokenMapFactory = tokenMapFactory;
        _logger = logger;
        _timelineService = timelineService;
        _stepPersonas = stepPersonas;
        _sessionGrants = sessionGrants;
        _exchangeStore = exchangeStore;
    }

    /// <summary>
    /// Seed the granted write tools, an optional provider and persona override (the launcher's resolved
    /// pair, kept in lock-step with the orchestrator's planner so the two never diverge), and the run's
    /// isolated workspace root. Called from the launcher's fresh DI scope BEFORE <c>orchestrator.RunAsync</c>.
    /// <para>
    /// <paramref name="workspaceRoot"/> is the run's isolated base root and is <b>non-null for an isolated
    /// run</b> — the normal case since Batch 06 G2, where the launcher passes
    /// <c>%LOCALAPPDATA%\Pia\runs\&lt;runId&gt;</c>. A non-null value confines every file operation (read,
    /// write, delete, list, search) to that directory with full containment — no traversal, no absolute
    /// escape, no system paths — and <see cref="BeginRunAsync"/> republishes it onto
    /// <c>RunContext.WorkspaceRoot</c> so the verifier's artifact probe, which runs outside any step's
    /// ambient, resolves declared artifacts against the same root (B3).
    /// </para>
    /// <para>
    /// <c>null</c> means <b>no isolation</b>: the run writes straight into the user's assistant files
    /// folder, contained exactly like an interactive chat (only MCP is withheld). That is the degrade
    /// path — the behaviour of every build before G2, and where provisioning falls back to when an
    /// isolated workspace cannot be created. It is not the intended value for a healthy run. Callers
    /// must therefore assume neither value: a run may also be handed a root it did not provision itself
    /// (a child run inherits its parent's), which is why this parameter is a plain root and not a run id.
    /// </para>
    /// </summary>
    /// <param name="policy">The run's autonomy policy (Batch 04), or null for "today's behaviour": no tool
    /// class is auto-approved and only the named grant set authorizes a write. Relayed verbatim into the one
    /// unattended gate; a resume restores it from the run's envelope, never from settings.</param>
    /// <param name="canPark">hermes #16: may this run stop and ask a human for a promptable capability it was
    /// not granted, instead of hard-denying it? See <see cref="_canPark"/>. Trailing and defaulted to FALSE,
    /// which is the pre-#16 behaviour — a caller that forgets it gets the safe answer.</param>
    /// <param name="deniedWrites">Tools a person declined for this run on a tool-approval park; the unattended
    /// gate refuses them with "adapt" instead of re-parking. Null/empty = no denials.</param>
    /// <param name="personaOverride">The launcher's resolved run persona; null ⇒ resolve the per-mode one here.
    /// Replaces the RUN DEFAULT only — a step naming its own persona still gets that one.</param>
    /// <param name="approvedTool">The tool a person just approved on this resume; its persisted calls are
    /// replayed once before the step re-runs. Null on a fresh launch and on a decline.</param>
    public void Initialize(
        string? workspaceRoot,
        IReadOnlyCollection<string> grantedWrites,
        AiProvider? providerOverride = null,
        RunAutonomyPolicy? policy = null,
        bool canPark = false,
        IReadOnlyCollection<string>? deniedWrites = null,
        Persona? personaOverride = null,
        string? approvedTool = null)
    {
        _workspaceRoot = workspaceRoot;
        _providerOverride = providerOverride;
        _personaOverride = personaOverride;
        _policy = policy;
        _canPark = canPark;
        _approvedTool = approvedTool;
        _grantedWrites.Clear();
        foreach (var w in grantedWrites)
            _grantedWrites.Add(w);
        _deniedWrites.Clear();
        if (deniedWrites is not null)
            foreach (var w in deniedWrites)
                _deniedWrites.Add(w);
    }

    public async Task BeginRunAsync(AgentRun run, RunContext ctx, CancellationToken ct)
    {
        _chatId = run.ChatId;
        _runCreatedAt = run.CreatedAt;
        _goal = ctx.Goal;

        // Parity note (guardrail 3): LiveTurnExecutor hands the chat's working subpath to the context so the
        // verifier's artifact probe stats the root its steps wrote into. A headless run deliberately does NOT
        // inherit one — every step runs with TaskContext.WorkingSubpath: null (RunExchangeStepAsync), so its
        // writes land at the base root even when the chat row carries a WorkingDirectory. Stated as an
        // explicit assignment rather than left to the default.
        ctx.WorkingSubpath = null;
        // Batch 06 B3: publish the run's workspace root onto the context so the verifier (which runs on
        // the orchestrator thread, outside any step's ambient) can resolve declared artifacts against the
        // root the steps actually wrote into instead of falling back to the settings folder. Non-null for
        // an isolated run since G2 (the launcher passes runs\<runId> at both dispatch sites); null only on
        // the no-isolation degrade, which resolves the settings folder exactly as before.
        ctx.WorkspaceRoot = _workspaceRoot;

        var settings = await _settingsService.GetSettingsAsync().ConfigureAwait(false);
        _tokenizationEnabled = settings.Privacy.TokenizationEnabled;

        // Prefer the launcher's resolution, so the step turns run on the persona the planner and the panel name.
        _persona = _personaOverride
            ?? await _personaService.ResolveActiveAsync(
                WindowMode.Assistant, settings.UserOperatingMode ?? UserOperatingMode.Personal).ConfigureAwait(false);

        // Prefer the launcher-resolved override so the executor and the orchestrator's planner run on
        // the SAME provider (honors a scheduled job's ProviderId); fall back to persona-preferred/default.
        var provider = _providerOverride;
        if (provider is null)
        {
            provider = _persona.PreferredProviderId.HasValue
                ? await _providerService.GetProviderAsync(_persona.PreferredProviderId.Value).ConfigureAwait(false)
                : null;
            provider ??= await _providerService.GetDefaultProviderForModeAsync(WindowMode.Assistant).ConfigureAwait(false);
            if (provider is null)
                throw new InvalidOperationException("No provider configured for a headless agent run.");
            if (_persona.ReasoningEffort.HasValue)
            {
                provider = provider.Clone();
                provider.ReasoningEffort = _persona.ReasoningEffort.Value;
            }
        }
        _provider = provider;

        // Headless path — no user to click the chip (R7) → never eligible.
        _setup = _promptComposer.PrepareTurn(_persona, _provider, [], _tokenizationEnabled,
            suggestAgentModeEligible: false);

        // Batch 07 G6: the resolution above is now the run DEFAULT rather than the only answer. Deliberately
        // still done here and still cached — see _runDefault's own comment for the three consumers that need a
        // run-level triple. A step that names a roster persona resolves its own on top of this.
        _runDefault = new StepPersonaSetup(_persona, _provider, _setup);

        // MCP is offered to unattended runs like any other tool now that the Phase-2 gate is in place: an
        // MCP call returns a deferred PluginToolCall and is denied inline unless its tool name is in the
        // run's write-grant set (default-deny — the launcher's default is
        // HeadlessRunRequest.DefaultGrantedWrites = {write_file}; delete_file must be granted explicitly,
        // and a granted tool that is both delete-like and external is refused regardless — B2).

        // Initialize the run's token map. NOTE: the ambients are set PER STEP (in RunExchangeStepAsync),
        // not here — an AsyncLocal set after an await inside BeginRunAsync would NOT propagate into the
        // separately-awaited ExecuteStepAsync (ExecutionContext flows down, not back out), so a run-scoped
        // bracket here would leave the exchange with no ambient. Setting it around each exchange keeps the
        // run-stable TaskId live where it is read (§16 R9).
        _runId = run.Id;
        _grantedBy = AssignmentGranter.ForUnattendedRun(run.TriggerKind, run.TriggerRef, run.Id);
        // Read directly from the row, unlike _canPark (seeded by the launcher) — this is a structural fact,
        // not a grant.
        _canAskUser = AgentStepTools.CanRequestUserInput(run.ParentRunId);
        // The same kind of structural fact, from the same row and never from a tool name: a scheduled or
        // routine run has nobody watching, and a child never acquires authority its parent narrowed away.
        _isTopLevelUserRun = run.ParentRunId is null && run.TriggerKind == AgentRunTrigger.User;
        _tokenMap = _tokenMapFactory();
        if (_tokenizationEnabled)
        {
            try { await _tokenMap.InitializeAsync().ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogError(ex, "Failed to initialize token map for headless run {RunId}", run.Id); }
        }

        // Seed the accumulating transcript. The terminal EndRunAsync does a full chat replace, so on a
        // RESUME (or any pre-populated chat) we must load the existing rows first — otherwise EndRunAsync
        // would erase the prior transcript (D2). A fresh launch's stub chat is empty → seed [system, goal].
        // W2: this snapshot is no longer taken ONCE and trusted for the rest of the run — every write goes
        // through SaveMergedAsync, which re-reads the persisted rows under the store's gate and absorbs
        // anything this executor did not author, so a concurrent writer's rows survive the full replace.
        // What is seeded HERE is still the only thing that reaches _messages (the model context); the merge
        // happens inside the store and deliberately never reaches it.
        _messages.Clear();
        _persisted.Clear();
        _messages.Add(new ChatMessage(ChatRole.System, _setup.SystemPrompt)); // system: never persisted

        var chat = await _chatService.GetAsync(run.ChatId, ct).ConfigureAwait(false);
        // Carry the row's own metadata forward: every chat write here is a FULL replace, and with per-step
        // interim saves it now happens repeatedly mid-run. Re-using the persisted title keeps an interim
        // save from downgrading a good title (the launcher's derived one, or an LLM title an earlier segment
        // produced) and re-using WorkingDirectory keeps an interactive chat's per-chat folder from being
        // nulled by a resumed run's saves.
        _existingTitle = chat?.Title;
        _existingWorkingDirectory = chat?.WorkingDirectory;

        // The prose transcript alone told a resumed step nothing about the files an abandoned attempt had
        // already read or written, so it asked the user for data it had.
        var carried = await ReadCarriedAsync(run.Id, chat, ct).ConfigureAwait(false);
        void SeedRow(SyncAssistantChatMessage m)
        {
            if (carried.Anchored.TryGetValue(m.Id, out var groups))
                _messages.AddRange(groups);
            _messages.Add(new ChatMessage(ParseRole(m.Role), m.Content));
            _persisted.Add(m);
        }

        // Checks that the FIRST message is a user row, not just any — a parked run's chat can open with the
        // assistant's clarification question instead of the goal.
        if (chat is { Messages.Count: > 0 }
            && string.Equals(chat.Messages[0].Role, "user", StringComparison.OrdinalIgnoreCase))
        {
            // Resume: seed from the persisted transcript so the terminal full-replace PRESERVES prior
            // rows. No synthetic goal (it is already in the transcript).
            foreach (var m in chat.Messages)
                SeedRow(m);
        }
        else
        {
            // Fresh launch, or a park whose chat opens with the clarification question rather than the goal:
            // seed the goal first, then carry forward whatever the chat already holds so it still reads in order.
            var goalMsgId = Guid.NewGuid();
            _messages.Add(new ChatMessage(ChatRole.User, ctx.Goal));
            _persisted.Add(new SyncAssistantChatMessage
            {
                Id = goalMsgId,
                Role = "user",
                Content = ctx.Goal,
                Timestamp = DateTime.UtcNow,
            });

            if (chat is { Messages.Count: > 0 })
            {
                foreach (var m in chat.Messages)
                    SeedRow(m);
            }
        }

        // The abandoned attempt's rows: no assistant reply to anchor to, because a park discards the step's
        // prose. The tail is also what keeps them inside ClearOldResults' newest-by-position window.
        _messages.AddRange(carried.Trailing);
    }

    /// <summary>
    /// The run's carried tool exchanges, split into the groups that precede a surviving chat row and the ones
    /// that belong at the tail. Returned VERBATIM — the rows already are what the model saw, so detokenizing
    /// them here would send the provider something the pre-park rounds never sent.
    /// </summary>
    private async Task<CarriedToolExchanges> ReadCarriedAsync(Guid runId, SyncAssistantChat? chat, CancellationToken ct)
    {
        if (_exchangeStore is null)
            return CarriedToolExchanges.Empty;

        try
        {
            var rows = await _exchangeStore.ReadCarriedAsync(runId, ct).ConfigureAwait(false);
            if (rows.Count == 0)
                return CarriedToolExchanges.Empty;

            var chatIds = new HashSet<Guid>();
            if (chat?.Messages is { Count: > 0 } chatRows)
                foreach (var m in chatRows)
                    chatIds.Add(m.Id);

            var anchored = new Dictionary<Guid, List<ChatMessage>>();
            var trailing = new List<ChatMessage>();
            // A stale anchor falls into the tail rather than being dropped, so the split is total.
            foreach (var bucket in rows.GroupBy(r =>
                r.AnchorMessageId is { } id && chatIds.Contains(id) ? id : (Guid?)null))
            {
                var messages = AgentToolExchangeSerializer.ToMessages(bucket);
                if (bucket.Key is { } anchor)
                {
                    if (!anchored.TryGetValue(anchor, out var group))
                        anchored[anchor] = group = new List<ChatMessage>();
                    group.AddRange(messages);
                }
                else
                {
                    trailing.AddRange(messages);
                }
            }

            _logger.LogInformation(
                "Headless run {RunId} re-seeded {RowCount} carried tool-exchange row(s): {AnchoredGroups} anchored group(s), {TrailingMessages} trailing message(s)",
                runId, rows.Count, anchored.Count, trailing.Count);
            return new CarriedToolExchanges(anchored, trailing);
        }
        catch (Exception ex)
        {
            // A corrupt or unreachable store degrades the resume to prose-only instead of failing every resume.
            _logger.LogWarning(ex, "Failed to read carried tool exchanges for headless run {RunId}", runId);
            return CarriedToolExchanges.Empty;
        }
    }

    private sealed record CarriedToolExchanges(
        IReadOnlyDictionary<Guid, List<ChatMessage>> Anchored,
        IReadOnlyList<ChatMessage> Trailing)
    {
        public static CarriedToolExchanges Empty { get; } = new(new Dictionary<Guid, List<ChatMessage>>(), []);
    }

    private static ChatRole ParseRole(string role) => role switch
    {
        "system" => ChatRole.System,
        "assistant" => ChatRole.Assistant,
        "tool" => ChatRole.Tool,
        _ => ChatRole.User,
    };

    public async Task<StepTurnResult> ExecuteStepAsync(AgentRun run, AgentStep step, RunContext ctx, CancellationToken ct)
    {
        // Batch 07 G6: resolve THIS step's persona before the exchange. Never throws — every arm of the ladder
        // ends at _runDefault, because a per-step persona is an enhancement and must not be able to fail a run.
        var setup = _stepPersonas is null
            ? _runDefault
            : await _stepPersonas.ResolveAsync(step.AssignedPersonaId, _runDefault, _tokenizationEnabled, ct)
                .ConfigureAwait(false);

        // Before the step's first provider round-trip, and inside the run's own ambient: a replayed write must
        // land in the workspace this step writes into.
        await ReplayApprovedParkedCallsAsync(step.Id, TimelineScope(step.Id), ct).ConfigureAwait(false);

        // Batch 08 D4: the ONLY place a user steering note may ride — composed here (this method has ctx),
        // never inside RunExchangeStepAsync, which keeps taking a plain string and never sees ctx at all.
        return await RunExchangeStepAsync(
                ctx.AppendNudge(BuildInstruction(step.Ordinal, step.Intent ?? string.Empty, step.ExpectedArtifact)),
                persistInterim: true, ct, TimelineScope(step.Id), setup,
                // hermes #9: this is the ONE entry point whose result reaches RecordStepResultAsync as
                // Done/Failed, so it is the one that offers emit_step_result. The R10 degrade turn below
                // deliberately does not (no AgentStep row, no step status to decide) — the live executor
                // draws the same line at the same place.
                offerStepResultTool: true,
                exchanges: ExchangeScope(step.Id))
            .ConfigureAwait(false);
    }

    // persistInterim: false — the fallback turn is followed IMMEDIATELY by the terminal EndRunAsync on
    // every branch of the R10 degrade path, so an interim write here would only double the chat rewrite.
    // stepId: null — the degrade turn belongs to the run but to no step.
    // No step persona either, for the same reason: the R10 degrade turn belongs to the RUN, so it runs on the
    // run persona's prompt and provider (the trailing argument is left at its default).
    public Task<StepTurnResult> RunSingleTurnFallbackAsync(AgentRun run, RunContext ctx, CancellationToken ct) =>
        RunExchangeStepAsync(ctx.Goal, persistInterim: false, ct, TimelineScope(stepId: null),
            exchanges: ExchangeScope(stepId: null));

    /// <summary>
    /// T2-18 — the grace turn. One TOOL-FREE round through the same exchange engine, so the parked run's chat
    /// ends with a wrap-up a person can read hours later instead of trailing off after the last step.
    /// <para>
    /// <c>toolFree: true</c> is load-bearing, not tidiness: the run has just been told its budget is spent, and a
    /// turn that could still call <c>write_file</c> would make the cap advisory. <c>persistInterim: true</c> is
    /// equally load-bearing — a park never reaches <see cref="EndRunAsync"/>, so a wrap-up that was not written
    /// here would never be written at all. <c>stepId: null</c>: it belongs to the run, not to a step, exactly
    /// like the R10 degrade turn.
    /// </para>
    /// </summary>
    // No exchange scope: toolFree strips the tool list, so this turn cannot produce a round to record.
    public async Task<StepTurnResult?> RunGraceTurnAsync(AgentRun run, RunContext ctx, CancellationToken ct) =>
        await RunExchangeStepAsync(GraceTurnInstruction, persistInterim: true, ct,
                TimelineScope(stepId: null), persona: null, offerStepResultTool: false, toolFree: true)
            .ConfigureAwait(false);

    /// <summary>
    /// The grace turn's instruction. Deliberately not localized, like <c>AgentStepTools.UndetailedFailure</c>:
    /// it is model-facing, and this executor has no <c>ILocalizationService</c> at all. It says NO TOOLS out loud
    /// as well as withholding them, because a model told only implicitly tends to try anyway and burn the round
    /// on a refusal.
    /// </summary>
    private const string GraceTurnInstruction =
        "This run has reached its budget (step cap or wall clock), so no further steps will run right now and it "
        + "is about to pause until a person continues it. Write a short wrap-up for whoever reads this later: "
        + "what you actually accomplished, what is still outstanding, and anything they need to know to pick it "
        + "up. Do not call any tools — just write the summary.";

    /// <summary>The per-step audit sink, or null when no store was injected (⇒ record nothing).</summary>
    private AgentTimelineScope? TimelineScope(Guid? stepId) =>
        _timelineService is null ? null : new AgentTimelineScope(_timelineService, _runId, stepId);

    /// <summary>The per-step payload sink, or null when no store was injected (⇒ record nothing).</summary>
    private AgentToolExchangeScope? ExchangeScope(Guid? stepId) =>
        _exchangeStore is null ? null : new AgentToolExchangeScope(_exchangeStore, _runId, stepId);

    /// <summary>Two callers, so the replay's sandbox and audit attribution cannot drift from the step's.</summary>
    private TaskContext StepAmbient() =>
        new(_runId, WorkingSubpath: null, OnFileTouched: null, WorkspaceRoot: _workspaceRoot, ChatId: _chatId,
            UnattendedGranter: _grantedBy);

    /// <summary>
    /// Run the calls a person just approved, once each, then seed them so the model can see they ran. MUST NEVER
    /// THROW: it sits outside <see cref="RunExchangeStepAsync"/>'s try/catch, and a best-effort seed may not fail a run.
    /// </summary>
    private async Task ReplayApprovedParkedCallsAsync(Guid stepId, AgentTimelineScope? timeline, CancellationToken ct)
    {
        if (_exchangeStore is null || _approvedTool is not { } approvedTool || _replayAttempted)
            return;

        // One shot per dispatch, beside each row's own marker, and it saves a query on every later step.
        _replayAttempted = true;

        var previousAmbient = TokenMapAmbient.Current;
        var previousTask = TaskAmbient.Current;
        var replayed = new List<string>();
        try
        {
            var rows = await _exchangeStore.GetReplayableAsync(_runId, approvedTool, ct).ConfigureAwait(false);
            if (rows.Count == 0)
                return;

            _logger.LogInformation(
                "Headless run {RunId} replaying {RowCount} approved call(s) of {ToolName} before step {StepId}",
                _runId, rows.Count, approvedTool, stepId);

            if (_tokenizationEnabled)
                TokenMapAmbient.Current = _tokenMap;
            TaskAmbient.Current = StepAmbient();

            foreach (var row in rows)
            {
                if (row.ToolName is not { Length: > 0 } toolName)
                    continue;

                // Stamped BEFORE the call, and a lost or failed claim skips the row entirely: at-most-once has
                // to survive two concurrent dispatches and a crash between the mark and the effect.
                if (!await _exchangeStore.TryMarkReplayedAsync(row.Id, DateTime.UtcNow, ct).ConfigureAwait(false))
                    continue;

                // Synthesized only here: a provider that gave no id still has to produce a pairable seed, and
                // the recorded row must keep agreeing with the audit row for the same call.
                var call = new FunctionCallContent(
                    row.CallId is { Length: > 0 } id ? id : Guid.NewGuid().ToString("N"),
                    toolName,
                    AgentToolExchangeSerializer.DeserializeArguments(row.ArgumentsJson));

                var resultText = await ExecuteReplayAsync(call, timeline).ConfigureAwait(false);
                _logger.SensitiveDebug("Replayed {ToolName} result: {Result}", toolName, resultText);
                await _exchangeStore.SetResultAsync(row.Id, resultText, ct).ConfigureAwait(false);
                SeedReplayedCall(call, resultText);
                replayed.Add(toolName);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Replaying the approved call(s) of {ToolName} for run {RunId} failed",
                approvedTool, _runId);
        }
        finally
        {
            TokenMapAmbient.Current = previousAmbient;
            TaskAmbient.Current = previousTask;
        }

        if (replayed.Count > 0)
            _messages.Add(new ChatMessage(ChatRole.User, ReplayedCallNote(replayed)));
    }

    /// <summary>
    /// The gate's own answer, or the failure as ordinary result text — the step sees a call that failed, exactly
    /// as if it had made it itself, and the row is consumed either way.
    /// </summary>
    private async Task<string> ExecuteReplayAsync(FunctionCallContent call, AgentTimelineScope? timeline)
    {
        try
        {
            // CanPark false makes the park arm unreachable (no replay may re-park) and disarms the session tier
            // that rides on it; IsTopLevelUserRun stays honest so the call resolves on the inputs it was judged on.
            var approvals = new ToolApprovalStore(
                canPark: false, _sessionGrants, isTopLevelUserRun: _isTopLevelUserRun);
            // Round 1: the replay stands in for the call the model would otherwise make on the step's first round.
            var result = await _engine
                .ReplayToolCallAsync(call, _grantedWrites, round: 1, _policy, timeline, approvals, _deniedWrites)
                .ConfigureAwait(false);
            var (_, text) = AgentToolExchangeSerializer.SerializeResult(result);
            return text ?? string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Replay of the approved tool {ToolName} for run {RunId} faulted", call.Name, _runId);
            return $"Not run: the approved '{call.Name}' was executed on your behalf and failed: {ex.Message}";
        }
    }

    /// <summary>
    /// The pair the re-run's first view must contain, or the model reissues the call and the side effect happens
    /// twice. It bypasses <c>TokenizingAiClientService</c> entirely, so BOTH halves are tokenized here.
    /// </summary>
    private void SeedReplayedCall(FunctionCallContent call, string resultText)
    {
        _messages.Add(new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent(call.CallId, call.Name, SeedArguments(call.Arguments))]));
        _messages.Add(new ChatMessage(ChatRole.Tool,
            [new FunctionResultContent(call.CallId, Tokenize(resultText))]));
    }

    private IDictionary<string, object?>? SeedArguments(IDictionary<string, object?>? arguments)
    {
        if (arguments is null)
            return null;

        var seeded = AgentToolExchangeSerializer.CapForSeed(arguments);
        foreach (var key in seeded.Keys.ToList())
        {
            if (SeedText(seeded[key]) is { } text)
                seeded[key] = Tokenize(text);
        }

        return seeded;
    }

    private static string? SeedText(object? value) => value switch
    {
        string s => s,
        JsonElement { ValueKind: JsonValueKind.String } el => el.GetString(),
        _ => null,
    };

    private string Tokenize(string text) =>
        _tokenizationEnabled && _tokenMap is not null ? _tokenMap.TokenizeStructuredResult(text) : text;

    /// <summary>Model-facing and deliberately unlocalized, like <see cref="GraceTurnInstruction"/>.</summary>
    private static string ReplayedCallNote(IEnumerable<string> tools) =>
        "The approval you were waiting for was given, and the call(s) you had asked about were executed on your "
        + "behalf just now: " + string.Join(", ", tools.Distinct(StringComparer.OrdinalIgnoreCase))
        + ". The call and its result are above. Do not issue that call again — carry on from its result.";

    /// <param name="persona">The step's resolved triple (Batch 07 G6), or null for the RUN's — which is what
    /// the R10 degrade turn passes and what an executor built without a resolver always uses.</param>
    /// <param name="offerStepResultTool">hermes #9: offer <c>emit_step_result</c> on this turn. True only for
    /// <see cref="ExecuteStepAsync"/>; the R10 degrade turn leaves it false.</param>
    /// <param name="toolFree">T2-18: send NO tools at all. Only <see cref="RunGraceTurnAsync"/> passes true —
    /// that turn happens after the budget is spent, so it must not be able to act.</param>
    /// <param name="exchanges">The payload sink. A separate parameter rather than derived from
    /// <paramref name="timeline"/>: the timeline is optional and absent in most suites, and deriving one
    /// optional collaborator from another would make the store silently inert wherever no timeline is injected.</param>
    private async Task<StepTurnResult> RunExchangeStepAsync(
        string instruction, bool persistInterim, CancellationToken ct, AgentTimelineScope? timeline = null,
        StepPersonaSetup? persona = null, bool offerStepResultTool = false, bool toolFree = false,
        AgentToolExchangeScope? exchanges = null)
    {
        var p = persona ?? _runDefault;

        // hermes #16. Built for every turn that goes through here — including the R10 degrade turn, whose
        // store is simply never armed (CanPark false) because the run has no AgentStep row to put back to
        // Pending and the orchestrator's fallback path has no park slot. Derived from _canPark AND
        // offerStepResultTool for that reason: "this is a real, re-runnable planned step" is exactly the
        // condition both signals already encode, and deriving it keeps them from drifting apart.
        //
        // hermes #15 rides in the SAME store and is armed by the same CanPark: the session tier reaches an
        // unattended run exactly where the park does. See ToolApprovalStore.HasSessionGrant for why that
        // symmetry is the safe line — a child run must inherit neither.
        var approvals = new ToolApprovalStore(
            canPark: _canPark && offerStepResultTool, _sessionGrants, isTopLevelUserRun: _isTopLevelUserRun);

        // hermes #9, and the placement is the point: AFTER the persona ternary above, so a step carrying an
        // AssignedPersonaId gets the tool on ITS setup. Augmenting _runDefault/_setup instead would silently
        // withhold the tool from exactly the steps that resolved their own persona, and every such step would
        // fall back to the text heuristic forever with nothing failing.
        // T2-18: the tool-free variant strips the list rather than relying on the prompt — AiClientService
        // computes hasTools from `tools is { Count: > 0 }`, so a null list means no tools and no tool handler
        // reach the provider at all. Checked FIRST so the two flags cannot be combined into a grace turn that
        // is offered emit_step_result.
        var turnSetup = toolFree ? p.TurnSetup with { Tools = null }
            : offerStepResultTool ? AgentStepTools.WithStepResultTool(p.TurnSetup) : p.TurnSetup;
        // Gated on offerStepResultTool (the degrade/grace turns own no AgentStep row to park) and on
        // _canAskUser so a delegated run is not offered a park no surface would show.
        if (offerStepResultTool && _canAskUser)
            turnSetup = AgentStepTools.WithRequestUserInputTool(turnSetup);
        // Armed IFF offered — derived from the resolved list rather than from offerStepResultTool, so a setup
        // that could not take the tool (SupportsTools=false: no tools and no handler reach the provider) lands
        // on the unconfirmed fallback instead of waiting for a claim that can never arrive.
        var outcomeStore = AgentStepTools.OffersStepResultTool(turnSetup.Tools) ? new StepOutcomeStore() : null;
        // A delegated run still intercepts request_user_input (redirecting to emit_step_result) rather than
        // falling through to "Unknown tool.", so CanAsk is derived from the resolved list, not from _canAskUser.
        var userInput = outcomeStore is null
            ? null
            : new UserInputRequestStore(AgentStepTools.OffersRequestUserInputTool(turnSetup.Tools));

        // Append the EPHEMERAL step instruction to a COPY — the accumulating _messages keeps the
        // transcript (system + goal + each step's tool exchanges and assistant reply).
        //
        // Batch 07 G6, and this is the line that makes a per-step persona real rather than cosmetic: element 0
        // of the copy is THIS STEP's system prompt, not the run's. _messages[0] is left alone on purpose — it
        // stays the RUN persona's prompt, so the accumulating transcript keeps one well-defined system message
        // and the next step (whatever persona it resolves to) starts from the same place. Mutating _messages[0]
        // instead would leak step N's persona into step N+1 and into a resume's re-seed.
        var exchangeMessages = new List<ChatMessage>(_messages.Count + 1)
        {
            new(ChatRole.System, turnSetup.SystemPrompt),
        };
        exchangeMessages.AddRange(_messages.Skip(1));
        exchangeMessages.Add(new ChatMessage(ChatRole.User, instruction));

        // ONE compaction seam covers all three Headless entry points: ExecuteStepAsync, the R10
        // degrade turn (RunSingleTurnFallbackAsync) — both funnel through here — and the RESUME path.
        // Resume needs no seam of its own: its growth enters via the transcript re-seed into
        // _messages in BeginRunAsync (every prior segment's full assistant reply, verbatim, unbounded
        // in total steps ever run), and the copy above is what gets compacted.
        //
        // HARD GUARDRAIL, satisfied by construction: this cannot reach persistence. _messages is a
        // List<ChatMessage> and _persisted is a List<SyncAssistantChatMessage> — different types,
        // appended in parallel, never cross-read, no object aliasing — and the only route from
        // executor state to the DB is BuildChatSnapshot's `Messages = [.. _persisted]`, which serves
        // both the interim per-step write and the terminal one. A pass over a ChatMessage list is
        // therefore type-incapable of shrinking the transcript, so a resume still replays it in full.
        //
        // Cleared BEFORE compaction so the compactor never spends a summarization pass on a body that was
        // about to become a placeholder. ClearOldResults builds, so _messages keeps the full carried results
        // and a later step can still be the one that gets them verbatim.
        // The same test RunExchangeAsync makes before it sends them: SupportsTools false means no tools reach
        // the provider whatever the list holds.
        var carried = turnSetup.SupportsTools && turnSetup.Tools is { Count: > 0 }
            ? AgentToolCarryover.ClearOldResults(exchangeMessages)
            : AgentToolCarryover.WithoutToolExchanges(exchangeMessages);
        var contextBudget = AgentContextBudget.From(p.Provider);
        var request = await AgentContextCompactor
            .CompactAsync(carried, contextBudget, _logger, ct)
            .ConfigureAwait(false);

        // WHICH run lost context. The compactor logs the counts but holds no run id, so the correlation
        // has to happen here, where _runId is in scope. NO step ordinal: this one seam serves
        // ExecuteStepAsync, the R10 fallback turn and the resume path, and the ordinal only ever reaches
        // the instruction STRING - surfacing it would mean a new parameter, which one log line does not
        // justify. Counts and ids only: this lands in a support-attachable log.
        if (request.Count != exchangeMessages.Count)
        {
            _logger.LogInformation(
                "Headless run {RunId} context compaction changed the step request from {BeforeCount} to {AfterCount} messages",
                _runId, exchangeMessages.Count, request.Count);
        }

        // Per-step ambient bracket (§16 R9): run-stable TaskId, no file-chip sink (headless has no UI).
        // Set here — not in BeginRunAsync — so the AsyncLocal is live inside THIS exchange's flow.
        var previousAmbient = TokenMapAmbient.Current;
        var previousTask = TaskAmbient.Current;
        if (_tokenizationEnabled)
            TokenMapAmbient.Current = _tokenMap;
        TaskAmbient.Current = StepAmbient();

        BackgroundAssistantTurnRunner.ExchangeResult exchange;
        try
        {
            // The same budget goes down into the exchange so the IN-step tool loop is bounded too: the
            // request compacted above can still grow past the window inside AiClientService as tool
            // calls and tool results accumulate over up to 10 rounds.
            exchange = await _engine.RunExchangeAsync(request, p.Provider, turnSetup, _grantedWrites, ct,
                    onUsage: null, contextBudget: contextBudget, policy: _policy, timeline: timeline,
                    outcomeStore: outcomeStore, approvals: approvals, userInput: userInput,
                    deniedWrites: _deniedWrites, exchanges: exchanges)
                .ConfigureAwait(false);
        }
        // The token guard keeps a transport cancellation (an HTTP timeout escaped conversion) off this
        // arm: only a fired run token counts as a host/user cancel.
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new StepTurnResult(false, true, "cancelled", string.Empty, null, Guid.Empty, Guid.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Headless step exchange failed for chat {ChatId}", _chatId);

            // hermes #16: A PARK SURVIVES A FAULT THAT HAPPENS AFTER IT. The gate writes its
            // ParkedForApproval audit row the moment it parks, so returning a plain failure here made the
            // persisted state contradict itself — the timeline said "Awaiting approval" on a run the
            // orchestrator then settled terminally, with no pause envelope, no Continue card and no button.
            // That is the reporting failure #16 exists to remove, arrived at from the other side.
            //
            // Honouring the park is also the safe direction rather than the lenient one: the tool did not run,
            // this attempt's text is discarded and the step row goes back to Pending either way, so a fault and
            // a park end in the same place — except that parking KEEPS the question. And it cannot bury a
            // persistent fault, because the resume grants the tool, so the next attempt parks on nothing and the
            // fault surfaces normally. No usage is carried: the exchange never produced a total.
            //
            // The CANCELLED arm above is deliberately left alone. Cancellation is the host's or the user's
            // decision about the whole run and outranks the run's own request for attention, the same
            // precedence Batch 08 gave a user pause over a budget park.
            if (approvals.PendingToolName is { } parkedBeforeFault)
            {
                _logger.LogInformation(
                    "Headless run {RunId} step faulted after parking for approval of {ToolName}; keeping the park",
                    _runId, parkedBeforeFault);
                await PersistParkedCallsAsync(approvals, exchanges?.StepId).ConfigureAwait(false);
                return new StepTurnResult(
                    Succeeded: false, Cancelled: false, Error: null, VisibleText: string.Empty,
                    Usage: null, FirstMessageId: Guid.Empty, LastMessageId: Guid.Empty,
                    ApprovalRequiredTool: parkedBeforeFault,
                    ApprovalRequiredArguments: ToolApprovalArguments.Join(approvals.PendingToolArguments));
            }

            // A fault after an ask still returns the question instead of the error, mirroring the approval-park
            // arm above — the attempt is discarded either way, so losing the question here would strand it.
            if (userInput?.Question is not null)
            {
                _logger.LogInformation(
                    "Headless run {RunId} step faulted after asking the user; keeping the ask ({AcceptedCalls} ask(s))",
                    _runId, userInput.AcceptedCalls);
                return new StepTurnResult(
                    Succeeded: false, Cancelled: false, Error: null, VisibleText: string.Empty,
                    Usage: null, FirstMessageId: Guid.Empty, LastMessageId: Guid.Empty,
                    UserInputQuestion: userInput.Question);
            }

            return new StepTurnResult(false, false, ex.Message, string.Empty, null, Guid.Empty, Guid.Empty);
        }
        finally
        {
            TokenMapAmbient.Current = previousAmbient;
            TaskAmbient.Current = previousTask;
        }

        // ---- hermes #16: the step PARKED on a tool that needs a human ----
        // Placed with the two catch arms above rather than after them, because it is the same KIND of exit: the
        // step did not finish, so there is nothing for the model to have a vote on. It returns BEFORE the
        // transcript append and BEFORE the interim persist on purpose — D2's rule for an aborted step is that
        // its text is discarded so the step re-runs clean, and a park is an abort with a question attached.
        // Half a step's reply in the transcript would tell the resumed step it had already done the work.
        //
        // The USAGE is carried out, because those tokens were genuinely spent; the orchestrator bills them
        // run-level (stepId: null), exactly as the user-pause branch bills a cancelled step's.
        if (approvals.PendingToolName is { } parkedTool)
        {
            _logger.LogInformation(
                "Headless run {RunId} parked step for approval of {ToolName} ({ParkedCalls} parked call(s))",
                _runId, parkedTool, approvals.ParkedCalls);
            await PersistParkedCallsAsync(approvals, exchanges?.StepId).ConfigureAwait(false);
            return new StepTurnResult(
                Succeeded: false, Cancelled: false, Error: null, VisibleText: string.Empty,
                Usage: exchange.Usage, FirstMessageId: Guid.Empty, LastMessageId: Guid.Empty,
                ApprovalRequiredTool: parkedTool,
                // Never in the log line above: a path is user content, and ParkedCalls is the scalar that is safe.
                ApprovalRequiredArguments: ToolApprovalArguments.Join(approvals.PendingToolArguments));
        }

        // ---- the step ASKED the user a mid-plan question ----
        // Checked ahead of the step-success decision below, not &&-ed with it: a model that asks often also
        // declares emit_step_result{false} in the same breath, and that must not be read as an ordinary failure.
        if (userInput?.Question is not null)
        {
            _logger.LogInformation(
                "Headless run {RunId} step asked the user a question ({AcceptedCalls} ask(s), {RefusedCalls} refused)",
                _runId, userInput.AcceptedCalls, userInput.RefusedCalls);
            return new StepTurnResult(
                Succeeded: false, Cancelled: false, Error: null, VisibleText: string.Empty,
                Usage: exchange.Usage, FirstMessageId: Guid.Empty, LastMessageId: Guid.Empty,
                UserInputQuestion: userInput.Question);
        }

        // Logged (not silent) so a delegated run that keeps trying to reach a person leaves a trace.
        if (userInput is { CanAsk: false, RefusedCalls: > 0 })
        {
            _logger.LogInformation(
                "Headless run {RunId} refused {RefusedCalls} mid-plan ask(s) on a delegated step; the model was "
                + "redirected to emit_step_result", _runId, userInput.RefusedCalls);
        }

        // ---- hermes #9: the step-success decision ----
        // WAS: `var succeeded = !string.IsNullOrWhiteSpace(exchange.Visible);` — i.e. a step that ran, failed,
        // and then eloquently EXPLAINED its failure recorded Done and the run continued on a false premise.
        // NOW: the step's own structured declaration decides, in both directions. A claim of false is a Failed
        // step no matter how much prose came with it; a claim of true is a Done step even with no visible text.
        // The two catch arms above still short-circuit ahead of this — a cancelled or thrown exchange is not
        // something the model gets a vote on.
        var claim = outcomeStore?.Claim;
        var succeeded = claim?.Succeeded ?? !string.IsNullOrWhiteSpace(exchange.Visible);

        // THE FALLBACK, and why it is not "no call means failure": a step is executed by whatever provider and
        // persona the run resolved, and neither the tool-less provider (SupportsTools=false gets no tools at
        // all) nor a model that simply ignores an instruction is misbehaving in a way the USER should pay for
        // with a failed run. Treating silence as failure would fail-closed on every non-tool-calling provider.
        // So silence keeps the old heuristic — but it is recorded as UNCONFIRMED (Outcome stays null) and the
        // critic is told so, instead of the run pretending the model vouched for the step.
        _logger.LogInformation(
            "Headless run {RunId} step outcome: offered={Offered} confirmed={Confirmed} succeeded={Succeeded} declarations={Declarations}"
            + " artifactReported={ArtifactReported}",
            _runId, outcomeStore is not null, claim is not null, succeeded, outcomeStore?.AcceptedCalls ?? 0,
            !string.IsNullOrWhiteSpace(claim?.ArtifactRef));
        if (claim is not null)
        {
            // Model prose about the user's work — SensitiveDebug only (CLAUDE.md).
            _logger.SensitiveDebug("Step outcome summary: {Summary} artifact: {Artifact}",
                claim.Summary, claim.ArtifactRef);
        }

        var assistantMsgId = Guid.NewGuid();

        // This step's tool calls and their results, ahead of the reply so call and result stay adjacent and in
        // round order. Model context only: _persisted is a different list of a different type and does not grow
        // here, so the chat and a resume's re-seed are unchanged.
        _messages.AddRange(exchange.ToolExchanges);

        // The visible reply IS persisted and IS carried forward as context for later steps.
        _messages.Add(new ChatMessage(ChatRole.Assistant, exchange.Visible));
        _persisted.Add(new SyncAssistantChatMessage
        {
            Id = assistantMsgId,
            Role = "assistant",
            Content = exchange.Visible,
            ThinkingContent = exchange.Thinking,
            Timestamp = DateTime.UtcNow,
            Tokens = exchange.Tokens,
            ModelName = exchange.Model,
            ProviderName = exchange.Provider,
            IsProtectedRoute = exchange.Protected,
            Persona = new SyncMessagePersona { Id = p.Persona.Id, Name = p.Persona.Name, Emoji = p.Persona.Emoji },
        });

        // Unconditional even when this attempt recorded nothing: a previous, PARKED attempt's rows for the
        // same step are still unanchored, and this is the write that finally anchors them. CancellationToken.None
        // for PersistChatAsync's reason — a settle-time write must not be cancelled out from under the row it anchors.
        if (exchanges is not null)
            await exchanges.SealAsync(assistantMsgId).ConfigureAwait(false);

        // E2: this step's reply becomes DURABLE now. Until per-step persistence, EndRunAsync was the ONLY
        // chat write a headless run ever did — so a budget pause (which deliberately skips EndRunAsync and
        // calls the non-terminal OnPausedAsync instead) or a crash mid-run lost every step reply, and the
        // D2 resume seeding then re-seeded an empty chat. Awaited so writes stay serialized within the run.
        if (persistInterim)
            await PersistChatAsync(InterimTitle(), interim: true, CancellationToken.None).ConfigureAwait(false);

        return new StepTurnResult(
            Succeeded: succeeded,
            Cancelled: false,
            // A declared failure carries the model's OWN reason, which is strictly better replan input than
            // the old catch-all "Empty response" — the orchestrator hands this straight to ReplanAsync.
            Error: succeeded ? null : DescribeFailure(claim),
            VisibleText: exchange.Visible,
            Usage: exchange.Usage,
            FirstMessageId: assistantMsgId,
            LastMessageId: assistantMsgId,
            Outcome: claim);
    }

    /// <summary>Awaited before a park result returns: the run flips to WaitingForInput only afterwards, and the
    /// first approval projection reads these rows.</summary>
    private async Task PersistParkedCallsAsync(ToolApprovalStore approvals, Guid? stepId)
    {
        if (_exchangeStore is null || approvals.RecordedCalls.Count == 0)
            return;

        var records = approvals.RecordedCalls;
        try
        {
            // ONCE per pass, ahead of the append: per row, siblings of one tool would cancel each other, and
            // afterwards it would stale the rows just written.
            var toolNames = records.Select(r => r.ToolName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            await _exchangeStore.SupersedeUnreplayedAsync(_runId, toolNames, CancellationToken.None)
                .ConfigureAwait(false);

            var now = DateTime.UtcNow;
            // In RecordedCalls order: the store assigns Seq by list position, and Seq is the replay order.
            var rows = records.Select(r => new AgentToolExchangeRow(
                Id: Guid.NewGuid(),
                RunId: _runId,
                StepId: stepId,
                MessageSeq: 0,
                Seq: 0,
                Round: r.Round,
                Role: "assistant",
                Kind: r.Withheld ? AgentToolExchangeKind.WithheldCall : AgentToolExchangeKind.ParkedCall,
                CallId: string.IsNullOrWhiteSpace(r.CallId) ? string.Empty : r.CallId,
                ToolName: r.ToolName,
                PluginId: r.PluginId,
                ArgumentsJson: r.ArgumentsJson,
                ArgsOmitted: false,
                DisplayArgs: r.DisplayArgs,
                ResultKind: AgentToolExchangeResult.None,
                ResultText: null,
                Chars: r.ArgumentsJson?.Length ?? 0,
                AnchorMessageId: null,
                CreatedAt: now,
                ReplayedAt: null,
                SupersededAt: null)).ToList();

            await _exchangeStore.AppendParkedAsync(rows, CancellationToken.None).ConfigureAwait(false);

            _logger.LogInformation(
                "Headless run {RunId} persisted {RowCount} parked/withheld call(s) for step {StepId} "
                + "({Dropped} dropped, {Chars} arg chars)",
                _runId, rows.Count, stepId, approvals.DroppedRecords, rows.Sum(r => r.Chars));
            _logger.SensitiveDebug("Parked call arguments for run {RunId}: {Calls}", _runId,
                string.Join(" | ", rows.Select(r => r.ToolName + "=" + r.ArgumentsJson)));
        }
        catch (Exception ex)
        {
            // Failure-isolated: the park still happens, and the resume degrades to the pre-store behaviour.
            _logger.LogWarning(ex, "Failed to persist {RowCount} parked call(s) for headless run {RunId}",
                records.Count, _runId);
        }
    }

    public async Task EndRunAsync(AgentRun run, RunContext ctx, bool cancelled, bool failed, CancellationToken ct)
    {
        // The terminal write stays the single source of the FINAL chat (title precedence: LLM-generated >
        // derived-from-goal, unchanged by E2). The interim per-step saves wrote the very same rows with the
        // very same message Ids, so this full replace neither loses nor duplicates a message — and the
        // AgentRun/AgentStep First/LastMessageId slices keep pointing at live rows (R3).
        string? title = null;
        var firstAssistant = _persisted.FirstOrDefault(m => m.Role == "assistant" && !string.IsNullOrWhiteSpace(m.Content));
        if (firstAssistant is not null)
        {
            try { title = await _titleService.GenerateAsync(ctx.Goal, firstAssistant.Content, ct).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "Auto-title failed for headless run {RunId}", run.Id); }
        }
        if (string.IsNullOrWhiteSpace(title))
            title = DeriveTitleFromGoal(ctx.Goal);

        await PersistChatAsync(title, interim: false, ct).ConfigureAwait(false);

        // A settled run has no reader — nothing claims a Completed/Failed/Cancelled run — and the parked rows
        // hold the user's own file contents detokenized, so they must not outlive it. Never on a park or a pause:
        // SafeEndRun is only called on a terminal path.
        if (_exchangeStore is not null)
            await _exchangeStore.PurgeRunAsync(run.Id, CancellationToken.None).ConfigureAwait(false);
        // Ambients are set + restored per step (RunExchangeStepAsync); nothing to restore here.
    }

    /// <summary>
    /// The one chat write both the interim (per-step, E2) and the terminal path use, so an interim save can
    /// never diverge from the terminal one. Failure-isolated (guardrail 1): a persist fault logs a warning
    /// and lets the step/run continue — losing durability is bad, failing a run over bookkeeping is worse.
    /// <para>
    /// COST: <see cref="IAssistantChatService"/> offers no append — a save is a full-chat replace (upsert +
    /// DELETE/re-INSERT of the message rows in one transaction). So per-step durability costs one full
    /// replace per COMPLETED STEP; deliberately not per tool round and not per token.
    /// </para>
    /// <para>
    /// W2b: the write goes through <see cref="IAssistantChatService.SaveMergedAsync"/>, which re-reads the
    /// stored rows and merges back anything this executor did not author INSIDE its own gate hold, so a full
    /// replace can never DELETE another writer's row — not even one committed between this executor's read
    /// and its write, which is precisely what a rebase done out here could not prevent. This run is an
    /// append-only writer (it never removes a message), which is what makes a merging save correct for it.
    /// It also means the merge never touches <see cref="_messages"/> (the run's MODEL CONTEXT): the run's
    /// plan is fixed at <c>BeginRunAsync</c> and feeding it foreign turns would break executor parity, so
    /// the absorbed rows exist only in the row the store writes.
    /// </para>
    /// <paramref name="interim"/> only distinguishes the log/warning text — the caller resolves the title
    /// (<see cref="InterimTitle"/> mid-run, the LLM/derived one at the end).
    /// </summary>
    private async Task PersistChatAsync(string? title, bool interim, CancellationToken ct)
    {
        try
        {
            var chat = BuildChatSnapshot(title);
            var absorbed = await _chatService.SaveMergedAsync(chat, ct).ConfigureAwait(false);
            if (absorbed > 0)
            {
                // Ids + counts only — message content is user content (CLAUDE.md privacy logging).
                _logger.LogInformation(
                    "Headless run {RunId} absorbed {Count} foreign message row(s) of chat {ChatId} into its write",
                    _runId, absorbed, _chatId);
            }
            if (interim)
            {
                _logger.LogInformation("Headless run {RunId} interim-persisted chat {ChatId} ({MessageCount} messages)",
                    _runId, _chatId, chat.Messages.Count);
            }
            else
            {
                _logger.LogInformation("Headless run {RunId} persisted chat {ChatId} ({MessageCount} messages)",
                    _runId, _chatId, chat.Messages.Count);
                _logger.SensitiveDebug("Headless run {RunId} chat title: {Title}", _runId, title);
            }
        }
        catch (Exception ex) when (interim)
        {
            _logger.LogWarning(ex, "Failed to interim-persist headless run {RunId} chat {ChatId}", _runId, _chatId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist headless run {RunId} chat {ChatId}", _runId, _chatId);
        }
    }

    /// <summary>The accumulated transcript as one chat row. Metadata comes from the captured run/chat state.</summary>
    private SyncAssistantChat BuildChatSnapshot(string? title)
    {
        var now = DateTime.UtcNow;
        return new SyncAssistantChat
        {
            Id = _chatId,
            SchemaVersion = 1,
            Title = title,
            CreatedAt = _runCreatedAt == default ? now : _runCreatedAt,
            UpdatedAt = now,
            LastAccessedAt = now,
            WindowMode = WindowMode.Assistant.ToString(),
            // The RUN's provider on purpose, never a step's (Batch 07 G6): this is the chat ROW's provider —
            // what a later interactive turn on this chat would resume on — and a chat has one, whereas a
            // multi-persona run may have used several.
            ProviderId = _provider.Id,
            WorkingDirectory = _existingWorkingDirectory,
            Messages = [.. _persisted],
        };
    }

    /// <summary>
    /// Title for a mid-run save: PROVISIONAL by design. Keep whatever the row already carries (the
    /// launcher's derived title, or an LLM title from an earlier segment of a resumed run) and fall back to
    /// derived-from-goal only when the row has none — the terminal <see cref="EndRunAsync"/> still owns the
    /// final precedence (LLM > derived), so an interim save can neither pre-empt nor downgrade it.
    /// </summary>
    private string? InterimTitle() =>
        string.IsNullOrWhiteSpace(_existingTitle) ? DeriveTitleFromGoal(_goal) : _existingTitle;

    private static string DeriveTitleFromGoal(string goal)
    {
        var collapsed = TextFormatting.CollapseWhitespace(goal);
        return collapsed.Length <= 40 ? collapsed : collapsed[..40].TrimEnd() + "…";
    }

    /// <summary>
    /// Budget-pause hook (guardrail 5): no-op for headless. There is no live session to release, and the
    /// persisted steps/ledger already carry the parked state — and since E2 the parked TRANSCRIPT is durable
    /// too: every completed step wrote itself out, so nothing is left to flush here. Deliberately still NOT
    /// a persist-and-finalize: EndRunAsync would generate the run's FINAL (LLM) title and settle a
    /// non-terminal run. The run resumes out-of-band via ResumeAsync, whose EndRunAsync persists once more.
    /// </summary>
    public Task OnPausedAsync(AgentRun run, RunContext ctx, CancellationToken ct)
    {
        _logger.LogInformation("Headless run {RunId} parked at budget (no session to release)", run.Id);
        return Task.CompletedTask;
    }

    /// <summary>
    /// The failure text a non-succeeding step reports. A declared failure yields the model's own summary (the
    /// replanner then knows WHAT went wrong); a blank summary or no declaration at all keeps the historical
    /// <c>"Empty response"</c>, which is what the no-claim fallback arm literally means.
    /// </summary>
    private static string DescribeFailure(StepOutcomeClaim? claim) =>
        claim is null ? AgentStepTools.EmptyResponseFailure
        : string.IsNullOrWhiteSpace(claim.Summary) ? AgentStepTools.UndetailedFailure
        : claim.Summary;

    private static string BuildInstruction(int ordinal, string intent, string? expectedArtifact)
    {
        var instruction = $"Execute step {ordinal + 1}: {intent}.";
        if (!string.IsNullOrEmpty(expectedArtifact))
            instruction += $" Expected: {expectedArtifact}";
        return instruction + " " + AgentToolCarryover.ReReadHint + " " + RunScratchFolder.StepHint;
    }
}
