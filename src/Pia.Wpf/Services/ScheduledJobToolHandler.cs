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
                "The background turn may use read-only tools freely. Write tools are DENIED unless the user explicitly grants them: pass their EXACT tool names in grantedTools (comma-separated). Grantable write tools include: create_object/update_object/append_to_list/delete_object (memory), create_todo/update_todo/complete_todo/delete_todo (todos), write_file/delete_file (files). Only grant writes the user clearly asked for. " +
                "providerName is optional - if omitted, the provider mapped to Assistant mode at fire time is used. " +
                "KIND: 'research' (default) runs the query once at fire time and saves a summary as a chat; 'agent' runs a multi-step agent task that plans and can use granted write tools to actually carry out work. If the user has NOT made clear which of the two they want, do NOT call this tool - ask a single clarifying question (e.g. 'Should this just research and summarize, or actually carry out the task?') and call once they answer."),

            AIFunctionFactory.Create(QueryScheduledSchema, "query_scheduled_research",
                "List the user's scheduled research jobs. Use filter 'active' (default) for current jobs, 'all' for everything including disabled/failed."),

            AIFunctionFactory.Create(UpdateScheduledSchema, "update_scheduled_research",
                "Update a scheduled research job by ID. Only provide fields that should change."),

            AIFunctionFactory.Create(DeleteScheduledSchema, "delete_scheduled_research",
                "Delete a scheduled research job by ID. Permanent.")
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
            "create_scheduled_research" => ((object?)null, await PrepareCreateJob(args)),
            "update_scheduled_research" => ((object?)null, await PrepareUpdateJob(args)),
            "delete_scheduled_research" => ((object?)null, await PrepareDeleteJob(args)),
            _ => ((object?)$"Unknown tool: {toolCall.Name}", (ScheduledJobToolCall?)null)
        };

        // Error cases (invalid ID, not found) produce a pending action with no TargetJobId.
        // Return them as immediate results so no action card is shown to the user.
        if (pending is not null && pending.TargetJobId is null && toolCall.Name is not "create_scheduled_research")
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
        int? dayOfMonth = dayOfMonthStr is not null && int.TryParse(dayOfMonthStr, out var dom) ? dom : null;
        int? month = monthStr is not null && int.TryParse(monthStr, out var m) ? m : null;
        DateTime? specificDate = specificDateStr is not null && DateTime.TryParse(specificDateStr, out var sd) ? sd : null;
        var grantedTools = ParseGrantedTools(grantedToolsStr);

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
        if (grantedTools.Count > 0) detailSb.AppendLine($"{_localizationService["Tool_ScheduledResearch_Detail_GrantedTools"]}: {string.Join(", ", grantedTools)}");
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
                return _localizationService.Format("Tool_ScheduledResearch_Exec_Created", created.Id, created.NextFireAt.ToString("g"));
            });
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
        int? dayOfMonth = dayOfMonthStr is not null && int.TryParse(dayOfMonthStr, out var dom) ? dom : null;
        int? month = monthStr is not null && int.TryParse(monthStr, out var m) ? m : null;
        // null = leave existing grants unchanged; empty/whitespace string = clear all grants.
        IReadOnlyCollection<string>? grantedTools = grantedToolsStr is not null ? ParseGrantedTools(grantedToolsStr) : null;

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
                return _localizationService.Format("Tool_ScheduledResearch_Exec_Updated", id);
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
        [Description("Comma-separated EXACT write-tool names to allow at fire time (e.g. 'create_object,create_todo,write_file'). Omit for read-only. Only grant writes the user explicitly asked for.")] string? grantedTools = null,
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

    private static List<string> ParseGrantedTools(string? csv) =>
        string.IsNullOrWhiteSpace(csv)
            ? []
            : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                 .Distinct(StringComparer.OrdinalIgnoreCase)
                 .ToList();

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
