using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Pia.Logging;
using Pia.Services.Interfaces;
using Pia.Services.Operators;
using Pia.Shared.Operators;

namespace Pia.Services;

/// <summary>Tools over the background-assignment plane. The list arm projects field by field and the start arm
/// carries no item parameter, so neither an unasked-for artifact nor a model-chosen record leaves through them.</summary>
public class AssignmentToolHandler : IAssignmentToolHandler
{
    private const int ListLimitDefault = 20;
    private const int ListLimitMax = 50;

    private const string TransportFailure =
        "Your Pia server could not be reached, so this is not an answer about your runs — try again.";

    private const string NoRuns = "You have no background assignments.";

    private const string ListNote =
        "Progress only. A finished assignment's result is written into the user's chat history, never returned " +
        "here. Call get_assignment with one assignment_id for that run's detail.";

    private const string UnparseableId =
        "assignment_id must be the id of a run from a query_assignments row.";

    private const string ServerCannotAnswerForThatId =
        "Your Pia server could not answer for that id, so this says nothing about whether the run exists — " +
        "try again.";

    private const string AnswerIsInChatHistory =
        "This run's answer has been stored in the user's own chat history; point them at that chat instead of " +
        "restating it. Use search_chats or read_chat if you need its content.";

    private const string AnswerWillLandInChat =
        "This run's answer has not been collected yet. When it is, it lands in the chat named below.";

    private const string DroppedWithNoLocalRecord =
        "The server has dropped this run's plaintext and this device has no local record of it. Another device " +
        "may already have collected the answer into the user's chat history — look for it with search_chats " +
        "rather than telling them it is gone.";

    private const string AnswerIncluded =
        "This run finished and no local chat holds it yet, so its answer is included here.";

    private const string ProgressOnly = "No answer yet — this is progress only.";

    private const string StartUnavailable =
        "Background assignments are not available on this device, so nothing was proposed.";

    private const string StartNeedsAPrompt =
        "start_assignment needs a prompt saying what the assignment should do. Nothing was proposed.";

    private static readonly string StartPromptTooLong =
        $"That prompt is over the {AssignmentInput.MaxPromptChars}-character cap, so nothing was proposed. "
        + "Shorten it and call start_assignment again.";

    private const string StartDismissed =
        "The user closed the confirmation without sending, so no run exists. Do not offer again unless they "
        + "ask.";

    private const string StartStarted =
        "The assignment was started. Its answer will arrive as a new chat in the user's history, not here, so "
        + "do not promise to fetch it.";

    private const string StartNotAffirmed =
        "The confirmation was not completed, so nothing was read and nothing was sent.";

    private const string StartTooLarge =
        "The selection or prompt is over a published cap, so nothing was sent.";

    private const string StartRefused =
        "Your Pia server refused the assignment or could not be reached, so nothing is running.";

    private const string StartFailed =
        "The confirmation could not be shown, so no assignment was started.";

    private const string StartUnattendedFailed =
        "The assignment could not be started, so nothing is running.";

    private const string StartNeedsANamedSkill =
        "There is no user on this run to pick a skill, so start_assignment needs the name of one of the skills "
        + "listed in the assignments system prompt. Nothing was proposed.";

    private readonly IAssignmentSurfaceCache _surface;
    private readonly IAssignmentApiClient _api;
    private readonly IAssignmentPendingStore _pending;
    private readonly IAssignmentConsentPrompt _prompt;
    private readonly IHeadlessAssignmentLauncher _unattended;
    private readonly ILocalizationService _localization;
    private readonly ILogger<AssignmentToolHandler> _logger;

    public AssignmentToolHandler(
        IAssignmentSurfaceCache surface,
        IAssignmentApiClient api,
        IAssignmentPendingStore pending,
        IAssignmentConsentPrompt prompt,
        IHeadlessAssignmentLauncher unattended,
        ILocalizationService localization,
        ILogger<AssignmentToolHandler> logger)
    {
        _surface = surface;
        _api = api;
        _pending = pending;
        _prompt = prompt;
        _unattended = unattended;
        _localization = localization;
        _logger = logger;
    }

    public bool IsAvailable => _surface.Surface.Available;

    public IList<AITool> GetTools()
    {
        if (!IsAvailable) return [];

        return
        [
            AIFunctionFactory.Create(QueryAssignmentsSchema, "query_assignments"),
            AIFunctionFactory.Create(GetAssignmentSchema, "get_assignment"),
            AIFunctionFactory.Create(StartAssignmentSchema, "start_assignment"),
        ];
    }

    public async Task<(object? Result, AssignmentToolCall? PendingAction)> HandleToolCallAsync(
        FunctionCallContent toolCall,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("AssignmentToolHandler dispatching: {ToolName}", toolCall.Name);
        var args = toolCall.Arguments ?? new Dictionary<string, object?>();

        return toolCall.Name switch
        {
            "query_assignments" => (await HandleQueryAsync(args, cancellationToken), null),
            "get_assignment" => (await HandleGetAsync(args, cancellationToken), null),
            "start_assignment" => PrepareStart(args),
            _ => ($"Unknown tool: {toolCall.Name}", (AssignmentToolCall?)null),
        };
    }

    public async Task<object?> ExecutePendingActionAsync(AssignmentToolCall pendingAction)
    {
        _logger.LogDebug("Executing assignment action: {ToolName}", pendingAction.ToolName);
        try
        {
            var result = await pendingAction.Execute();
            _logger.LogInformation("Assignment action completed: {ToolName}", pendingAction.ToolName);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute assignment tool action: {ToolName}", pendingAction.ToolName);
            return $"Error executing {pendingAction.ToolName}: {ex.Message}";
        }
    }

    private async Task<object?> HandleQueryAsync(IDictionary<string, object?> args, CancellationToken ct)
    {
        var limit = ClampLimit(GetOptionalIntArg(args, "limit"), ListLimitDefault, ListLimitMax);
        var skip = Math.Max(0, GetOptionalIntArg(args, "skip") ?? 0);

        IReadOnlyList<AssignmentDto>? rows;
        try
        {
            rows = await _api.ListAsync(skip, limit, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "query_assignments could not read the run list");
            rows = null;
        }

        if (rows is null)
        {
            _logger.LogInformation("query_assignments: the server did not answer");
            return TransportFailure;
        }

        if (rows.Count == 0) return NoRuns;

        _logger.LogInformation("query_assignments returning {RowCount} run(s)", rows.Count);

        var listed = rows.Select(r => new AssignmentRow(
                r.Id.ToString(),
                r.SkillName,
                r.Mode,
                r.Status,
                r.StepCount,
                r.TokensSpent,
                r.TokensAbandoned,
                Stamp(r.CreatedAt),
                Stamp(r.CompletedAt)))
            .ToList();

        return new AssignmentList(listed, ListNote);
    }

    private async Task<object?> HandleGetAsync(IDictionary<string, object?> args, CancellationToken ct)
    {
        var raw = GetOptionalStringArg(args, "assignment_id");
        if (!Guid.TryParse(raw?.Trim(), out var id)) return UnparseableId;

        AssignmentDto? dto;
        try
        {
            dto = await _api.GetAsync(id, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "get_assignment could not read run {AssignmentId}", id);
            dto = null;
        }

        if (dto is null)
        {
            _logger.LogInformation("get_assignment: the server did not answer for {AssignmentId}", id);
            return ServerCannotAnswerForThatId;
        }

        // The journal, not the outstanding list: a collected run is absent from GetAllAsync, so keying off that
        // would report "no local record" for every run that already finished.
        PendingAssignment? local = null;
        try
        {
            local = (await _pending.GetJournalAsync()).FirstOrDefault(p => p.AssignmentId == id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "get_assignment could not read the local assignment journal");
        }

        var events = (dto.Events ?? [])
            .Select(e => new AssignmentProgressEvent(e.Kind, e.Message, Stamp(e.CreatedAt)))
            .ToList();

        string note;
        string? chatId = null;
        string? answer = null;

        if (local is not null)
        {
            note = local.CollectedAtUtc is not null ? AnswerIsInChatHistory : AnswerWillLandInChat;
            chatId = local.ChatId.ToString();
        }
        else if (dto.PlaintextDroppedAt is not null)
        {
            note = DroppedWithNoLocalRecord;
        }
        else if (!string.IsNullOrWhiteSpace(dto.ArtifactText))
        {
            note = AnswerIncluded;
            answer = dto.ArtifactText;
        }
        else
        {
            note = ProgressOnly;
        }

        _logger.LogInformation(
            "get_assignment {AssignmentId}: status={Status}, events={EventCount}, hasLocalChat={HasLocalChat}, "
            + "returningAnswer={ReturningAnswer}",
            id, dto.Status, events.Count, chatId is not null, answer is not null);

        return new AssignmentProgress(
            dto.Id.ToString(),
            dto.SkillName,
            dto.Mode,
            dto.Status,
            dto.StepCount,
            dto.TokensSpent,
            dto.TokensAbandoned,
            Stamp(dto.CreatedAt),
            Stamp(dto.StartedAt),
            Stamp(dto.CompletedAt),
            dto.ErrorMessage ?? dto.ErrorCode,
            events,
            note,
            chatId,
            answer);
    }

    /// <summary>Refuses before minting a card, so a call that cannot become a run never shows the user one.
    /// The closure carries no cancellation token: the user confirms the card long after the turn that
    /// proposed it.</summary>
    private (object? Result, AssignmentToolCall? PendingAction) PrepareStart(IDictionary<string, object?> args)
    {
        if (!IsAvailable) return (StartUnavailable, null);

        var prompt = GetOptionalStringArg(args, "prompt")?.Trim();
        if (string.IsNullOrWhiteSpace(prompt)) return (StartNeedsAPrompt, null);
        if (prompt.Length > AssignmentInput.MaxPromptChars) return (StartPromptTooLong, null);

        var skill = GetOptionalStringArg(args, "skill")?.Trim();
        if (skill?.Length == 0) skill = null;

        // Read HERE, not in the closure: the ambient is restored the moment the turn's exchange returns, and
        // on the interactive path the card is confirmed long after that.
        var granter = TaskAmbient.Current?.UnattendedGranter;

        AssignmentSkill? unattended = null;
        if (granter is not null)
        {
            unattended = ResolveUnattendedSkill(skill);
            if (unattended is null) return (StartNeedsANamedSkill, null);
        }
        else if (skill is not null && _surface.FindSkill(skill) is null)
        {
            // The dialog silently falls back to its first skill, so a name it cannot resolve would put one
            // skill on the card and run another. Drop it and let the card say the user picks.
            skill = null;
        }

        var label = unattended?.DisplayName
            ?? (skill is null ? null : _surface.FindSkill(skill)?.DisplayName)
            ?? _localization["Tool_Assignment_Skill_Unset"];

        // The resolved label, never the argument: a model-authored skill name can carry anything.
        _logger.LogInformation(
            "start_assignment proposing a run on '{Skill}' (granter: {Granter})", label, granter ?? "user");
        _logger.SensitiveDebug(
            "start_assignment proposed skill: {Skill}, prompt: {Prompt}", skill ?? "(unset)", prompt);

        var pending = new AssignmentToolCall(
            "start_assignment",
            _localization.Format("Tool_Assignment_Desc_Start", label),
            $"{_localization["Tool_Assignment_Detail_Skill"]}: {label}\n"
            + _localization.Format("Tool_Assignment_Detail_PromptLength", prompt.Length),
            async () =>
            {
                // The card's own executor is bypassed on both real confirm paths, so a throw here would reach
                // the turn raw.
                try
                {
                    return granter is null
                        ? StartResult(await _prompt.PromptAsync(skill, prompt))
                        : StartResult(await _unattended.StartAsync(unattended!, prompt, granter));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "start_assignment could not be carried out");
                    return granter is null ? StartFailed : StartUnattendedFailed;
                }
            });

        return (null, pending);
    }

    /// <summary>No human to choose one, so an unnamed skill is only allowed when there is nothing to choose
    /// between — the interactive path deliberately falls back to the first instead.</summary>
    private AssignmentSkill? ResolveUnattendedSkill(string? name)
    {
        if (name is not null) return _surface.FindSkill(name);

        var skills = _surface.Surface.Skills;
        return skills.Count == 1 ? skills[0] : null;
    }

    private static string StartResult(AssignmentStartStatus? status) => status switch
    {
        null => StartDismissed,
        AssignmentStartStatus.Started => StartStarted,
        AssignmentStartStatus.ConsentMissing => StartNotAffirmed,
        AssignmentStartStatus.TooLarge => StartTooLarge,
        _ => StartRefused,
    };

    /// <summary>An untagged wire timestamp is UTC already: System.Text.Json only marks it so when the JSON
    /// carried a <c>Z</c>, and converting it would shift every stamp by the reader's own offset.</summary>
    private static string Stamp(DateTime value)
    {
        var utc = value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };

        // Slashes, not hyphens: the PII detector reads a hyphenated date as a phone number and the model
        // would receive "[Phone_1]:46 UTC". Its character class has no '/', so this breaks the digit run.
        return utc.ToString("yyyy/MM/dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
    }

    private static string? Stamp(DateTime? value) => value is null ? null : Stamp(value.Value);

    /// <summary>Clamps in BOTH directions: a missing, zero or negative limit becomes the default.</summary>
    private static int ClampLimit(int? requested, int fallback, int max) =>
        requested is null or <= 0 ? fallback : Math.Min(requested.Value, max);

    private static string? GetOptionalStringArg(IDictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value is null) return null;

        if (value is JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Null) return null;
            return element.ValueKind == JsonValueKind.String ? element.GetString() : element.GetRawText();
        }

        var str = value.ToString();
        return string.IsNullOrEmpty(str) ? null : str;
    }

    private static int? GetOptionalIntArg(IDictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value is null) return null;

        if (value is JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var n)) return n;
            if (element.ValueKind == JsonValueKind.String && int.TryParse(element.GetString(), out var parsed))
                return parsed;
            return null;
        }

        if (value is int i) return i;
        if (value is long l) return (int)l;
        return int.TryParse(value.ToString(), out var fallback) ? fallback : null;
    }

    // Schema methods — the parameter signature and [Description] attributes ARE the tool metadata for
    // AIFunctionFactory. The body is never invoked (dispatch is by tool name in HandleToolCallAsync).
    [Description("List the user's background assignments — work handed to their Pia server to run on its own — newest first. Reports status and progress; a finished run's result is delivered into the user's chat history, not here.")]
    private static string QueryAssignmentsSchema(
        [Description("Max runs to return (default 20, max 50)")] int? limit = null,
        [Description("Runs to skip, for paging older ones (default 0)")] int? skip = null) => "";

    [Description("Ask how one background assignment is going, by id from a query_assignments row. Reports progress rather than the answer: a collected run's answer lives in the user's chat history.")]
    private static string GetAssignmentSchema(
        [Description("assignment_id from a query_assignments row")] string assignment_id) => "";

    // No item / record / entity parameter, in any spelling: its absence is what stops the model choosing
    // which of the user's records leave the encrypted side.
    [Description("Hand the user's request to their Pia server as a background assignment. In a chat this only PROPOSES one: the user is shown a confirmation and chooses which of their own records, if any, are sent. In a background run that has been granted this tool there is no confirmation — the call starts the run at once, with no records attached — so never call it speculatively or to offer the user options.")]
    private static string StartAssignmentSchema(
        [Description("The skill to run, from the names listed in the assignments system prompt. Omit to let the user pick.")] string? skill = null,
        [Description("What the assignment should do, self-contained — the run cannot ask a follow-up question.")] string prompt = "") => "";

    /// <summary>snake_case: these serialize straight to the provider. There is no artifact field — the list
    /// route carries none and this tool must not fetch one per row.</summary>
    private sealed record AssignmentRow(
        string assignment_id,
        string skill,
        string? mode,
        string status,
        int step_count,
        int tokens_spent,
        int tokens_abandoned,
        string created_at,
        string? completed_at);

    private sealed record AssignmentList(IReadOnlyList<AssignmentRow> assignments, string note);

    private sealed record AssignmentProgressEvent(string kind, string? message, string at);

    private sealed record AssignmentProgress(
        string assignment_id,
        string skill,
        string? mode,
        string status,
        int step_count,
        int tokens_spent,
        int tokens_abandoned,
        string created_at,
        string? started_at,
        string? completed_at,
        string? error,
        IReadOnlyList<AssignmentProgressEvent> events,
        string note,
        string? chat_id,
        string? answer);
}
