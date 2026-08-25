# Chat-history tools for the assistant

**Status:** Implemented 2026-08-25, pending the human smoke test (checklist step 11). All four owner
decisions were settled 2026-08-25 and are recorded in §10. This doc plus its checklist are meant to
be executed cold — §11 carries every literal string the build needs.
**Owner:** Marco Altmann
**Written:** 2026-08-25
**Origin:** The question "does the assistant mode have tools to read the assistant chat
history?", answered *no* by reading the tool list the client actually sends. This doc designs
the missing pack on top of the retrieval plumbing that already exists for the history view.

## 1. What is true today

**The assistant sees only the chat it is in.** The tool array is assembled client-side —
`AssistantPromptComposer.PrepareTurn` → `PluginService.GetAllTools()` →
`PiaCloudChatClient.SerializeTools` — from seven built-in packs (memory, files, git, todo,
reminder, scheduled-research, ingest) plus any MCP plugin. The server cannot add a tool the
client would execute, because dispatch is by name into locally registered handlers. Nothing in
that catalog reaches another conversation.

**The retrieval half already exists**, wired for the history view only:

| Member | What it gives |
|---|---|
| `IAssistantChatService.SearchAsync(text, from, to, providerId, offset, limit)` | Chat **metadata rows**, `ORDER BY UpdatedAt DESC`. Hides message-less headless stubs. No snippet, no relevance rank. |
| `CountAsync(...)` | Total for the same filter. |
| `GetAsync(id)` | The chat plus its **entire** transcript. |
| `AssistantChatsFts(ChatId UNINDEXED, Title, Body)` | FTS5, content-carrying since the `content=''` rebuild. `Body` is the chat's messages `GROUP_CONCAT`-ed. |

**Local rows are plaintext.** E2EE is wire-only: `AssistantChatSyncService` decrypts through
`SyncMapper.FromSyncAssistantChat` *before* calling `SaveFromRemoteAsync`, so a chat synced from
another device is as searchable here as a local one.

**The file tools are not a workaround.** `history.db` sits in `PiaPaths.LocalDataDirectory`, and
`SensitivePathGuard.BuildBlockedRoots` blocks `%LOCALAPPDATA%\Pia`, `%APPDATA%\Pia` and both
routed data roots by name.

**Volume is real.** `docs/chat_import/2026-08-22-open-webui-import-fixes.md` records a 573-chat,
37 MB import. The design assumes a store far too large to browse, and hard-caps every result.

## 2. Shape: two read-only tools

```
search_chats(query?, from_date?, to_date?, limit?)   → ranked hits with snippets
read_chat(chat_id, offset?, limit?)                  → a windowed transcript
```

This is the vault's orient→drill loop (`recall` → `read_topic`) applied to conversations, and
the tool descriptions should point at each other the same way.

**Two tools, not three.** `SearchAsync` with no `searchText` already returns recency-ordered
rows, so "what did we talk about yesterday" is `search_chats` with `query` omitted. A separate
`list_chats` would buy nothing and cost prompt weight.

### `search_chats`

```csharp
[Description("Search past conversations with this assistant (not the current one) by keyword and date")]
private static string SearchChatsSchema(
    [Description("Keywords to look for in past chats. Omit to list the most recent chats instead.")] string? query = null,
    [Description("Only chats updated on or after this date (YYYY-MM-DD)")] string? from_date = null,
    [Description("Only chats updated on or before this date (YYYY-MM-DD)")] string? to_date = null,
    [Description("Max chats to return (default 10, max 25)")] int? limit = null) => "";
```

Result — one object per hit, no message bodies:

```json
{ "chat_id": "…", "title": "Pricing for the Hetzner box",
  "updated_at": "2026-08-19", "message_count": 24,
  "snippet": "…we settled on the CPX41 because …" }
```

plus a standing `note` on the envelope, in the spirit of `recall`'s: *"Snippets are excerpts.
Call read_chat(chat_id) for the actual conversation before relying on it."*

### `read_chat`

```csharp
[Description("Read a past conversation's messages by id, from a search_chats hit")]
private static string ReadChatSchema(
    [Description("chat_id from a search_chats hit")] string chat_id,
    [Description("0-based message index to start at, for paging a long chat")] int? offset = null,
    [Description("Max messages to return (default 40, max 100)")] int? limit = null) => "";
```

Returns `{ title, created_at, updated_at, message_count, messages: [{ index, role, timestamp,
content }], has_more, next_offset }`.

`ThinkingContent` is **never** returned — it is model-internal scratch, it is the largest field
on the row, and replaying one model's reasoning into another model's context is a known way to
manufacture confident nonsense.

## 3. What the store gains: exactly one method

`SearchAsync` returns metadata only, so a hit list built on it alone would be titles and dates —
the model would have to read whole chats to find anything. The query path needs rank and a
snippet, and the existing `Id IN (SELECT ChatId FROM …Fts WHERE … MATCH …)` shape throws both
away by construction.

Add **one** method beside it; leave `SearchAsync` untouched for the no-query recency path.

```csharp
Task<IReadOnlyList<AssistantChatSearchHit>> SearchRankedAsync(
    string searchText, DateTime? fromDate, DateTime? toDate, Guid? providerId,
    Guid? excludeChatId, int limit, CancellationToken ct = default);

public readonly record struct AssistantChatSearchHit(
    Guid Id, string? Title, DateTime UpdatedAt, Guid? ProviderId, string Snippet, int MessageCount);
```

```sql
SELECT c.Id, c.Title, c.UpdatedAt, c.ProviderId,
       snippet(AssistantChatsFts, 2, '', '', '…', 24),
       (SELECT COUNT(*) FROM AssistantChatMessages m WHERE m.ChatId = c.Id)
FROM AssistantChatsFts
JOIN AssistantChats c ON c.Id = AssistantChatsFts.ChatId
WHERE AssistantChatsFts MATCH @Search
  AND EXISTS (SELECT 1 FROM AssistantChatMessages m WHERE m.ChatId = c.Id)
  AND (@ExcludeId IS NULL OR c.Id <> @ExcludeId)
ORDER BY AssistantChatsFts.rank
LIMIT @Limit
```

Notes for whoever writes it:

- Column index `2` is `Body` (`0` = `ChatId`, `1` = `Title`). `MemoryService` already uses both
  the bare `ORDER BY rank` form and a joined-FTS form, so neither is new here.
- The join only works because the FTS table is content-carrying. That was a real bug once —
  `EnsureAssistantChatsFts` detects and rebuilds an old `content=''` table. **Assert it in a
  test**; do not infer it from the DDL.
- Reuse `BuildFtsQuery` for query sanitation. Do **not** reuse `BuildSearchWhere` — its `Id IN
  (…)` clause is the thing being replaced. Lift the "hide message-less stubs" `EXISTS` clause
  into a shared `const string` so the two queries cannot drift.
- Expand `toDate` to end-of-day exactly as `BuildSearchWhere` does
  (`.Date.AddDays(1).AddTicks(-1)`). Skip it and `to_date` means one thing on the ranked path
  and another on the recency path — the same request returns different chats depending on
  whether the model happened to pass a `query`.
- `providerId` is carried even though the settled decision (§10, G4) declines to use it, so
  reversing that is a call-site change rather than an interface change.
- Runs on the same gated dedicated connection as every other member (`_gate.WaitAsync` →
  `Connection()`). The gate is an awaited `SemaphoreSlim`, so a tool read overlapping a UI
  persist yields rather than blocks.

`read_chat` reuses `GetAsync` and slices in the handler. That reads a whole transcript to return
40 messages; for one chat that is acceptable, and a paged store method is the escape hatch if a
real profile proves otherwise.

## 4. Excluding the current chat — `TaskContext` needs a `ChatId`

The obvious source for "which chat am I in" is `TaskAmbient.Current?.TaskId`, and **it is wrong
on three of the four surfaces** — this table said two until the implementation read the code:

| Site | `TaskId` is |
|---|---|
| `ChatSession.cs:332` | the chat id ✔ |
| `ChatSession.cs:716` | `spec.RunId` — the **run** id, on the step-turn path |
| `BackgroundAssistantTurnRunner.cs:152` | `run?.Id ?? chatId` — the **run** id whenever a run exists |
| `HeadlessTurnExecutor.cs:490` | `_runId` |

A run id never equals a chat id, so an exclusion keyed on `TaskId` silently stops excluding in
exactly the unattended surfaces. Fix it at the source — `TaskContext` is a `readonly record
struct` whose optional members are already passed by name, so appending one is source-compatible:

```csharp
public readonly record struct TaskContext(
    Guid? TaskId, string? WorkingSubpath, Action<FileTouch>? OnFileTouched = null,
    string? WorkspaceRoot = null, Guid? ChatId = null);
```

Set it at all four sites (`ChatId: Id`, `ChatId: chatId`, `ChatId: _chatId` — verify `_chatId` is
assigned from `run.ChatId` before the ambient is set, not after).

**When `ChatId` is null, exclude nothing.** Failing open here costs tokens, not privacy: the
worst case is the model re-reading a transcript it already has in context. The privacy lever is
the settings toggle in §6, not this.

**`read_chat` honours the same exclusion.** It takes a raw chat id, so without an explicit check
the current conversation stays readable by id even though `search_chats` hides it — the exclusion
would be a property of one tool rather than of the pack. Refuse it with a plain sentence ("that is
the current conversation; it is already in front of you"), not an error. Note the wider shape
while you are here: unlike `list_files`, which an isolated run's workspace deliberately narrows,
this pack reads the same interactive history from inside a run. That is the §5 finding again, and
the toggle is still the only lever.

## 5. These tools are ungated on every surface — by existing design

Both gates take a `PluginToolCall`, which only a *pending action* produces:

- `ChatSession.ResolveToolGate(PluginToolCall pendingAction, …)`
- `BackgroundAssistantTurnRunner.HandleToolCallAsync` — `if (result is not null) return result;
  // read → always allowed`

A read-only handler returns `(result, null)` and never constructs one, so `search_chats` and
`read_chat` run inline with no action card, no approval, and no `ToolClass`. That is consistent
with `recall`, `read_file` and `git_status` — and it means **an unattended background run can
read every stored conversation with nobody watching.**

Two consequences, both deliberate:

- **No `ToolClassifier` / `ToolClass` / `ToolAutonomy` change is needed.** Read-only tools never
  reach the code those types serve. Adding a class member would ripple into the autonomy presets
  and change nothing.
- **The settings toggle is the only lever**, which is why §10 G1 answers it explicitly rather
  than leaving it to the implementer.

## 6. Registration — a `chat-history` built-in pack

Mirrors the git pack, the most recent built-in added. Every literal is in §11.

| # | File | Change |
|---|---|---|
| 1 | `Services/Interfaces/IChatHistoryToolHandler.cs` | new — the inline-only shape, copied from `IIngestToolHandler`: `IList<AITool> GetTools()`, `Task<object?> HandleToolCallAsync(FunctionCallContent, CancellationToken = default)`, plus `bool IsAvailable { get; }`. **No pending-action type and no tuple** — those exist only for handlers that raise approval cards. |
| 2 | `Services/ChatHistoryToolHandler.cs` | new — the two tools, caps, formatting |
| 3 | `Services/Interfaces/IAssistantChatService.cs` + `AssistantChatService.cs` | `SearchRankedAsync` + `AssistantChatSearchHit` (§3) |
| 4 | `Services/TaskAmbient.cs` + 4 call sites | `ChatId` (§4) |
| 5 | `Services/Plugins/BuiltInPluginDefaults.cs` | GUID `…-000000000009`, `Defaults` entry, `PreloadedPluginIds` |
| 6 | `Services/Plugins/BuiltInPluginHandler.cs` | `FromChatHistoryHandler`, with `isAvailable:` |
| 7 | `Services/Plugins/PluginService.cs` | ctor param + `"chat-history"` switch arm |
| 8 | `Bootstrapper.cs` (~line 536) | `services.AddSingleton<IChatHistoryToolHandler, ChatHistoryToolHandler>()` |
| 9 | `Services/AssistantPromptComposer.cs` | a step in the tool-selection decision tree |
| 10 | `Services/ActionCardBuilder.cs` + 3 `MessageStrings*.resx` | `Msg_Assistant_StatusSearchingChats`, `Msg_Assistant_StatusReadingChat` |
| 11 | `AppSettings`, `AssistantSettingsViewModel`, `AssistantView.xaml`, 3 `ViewStrings*.resx` | `AssistantChatHistoryToolsEnabled`, default `true` |

The plugin is **client-only** — no server seed row, same as files/ingest/git. `SyncService.PushAsync`
skips a preference naming an unknown plugin id, so toggling it cannot wedge preference sync.
Extend the `BuiltInPluginDefaults` class comment that lists which ids are server-seeded.

`systemPromptAddition` must name both tools verbatim (so the model stops inventing variants),
state that the current chat is excluded, and state that history is **not** complete — retention
evicts past `AppSettings.ChatHistoryRetentionDays`.

## 7. Caps, because the store is large

| Cap | Value | Why |
|---|---|---|
| `search_chats` hits | default 10, hard max 25 | A hit is ~4 short fields; 25 is already a wall of text. |
| `read_chat` messages | default 40, hard max 100 | One window, then page. |
| Per-message body | ~1500 chars, then `…[truncated]` | One chat from a 37 MB import must not eat the window. |
| Snippet | 24 tokens | FTS5's own budget. |
| `ThinkingContent` | never returned | §2. |

Clamp in the handler, never trust the model's number, and always return `has_more`/`next_offset`
so a truncated read is visibly truncated rather than silently short.

## 8. Non-goals

- **No writes.** No `delete_chat`, no `rename_chat`. Deleting a conversation is a human act.
- **No `TouchLastAccessedAsync`.** An assistant read must not reset the retention clock or
  reorder the user's history list. This is a rule, not an oversight.
- **No @-command domain** (`@chat …`) in v1 — `AtCommandDomain`, `GetAtCommandToolMapping` and
  the picker are a separate surface. Add later if the tools earn it.
- **No sync impact.** Reads only; nothing new crosses the wire.
- **No new logging exposure.** Titles, queries, snippets and message bodies are all user content:
  `SensitiveDebug` or nothing. Log ids and counts.

## 9. Test plan

- `ChatHistoryPluginRegistrationTests` — mirrors `GitPluginRegistrationTests`: preloaded,
  default-enabled, `handlerId`, prompt names both tools, adapter exposes them, `isAvailable`
  suppresses them.
- `ChatHistoryToolHandlerTests` — current chat excluded from search **and** refused by
  `read_chat`; a null `ChatId` returns everything; caps clamped above and below;
  `has_more`/`next_offset` correct; `ThinkingContent` absent; unknown `chat_id` returns a
  readable error, not an exception.
- `AssistantChatServiceRankedSearchTests` — snippet non-empty for a body match (**this is the
  content-carrying-FTS assertion**), rank orders by relevance not recency, `excludeChatId`
  honoured, message-less stubs hidden, and `to_date` resolves to the same end-of-day bound as
  `SearchAsync` — assert the two paths agree, since the model chooses between them by whether it
  passed a query.
- `TaskContextChatIdTests` — all four ambient sites carry a chat id, and it is the *chat* id on
  the two run surfaces.
- Re-run unchanged: `PluginServiceToolCatalogTests` (its one `new PluginService(...)` needs the
  new substitute argument), `ToolClassifierTests` (staying unchanged is the proof that no class
  was added), localization parity.

## 10. Decisions taken

Settled by the owner on 2026-08-25. Recorded here so nothing below has to be re-litigated.

| # | Question | Answer |
|---|---|---|
| **G1** | Settings toggle, and its default? | **Toggle, default on.** `AssistantChatHistoryToolsEnabled = true`, mirroring `AssistantGitToolsEnabled`. The cross-provider flow it enables already exists for `recall` and `read_file`; the toggle exists because §5 makes it the only off switch. |
| **G2** | Are headless agent-run chats searchable? | **Yes, and unlabelled.** They are real work. `WindowMode` cannot distinguish them (`HeadlessTurnExecutor.BuildChatSnapshot` writes `"Assistant"`), and the only thing that can — `IAgentRunService.GetByChatAsync` — costs a dependency plus one query per hit. Labelling is parked in the checklist's *not yet planned*. |
| **G3** | Ranked search with snippets, or metadata only? | **Ranked**, via the one new store method in §3. Metadata-only hits carry no evidence, so on a 573-chat store the model would have to read whole chats to find anything. |
| **G4** | Scope reads to the current chat's provider? | **No.** It would silently hide most of the history for anyone who has switched providers, with no signal explaining why. The parameter is carried on `SearchRankedAsync` anyway (§3) so reversing this is a call-site change. |

## 11. Implementation appendix — the literal strings

Everything a cold session would otherwise have to invent. Line endings in this repo are **mixed
per file**: match the file you are editing (`git ls-files --eol <path>`), do not assume CRLF.

### 11.1 `BuiltInPluginDefaults.cs`

```csharp
public static readonly Guid ChatHistoryPluginId = new("10000000-0000-0000-0000-000000000009");
```

Add it to `PreloadedPluginIds`, and this entry to `Defaults`:

```csharp
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
```

Then extend the class `<summary>`'s list of client-only ids to include `chat-history (…009)`.

### 11.2 `PluginService.cs`

Constructor gains `IChatHistoryToolHandler chatHistoryToolHandler` (and its field), and
`InitializeBuiltInPlugins`'s switch gains:

```csharp
"chat-history" => BuiltInPluginHandler.FromChatHistoryHandler(_chatHistoryToolHandler, config),
```

No new `SettingsChanged` subscription: the ctor already wires
`_settingsService.SettingsChanged += (_, _) => RebuildToolNameRoutes();`, which is what makes the
G1 toggle take effect without a restart. There is exactly **one** `new PluginService(...)` in the
tree (`PluginServiceToolCatalogTests.cs:46`) — add `Substitute.For<IChatHistoryToolHandler>()`
after the git one.

### 11.3 `BuiltInPluginHandler.cs`

This pack is inline-only like ingest, but unlike ingest it needs git's availability gate. The
factory is the two combined — `executePending` is non-nullable on the ctor, so it throws rather
than being omitted:

```csharp
public static BuiltInPluginHandler FromChatHistoryHandler(
    IChatHistoryToolHandler handler, SyncPlugin config)
{
    return new BuiltInPluginHandler(
        config.Id,
        config.Name,
        handler.GetTools,
        async (toolCall, ct) => (await handler.HandleToolCallAsync(toolCall, ct), (PluginToolCall?)null),
        _ => throw new InvalidOperationException("The chat-history plugin has no pending actions."),
        GetSystemPromptFromConfig(config.ConfigJson),
        isAvailable: () => handler.IsAvailable);
}
```

`IsAvailable` reads `AppSettings.AssistantChatHistoryToolsEnabled` and refreshes on
`SettingsChanged`, the shape `GitToolHandler` uses at its lines 62 and 78.

### 11.4 `Bootstrapper.cs`, beside the other tool handlers (~line 536)

```csharp
services.AddSingleton<IChatHistoryToolHandler, ChatHistoryToolHandler>();
```

### 11.5 `AssistantPromptComposer.cs` — the decision tree

Step 4's `NO` currently ends the tree. Repoint it and append a step 5:

```
   - NO → Continue to step 5.
5. Does the request refer to something from an EARLIER conversation ("we talked about",
   "what did I tell you about X", "that chat where we…")?
   - YES → Use the chat-history tools: search_chats to find the conversation (omit query to
     list recent ones), then read_chat(chat_id) to read it. NOT chat history: "remember that I
     like coffee" (a fact to store = memory).
   - NO → Respond conversationally without tools.
```

And extend step 3's counter-example so memory and history do not compete:

```
NOT a memory: "what did we decide in that chat last week" (a past conversation = step 5).
```

### 11.6 `ActionCardBuilder.ResolveStatusText`, above the `git_` arm

```csharp
"search_chats" => _localizationService["Msg_Assistant_StatusSearchingChats"],
"read_chat" => _localizationService["Msg_Assistant_StatusReadingChat"],
```

### 11.7 `MessageStrings*.resx` — three files, one line each per key

```xml
<!-- MessageStrings.resx -->
<data name="Msg_Assistant_StatusSearchingChats" xml:space="preserve"><value>Searching past chats...</value></data>
<data name="Msg_Assistant_StatusReadingChat" xml:space="preserve"><value>Reading a past chat...</value></data>

<!-- MessageStrings.de.resx -->
<data name="Msg_Assistant_StatusSearchingChats" xml:space="preserve"><value>Frühere Chats werden durchsucht...</value></data>
<data name="Msg_Assistant_StatusReadingChat" xml:space="preserve"><value>Früherer Chat wird gelesen...</value></data>

<!-- MessageStrings.fr.resx -->
<data name="Msg_Assistant_StatusSearchingChats" xml:space="preserve"><value>Recherche dans les conversations passées...</value></data>
<data name="Msg_Assistant_StatusReadingChat" xml:space="preserve"><value>Lecture d'une conversation passée...</value></data>
```

Three dots, not `…` — that is the house style for these keys. Do not hand-edit `Designer.cs`.

### 11.8 The G1 toggle

`AppSettings.cs`, beside `AssistantGitToolsEnabled` (~line 400):

```csharp
public bool AssistantChatHistoryToolsEnabled { get; set; } = true;
```

`AssistantSettingsViewModel` needs four things, not two — the fourth is what makes the checkbox
actually persist:

```csharp
[ObservableProperty]                                   // beside _gitToolsEnabled (~line 120)
private bool _chatHistoryToolsEnabled = true;

partial void OnChatHistoryToolsEnabledChanged(bool value)   // beside OnGitToolsEnabledChanged (~line 183)
{
    if (!_isLoading) SaveSettingsAsync().SafeFireAndForget(_logger);
}
```

plus `ChatHistoryToolsEnabled = settings.AssistantChatHistoryToolsEnabled;` in the load path
(beside line 512) and `settings.AssistantChatHistoryToolsEnabled = ChatHistoryToolsEnabled;` in
the save path (beside line 681). Skip the `partial void` and the toggle reverts on the next load.

`Views/SettingsViews/AssistantView.xaml` — as shipped this sits in its own `StackPanel` in the
**Chat history** section, after the "Save chat history" block. "After the git checkbox", as this
section originally said, puts it directly above `Settings_AssistantFilesFolder_Description`, whose
"restricted to this folder and cannot access files outside it" then reads as its caption — the
opposite of what the toggle grants.

```xml
<CheckBox Content="{loc:Str Settings_AssistantChatHistoryToolsEnabled}"
          IsChecked="{Binding ChatHistoryToolsEnabled}"
          IsEnabled="{Binding Policy[AssistantChatHistoryToolsEnabled]}"
          AutomationProperties.AutomationId="Settings_Assistant_ChatHistoryToolsEnabled"/>
```

The `Policy[…]` key needs registering nowhere: `PolicyLock`'s indexer is
`!_policyService.IsEnforced(name)`, and `IsEnforced` is a set-membership test, so an unknown
setting name reads as *not enforced* and the checkbox is enabled. It only greys out if a group
policy later names this setting.

```xml
<!-- ViewStrings.resx -->
<data name="Settings_AssistantChatHistoryToolsEnabled" xml:space="preserve"><value>Allow the assistant to search and read your past chats</value></data>
<!-- ViewStrings.de.resx -->
<data name="Settings_AssistantChatHistoryToolsEnabled" xml:space="preserve"><value>Dem Assistenten erlauben, frühere Chats zu durchsuchen und zu lesen</value></data>
<!-- ViewStrings.fr.resx -->
<data name="Settings_AssistantChatHistoryToolsEnabled" xml:space="preserve"><value>Autoriser l'assistant à rechercher et lire vos conversations passées</value></data>
```

**`ViewAutomationIdTests` needs no edit.** Its per-view number is a deliberate non-vacuity
*floor*, not a count ("set well under the measured total so ordinary edits to the view never
touch this file"). Adding a control to an existing view does not move it; only a brand-new view
needs a new `[InlineData]` row.

### 11.9 Verification

```bash
dotnet build -t:Rebuild -v:n              # 0 Warning(s), 0 Error(s)
dotnet build -t:Rebuild -v:n -c Release   # same, again
dotnet test                               # the gate: failed: 0
```

Read the warning count off MSBuild's `N Warning(s)` summary line — at `-v:n` every warning prints
twice, so grepping the log double-counts. WPF re-reports `src/` warnings under a generated
`Pia.Wpf_<hash>_wpftmp.csproj`; fixing the source clears both.
