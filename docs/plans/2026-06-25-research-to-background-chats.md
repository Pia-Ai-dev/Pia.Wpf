# Remove Research Mode View → Background Assistant-Turn Jobs

**Date:** 2026-06-25
**Branch:** feature/meeting_attendee (or a new feature branch)

## Goal

Remove the entire **Research mode view** and the standalone research storage. Keep the
*background* research capability, but re-cast it as a **general, reusable headless
background assistant-turn runner**. A scheduled job, when due, runs the assistant-crafted
prompt as one full (tool-capable) assistant turn **off-thread, with no window**, and the
result is saved as a normal **assistant chat** (new chat per result), visible in chat history.

## Confirmed decisions

1. **Result delivery:** new assistant chat per completed job (prompt = user message,
   answer = assistant message).
2. **Triggering:** keep the existing `create/query/update/delete_scheduled_research` tools.
   No research view, no separate on-demand UI.
3. **Research storage:** drop client use — remove local `ResearchSessions` table, history
   service, export service, research-history tool handler. **Keep** the shared
   `SyncResearchSession` DTO and the `ResearchSessions` fields on `SyncPushRequest`/
   `SyncPullResponse` (server wire contract untouched); client just stops populating/reading them.
4. **Execution path:** **full assistant turn, run headless** (not a bare completion).
   Built as reusable infra for future background-assistant features.
5. **Tool policy (per job):** reads default-allow, writes default-deny. Extra write-tool
   grants are specified at job-creation time and stored on the job.
6. **Mode enum:** delete `WindowMode.Research`; repoint scheduled-research provider
   resolution to `WindowMode.Assistant`. Verify the persisted/synced `ModeProviderDefaults`
   int-keyed dictionary tolerates an orphan key (=2) from old data.
7. **5-step engine:** removed entirely. `AnswerLength` is obsolete (length now lives in the
   crafted prompt) — removed from the tool/model; DB column + shared DTO field left dormant
   to avoid migration/contract churn.

## New architecture: headless background assistant-turn runner

New service `IBackgroundAssistantTurnRunner` (impl `BackgroundAssistantTurnRunner`),
resolvable from the singleton `ScheduledJobBackgroundService` (resolved per-run from a DI
scope, so transient deps like `IAiClientService`/`IAssistantPromptComposer` are not captive).

```csharp
public sealed record BackgroundTurnRequest
{
    public required string Prompt { get; init; }          // assistant-crafted user turn
    public required AiProvider Provider { get; init; }     // resolved by caller
    public IReadOnlyCollection<string> GrantedWriteTools { get; init; } = [];
    public string? Title { get; init; }                    // initial title; else auto-derive
}

public sealed record BackgroundTurnResult(Guid ChatId, bool Succeeded, string? Error);

public interface IBackgroundAssistantTurnRunner
{
    Task<BackgroundTurnResult> RunAsync(BackgroundTurnRequest request, CancellationToken ct);
}
```

**Implementation** (reuses the exact interactive AI+plugin pipeline minus UI action cards):

1. Resolve settings + the default Assistant persona; build system prompt + tool list via
   `IAssistantPromptComposer.PrepareTurn(persona, provider, atCommands: [], tokenizationEnabled)`.
2. `chatMessages = [System(systemPrompt), User(prompt)]`.
3. **(Included)** set `TokenMapAmbient.Current` from a fresh initialized `ITokenMapService`
   for the turn (the IAiClientService tokenization decorator reads it); detokenize the final
   text; restore ambient in `finally`. Parity with the interactive path.
4. Stream `IAiClientService.GetChatCompletionWithToolsAsync(chatMessages, provider, tools,
   toolHandler, nameof(WindowMode.Assistant), ct)` accumulating text + stats.
5. **Policy tool handler** (the headless equivalent of `ChatSession.HandleToolCall`, no cards):
   ```
   route = await _pluginService.RouteToolCallAsync(toolCall)
   (result, pending) = route
   if result   != null -> return result                      // READ → always allowed
   if pending  != null -> GrantedWriteTools.Contains(pending.ToolName)
                            ? await pending.Execute()         // WRITE → granted
                            : "Denied: '<tool>' is a write action not granted to this job."
   ```
   (Read vs write is naturally encoded: reads return an immediate `result`; writes return a
   `pendingAction`. No pre-classification needed.)
6. Build `SyncAssistantChat { Id=new, WindowMode="Assistant", ProviderId=provider.Id,
   Messages=[user(prompt), assistant(text)], timestamps=now }`, `await _chatService.SaveAsync`.
7. Best-effort auto-title via `IChatTitleService`.
8. Return `(chatId, succeeded, error)`. On exception: persist a chat containing the error as
   the assistant message (so it shows in history) OR return failure without a chat — **plan:
   return failure, no chat; surface via toast** (mirrors today's failure UX).

Threading: runs on the background thread; the AI client + plugin handlers are services (not
UI-bound). `IActionCardBuilder`/UI dispatcher are never touched.

## Schema / persistence changes

- **`ScheduledJob`** (model): remove `AnswerLength`; add `public List<string> GrantedTools { get; set; } = [];`.
- **`ScheduledJobs` table:** add `GrantedTools TEXT` (JSON array) via PRAGMA-checked
  `ALTER TABLE ADD COLUMN` migration (mirror the existing `UpdatedAt`/`OwnerDeviceId` pattern).
  `AnswerLength` column kept dormant (has `DEFAULT 'Balanced'`, so omitting it from INSERT is fine).
- **`SyncScheduledJob`** (shared): add `GrantedTools` (additive, safe). `AnswerLength` field kept
  dormant.
- **`SyncMapper`** job mapping: map `GrantedTools`; stop mapping `AnswerLength` (default it).
- **`ResearchSessions` table:** `DROP TABLE IF EXISTS ResearchSessions` migration + remove from
  fresh `CREATE`.
- **`ScheduledJob.LastResultEntryId`** (already `Guid?`): now holds the result **chat id**
  (no type/schema change).
- **`IScheduledJobService.CreateAsync/UpdateAsync`:** drop `answerLength`, add `grantedTools`.

## Tool-handler changes (`ScheduledJobToolHandler`)

- `create_scheduled_research`: drop `answerLength` param; add `grantedTools` (comma-sep list of
  write-tool names the job may execute, default none). Update description to explain read-default-
  allow / write-default-deny and that the user must opt-in writes.
- `update_scheduled_research`: same param swap.
- `query_scheduled_research`: drop the AnswerLength line; show GrantedTools.

## Removals

**Delete files**
- ViewModels: `ResearchViewModel`, `ResearchHistoryViewModel`, `ResearchSettingsViewModel`.
- Views: `ResearchView.xaml(.cs)`, `ResearchHistoryView.xaml(.cs)`, `SettingsViews/ResearchView.xaml(.cs)`.
- Services: `ResearchService`/`IResearchService`, `ResearchHistoryService`/`IResearchHistoryService`,
  `ResearchExportService`/`IResearchExportService`, `ResearchHistoryToolHandler`/`IResearchHistoryToolHandler`.
- Models: `ResearchSession`, `ResearchStep`, `ResearchStatus`, `ResearchAnswerLength`, `ResearchHistoryEntry`.
- **Keep** `SyncResearchSession` (shared DTO).

**Edit**
- `App.xaml`: remove the two research `DataTemplate`s.
- `Bootstrapper.cs`: remove DI for all deleted types; register `IBackgroundAssistantTurnRunner`.
- `WindowMode.cs`: remove `Research`.
- WindowMode.Research usages: `MainWindowViewModel` (nav shortcuts/labels), `NavigationSidebarView.xaml`,
  `TrayIconService` (menu item + hotkey), `WindowManagerService` + `IWindowManagerService`,
  `ProviderService`, `ProvidersSettingsViewModel` (ResearchProviderId), `GeneralSettingsViewModel`
  (ResearchHotkey), `AppSettings` (`ResearchHotkey`, `ModeProviderDefaults[Research]` seeding),
  `App.xaml.cs`, `FlowItemViewModel`, `ScheduledJobNotificationSurface`,
  `ScheduledResearchProviderResolver` (→ `WindowMode.Assistant`).
- `SyncClientService`: drop `IResearchHistoryService` dep + research push/pull blocks (leave the
  request/response fields empty/ignored).
- `SyncMapper`: remove `ToSyncResearchSession`/`FromSyncResearchSession`.
- `SqliteContext`: drop ResearchSessions (create + migration), add ScheduledJobs.GrantedTools.
- Plugins (`BuiltInPluginDefaults`/`PluginService`/`BuiltInPluginHandler`): remove the
  `search_research_history`/`get_research_entry` tools (and their plugin wiring); keep the
  scheduled-research tools. *(Verify exact mapping during impl.)*
- `ScheduledJobBackgroundService`: replace `IResearchService`/`IResearchHistoryService` flow with
  `IBackgroundAssistantTurnRunner`; `MarkRunCompleteAsync(job.Id, result.ChatId)`; rewire notifications.
- `IScheduledJobNotificationSurface` + impl: `NotifySuccess(job, Guid chatId, string title)` opens
  the **Assistant** window + activates the chat by id (mirror `BackgroundChatNotificationSurface`);
  `NotifyFailure(job, reason)` toast-only.
- Resource strings (resx + de/fr): remove `Research_*`, `ResearchHistory_*`, `Nav_Research`,
  `Tray_OpenResearch`/`Tray_CloseResearch`; keep `Tool_ScheduledResearch_*`; add any new strings
  (granted-tools detail, success toast).

## Tests (gate: MTP runner, `--filter-not-namespace "Pia.Wpf.Tests.Integration.Providers"`, no new failures outside that namespace)

- Delete `ResearchHistoryToolHandlerTests`.
- Rewrite `ScheduledJobBackgroundServiceTests`: assert a chat is saved via `IAssistantChatService`
  (not a `ResearchHistoryEntry`); success/failure/no-provider paths.
- Update `ScheduledJobToolHandlerTests` / `ScheduledJobToolIntegrationTests` for the param swap
  (answerLength → grantedTools).
- Update `SyncMapperNewEntitiesTests` (research-session mapping removed; job GrantedTools added).
- Update `SyncMapperModeDefaultsMergeTests` (uses `WindowMode.Research` → switch to another mode;
  add a test that an orphan int key survives a load/merge).
- New `BackgroundAssistantTurnRunnerTests`: read allowed, ungranted write denied, granted write
  executed, chat persisted with the user+assistant pair.

## Open items to verify during implementation
- Plugin→tool ownership mapping for removing only the research-history tools.
- The exact "activate assistant chat by id from a toast" call used by `BackgroundChatNotificationSurface`.
- `ModeProviderDefaults` dictionary JSON converter tolerates an orphan `2` key after enum removal.

## Suggested execution order (each builds + tests green before next)
1. Build `IBackgroundAssistantTurnRunner` + tests (no removals yet; additive).
2. Schema: `ScheduledJob.GrantedTools` (model + DB + sync + tool params) + tests.
3. Rewire `ScheduledJobBackgroundService` + notifications to the runner/chats + tests.
4. Remove research storage (history/export/research-history tool/model/table/sync usage) + tests.
5. Remove the research **view** + `ResearchService` engine + `ResearchAnswerLength`.
6. Remove `WindowMode.Research` + all usages; strings cleanup.
7. Full build + test sweep; manual smoke (create job via assistant, force-fire, see chat).
</content>
</invoke>
