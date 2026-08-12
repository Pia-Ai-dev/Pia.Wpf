using Microsoft.Extensions.Logging;
using Pia.Services.Interfaces;
using Pia.Shared.Operators;

namespace Pia.Services.Operators;

/// <summary>
/// Reads the local records a skill may be offered, in two deliberately separate steps: metadata for the
/// picker, and content only once consent exists. The split is what makes "nothing is read before the user
/// affirms it" an assertable property rather than a claim about call order.
/// </summary>
public interface IAssignmentScopeResolver
{
    /// <summary>Recent records of each requested type — never a whole store, and never a type the skill did
    /// not declare. An unknown or undeclared type contributes nothing rather than throwing: the server would
    /// refuse it anyway, and a picker that crashes on a newer server's vocabulary is worse.</summary>
    Task<IReadOnlyList<AssignmentScopeItem>> ListAsync(
        IReadOnlyList<string> declaredInputTypes, CancellationToken ct = default);

    /// <summary>The record's content. Null when it has since been deleted or changed type — the caller drops
    /// the item rather than sending a placeholder.</summary>
    Task<string?> ReadTextAsync(AssignmentScopeItem item, CancellationToken ct = default);
}

/// <inheritdoc cref="IAssignmentScopeResolver"/>
public sealed class AssignmentScopeResolver : IAssignmentScopeResolver
{
    /// <summary>How many of each kind the picker offers. A scoping UI is for choosing a handful of records,
    /// and the envelope takes at most 20 of them; listing every memory a long-term user has would make the
    /// list unusable and the pass expensive for nothing.</summary>
    private const int RecentPerType = 50;

    private readonly IMemoryService _memories;
    private readonly ITodoService _todos;
    private readonly ITemplateService _templates;
    private readonly IHistoryService _history;
    private readonly IAssistantChatService _chats;
    private readonly ILogger<AssignmentScopeResolver> _logger;

    public AssignmentScopeResolver(
        IMemoryService memories,
        ITodoService todos,
        ITemplateService templates,
        IHistoryService history,
        IAssistantChatService chats,
        ILogger<AssignmentScopeResolver> logger)
    {
        _memories = memories;
        _todos = todos;
        _templates = templates;
        _history = history;
        _chats = chats;
        _logger = logger;
    }

    public async Task<IReadOnlyList<AssignmentScopeItem>> ListAsync(
        IReadOnlyList<string> declaredInputTypes, CancellationToken ct = default)
    {
        var items = new List<AssignmentScopeItem>();

        foreach (var entityType in declaredInputTypes.Distinct(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            items.AddRange(entityType switch
            {
                AssignmentInputEntityTypes.Memory => await ListMemoriesAsync(),
                AssignmentInputEntityTypes.Todo => await ListTodosAsync(),
                AssignmentInputEntityTypes.Template => await ListTemplatesAsync(),
                AssignmentInputEntityTypes.Session => await ListSessionsAsync(ct),
                AssignmentInputEntityTypes.AssistantChat => await ListChatsAsync(ct),
                _ => SkipUnknown(entityType),
            });
        }

        return items;
    }

    public async Task<string?> ReadTextAsync(AssignmentScopeItem item, CancellationToken ct = default)
    {
        switch (item.EntityType)
        {
            case AssignmentInputEntityTypes.Memory:
                var memory = await _memories.GetObjectAsync(item.EntityId);
                return memory is null ? null : MemoryText(memory);

            case AssignmentInputEntityTypes.Todo:
                var todo = await _todos.GetAsync(item.EntityId);
                return todo is null ? null : TodoText(todo);

            case AssignmentInputEntityTypes.Template:
                var template = await _templates.GetTemplateAsync(item.EntityId);
                return template is null || template.IsBuiltIn ? null : TemplateText(template);

            case AssignmentInputEntityTypes.Session:
                var session = await _history.GetSessionAsync(item.EntityId);
                return session is null ? null : SessionText(session);

            case AssignmentInputEntityTypes.AssistantChat:
                var chat = await _chats.GetAsync(item.EntityId, ct);
                return chat is null ? null : ChatText(chat);

            default:
                return null;
        }
    }

    private IReadOnlyList<AssignmentScopeItem> SkipUnknown(string entityType)
    {
        _logger.LogInformation(
            "A skill declares entity type '{EntityType}', which this client cannot read; offering nothing for it.",
            entityType);
        return [];
    }

    // Each list pass counts characters from the content it already has in hand and then discards it, so the
    // picker can show a size and refuse an over-cap record without the content having been kept anywhere.
    // ReadTextAsync re-reads by id after consent; the second read is the price of the seam.

    private async Task<IReadOnlyList<AssignmentScopeItem>> ListMemoriesAsync()
    {
        var all = await _memories.GetAllObjectsAsync();
        return all
            .OrderByDescending(m => m.UpdatedAt)
            .Take(RecentPerType)
            .Select(m => new AssignmentScopeItem(
                AssignmentInputEntityTypes.Memory, m.Id, Label(m.Label, "Memory"),
                MemoryText(m).Length, m.UpdatedAt))
            .ToList();
    }

    private async Task<IReadOnlyList<AssignmentScopeItem>> ListTodosAsync()
    {
        var all = await _todos.GetAllAsync();
        return all
            .OrderByDescending(t => t.UpdatedAt)
            .Take(RecentPerType)
            .Select(t => new AssignmentScopeItem(
                AssignmentInputEntityTypes.Todo, t.Id, Label(t.Title, "Todo"),
                TodoText(t).Length, t.UpdatedAt))
            .ToList();
    }

    /// <summary>Custom templates only. A built-in is a client-side constant that never syncs and is not the
    /// user's own writing, so it is not theirs to consent to sending.</summary>
    private async Task<IReadOnlyList<AssignmentScopeItem>> ListTemplatesAsync()
    {
        var all = await _templates.GetTemplatesAsync();
        return all
            .Where(t => !t.IsBuiltIn)
            .OrderByDescending(t => t.ModifiedAt ?? t.CreatedAt)
            .Take(RecentPerType)
            .Select(t => new AssignmentScopeItem(
                AssignmentInputEntityTypes.Template, t.Id, Label(t.Name, "Template"),
                TemplateText(t).Length, t.ModifiedAt ?? t.CreatedAt))
            .ToList();
    }

    private async Task<IReadOnlyList<AssignmentScopeItem>> ListSessionsAsync(CancellationToken ct)
    {
        _ = ct;
        var sessions = await _history.GetSessionsAsync(0, RecentPerType);
        return sessions
            .Select(s => new AssignmentScopeItem(
                AssignmentInputEntityTypes.Session, s.Id, Label(FirstLine(s.OriginalText), "Session"),
                SessionText(s).Length, s.CreatedAt))
            .ToList();
    }

    private async Task<IReadOnlyList<AssignmentScopeItem>> ListChatsAsync(CancellationToken ct)
    {
        var chats = await _chats.SearchAsync(limit: RecentPerType, ct: ct);
        return chats
            .Select(c => new AssignmentScopeItem(
                AssignmentInputEntityTypes.AssistantChat, c.Id, Label(c.Title, "Conversation"),
                ChatText(c).Length, c.UpdatedAt))
            .ToList();
    }

    private static string MemoryText(Pia.Models.MemoryObject memory) =>
        string.IsNullOrWhiteSpace(memory.Label) ? memory.Data : $"{memory.Label}\n\n{memory.Data}";

    private static string TodoText(Pia.Models.TodoItem todo) =>
        string.IsNullOrWhiteSpace(todo.Notes) ? todo.Title : $"{todo.Title}\n\n{todo.Notes}";

    private static string TemplateText(Pia.Models.OptimizationTemplate template) =>
        string.IsNullOrWhiteSpace(template.Description)
            ? template.Prompt
            : $"{template.Description}\n\n{template.Prompt}";

    /// <summary>Both halves: what the user wrote and what came back. A session is only meaningful as the
    /// pair.</summary>
    private static string SessionText(Pia.Models.OptimizationSession session) =>
        $"Original:\n{session.OriginalText}\n\nResult:\n{session.OptimizedText}";

    private static string ChatText(Pia.Shared.Models.SyncAssistantChat chat) =>
        string.Join(
            "\n\n",
            chat.Messages
                .OrderBy(m => m.Timestamp)
                .Select(m => $"{m.Role}: {m.Content}"));

    private static string Label(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static string FirstLine(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var line = value.Split('\n', 2)[0].Trim();
        return line.Length <= 80 ? line : line[..80];
    }
}
