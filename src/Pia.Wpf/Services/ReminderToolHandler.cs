using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Pia.Models;
using Pia.Services.Interfaces;

namespace Pia.Services;

public class ReminderToolHandler : IReminderToolHandler
{
    private readonly IReminderService _reminderService;
    private readonly ILogger<ReminderToolHandler> _logger;

    public ReminderToolHandler(IReminderService reminderService, ILogger<ReminderToolHandler> logger)
    {
        _reminderService = reminderService;
        _logger = logger;
    }

    public IList<AITool> GetTools()
    {
        return
        [
            AIFunctionFactory.Create(CreateReminderSchema, "create_reminder",
                $"Create a new reminder. Current date/time is {DateTime.Now:yyyy-MM-dd HH:mm} ({DateTime.Now:dddd}). " +
                "Parse the user's natural language request into structured fields. " +
                "Examples: 'remind me every day at 9pm to take meds' -> recurrence=Daily, timeOfDay=21:00. " +
                "'remind me on Nov 16 every year about mom's birthday' -> recurrence=Yearly, month=11, dayOfMonth=16. " +
                "'remind me tomorrow at 3pm to call dentist' -> recurrence=Once, specificDate=<tomorrow's date>, timeOfDay=15:00."),

            AIFunctionFactory.Create(QueryRemindersSchema, "query_reminders",
                "List the user's reminders. Use filter 'active' for current reminders, 'all' for everything including completed/disabled."),

            AIFunctionFactory.Create(UpdateReminderSchema, "update_reminder",
                "Update an existing reminder by ID. Only provide fields that need to change."),

            AIFunctionFactory.Create(DeleteReminderSchema, "delete_reminder",
                "Delete a reminder by ID. Use when the user wants to permanently remove a reminder.")
        ];
    }

    public async Task<(object? Result, ReminderToolCall? PendingAction)> HandleToolCallAsync(
        FunctionCallContent toolCall,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("ReminderToolHandler dispatching: {ToolName}", toolCall.Name);
#if DEBUG
        Debug.WriteLine($"[ReminderToolHandler Args] {toolCall.Name}: {JsonSerializer.Serialize(toolCall.Arguments)}");
#endif
        var args = toolCall.Arguments ?? new Dictionary<string, object?>();

        var (result, pending) = toolCall.Name switch
        {
            "query_reminders" => (await HandleQueryReminders(args), (ReminderToolCall?)null),
            "create_reminder" => ((object?)null, PrepareCreateReminder(args)),
            "update_reminder" => ((object?)null, await PrepareUpdateReminder(args)),
            "delete_reminder" => ((object?)null, await PrepareDeleteReminder(args)),
            _ => ((object?)$"Unknown tool: {toolCall.Name}", (ReminderToolCall?)null)
        };

        // Error cases (invalid ID, not found) produce a pending action with no TargetReminderId.
        // Return them as immediate results so no action card is shown to the user.
        if (pending is not null && pending.TargetReminderId is null && toolCall.Name is not "create_reminder")
        {
            _logger.LogWarning("ReminderToolHandler {ToolName} returning error: {Description}", toolCall.Name, pending.Description);
            return (await pending.Execute(), null);
        }

        _logger.LogDebug("ReminderToolHandler {ToolName} result: hasResult={HasResult}, hasPending={HasPending}",
            toolCall.Name, result is not null, pending is not null);
        return (result, pending);
    }

    public async Task<object?> ExecutePendingActionAsync(ReminderToolCall pendingAction)
    {
        _logger.LogDebug("Executing reminder action: {ToolName}, targetId={TargetReminderId}",
            pendingAction.ToolName, pendingAction.TargetReminderId);
        try
        {
            var result = await pendingAction.Execute();
            _logger.LogInformation("Reminder action completed: {ToolName}", pendingAction.ToolName);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute reminder tool action: {ToolName}", pendingAction.ToolName);
            return $"Error executing {pendingAction.ToolName}: {ex.Message}";
        }
    }

    private async Task<object?> HandleQueryReminders(IDictionary<string, object?> args)
    {
        var filter = GetStringArg(args, "filter");

        var reminders = filter.Equals("all", StringComparison.OrdinalIgnoreCase)
            ? await _reminderService.GetAllAsync()
            : await _reminderService.GetActiveAsync();

        if (reminders.Count == 0)
            return "No reminders found.";

        var sb = new StringBuilder();
        sb.AppendLine($"Found {reminders.Count} reminder(s):");

        foreach (var r in reminders)
        {
            sb.AppendLine($"\n[ID: {r.Id}] {r.Description}");
            sb.AppendLine($"  Recurrence: {r.Recurrence}, Time: {r.TimeOfDay:HH:mm}");
            sb.AppendLine($"  Status: {r.Status}, Next fire: {r.NextFireAt:g}");
            if (r.DayOfWeek.HasValue) sb.AppendLine($"  Day of week: {r.DayOfWeek}");
            if (r.DayOfMonth.HasValue) sb.AppendLine($"  Day of month: {r.DayOfMonth}");
            if (r.Month.HasValue) sb.AppendLine($"  Month: {r.Month}");
        }

        return sb.ToString();
    }

    private ReminderToolCall PrepareCreateReminder(IDictionary<string, object?> args)
    {
        var description = GetStringArg(args, "description");
        var recurrenceStr = GetStringArg(args, "recurrence");
        var timeOfDayStr = GetStringArg(args, "timeOfDay");
        var dayOfWeekStr = GetStringArg(args, "dayOfWeek");
        var dayOfMonthStr = GetStringArg(args, "dayOfMonth");
        var monthStr = GetStringArg(args, "month");
        var specificDateStr = GetStringArg(args, "specificDate");

        var recurrence = Enum.TryParse<RecurrenceType>(recurrenceStr, true, out var r) ? r : RecurrenceType.Once;
        var timeOfDay = TimeOnly.TryParse(timeOfDayStr, out var t) ? t : new TimeOnly(9, 0);
        DayOfWeek? dayOfWeek = Enum.TryParse<DayOfWeek>(dayOfWeekStr, true, out var dow) ? dow : null;
        int? dayOfMonth = int.TryParse(dayOfMonthStr, out var dom) ? dom : null;
        int? month = int.TryParse(monthStr, out var m) ? m : null;
        DateTime? specificDate = DateTime.TryParse(specificDateStr, out var sd) ? sd : null;

        var detailSb = new StringBuilder();
        detailSb.AppendLine($"Description: {description}");
        detailSb.AppendLine($"Recurrence: {recurrence}");
        detailSb.AppendLine($"Time: {timeOfDay:HH:mm}");
        if (dayOfWeek.HasValue) detailSb.AppendLine($"Day of week: {dayOfWeek}");
        if (dayOfMonth.HasValue) detailSb.AppendLine($"Day of month: {dayOfMonth}");
        if (month.HasValue) detailSb.AppendLine($"Month: {month}");
        if (specificDate.HasValue) detailSb.AppendLine($"Date: {specificDate:d}");

        return new ReminderToolCall(
            ToolName: "create_reminder",
            Description: $"Create {recurrence.ToString().ToLower()} reminder: {description}",
            Details: detailSb.ToString(),
            TargetReminderId: null,
            Execute: async () =>
            {
                var created = await _reminderService.CreateAsync(
                    description, recurrence, timeOfDay, dayOfWeek, dayOfMonth, month, specificDate);
                return $"Reminder created successfully (ID: {created.Id}). Next fire at: {created.NextFireAt:g}";
            });
    }

    private async Task<ReminderToolCall> PrepareUpdateReminder(IDictionary<string, object?> args)
    {
        var idStr = GetStringArg(args, "id");
        if (!Guid.TryParse(idStr, out var id))
        {
            _logger.LogWarning("update_reminder called with invalid ID: '{IdValue}'", idStr);
            return new ReminderToolCall("update_reminder", "Invalid ID format", null, null,
                () => Task.FromResult<object?>($"Error: Invalid reminder ID format. You provided '{idStr}' which is not a valid GUID. Use query_reminders to get valid IDs."));
        }

        var existing = await _reminderService.GetAsync(id);
        if (existing is null)
            return new ReminderToolCall("update_reminder", "Reminder not found", null, null,
                () => Task.FromResult<object?>($"Error: Reminder {id} not found"));

        var description = GetOptionalStringArg(args, "description");
        var recurrenceStr = GetOptionalStringArg(args, "recurrence");
        var timeOfDayStr = GetOptionalStringArg(args, "timeOfDay");
        var dayOfWeekStr = GetOptionalStringArg(args, "dayOfWeek");
        var dayOfMonthStr = GetOptionalStringArg(args, "dayOfMonth");
        var monthStr = GetOptionalStringArg(args, "month");

        RecurrenceType? recurrence = recurrenceStr is not null && Enum.TryParse<RecurrenceType>(recurrenceStr, true, out var r) ? r : null;
        TimeOnly? timeOfDay = timeOfDayStr is not null && TimeOnly.TryParse(timeOfDayStr, out var t) ? t : null;
        DayOfWeek? dayOfWeek = dayOfWeekStr is not null && Enum.TryParse<DayOfWeek>(dayOfWeekStr, true, out var dow) ? dow : null;
        int? dayOfMonth = dayOfMonthStr is not null && int.TryParse(dayOfMonthStr, out var dom) ? dom : null;
        int? month = monthStr is not null && int.TryParse(monthStr, out var m) ? m : null;

        return new ReminderToolCall(
            ToolName: "update_reminder",
            Description: $"Update reminder: {existing.Description}",
            Details: $"Current: {existing.Recurrence} at {existing.TimeOfDay:HH:mm}\nChanges will be applied.",
            TargetReminderId: id,
            Execute: async () =>
            {
                await _reminderService.UpdateAsync(id, description, recurrence, timeOfDay, dayOfWeek, dayOfMonth, month);
                return $"Reminder {id} updated successfully.";
            });
    }

    private async Task<ReminderToolCall> PrepareDeleteReminder(IDictionary<string, object?> args)
    {
        var idStr = GetStringArg(args, "id");
        if (!Guid.TryParse(idStr, out var id))
        {
            _logger.LogWarning("delete_reminder called with invalid ID: '{IdValue}'", idStr);
            return new ReminderToolCall("delete_reminder", "Invalid ID format", null, null,
                () => Task.FromResult<object?>($"Error: Invalid reminder ID format. You provided '{idStr}' which is not a valid GUID. Use query_reminders to get valid IDs."));
        }

        var existing = await _reminderService.GetAsync(id);
        if (existing is null)
            return new ReminderToolCall("delete_reminder", "Reminder not found", null, null,
                () => Task.FromResult<object?>($"Error: Reminder {id} not found"));

        return new ReminderToolCall(
            ToolName: "delete_reminder",
            Description: $"Delete reminder: {existing.Description}",
            Details: $"This will permanently delete this {existing.Recurrence.ToString().ToLower()} reminder ({existing.TimeOfDay:HH:mm}).",
            TargetReminderId: id,
            Execute: async () =>
            {
                await _reminderService.DeleteAsync(id);
                return $"Reminder {id} deleted successfully.";
            });
    }

    // Schema methods — signature only, used by AIFunctionFactory for reflection
    [Description("Create a new reminder")]
    private static string CreateReminderSchema(
        [Description("What to remind about")] string description,
        [Description("Recurrence type: Once, Daily, Weekly, Monthly, Yearly")] string recurrence,
        [Description("Time of day in HH:mm format (e.g., '21:00', '09:30')")] string timeOfDay,
        [Description("Day of week for Weekly reminders (e.g., 'Monday')")] string? dayOfWeek = null,
        [Description("Day of month for Monthly/Yearly reminders (1-31)")] string? dayOfMonth = null,
        [Description("Month for Yearly reminders (1-12)")] string? month = null,
        [Description("Specific date for Once reminders in yyyy-MM-dd format")] string? specificDate = null) => "";

    [Description("List reminders")]
    private static string QueryRemindersSchema(
        [Description("Filter: 'active' (default) or 'all'")] string filter = "active") => "";

    [Description("Update an existing reminder")]
    private static string UpdateReminderSchema(
        [Description("The ID of the reminder to update")] string id,
        [Description("New description (optional)")] string? description = null,
        [Description("New recurrence type (optional): Once, Daily, Weekly, Monthly, Yearly")] string? recurrence = null,
        [Description("New time of day in HH:mm format (optional)")] string? timeOfDay = null,
        [Description("New day of week (optional)")] string? dayOfWeek = null,
        [Description("New day of month (optional)")] string? dayOfMonth = null,
        [Description("New month (optional)")] string? month = null) => "";

    [Description("Delete a reminder")]
    private static string DeleteReminderSchema(
        [Description("The ID of the reminder to delete")] string id) => "";

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
