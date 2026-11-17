using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Pia.Logging;
using Pia.Models;
using Pia.Services.Interfaces;

namespace Pia.Services;

public sealed class ChatJobToolService : IChatJobToolService
{
    private const int MaxNoteChars = 1000;
    private const int MaxReplyChars = 4000;
    private const int MaxCallTextChars = 500;
    internal static TimeSpan RestartPauseTimeout { get; set; } = TimeSpan.FromSeconds(60);

    private static readonly IReadOnlyList<AITool> Tools = JobTools.Build();

    private readonly ISettingsService _settings;
    private readonly IAgentRunService _runs;
    private readonly IJobSteeringStore _jobs;
    private readonly IAgentRunSteeringService _pause;
    private readonly IRunSteeringStore _steering;
    private readonly IAgentRunResumeService _resume;
    private readonly IAssistantChatService _chats;
    private readonly IRunAskBroker? _askBroker;
    private readonly IAgentToolExchangeStore? _toolCalls;
    private readonly ILogger<ChatJobToolService> _logger;

    public ChatJobToolService(
        ISettingsService settings, IAgentRunService runs, IJobSteeringStore jobs, IAgentRunSteeringService pause,
        IRunSteeringStore steering, IAgentRunResumeService resume, IAssistantChatService chats,
        ILogger<ChatJobToolService> logger, IRunAskBroker? askBroker = null, IAgentToolExchangeStore? toolCalls = null)
    {
        _settings = settings;
        _runs = runs;
        _jobs = jobs;
        _pause = pause;
        _steering = steering;
        _resume = resume;
        _chats = chats;
        _logger = logger;
        _askBroker = askBroker;
        _toolCalls = toolCalls;
        _runs.RunChanged += OnRunChanged;
    }

    private void OnRunChanged(object? sender, AgentRunChangedEventArgs e)
    {
        if (e.State is AgentRunState.Completed or AgentRunState.Failed or AgentRunState.Cancelled)
        {
            var unapplied = _jobs.PeekPlanChanges(e.RunId).Count;
            if (unapplied > 0)
                _logger.LogInformation("Run {RunId} settled {State} with {Count} plan change(s) not applied",
                    e.RunId, e.State, unapplied);
            _jobs.Forget(e.RunId);
        }
        else if (AgentRunStates.IsParked(e.State))
            _jobs.DropPauseAfterStep(e.RunId);
    }

    public bool Handles(string toolName) => JobTools.Names.Contains(toolName);

    public async Task<ChatJobTurn?> PrepareTurnAsync(Guid? chatId, CancellationToken ct = default)
    {
        try
        {
            if (await FindJobAsync(chatId, ct) is not { } run)
                return null;
            return new ChatJobTurn(run.Id, BuildBlock(run), JobTools.PromptAddition, Tools);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Preparing the job block for chat {ChatId} failed", chatId);
            return null;
        }
    }

    public async Task<AgentRunLookup?> FindUnfinishedChatJobAsync(Guid? chatId, CancellationToken ct = default)
    {
        try
        {
            return await FindJobAsync(chatId, ct) is { } run
                ? new AgentRunLookup(run.Id, run.State, AgentRunStates.IsExecuting(run.State))
                : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Looking up the chat job of chat {ChatId} failed", chatId);
            return null;
        }
    }

    public async Task<bool> TypedMessageIsTurnAsync(Guid runId, CancellationToken ct = default)
    {
        try
        {
            if (!(await _settings.GetSettingsAsync()).UnifiedAssistantFlowEnabled)
                return false;
            var run = await Task.Run(() => _runs.GetAsync(runId, ct), ct);
            if (run is null || run.ParentRunId is not null || !run.IsChatJob())
                return false;
            var reason = RunPauseEnvelope.ReadReason(run);
            return reason == AgentRunOrchestrator.NeedsInputReason || IsInterruption(reason);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Reading run {RunId} for a typed message failed", runId);
            return false;
        }
    }

    public async Task<string> HandleAsync(
        Guid chatId, string toolName, IDictionary<string, object?>? arguments, CancellationToken ct = default)
    {
        try
        {
            var run = await FindJobAsync(chatId, ct);
            if (run is null)
                return "This chat has no unfinished job.";

            _logger.LogInformation("Job tool {ToolName} on run {RunId} ({State})", toolName, run.Id, run.State);
            var result = toolName switch
            {
                JobTools.StepDetails => await StepDetailsAsync(run, GetString(arguments, "step"), ct),
                JobTools.UpdatePlan => await UpdatePlanAsync(run, arguments, ct),
                JobTools.Note => await NoteAsync(run, GetString(arguments, "note")),
                JobTools.Pause => await PauseAsync(run, GetBool(arguments, "afterThisStep"), ct),
                JobTools.RestartStep => await RestartStepAsync(run, GetString(arguments, "note"), ct),
                JobTools.Continue => await ContinueAsync(run, ct),
                JobTools.Answer => await AnswerAsync(run, GetString(arguments, "answer"), ct),
                JobTools.Stop => await StopAsync(run, ct),
                _ => "Unknown tool.",
            };
            _logger.SensitiveDebug("Job tool {ToolName} result: {Result}", toolName, result);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Job tool {ToolName} failed for chat {ChatId}", toolName, chatId);
            return "The job could not be reached. Nothing was changed.";
        }
    }

    private async Task<AgentRun?> FindJobAsync(Guid? chatId, CancellationToken ct)
    {
        if (chatId is not { } id || !(await _settings.GetSettingsAsync()).UnifiedAssistantFlowEnabled)
            return null;
        var runs = await Task.Run(() => _runs.GetByChatAsync(id, ct), ct);
        var job = runs
            .Where(r => r.ParentRunId is null
                && r.State is not (AgentRunState.Completed or AgentRunState.Failed or AgentRunState.Cancelled)
                && r.IsChatJob())
            .OrderBy(r => r.CreatedAt)
            .LastOrDefault();
        return job is null ? null : await Task.Run(() => _runs.GetAsync(job.Id, ct), ct) ?? job;
    }

    private async Task<AgentRun> ReloadAsync(AgentRun run, CancellationToken ct) =>
        await Task.Run(() => _runs.GetAsync(run.Id, ct), ct) ?? run;

    private string BuildBlock(AgentRun run) =>
        JobTurnBlockBuilder.Build(run, _jobs.PeekPlanChanges(run.Id), _jobs.ChangedSteps(run.Id),
            _jobs.IsPauseAfterStepRequested(run.Id), _askBroker?.GetPending(run.Id),
            _askBroker?.GetUserInputQuestion(run.Id), _jobs.TakeDropped(run.Id), DateTime.UtcNow);

    private async Task<string> WithBlockAsync(string message, AgentRun run, CancellationToken ct) =>
        message + "\n\n" + BuildBlock(await ReloadAsync(run, ct));

    private async Task<string> StepDetailsAsync(AgentRun run, string? handle, CancellationToken ct)
    {
        if (ResolveStep(run, handle) is not { } step)
            return "No step of the job has that id. Use an id from the job block.";

        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Step [{JobTurnBlockBuilder.Handle(step.Id)}] {step.Title} ({step.Status})");
        if (!string.IsNullOrWhiteSpace(step.Intent))
            sb.AppendLine("Task: " + step.Intent);

        var reply = await ReadReplyAsync(run.ChatId, step, ct);
        sb.AppendLine(reply is null ? "Reply: none yet." : "Reply:\n" + Cap(reply, MaxReplyChars));

        if (_toolCalls is not null)
        {
            var calls = await Task.Run(() => _toolCalls.GetStepCallsAsync(run.Id, step.Id, ct), ct);
            sb.AppendLine(calls.Calls.Count == 0 ? "Tool calls: none recorded." : "Tool calls:");
            foreach (var call in calls.Calls)
            {
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"- {call.ToolName ?? "unknown"}({Cap(call.Arguments ?? string.Empty, MaxCallTextChars)}) -> {Cap(call.Result ?? "(no result)", MaxCallTextChars)}");
            }
        }

        return sb.ToString().TrimEnd();
    }

    private async Task<string?> ReadReplyAsync(Guid chatId, AgentStep step, CancellationToken ct)
    {
        if (step.FirstMessageId is not { } first)
            return null;
        var chat = await Task.Run(() => _chats.GetAsync(chatId, ct), ct);
        if (chat is null)
            return null;
        var start = chat.Messages.FindIndex(m => m.Id == first);
        if (start < 0)
            return null;
        var end = step.LastMessageId is { } last ? chat.Messages.FindIndex(m => m.Id == last) : start;
        if (end < start)
            end = start;
        var text = string.Join("\n\n", chat.Messages.Skip(start).Take(end - start + 1)
            .Where(m => m.Role == "assistant" && !string.IsNullOrWhiteSpace(m.Content))
            .Select(m => m.Content));
        return text.Length == 0 ? null : text;
    }

    private async Task<string> UpdatePlanAsync(AgentRun run, IDictionary<string, object?>? arguments, CancellationToken ct)
    {
        if (run.State is AgentRunState.Planning or AgentRunState.Verifying)
            return "The plan cannot change right now: the job is " +
                (run.State == AgentRunState.Planning ? "still planning." : "checking its result.");

        var parsed = ParseChanges(run, arguments, out var error);
        if (parsed is null)
            return error!;

        await DiscardParkedCallsOfChangedStepsAsync(run, parsed, ct);

        var reason = AgentRunStates.IsParked(run.State) ? RunPauseEnvelope.ReadReason(run) : null;
        var direct = run.State == AgentRunState.Paused
            || (run.State == AgentRunState.WaitingForInput && reason == AgentRunOrchestrator.PlanApprovalReason);
        if (direct && await TryApplyDirectlyAsync(run, parsed, ct))
            return await WithBlockAsync("Changed the plan.", run, ct);

        _jobs.QueuePlanChanges(run.Id, parsed);
        _logger.LogInformation("Run {RunId}: queued {Count} plan change(s)", run.Id, parsed.Count);
        return await WithBlockAsync(
            "Queued the change; the job applies it before its next step. The block shows it already.", run, ct);
    }

    // An approval given for the old wording of a step must not run the call that wording produced.
    private async Task DiscardParkedCallsOfChangedStepsAsync(AgentRun run, IReadOnlyList<JobPlanChange> changes,
        CancellationToken ct)
    {
        if (_toolCalls is null)
            return;
        foreach (var stepId in changes.Where(c => c.Kind is JobPlanChangeKind.Edit or JobPlanChangeKind.Remove)
                     .Select(c => c.StepId).Distinct())
        {
            var discarded = await Task.Run(() => _toolCalls.SupersedeStepUnreplayedAsync(run.Id, stepId, ct), ct);
            if (discarded > 0)
                _logger.LogInformation("Run {RunId}: discarded {Count} parked call(s) of changed step {StepId}",
                    run.Id, discarded, stepId);
        }
    }

    private async Task<bool> TryApplyDirectlyAsync(AgentRun run, IReadOnlyList<JobPlanChange> changes, CancellationToken ct)
    {
        var all = _jobs.TakePlanChanges(run.Id).Concat(changes).ToList();
        var before = JobPlanChanges.PendingTail(run);
        var beforeIds = before.Select(s => s.Id).ToHashSet();
        var (tail, touched, _) = JobPlanChanges.Apply(run.Id, before, all);
        var edits = tail.Select(s => new PlanStepEdit(beforeIds.Contains(s.Id) ? s.Id : null, s.Title, s.Intent,
            s.ExpectedArtifact)).ToList();

        var result = await Task.Run(() => _runs.ApplyPlanMutationAsync(run.Id, edits, ct), ct);
        if (result.Outcome != PlanMutationOutcome.Applied)
        {
            _jobs.QueuePlanChanges(run.Id, all.Except(changes).ToList());
            _logger.LogInformation("Run {RunId}: direct plan change refused ({Outcome})", run.Id, result.Outcome);
            return false;
        }

        var after = JobPlanChanges.PendingTail(await ReloadAsync(run, ct));
        _jobs.MarkChanged(run.Id, after.Select(s => s.Id).Where(id => !beforeIds.Contains(id))
            .Concat(touched.Where(beforeIds.Contains)));
        return true;
    }

    private List<JobPlanChange>? ParseChanges(AgentRun run, IDictionary<string, object?>? arguments, out string? error)
    {
        error = null;
        var queued = _jobs.PeekPlanChanges(run.Id);
        var (tail, _, _) = JobPlanChanges.Apply(run.Id, JobPlanChanges.PendingTail(run), queued);
        var known = tail.ToDictionary(s => JobTurnBlockBuilder.Handle(s.Id), s => s.Id, StringComparer.OrdinalIgnoreCase);
        var started = run.Plan.Where(s => s.Status != AgentStepStatus.Pending)
            .Select(s => JobTurnBlockBuilder.Handle(s.Id)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (arguments is null || !arguments.TryGetValue("changes", out var raw) || ToElement(raw) is not
            { ValueKind: JsonValueKind.Array } array || array.GetArrayLength() == 0)
        {
            error = "No changes were given. Nothing was changed.";
            return null;
        }

        var changes = new List<JobPlanChange>();
        foreach (var item in array.EnumerateArray())
        {
            var action = Prop(item, "action")?.Trim().ToLowerInvariant();
            var stepHandle = Prop(item, "step")?.Trim();
            var afterHandle = Prop(item, "after")?.Trim();
            var title = Prop(item, "title");
            var details = Prop(item, "details");

            Guid? stepId = null;
            if (action != "insert")
            {
                if (stepHandle is not null && started.Contains(stepHandle))
                {
                    error = $"Step {stepHandle} has already started or finished and cannot change. Nothing was changed.";
                    return null;
                }
                if (stepHandle is null || !known.TryGetValue(stepHandle, out var id))
                {
                    error = $"No step of the job that has not started has the id '{stepHandle}'. Nothing was changed.";
                    return null;
                }
                stepId = id;
            }

            Guid? after = null;
            var atStart = string.Equals(afterHandle, "start", StringComparison.OrdinalIgnoreCase);
            if (!atStart && !string.IsNullOrEmpty(afterHandle))
            {
                var anchor = run.Plan.FirstOrDefault(s => JobTurnBlockBuilder.Handle(s.Id)
                    .Equals(afterHandle, StringComparison.OrdinalIgnoreCase))?.Id;
                if (anchor is null && known.TryGetValue(afterHandle, out var pendingAnchor))
                    anchor = pendingAnchor;
                if (anchor is null)
                {
                    error = $"No step has the id '{afterHandle}'. Nothing was changed.";
                    return null;
                }
                after = anchor;
            }

            switch (action)
            {
                case "edit" when string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(details):
                    error = "An edit needs a new title or new details. Nothing was changed.";
                    return null;
                case "edit":
                    changes.Add(new JobPlanChange(JobPlanChangeKind.Edit, stepId!.Value, Title: title, Intent: details));
                    break;
                case "insert" when string.IsNullOrWhiteSpace(title):
                    error = "A new step needs a title. Nothing was changed.";
                    return null;
                case "insert":
                    var newId = Guid.NewGuid();
                    changes.Add(new JobPlanChange(JobPlanChangeKind.Insert, newId, after, atStart, title, details));
                    known[JobTurnBlockBuilder.Handle(newId)] = newId;
                    break;
                case "remove":
                    changes.Add(new JobPlanChange(JobPlanChangeKind.Remove, stepId!.Value));
                    break;
                case "move":
                    changes.Add(new JobPlanChange(JobPlanChangeKind.Move, stepId!.Value, after, atStart));
                    break;
                default:
                    error = $"Unknown action '{action}'; use edit, insert, remove or move. Nothing was changed.";
                    return null;
            }
        }

        var (check, _, _) = JobPlanChanges.Apply(run.Id, tail, changes);
        if (run.Plan.Count(s => s.Status != AgentStepStatus.Pending) + check.Count == 0)
        {
            error = "That would leave the job with no steps. Use job_stop to cancel it instead. Nothing was changed.";
            return null;
        }

        _logger.SensitiveDebug("Run {RunId} plan changes from chat: {Changes}", run.Id,
            string.Join(" | ", changes.Select(c => $"{c.Kind} {c.Title} {c.Intent}")));
        return changes;
    }

    private async Task<string> NoteAsync(AgentRun run, string? note)
    {
        var text = JobPlanChanges.Normalize(note, MaxNoteChars);
        if (text.Length == 0)
            return "The note was empty. Nothing was changed.";

        _jobs.AddNote(run.Id, text);
        _logger.SensitiveDebug("Run {RunId} steering note: {Note}", run.Id, text);
        return await Task.FromResult(AgentRunStates.IsExecuting(run.State)
            ? "Noted. Every step the job starts from now on follows the note; the running step is not changed (use job_restart_step for that)."
            : "Noted. The job follows the note for every remaining step once it continues.");
    }

    private async Task<string> PauseAsync(AgentRun run, bool afterThisStep, CancellationToken ct)
    {
        if (run.State is not (AgentRunState.Running or AgentRunState.WaitingForChildren))
            return $"The job is not working right now ({Describe(run)}), so there is nothing to pause.";

        if (afterThisStep)
        {
            if (run.State != AgentRunState.Running || _steering.IsFanningOut(run.Id))
                return "The job is waiting for its sub-tasks; it can only pause now, not after a step.";
            _jobs.RequestPauseAfterStep(run.Id);
            return await WithBlockAsync("The job pauses once the running step has finished.", run, ct);
        }

        return await _pause.PauseAsync(run.Id, ct)
            ? "Pausing the job now. The running step goes back to the plan and runs again on continue."
            : "The job could not be paused right now.";
    }

    private async Task<string> RestartStepAsync(AgentRun run, string? note, CancellationToken ct)
    {
        if (run.State != AgentRunState.Running || run.Plan.All(s => s.Status != AgentStepStatus.Running))
            return $"No step is running right now ({Describe(run)}).";
        if (_steering.IsFanningOut(run.Id))
            return "The job is running sub-tasks; a step cannot be restarted now.";

        var text = JobPlanChanges.Normalize(note, MaxNoteChars);
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged(object? sender, AgentRunChangedEventArgs e)
        {
            if (e.RunId == run.Id && !AgentRunStates.IsExecuting(e.State))
                paused.TrySetResult();
        }

        _runs.RunChanged += OnChanged;
        try
        {
            if (!await _pause.PauseAsync(run.Id, ct))
                return "The running step could not be stopped right now.";

            var settled = await Task.WhenAny(paused.Task, Task.Delay(RestartPauseTimeout, ct)) == paused.Task;
            var now = await ReloadAsync(run, ct);
            if (!settled && now.State != AgentRunState.Paused)
                return "The job was asked to stop the step but has not stopped yet. It will pause; continue it with job_continue.";
            if (now.State != AgentRunState.Paused)
                return $"The job did not pause ({Describe(now)}), so the step was not restarted.";
        }
        finally
        {
            _runs.RunChanged -= OnChanged;
        }

        _logger.SensitiveDebug("Run {RunId} restart note: {Note}", run.Id, text);
        return await _resume.ResumeAsync(run.Id, text.Length == 0 ? null : text, ct,
                parkReasonAllowed: r => r == AgentRunService.UserPausedReason)
            ? "Restarted the running step" + (text.Length == 0 ? "." : " with the note.")
            : "The step was stopped, but the job could not be continued; it is paused now.";
    }

    private async Task<string> ContinueAsync(AgentRun run, CancellationToken ct)
    {
        if (!AgentRunStates.IsParked(run.State))
            return $"The job is not stopped ({Describe(run)}); there is nothing to continue.";

        var reason = RunPauseEnvelope.ReadReason(run);
        switch (reason)
        {
            case AgentRunOrchestrator.PlanApprovalReason:
                return "The plan waits for the user's approval. Only the user can start it, with Start on the job block.";
            case AgentRunOrchestrator.ToolApprovalReason:
                return "The job waits for the user to decide on a tool call. Only the user can answer that, on the job block.";
            case AgentRunOrchestrator.NeedsInputReason:
                return "The job waits for an answer to its question; pass the user's answer with job_answer.";
            case AgentRunOrchestrator.NeedsGoalReason:
                return "The job waits for the user to clarify the goal on the job block.";
        }

        if (run.State != AgentRunState.Paused && !IsContinuable(reason))
            return "This job cannot be continued from the chat.";

        return await _resume.ResumeAsync(run.Id, null, ct, parkReasonAllowed: ModelMayContinue)
            ? "Continued the job."
            : "The job could not be continued; it may already be continuing.";
    }

    private async Task<string> AnswerAsync(AgentRun run, string? answer, CancellationToken ct)
    {
        if (run.State != AgentRunState.WaitingForInput
            || RunPauseEnvelope.ReadReason(run) != AgentRunOrchestrator.NeedsInputReason)
            return "The job has not asked a question that waits for an answer.";
        if (string.IsNullOrWhiteSpace(answer))
            return "The answer was empty. Nothing was sent.";

        _logger.SensitiveDebug("Run {RunId} answer from chat: {Answer}", run.Id, answer);
        return await _resume.ResumeAsync(run.Id, answer, ct,
                parkReasonAllowed: r => r == AgentRunOrchestrator.NeedsInputReason)
            ? "Passed the answer on; the job goes on."
            : "The answer could not be passed on; the job may already have been answered.";
    }

    private async Task<string> StopAsync(AgentRun run, CancellationToken ct) =>
        await _resume.CancelRunAsync(run.Id, ct) ? "Stopped the job." : "The job could not be stopped.";

    private static bool IsInterruption(string? reason) =>
        AgentRunOrchestrator.IsInterruptionReason(reason) || reason == HeadlessRunLauncher.ResumeInterruptedReason;

    private static bool ModelMayContinue(string? reason) =>
        reason is not (AgentRunOrchestrator.PlanApprovalReason or AgentRunOrchestrator.ToolApprovalReason
            or AgentRunOrchestrator.NeedsInputReason or AgentRunOrchestrator.NeedsGoalReason);

    private static bool IsContinuable(string? reason) =>
        IsInterruption(reason) || reason is "step-cap" or "wall-clock" or AgentRunService.UserPausedReason
            or AgentRunOrchestrator.ChildrenParkedReason or AgentRunService.ChildrenInterruptedReason;

    private static string Describe(AgentRun run) => run.State.ToString();

    private static AgentStep? ResolveStep(AgentRun run, string? handle) =>
        string.IsNullOrWhiteSpace(handle)
            ? null
            : run.Plan.FirstOrDefault(s => JobTurnBlockBuilder.Handle(s.Id).Equals(handle.Trim(), StringComparison.OrdinalIgnoreCase));

    private static string Cap(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    private static JsonElement? ToElement(object? value) => value switch
    {
        JsonElement e => e,
        null => null,
        _ => JsonSerializer.SerializeToElement(value),
    };

    private static string? Prop(JsonElement item, string name)
    {
        foreach (var p in item.EnumerateObject())
        {
            if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : null;
        }
        return null;
    }

    private static string? GetString(IDictionary<string, object?>? args, string key) =>
        args is not null && args.TryGetValue(key, out var v)
            ? v switch { JsonElement { ValueKind: JsonValueKind.String } e => e.GetString(), string s => s, _ => null }
            : null;

    private static bool GetBool(IDictionary<string, object?>? args, string key) =>
        args is not null && args.TryGetValue(key, out var v) && v switch
        {
            JsonElement { ValueKind: JsonValueKind.True } => true,
            JsonElement { ValueKind: JsonValueKind.String } e => bool.TryParse(e.GetString(), out var b) && b,
            bool b => b,
            string s => bool.TryParse(s, out var b2) && b2,
            _ => false,
        };
}
