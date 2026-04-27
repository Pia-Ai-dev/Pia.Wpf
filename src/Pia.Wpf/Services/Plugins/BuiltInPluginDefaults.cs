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
    public static readonly Guid MeetingPluginId = new("10000000-0000-0000-0000-000000000004");

    public static readonly HashSet<Guid> PreloadedPluginIds = [MemoryPluginId, TodoPluginId, ReminderPluginId, MeetingPluginId];

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
        [MeetingPluginId] = new SyncPlugin
        {
            Id = MeetingPluginId,
            Kind = "builtin_tool_pack",
            Name = "meeting",
            Description = "Meeting transcript summarization and meeting-summary memory.",
            IsPreloaded = true,
            IsActive = true,
            Version = "1.0.0",
            ConfigJson = """{"handlerId":"meeting","defaultEnabled":true,"systemPromptAddition":"You can summarize saved meeting transcripts. After producing a summary, ask the user once whether they'd like to save it as a memory. If yes, call create_object with type=meeting_summary, label=<topic distilled from the summary>, and data as a JSON object with topic, date (from the front-matter), speakers (from the front-matter), originalFilename (from the front-matter), summaryKind (the chosen kind), and content (the summary you produced). Do not save without explicit user confirmation."}""",
            UpdatedAt = new DateTime(2026, 4, 27, 0, 0, 0, DateTimeKind.Utc)
        }
    };
}
