# Scheduled Research Jobs — Design

Date: 2026-05-02
Status: Approved (brainstorming complete)

## Summary

Lets the user schedule recurring research jobs from the Assistant — e.g. *"every weekday at 8:00 check the latest news about Tesla stock pricing"*. When the schedule fires, Pia runs a full research workflow (decompose → parallel sub-questions → synthesize) using the provider mapped to Research mode, persists the result to Research history, and surfaces a toast that opens the result. The Assistant gains a tool to search Research history (text + vector hybrid), so past briefings are conversationally retrievable.

## Goals

- Schedule recurring research-and-notify jobs through the Assistant chat.
- Run on the configured Research-mode provider, with the existing decompose/parallel/synthesize pipeline.
- Persist results to existing Research history with a back-reference to the originating job.
- Make all research entries (scheduled and ad-hoc) searchable from the Assistant via hybrid text + vector search.
- Reuse and share existing scheduling and embedding infrastructure (no duplication of recurrence math or cosine ranking).
- Auto-download the embedding model wherever embeddings are needed.

## Non-Goals (v1)

- Dedicated UI to manage scheduled jobs. CRUD is chat-driven via tools, mirroring how reminders work today.
- Windows Task Scheduler integration to launch Pia when closed. App-not-running behavior is handled via a 15-minute grace period and a missed-run dialog (see Section 4).
- File read/write tool for jobs that need persistence — separate brainstorm.
- Backfill of embeddings on existing Research history entries (opportunistic / future work).
- A `Weekday` recurrence type. *"Every weekday"* is represented as 5 separate Weekly jobs; the LLM creates them.

## Architecture

### Domain model

`Pia.Models.ScheduledJob` — separate from `Reminder`:

```csharp
public enum ScheduledJobKind { Research }
public enum ScheduledJobStatus { Active, Disabled, Failed }

public class ScheduledJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public required string Query { get; set; }
    public ScheduledJobKind Kind { get; set; } = ScheduledJobKind.Research;
    public ResearchAnswerLength AnswerLength { get; set; } = ResearchAnswerLength.Default;
    public Guid? ProviderId { get; set; }            // null = use the provider mapped to Research mode at fire time
    public RecurrenceType Recurrence { get; set; }   // reused from Reminder
    public TimeOnly TimeOfDay { get; set; }
    public DayOfWeek? DayOfWeek { get; set; }
    public int? DayOfMonth { get; set; }
    public int? Month { get; set; }
    public DateTime? SpecificDate { get; set; }
    public DateTime NextFireAt { get; set; }
    public ScheduledJobStatus Status { get; set; } = ScheduledJobStatus.Active;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? LastFiredAt { get; set; }
    public Guid? LastResultEntryId { get; set; }
    public int ConsecutiveFailures { get; set; }
}
```

`ResearchHistoryEntry` gains two columns:

- `ScheduledJobId` (`Guid?`) — links a result to its originating job; null for ad-hoc research.
- `Embedding` (`byte[]?`) — same blob shape as memory embeddings; generated from `Query + "\n\n" + SynthesizedResult`.

### Shared abstractions extracted from existing code

1. **`Pia.Services.Scheduling.IRecurrenceCalculator`** — pure-function `ComputeNextFireAt(...)`. The current `ReminderService.ComputeNextFireAt` family (private statics, lines 251–314) moves there. `ReminderService` and `ScheduledJobService` both call into it. No duplicated date math.

2. **`Pia.Services.Search.VectorSearchHelper`** —
   - `CosineSimilarity(float[] a, float[] b)`
   - `RankByCosine<T>(IEnumerable<T> items, Func<T, float[]?> getEmbedding, float[] query, int topK, float threshold)`
   
   The current `MemoryService.CosineSimilarity` (private static) and the in-memory ranking in `MemoryService.VectorSearchAsync` (lines 319–336) collapse into calls to this helper. Used by `MemoryService` and the new `ResearchHistoryService` semantic search.
   
   The text-vs-vector merge inside `MemoryService.HybridSearchAsync` is reviewed during implementation: if the merge logic has memory-specific scoring quirks (e.g. type-match boost), keep merge inline in each service and only share `CosineSimilarity` + `RankByCosine`. Otherwise extract a generic `MergeHybridResults` helper.

3. **`IEmbeddingService.EnsureAvailableAsync(IProgress<float>?, CancellationToken)`** — new method returning `true` if the model is already downloaded or downloads successfully, `false` if download fails. `GenerateEmbeddingAsync` invokes it internally as its first step. All current call sites that check `IsModelAvailable` and skip silently switch to awaiting `EnsureAvailableAsync`. The user is informed via in-app toast on the first download trigger.

### Services and scheduling

**`ScheduledJobService` / `IScheduledJobService`** — mirrors `ReminderService` shape (CRUD + `GetDueAsync`), with a separate SQLite table. Failure handling:

- `MarkRunCompleteAsync(Guid id, Guid resultEntryId)` → updates `LastFiredAt`, `LastResultEntryId`; resets `ConsecutiveFailures = 0`; recomputes `NextFireAt` via `IRecurrenceCalculator`.
- `MarkRunFailedAsync(Guid id, string reason)` → increments `ConsecutiveFailures`; recomputes `NextFireAt`. After **5** consecutive failures, sets `Status = Failed` so a broken provider doesn't keep firing forever.

**`ScheduledJobBackgroundService`** — `BackgroundService` parallel to `ReminderBackgroundService`. 30s `PeriodicTimer`. Each tick:

1. `GetDueJobsAsync()` → jobs with `NextFireAt <= now AND Status = Active`.
2. For each due job, compute `lateBy = now - scheduledFireAt` and apply the **grace policy** (Section 4).
3. For runs that proceed:
   - Resolve provider — pinned `ProviderId` if set, else the provider mapped to `WindowMode.Research` via `ISettingsService`. If neither resolves, mark failed with reason `NoProvider`.
   - `_researchService.ExecuteResearchAsync(session, provider, job.AnswerLength, ct)`.
   - On success: persist a `ResearchHistoryEntry` with `ScheduledJobId = job.Id` and embedding generated from `Query + SynthesizedResult` (using `EnsureAvailableAsync`; embedding is `null` if download fails). Then `MarkRunCompleteAsync`.
   - On failure (`LlmTimeoutException`, generic exception, etc.): persist a *failed* `ResearchHistoryEntry` (`Status = "Failed"`) so the toast still has a click target with diagnostic info. Then `MarkRunFailedAsync`.
4. Show toast — Windows + in-app — with a single **Open** button. Clicking it activates the main window and routes to the Research view focused on `LastResultEntryId`.

**Concurrency cap:** singleton `SemaphoreSlim(1)` inside the background service. Per-run duration is naturally bounded by `AiProvider.TimeoutSeconds` enforced in `AiClientService.StreamChatCompletionAsync`, so worst-case queue blocking equals research-provider timeout.

### AI tools

**`ScheduledJobToolHandler` / `IScheduledJobToolHandler`** — built-in plugin via `BuiltInPluginHandler.FromScheduledJobHandler`:

- `create_scheduled_research(name, query, recurrence, timeOfDay, dayOfWeek?, dayOfMonth?, month?, specificDate?, answerLength?, providerName?)`. Returns a `ScheduledJobToolCall` (pending action card, same UX as reminders). `providerName` is null = use Research-mode default at fire time; otherwise fuzzy-matched.
- `query_scheduled_research(filter)` — `active` (default) / `all`.
- `update_scheduled_research(id, ...optional fields...)`.
- `delete_scheduled_research(id)`.

`(Result, PendingAction)` pattern matches `ReminderToolHandler`. Create/update/delete go through the existing `ActionCardControl` confirmation flow; `query_*` returns directly. Tool descriptions include current date/time and NL examples.

**`ResearchHistoryToolHandler` / `IResearchHistoryToolHandler`** — built-in plugin, read-only:

- `search_research_history(query, scheduledJobId?, fromDate?, toDate?, topK?)` — runs `ResearchHistoryService.HybridSearchAsync(text, queryEmbedding, ...)`. Generates `queryEmbedding` via `IEmbeddingService.EnsureAvailableAsync` + `GenerateEmbeddingAsync`; falls back to text-only if embedding generation fails. Returns compact per-hit summaries: `Id`, `CreatedAt`, `Query`, first ~300 chars of `SynthesizedResult`, and `ScheduledJobId` if present.
- `get_research_entry(id)` — full result text for one entry, so the assistant can quote it after a search.

**Both handlers register through `PluginService`:**

- New cases in `PluginService.GetHandlerId`: `"scheduled-research"`, `"research-history"`.
- Two new factories: `BuiltInPluginHandler.FromScheduledJobHandler`, `BuiltInPluginHandler.FromResearchHistoryHandler`.
- `_pluginService.GetAllTools()` automatically picks them up — no `AssistantViewModel` change needed.

### Research history additions

`ResearchHistoryService` gains:

- `UpdateEmbeddingAsync(Guid id, byte[] embedding)` — for opportunistic backfill (no v1 caller; provided for future use).
- `VectorSearchAsync(float[] queryEmbedding, int topK, float threshold)` — uses `VectorSearchHelper.RankByCosine`.
- `HybridSearchAsync(string text, float[]? queryEmbedding, int topK)` — same merge shape as memory.

`AddEntryAsync` is updated to generate `Embedding` (best-effort: `EnsureAvailableAsync` + `GenerateEmbeddingAsync`; on failure logs and stores `null`).

## Data and Persistence

SQLite migrations applied through the existing migration mechanism in `Pia.Infrastructure.SqliteContext`:

```sql
CREATE TABLE IF NOT EXISTS ScheduledJobs (
    Id TEXT PRIMARY KEY,
    Name TEXT NOT NULL,
    Query TEXT NOT NULL,
    Kind TEXT NOT NULL DEFAULT 'Research',
    AnswerLength TEXT NOT NULL DEFAULT 'Default',
    ProviderId TEXT NULL,
    Recurrence TEXT NOT NULL,
    TimeOfDay TEXT NOT NULL,
    DayOfWeek INTEGER NULL,
    DayOfMonth INTEGER NULL,
    Month INTEGER NULL,
    SpecificDate TEXT NULL,
    NextFireAt TEXT NOT NULL,
    Status TEXT NOT NULL DEFAULT 'Active',
    CreatedAt TEXT NOT NULL,
    LastFiredAt TEXT NULL,
    LastResultEntryId TEXT NULL,
    ConsecutiveFailures INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS IX_ScheduledJobs_NextFireAt ON ScheduledJobs(NextFireAt, Status);

ALTER TABLE ResearchSessions ADD COLUMN ScheduledJobId TEXT NULL;
ALTER TABLE ResearchSessions ADD COLUMN Embedding BLOB NULL;
CREATE INDEX IF NOT EXISTS IX_ResearchSessions_ScheduledJobId ON ResearchSessions(ScheduledJobId);
```

`ALTER TABLE ... ADD COLUMN` statements are guarded by a "column-exists" check matching the existing migration pattern.

## Section 4 — Missed-run handling

When `ScheduledJobBackgroundService` finds a due job, compute `lateBy = now - scheduledFireAt`:

- **`lateBy ≤ 15 minutes`** → silent catch-up; run normally.
- **`lateBy > 15 minutes`** → defer. Mark a transient per-job `PendingMissedRunPrompt` (in-memory `HashSet<Guid>` on the service, cleared on user answer or session end). Dispatch to the UI thread to show a `MissedScheduledJobDialog` (a `Wpf.Ui.Controls.ContentDialog`):

  > *"Tesla stock briefing was scheduled for 08:00 but the app wasn't open. Run it now in the background?"*  
  > **\[Run now\]   \[Skip this run\]**

  - **Run now** → enqueue execution via the same singleton semaphore as on-time runs.
  - **Skip this run** → advance `NextFireAt` to the next future occurrence via `IRecurrenceCalculator`, persist, and do not ask again about this occurrence.

**Dedup rules:**

- While a job is in `PendingMissedRunPrompt`, the polling loop skips it (no duplicate dialogs, no nagging).
- Multiple jobs simultaneously missed-and-out-of-grace → dialogs are queued and shown one-per-job sequentially (matches "a new dialog overlay for each missed task").
- Multiple consecutive missed occurrences for the same recurring job → still **one** dialog per detection. Choosing "Run now" runs **once** with the latest schedule context, not N times.
- If the user closes the dialog without answering, the prompt is offered again on the next session (transient state, intentional).

## Localization

New keys mirroring `Tool_Reminder_*` / `Notification_Reminder*`, all three languages (en/de/fr):

- `Tool_ScheduledResearch_Desc_Create` / `_Update` / `_Delete`
- `Tool_ScheduledResearch_Detail_Name` / `_Query` / `_Recurrence` / `_Time` / `_Provider` / `_AnswerLength`
- `Tool_ScheduledResearch_Exec_Created` / `_Updated` / `_Deleted`
- `Tool_ResearchHistory_Search_Description` / `Tool_ResearchHistory_Get_Description`
- `Notification_ScheduledResearch` / `Notification_ScheduledResearchInApp` / `Notification_ScheduledResearchFailed`
- `Notification_OpenBriefing`
- `MissedRun_Dialog_Title` / `MissedRun_Dialog_Body` / `MissedRun_RunNow` / `MissedRun_Skip`
- `Embedding_Downloading_Toast`

## Logging and privacy

Per `Pia.Logging` rules: job names, queries, and result excerpts are user content and go through `_logger.SensitiveDebug(...)`. Only IDs and counts go to `LogInformation`. Embedding download URLs use `SafeUrl.Format` if logged. Same rule for both new tool handlers.

## DI registration

In `Bootstrapper.cs`:

```csharp
services.AddSingleton<IRecurrenceCalculator, RecurrenceCalculator>();
services.AddSingleton<VectorSearchHelper>();
services.AddSingleton<IScheduledJobService, ScheduledJobService>();
services.AddSingleton<IScheduledJobToolHandler, ScheduledJobToolHandler>();
services.AddSingleton<IResearchHistoryToolHandler, ResearchHistoryToolHandler>();
services.AddHostedService<ScheduledJobBackgroundService>();
```

`PluginService` constructor gains two parameters — `IScheduledJobToolHandler`, `IResearchHistoryToolHandler` — and `BuiltInPluginHandler` gains two factory methods.

## Toast activation routing

The toast carries `entryId` and `jobId` arguments. The activation handler activates the main window via `WindowManagerService` and navigates to the Research view with the entry pre-loaded. `ToastNotificationManagerCompat.OnActivated` is now subscribed by both `ReminderBackgroundService` and `ScheduledJobBackgroundService`; routing is by argument shape (presence of `reminderId` vs `jobId`/`entryId`). Flagged for a future `ToastActivationHub` extraction (out of scope).

## Testing

xUnit v3 + plain `Xunit.Assert`, matching project conventions:

- `RecurrenceCalculatorTests` — table-driven; covers all `RecurrenceType` cases plus edge cases (Feb 29 → Feb 28 in non-leap years, weekly when target day is today and time has passed, time-passed-today rolls to tomorrow). Port of the implicit invariants in `ReminderService.ComputeNextFireAt`.
- `VectorSearchHelperTests` — cosine similarity with known vectors; `RankByCosine` ordering and threshold filtering.
- `ScheduledJobServiceTests` — CRUD; `GetDueJobsAsync` filtering (status + time); `MarkRunCompleteAsync` recomputes `NextFireAt`; `MarkRunFailedAsync` increments and disables-after-5-failures behavior. In-memory SQLite per existing service-test pattern.
- `ScheduledJobToolHandlerTests` — schema parsing, action-card construction, error paths (invalid GUID, not found).
- `ScheduledJobBackgroundServiceTests` — fakes `IResearchService` + `IResearchHistoryService` + notification surface; verifies due jobs run research, persist with `ScheduledJobId`, call `MarkRunCompleteAsync`. Failure path: research throws → `MarkRunFailedAsync` called and a failed `ResearchHistoryEntry` persisted. Grace-period and missed-run-dialog paths.
- `ResearchHistoryServiceTests` — extended with `VectorSearchAsync` and `HybridSearchAsync` cases (text-only, vector-only, both, dedup).
- `ResearchHistoryToolHandlerTests` — search returns expected hits; embedding-unavailable falls back to text-only without throwing.
- `EmbeddingServiceTests` — `EnsureAvailableAsync` returns true when model is present; downloads when missing; returns false on download failure.
- `MemoryService` cosine refactor — existing tests must still pass after the helper extraction. Regression net for the refactor.

Integration test `ScheduledJobToolIntegrationTests` follows the existing `ReminderToolIntegrationTests` shape.

## File layout (new files)

```
src/Pia.Wpf/
  Models/
    ScheduledJob.cs
    ScheduledJobKind.cs
    ScheduledJobStatus.cs
  Services/
    Scheduling/
      IRecurrenceCalculator.cs
      RecurrenceCalculator.cs
    Search/
      VectorSearchHelper.cs
    ScheduledJobService.cs
    ScheduledJobBackgroundService.cs
    ScheduledJobToolHandler.cs
    ResearchHistoryToolHandler.cs
    Interfaces/
      IScheduledJobService.cs
      IScheduledJobToolHandler.cs
      IResearchHistoryToolHandler.cs
  Views/
    Dialogs/
      MissedScheduledJobDialog.xaml
      MissedScheduledJobDialog.xaml.cs

tests/Pia.Wpf.Tests/
  Unit/
    RecurrenceCalculatorTests.cs
    VectorSearchHelperTests.cs
    ScheduledJobServiceTests.cs
    ScheduledJobToolHandlerTests.cs
    ScheduledJobBackgroundServiceTests.cs
    ResearchHistoryToolHandlerTests.cs
    EmbeddingServiceTests.cs
  Integration/
    ScheduledJobToolIntegrationTests.cs
```

## Risks

1. **Provider de-pinning** — if a `ProviderId`-pinned provider is deleted, the run fails cleanly with reason `ProviderNotFound`, increments `ConsecutiveFailures`. After 5 consecutive failures the job is auto-disabled.
2. **Embedding model not downloaded** — auto-download via `EnsureAvailableAsync` is the first action of any embedding-needing flow. If download fails, semantic search degrades to text-only `LIKE` search and history entries persist without an embedding. Backfill is opportunistic and out of scope for v1.
3. **Memory `HybridSearchAsync` extraction nuance** — verified during implementation: if memory's merge logic has memory-specific scoring quirks, keep merge inline and share only `CosineSimilarity` + `RankByCosine`.
4. **Toast activation handler duplication** — `ReminderBackgroundService` and `ScheduledJobBackgroundService` both subscribe to `ToastNotificationManagerCompat.OnActivated` and route by argument shape. Flagged as a future `ToastActivationHub` extraction; not blocking for v1.
