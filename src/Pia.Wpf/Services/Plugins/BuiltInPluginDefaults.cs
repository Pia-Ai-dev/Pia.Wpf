using Pia.Shared.Models;

namespace Pia.Services.Plugins;

/// <summary>
/// Hardcoded defaults for built-in plugins. Used on first launch or offline
/// when no server data is cached. Well-known GUIDs match server seed data.
/// </summary>
public static class BuiltInPluginDefaults
{
    public static readonly Guid MemoryPluginId = new("10000000-0000-0000-0000-000000000001");
    public static readonly Guid TodoPluginId = new("10000000-0000-0000-0000-000000000002");
    public static readonly Guid ReminderPluginId = new("10000000-0000-0000-0000-000000000003");
    public static readonly Guid ScheduledResearchPluginId = new("10000000-0000-0000-0000-000000000004");
    public static readonly Guid ResearchHistoryPluginId = new("10000000-0000-0000-0000-000000000005");

    public static readonly HashSet<Guid> PreloadedPluginIds = [
        MemoryPluginId, TodoPluginId, ReminderPluginId,
        ScheduledResearchPluginId, ResearchHistoryPluginId];

    public static readonly IReadOnlyDictionary<Guid, SyncPlugin> Defaults = new Dictionary<Guid, SyncPlugin>
    {
        [MemoryPluginId] = new SyncPlugin
        {
            Id = MemoryPluginId,
            Kind = "builtin_tool_pack",
            Name = "memory",
            Description = "Persistent memory system for storing and recalling personal information.",
            IsPreloaded = true,
            IsActive = true,
            Version = "1.0.0",
            ConfigJson = """{"handlerId":"memory","defaultEnabled":true,"systemPromptAddition":"You have a persistent memory system. When the user asks about something personal or tells you something to remember, use your memory tools to look it up or store it. Use list_memories to see what's stored, and query_memory to retrieve details.\n\nMemory workflow — ALWAYS follow this sequence when storing information:\n1. First call query_memory to check if a related memory already exists.\n2. If a match is found, use update_object to modify it (do NOT create a duplicate).\n3. Only if no related memory exists, use create_object to store it as new."}""",
            UpdatedAt = new DateTime(2026, 4, 6, 0, 0, 0, DateTimeKind.Utc)
        },
        [TodoPluginId] = new SyncPlugin
        {
            Id = TodoPluginId,
            Kind = "builtin_tool_pack",
            Name = "todo",
            Description = "Task management with kanban board support.",
            IsPreloaded = true,
            IsActive = true,
            Version = "1.0.0",
            ConfigJson = """{"handlerId":"todo","defaultEnabled":true,"systemPromptAddition":"You have access to a todo list for managing the user's tasks.\nTools: create_todo, query_todos, complete_todo, update_todo, delete_todo.\nWhen a user mentions something they need to do, offer to add it as a todo.\nWhen creating or updating a todo with a due date, suggest setting a reminder so they don't forget.\nWhen listing todos, highlight any that are overdue (past due date, still pending)."}""",
            UpdatedAt = new DateTime(2026, 4, 6, 0, 0, 0, DateTimeKind.Utc)
        },
        [ReminderPluginId] = new SyncPlugin
        {
            Id = ReminderPluginId,
            Kind = "builtin_tool_pack",
            Name = "reminder",
            Description = "Time-based reminders and notifications.",
            IsPreloaded = true,
            IsActive = true,
            Version = "1.0.0",
            ConfigJson = """{"handlerId":"reminder","defaultEnabled":true,"systemPromptAddition":"When the user asks about their reminders, use query_reminders. To modify or cancel, first query to find the ID."}""",
            UpdatedAt = new DateTime(2026, 4, 6, 0, 0, 0, DateTimeKind.Utc)
        },
        [ScheduledResearchPluginId] = new SyncPlugin
        {
            Id = ScheduledResearchPluginId,
            Kind = "builtin_tool_pack",
            Name = "scheduled-research",
            Description = "Schedule recurring research jobs and view results.",
            IsPreloaded = true,
            IsActive = true,
            Version = "1.0.0",
            ConfigJson = """{"handlerId":"scheduled-research","defaultEnabled":true,"systemPromptAddition":"You can schedule recurring research jobs that run on a cron schedule. Use create_scheduled_research to set one up, query_scheduled_research to list them, update_scheduled_research and delete_scheduled_research to manage existing ones."}""",
            UpdatedAt = new DateTime(2026, 5, 2, 0, 0, 0, DateTimeKind.Utc)
        },
        [ResearchHistoryPluginId] = new SyncPlugin
        {
            Id = ResearchHistoryPluginId,
            Kind = "builtin_tool_pack",
            Name = "research-history",
            Description = "Search past research findings.",
            IsPreloaded = true,
            IsActive = true,
            Version = "1.0.0",
            ConfigJson = """{"handlerId":"research-history","defaultEnabled":true,"systemPromptAddition":"You can search the user's prior research findings. Use search_research_history to find past research and get_research_entry to retrieve a full entry by ID."}""",
            UpdatedAt = new DateTime(2026, 5, 2, 0, 0, 0, DateTimeKind.Utc)
        }
    };
}
