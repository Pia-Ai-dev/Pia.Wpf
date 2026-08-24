using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Pia.Logging;
using Pia.Models;
using Pia.Services.Interfaces;

namespace Pia.Services;

public class ScheduledJobToolHandler : IScheduledJobToolHandler
{
    private readonly IScheduledJobService _jobs;
    private readonly IProviderService _providers;
    private readonly ILocalizationService _localizationService;
    private readonly ILogger<ScheduledJobToolHandler> _logger;

    public ScheduledJobToolHandler(
        IScheduledJobService jobs,
        IProviderService providers,
        ILocalizationService localizationService,
        ILogger<ScheduledJobToolHandler> logger)
    {
        _jobs = jobs;
        _providers = providers;
        _localizationService = localizationService;
        _logger = logger;
    }

    public IList<AITool> GetTools()
    {
        return
        [
            AIFunctionFactory.Create(CreateScheduledSchema, "create_scheduled_research",
                $"Create a new scheduled research job that fires on a recurring schedule, runs the query as a background assistant turn, saves the result as a new assistant chat, and shows a toast when complete. Current date/time is {DateTime.Now:yyyy-MM-dd HH:mm} ({DateTime.Now:dddd}). " +
                "PRECONDITION: before calling, you must have explicit user-given values for name (display name) and query. The query is a self-contained prompt that will be run once at fire time, so craft it well (bake in any desired answer length/format). If the user does not give a query - but a name - suggest a query. " +
                "If the user's request is ambiguous, do NOT call this tool. Ask a single clarifying question that requests the missing fields, then call once the user has answered. " +
                "Parse the user's natural language request into structured fields. " +
                "Examples: 'every weekday at 8am check Tesla stock news' -> create 5 separate Weekly jobs (Mon-Fri) since 'weekday' is not a single recurrence type. " +
                "'every Monday research crypto trends' -> recurrence=Weekly, dayOfWeek=Monday, timeOfDay=08:00. " +
                "The background turn may use read-only tools freely. Write tools are DENIED unless the user explicitly grants them: pass their EXACT tool names in grantedTools (comma-separated). Grantable write tools include: remember/forget (memory), create_todo/update_todo/complete_todo/delete_todo (todos), write_file/delete_file (files). Only grant writes the user clearly asked for. Presumed-destructive tool NAMES from EXTERNAL (MCP) plugins are stripped from the grant list at creation. " +
                "providerName is optional - if omitted, the provider mapped to Assistant mode at fire time is used. " +
                "KIND: 'research' (default) runs the query once at fire time and saves a summary as a chat; 'agent' runs a multi-step agent task that plans and can use granted write tools to actually carry out work. If the user has NOT made clear which of the two they want, do NOT call this tool - ask a single clarifying question (e.g. 'Should this just research and summarize, or actually carry out the task?') and call once they answer."),

            AIFunctionFactory.Create(QueryScheduledSchema, "query_scheduled_research",
                "List the user's scheduled research jobs. Use filter 'active' (default) for current jobs, 'all' for everything including disabled/failed."),

            AIFunctionFactory.Create(UpdateScheduledSchema, "update_scheduled_research",
                "Update a scheduled research job by ID. Only provide fields that should change."),

            AIFunctionFactory.Create(DeleteScheduledSchema, "delete_scheduled_research",
                "Delete a scheduled research job by ID. Permanent."),

            AIFunctionFactory.Create(ListBlueprintsSchema, "list_routine_blueprints",
                "List the ready-made routine blueprints. Call this FIRST when the user asks for a recurring routine of a familiar kind (a daily digest, a morning brief, a weekly review, a competitor watch, meeting follow-ups): a blueprint ships a tested prompt, a schedule and the narrowest write grants for that job, so create_routine_from_blueprint beats writing a query freehand with create_scheduled_research. Returns each blueprint's key, what it does, its schedule, its write grants and its fillable slots."),

            AIFunctionFactory.Create(CreateFromBlueprintSchema, "create_routine_from_blueprint",
                "Create a routine from a blueprint listed by list_routine_blueprints. The blueprint owns the prompt, the job type, the reasoning effort and the write grants — you cannot widen them here, and there is no query parameter. Fill every slot the blueprint declares whose value the user has actually given; ASK the user for a slot rather than guessing one, and use the EXACT slot names from the listing (an unrecognised name is refused, never silently defaulted). A slot you omit falls back to the blueprint's own default.")
        ];
    }

    public async Task<(object? Result, ScheduledJobToolCall? PendingAction)> HandleToolCallAsync(
        FunctionCallContent toolCall,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("ScheduledJobToolHandler dispatching: {ToolName}", toolCall.Name);
#if DEBUG
        Debug.WriteLine($"[ScheduledJobToolHandler Args] {toolCall.Name}: {JsonSerializer.Serialize(toolCall.Arguments)}");
#endif
        var args = toolCall.Arguments ?? new Dictionary<string, object?>();

        var (result, pending) = toolCall.Name switch
        {
            "query_scheduled_research" => (await HandleQueryJobs(args), (ScheduledJobToolCall?)null),
            "list_routine_blueprints" => (HandleListBlueprints(), (ScheduledJobToolCall?)null),
            "create_scheduled_research" => ((object?)null, await PrepareCreateJob(args)),
            "create_routine_from_blueprint" => await PrepareCreateFromBlueprint(args),
            "update_scheduled_research" => ((object?)null, await PrepareUpdateJob(args)),
            "delete_scheduled_research" => ((object?)null, await PrepareDeleteJob(args)),
            _ => ((object?)$"Unknown tool: {toolCall.Name}", (ScheduledJobToolCall?)null)
        };

        // Error cases (invalid ID, not found) produce a pending action with no TargetJobId.
        // Return them as immediate results so no action card is shown to the user.
        if (pending is not null && pending.TargetJobId is null
            && toolCall.Name is not ("create_scheduled_research" or "create_routine_from_blueprint"))
        {
            _logger.LogWarning("ScheduledJobToolHandler {ToolName} returning error", toolCall.Name);
            _logger.SensitiveDebug("ScheduledJobToolHandler {ToolName} error description: {Description}", toolCall.Name, pending.Description);
            return (await pending.Execute(), null);
        }

        _logger.LogDebug("ScheduledJobToolHandler {ToolName} result: hasResult={HasResult}, hasPending={HasPending}",
            toolCall.Name, result is not null, pending is not null);
        return (result, pending);
    }

    public async Task<object?> ExecutePendingActionAsync(ScheduledJobToolCall pendingAction)
    {
        _logger.LogDebug("Executing scheduled-job action: {ToolName}, targetId={TargetJobId}",
            pendingAction.ToolName, pendingAction.TargetJobId);
        try
        {
            var result = await pendingAction.Execute();
            _logger.LogInformation("Scheduled-job action completed: {ToolName}", pendingAction.ToolName);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute scheduled-job tool action: {ToolName}", pendingAction.ToolName);
            return $"Error executing {pendingAction.ToolName}: {ex.Message}";
        }
    }

    private async Task<object?> HandleQueryJobs(IDictionary<string, object?> args)
    {
        var filter = GetStringArg(args, "filter");

        var jobs = filter.Equals("all", StringComparison.OrdinalIgnoreCase)
            ? await _jobs.GetAllAsync()
            : await _jobs.GetActiveAsync();

        if (jobs.Count == 0)
            return "No scheduled research jobs found.";

        var sb = new StringBuilder();
        sb.AppendLine($"Found {jobs.Count} scheduled research job(s):");

        foreach (var j in jobs)
        {
            sb.AppendLine($"\n[ID: {j.Id}] {j.Name}");
            sb.AppendLine($"  Query: {j.Query}");
            sb.AppendLine($"  Recurrence: {j.Recurrence}, Time: {j.TimeOfDay:HH:mm}");
            if (j.GrantedTools.Count > 0) sb.AppendLine($"  Granted write tools: {string.Join(", ", j.GrantedTools)}");
            sb.AppendLine($"  Status: {j.Status}, Next fire: {j.NextFireAt:g}");
            if (j.DayOfWeek.HasValue) sb.AppendLine($"  Day of week: {j.DayOfWeek}");
            if (j.DayOfMonth.HasValue) sb.AppendLine($"  Day of month: {j.DayOfMonth}");
            if (j.Month.HasValue) sb.AppendLine($"  Month: {j.Month}");
            if (j.LastFiredAt.HasValue) sb.AppendLine($"  Last fired: {j.LastFiredAt:g}");
            if (j.ConsecutiveFailures > 0) sb.AppendLine($"  Consecutive failures: {j.ConsecutiveFailures}");
        }

        return sb.ToString();
    }

    private async Task<ScheduledJobToolCall> PrepareCreateJob(IDictionary<string, object?> args)
    {
        var name = GetStringArg(args, "name");
        var query = GetStringArg(args, "query");
        var recurrenceStr = GetStringArg(args, "recurrence");
        var timeOfDayStr = GetStringArg(args, "timeOfDay");
        var dayOfWeekStr = GetOptionalStringArg(args, "dayOfWeek");
        var dayOfMonthStr = GetOptionalStringArg(args, "dayOfMonth");
        var monthStr = GetOptionalStringArg(args, "month");
        var specificDateStr = GetOptionalStringArg(args, "specificDate");
        var grantedToolsStr = GetOptionalStringArg(args, "grantedTools");
        var providerName = GetOptionalStringArg(args, "providerName");
        var kindStr = GetOptionalStringArg(args, "kind");
        var kind = string.Equals(kindStr, "agent", StringComparison.OrdinalIgnoreCase)
            ? ScheduledJobKind.AgentTask
            : ScheduledJobKind.Research;

        var recurrence = Enum.TryParse<RecurrenceType>(recurrenceStr, true, out var r) ? r : RecurrenceType.Daily;
        var timeOfDay = TimeOnly.TryParse(timeOfDayStr, out var t) ? t : new TimeOnly(8, 0);
        DayOfWeek? dayOfWeek = dayOfWeekStr is not null && Enum.TryParse<DayOfWeek>(dayOfWeekStr, true, out var dow) ? dow : null;
        var dayOfMonth = ParseBoundedInt(dayOfMonthStr, 1, 31);
        var month = ParseBoundedInt(monthStr, 1, 12);
        DateTime? specificDate = specificDateStr is not null && DateTime.TryParse(specificDateStr, out var sd) ? sd : null;
        var (grantedTools, rejectedTools) = ParseGrantedTools(grantedToolsStr);
        if (rejectedTools.Count > 0)
        {
            // The rejected names are raw substrings of the MODEL's grantedTools argument — never validated
            // against a registered tool, so they can carry arbitrary user/customer content ("delete Acme's
            // invoices"). Count at Warning, names only via SensitiveDebug (CLAUDE.md: tool-call arguments
            // are a payload). The model-facing DescribeRejectedGrants string still carries them.
            _logger.LogWarning("create_scheduled_research refused {Count} destructive external grant(s)", rejectedTools.Count);
            _logger.SensitiveDebug("create_scheduled_research refused grants: {Tools}", string.Join(", ", rejectedTools));
        }

        // The grant set that will ACTUALLY be in force at fire time. An AgentTask job with no explicit
        // grant silently receives the launcher's default, so render that default instead of omitting the
        // line — the user must be able to see on the approval card what the job may write. A Research job
        // with no grants genuinely is read-only, so it keeps no line at all.
        var effectiveGrants = grantedTools.Count > 0
            ? grantedTools
            : kind == ScheduledJobKind.AgentTask
                ? HeadlessRunRequest.DefaultGrantedWrites.ToList()
                : [];

        var providerId = await ResolveProviderIdAsync(providerName);

        var detailSb = new StringBuilder();
        detailSb.AppendLine($"{_localizationService["Tool_ScheduledResearch_Detail_Name"]}: {name}");
        detailSb.AppendLine($"{_localizationService["Tool_ScheduledResearch_Detail_Kind"]}: {(kind == ScheduledJobKind.AgentTask ? "Agent task" : "Research")}");
        detailSb.AppendLine($"{_localizationService["Tool_ScheduledResearch_Detail_Query"]}: {query}");
        detailSb.AppendLine($"{_localizationService["Tool_ScheduledResearch_Detail_Recurrence"]}: {recurrence}");
        detailSb.AppendLine($"{_localizationService["Tool_ScheduledResearch_Detail_Time"]}: {timeOfDay:HH:mm}");
        if (dayOfWeek.HasValue) detailSb.AppendLine($"{_localizationService["Tool_ScheduledResearch_Detail_DayOfWeek"]}: {dayOfWeek}");
        if (dayOfMonth.HasValue) detailSb.AppendLine($"{_localizationService["Tool_ScheduledResearch_Detail_DayOfMonth"]}: {dayOfMonth}");
        if (month.HasValue) detailSb.AppendLine($"{_localizationService["Tool_ScheduledResearch_Detail_Month"]}: {month}");
        if (specificDate.HasValue) detailSb.AppendLine($"{_localizationService["Tool_ScheduledResearch_Detail_Date"]}: {specificDate:d}");
        if (effectiveGrants.Count > 0) detailSb.AppendLine($"{_localizationService["Tool_ScheduledResearch_Detail_GrantedTools"]}: {string.Join(", ", effectiveGrants)}");
        if (providerId.HasValue && !string.IsNullOrWhiteSpace(providerName))
            detailSb.AppendLine($"{_localizationService["Tool_ScheduledResearch_Detail_Provider"]}: {providerName}");

        return new ScheduledJobToolCall(
            ToolName: "create_scheduled_research",
            Description: _localizationService.Format("Tool_ScheduledResearch_Desc_Create", name, recurrence.ToString().ToLower()),
            Details: detailSb.ToString(),
            TargetJobId: null,
            Execute: async () =>
            {
                var created = await _jobs.CreateAsync(name, query, recurrence, timeOfDay,
                    dayOfWeek, dayOfMonth, month, specificDate, providerId, grantedTools, kind);
                return _localizationService.Format("Tool_ScheduledResearch_Exec_Created", created.Id, created.NextFireAt.ToString("g"))
                       + DescribeRejectedGrants(rejectedTools);
            });
    }

    private string HandleListBlueprints()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{RoutineBlueprintCatalog.All.Count} routine blueprint(s). "
            + "Create one with create_routine_from_blueprint, which takes the key below.");

        foreach (var b in RoutineBlueprintCatalog.All)
        {
            sb.AppendLine($"\n[key: {b.Key}] {_localizationService[b.TitleKey]}");
            sb.AppendLine($"  Does: {_localizationService[b.DescriptionKey]}");
            sb.AppendLine($"  Schedule: {b.Recurrence}{(b.DefaultDayOfWeek.HasValue ? $" on {b.DefaultDayOfWeek}" : "")} at {b.DefaultTime:HH:mm}");
            sb.AppendLine($"  Write grants: {(b.GrantedTools.Count == 0 ? "none, read-only" : string.Join(", ", b.GrantedTools))}");
            if (b.Slots.Count == 0)
            {
                sb.AppendLine("  Slots: none — nothing to ask the user for.");
                continue;
            }

            foreach (var slot in b.Slots)
                sb.AppendLine($"  Slot '{slot.Name}' ({_localizationService[slot.LabelKey]}): "
                    + $"{_localizationService[slot.HelpKey]} "
                    + $"If omitted: {(slot.Default is null ? "REQUIRED, the call is refused" : $"\"{slot.Default}\"")}");
        }

        return sb.ToString();
    }

    /// <summary>The blueprint — not the model — decides the prompt, the kind, the grants and the effort, so a
    /// hallucinated grant cannot reach a job the user approved from a card advertising none.</summary>
    private async Task<(object? Result, ScheduledJobToolCall? Pending)> PrepareCreateFromBlueprint(IDictionary<string, object?> args)
    {
        var key = GetStringArg(args, "blueprintKey");
        if (RoutineBlueprintCatalog.Find(key) is not { } blueprint)
        {
            _logger.LogWarning("create_routine_from_blueprint called with an unknown blueprint key");
            _logger.SensitiveDebug("create_routine_from_blueprint unknown key: {Key}", key);
            return ($"Error: '{key}' is not a routine blueprint. Call list_routine_blueprints for the keys.", null);
        }

        var (values, slotsError) = ParseSlotValues(GetOptionalStringArg(args, "slots"));
        if (slotsError is not null)
        {
            _logger.LogWarning("create_routine_from_blueprint got unparseable slot values for {Key}", blueprint.Key);
            return ($"Error: {slotsError}", null);
        }

        var fill = RoutineBlueprintFill.ToCreateArgs(blueprint, values);
        if (fill.Error is { } fillError)
        {
            _logger.LogWarning("create_routine_from_blueprint refused {Key}: {Kind}", blueprint.Key, fillError.Kind);
            return ($"Error: {fillError.Message}", null);
        }

        var query = fill.Query!;
        var suppliedName = GetOptionalStringArg(args, "name")?.Trim();
        var name = string.IsNullOrEmpty(suppliedName) ? _localizationService[blueprint.TitleKey] : suppliedName;
        var timeOfDay = TimeOnly.TryParse(GetOptionalStringArg(args, "timeOfDay"), out var t) ? t : blueprint.DefaultTime;
        var dayOfWeekStr = GetOptionalStringArg(args, "dayOfWeek");
        var dayOfWeek = dayOfWeekStr is not null && Enum.TryParse<DayOfWeek>(dayOfWeekStr, true, out var dow)
            ? dow
            : blueprint.DefaultDayOfWeek;

        // Same rule as the freehand create path: an AgentTask with no grants silently receives the launcher's
        // write_file default, so the card renders that default rather than claiming the job writes nothing.
        var effectiveGrants = blueprint.GrantedTools.Count > 0
            ? blueprint.GrantedTools
            : blueprint.Kind == ScheduledJobKind.AgentTask
                ? HeadlessRunRequest.DefaultGrantedWrites.ToList()
                : [];

        var detailSb = new StringBuilder();
        detailSb.AppendLine($"{_localizationService["Tool_ScheduledResearch_Detail_Name"]}: {name}");
        detailSb.AppendLine($"{_localizationService["Tool_ScheduledResearch_Detail_Blueprint"]}: {_localizationService[blueprint.TitleKey]}");
        detailSb.AppendLine($"{_localizationService["Tool_ScheduledResearch_Detail_Kind"]}: {(blueprint.Kind == ScheduledJobKind.AgentTask ? "Agent task" : "Research")}");
        detailSb.AppendLine($"{_localizationService["Tool_ScheduledResearch_Detail_Query"]}: {query}");
        detailSb.AppendLine($"{_localizationService["Tool_ScheduledResearch_Detail_Recurrence"]}: {blueprint.Recurrence}");
        detailSb.AppendLine($"{_localizationService["Tool_ScheduledResearch_Detail_Time"]}: {timeOfDay:HH:mm}");
        if (dayOfWeek.HasValue) detailSb.AppendLine($"{_localizationService["Tool_ScheduledResearch_Detail_DayOfWeek"]}: {dayOfWeek}");
        if (effectiveGrants.Count > 0) detailSb.AppendLine($"{_localizationService["Tool_ScheduledResearch_Detail_GrantedTools"]}: {string.Join(", ", effectiveGrants)}");
        if (blueprint.DefaultEffort is { } effort)
            detailSb.AppendLine($"{_localizationService["Tool_ScheduledResearch_Detail_Effort"]}: {_localizationService[$"Routines_Effort_{effort}"]}");

        var pending = new ScheduledJobToolCall(
            ToolName: "create_routine_from_blueprint",
            Description: _localizationService.Format("Tool_ScheduledResearch_Desc_Create", name, blueprint.Recurrence.ToString().ToLower()),
            Details: detailSb.ToString(),
            TargetJobId: null,
            Execute: async () =>
            {
                var created = await _jobs.CreateAsync(name, query, blueprint.Recurrence, timeOfDay,
                    dayOfWeek: dayOfWeek, providerId: null, grantedTools: blueprint.GrantedTools,
                    kind: blueprint.Kind, quietOnSuccess: blueprint.QuietOnSuccess,
                    reasoningEffort: blueprint.DefaultEffort, blueprintKey: blueprint.Key);
                return _localizationService.Format("Tool_ScheduledResearch_Exec_Created", created.Id, created.NextFireAt.ToString("g"));
            });

        return (null, pending);
    }

    /// <summary>A JSON object of slot name to value. Not a comma-separated list: a slot value is free text that
    /// routinely contains commas, which is exactly what "which companies" produces.</summary>
    private static (Dictionary<string, string>? Values, string? Error) ParseSlotValues(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return (null, null);

        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return (null, "slots must be a JSON object of slot name to value, for example {\"topic\":\"quantum computing\"}.");

            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in doc.RootElement.EnumerateObject())
                values[property.Name] = property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString() ?? string.Empty
                    : property.Value.GetRawText();

            return (values, null);
        }
        catch (JsonException)
        {
            return (null, "slots is not valid JSON. Pass a JSON object of slot name to value, for example {\"topic\":\"quantum computing\"}.");
        }
    }

    private async Task<ScheduledJobToolCall> PrepareUpdateJob(IDictionary<string, object?> args)
    {
        var idStr = GetStringArg(args, "id");
        if (!Guid.TryParse(idStr, out var id))
        {
            _logger.LogWarning("update_scheduled_research called with invalid ID: '{IdValue}'", idStr);
            return new ScheduledJobToolCall("update_scheduled_research", "Invalid ID format", null, null,
                () => Task.FromResult<object?>($"Error: Invalid scheduled-job ID format. You provided '{idStr}' which is not a valid GUID. Use query_scheduled_research to get valid IDs."));
        }

        var existing = await _jobs.GetAsync(id);
        if (existing is null)
            return new ScheduledJobToolCall("update_scheduled_research", "Scheduled job not found", null, null,
                () => Task.FromResult<object?>($"Error: Scheduled job {id} not found"));

        var name = GetOptionalStringArg(args, "name");
        var query = GetOptionalStringArg(args, "query");
        var recurrenceStr = GetOptionalStringArg(args, "recurrence");
        var timeOfDayStr = GetOptionalStringArg(args, "timeOfDay");
        var dayOfWeekStr = GetOptionalStringArg(args, "dayOfWeek");
        var dayOfMonthStr = GetOptionalStringArg(args, "dayOfMonth");
        var monthStr = GetOptionalStringArg(args, "month");
        var grantedToolsStr = GetOptionalStringArg(args, "grantedTools");
        var providerName = GetOptionalStringArg(args, "providerName");

        RecurrenceType? recurrence = recurrenceStr is not null && Enum.TryParse<RecurrenceType>(recurrenceStr, true, out var r) ? r : null;
        TimeOnly? timeOfDay = timeOfDayStr is not null && TimeOnly.TryParse(timeOfDayStr, out var t) ? t : null;
        DayOfWeek? dayOfWeek = dayOfWeekStr is not null && Enum.TryParse<DayOfWeek>(dayOfWeekStr, true, out var dow) ? dow : null;
        var dayOfMonth = ParseBoundedInt(dayOfMonthStr, 1, 31);
        var month = ParseBoundedInt(monthStr, 1, 12);
        // null = leave existing grants unchanged; empty/whitespace string = clear all grants.
        List<string>? grantedTools = null;
        List<string> rejectedTools = [];
        if (grantedToolsStr is not null)
            (grantedTools, rejectedTools) = ParseGrantedTools(grantedToolsStr);
        if (rejectedTools.Count > 0)
        {
            // Same privacy split as the create path: count at Warning, model-authored names DEBUG-only.
            _logger.LogWarning("update_scheduled_research refused {Count} destructive external grant(s)", rejectedTools.Count);
            _logger.SensitiveDebug("update_scheduled_research refused grants: {Tools}", string.Join(", ", rejectedTools));
        }

        var providerId = await ResolveProviderIdAsync(providerName);

        return new ScheduledJobToolCall(
            ToolName: "update_scheduled_research",
            Description: _localizationService.Format("Tool_ScheduledResearch_Desc_Update", existing.Name),
            Details: _localizationService.Format("Tool_ScheduledResearch_Detail_CurrentStatus", existing.Recurrence, existing.TimeOfDay.ToString("HH:mm")),
            TargetJobId: id,
            Execute: async () =>
            {
                await _jobs.UpdateAsync(id, name, query, recurrence, timeOfDay,
                    dayOfWeek, dayOfMonth, month, providerId, grantedTools);
                return _localizationService.Format("Tool_ScheduledResearch_Exec_Updated", id)
                       + DescribeRejectedGrants(rejectedTools);
            });
    }

    private async Task<ScheduledJobToolCall> PrepareDeleteJob(IDictionary<string, object?> args)
    {
        var idStr = GetStringArg(args, "id");
        if (!Guid.TryParse(idStr, out var id))
        {
            _logger.LogWarning("delete_scheduled_research called with invalid ID: '{IdValue}'", idStr);
            return new ScheduledJobToolCall("delete_scheduled_research", "Invalid ID format", null, null,
                () => Task.FromResult<object?>($"Error: Invalid scheduled-job ID format. You provided '{idStr}' which is not a valid GUID. Use query_scheduled_research to get valid IDs."));
        }

        var existing = await _jobs.GetAsync(id);
        if (existing is null)
            return new ScheduledJobToolCall("delete_scheduled_research", "Scheduled job not found", null, null,
                () => Task.FromResult<object?>($"Error: Scheduled job {id} not found"));

        return new ScheduledJobToolCall(
            ToolName: "delete_scheduled_research",
            Description: _localizationService.Format("Tool_ScheduledResearch_Desc_Delete", existing.Name),
            Details: _localizationService.Format("Tool_ScheduledResearch_Detail_PermanentDelete", existing.Recurrence.ToString().ToLower(), existing.TimeOfDay.ToString("HH:mm")),
            TargetJobId: id,
            Execute: async () =>
            {
                await _jobs.DeleteAsync(id);
                return _localizationService.Format("Tool_ScheduledResearch_Exec_Deleted", id);
            });
    }

    private async Task<Guid?> ResolveProviderIdAsync(string? providerName)
    {
        if (string.IsNullOrWhiteSpace(providerName))
            return null;

        try
        {
            var providers = await _providers.GetProvidersAsync();
            var match = providers.FirstOrDefault(p => p.Name.Contains(providerName, StringComparison.OrdinalIgnoreCase));
            return match?.Id;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Provider name lookup failed");
            return null;
        }
    }

    // Schema methods - signature only, used by AIFunctionFactory for reflection.

    [Description("Create a new scheduled research job")]
    private static string CreateScheduledSchema(
        [Description("Short label for the job (e.g. 'Tesla stock briefing')")] string name,
        [Description("The research query to execute when the job fires")] string query,
        [Description("Recurrence type: Once, Daily, Weekly, Monthly, Yearly")] string recurrence,
        [Description("Time of day in HH:mm format (e.g. '08:00', '21:30')")] string timeOfDay,
        [Description("Day of week for Weekly recurrence (e.g. 'Monday')")] string? dayOfWeek = null,
        [Description("Day of month for Monthly/Yearly recurrence (1-31)")] string? dayOfMonth = null,
        [Description("Month for Yearly recurrence (1-12)")] string? month = null,
        [Description("Specific date for Once recurrence in yyyy-MM-dd format")] string? specificDate = null,
        [Description("Comma-separated EXACT write-tool names to allow at fire time (e.g. 'create_todo,write_file'). Omit for read-only ('agent' jobs still get write_file). Only grant writes the user explicitly asked for. Destructive external (MCP) tool names are refused.")] string? grantedTools = null,
        [Description("Optional substring of an AI provider name to pin")] string? providerName = null,
        [Description("Job type: 'research' (default) = one-shot query saved as a chat; 'agent' = a multi-step agent task that can use granted write tools. If the user hasn't made the type clear, ASK before calling.")] string? kind = null) => "";

    [Description("List scheduled research jobs")]
    private static string QueryScheduledSchema(
        [Description("Filter: 'active' (default) or 'all'")] string filter = "active") => "";

    [Description("Update a scheduled research job")]
    private static string UpdateScheduledSchema(
        [Description("The ID of the scheduled job to update")] string id,
        [Description("New label/name (optional)")] string? name = null,
        [Description("New query (optional)")] string? query = null,
        [Description("New recurrence type (optional): Once, Daily, Weekly, Monthly, Yearly")] string? recurrence = null,
        [Description("New time of day in HH:mm format (optional)")] string? timeOfDay = null,
        [Description("New day of week (optional)")] string? dayOfWeek = null,
        [Description("New day of month (optional)")] string? dayOfMonth = null,
        [Description("New month (optional)")] string? month = null,
        [Description("New comma-separated write-tool grants (optional). Pass an empty string to revoke all write grants.")] string? grantedTools = null,
        [Description("Substring of new provider name (optional)")] string? providerName = null) => "";

    [Description("Delete a scheduled research job")]
    private static string DeleteScheduledSchema(
        [Description("The ID of the scheduled job to delete")] string id) => "";

    [Description("List the ready-made routine blueprints and their slots")]
    private static string ListBlueprintsSchema() => "";

    [Description("Create a routine from a blueprint")]
    private static string CreateFromBlueprintSchema(
        [Description("The blueprint's key, exactly as list_routine_blueprints printed it")] string blueprintKey,
        [Description("JSON object of slot name to value, e.g. {\"topic\":\"quantum computing\"}. Only names the blueprint declares; an unknown name is refused. Omit a slot to take the blueprint's default.")] string? slots = null,
        [Description("Override the display name (optional). Defaults to the blueprint's own title.")] string? name = null,
        [Description("Override the time of day in HH:mm (optional). Defaults to the blueprint's own time.")] string? timeOfDay = null,
        [Description("Override the day of week for a weekly blueprint (optional), e.g. 'Monday'.")] string? dayOfWeek = null) => "";

    /// <summary>
    /// Model-facing note appended to the tool result when grants were stripped, so the assistant can tell
    /// the user instead of silently creating a weaker job than they asked for. Not a UI string (the tool
    /// result is consumed by the model), so it stays English like the other inline results here.
    /// </summary>
    private static string DescribeRejectedGrants(List<string> rejected)
        => rejected.Count == 0
            ? string.Empty
            : $" NOTE: refused these grants — {string.Join(", ", rejected)} — because you may not put a "
              + "destructive third-party tool into a job's grant list. Tell the user; do not retry.";

    /// <summary>
    /// Parse the comma-separated grant list and STRIP every presumed-external destructive name. The gate
    /// honours whatever this list names, so a MODEL-authored grant for an irreversible third-party action is
    /// refused where it is written rather than at fire time. Our own destructive tools are untouched —
    /// granting those is the user's explicit, auditable choice. Rejects come back too, so the approval card
    /// and the tool result can both tell the truth about the grant set that will be used.
    /// </summary>
    /// <summary>
    /// Clamps rather than rejects, because RecurrenceCalculator THROWS on an out-of-range value —
    /// <c>DateTime.DaysInMonth</c> for a month outside 1-12, <c>new DateTime</c> for a day below 1 — and a
    /// null would instead fall back to today's date, which is the drift the day fields exist to stop.
    /// </summary>
    private static int? ParseBoundedInt(string? raw, int min, int max) =>
        raw is not null && int.TryParse(raw, out var value) ? Math.Clamp(value, min, max) : null;

    private static (List<string> Accepted, List<string> Rejected) ParseGrantedTools(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv))
            return ([], []);

        var names = csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var accepted = new List<string>();
        var rejected = new List<string>();
        foreach (var name in names)
        {
            if (ToolPermissionService.IsPresumedExternalDeleteLike(name))
                rejected.Add(name);
            else
                accepted.Add(name);
        }

        return (accepted, rejected);
    }

    private static string GetStringArg(IDictionary<string, object?> args, string key)
    {
        if (args.TryGetValue(key, out var value))
        {
            if (value is JsonElement element)
                return element.ValueKind == JsonValueKind.String
                    ? element.GetString() ?? string.Empty
                    : element.GetRawText();
            return value?.ToString() ?? string.Empty;
        }
        return string.Empty;
    }

    private static string? GetOptionalStringArg(IDictionary<string, object?> args, string key)
    {
        if (args.TryGetValue(key, out var value) && value is not null)
        {
            if (value is JsonElement element)
            {
                if (element.ValueKind == JsonValueKind.Null) return null;
                return element.ValueKind == JsonValueKind.String
                    ? element.GetString()
                    : element.GetRawText();
            }
            var str = value.ToString();
            return string.IsNullOrEmpty(str) ? null : str;
        }
        return null;
    }
}
