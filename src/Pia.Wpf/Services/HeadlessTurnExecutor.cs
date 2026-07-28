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

    public HeadlessTurnExecutor(
        BackgroundAssistantTurnRunner engine,
        IAssistantChatService chatService,
        ISettingsService settingsService,
        IPersonaService personaService,
        IProviderService providerService,
        IAssistantPromptComposer promptComposer,
        IChatTitleService titleService,
        Func<ITokenMapService> tokenMapFactory,
        ILogger<HeadlessTurnExecutor> logger)
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
    }

    /// <summary>
    /// Seed the granted write tools, an optional provider override (the launcher's resolved provider, kept
    /// in lock-step with the orchestrator's planner so the two never diverge), and an optional per-run
    /// workspace root. Called from the launcher's fresh DI scope BEFORE <c>orchestrator.RunAsync</c>.
    /// <para>
    /// <paramref name="workspaceRoot"/> is <c>null</c> for a normal unattended run: real deliverables are
    /// written to the user's assistant files folder with full read/write/delete, contained (no escape, no
    /// system paths) exactly like an interactive chat — only MCP is withheld. A non-null value instead
    /// confines every file operation to that folder; it is the reserved seam for a future opt-in per-run
    /// sandbox. The run's <c>%LOCALAPPDATA%\Pia\runs\&lt;runId&gt;</c> directory remains the ephemeral
    /// scratch/temp area (auto-cleaned), separate from where real deliverables land.
    /// </para>
    /// </summary>
    public void Initialize(string? workspaceRoot, IReadOnlyCollection<string> grantedWrites, AiProvider? providerOverride = null)
    {
        _workspaceRoot = workspaceRoot;
        _providerOverride = providerOverride;
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
        // W2: this snapshot is no longer taken ONCE and trusted for the rest of the run —
        // RebaseOnPersistedRowsAsync re-reads the persisted rows before EVERY write and absorbs anything this
        // executor did not author, so a concurrent writer's rows survive the full replace. What is seeded
        // HERE is still the only thing that reaches _messages (the model context); the rebase deliberately
        // does not touch it.
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
            persistInterim: true, ct);

    // persistInterim: false — the fallback turn is followed IMMEDIATELY by the terminal EndRunAsync on
    // every branch of the R10 degrade path, so an interim write here would only double the chat rewrite.
    public Task<StepTurnResult> RunSingleTurnFallbackAsync(AgentRun run, RunContext ctx, CancellationToken ct) =>
        RunExchangeStepAsync(ctx.Goal, persistInterim: false, ct);

    private async Task<StepTurnResult> RunExchangeStepAsync(string instruction, bool persistInterim, CancellationToken ct)
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
                    onUsage: null, contextBudget: contextBudget)
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
    /// COST: <see cref="IAssistantChatService"/> offers no append — <c>SaveAsync</c> is a full-chat replace
    /// (upsert + DELETE/re-INSERT of the message rows in one transaction). So per-step durability costs one
    /// full replace per COMPLETED STEP; deliberately not per tool round and not per token.
    /// </para>
    /// <paramref name="interim"/> only distinguishes the log/warning text — the caller resolves the title
    /// (<see cref="InterimTitle"/> mid-run, the LLM/derived one at the end).
    /// </summary>
    private async Task PersistChatAsync(string? title, bool interim, CancellationToken ct)
    {
        try
        {
            await RebaseOnPersistedRowsAsync(ct).ConfigureAwait(false);
            var chat = BuildChatSnapshot(title);
            await _chatService.SaveAsync(chat, ct).ConfigureAwait(false);
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

    /// <summary>
    /// W2: absorb any row this executor did not author into <see cref="_persisted"/> before every write, so a
    /// full replace can never DELETE a row written by another writer. Rows are matched by <c>Id</c> and
    /// appended in DB ordinal order.
    /// <para>
    /// Keying on <c>Id</c> is mandatory: <c>Ordinal</c> is not identity, it is the writer's loop index
    /// (<c>AssistantChatService</c> renumbers from 0 on every replace), whereas Ids round-trip through
    /// <c>AssistantMessageMapper</c> and the D2 resume seeding, and are what
    /// <see cref="AgentStep.FirstMessageId"/>/<see cref="AgentStep.LastMessageId"/> name.
    /// </para>
    /// <para>
    /// It touches <see cref="_persisted"/> (the DURABLE transcript) ONLY — never <see cref="_messages"/> (the
    /// run's MODEL CONTEXT). Injecting foreign turns into a mid-flight run's context would change its
    /// behaviour unpredictably and break executor parity: the run's plan is fixed at
    /// <c>BeginRunAsync</c>, and the live executor does not feed it foreign turns either.
    /// </para>
    /// <para>
    /// Called from inside <see cref="PersistChatAsync"/>'s try, so a read fault degrades to the previous
    /// behaviour (write the run's own transcript) and can never fail the step (guardrail 1). A chat deleted
    /// mid-run reads back null and the rebase is a no-op. Costs one extra GetAsync per completed step — a
    /// read, NOT a second SaveAsync, so the per-step write-cost assertion stays at one write per step.
    /// </para>
    /// </summary>
    private async Task RebaseOnPersistedRowsAsync(CancellationToken ct)
    {
        var stored = await _chatService.GetAsync(_chatId, ct).ConfigureAwait(false);
        if (stored is null || stored.Messages.Count == 0)
            return;

        var known = _persisted.Select(m => m.Id).ToHashSet();
        var absorbed = 0;
        foreach (var row in stored.Messages)
        {
            if (!known.Add(row.Id))
                continue;
            _persisted.Add(row);
            absorbed++;
        }

        if (absorbed > 0)
        {
            // Ids + counts only — message content is user content (CLAUDE.md privacy logging).
            _logger.LogInformation(
                "Headless run {RunId} absorbed {Count} foreign message row(s) from chat {ChatId} before writing",
                _runId, absorbed, _chatId);
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
