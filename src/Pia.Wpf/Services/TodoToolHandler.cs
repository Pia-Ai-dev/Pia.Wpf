using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Pia.Models;
using Pia.Services.Interfaces;

namespace Pia.Services;

public class TodoToolHandler : ITodoToolHandler
{
    private readonly ITodoService _todoService;
    private readonly IKanbanColumnService _columnService;
    private readonly ILogger<TodoToolHandler> _logger;

    public TodoToolHandler(ITodoService todoService, IKanbanColumnService columnService, ILogger<TodoToolHandler> logger)
    {
        _todoService = todoService;
        _columnService = columnService;
        _logger = logger;
    }

    public IList<AITool> GetTools()
    {
        return
        [
            AIFunctionFactory.Create(CreateTodoSchema, "create_todo",
                "Create a new todo item. Use this when the user mentions something they need to do. " +
                "Priority: Low, Medium (default), High. Due date is optional but enables reminder suggestions."),

            AIFunctionFactory.Create(QueryTodosSchema, "query_todos",
                "List the user's todos. Use filter 'pending' (default) for active tasks, 'completed' for done tasks, 'all' for everything."),

            AIFunctionFactory.Create(CompleteTodoSchema, "complete_todo",
                "Mark a todo as completed by ID. Use when the user says they finished a task."),

            AIFunctionFactory.Create(UpdateTodoSchema, "update_todo",
                "Update an existing todo by ID. Only provide fields that need to change."),

            AIFunctionFactory.Create(DeleteTodoSchema, "delete_todo",
                "Delete a todo by ID. Use when the user wants to permanently remove a task."),

            AIFunctionFactory.Create(ListColumnsSchema, "list_columns",
                "List all kanban board columns with their names and todo counts."),

            AIFunctionFactory.Create(MoveTodoSchema, "move_todo",
                "Move a todo to a different kanban column by specifying the todo ID and column name.")
        ];
    }

    public async Task<(object? Result, TodoToolCall? PendingAction)> HandleToolCallAsync(
        FunctionCallContent toolCall,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("TodoToolHandler dispatching: {ToolName}", toolCall.Name);
#if DEBUG
        Debug.WriteLine($"[TodoToolHandler Args] {toolCall.Name}: {JsonSerializer.Serialize(toolCall.Arguments)}");
#endif
        var args = toolCall.Arguments ?? new Dictionary<string, object?>();

        (object? result, TodoToolCall? pending) = toolCall.Name switch
        {
            "query_todos" => ((object?)await HandleQueryTodos(args), (TodoToolCall?)null),
            "list_columns" => ((object?)await HandleListColumns(), (TodoToolCall?)null),
            "move_todo" => ((object?)null, await PrepareMoveTodo(args)),
            "create_todo" => ((object?)null, await PrepareCreateTodo(args)),
            "complete_todo" => ((object?)null, await PrepareCompleteTodo(args)),
            "update_todo" => ((object?)null, await PrepareUpdateTodo(args)),
            "delete_todo" => ((object?)null, await PrepareDeleteTodo(args)),
            _ => ((object?)$"Unknown tool: {toolCall.Name}", (TodoToolCall?)null)
        };

        // Error cases (invalid ID, not found) produce a pending action with no TargetTodoId.
        // Return them as immediate results so no action card is shown to the user.
        if (pending is not null && pending.TargetTodoId is null && toolCall.Name is not "create_todo")
        {
            _logger.LogWarning("TodoToolHandler {ToolName} returning error: {Description}", toolCall.Name, pending.Description);
            return (await pending.Execute(), null);
        }

        _logger.LogDebug("TodoToolHandler {ToolName} result: hasResult={HasResult}, hasPending={HasPending}",
            toolCall.Name, result is not null, pending is not null);
        return (result, pending);
    }

    public async Task<object?> ExecutePendingActionAsync(TodoToolCall pendingAction)
    {
        _logger.LogDebug("Executing todo action: {ToolName}, targetId={TargetTodoId}",
            pendingAction.ToolName, pendingAction.TargetTodoId);
        try
        {
            var result = await pendingAction.Execute();
            _logger.LogInformation("Todo action completed: {ToolName}", pendingAction.ToolName);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute todo tool action: {ToolName}", pendingAction.ToolName);
            return $"Error executing {pendingAction.ToolName}: {ex.Message}";
        }
    }

    private async Task<object?> HandleQueryTodos(IDictionary<string, object?> args)
    {
        var filter = GetStringArg(args, "filter");
        var columnName = GetOptionalStringArg(args, "column");

        IReadOnlyList<TodoItem> todos;
        if (filter.Equals("completed", StringComparison.OrdinalIgnoreCase))
            todos = await _todoService.GetCompletedAsync();
        else if (filter.Equals("all", StringComparison.OrdinalIgnoreCase))
            todos = await _todoService.GetAllAsync();
        else
            todos = await _todoService.GetPendingAsync();

        var columns = await _columnService.GetAllAsync();

        if (columnName is not null)
        {
            var targetColumn = columns.FirstOrDefault(c =>
                c.Name.Equals(columnName, StringComparison.OrdinalIgnoreCase));
            if (targetColumn is not null)
                todos = todos.Where(t => t.ColumnId == targetColumn.Id).ToList().AsReadOnly();
        }

        if (todos.Count == 0)
            return filter.Equals("completed", StringComparison.OrdinalIgnoreCase)
                ? "No completed todos found."
                : "No pending todos. The task list is empty.";

        var now = DateTime.Now;
        var sb = new StringBuilder();
        sb.AppendLine($"Found {todos.Count} todo(s):");

        foreach (var t in todos)
        {
            sb.AppendLine($"\n[ID: {t.Id}] {t.Title}");
            sb.AppendLine($"  Priority: {t.Priority}, Status: {t.Status}");
            if (t.ColumnId.HasValue)
            {
                var col = columns.FirstOrDefault(c => c.Id == t.ColumnId.Value);
                if (col is not null)
                    sb.AppendLine($"  Column: {col.Name}");
            }
            if (t.DueDate.HasValue)
            {
                var overdue = t.Status == TodoStatus.Pending && t.DueDate.Value < now;
                sb.AppendLine($"  Due: {t.DueDate.Value:g}{(overdue ? " (OVERDUE)" : "")}");
            }
            if (t.Notes is not null)
                sb.AppendLine($"  Notes: {t.Notes}");
            if (t.CompletedAt.HasValue)
                sb.AppendLine($"  Completed: {t.CompletedAt.Value:g}");
            if (t.LinkedReminderId.HasValue)
                sb.AppendLine($"  Linked reminder: {t.LinkedReminderId.Value}");
        }

        return sb.ToString();
    }

    private async Task<TodoToolCall> PrepareCreateTodo(IDictionary<string, object?> args)
    {
        var title = GetStringArg(args, "title");
        var priorityStr = GetStringArg(args, "priority");
        var notes = GetOptionalStringArg(args, "notes");
        var dueDateStr = GetOptionalStringArg(args, "dueDate");

        var priority = Enum.TryParse<TodoPriority>(priorityStr, true, out var p) ? p : TodoPriority.Medium;
        DateTime? dueDate = dueDateStr is not null && DateTime.TryParse(dueDateStr, out var dd) ? dd : null;

        var columnName = GetOptionalStringArg(args, "column");
        Guid? columnId = null;
        if (columnName is not null)
        {
            var columns = await _columnService.GetAllAsync();
            var targetColumn = columns.FirstOrDefault(c =>
                c.Name.Equals(columnName, StringComparison.OrdinalIgnoreCase));
            if (targetColumn is not null)
                columnId = targetColumn.Id;
        }

        var detailSb = new StringBuilder();
        detailSb.AppendLine($"Title: {title}");
        detailSb.AppendLine($"Priority: {priority}");
        if (notes is not null) detailSb.AppendLine($"Notes: {notes}");
        if (dueDate.HasValue) detailSb.AppendLine($"Due: {dueDate.Value:g}");
        if (columnName is not null) detailSb.AppendLine($"Column: {columnName}");

        return new TodoToolCall(
            ToolName: "create_todo",
            Description: $"Create {priority.ToString().ToLower()} priority todo: {title}",
            Details: detailSb.ToString(),
            TargetTodoId: null,
            Execute: async () =>
            {
                var created = await _todoService.CreateAsync(title, priority, notes, dueDate, columnId);
                var result = $"Todo created successfully (ID: {created.Id}).";
                if (dueDate.HasValue)
                    result += " This task has a due date. You may want to suggest a reminder.";
                return result;
            });
    }

    private async Task<TodoToolCall> PrepareCompleteTodo(IDictionary<string, object?> args)
    {
        var idStr = GetStringArg(args, "id");
        if (!Guid.TryParse(idStr, out var id))
        {
            _logger.LogWarning("complete_todo called with invalid ID: '{IdValue}'", idStr);
            return new TodoToolCall("complete_todo", "Invalid ID format", null, null,
                () => Task.FromResult<object?>($"Error: Invalid todo ID format. You provided '{idStr}' which is not a valid GUID. Use query_todos to get valid IDs."));
        }

        var existing = await _todoService.GetAsync(id);
        if (existing is null)
            return new TodoToolCall("complete_todo", "Todo not found", null, null,
                () => Task.FromResult<object?>($"Error: Todo {id} not found"));

        return new TodoToolCall(
            ToolName: "complete_todo",
            Description: $"Complete todo: {existing.Title}",
            Details: $"Mark this {existing.Priority.ToString().ToLower()} priority task as completed.",
            TargetTodoId: id,
            Execute: async () =>
            {
                await _todoService.CompleteAsync(id);
                return $"Todo \"{existing.Title}\" marked as completed.";
            });
    }

    private async Task<TodoToolCall> PrepareUpdateTodo(IDictionary<string, object?> args)
    {
        var idStr = GetStringArg(args, "id");
        if (!Guid.TryParse(idStr, out var id))
        {
            _logger.LogWarning("update_todo called with invalid ID: '{IdValue}'", idStr);
            return new TodoToolCall("update_todo", "Invalid ID format", null, null,
                () => Task.FromResult<object?>($"Error: Invalid todo ID format. You provided '{idStr}' which is not a valid GUID. Use query_todos to get valid IDs."));
        }

        var existing = await _todoService.GetAsync(id);
        if (existing is null)
            return new TodoToolCall("update_todo", "Todo not found", null, null,
                () => Task.FromResult<object?>($"Error: Todo {id} not found"));

        var title = GetOptionalStringArg(args, "title");
        var priorityStr = GetOptionalStringArg(args, "priority");
        var notes = GetOptionalStringArg(args, "notes");
        var dueDateStr = GetOptionalStringArg(args, "dueDate");
        var linkedReminderIdStr = GetOptionalStringArg(args, "linkedReminderId");

        return new TodoToolCall(
            ToolName: "update_todo",
            Description: $"Update todo: {existing.Title}",
            Details: $"Current: {existing.Priority} priority, {existing.Status} status\nChanges will be applied.",
            TargetTodoId: id,
            Execute: async () =>
            {
                if (title is not null) existing.Title = title;
                if (priorityStr is not null && Enum.TryParse<TodoPriority>(priorityStr, true, out var p))
                    existing.Priority = p;
                if (notes is not null) existing.Notes = notes;
                if (dueDateStr is not null && DateTime.TryParse(dueDateStr, out var dd))
                    existing.DueDate = dd;
                if (linkedReminderIdStr is not null && Guid.TryParse(linkedReminderIdStr, out var rid))
                    existing.LinkedReminderId = rid;

                await _todoService.UpdateAsync(existing);

                var result = $"Todo {id} updated successfully.";
                if (dueDateStr is not null && existing.DueDate.HasValue)
                    result += " This task has a due date. You may want to suggest a reminder.";
                return result;
            });
    }

    private async Task<TodoToolCall> PrepareDeleteTodo(IDictionary<string, object?> args)
    {
        var idStr = GetStringArg(args, "id");
        if (!Guid.TryParse(idStr, out var id))
        {
            _logger.LogWarning("delete_todo called with invalid ID: '{IdValue}'", idStr);
            return new TodoToolCall("delete_todo", "Invalid ID format", null, null,
                () => Task.FromResult<object?>($"Error: Invalid todo ID format. You provided '{idStr}' which is not a valid GUID. Use query_todos to get valid IDs."));
        }

        var existing = await _todoService.GetAsync(id);
        if (existing is null)
            return new TodoToolCall("delete_todo", "Todo not found", null, null,
                () => Task.FromResult<object?>($"Error: Todo {id} not found"));

        return new TodoToolCall(
            ToolName: "delete_todo",
            Description: $"Delete todo: {existing.Title}",
            Details: $"This will permanently delete this {existing.Priority.ToString().ToLower()} priority task.",
            TargetTodoId: id,
            Execute: async () =>
            {
                await _todoService.DeleteAsync(id);
                return $"Todo \"{existing.Title}\" deleted successfully.";
            });
    }

    private async Task<object?> HandleListColumns()
    {
        var columns = await _columnService.GetAllAsync();
        var sb = new StringBuilder();
        sb.AppendLine("Kanban board columns:");

        foreach (var col in columns)
        {
            var count = await _columnService.GetTodoCountAsync(col.Id);
            var markers = new List<string>();
            if (col.IsDefaultView) markers.Add("default");
            if (col.IsClosedColumn) markers.Add("closed");
            var markerStr = markers.Count > 0 ? $" ({string.Join(", ", markers)})" : "";
            sb.AppendLine($"  [{col.Id}] {col.Name}{markerStr} — {count} todo(s)");
        }

        return sb.ToString();
    }

    private async Task<TodoToolCall> PrepareMoveTodo(IDictionary<string, object?> args)
    {
        var idStr = GetStringArg(args, "id");
        if (!Guid.TryParse(idStr, out var id))
            return new TodoToolCall("move_todo", "Invalid ID format", null, null,
                () => Task.FromResult<object?>("Error: Invalid todo ID format"));

        var existing = await _todoService.GetAsync(id);
        if (existing is null)
            return new TodoToolCall("move_todo", "Todo not found", null, null,
                () => Task.FromResult<object?>($"Error: Todo {id} not found"));

        var columnName = GetStringArg(args, "column");
        var columns = await _columnService.GetAllAsync();
        var targetColumn = columns.FirstOrDefault(c =>
            c.Name.Equals(columnName, StringComparison.OrdinalIgnoreCase));

        if (targetColumn is null)
            return new TodoToolCall("move_todo", "Column not found", null, null,
                () => Task.FromResult<object?>($"Error: Column '{columnName}' not found. Available columns: {string.Join(", ", columns.Select(c => c.Name))}"));

        return new TodoToolCall(
            ToolName: "move_todo",
            Description: $"Move \"{existing.Title}\" to column \"{targetColumn.Name}\"",
            Details: $"This will move the todo from its current column to \"{targetColumn.Name}\"." +
                     (targetColumn.IsClosedColumn ? " This will mark the todo as completed." : ""),
            TargetTodoId: id,
            Execute: async () =>
            {
                await _todoService.MoveToColumnAsync(id, targetColumn.Id);
                var result = $"Todo \"{existing.Title}\" moved to \"{targetColumn.Name}\".";
                if (targetColumn.IsClosedColumn)
                    result += " The todo has been marked as completed.";
                return result;
            });
    }

    // Schema methods — signature only, used by AIFunctionFactory for reflection
    [Description("Create a new todo item")]
    private static string CreateTodoSchema(
        [Description("Short task description")] string title,
        [Description("Priority: Low, Medium (default), High")] string? priority = null,
        [Description("Optional extra detail or notes")] string? notes = null,
        [Description("Optional due date in yyyy-MM-dd or yyyy-MM-ddTHH:mm format")] string? dueDate = null,
        [Description("Optional kanban column name to create the todo in")] string? column = null) => "";

    [Description("List todos")]
    private static string QueryTodosSchema(
        [Description("Filter: 'pending' (default), 'completed', or 'all'")] string filter = "pending",
        [Description("Optional: filter by kanban column name")] string? column = null) => "";

    [Description("List kanban board columns")]
    private static string ListColumnsSchema() => "";

    [Description("Move a todo to a different column")]
    private static string MoveTodoSchema(
        [Description("The ID of the todo to move")] string id,
        [Description("Name of the target column")] string column) => "";

    [Description("Mark a todo as completed")]
    private static string CompleteTodoSchema(
        [Description("The ID of the todo to complete")] string id) => "";

    [Description("Update an existing todo")]
    private static string UpdateTodoSchema(
        [Description("The ID of the todo to update")] string id,
        [Description("New title (optional)")] string? title = null,
        [Description("New priority: Low, Medium, High (optional)")] string? priority = null,
        [Description("New notes (optional)")] string? notes = null,
        [Description("New due date in yyyy-MM-dd format (optional)")] string? dueDate = null,
        [Description("Reminder ID to link (optional)")] string? linkedReminderId = null) => "";

    [Description("Delete a todo")]
    private static string DeleteTodoSchema(
        [Description("The ID of the todo to delete")] string id) => "";

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
