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
    public static readonly Guid FilesPluginId = new("10000000-0000-0000-0000-000000000006");

    public static readonly HashSet<Guid> PreloadedPluginIds = [
        MemoryPluginId, TodoPluginId, ReminderPluginId,
        ScheduledResearchPluginId, ResearchHistoryPluginId, FilesPluginId];

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
            ConfigJson = """{"handlerId":"memory","defaultEnabled":true,"systemPromptAddition":"You have a persistent memory system. To store or update personal information, call remember(type, subject, content) — it AUTOMATICALLY finds-or-creates the right record and de-duplicates, so you do NOT need to look anything up first. To look something up, call recall(query). To remove a record, call forget(reference). Valid type values: personal_profile, contact_list, preference, note, project, topic."}""",
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
        },
        [FilesPluginId] = new SyncPlugin
        {
            Id = FilesPluginId,
            Kind = "builtin_tool_pack",
            Name = "files",
            Description = "Read, summarize, update and delete text files inside a user-configured sandbox folder.",
            IsPreloaded = true,
            IsActive = true,
            Version = "1.0.0",
            ConfigJson = """{"handlerId":"files","defaultEnabled":true,"systemPromptAddition":"You have access to a sandboxed local folder configured by the user under Settings > Assistant. Tools: list_files, read_file, search_files, write_file, delete_file. read_file returns line-numbered content as LINE|CONTENT and accepts optional offset/limit arguments to window large files (read a slice, then request the next slice if needed). search_files scans the folder for a text or regex pattern and returns matching files and lines. write_file shows the user a diff preview and requires their approval before the change is applied. Paths may be RELATIVE to that folder or absolute, but must stay inside the configured folder — the host rejects '..' traversal that escapes the folder and any absolute path that points outside it. If a tool returns an error about the folder not being configured, tell the user to set it in Settings > Assistant. When the user asks to summarize a file, call read_file first and then summarize the returned content in your reply."}""",
            UpdatedAt = new DateTime(2026, 5, 17, 0, 0, 0, DateTimeKind.Utc)
        }
    };
}
