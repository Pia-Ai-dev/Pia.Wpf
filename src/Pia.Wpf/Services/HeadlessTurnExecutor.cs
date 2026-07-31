using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Pia.Helpers;
using Pia.Logging;
using Pia.Models;
using Pia.Services.Interfaces;
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

    // Per-run accumulating state.
    private readonly List<ChatMessage> _messages = new();
    private readonly List<SyncAssistantChatMessage> _persisted = new();
    private readonly HashSet<string> _grantedWrites = new(StringComparer.OrdinalIgnoreCase);
    private AssistantTurnSetup _setup = default!;
    private Persona _persona = default!;
    private AiProvider _provider = default!;
    private ITokenMapService? _tokenMap;
    private bool _tokenizationEnabled;
    private Guid _chatId;
    private Guid _runId;

    // Captured at BeginRunAsync so the interim (per-step) and the terminal chat write build the SAME
    // row from one place — an interim save must never drop a column the terminal save preserves.
    private DateTime _runCreatedAt;
    private string _goal = string.Empty;
    private string? _existingTitle;
    private string? _existingWorkingDirectory;

    // Seeded by the launcher via Initialize before the orchestrator runs (§17.3).
    private string? _workspaceRoot;
    private AiProvider? _providerOverride;

    /// <summary>This run's autonomy policy (Batch 04); null ⇒ no per-run policy, i.e. today's behaviour.</summary>
    private RunAutonomyPolicy? _policy;

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
        IAgentTimelineService? timelineService = null)
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
    }

    /// <summary>
    /// Seed the granted write tools, an optional provider override (the launcher's resolved provider, kept
    /// in lock-step with the orchestrator's planner so the two never diverge), and the run's isolated
    /// workspace root. Called from the launcher's fresh DI scope BEFORE <c>orchestrator.RunAsync</c>.
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
    public void Initialize(
        string? workspaceRoot,
        IReadOnlyCollection<string> grantedWrites,
        AiProvider? providerOverride = null,
        RunAutonomyPolicy? policy = null)
    {
        _workspaceRoot = workspaceRoot;
        _providerOverride = providerOverride;
        _policy = policy;
        _grantedWrites.Clear();
        foreach (var w in grantedWrites)
            _grantedWrites.Add(w);
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

        _persona = await _personaService.ResolveActiveAsync(
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
        if (chat is { Messages.Count: > 0 })
        {
            // Resume: seed from the persisted transcript so the terminal full-replace PRESERVES prior
            // rows. No synthetic goal (it is already in the transcript).
            foreach (var m in chat.Messages)
            {
                _messages.Add(new ChatMessage(ParseRole(m.Role), m.Content));
                _persisted.Add(m);
            }
        }
        else
        {
            // Fresh launch: stub chat is empty → seed the goal as the opening user message (as before).
            var goalMsgId = Guid.NewGuid();
            _messages.Add(new ChatMessage(ChatRole.User, ctx.Goal));
            _persisted.Add(new SyncAssistantChatMessage
            {
                Id = goalMsgId,
                Role = "user",
                Content = ctx.Goal,
                Timestamp = DateTime.UtcNow,
            });
        }
    }

    private static ChatRole ParseRole(string role) => role switch
    {
        "system" => ChatRole.System,
        "assistant" => ChatRole.Assistant,
        "tool" => ChatRole.Tool,
        _ => ChatRole.User,
    };

    public Task<StepTurnResult> ExecuteStepAsync(AgentRun run, AgentStep step, RunContext ctx, CancellationToken ct) =>
        RunExchangeStepAsync(BuildInstruction(step.Ordinal, step.Intent ?? string.Empty, step.ExpectedArtifact),
            persistInterim: true, ct, TimelineScope(step.Id));

    // persistInterim: false — the fallback turn is followed IMMEDIATELY by the terminal EndRunAsync on
    // every branch of the R10 degrade path, so an interim write here would only double the chat rewrite.
    // stepId: null — the degrade turn belongs to the run but to no step.
    public Task<StepTurnResult> RunSingleTurnFallbackAsync(AgentRun run, RunContext ctx, CancellationToken ct) =>
        RunExchangeStepAsync(ctx.Goal, persistInterim: false, ct, TimelineScope(stepId: null));

    /// <summary>The per-step audit sink, or null when no store was injected (⇒ record nothing).</summary>
    private AgentTimelineScope? TimelineScope(Guid? stepId) =>
        _timelineService is null ? null : new AgentTimelineScope(_timelineService, _runId, stepId);

    private async Task<StepTurnResult> RunExchangeStepAsync(
        string instruction, bool persistInterim, CancellationToken ct, AgentTimelineScope? timeline = null)
    {
        // Append the EPHEMERAL step instruction to a COPY — the accumulating _messages keeps the
        // clean transcript (system + goal + one assistant reply per step) — §13.7.
        var exchangeMessages = new List<ChatMessage>(_messages)
        {
            new(ChatRole.User, instruction),
        };

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
        var contextBudget = AgentContextBudget.From(_provider);
        var request = await AgentContextCompactor
            .CompactAsync(exchangeMessages, contextBudget, _logger, ct)
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
        TaskAmbient.Current = new TaskContext(_runId, WorkingSubpath: null, OnFileTouched: null, WorkspaceRoot: _workspaceRoot);

        BackgroundAssistantTurnRunner.ExchangeResult exchange;
        try
        {
            // The same budget goes down into the exchange so the IN-step tool loop is bounded too: the
            // request compacted above can still grow past the window inside AiClientService as tool
            // calls and tool results accumulate over up to 10 rounds.
            exchange = await _engine.RunExchangeAsync(request, _provider, _setup, _grantedWrites, ct,
                    onUsage: null, contextBudget: contextBudget, policy: _policy, timeline: timeline)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new StepTurnResult(false, true, "cancelled", string.Empty, null, Guid.Empty, Guid.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Headless step exchange failed for chat {ChatId}", _chatId);
            return new StepTurnResult(false, false, ex.Message, string.Empty, null, Guid.Empty, Guid.Empty);
        }
        finally
        {
            TokenMapAmbient.Current = previousAmbient;
            TaskAmbient.Current = previousTask;
        }

        var succeeded = !string.IsNullOrWhiteSpace(exchange.Visible);
        var assistantMsgId = Guid.NewGuid();

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
            Persona = new SyncMessagePersona { Id = _persona.Id, Name = _persona.Name, Emoji = _persona.Emoji },
        });

        // E2: this step's reply becomes DURABLE now. Until per-step persistence, EndRunAsync was the ONLY
        // chat write a headless run ever did — so a budget pause (which deliberately skips EndRunAsync and
        // calls the non-terminal OnPausedAsync instead) or a crash mid-run lost every step reply, and the
        // D2 resume seeding then re-seeded an empty chat. Awaited so writes stay serialized within the run.
        if (persistInterim)
            await PersistChatAsync(InterimTitle(), interim: true, CancellationToken.None).ConfigureAwait(false);

        return new StepTurnResult(
            Succeeded: succeeded,
            Cancelled: false,
            Error: succeeded ? null : "Empty response",
            VisibleText: exchange.Visible,
            Usage: exchange.Usage,
            FirstMessageId: assistantMsgId,
            LastMessageId: assistantMsgId);
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

    private static string BuildInstruction(int ordinal, string intent, string? expectedArtifact)
    {
        var instruction = $"Execute step {ordinal + 1}: {intent}.";
        if (!string.IsNullOrEmpty(expectedArtifact))
            instruction += $" Expected: {expectedArtifact}";
        return instruction;
    }
}
