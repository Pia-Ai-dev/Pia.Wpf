using System.ComponentModel;
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
    private readonly ILogger<TodoToolHandler> _logger;

    public TodoToolHandler(ITodoService todoService, ILogger<TodoToolHandler> logger)
    {
        _todoService = todoService;
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
                "Delete a todo by ID. Use when the user wants to permanently remove a task.")
        ];
    }

    public async Task<(object? Result, TodoToolCall? PendingAction)> HandleToolCallAsync(
        FunctionCallContent toolCall,
        CancellationToken cancellationToken = default)
    {
        var args = toolCall.Arguments ?? new Dictionary<string, object?>();

        return toolCall.Name switch
        {
            "query_todos" => (await HandleQueryTodos(args), null),
            "create_todo" => (null, PrepareCreateTodo(args)),
            "complete_todo" => (null, await PrepareCompleteTodo(args)),
            "update_todo" => (null, await PrepareUpdateTodo(args)),
            "delete_todo" => (null, await PrepareDeleteTodo(args)),
            _ => ($"Unknown tool: {toolCall.Name}", null)
        };
    }

    public async Task<object?> ExecutePendingActionAsync(TodoToolCall pendingAction)
    {
        try
        {
            return await pendingAction.Execute();
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

        IReadOnlyList<TodoItem> todos;
        if (filter.Equals("completed", StringComparison.OrdinalIgnoreCase))
            todos = await _todoService.GetCompletedAsync();
        else if (filter.Equals("all", StringComparison.OrdinalIgnoreCase))
            todos = await _todoService.GetAllAsync();
        else
            todos = await _todoService.GetPendingAsync();

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

    private TodoToolCall PrepareCreateTodo(IDictionary<string, object?> args)
    {
        var title = GetStringArg(args, "title");
        var priorityStr = GetStringArg(args, "priority");
        var notes = GetOptionalStringArg(args, "notes");
        var dueDateStr = GetOptionalStringArg(args, "dueDate");

        var priority = Enum.TryParse<TodoPriority>(priorityStr, true, out var p) ? p : TodoPriority.Medium;
        DateTime? dueDate = dueDateStr is not null && DateTime.TryParse(dueDateStr, out var dd) ? dd : null;

        var detailSb = new StringBuilder();
        detailSb.AppendLine($"Title: {title}");
        detailSb.AppendLine($"Priority: {priority}");
        if (notes is not null) detailSb.AppendLine($"Notes: {notes}");
        if (dueDate.HasValue) detailSb.AppendLine($"Due: {dueDate.Value:g}");

        return new TodoToolCall(
            ToolName: "create_todo",
            Description: $"Create {priority.ToString().ToLower()} priority todo: {title}",
            Details: detailSb.ToString(),
            TargetTodoId: null,
            Execute: async () =>
            {
                var created = await _todoService.CreateAsync(title, priority, notes, dueDate);
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
            return new TodoToolCall("complete_todo", "Invalid ID format", null, null,
                () => Task.FromResult<object?>("Error: Invalid todo ID format"));

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
            return new TodoToolCall("update_todo", "Invalid ID format", null, null,
                () => Task.FromResult<object?>("Error: Invalid todo ID format"));

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
            return new TodoToolCall("delete_todo", "Invalid ID format", null, null,
                () => Task.FromResult<object?>("Error: Invalid todo ID format"));

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

    // Schema methods — signature only, used by AIFunctionFactory for reflection
    [Description("Create a new todo item")]
    private static string CreateTodoSchema(
        [Description("Short task description")] string title,
        [Description("Priority: Low, Medium (default), High")] string? priority = null,
        [Description("Optional extra detail or notes")] string? notes = null,
        [Description("Optional due date in yyyy-MM-dd or yyyy-MM-ddTHH:mm format")] string? dueDate = null) => "";

    [Description("List todos")]
    private static string QueryTodosSchema(
        [Description("Filter: 'pending' (default), 'completed', or 'all'")] string filter = "pending") => "";

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
