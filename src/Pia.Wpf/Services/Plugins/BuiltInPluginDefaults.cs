using Pia.Shared.Models;

namespace Pia.Services.Plugins;

/// <summary>
/// Hardcoded defaults for built-in plugins. Used on first launch or offline when no server data is cached. The
/// GUIDs are well-known and stable, but they do NOT all match server seed data: only memory/todo/reminder
/// (...001-...003) are seeded server-side. scheduled-research (...004), files (...006), ingest (...007), git
/// (...008), chat-history (...009) and assignments (...00A) are client-only built-ins with no server plugin
/// row — the server's sync push tolerates a preference referencing such an unknown plugin id by skipping it, so toggling a client-only
/// built-in cannot wedge preference sync (SyncService.PushAsync in the Pia server repo).
/// </summary>
public static class BuiltInPluginDefaults
{
    public static readonly Guid MemoryPluginId = new("10000000-0000-0000-0000-000000000001");
    public static readonly Guid TodoPluginId = new("10000000-0000-0000-0000-000000000002");
    public static readonly Guid ReminderPluginId = new("10000000-0000-0000-0000-000000000003");
    public static readonly Guid ScheduledResearchPluginId = new("10000000-0000-0000-0000-000000000004");
    // Retired: the research-history plugin was removed with the research view. The id is kept in
    // PreloadedPluginIds (but not in Defaults) so any legacy persisted row is skipped, not re-seeded
    // or treated as an unknown server plugin.
    public static readonly Guid ResearchHistoryPluginId = new("10000000-0000-0000-0000-000000000005");
    public static readonly Guid FilesPluginId = new("10000000-0000-0000-0000-000000000006");
    public static readonly Guid IngestPluginId = new("10000000-0000-0000-0000-000000000007");
    public static readonly Guid GitPluginId = new("10000000-0000-0000-0000-000000000008");
    public static readonly Guid ChatHistoryPluginId = new("10000000-0000-0000-0000-000000000009");
    public static readonly Guid AssignmentsPluginId = new("10000000-0000-0000-0000-00000000000A");

    public static readonly HashSet<Guid> PreloadedPluginIds = [
        MemoryPluginId, TodoPluginId, ReminderPluginId,
        ScheduledResearchPluginId, ResearchHistoryPluginId, FilesPluginId, IngestPluginId, GitPluginId,
        ChatHistoryPluginId, AssignmentsPluginId];

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
            ConfigJson = """{"handlerId":"memory","defaultEnabled":true,"systemPromptAddition":"You have a persistent memory system. To store or update personal information, call remember(type, subject, content) — it AUTOMATICALLY finds-or-creates the right record and de-duplicates, so you do NOT need to look anything up first. To look something up, call recall(query). To remove a record, call forget(reference). Valid type values: personal_profile, contact_list, preference, note, project, topic. To correct an EXISTING raw source document (not a memory record) — e.g. fixing an error in an already-ingested transcript or report — call update_source(reference, content) with the full corrected text; get the reference from a topic's cited sources via read_topic or from search_files. To create a NEW source document worth keeping as a primary source — a document the user pasted, meeting notes, a decision write-up — call create_source(reference, content) instead; it ingests automatically. Do not use write_file for a vault source, new or existing: its path spelling differs from update_source's/create_source's and will not resolve the same way, and it does not work if the chat's working directory is scoped elsewhere."}""",
            UpdatedAt = new DateTime(2026, 8, 11, 0, 0, 0, DateTimeKind.Utc)
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
            ConfigJson = """{"handlerId":"scheduled-research","defaultEnabled":true,"systemPromptAddition":"You can schedule recurring research jobs that run on a cron schedule. Use create_scheduled_research to set one up, query_scheduled_research to list them, update_scheduled_research and delete_scheduled_research to manage existing ones. For a routine of a familiar kind, check list_routine_blueprints first and create it with create_routine_from_blueprint rather than writing the prompt freehand."}""",
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
            ConfigJson = """{"handlerId":"files","defaultEnabled":true,"systemPromptAddition":"You have access to a sandboxed local folder configured by the user under Settings > Assistant. Tools: list_files, read_file, search_files, write_file, delete_file. read_file returns line-numbered content as LINE|CONTENT and accepts optional offset/limit arguments to window large files (read a slice, then request the next slice if needed). search_files scans the folder for a text or regex pattern and returns matching files and lines. write_file shows the user a diff preview and requires their approval before the change is applied. Paths may be RELATIVE to that folder or absolute, but must stay inside the configured folder — the host rejects '..' traversal that escapes the folder and any absolute path that points outside it. If a tool returns an error about the folder not being configured, tell the user to set it in Settings > Assistant. When the user asks to summarize a file, call read_file first and then summarize the returned content in your reply. This folder is NOT the user's memory vault: nothing you write with write_file is in the vault, and in an unattended run the vault is not part of this folder at all. To put a document in the vault, call the memory tool create_source('sources/<name>', content) for a new source, or update_source(reference, content) to correct an existing one."}""",
            UpdatedAt = new DateTime(2026, 8, 31, 0, 0, 0, DateTimeKind.Utc)
        },
        [IngestPluginId] = new SyncPlugin
        {
            Id = IngestPluginId,
            Kind = "builtin_tool_pack",
            Name = "ingest",
            Description = "Compile raw documents from the vault's sources folder into recallable memory topic pages.",
            IsPreloaded = true,
            IsActive = true,
            Version = "1.0.0",
            ConfigJson = """{"handlerId":"ingest","defaultEnabled":true,"systemPromptAddition":"You can compile raw documents into recallable memory. Raw files live in the assistant vault's 'sources/' folder. Call ingest with the vault-relative path (e.g. ingest(\"sources/q2-report.txt\")) to extract the key entities from the file and write one memory topic page per entity — after that the content can be found with recall. To stage a NEW document, use the memory tool create_source(reference, content) instead of the files tools — it works no matter what folder the chat's working directory is scoped to, and ingests automatically, so you do not need a separate ingest call. To correct a source that has ALREADY been ingested, call the memory tool update_source(reference, content) instead, which shows a diff, applies it, and re-ingests automatically. Re-ingesting the same source does not create duplicates. Only text files are supported (e.g. txt, md, csv, json, html, xml, log)."}""",
            UpdatedAt = new DateTime(2026, 8, 11, 0, 0, 0, DateTimeKind.Utc)
        },
        [GitPluginId] = new SyncPlugin
        {
            Id = GitPluginId,
            Kind = "builtin_tool_pack",
            Name = "git",
            Description = "Run local git operations (status, log, diff, branch, show, init, add, commit, switch, restore, stash) on the repository in the working directory.",
            IsPreloaded = true,
            IsActive = true,
            Version = "1.0.0",
            ConfigJson = """{"handlerId":"git","defaultEnabled":true,"systemPromptAddition":"You can run local git operations on the repository in the active chat's working directory (inside the assistant files folder). Read-only tools (run inline): git_status, git_log, git_diff, git_branch, git_show. Mutating tools (each asks the user to approve before it runs): git_init, git_add, git_commit, git_switch, git_restore, git_stash. There are NO network operations: you cannot push, pull, fetch, or clone. If the working directory is not a git repository yet, call git_init to create one there, then retry. Prefer git_status and git_diff to review changes before you git_commit. Switch branches with git_switch (git_switch with create=true to make a new branch); never rely on a raw checkout. Discard changes with git_restore. All paths must stay inside the assistant files folder."}""",
            UpdatedAt = new DateTime(2026, 7, 15, 0, 0, 0, DateTimeKind.Utc)
        },
        [ChatHistoryPluginId] = new SyncPlugin
        {
            Id = ChatHistoryPluginId,
            Kind = "builtin_tool_pack",
            Name = "chat-history",
            Description = "Search and read the user's past conversations with the assistant.",
            IsPreloaded = true,
            IsActive = true,
            Version = "1.0.0",
            ConfigJson = """{"handlerId":"chat-history","defaultEnabled":true,"systemPromptAddition":"You can look up the user's PAST conversations with you. The CURRENT conversation is already in front of you: it is never returned by search_chats, and read_chat will refuse its id, so never pass it. Tools: search_chats(query, from_date, to_date, limit) returns past chats that match, each with a title, a date and a relevance snippet — omit query to list the most recent chats instead; read_chat(chat_id, offset, limit) returns a window of one chat's messages, oldest first. Always work in two steps: search first, then read the chat_id you actually need — a snippet is an excerpt, not a quotation, so never quote it or draw a conclusion from it alone. read_chat is paged: when has_more is true, call it again with next_offset. This history is NOT complete — chats older than the user's retention setting are deleted and an imported chat may have no useful title — so when a search misses, say you could not find it rather than asserting the conversation never happened."}""",
            UpdatedAt = new DateTime(2026, 8, 25, 0, 0, 0, DateTimeKind.Utc)
        },
        [AssignmentsPluginId] = new SyncPlugin
        {
            Id = AssignmentsPluginId,
            Kind = "builtin_tool_pack",
            Name = "assignments",
            Description = "Follow the user's background assignments — work the Pia server runs remotely on records they select.",
            IsPreloaded = true,
            IsActive = true,
            Version = "1.0.0",
            ConfigJson = """{"handlerId":"assignments","defaultEnabled":true,"systemPromptAddition":"The user can hand work to background assignments — the Pia server running a skill remotely over records the user picks, outside this chat. Tools: query_assignments lists their runs, newest first; get_assignment(assignment_id) reports one run's progress; start_assignment(skill, prompt) asks for a new run — in a chat it does not start until the user confirms it in a dialog and picks the records themselves, but in a background run that has been granted this tool it starts at once with no records attached, so never call it speculatively. A finished run's answer does NOT come back through these tools: it arrives as a new chat in the user's history, so point them there rather than promising to fetch it. You may propose what a run should do, but you never choose which records are sent — either the user selects those in the confirmation dialog or none are sent at all."}""",
            UpdatedAt = new DateTime(2026, 8, 26, 0, 0, 0, DateTimeKind.Utc)
        },
    };
}
