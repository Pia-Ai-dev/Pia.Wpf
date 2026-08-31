# Approval-park defects — one buildable implementation plan

**Status:** executable · **Owner:** Marco Altmann · **Written:** 2026-08-31
**Origin:** [2026-08-31-approval-park-checklist.md](2026-08-31-approval-park-checklist.md), whose four
decision gates (Q1–Q4) are closed, and
[2026-08-31-approval-park-defects.md](2026-08-31-approval-park-defects.md), which root-causes the four
reported problems.

This document is the integrated build order for groups A–G. It is **self-contained**: an implementer who
never saw the design conversation can execute it cold. Step tracking stays in the checklist — tick its
boxes in the commit that lands each slice; this plan adds no second tracking surface.

Every claim below was verified against the code on branch `feature/agent_issues`. Where a source doc, a
checklist step or a design assumption disagrees with the code, the code wins and the disagreement is
recorded in *Resolved contradictions*.

---

## 0. The four naming decisions, settled

Three designers proposed three names for the one new store. These are binding; do not re-open them at
implementation time or the repo ends up with two tables.

| Thing | Name | Where |
|---|---|---|
| Table | `AgentToolExchanges` | `src/Pia.Wpf/Infrastructure/SqliteContext.cs`, `EnsureSchema` |
| Row record | `AgentToolExchangeRow` | `src/Pia.Wpf/Models/AgentToolExchange.cs`, namespace `Pia.Models` |
| Kind enum | `AgentToolExchangeKind { Unknown = 0, Call = 1, Result = 2, ParkedCall = 3, WithheldCall = 4 }` | same file |
| Result enum | `AgentToolExchangeResult { None = 0, Text = 1, Json = 2 }` | same file |
| Contract | `IAgentToolExchangeStore` | `src/Pia.Wpf/Services/Interfaces/IAgentToolExchangeStore.cs` |
| Per-turn handle | `AgentToolExchangeScope` | same file (mirrors `AgentTimelineScope` living in `IAgentTimelineService.cs`) |
| Implementation | `AgentToolExchangeStore` | `src/Pia.Wpf/Services/AgentToolExchangeStore.cs` |
| Both-directions codec | `internal static class AgentToolExchangeSerializer` | `src/Pia.Wpf/Services/AgentToolExchangeSerializer.cs` |
| Executor ctor param | `IAgentToolExchangeStore? exchangeStore = null` — **ONE** trailing param, not two | `HeadlessTurnExecutor` |
| DI line | `services.AddSingleton<IAgentToolExchangeStore, AgentToolExchangeStore>();` — **ONE** line beside `Bootstrapper.cs:567` | `Bootstrapper.cs` |

Dropped names: `IAgentToolExchangeService`, `IAgentToolCallStore`, `AgentToolExchange` (as the row record),
`ToolCallPayload` (folded into `AgentToolExchangeSerializer`), `AgentToolExchangeKind.Exchange`.

`AgentToolExchangeRow` must NOT be declared in the `Pia.Services` root namespace:
`NamingConventionTests.RecordTypes_MustNotLiveInTheServicesRootNamespace` fails on a non-nested reference
record there. `AgentToolExchangeSerializer` is a `static class` (`abstract sealed` in IL), so
`NamingConventionTests.ServiceClasses_MustFollowNamingConvention`'s `AreNotAbstract()` filter excludes it
from the suffix list — the same reason `ToolApprovalArguments` beside it passes. `…Store` and `…Service`
are both on that suffix list; `…Serializer` is not, which is the other reason the codec is static.

The DI registration is **mandatory, not stylistic**:
`DiRegistrationTests.AllServiceInterfaces_MustHaveRegisteredImplementation` enumerates every interface in
`Pia.Services.Interfaces` and fails on an unregistered one.

---

## 1. The finding that shapes the whole plan: two writers, not one

`TokenizingAiClientService.WrapToolHandler` (`src/Pia.Wpf/Services/TokenizingAiClientService.cs:295-327`)
hands the gate `handler(DetokenizeToolCallArguments(toolCall), ctx)` and **tokenizes the handler's result
before the tool loop sees it**. So for one and the same call:

- what the **gate** sees (and what group B records off `ToolApprovalStore`) is **detokenized** — the real
  user content, including real PII;
- what the **tool loop** appends to `workingMessages` (and what `AgentToolCarryover.Capture` snapshots, i.e.
  what group C persists) is the **tokenized placeholder form** — exactly what the model saw.

Consequences, all binding:

1. **Group B cannot replay from a group C row.** Replaying a tokenized argument would write `[Phone_9]`-style
   text into the user's file — the precise defect `WrapToolHandler`'s own comment says it exists to prevent.
   Gate Q3's "one table for both" therefore means **one table with two writers and a `Kind` discriminator**,
   not one writer the other queries.
2. **Gate Q3's phrasing "a parked call is just a call with no result" does not hold.** The Park arm at
   `BackgroundAssistantTurnRunner.cs:611-626` **returns a string**, so `AiClientService` wraps it in a
   `FunctionResultContent` and `Capture` snapshots a complete call+result pair. "No result" cannot be the
   discriminator. Hence `Kind`.
3. **Group C's re-seed must NOT detokenize.** C's rows are already what the model saw; re-seeding them
   verbatim reproduces the in-process path byte-for-byte. Do not "helpfully" detokenize on the read path.
4. **Group B's seed must tokenize.** B2 seeds `_messages` with a call/result pair that bypasses
   `TokenizingAiClientService` entirely. The seeded **arguments** are detokenized real content and would
   reach the provider raw on the next round. Both halves go through
   `ITokenMapService.TokenizeStructuredResult` when `_tokenizationEnabled`. Nothing in the suite guards
   this today — Slice 8 adds the test.
5. With tokenization **off**, B and C rows coincide in content. The discriminator is a column, never a
   content property, so both configurations read correctly.

---

## 2. The merged DDL, verbatim

Append this block to the **end of the one raw-string `command.CommandText`** in
`SqliteContext.EnsureSchema` (`src/Pia.Wpf/Infrastructure/SqliteContext.cs`), immediately after the
`IX_AgentTimelineEvents_CreatedAt` line and before the closing triple quote.

**No `MigrateSchema` entry.** This is a brand-new table and `CREATE TABLE IF NOT EXISTS` re-runs on every
open, so an existing database gets it at next launch — the same reasoning the `IX_AgentRuns_ParentRunId`
comment already states at `:501-504`.

```sql
CREATE TABLE IF NOT EXISTS AgentToolExchanges (
    Id              TEXT PRIMARY KEY,
    SchemaVersion   INTEGER NOT NULL DEFAULT 1,
    RunId           TEXT    NOT NULL,
    -- Not a foreign key, for AgentTimelineEvents.StepId's reason: ReplaceStepsAsync re-inserts every
    -- AgentSteps row on each replan, so a cascade would wipe the payload of steps that already ran.
    StepId          TEXT    NULL,
    -- Per RUN, over captured MESSAGES: rows sharing it rebuild into ONE ChatMessage, so two parallel
    -- calls in one assistant message come back in one message.
    MessageSeq      INTEGER NOT NULL,
    -- Per RUN, over CONTENT rows, and the only ordering. Allocated from MAX(Seq) inside the write
    -- transaction, so a run parked in one process and resumed in another continues its sequence.
    Seq             INTEGER NOT NULL,
    -- 1-based, the number every log line in the tool loop prints.
    Round           INTEGER NULL,
    Role            TEXT    NOT NULL,
    -- TWO WRITERS: 1 Call / 2 Result is what the MODEL saw (tokenized when tokenization is on), 3 ParkedCall
    -- / 4 WithheldCall is what the GATE saw (detokenized, replayable, can hold real PII). Append-only.
    Kind            INTEGER NOT NULL,
    -- FunctionCallContent.CallId verbatim, EMPTY STRING when the provider gave none (the same case
    -- AgentTimelineEvents.ToolCallId records as NULL); the replay synthesizes one when it rebuilds the call.
    CallId          TEXT    NOT NULL,
    ToolName        TEXT    NULL,
    PluginId        TEXT    NULL,
    -- PAYLOAD-BEARING, the inverse of AgentTimelineEvents' metadata-only contract beside it. Local-only,
    -- purged with the run, never logged outside SensitiveDebug, never copied into SyncAssistantChatMessage.
    ArgumentsJson   TEXT    NULL,
    -- The args exceeded MaxRowChars and were dropped: context only, never replayable. Kind 1 only — a
    -- Kind 3/4 row is refused whole rather than stubbed, because half a payload is unreplayable.
    ArgsOmitted     INTEGER NOT NULL DEFAULT 0,
    -- Today's 120/400-capped display line, for the approval surfaces. Kind 3/4 only.
    DisplayArgs     TEXT    NULL,
    ResultKind      INTEGER NOT NULL DEFAULT 0,
    ResultText      TEXT    NULL,
    -- ArgumentsJson.Length + ResultText.Length, so the per-run byte cap is SUM(Chars) rather than a
    -- length() scan over 512 K blobs.
    Chars           INTEGER NOT NULL DEFAULT 0,
    -- The AssistantChatMessages row this group precedes on the re-seed. Not a foreign key: every chat save
    -- re-INSERTs the message rows in one transaction, which an FK would cascade through or reject.
    AnchorMessageId TEXT    NULL,
    CreatedAt       TEXT    NOT NULL,
    -- Stamped BEFORE the replay executes, so at-most-once survives a crash between mark and call.
    ReplayedAt      TEXT    NULL,
    -- A later park recording the same tool made this row's arguments stale.
    SupersededAt    TEXT    NULL,
    FOREIGN KEY (RunId) REFERENCES AgentRuns(Id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS IX_AgentToolExchanges_RunId     ON AgentToolExchanges(RunId, Seq);
CREATE INDEX IF NOT EXISTS IX_AgentToolExchanges_CreatedAt ON AgentToolExchanges(CreatedAt);
```

The column list above is asserted exactly by
`tests/Pia.Wpf.Tests/Infrastructure/SqliteContextTests.AgentToolExchanges_HasExactlyTheseColumns`, modelled
on `AgentTimelineEvents_HasExactlyTheMetadataColumns` (`:100-121`). Its comment must say out loud that this
table's contract is the **inverse** of the timeline's, so nobody "aligns" the two.

Every DDL comment above is held to CLAUDE.md's two-wrapped-line ceiling. The adjacent
`AgentTimelineEvents` block is longer because it is grandfathered; this table is not, so do not grow these.

### Three column rules that are easy to get wrong

1. **`CallId` is verbatim, and only the REPLAY synthesizes.** `AgentTimelineScope.SanitizeCallId`
   (`IAgentTimelineService.cs:137-142`) returns null for a blank id, which is why
   `AgentTimelineEvents.ToolCallId` is nullable — so a provider CAN omit it. Rules: every writer stores what
   the message carried, EMPTY STRING when that is blank, so a Kind 1 row and its Kind 2 row still pair
   blank-to-blank exactly as the in-memory pair did and the re-seed reproduces what the model saw. Slice 8
   rebuilds a parked call as
   `new FunctionCallContent(row.CallId is { Length: > 0 } id ? id : Guid.NewGuid().ToString("N"), row.ToolName, ...)`
   and pairs its seeded `FunctionResultContent` on **that same value**, so two parked calls with blank ids can
   never produce an unpairable seed. Do NOT synthesize at record time: the row would then disagree with the
   audit row for the same call.
2. **`Seq` and `MessageSeq` are allocated from `MAX` over ALL kinds, by both writers.** Only the per-run
   **cap** is Kind-scoped. One aggregate query with conditional sums does both:
   `SELECT COALESCE(MAX(Seq),0), COALESCE(MAX(MessageSeq),0), COUNT(CASE WHEN Kind IN (1,2) THEN 1 END), COALESCE(SUM(CASE WHEN Kind IN (1,2) THEN Chars END),0) FROM AgentToolExchanges WHERE RunId=@r`.
   Allocating the two sequences from a Kind-scoped maximum would let a Kind 3 row and a Kind 1 row share a
   `MessageSeq`, which is harmless only by accident of `ReadCarriedAsync`'s filter. One column, one meaning.
3. **`ArgsOmitted` is a Kind 1 concept only.** A Kind 3/4 row over the cap is dropped whole by
   `ToolApprovalStore.Record` before it ever reaches the store, because a stubbed replay row would be a row
   the grant cannot honour.

### Why the cascade actually fires

`PRAGMA foreign_keys` is never issued anywhere in `src/`; Microsoft.Data.Sqlite enables foreign keys per
connection by default, and `AssistantChatService.DeleteUnderGateAsync` (`:616-638`) already relies on it —
its only delete is `DELETE FROM AssistantChats` under the comment *"ON DELETE CASCADE removes messages."*
SQLite cascades recursively, so `AssistantChats → AgentRuns → AgentToolExchanges` purges in one statement.
`AgentToolExchangeStoreTests.DeletingTheChat_PurgesTheExchangeRows` pins it, because the whole of Q1 rests
on that property.

### The size cap — the one that bites

Two caps, and which rows each governs is load-bearing:

| Cap | Value | Governs | Behaviour past it |
|---|---|---|---|
| `AgentToolExchangeStore.MaxRowsPerRun` | 500 | **Kind 1/2 only** | The whole round batch is rolled back, so no call is ever stored without its result. One `Information` line per run, from an in-memory `HashSet<Guid> _capNoted`. |
| `AgentToolExchangeStore.MaxCharsPerRun` | 4 000 000 | **Kind 1/2 only** | Same all-or-nothing rollback. |
| `AgentToolExchangeSerializer.MaxRowChars` | 1 048 576 | **Kind 1 only** | The call row is kept with `ArgumentsJson = NULL` and `ArgsOmitted = 1`, so the pair is never orphaned and the model still sees that the call was made. |
| `ToolApprovalStore.MaxRecordedCalls` | 8 | **Kind 3/4** (in memory, before the write) | The record is **dropped whole** — a half payload is unreplayable — and `DroppedRecords` increments. |
| `ToolApprovalStore.MaxRecordedArgumentChars` | 1 048 576 | **Kind 3/4** (in memory) | Same drop-whole rule. |

**A Kind 3/4 row must never be silently dropped by the per-run cap.** It is the row the human's Continue
press replays; dropping it disables an approval the human just gave, with nothing failing. That is why the
per-run cap is scoped to Kind 1/2 in the `RecordAsync` aggregate query
(`WHERE RunId=@r AND Kind IN (1,2)`), and why B's bound lives in `ToolApprovalStore` instead.

### The purge rule — four mechanisms, union

1. **FK cascade** on chat delete / retention eviction (see above). This is the only one Q1 names, and Q1's
   wording *"purged with the run"* over-promises: there is **no `DELETE FROM AgentRuns` anywhere in
   `src/`**, so the cascade fires when the run's **chat** goes, not when the run finishes.
2. **`PurgeRunAsync(runId)` at `HeadlessTurnExecutor.EndRunAsync`**, after the terminal `PersistChatAsync`.
   `SafeEndRun` is called on every terminal path in `AgentRunOrchestrator` and deliberately never on a park
   or a pause, and a terminal run has no reader (`TryBeginResumeAsync` claims `WaitingForInput` only,
   `TryResumeFromPauseAsync` claims `Paused` only, and there is no run-level Retry). This is what closes
   Q1's gap and is the plan's strongest privacy statement: **the detokenized Kind 3/4 rows do not outlive
   the run.**
3. **`PruneAsync(cutoff)`** from `AssistantChatRetentionService`, one statement:
   `DELETE FROM AgentToolExchanges WHERE CreatedAt < @Cutoff OR RunId IN (SELECT Id FROM AgentRuns WHERE
   State IN (5,6,7))`. The terminal set is **explicit and never a range** — `WaitingForChildren = 8` sits
   above the terminal band (`AgentEnums.cs:30-50`). The second clause closes the leak when a process dies
   before `EndRunAsync`.
4. **`DeleteReplayableAsync(runId, toolName)`** on the decline path (Slice 8): the human said no to up to
   512 K chars of their own content, and it should not outlive the decision. Scoped to the **declined tool
   only** — never `DeleteForRunAsync`, which would destroy another tool's surviving withheld call and group
   C's context seed, i.e. break the very resume the decline is meant to continue.

### The payload-vs-cleared-result question, resolved

**Results are persisted in the form `AgentToolCarryover.Capture` produced them — never the
`ClearOldResults` placeholder form — and that is structural, not a policy choice.**

The proof, from the code:

- `ClearOldResults` runs on `exchangeMessages`, a **copy** built at `HeadlessTurnExecutor.cs:459-464`.
  `_messages` deliberately keeps the full bodies; the comment at `:480-481` says so
  (*"ClearOldResults builds, so `_messages` keeps the full carried results"*).
- The store is hooked at `BackgroundAssistantTurnRunner.RunExchangeAsync`'s `case ToolRoundExchange` arm,
  whose payload is `AgentToolCarryover.Capture(workingMessages.Skip(appendedFrom))` — **only a round's new
  messages**, which hold real results. A placeholder is therefore *type-incapable* of reaching the table.

Persisting the cleared form would make a resume strictly **worse** than an in-process step. In the reported
run the park happened in round 3 of step 1 after two successful `read_file` calls, both well inside
`KeptResults = 8`, so a cleared store would hand the resumed step
`[result cleared; call read_file on … again]` for the very Excel data it had just read — exactly the state
that made the model ask the user instead.

The inverse worry is also answered: re-seeding **full** results does not defeat clearing, because
`ClearOldResults` + `AgentContextCompactor` still run on the re-seeded list on every step inside
`RunExchangeStepAsync`, unchanged, so the post-resume context budget is identical to the pre-park one.

**One precision that must not be lost.** `Capture` already caps **string** results at
`MaxCarriedResultChars = 4000` (`AgentToolCarryover.cs:42-45`); a non-string (object) result falls through
the `case FunctionCallContent or FunctionResultContent` arm **uncapped**. So "persisted in full" means
*whatever `Capture` produced*, not *whatever the tool returned*. Without that sentence a future reader
widens the store to hold pre-`Capture` bodies. `MaxRowChars`'s oversize-downgrade arm can therefore only
fire on a non-string result — and on `ArgumentsJson`, which `Capture` never caps at all (a `write_file`
`content` argument reaches the table at its full 512 K chars, which is the property that lets one table
serve group B's replay).

`AgentToolExchangeStoreTests.NoPersistedResultBody_IsEverAClearedPlaceholder` pins the invariant.

### The store contract

`src/Pia.Wpf/Services/Interfaces/IAgentToolExchangeStore.cs` — one interface, three consumer groups. Every
method is **awaited** (unlike `AgentTimelineService.Emit`, which is fire-and-forget): there is no UI-thread
caller here, a lost payload row is a lost replay, and `TryMarkReplayedAsync`'s rows-affected result is what
gates execution. Every method is failure-isolated — a fault logs a warning carrying run id and counts only,
and returns, never propagating into a step.

```csharp
public interface IAgentToolExchangeStore
{
    // Group C — the durable twin of the model context.
    Task RecordAsync(Guid runId, Guid? stepId, int round, IReadOnlyList<ChatMessage> messages, CancellationToken ct = default);
    Task<int> SealStepAsync(Guid runId, Guid? stepId, Guid anchorMessageId, CancellationToken ct = default);
    Task<IReadOnlyList<AgentToolExchangeRow>> ReadCarriedAsync(Guid runId, CancellationToken ct = default);
    Task<int> PurgeRunAsync(Guid runId, CancellationToken ct = default);
    Task<int> PruneAsync(DateTime cutoff, CancellationToken ct = default);

    // Group B — the replayable park.
    Task AppendParkedAsync(IReadOnlyList<AgentToolExchangeRow> rows, CancellationToken ct = default);
    Task<IReadOnlyList<AgentToolExchangeRow>> GetReplayableAsync(Guid runId, string toolName, CancellationToken ct = default);
    Task<int> SupersedeUnreplayedAsync(Guid runId, IReadOnlyCollection<string> toolNames, CancellationToken ct = default);
    Task<bool> TryMarkReplayedAsync(Guid id, DateTime replayedAt, CancellationToken ct = default);
    Task SetResultAsync(Guid id, string? resultText, CancellationToken ct = default);
    Task<int> DeleteReplayableAsync(Guid runId, string toolName, CancellationToken ct = default);

    // Group G — the approval surface.
    Task<AgentToolExchangeRow?> GetParkedCallAsync(Guid runId, string toolName, CancellationToken ct = default);
}

public sealed class AgentToolExchangeScope
{
    public AgentToolExchangeScope(IAgentToolExchangeStore store, Guid runId, Guid? stepId);
    public Guid RunId { get; }
    public Guid? StepId { get; }
    public Task RecordAsync(int round, IReadOnlyList<ChatMessage> messages);
    public Task SealAsync(Guid anchorMessageId);
}
```

Query shapes, so two implementers cannot write two predicates:

- `ReadCarriedAsync`: `WHERE RunId=@r AND Kind IN (1,2) ORDER BY Seq`. The `Kind` filter is load-bearing —
  re-seeding a `ParkedCall` row would send a second `FunctionCallContent` under a `CallId` the `Call` row
  already used.
- `SealStepAsync`: `UPDATE … SET AnchorMessageId=@m WHERE RunId=@r AND AnchorMessageId IS NULL AND (StepId=@s OR (@s IS NULL AND StepId IS NULL))`.
- `GetReplayableAsync`: `WHERE RunId=@r AND Kind IN (3,4) AND ToolName=@t COLLATE NOCASE AND ReplayedAt IS NULL AND SupersededAt IS NULL ORDER BY Seq`.
- `SupersedeUnreplayedAsync`: `UPDATE … SET SupersededAt=@now WHERE RunId=@r AND ToolName IN (…) COLLATE NOCASE AND Kind IN (3,4) AND ReplayedAt IS NULL AND SupersededAt IS NULL`. Run **once per persist pass**, not per row, or four parked calls of one tool in a single round supersede each other.
- `TryMarkReplayedAsync`: `UPDATE … SET ReplayedAt=@t WHERE Id=@id AND ReplayedAt IS NULL`, returning `rowsAffected == 1`. This is the structural half of Q2's at-most-once.
- `GetParkedCallAsync`: the newest `Kind=3` row for that tool with `ReplayedAt IS NULL AND SupersededAt IS NULL`, `ORDER BY Seq DESC LIMIT 1`.

`COLLATE NOCASE` matches `grantedWrites`' `StringComparer.OrdinalIgnoreCase`. SQLite's NOCASE is ASCII-only;
tool names are ASCII identifiers (`AgentTimelineScope.IsToolIdChar`), so the two agree.

Infrastructure: its own dedicated `SqliteConnection` with `PRAGMA busy_timeout=3000`, opened lazily, one
`lock (_gate)` around every use, `context.GetConnection()` forced in the ctor so `EnsureSchema` has created
the table first, `IDisposable`. Copied from `AgentRunService` / `AgentTimelineService`. **Singleton** in DI,
for `AgentRunService`'s reason: it owns a connection.

---

## 3. Build order — sixteen slices

The checklist's suggested order is honoured: **A -> F -> D2 -> C -> B -> D1/D3 -> G -> E**. Two
deviations, each with a stated reason:

- **C, B and E are split.** As designed, group C alone was 8 production files plus ~14 store tests plus
  the executor rewrite plus a test-harness change; B was comparable. Neither fits one agent. The sizing
  rule for this plan is **<= ~6 production files per slice**, and every slice must be independently
  buildable and verifiable.
- **G3 moves up beside D2** (Slice 3). Its declared `Deps: G1` does not hold: the Flow card's body source
  is unchanged by this plan, so G3 is a pure XAML fix with two tests and nothing else touches
  `FlowView.xaml`. Landing it early buys a user-visible fix ahead of the gated work.

Two dependencies the checklist states loosely are **hard**:

- **A must precede C.** C's rows for the parking round exist only because A leaves the `ToolRoundExchange`
  yield in place on the stop path. If A short-circuits before that yield, C records nothing for the one
  step it exists for and Slice 6 fails.
- **D3 must precede E1.** D3 creates the shared `AgentStepInstruction.Compose`; E1 widens it. The other
  order means extracting the same two duplicated builders twice.

**Verification per slice** (from the checklist, non-negotiable): `dotnet build -t:Rebuild -v:n` in
**Debug and Release**, `0 Warning(s)` / `0 Error(s)` read off MSBuild's `N Warning(s)` summary line,
before the next slice starts. **Verification per group** (A, B, C, D, E, F, G): one full unfiltered
`dotnet test` at `failed: 0`. The suite runs ~11 min and class filters do not narrow it, so do not run
one per slice.

---

### Slice 1 — A1 + A2 + A3: stop the tool loop when a run parks

> **OWNER DECISION, 2026-08-31: FOUR stop arms, not three.** The `request_user_input` pre-route
> (`BackgroundAssistantTurnRunner.cs:432-437`) raises the signal as well. This changes the step list below:
> add the fourth arm, treat the withheld-because-asking arm as same-round-only, rework
> `MidPlanAskTests.Drive`'s round-2 double into a same-round sequence, and invert
> `AskAlone_DoesNotRaiseTheLoopStopSignal` into `AskAlone_RaisesTheLoopStopSignal`. Full reasoning in
> section 5.

Closes reported issue 1 (the "30-60 s" dialog delay, which is actually unbounded: rounds the model still
spends x provider round-trip, capped only by `MaxToolRoundsPerStep`, default 10).

**Files**

- `src/Pia.Wpf/Services/Interfaces/IAiClientService.cs`
- `src/Pia.Wpf/Services/AiClientService.cs`
- `src/Pia.Wpf/Services/BackgroundAssistantTurnRunner.cs`
- `src/Pia.Wpf/Services/Interfaces/IAgentTimelineService.cs` (doc comment only)

**Steps**

**1. `IAiClientService.cs`** — insert immediately above the existing `ToolDispatchContext` doc block:

```csharp
public sealed class ToolLoopStopSignal
{
    public bool IsStopRequested { get; private set; }
    public void RequestStop() => IsStopRequested = true;
}
```

Then change the record to
`public readonly record struct ToolDispatchContext(int Round, ToolLoopStopSignal? Stop = null);`.

A **reference type**, because the context is a by-value `readonly record struct`: a flag set on the
handler's copy can never reach the loop, and `readonly` forbids a setter outright. One short `<summary>`
on the class saying exactly that, and one short `<param name="Stop">` line. No `volatile` and no lock —
the per-call `foreach` is sequential and the loop reads the flag only after that dispatch's awaits
completed, so the happens-before is already there.

The new parameter **must stay trailing and optional**: all 67 `new ToolDispatchContext(1)` sites in
`tests/` are positional single-arg.

Also delete the now-false three words in the existing doc's first sentence: "A record STRUCT with one
field rather than a bare int parameter" becomes "A record STRUCT rather than a bare int parameter".

**2. `AiClientService.cs`** — change `DispatchToolCallsAsync` (`:572`) to return `Task<bool>`; parameter
list unchanged. Hoist the context construction **and its existing four-line comment at `:621-624`** out of
the per-call `foreach` to immediately above `foreach (var toolCall in toolCalls)` (`:598`):

```csharp
var stop = new ToolLoopStopSignal();
var dispatch = new ToolDispatchContext(round + 1, stop);
```

`:625` becomes `var result = await toolHandler(toolCall, dispatch);`. Add `return stop.IsStopRequested;`
after the `Round {Round} complete` `LogDebug` at `:636-638`.

Returning the decision (rather than hoisting the context into the round loop) is the chosen option: the
signal's lifetime is then exactly one dispatch, the round loop gains no mutable local, and the comment at
`:621` that documents the *one* construction site stays true where it is.

**LOAD-BEARING: do NOT `break` the per-call `foreach` on a stop.** The round's remaining calls are still
dispatched and still answered (by the withhold arm), so every `FunctionCallContent` keeps its matching
`FunctionResultContent`. A `break` leaves an unpaired call in the slice `AgentToolCarryover.Capture` hands
to Slice 5, which would then persist it and re-seed it into a provider request many providers reject
outright. It also silently kills the withheld-because-parked arm and every `secondToolName` fact in
`UnattendedApprovalParkTests`.

**3. `AiClientService.cs`, the round loop (`:392-406`)** — `:398` becomes
`var stopRequested = await DispatchToolCallsAsync(...)`. Keep the `ToolRoundExchange` yield at `:402-403`
**exactly where it is**. Then, before the `continue;` at `:405`:

```csharp
if (stopRequested)
{
    _logger.LogInformation("Round {Round}: a tool handler stopped the loop; finishing the exchange", round + 1);
    yield return BuildFinishedItem(provider, hasUsage, aggregatedInput, aggregatedOutput, protectedRoute, lastModelId);
    yield break;
}
```

`Round` is a scalar, so a plain `LogInformation` is privacy-clean. The `yield break` deliberately skips
`RunToolRoundWrapUpAsync`: the wrap-up exists for round exhaustion, and spending a tool-free round on a
parked step re-introduces the round-trip this slice removes. `Finished` **must** be yielded, not skipped,
for two traced reasons — `TokenizingAiClientService` flushes its detokenize buffers on the `Finished`
branch (`:199-213`) rather than through the post-enumeration safety net, and `HeadlessTurnExecutor`'s park
arm carries `exchange.Usage` out (`:597`) for the orchestrator to bill run-level.

**4. `BackgroundAssistantTurnRunner.cs`** — three one-line additions, each the **first** statement of its
block so a future early return inside the arm cannot skip it. No signature changes: `dispatch` is already
a parameter of both `HandleToolCallAsync` (`:416`) and `DispatchGateVerdictAsync` (`:557`).

- `:474`, inside `if (approvals?.PendingToolName is { } parkedFor)`, above the `approvals.Park(...)` call
  (withheld-because-parked).
- `:489`, inside `if (userInput?.Question is not null)` (withheld-because-asking).
- `:611`, as the first statement of `case ToolGateOutcome.Park:`, **above** `var parked = ...` — the run
  stops whether or not this call is the first park, so the flag must not be conditional on `parked`.

Each line is exactly `dispatch.Stop?.RequestStop();`. The null-conditional keeps all 67 existing doubles a
no-op and keeps the voice path safe (`AssistantViewModel.cs:2141` documents its `ctx` as deliberately
unused). **Never** `Stop!.RequestStop()` — that is a `NullReferenceException` in every double. No new
comment on any of the three lines: the surrounding comments already say the run is stopping. The advisory
return strings stay verbatim, per the defect doc.

One EXISTING comment must be trimmed. `:468-473` reads "AiClientService walks the round's REMAINING calls
and then continues to the next round - so without this guard a granted, side-effecting call made after the
run decided to park still executed." The **second clause is false after step 3**. Delete it; keep the
first, which is still the whole justification for the withhold arm (same-round calls are still
dispatched). Leaving the stale clause hands the next reader a false premise for deleting the arm.

**5. `IAgentTimelineService.cs:60`** embeds the dispatch expression verbatim in a doc comment. Minimal
true edit: `new ToolDispatchContext(round + 1)` becomes `dispatch` inside that `<c>` element.

**Tests**

- `tests/Pia.Wpf.Tests/Services/AiClientServiceToolLoopArmTests.cs` ->
  **`ToolHandler_RequestsStop_FinishesTheExchangeAfterOneRound`**. Real round loop with a fake
  `IChatClient` that returns a tool-call round on **every** round, so an unstopped loop runs to
  `MaxToolRounds` and spends a wrap-up. One harness shim: add
  `public Action<ToolDispatchContext>? OnDispatch { get; init; }` and invoke it in `RunAsync`'s
  `toolHandler` lambda, which today discards `ctx` as `(call, _)`. Set
  `OnDispatch = ctx => ctx.Stop?.RequestStop()`. Asserts: exactly one `GetStreamingResponseAsync`
  (**not awaited** — it returns `IAsyncEnumerable`); zero `GetResponseAsync` (**awaited** — it returns
  `Task`), i.e. no tool-free wrap-up round; one dispatched tool; exactly one `ToolRoundExchange` at
  `Round == 1` whose `Messages` carry **both** a `FunctionCallContent` and its matching
  `FunctionResultContent` (the unpaired-call guard); exactly one `Finished` with
  `ToolRoundsExhausted == false`. The existing `ToolRoundsExhausted_SpendOneToolFreeWrapUpRound` is the
  unstopped control.
- `tests/Pia.Wpf.Tests/Services/UnattendedApprovalParkTests.cs` ->
  **`ParkingACall_RaisesTheLoopStopSignal_AndStillReachesWaitingForInput`**. Upgrade `DriveWithToolCall`
  (`:1005-1030`) to create ONE `ToolLoopStopSignal` and pass `new ToolDispatchContext(1, stop)` to **both**
  handler invocations, recording `stop.IsStopRequested` into `ToolProbe` (new
  `public bool StopAfterFirstCall { get; set; }` at `:872`) after the first call. The second, same-round
  call must stay **unconditional** so every existing `secondToolName` fact (first-wins `PendingToolName`,
  `ParkedCalls`, `PendingToolArguments` accumulation) stays green. Asserts the flag,
  `run.State == WaitingForInput`, and `PauseMember(run, "tool") == "write_file"`. This is the only place a
  production arm is observed raising a real signal.
- `tests/Pia.Wpf.Tests/Services/MidPlanAskTests.cs` ->
  **`PendingWriteAfterAnAsk_RaisesTheLoopStopSignal`** and
  **`AskAlone_DoesNotRaiseTheLoopStopSignal`**. Share one signal across `Drive`'s four contexts
  (`:513-552`, rounds 1/1/2/3). Fact one: with `probe.FollowUpTool` set (a pending write in round 2, the
  withheld-because-asking arm) the signal is raised. Fact two: with the ask alone it is **not** — this pins
  the deliberate three-arm scope as a fact rather than an omission, and keeps `Drive`'s round-2
  `FollowUpTool` double a sequence that can still occur in production.
- `tests/Pia.Wpf.Tests/Services/TokenizingAiClientServiceTests.cs` ->
  **`RelaysTheStopSignalToTheInnerHandler`**. Sibling of the existing
  `RelaysTheDispatchContextToTheInnerHandler` (`:205-260`), same wiring. Invoke the captured wrapped
  handler with `new ToolDispatchContext(7, signal)`, assert `Assert.Same(signal, seen!.Value.Stop)`, then
  that calling `RequestStop()` on what the inner handler saw is observable as `signal.IsStopRequested` on
  the caller's instance. **Highest-value cheap test in the slice**: if the decorator dropped `Stop`,
  exactly the tokenization-enabled installs would keep the defect and no other test would see it.

**Why A3 is four test files, and why there is no interactive-park test.** Every run-level suite
substitutes `IAiClientService` wholesale (`UnattendedApprovalParkTests.Build:929-937` hands back a
hand-written `IAsyncEnumerable`, so the real round loop never executes), and the only real-loop harness
(`AiClientServiceToolLoopArmTests.Harness:133-203` — real `AiClientService` plus a fake `IChatClient`) has
no run, no orchestrator and no SQLite. Nothing in `tests/` wires a real `AiClientService` into
`HeadlessTurnExecutor`; building that is M-sized against an XS step. Interactive parity is provable **by
construction, not by a test**: `ToolGateOutcome.Park` is unreachable interactively
(`ChatSession.cs:1336-1349`, with `IsTopLevelUserRun: false` hardcoded at `:1338` under the comment "Only
the park reads it, and this surface never parks"), `ToolApprovalStore` is constructed in exactly one place
(`HeadlessTurnExecutor.RunExchangeStepAsync:424`), and this slice touches no file under
`src/Pia.Wpf/ViewModels/`. A test that faked an interactive park would pin a state that cannot occur.

---

### Slice 2 — F1 + F2 + F3: the approval counter

Closes reported issue 4 ("2 Freigabe(n) ausstehend" on a completed run). No schema, no store, no XAML
change; the timeline stays INSERT-only.

**Files**

- `src/Pia.Wpf/ViewModels/RunProgressViewModel.cs`
- `src/Pia.Wpf/Resources/Strings/ViewStrings.resx`
- `src/Pia.Wpf/Resources/Strings/ViewStrings.de.resx`
- `src/Pia.Wpf/Resources/Strings/ViewStrings.fr.resx`

**The ordering problem this slice actually solves.** `ApplyDecisionSummary` runs inside
`ApplyTimelineAsync`'s `_uiContext.Post` body (`:1734-1782`, call at `:1773`), while
`IsToolApprovalPause` / `ApprovalToolName` / `ApprovalToolArguments` are set in `Project(AgentRun, ...)`
at `:1003-1009`, reached from `RefreshAsync` (`:779`). The two paths are **independent**:
`OnTimelineAppended` (`:611-615`) fires from `RunTimelineWatcher` on a pool thread and drives a timeline
reload with no run read at all, while `RunChanged` drives `RefreshAsync` and, for a live run whose
`_liveTracePrimed` is already true (`:800-805`), reads no trace. The reported run proves the gap is real:
the gate emits the `ParkedForApproval` row at 14:07:16 and the run reaches `WaitingForInput` at 14:07:57.
So the trace load that first sees the park row runs with `IsToolApprovalPause == false`, and the projection
that sets it true never re-reads the trace. **A pill derived only inside `ApplyDecisionSummary` would never
appear on a parked run.** Both facts must meet, and neither path can be assumed to be second.

**Steps, part 1 — the derived fact**

1. Two UI-thread-only fields after `private bool _liveTracePrimed;` (`:609`):
   `private IReadOnlyList<AgentTimelineEvent> _timelineEvents = [];` and `private Guid? _liveParkRowId;`.
   No lock: every writer and reader is inside a `_uiContext.Post` body or inside `Project`, which is only
   called from one — the same contract `Timeline` and `DecisionPills` already live under.
2. `private static Guid? LiveParkRowId(IReadOnlyList<AgentTimelineEvent> events, string? approvalTool)`
   next to `DecisionLabelKey` (~`:1885`): null when `approvalTool` is null or empty, else the `Id` of the
   highest-`Seq` row with `Decision == ToolGateDecision.ParkedForApproval` whose `ToolName` equals
   `approvalTool` (`OrdinalIgnoreCase`). A `foreach` over `Seq`, not list position — `_timelineEvents` is
   the reversed, newest-first list.
3. Extract the row-building half of `ApplyTimelineAsync` into `private void RenderTimelineRows()`:
   `Timeline.Clear()`, then the existing exceptions/routine partition and the two `Add` loops verbatim,
   reading `_timelineEvents`, with the partition predicate changed from `Severity(e.Decision)` to
   `SeverityForKey(RowLabelKey(e))`. Move the existing EXCEPTIONS-FIRST comment onto it.
4. `ApplyTimelineAsync`'s posted body: keep `IsTimelineTruncated`, `TimelineNote`,
   `HasTimelineReadError` and the `TraceTruncated` note block unchanged; **drop** the `Timeline.Clear()`
   and `DecisionPills.Clear()` lines (they move into the two callees); assign `_timelineEvents`, then
   `_liveParkRowId = LiveParkRowId(_timelineEvents, IsToolApprovalPause ? ApprovalToolName : null);`, then
   `RenderTimelineRows(); ApplyDecisionSummary(_timelineEvents);`, then the unchanged `HasNoTimeline` line.
5. `private void RefreshApprovalDerivation()` directly after `ApplyDecisionSummary`: recompute
   `LiveParkRowId(...)`; **return immediately if it equals `_liveParkRowId`**; otherwise store it and call
   `RenderTimelineRows(); ApplyDecisionSummary(_timelineEvents);`. The identity guard keeps roughly 500
   `RunChanged` emits per run off the row rebuild — the answer changes at most twice per park. It sits
   BEFORE any collection mutation.
6. Call `RefreshApprovalDerivation();` in `Project(AgentRun, ...)` on the line immediately after
   `ApprovalToolArguments = ...` (`:1009`). It must be after `SyncSteps(run.Plan)` (`:1002`), because
   `Project(AgentTimelineEvent, ...)` reads `Steps` for `StepLabel`, and after both approval assignments.
   Deliberately NOT an `OnIsToolApprovalPauseChanged` partial hook: `ApprovalToolName` is assigned on the
   next line, so the generated hook would run against a stale name.

**Steps, part 2 — the relabel through one mapping**

7. `ApplyDecisionSummary` now owns `DecisionPills.Clear()` (first line, before the badge reset), iterates a
   new `internal static IReadOnlyList<(string LabelKey, string PillKey, RunDecisionSeverity Severity)>`
   `DecisionCategories` table instead of its local `categories` array, and counts with
   `events.Count(e => RowLabelKey(e) == labelKey)`. The badge loop below it is untouched.
8. `private string RowLabelKey(AgentTimelineEvent row)` directly under `DecisionLabelKey`: returns the
   `Run_Timeline_Decision_NotExecuted` key when
   `row.Decision == ToolGateDecision.ParkedForApproval && row.Id != _liveParkRowId`, else
   `DecisionLabelKey(row.Decision)`. A **wrapper** over the one mapping, not a second switch — the file's
   own warning at `:1827-1829` (two mappings over the same eleven ordinals is how a decision ends up
   labelled Denied and painted in the routine grey) is honoured because severity is derived from the
   wrapper's output too.
9. **DELETE** `internal static RunDecisionSeverity Severity(ToolGateDecision decision)` (`:1830-1835`) and
   replace it with `internal static RunDecisionSeverity SeverityForKey(string labelKey)` carrying the
   identical switch body. Grep confirmed exactly five call sites, all in this file (`:1763`, `:1764`,
   `:1830`, `:2091`, `:2092`), none in tests or converters — **re-verify at integration**, because another
   slice's branch could add one and turn this into a build break. The new key falls through to
   `RunDecisionSeverity.Routine` with no new arm.
10. Convert `Project(AgentTimelineEvent row, bool showGroupSeparator)` (`:1837`) from an expression body to
    a block: `var labelKey = RowLabelKey(row);`, then `DecisionLabel = _localization[labelKey]` and
    `Severity = SeverityForKey(labelKey)`. Every other member byte-for-byte unchanged, so
    `TimelineRowViewModel` gains no property and `TimelineRowsCarryNoPathAndNoPayload`'s exact-set
    assertion still holds.
11. `ApplyChildTimelineAsync` (`:2091-2092`): change both partition predicates to
    `SeverityForKey(RowLabelKey(e))`. Children can never park (`HeadlessRunLauncher.cs:154` —
    `CanParkForApproval(Guid? parentRunId) => parentRunId is null`), so the change is vacuous today; it is
    made anyway so the two partitions cannot diverge from the row labels.
12. Add the new category to `DecisionCategories` **between Blocked and Approved**, so a completed run reads
    "2 nicht ausgefuehrt, 4 automatisch freigegeben" and the badge (first non-Routine category) goes quiet.

**Which discriminator, and why not "a later row exists for the same tool".** That alternative cannot tell a
granted-and-re-parked `write_file` from a still-parked one (the reported run parks on `write_file` twice),
and `ToolCallId` is nullable on v1 rows. The chosen rule is `row.Id != _liveParkRowId`: a row is live iff it
is THE newest `ParkedForApproval` row whose tool matches the pause envelope on a run that is actually
parked. A run parked a second time on the SAME tool therefore renders the newer row as awaiting and the
older one as not-executed — correct, because `ToolApprovalStore` is per-step and first-call-wins, so the run
can only be stopped on one call at a time.

**Why `Routine` and not `Refused`.** `RunDecisionSeverity`'s own doc says the tiers answer "does this need
me?", and that `Awaiting` is the warning palette because the call was not turned down but is waiting for an
answer. A superseded park needs nobody and was not turned down. `Refused` would put a semibold
danger-palette badge on the collapsed header of every run that ever parked — the same class of noise
issue 4 exists to remove, differently worded. Consequence: superseded park rows move from the exceptions
block into the routine block, which is why Slice 2 rewrites the ordering test.

**Why Awaiting <= 1 is structural, not a clamp.** `ToolApprovalStore.PendingToolName` is first-call-wins
(its own doc: the pause envelope names ONE tool), and the park arm emits the audit row only `if (parked)`
("Audited only for the call that actually parked the run"). So there is exactly one `ParkedForApproval` row
per park and `LiveParkRowId` returns at most one id. No `Math.Min` anywhere.

**Why 0 on a terminal run is structural.** `IsToolApprovalPause` is set from
`run.State == WaitingForInput && RunPauseEnvelope.ReadReason(run) == ToolApprovalReason` (`:1003-1007`), and
`WaitingForInput` (3) is disjoint from `IsTerminal`'s Completed(5) / Failed(6) / Cancelled(7) (`:815`). No
extra `IsTerminal` guard is added — it would be dead code; F3's theory pins it instead.

**Resources — all three locales**, or `LocalizationTests.AllTranslations_MustBeComplete` fails on both its
missing- and orphan- assertions. The app ships invariant/de/fr, not two languages.

- `Run_Timeline_Decision_NotExecuted`, immediately after `Run_Timeline_Decision_AwaitingApproval`
  (resx `:1071`, de `:1095`, fr `:1095`). EN "Not executed"; DE "Nicht ausgefuehrt" with the real
  u-umlaut; FR "Non execute" with the real accents. Sentence casing matches the siblings Abgelehnt and
  Blockiert.
- `Run_Timeline_Pill_NotExecuted`, immediately after `Run_Timeline_Pill_Blocked` (resx `:1091`, de `:1115`,
  fr `:1115`). EN "{0} not executed"; DE "{0} nicht ausgefuehrt"; FR "{0} non execute(s)". Lowercase, to
  match the sibling "{0} abgelehnt".

**Tests — fixture additions first.** In
`tests/Pia.Wpf.Tests/ViewModels/RunProgressViewModelTimelineTests.cs`, extend the private `Row(...)` helper
with two optional trailing params (`string? toolName = null`, `DateTime? createdAt = null`) mapping to
`ToolName: toolName ?? "write_file"` (empty on the `TraceTruncated` kind) and
`CreatedAt: createdAt ?? DateTime.UtcNow`; every existing call site keeps compiling. Add
`private void StubRun(AgentRunState state, string? extraJson = null)` returning a run with `Plan = []`;
re-stubbing replaces the previous `Returns`, which is what the same-vm transition tests need.

**Tests — the six new facts**

- **`AParkThatLandsAfterTheTraceRead_StillLightsTheAwaitingPill_WithNoSecondStoreRead`** — the decisive
  ordering test, and the only shape that proves the projection path re-derives. Rows: one
  `AutoApprovedPolicy`, one `ParkedForApproval` (`NotExecuted`). Stub the run `Running` with
  `ExtraJson = null`, `CreateVm()`, await `TimelineLoadTask` (the priming read). First assert the 41-second
  window: no awaiting pill, and the park row reads the not-executed label. Then re-stub the run
  `WaitingForInput` with the tool-approval envelope naming `write_file` and `await vm.RefreshAsync()` on the
  SAME vm. Assert `IsToolApprovalPause`; exactly one awaiting pill by **equality**, not `<= 1`, so an absent
  pill reds; `TimelineExceptionBadge` is the awaiting pill; `TimelineExceptionSeverity == Awaiting`;
  `Timeline[0].DecisionLabel` is the awaiting label; and `Received(1)` on `GetForRunAsync` — one read total,
  proving the pill came from the cached snapshot rather than a re-read.
- **`ANonParkProjection_DoesNotRebuildTheTraceRows`** — pins the identity guard, without which a refactor
  silently reintroduces ~500 row rebuilds per run and nothing reds. Two routine rows, run `Running`, capture
  `Timeline[0]` and `DecisionPills[0]`, raise `RunChanged` five times (the pattern
  `TimelineIsNotLoadedByRunChanged` already uses), then `Assert.Same` on both and `Received(1)`.
- **`ASecondParkOnTheSameTool_LeavesOnlyTheNewerRowAwaiting`** — three `write_file` rows: park (older),
  auto-approved, park (newer). Run `WaitingForInput` on `write_file`. Assert `Assert.Single` for the
  awaiting label, that its `TimeLabel` is the **newer** row's, `Severity == Awaiting`, `Assert.Single` for
  the not-executed label at `Routine`, and exactly one awaiting pill.
- **`AParkRowOnARunParkedOnADifferentTool_ReadsNotExecuted`** — non-vacuity for the tool-name term. A
  `write_file` park row and a `remember` park row; run parked on `remember`. The `remember` row (located by
  `ToolName`, never by index) is the only awaiting row; the `write_file` park row is not-executed at
  `Routine`.
- **`ATerminalRunShowsNoAwaitingPill_AndItsParkRowsReadNotExecuted`** — `[Theory]` over Completed, Failed
  and Cancelled, the exact set `IsTerminal` uses. Reproduces `approval_counter_wrong.png`: two park rows
  (`write_file`, `remember`) plus four `AutoApprovedPolicy` rows. Assert no awaiting pill, null badge,
  `HasTimelineExceptionBadge` false, severity `Routine`, two not-executed rows,
  `Assert.All(vm.Timeline, r => Assert.False(r.IsException))`, and that the not-executed **pill** is present
  — the fact is kept, only the copy and the palette change.
- **`AParkedRunThatSettles_DropsTheAwaitingPillWithoutASecondStoreRead`** — same-vm transition. Park, assert
  the awaiting pill; re-stub `Completed`, `RefreshAsync`, assert no awaiting pill, null badge, a single
  not-executed row and `IsToolApprovalPause` false. Note in the test that a terminal `RefreshAsync` does
  latch one extra trace read via `_settledTraceRead` (`:794-798`), which is why the no-re-read assertion
  lives in the first test and not here.

**Tests — the two rewrites**

- **`TheTraceSortsExceptionsFirst_ThenTheRestNewestFirst`** (`RunProgressViewModelTimelineTests.cs:63-110`)
  — REWRITE. Its fixture leaves `_runs.GetAsync` returning null, so `Project` never runs and the seq-2 park
  row is now a superseded park at `Routine`. The `Assert.Collection` becomes four arms: [0] Blocked /
  `Refused` / separator false; [1] AutoApproved (seq 3) / `Routine` / **separator TRUE** — it moves here,
  because the exception block is now one row; [2] NotExecuted (seq 2) / `Routine` / false; [3] AutoApproved
  (seq 1) / `Routine` / false. Pills, in `DecisionCategories` order: Blocked, NotExecuted, AutoApproved.
  Badge is the Blocked pill at `Refused` — the awaiting category no longer outranks it, because on a run
  nobody is parked on there is no awaiting category. Extend the doc comment with one sentence: a park row on
  a run that is not parked is history, so the only exception left is the blocked call.
- **`EveryDecisionPillKeyResolvesInAllThreeLocales`** (`Architecture/LocalizationTests.cs:217-247`) —
  REWRITE, mandatory not cosmetic. Its tail `Assert.Equal(categories, keys.Length)` hard-fails at seven pill
  keys against six mapped categories. Drive it off `RunProgressViewModel.DecisionCategories` instead of the
  hand-written key copy: `Assert.Equal(7, pillKeys.Length)` for non-vacuity; resolve **both** the pill keys
  and the `LabelKey`s in invariant/de/fr through the existing `GetResourceKeysForCulture` loop (this also
  closes a pre-existing gap — the decision LABEL keys are literals inside a switch, so
  `AllCodeLocalizationKeys_MustExistInResources`'s regexes never see them); replace the count assertion with
  coverage, `Assert.Contains(mapped, labelKeys)` for every distinct `DecisionLabelKey` output, plus
  `Assert.Contains("Run_Timeline_Decision_NotExecuted", labelKeys)` for the derived category no ordinal maps
  to.

**Do NOT touch** `AParkedForApprovalRow_IsLabelledAsAwaiting_NotAsUnknownAndNotAsDenied` (`:301-309`),
`EveryDecisionOrdinalMapsToALabel` (`:292-297`), `OutOfRangeAndUnknownOrdinalsRenderAsUnknown` (`:338-343`)
or `TheSessionTierDecisions_FoldIntoAutoApprovedAndApproved_NotIntoUnknown` (`:313-325`). They pin the raw
`DecisionLabelKey(ToolGateDecision)` mapping, which this slice deliberately leaves alone, and keeping them
green is what proves the relabel is a wrapper rather than a rewrite of the eleven-ordinal switch.
`TimelineRowsCarryNoPathAndNoPayload` (`:346-366`) is unaffected — `TimelineRowViewModel` gains no property.

**Build hazard, stated once.** `LocalizationTests.cs:243` consumes `DecisionLabelKey` as a **method group**
(`.Select(RunProgressViewModel.DecisionLabelKey)`). Adding an optional or extra parameter to that method is
a hard build break (CS0123) — optional parameters do not participate in method-group conversion. That is
precisely why this slice introduces a separate `RowLabelKey` wrapper instead of widening `DecisionLabelKey`.

**`RunProgressPanel.xaml` is NOT touched.** The pill `ItemTemplate` (`:695-706`) and the badge `Border`
(`:709-714`) already bind `Text`, `Severity`, `IsException`, `TimelineExceptionBadge` and
`TimelineExceptionSeverity`; this slice only re-values them. No interactive control is added, so no
`AutomationId` and no `ViewAutomationIdTests` row. Verified: `IAgentTimelineService.GetForRunAsync` has
exactly two call sites in `src/`, both in this view model (`:1715`, `:2063`), so no second surface counts
these rows — `AgentRunNotificationSurface.cs:145-146` reads the pause ENVELOPE, not the trace, and is
already correct.

**A second surface fixed for free.** The same loop feeds `TimelineExceptionBadge` /
`TimelineExceptionSeverity`, i.e. the collapsed-header badge at `RunProgressPanel.xaml:709-714` — the
surface in the screenshot a reader sees WITHOUT expanding. A design that touched only the pill list would
have left the badge wrong.

---

### Slice 3 — D2 + G3: the two gate-free string-and-XAML fixes

Two unrelated one-file changes, batched because each is XS and neither shares a file with anything else in
the plan.

**Files**

- `src/Pia.Wpf/Services/Plugins/BuiltInPluginDefaults.cs`
- `src/Pia.Wpf/Controls/Flow/FlowView.xaml`

**D2 — the missing FILES half of the two-stores disambiguation.** Confirmed by reading the file: the memory
half is **already written**. `BuiltInPluginDefaults.cs:45` already ends with "Do not use write_file for a
vault source, new or existing: its path spelling differs from update_source's/create_source's and will not
resolve the same way..." and the ingest entry (`:105`) carries a third, compatible statement. Nothing is
added there. The files `ConfigJson` at `:93` contains **zero** occurrences of "vault" (verified by grep).

Edit `:93` only. Append two sentences to the end of the files `systemPromptAddition`, after its existing
final sentence ("When the user asks to summarize a file, call read_file first and then summarize the
returned content in your reply."):

> This folder is NOT the user's memory vault: nothing you write with write_file is in the vault, and in an
> unattended run the vault is not part of this folder at all. To put a document in the vault, call the
> memory tool create_source('sources/<name>', content) for a new source, or update_source(reference,
> content) to correct an existing one.

Use single quotes inside the raw-string JSON literal, matching `MemoryService.cs:933`'s own error text
("choose a new 'sources/<name>' path") and avoiding the backslash-escaped double quotes the ingest entry
needs. Bump the files entry's `UpdatedAt` (`:94`) from `new DateTime(2026, 5, 17, ...)` to
`new DateTime(2026, 8, 31, 0, 0, 0, DateTimeKind.Utc)`, matching what the memory entry did (`:46`) when its
vault sentence landed. No comment is added to the source — the prompt text is the change.

No migration and no DB touch: `PluginService.InitializeBuiltInPlugins` (`:90`) loads every entry from code,
and `LoadPersistedPlugins` (`:116-134`) skips any DB row whose id is in `PreloadedPluginIds` (`:123`), so
the edit reaches existing installs on next launch.

**Tests (D2)** — `tests/Pia.Wpf.Tests/Services/FilesPluginPromptScaffoldingTests.cs`, beside its three
existing facts, which stay untouched:

- **`FilesPlugin_ConfigJson_SaysTheFolderIsNotTheVault`** — reuses the existing private
  `FilesSystemPromptAddition()` helper; asserts the string contains "NOT the user's memory vault",
  "create_source", "update_source" and "sources/".
- **`MemoryPlugin_ConfigJson_StillForbidsWriteFileForAVaultSource`** — the other half of the pair, so a
  later edit cannot delete one side of the disambiguation and leave the other. Reads
  `BuiltInPluginDefaults.Defaults[MemoryPluginId].ConfigJson` and asserts it contains "Do not use write_file
  for a vault source" and "create_source". One-line summary saying the two halves are asserted together on
  purpose.

**G3 — let the Flow card body be readable.** Pure XAML, one element, no new interactive control, no new
dependency, no change to what the card carries. `FlowView.xaml:163-169`'s `BodyText` keeps its
`Text="{Binding Body}"`, its per-item AutomationId
`{Binding Item.Id, StringFormat='Flow_Body_{0}'}` and its two `DataTrigger`s (`:223-229`). Replace the bare
`TextTrimming="CharacterEllipsis"` with the bounded multi-line form: add `TextWrapping="Wrap"` and
`MaxLines="3"`, **keep** `TextTrimming="CharacterEllipsis"` (it is what puts the ellipsis on the third line
rather than hard-clipping), add `ToolTipService.ShowDuration="30000"`, and give it an explicit **wrapping**
tooltip instead of a string one:

```xml
<TextBlock.ToolTip>
  <ToolTip MaxWidth="420">
    <TextBlock Text="{Binding Body}" TextWrapping="Wrap" />
  </ToolTip>
</TextBlock.ToolTip>
```

The explicit `ToolTip` element is load-bearing twice: a plain `ToolTip="{Binding Body}"` renders one
unwrapped line that runs off the screen edge, and the default 5 s `ShowDuration` closes before a 400-char
approval sentence can be read. The inline tooltip inherits its `DataContext` from its placement target, so
`{Binding Body}` resolves to the same `FlowItemViewModel`.

**Why `MaxLines` and not `MaxHeight`.** `MaxLines` bounds the number of formatted lines, so the body
occupies at most 3 line-heights (~54 px at `FontSize` 12) no matter how long `Body` is; a `MaxHeight` lets
the `TextBlock` format every line first and then clip, which bounds the pixels but not the work. That
matters here because **nothing virtualizes**: `FlowView.xaml:378-388` is a plain `ItemsControl` inside a
`ScrollViewer` with no `ItemsPanel` and no `VirtualizingStackPanel`, and the same `DataTemplate` is
instantiated a second time by the collapsed-rail arrival peek at `:508`.

**What G3 does and does not widen.** The Flow `Body` stays sourced from the pause envelope through the
unchanged `AgentRunNotificationSurface.PausedBody` -> `Flow_Run_ToolApprovalOn` (tool plus the 400-capped
args). Source cap unchanged at 120/400; visible cap 3 wrapped lines (~165 chars at the 340 px
`FlowRailWidth`); tooltip cap = the whole `Body`, ~470 chars, about 7 wrapped lines at `MaxWidth` 420. That
makes 100% of what the card carries legible, where today ~55 of ~470 chars are. It deliberately does **not**
widen `Body` itself: `FlowItem.Body` is persisted per durable item in `FlowPersistenceStore` and compared by
value on dedup re-publish, and `AgentRunNotificationSurface`'s own comments call the args "the exception to
that rule and a deliberate one". The full read belongs on the run panel (Slice 13), where the reader can
scroll.

**Tests (G3)** — new file `tests/Pia.Wpf.Tests/Views/FlowCardBodyBoundsTests.cs`,
`[Collection("WpfApplicationStatic")]`, rail built like `FlowRailCardAutomationTests.BuildRail`:

- **`AnApprovalBody_RendersUpToThreeWrappedLines_WithAWrappingTooltip`** — one `FlowItem` with a realistic
  470-char approval body, measured and arranged at 1000x900. On the realized `BodyText`: `TextWrapping ==
  Wrap`, `MaxLines == 3`, `TextTrimming == CharacterEllipsis`, `ActualHeight <= 3 * lineHeight + 2`, and
  `ToolTip` is a `ToolTip` whose single child is a `TextBlock` with `TextWrapping == Wrap` and `Text` equal
  to the body. Today's single line is the failing leg of the wrap claim; an unbounded wrap is the failing leg
  of the height claim; a plain string tooltip fails the last clause, which is the whole point of the explicit
  element.
- **`AnOversizedBody_StillCannotGrowTheCard`** — regression guard for the day someone widens the Flow body
  past 400 chars: a 200 000-char body of space-separated words with newlines every ~80 chars (breakable
  text, not one pathological unbreakable token). The same measure/arrange completes and the height bound
  still holds — remembering that this `ItemsControl` realizes every card.

**`ViewAutomationIdTests`' FlowView row stays `(10, 10, "CardDecisionBar,PiaChatStateBadge")`** — G3 adds no
control. If a "show more" toggle is ever added it needs the per-item form
`{Binding Item.Id, StringFormat='Flow_ShowFullBody_{0}'}` — **not** `Flow_BodyToggle_`, which collides with
`Flow_Body_` under prefix matching — the row becomes `(12, 12, ...)` because both card-template hosts
realize it, and `FlowRailCardAutomationTests.Expected` gains `|Flow_ShowFullBody_{i.Id}` directly after
`Flow_Body_{i.Id}`. That second pin fixes the exact per-card id sequence in tree order and is the other
reason G3 adds no control.

---

### Slice 4 — C1(a): the store, inert

Nothing reads or writes it yet, so this slice is verifiable entirely by its own tests.

**Files** (7, but two are one line each)

- `src/Pia.Wpf/Infrastructure/SqliteContext.cs` — the merged DDL block from section 2, appended to
  `EnsureSchema`'s single CREATE string after the `AgentTimelineEvents` block and its two indexes. No
  `MigrateSchema` entry.
- `src/Pia.Wpf/Models/AgentToolExchange.cs` (new) — `AgentToolExchangeKind`, `AgentToolExchangeResult`,
  `AgentToolExchangeRow`. Namespace `Pia.Models`, beside `AgentTimelineEvent.cs`.
- `src/Pia.Wpf/Services/Interfaces/IAgentToolExchangeStore.cs` (new) — the full interface from section 2,
  plus `AgentToolExchangeScope`.
- `src/Pia.Wpf/Services/AgentToolExchangeSerializer.cs` (new) — both directions of the
  `ChatMessage`-to-row round trip in one `internal static` class, so losslessness has a single test target.
- `src/Pia.Wpf/Services/AgentToolExchangeStore.cs` (new) — the SQLite implementation.
- `src/Pia.Wpf/Bootstrapper.cs` — ONE line beside `:567`.
- `src/Pia.Wpf/Services/AssistantChatRetentionService.cs` — take `IAgentToolExchangeStore` and call
  `PruneAsync(cutoff)` from the existing single `PruneTimelineAsync` call site (`:116-118`), so both
  retention modes — including the history-disabled one-day floor at `:82` — sweep the exchange store the way
  they sweep the timeline.

**Serializer members**

```csharp
internal const int MaxRowChars = 1_048_576;
internal const int MaxSeedValueChars = 400;

internal static IReadOnlyList<AgentToolExchangeRow> ToRows(
    Guid runId, Guid? stepId, int round, long seqFrom, long messageSeqFrom,
    IReadOnlyList<ChatMessage> messages, DateTime now);
internal static IReadOnlyList<ChatMessage> ToMessages(IEnumerable<AgentToolExchangeRow> rows);
internal static string? SerializeArguments(IDictionary<string, object?>? arguments);
internal static Dictionary<string, object?>? DeserializeArguments(string? json);
internal static (AgentToolExchangeResult Kind, string? Text) SerializeResult(object? result);
internal static object? DeserializeResult(AgentToolExchangeResult kind, string? text);
internal static Dictionary<string, object?> CapForSeed(IDictionary<string, object?> arguments);
```

`CapForSeed` is group B's model-facing cap (400 chars per value) and lives here so the codec owns every
argument transformation; it is unused until Slice 8.

**Serialization rules, and the two stated lossy edges**

- **Arguments**: `JsonSerializer.Serialize(arguments)` over the `IDictionary<string, object?>`. Values are
  `JsonElement` (provider-parsed) or primitives (test- and decorator-built); System.Text.Json writes an
  object-typed `JsonElement` as its exact raw JSON and a primitive as its natural JSON, so the text is
  byte-identical to what the provider sent. Rehydration is
  `JsonSerializer.Deserialize<Dictionary<string, JsonElement>>` boxed into `Dictionary<string, object?>`.
- **Lossy edge 1, benign**: a value that entered as a raw CLR `string` comes back as a `JsonElement` of
  `ValueKind.String`. Both existing argument readers already accept both shapes —
  `AgentToolCarryover.PathArgument` and `ToolApprovalArguments.Describe` each switch on `string s` AND
  `JsonElement { ValueKind: JsonValueKind.String }`, and `FilesToolHandler.GetStringArg` unwraps
  `JsonElement`. That is the evidence `JsonElement` is this codebase's canonical argument shape. On the wire
  it re-serializes to the same JSON either way, so the provider cannot tell.
- **Result**: `string s` -> `(Text, s)`. Anything else -> `(Json, JsonSerializer.Serialize(result))`,
  rehydrated as a `JsonElement`. M.E.AI serializes `FunctionResultContent.Result` to JSON on the way out, so
  a `JsonElement` and the original object produce the same wire bytes. A **Json** result over `MaxRowChars`
  is downgraded to `(Text, truncated + AgentToolCarryover`'s `"\n[truncated]"` shape)`, because a truncated
  JSON is not parseable. A **string** result cannot exceed `MaxRowChars` in practice — `Capture` already caps
  string results at `MaxCarriedResultChars = 4000`.
- **Lossy edge 2, stated**: `FunctionCallContent.Exception` is NOT persisted. It has no provider wire
  representation, and the paired result already carries `MalformedToolArgumentsResult` saying the args did
  not parse.
- **`ToMessages`**: group by `MessageSeq` (rows already ordered by `Seq`), map `Role` to `ChatRole`
  ("assistant"/"tool", falling back to the `Kind`-implied role), build a `List<AIContent>` of
  `FunctionCallContent(CallId, ToolName, DeserializeArguments(...))` and
  `FunctionResultContent(CallId, DeserializeResult(...))`, then one `ChatMessage(role, contents)`. This
  reproduces exactly the shape `Capture` produced — including two parallel `FunctionCallContent`s in one
  assistant message — so nothing is regrouped and no provider adjacency rule is disturbed.

**Store behaviour**

- **Infrastructure** as in section 2: dedicated connection, `busy_timeout=3000`, lazy open, one `_gate`,
  `context.GetConnection()` forced in the ctor, `IDisposable`.
- **`RecordAsync`**: serialize the round's messages to candidate rows; open a transaction; run the ONE
  conditional-aggregate query from section 2's column rule 2 (maxima over ALL kinds, counts and char sums over
  Kind 1/2 only) — which also seeds `Seq`/`MessageSeq` **from the table**, so a run parked in one process and
  resumed in another continues its sequence instead of interleaving; if the batch would exceed
  `MaxRowsPerRun` or `MaxCharsPerRun`, **roll back the whole batch** and log once per run from an in-memory
  `HashSet<Guid> _capNoted`; else INSERT each row with `++Seq` and a per-message `++MessageSeq`, and commit.
  All-or-nothing is what keeps a call from being stored without its result.
- **`AppendParkedAsync`** (Slice 7) runs the SAME aggregate and allocates from the SAME maxima, so ordering is
  global per run — but it is **exempt from the per-run cap**, which is why the cap terms are Kind-scoped
  rather than the maxima. See section 2, column rule 2.
- **`PurgeRunAsync`**: `DELETE FROM AgentToolExchanges WHERE RunId=@r` plus `_capNoted.Remove(runId)`.
- Every public method failure-isolated: a fault logs a warning with run id and counts, and returns.
- **Privacy**: no payload is ever logged. Run ids, row counts, char counts and tool names only.

**Tests (Slice 4)**

New file `tests/Pia.Wpf.Tests/Services/AgentToolExchangeSerializerTests.cs`:

- **`Arguments_RoundTrip_ReproducesTheExactJsonTheProviderSent`** — a dictionary holding a `JsonElement`
  string, a `JsonElement` number, a nested object, an array, a raw CLR string and a null survives
  serialize -> deserialize -> serialize with byte-identical JSON text and the same key set.
- **`ARawStringArgument_WidensToAJsonElement_WhichBothArgumentReadersStillAccept`** — after the round trip,
  `ToolApprovalArguments.Describe` on the rebuilt call still renders `path=...`, and
  `AgentToolCarryover.ClearOldResults`' placeholder for that call still names the path. Pins lossy edge 1 as
  benign.
- **`Result_AStringIsText_AnObjectIsJson_AndAnOversizeJsonResultDowngradesToTruncatedText`**.
- **`ToMessages_GroupsByMessageSeq_KeepingParallelCallsInOneAssistantMessage`** — rows for one assistant
  message carrying two calls plus two following tool-result messages rebuild into exactly 3 `ChatMessage`s
  with the original roles, the first holding both calls, and every `CallId` matched by a result.

New file `tests/Pia.Wpf.Tests/Services/AgentToolExchangeStoreTests.cs`:

- **`RecordAsync_ThenReadCarriedAsync_ReturnsTheRoundsMessagesInOrder_WithFullResultBodies`**.
- **`NoPersistedResultBody_IsEverAClearedPlaceholder`** — after recording more than
  `AgentToolCarryover.KeptResults` rounds, no `ResultText` starts with "[result cleared". The durable twin of
  `_messages`, not of the compacted request.
- **`AWriteFileArgumentOf512KChars_IsPersistedVerbatim`** — a call whose `content` argument is
  `FilesToolHandler.MaxWriteChars` long comes back untruncated with `ArgsOmitted` false. This is the property
  that lets one table serve group B's replay.
- **`PastTheRunCharCap_TheWholeBatchIsRefused_SoNoCallIsStoredWithoutItsResult`** — every returned
  `FunctionCallContent` `CallId` still has a matching `FunctionResultContent`, the row count stops growing,
  and one Information line names the run id.
- **`AnArgumentOverMaxRowChars_KeepsTheCallButOmitsTheArgs`** — `ArgsOmitted` true, `ArgumentsJson` null, the
  paired result present, `Chars` 0.
- **`SeqContinues_AcrossAFreshStoreInstance_ForTheSameRun`** — the cross-process resume property.
- **`DeletingTheChat_PurgesTheExchangeRows`** — create chat plus run, append one row with a canary payload,
  delete the chat through `AssistantChatService`, assert `SELECT COUNT(*)` is 0. Proves the ON DELETE CASCADE
  is actually enforced, which is the property the whole of Q1 rests on.
- **`PruneAsync_DropsRowsOfTerminalRuns_AndKeepsAParkedRuns`** — one Completed, one Failed, one Cancelled and
  one `WaitingForInput` run; exactly the first three are deleted.
- **`ReadCarriedAsync_ExcludesAParkedCallRow`** — a `Kind = ParkedCall` row is not returned, so the re-seed
  can never send a duplicate `tool_call_id`.
- **`TheStoresPublicSurface_NamesNoSyncAssistantChatType`** — reflection over the interface's and the class's
  public members finds no parameter, return or generic argument mentioning `SyncAssistantChat` or
  `SyncAssistantChatMessage`. The cheap type-level backstop for the `_messages`/`_persisted` guardrail.

`tests/Pia.Wpf.Tests/Infrastructure/SqliteContextTests.cs`:

- **`AgentToolExchanges_HasExactlyTheseColumns`** — `PRAGMA table_info` against the exact 22-name list from
  section 2, in the style of `AgentTimelineEvents_HasExactlyTheMetadataColumns` (`:100-121`). Its comment
  states out loud that this table's contract is the INVERSE of the timeline's metadata-only one, so nobody
  aligns the two.

**Also in this slice**: mark `docs/agent_run_e2e/2026-08-27-cross-step-tool-context.md` superseded in two
places (its section 5 "Resume" claim that "the post-resume state equals the post-clearing state", and its
"What this does not do" bullet "It does not persist tool exchanges... never reach history.db"). Both become
false. `AgentToolCarryover.ReReadHint` stays — it is still true for genuinely cleared results.

---

### Slice 5 — C1(b) + C2: write per round, re-seed in `BeginRunAsync`

**Files**

- `src/Pia.Wpf/Services/BackgroundAssistantTurnRunner.cs`
- `src/Pia.Wpf/Services/HeadlessTurnExecutor.cs`

**WHEN the write happens, and why per ROUND is the only correct seam.** `RunExchangeAsync` gains a trailing
optional `AgentToolExchangeScope? exchanges = null`, and its `case ToolRoundExchange round:` arm (`:374-376`)
becomes:

```csharp
toolExchanges.AddRange(round.Messages);
if (exchanges is not null)
    await exchanges.RecordAsync(round.Round, round.Messages).ConfigureAwait(false);
```

Per-STEP writing at `HeadlessTurnExecutor`'s `_messages.AddRange(exchange.ToolExchanges)` line (`:660`) is
**wrong and would silently persist nothing for the one step group C exists for**. Verified in the code: the
approval-park arm (`:592`), the `request_user_input` arm (`:606`) and the fault-after-park arm inside
`catch (Exception ex)` (`:550`) **all return ABOVE `:660`**, and the fault arm has no `exchange` variable
assigned at all. Per-round also carries the ask park and a crash mid-step for free. Every other caller passes
null — `RunAsync`'s single-turn path and the whole interactive path record nothing.

**Known lossy edge, named not fixed.** A round whose tool dispatch THROWS never yields `ToolRoundExchange` —
in `AiClientService` the yield sits AFTER `await DispatchToolCallsAsync`. So no seam, per-round or per-step,
can persist a round whose tool threw. Fixing it means moving the capture inside `DispatchToolCallsAsync`,
which is out of scope here.

**`HeadlessTurnExecutor` changes**

1. Ctor: add `IAgentToolExchangeStore? exchangeStore = null` as the **last** parameter, after
   `ISessionToolGrantStore? sessionGrants = null`, trailing and defaulted for the reason the file already
   gives three times over. Field `private readonly IAgentToolExchangeStore? _exchangeStore;`. All eight
   existing positional constructions in the suites keep compiling with the store null, which is exactly
   today's behaviour.
2. `private AgentToolExchangeScope? ExchangeScope(Guid? stepId)` beside the existing `TimelineScope`
   (`:400-401`): null when `_exchangeStore is null`, else a new scope over `(_runId, stepId)`.
3. `RunExchangeStepAsync` gains a trailing `AgentToolExchangeScope? exchanges = null` and relays it into
   `_engine.RunExchangeAsync(..., deniedWrites: _deniedWrites, exchanges: exchanges)`. A separate parameter
   rather than deriving it from `timeline?.StepId`: the timeline service is optional and null in most suites,
   and deriving one optional collaborator's key from another would make the store silently inert wherever no
   timeline is injected. Call sites: `ExecuteStepAsync` -> `ExchangeScope(step.Id)`;
   `RunSingleTurnFallbackAsync` -> `ExchangeScope(stepId: null)`; `RunGraceTurnAsync` -> `null`, because
   `toolFree` strips the tool list so no round can produce an exchange — stated rather than relied upon.
4. **Seal**, in `RunExchangeStepAsync` immediately after the `_persisted.Add(new SyncAssistantChatMessage {
   Id = assistantMsgId, ... })` block and BEFORE `if (persistInterim)` (so the R10 degrade turn seals too):
   `if (exchanges is not null) await exchanges.SealAsync(assistantMsgId).ConfigureAwait(false);` with
   `CancellationToken.None` inside the scope, for the reason `PersistChatAsync` already uses it — a
   settle-time write must not be cancelled out from under the row it anchors. The seal is unconditional even
   when this attempt recorded nothing: a previous, parked attempt's rows for the same step are still
   unanchored, and this is the write that finally anchors them.
5. **Purge**, in `EndRunAsync` after the terminal `PersistChatAsync`:
   `if (_exchangeStore is not null) await _exchangeStore.PurgeRunAsync(run.Id, CancellationToken.None).ConfigureAwait(false);`
   `SafeEndRun` is called on every terminal path in `AgentRunOrchestrator` and deliberately never on a park
   or a pause ("Deliberately NO SafeEndRun and no promotion: a park is not terminal"). A terminal run has no
   reader.

**The re-seed, in `BeginRunAsync`.** Today the method loads `chat`, then seeds `_messages`/`_persisted` in
two branches (resume at `:303-306`, fresh-with-goal-first at `:308-330`), each looping `chat.Messages`.

1. Right after `_existingWorkingDirectory = chat?.WorkingDirectory;`:
   `var carried = await ReadCarriedAsync(run.Id, chat, ct).ConfigureAwait(false);`
2. Replace both loop bodies with one local function, so the two branches cannot drift:

   ```csharp
   void SeedRow(SyncAssistantChatMessage m)
   {
       if (carried.Anchored.TryGetValue(m.Id, out var groups)) _messages.AddRange(groups);
       _messages.Add(new ChatMessage(ParseRole(m.Role), m.Content));
       _persisted.Add(m);
   }
   ```

3. At the very end of the method, after both branches: `_messages.AddRange(carried.Trailing);`

New members: `private async Task<CarriedToolExchanges> ReadCarriedAsync(Guid runId, SyncAssistantChat? chat,
CancellationToken ct)` and
`private sealed record CarriedToolExchanges(IReadOnlyDictionary<Guid, List<ChatMessage>> Anchored,
IReadOnlyList<ChatMessage> Trailing)` with a static `Empty`. `ReadCarriedAsync` returns `Empty` when
`_exchangeStore is null`; otherwise it reads, runs `AgentToolExchangeSerializer.ToMessages` per `MessageSeq`
group, and partitions on whether the group's `AnchorMessageId` is in the chat's message-id set.
Failure-isolated in the shape `SafeSeedResumeContext` uses:
`catch (Exception ex) { _logger.LogWarning(ex, "..."); return CarriedToolExchanges.Empty; }` — a corrupt row
degrades a resume to prose-only (today's behaviour) instead of failing every resume. One Information line,
ids and counts only.

**Ordering, stated precisely.** Anchored groups land immediately BEFORE the chat row they were anchored to,
which is byte-for-byte where the in-process path puts them (`_messages.AddRange(exchange.ToolExchanges)` then
`_messages.Add(assistant reply)`) and byte-for-byte the rule the live twin uses. Unanchored groups — the
abandoned step's pre-park exchanges, which have no assistant reply because a park discards the step's prose —
go at the **TAIL**, after every chat row. Two reasons: they are the context of the step that is about to
re-run; and `ClearOldResults` keeps the newest K results by **list position**, so the tail is what guarantees
the pre-park results are the ones that stay verbatim. A group whose `AnchorMessageId` matches no surviving
chat row falls into `Trailing` too, so the algorithm is total and a stale anchor can never silently drop a
group.

**The compaction seam is NOT touched.** The re-seed lands in `_messages`; `RunExchangeStepAsync` still builds
`exchangeMessages` as a copy (`:459-464`), still runs `AgentToolCarryover.ClearOldResults` (or
`WithoutToolExchanges` on a tool-free turn) and still runs `AgentContextCompactor.CompactAsync`, in the same
build -> clear -> compact order. So the post-resume context budget is identical to the pre-park one, and a
tool-free turn after a resume still drops the pairs. `AgentToolCarryover.cs` and `AgentContextCompactor.cs`
are not edited at all by this plan.

**How the `_messages`/`_persisted` guardrail (`:466-480`) still holds with a THIRD source.** The guardrail's
property is one-directional: nothing that reaches the model context may shrink or alter the persisted
transcript. The new source is a one-way arrow INTO `_messages` only. (a) It writes `ChatMessage`, a type
`_persisted` cannot hold, and `BuildChatSnapshot`'s `Messages = [.. _persisted]` remains the only route from
executor state to the chat DB. (b) The store's own write source is `round.Messages` — never `_persisted`,
which the store's type surface cannot even name. (c) The only fact crossing from the chat side to the store
side is one scalar, `AnchorMessageId`. (d) The store is a different table on a different connection and is
not in `AssistantChatSyncService`'s path. Net: `_messages` still has exactly one consumer and `_persisted`
still has exactly one producer.

**Why `AnchorMessageId` is not an FK.** The terminal chat write is a full DELETE/re-INSERT of the message
rows, but it re-writes the SAME ids (already pinned by `InterimAndTerminalSaves_AgreeOnMessageIds`), so an
anchor stays valid across it. An FK would either cascade through that in-transaction DELETE and destroy the
anchors mid-save, or reject the save — the same house reasoning `AgentTimelineEvents.StepId` is documented
with. `SaveMergedAsync`'s absorbed foreign rows carry their own ids and have no groups anchored to them, so
they are seeded as prose exactly as today.

**Tests (Slice 5)** — all in `tests/Pia.Wpf.Tests/Services/HeadlessTurnExecutorTests.cs`:

- **`AResumeSeedsCarriedExchangesBeforeTheReplyTheyBelongTo`** — two steps run with the store wired; a fresh
  executor resumes and runs step 2. In the captured request, step 0's `read_file` call/result pair appears at
  a lower index than the "reply 1" assistant prose row, i.e. the same relative position the in-process path
  produced.
- **`AStoreFaultOnResume_DegradesToProseOnly_AndDoesNotFailTheRun`** — with a store substitute whose
  `ReadCarriedAsync` throws, `BeginRunAsync` completes, the step runs, the request holds the prose rows only,
  and one Warning was logged.
- **`ANullExchangeStore_LeavesEveryExistingPathByteForByteUnchanged`** — the existing two-step carry fixture
  produces identical captured requests with the store parameter omitted, and no store method is called.
- **`AToolFreeTurnAfterAResume_IsStillNotHandedTheReSeededExchanges`** — after a resume that re-seeded pairs,
  `RunGraceTurnAsync`'s captured request holds no `FunctionCallContent` and no `FunctionResultContent`.
- **`PastTheKeptCount_AResumedRunStillClearsTheOldestResult`** — with more than
  `AgentToolCarryover.KeptResults` results across the park boundary, the resumed step's request holds the
  cleared placeholder for the oldest and the verbatim body for the newest. Clearing still runs downstream of
  the re-seed, so the context budget holds.
- **`ATerminalSettle_PurgesTheRunsExchanges_ButAParkDoesNot`**.
- **`AnOrphanedAnchor_StillReachesTheModelContextAtTheTail`** — a group whose `AnchorMessageId` names a
  message id absent from the chat appears in the rebuilt request (at the tail) rather than being dropped.

---

### Slice 6 — C3: pin the park/resume, and hoist the harness ONCE

Test-only slice. The harness change lands here so Slices 11 and 15 can build on it rather than three groups
reconciling the same file.

**Files**

- `tests/Pia.Wpf.Tests/Services/HeadlessTurnExecutorTests.cs`

**Harness change.** The existing `DurabilityHarness` cannot drive a REAL approval park: `NewExecutor` creates
its `IPluginService` and `IToolPermissionService` substitutes locally, so no fixture can make
`RouteToolCallAsync` return a pending action. Hoist both into the harness as public readonly fields, beside
`Composer` / `Personas` / `Providers` / `SettingsService`, which are already hoisted for exactly this reason
("a resume must meet the same persona store and composer the launch did"). Add
`public IAgentToolExchangeStore? Exchanges;` seeded by a fixture and threaded into `NewExecutor`'s
construction as the trailing argument. Add
`private static void ArmApprovalPark(DurabilityHarness h, string toolName)`.

**The park is produced through the REAL gate**, which is what makes this a regression test rather than a
mirror of the fix:

- `h.Plugins.RouteToolCallAsync(Arg.Any<FunctionCallContent>())` returns
  `(null, new PluginToolCall("write_file", pluginId, "Files", "write a file", null, () => Task.FromResult<object?>("written")))`.
- `h.Plugins.IsMcpTool("write_file")` returns false, so `ToolClassifier` does not classify it External.
- `h.Permissions.IsGranted(...)` returns false (the substitute default).
- `executor.Initialize(workspaceRoot: null, grantedWrites: [], h.Provider, policy: null, canPark: true)` —
  `grantedWrites` deliberately EMPTY so nothing above the park arm in `ToolAutonomy.Resolve` authorizes it.
- The run row is `TriggerKind User` with `ParentRunId` null, so `_isTopLevelUserRun` and `_canAskUser` are
  true and `ToolApprovalStore` is armed (`canPark` AND `offerStepResultTool`).

Verified against `ToolAutonomy.Resolve`: Surface Unattended + CanPark + not delete-like + not External falls
through every auto-approval tier to the park arm.

**The fixture, mirroring the reported run.** Step 0: one `read_file` round returning an inventory line;
completes; persists "reply 1". Step 1: a `read_file` round, then a `write_file` call in round 2 that hits the
gate and parks; `ExecuteStepAsync` returns `ApprovalRequiredTool == "write_file"` and no persisted reply.
Resume: a FRESH executor from the same harness (a new DI scope, exactly as the existing
`ParkedAtBudget_...` test models it), sharing the same `Exchanges` store; `BeginRunAsync`, then
`ExecuteStepAsync` for step 1 again, capturing the request.

**Tests**

- **`ParkedMidStep_TheResumedStepsRequestCarriesThePreParkToolExchange`** — the resumed step-1 request holds
  step 0's `read_file` result body AND step 1's pre-park `read_file` result body verbatim, plus a
  `FunctionCallContent` for each; and `ApprovalRequiredTool == "write_file"` asserted in the same test, so a
  fixture that stopped parking fails loudly instead of silently asserting on a non-park.
- **`ParkedMidStep_WithNoExchangeStore_TheResumedStepSeesProseAlone`** — the NEGATIVE control, and what makes
  the test above a regression test: the same fixture with `h.Exchanges = null` yields a resumed request with
  zero `FunctionResultContent` and only the "reply 1" prose row. Without it the first assertion could pass
  for the wrong reason.
- **`ParkedMidStep_TheReSeededExchangesReadInTheOrderTheyHappened`** — index(step-0 pair) < index("reply 1"
  prose row) < index(step-1 pre-park pair) < index(step instruction user row).
- **`ParkedMidStep_ThePayloadNeverReachesTheCloudSyncedChat`** — the primary guardrail pin. After the park
  and the resume, no persisted chat row has `Role == "tool"` and none contains either result body, while the
  request just asserted on contains both. The park+resume extension of the existing
  `CarriedToolExchanges_DoNotReachThePersistedChat`.

**Do not add a turn-count or round-count assertion here.** Slice 1 changes how many rounds step 1 spends
before parking; these assertions are on request CONTENT and survive it.

---

### Slice 7 — B1: persist the parked and withheld calls verbatim

**Files**

- `src/Pia.Wpf/Services/ToolApprovalStore.cs`
- `src/Pia.Wpf/Services/BackgroundAssistantTurnRunner.cs`
- `src/Pia.Wpf/Services/HeadlessTurnExecutor.cs`
- `src/Pia.Wpf/Services/AgentToolExchangeStore.cs` (+ `IAgentToolExchangeStore.cs`) — the group-B methods
- `src/Pia.Wpf/Services/AgentToolExchangeSerializer.cs` — nothing new; `CapForSeed` already landed in Slice 4

**Store additions.** `ToolApprovalStore` gains a **nested** `public sealed record ParkedCall(string ToolName,
string? CallId, int Round, Guid? PluginId, string? ArgumentsJson, string? DisplayArgs, bool Withheld)` —
nested, so it is exempt from `RecordTypes_MustNotLiveInTheServicesRootNamespace` — plus
`void Record(ParkedCall call)`, `IReadOnlyList<ParkedCall> RecordedCalls`, `int DroppedRecords`, and the two
cap constants `MaxRecordedCalls = 8` / `MaxRecordedArgumentChars = 1_048_576`.

`Park(string, string?)` is left **byte-for-byte unchanged**, including its first-wins rule and its
"only this tool's own arguments" filter, so `PendingToolName`, `ParkedCalls` and `PendingToolArguments` keep
every existing meaning and every existing test. The cap lives in `Record`, under the same `_lock`: an
overflow record is **DROPPED whole**, never truncated (a half payload is unreplayable), and `DroppedRecords`
increments. This bound is B-owned and not punted to the store's per-run cap, which by design does not govern
Kind 3/4 rows.

Trim `ToolApprovalStore`'s class remark: "nothing here is durable — the only thing that survives is the tool
NAME, which the orchestrator writes into the run's pause envelope" is now false. Cut to one short line. The
"ARMED IFF THE RUN MAY PARK" and first-wins paragraphs are unaffected.

**Gate arms.** In `BackgroundAssistantTurnRunner`:

- `HandleToolCallAsync`, the withheld-because-parked arm (`:474`): one added line after its existing
  `approvals.Park(...)` — `approvals.Record(BuildParkedCall(pending, toolCall, dispatch, withheld: true));`
- `DispatchGateVerdictAsync`, `case ToolGateOutcome.Park:`: the same with `withheld: false`, placed right
  after the existing `var parked = ...` and **OUTSIDE** the `if (parked)` audit guard. A second parked call
  of the same tool must be persisted (the delete-four-files case) even though it writes no second timeline
  row.

`private static ToolApprovalStore.ParkedCall BuildParkedCall(PluginToolCall pending, FunctionCallContent
toolCall, ToolDispatchContext dispatch, bool withheld)` persists `pending.ToolName` (**not**
`toolCall.Name`): that is the single spelling the pause envelope, the grant list, `deniedWrites` and
`RouteToolCallAsync` all key on, so the replay predicate and the re-route cannot drift. `ArgumentsJson` is
`AgentToolExchangeSerializer.SerializeArguments(toolCall.Arguments)`; `DisplayArgs` is
`ToolApprovalArguments.Describe(toolCall)`.

The withheld-because-**asking** arm is deliberately NOT touched: that exit is a `request_user_input` park with
no grant to replay against, and leaving it alone is what keeps a row's existence equivalent to "this run
parked for a tool approval".

**Merge note.** Slice 1 added `dispatch.Stop?.RequestStop();` as the first statement of these same three
arms. Slice 7 adds a different statement to two of them. Textual merge only — do not move the arm bodies.

**Persist site.** `HeadlessTurnExecutor` gains one new private method
`private async Task PersistParkedCallsAsync(ToolApprovalStore approvals, Guid? stepId)`, awaited from BOTH
park exits in `RunExchangeStepAsync`: the fault-after-park arm (`if (approvals.PendingToolName is { }
parkedBeforeFault)`, inside the `catch`, `:550`) and the normal park arm
(`if (approvals.PendingToolName is { } parkedTool)`, `:592`), in each case immediately before the
`return new StepTurnResult(... ApprovalRequiredTool: ...)`.

The method: returns immediately when `_exchangeStore is null` or `approvals.RecordedCalls.Count == 0`; calls
`SupersedeUnreplayedAsync(_runId, distinct tool names)` **ONCE** (not per row, or rows of the same tool in one
pass would supersede each other); then one `AppendParkedAsync` with every record mapped to an
`AgentToolExchangeRow` (`Kind = ParkedCall` or `WithheldCall`, `Role = "assistant"`, `ResultKind = None`,
`ResultText = NULL`); all wrapped in a single try/catch plus `LogWarning`, so a failed persist degrades the
resume to today's behaviour and never fails the park.

**No new ctor parameter.** Slice 5 already added `IAgentToolExchangeStore? exchangeStore = null`. ONE
parameter total; this slice reuses `_exchangeStore`.

**Logging.** Run id, step id, row count, `DroppedRecords`, tool name — all scalars and ids.
`ArgumentsJson` and `DisplayArgs` reach only `_logger.SensitiveDebug`.

**Privacy note that must be stated in the code, once, on the DDL `Kind` comment (already written in
section 2).** These arguments are REAL user content: the gate sits below
`TokenizingAiClientService.WrapToolHandler`, which detokenizes before the handler sees them. So Kind 3/4 rows
hold un-tokenized PII by construction. That is exactly what Q1 authorises — local-only, FK-cascaded, purged
at terminal settle, `SensitiveDebug` only — and it is the reason nothing here may ever reach `_persisted` or
`SyncAssistantChatMessage`.

**Tests (Slice 7)**

- `tests/Pia.Wpf.Tests/Services/UnattendedApprovalParkTests.cs` ->
  **`AParkPersistsTheCallVerbatim_NotJustTheDisplayString`**. Launch, park on `write_file` whose `content`
  argument is 200 000 chars (extend `Build`'s existing `firstPath` plumbing with a `firstContent` parameter).
  Read `AgentToolExchanges` for the run: exactly one row, `Kind == ParkedCall`, `ToolName == "write_file"`,
  `CallId == "call-1"`, `Round == 1`, `ResultText IS NULL`, `ReplayedAt IS NULL`, `SupersededAt IS NULL`; the
  deserialized `content` equals the full 200 000-char string, so a cap regression goes red; `DisplayArgs` is
  the 400-char capped line and is NOT equal to `ArgumentsJson`. The control is that the **envelope's** `args`
  member is still the capped string — this slice adds a channel, it does not widen the envelope.
- `tests/Pia.Wpf.Tests/Services/AgentToolExchangeStoreTests.cs` ->
  **`ThePerParkCap_DropsWholeRecordsRatherThanTruncatingThem`**. Record 12 parked calls of 200 000 chars each
  into one `ToolApprovalStore`; assert `RecordedCalls.Count <= MaxRecordedCalls`, `DroppedRecords > 0`, and
  that every persisted row's deserialized `content` is the FULL 200 000 chars — no row carries a partial
  payload, because a partial payload is unreplayable.
- `tests/Pia.Wpf.Tests/Services/AgentToolExchangeStoreTests.cs` ->
  **`SupersedeRunsOncePerPass_SoSiblingCallsOfOneToolDoNotCancelEachOther`**. Persist four `ParkedCall` rows
  of one tool in a single pass; assert all four have `SupersededAt IS NULL` and all four are returned by
  `GetReplayableAsync` in `Seq` order. A per-row supersede leaves only the last one replayable, which would
  silently drop three of a four-file delete approval.
- `tests/Pia.Wpf.Tests/Services/AgentToolExchangeStoreTests.cs` ->
  **`AParkedRowIsNeverDroppedByThePerRunCap`**. Fill the run past `MaxRowsPerRun` with Kind 1/2 rows, then
  append a `ParkedCall`; assert it is present and returned by `GetReplayableAsync`. Pins the cap-scoping
  decision from section 2 — the row a human's Continue press replays can never be silently dropped.

`Build` also needs one mechanical change: register `IAgentToolExchangeStore` and pass it to the
`HeadlessRunLauncher` ctor. Slice 1 already touched `DriveWithToolCall` and `ToolProbe` in this file; the two
edits are in different members.

---

### Slice 8 — B2: replay the granted call once, before the step re-runs

The highest-risk slice in the plan. Read gate Q2 and section 1 consequence 4 before starting.

**Files**

- `src/Pia.Wpf/Services/HeadlessTurnExecutor.cs`
- `src/Pia.Wpf/Services/BackgroundAssistantTurnRunner.cs`
- `src/Pia.Wpf/Services/HeadlessRunLauncher.cs`
- `src/Pia.Wpf/Services/Interfaces/IAgentTurnExecutor.cs` (doc comment only)

**WHERE, exactly.** `HeadlessTurnExecutor.ExecuteStepAsync` — one awaited line inserted after the
`var setup = _stepPersonas is null ? ... : await _stepPersonas.ResolveAsync(...)` block (`:344-348`) and
BEFORE the `return await RunExchangeStepAsync(...)` call (`:351`):

```csharp
await ReplayApprovedParkedCallsAsync(step.Id, TimelineScope(step.Id), ct).ConfigureAwait(false);
```

That point is **after** the grant is known — `HeadlessRunLauncher.ApplyToolApprovalDecisionAsync` has already
widened the grant set and `executor.Initialize(...)` has already seeded `_grantedWrites` and the new
`_approvedTool`, both before `orchestrator.RunAsync` — and **before** the step's first provider round-trip,
which happens inside `RunExchangeStepAsync` -> `_engine.RunExchangeAsync`.

It is NOT put inside `RunExchangeStepAsync`, because that method builds `exchangeMessages` from `_messages` at
its top (`:459-464`): a replay placed after the ambient bracket there could not reach the request it is
supposed to seed. It is NOT put in the launcher or the orchestrator, because the replayed call must run under
`TaskAmbient.Current.WorkspaceRoot` or a workspace-isolated `write_file` escapes into the user's assistant
files folder. `RunSingleTurnFallbackAsync` and `RunGraceTurnAsync` reach `RunExchangeStepAsync` directly and
therefore never replay — no extra flag needed.

**Ambient.** Extract the existing
`new TaskContext(_runId, WorkingSubpath: null, OnFileTouched: null, WorkspaceRoot: _workspaceRoot, ChatId: _chatId, UnattendedGranter: _grantedBy)`
(`:510-511`) into `private TaskContext StepAmbient()` and call it from both sites, so the replay's
`WorkspaceRoot` (sandbox) and `UnattendedGranter` (audit attribution) cannot drift from the step's.
`ReplayApprovedParkedCallsAsync` sets `TokenMapAmbient.Current = _tokenMap` when `_tokenizationEnabled` and
`TaskAmbient.Current = StepAmbient()` in a try/finally that restores both, exactly like the step bracket. One
short comment on `StepAmbient` saying it has two callers.

**THE PREDICATE — get this exactly right.** `_approvedTool`, a new trailing defaulted `Initialize`
parameter, is the whole predicate together with the row state. The pass is a no-op unless
`_approvedTool is not null && !_replayAttempted`; then `_replayAttempted = true` (one-shot per dispatch,
belt-and-braces beside the DB marker, and it saves a query per step) and the rows come from
`GetReplayableAsync(_runId, _approvedTool)`.

It is deliberately **NOT** `_grantedWrites.Contains(row.ToolName)`. The withheld-because-parked arm fires for
ALREADY-GRANTED tools too, and replaying one of those would execute it twice — once by the replay and once by
the re-run — breaking the existing pinned fact
`UnattendedApprovalParkTests.AGrantedCallAfterThePark_DoesNotRun_AndIsNotReplayedByTheResume`. The rule is
"you replay what the human just said yes to, nothing else", and it gets both B2 and B3 right with one
predicate and no extra column.

The row's `StepId` is deliberately **NOT** matched against `step.Id`: a replan deletes and re-inserts
`AgentSteps` rows (which is why `StepId` is not an FK here), so keying on it would silently disable the
replay after a replan.

**AT-MOST-ONCE.** For each row, in `Seq` order:
`if (!await _exchangeStore.TryMarkReplayedAsync(row.Id, DateTime.UtcNow, ct)) continue;`. Execution happens
only on a won mark, so two concurrent resume dispatches, a crash between mark and execute, and a second
Continue press all resolve to at most one execution — **by construction, not by advice**. A failed or false
mark skips the row entirely (fail-closed).

**EXECUTION.** New public method on `BackgroundAssistantTurnRunner`, which already holds `_pluginService`,
`_permissions` and the gate, so no new dependency reaches the executor:

```csharp
public Task<object?> ReplayToolCallAsync(
    FunctionCallContent call, HashSet<string> grantedWrites, int round,
    RunAutonomyPolicy? policy = null, AgentTimelineScope? timeline = null,
    ToolApprovalStore? approvals = null, HashSet<string>? deniedWrites = null);
```

which forwards to `HandleToolCallAsync(call, grantedWrites, new ToolDispatchContext(round), policy, timeline,
outcomeStore: null, approvals, userInput: null, deniedWrites)`. The executor rebuilds the call as
`new FunctionCallContent(row.CallId is { Length: > 0 } id ? id : Guid.NewGuid().ToString("N"), row.ToolName, AgentToolExchangeSerializer.DeserializeArguments(row.ArgumentsJson))`
— the synthesis is section 2's column rule 1, and the seeded `FunctionResultContent` must pair on that same
value, or two parked calls with blank provider ids produce an unpairable seed —
and passes `round: 1` — the replay stands in for the round-1 call the model would otherwise have made, so its
audit row reads exactly like the call it replaces. A replay must **not** pass a stop signal (there is no loop
to stop); `new ToolDispatchContext(round)` stays source-compatible with Slice 1's optional trailing param.

`approvals` is a THROWAWAY `new ToolApprovalStore(canPark: false, _sessionGrants, isTopLevelUserRun:
_isTopLevelUserRun)`, not null: `CanPark: false` makes `ToolAutonomy.Resolve`'s Park arm unreachable (no
replay may re-park) and self-disarms `HasSessionGrant` (which is gated on `CanPark`), while the honest
`IsTopLevelUserRun` keeps the replay resolving on the same inputs the original call was judged on. Verified in
source: `if (input.IsNamedGrant) return AutoRun/GrantedByName` sits ABOVE the park arm and carries no
delete-like exclusion, so an approved `delete_file` replays as `GrantedByName` today even with
`IsTopLevelUserRun: false`; passing it honestly is insurance against a future reordering of the codebase's
most-guarded method. A delete-like EXTERNAL tool can never be a parked row at all
(`DestructiveExternalTool_StillHardDenies_AndNeverParks`), so the refused-regardless rule cannot bite.

**OUTCOME AND FAILURE.** The whole per-row execution is wrapped. An `AutoRun` arm that throws rethrows out of
`HandleToolCallAsync` (after emitting its `AgentTimelineOutcome.Error` row), so the catch turns it into
`$"Not run: the approved '{row.ToolName}' was executed on your behalf and failed: {ex.Message}"`. A disabled
plugin or removed route yields `"Unknown tool."`; a declined tool yields the gate's own `"Denied: ..."`
string (the `HasNamedDenial` tier is the FIRST arm of `Resolve`); a read tool yields its immediate result. In
every case the text is written to the row via `SetResultAsync` and seeded into the context, and **the STEP IS
NEVER FAILED by a replay** — the step sees an ordinary tool exchange whose result happens to be an error,
exactly as if it had made the call itself and it had failed. Because `ReplayedAt` was stamped BEFORE
execution, a failed replay is consumed and never retried on a later resume.

Logging: `_logger.LogWarning(ex, "Replay of the approved tool {ToolName} for run {RunId} faulted",
row.ToolName, _runId)` plus `_logger.SensitiveDebug` for the result text; never the arguments.

`ReplayApprovedParkedCallsAsync` runs OUTSIDE `RunExchangeStepAsync`'s try/catch, so it **must never throw**:
every arm is wrapped, or a fault would escape `ExecuteStepAsync` into the orchestrator's outer handler and
fail a run over a best-effort seed.

**SEEDING — and the tokenization fix that has no test today.** Per replayed row, appended to `_messages` and
to NOTHING else (`_persisted` is a `List<SyncAssistantChatMessage>`, a different type on the cloud-synced
path, and the guardrail at `:466-480` is what keeps them apart):

```csharp
_messages.Add(new ChatMessage(ChatRole.Assistant, [seedCall]));
_messages.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(seedCall.CallId, seedResult)]));
```

This pair **bypasses `TokenizingAiClientService` entirely**, so when `_tokenizationEnabled` BOTH halves must
be tokenized before they are seeded:

- `seedCall`'s arguments are `AgentToolExchangeSerializer.CapForSeed(args)` (400 chars per value — the model
  does not need its own 512 K body echoed back, it needs to know the write landed), **each string value then
  passed through `_tokenMap.TokenizeStructuredResult(...)`**. Without this, detokenized real content —
  the row is Kind 3/4, i.e. what the gate saw — reaches the provider raw on the next round.
- `seedResult` is the real result string, likewise through `_tokenMap.TokenizeStructuredResult(...)`.

After the pass, ONE `ChatMessage(ChatRole.User, ...)` note naming the replayed tool(s) and saying the call was
executed and must not be repeated — model-facing and deliberately unlocalized, like `GraceTurnInstruction` and
`AgentToolCarryover.ReReadHint` beside it.

Because the seed lands in `_messages` before `RunExchangeStepAsync` builds `exchangeMessages`, it flows
through `ClearOldResults` + `AgentContextCompactor` unchanged — no post-compaction append, so the context
budget still holds.

**Why this is safe against a double execution.** Two mechanisms, and only the first is structural.
(1) `ReplayedAt` guarantees the persisted call is executed by the replay at most once, ever, across processes
and concurrent dispatches. (2) Nothing can stop the MODEL reissuing the call in the re-run — that is true
today and this plan does not change it; the orchestrator's own `ParkForUserInputAsync` remark already says a
re-run step's side effects may repeat. What makes a reissue unlikely is the seed: the model's first view of
the step already contains `assistant: write_file(...)` / `tool: <real result>`, which is far stronger
discouragement than prose. **No argument-hash dedupe gate is added**: it would misfire on a legitimate second
write to the same path, and it is not what Q2 asked for.

Note that Q2's phrasing offered replay OR seeding as alternatives. They are not alternatives: replaying
without seeding leaves the model unable to see that its call ran, so it reissues and the side effect happens
twice. Replay is for the effect; the seed is for the model's first view.

**DECLINE PURGE.** In `HeadlessRunLauncher.ApplyToolApprovalDecisionAsync`'s
`if (declineToolApproval && approvedTool is not null)` branch (`:873`), after the existing
`UpdatePolicyJsonAsync` persist and before the return, add a failure-isolated
`await _exchangeStore.DeleteReplayableAsync(run.Id, approvedTool, ct)`. Scoped to the declined tool and
nothing else — never a whole-run delete, which would destroy another tool's surviving withheld call and
Slice 5's context seed. It is triple-covered (the purge removes the rows; `_approvedTool` is null on the
decline path so the predicate never fires; and `HasNamedDenial` is the first arm of `ToolAutonomy.Resolve`),
but the deletion is the one that matters: the human said no to up to 512 K chars of their own content.

**LAUNCHER PLUMBING.** `HeadlessRunLauncher` gains a trailing defaulted ctor param
`IAgentToolExchangeStore? exchangeStore = null` (after `steering`, so the existing `runsBaseDirOverride:`-named
construction in the suites keeps compiling). `ResumeDispatchPlan` (`:774-784`) gains `string? ApprovedTool`,
built in `ResumeAsync` as `declineToolApproval ? null : approvedTool`. **That ternary is load-bearing, not
defensive**: `claim.ApprovedTool` is non-null on the DECLINE path too, because `TryClaimForResumeAsync`
derives it purely from `parkReason == ToolApprovalReason` with `declineToolApproval` as a separate flag
(`:810-816`). `RunResumedDispatchAsync` (`:1005-1006`) passes `approvedTool: plan.ApprovedTool` to
`Initialize`. `LaunchCoreAsync`'s `Initialize` call (`:570-571`) is left alone — a fresh launch has approved
nothing.

**Two doc comments become false and are trimmed in this slice:**

- `HeadlessRunLauncher.ApplyToolApprovalDecisionAsync`'s remark (`:857-860`): "The pending CALL cannot be
  replayed — a park outlives the process, and the deferred action's Execute() delegate does not — so what is
  applied is the CAPABILITY." Route-and-execute from persisted arguments is exactly the thing that remark
  calls impossible. Cut to one short line: the delegate does not survive; the arguments now do. Its
  neighbouring paragraph about persisting the widened grant ("Two tools, two parks") stays true and stays.
- `StepTurnResult.ApprovalRequiredTool`'s doc (`Interfaces/IAgentTurnExecutor.cs:154-158`): "The pending call
  itself cannot survive a park ... so what the human approves is the capability, and the resumed step
  re-issues the call." Half of that is now wrong. Cut to one short line. No member is added or changed.

**No `IAgentRunService` change.** `TryBeginResumeAsync` / `TryResumeFromPauseAsync` / `CompleteAsync` all
`SET ExtraJson=NULL` on `AgentRuns` only, and `ReplaceStepsAsync`'s `DELETE FROM AgentSteps WHERE RunId=@RunId`
is exactly why `StepId` is not an FK on the new table. `IAgentTurnExecutor` is unchanged too — `Initialize` is
on the concrete `HeadlessTurnExecutor`, not on the interface.

**Tests (Slice 8)**

- `UnattendedApprovalParkTests` -> **`ContinuingAPark_ExecutesTheParkedCallOnce_BeforeTheStepReruns`**. Park
  on `write_file`, `ResumeAsync`, await settled. (a) the row's `ReplayedAt` is non-null and `ResultText` is
  the route's result; (b) the messages the `IAiClientService` substitute received on the RESUMED dispatch
  (captured via `ci.ArgAt<IList<ChatMessage>>(0)`) already contain a `FunctionCallContent` named `write_file`
  and a matching `FunctionResultContent` — which is what proves "before the first provider round-trip" rather
  than merely "somewhere during the resume"; (c) the seeded call's `content` argument is capped at 400 chars
  while the persisted `ArgumentsJson` is not.
- `UnattendedApprovalParkTests` -> **`AReplayedCallIsSeededInItsTokenizedForm`** — the new privacy pin, and
  the only guard on section 1 consequence 4. With `Privacy.TokenizationEnabled` on, park on a `write_file`
  whose `content` carries a value the token map masks; resume; assert the resumed dispatch's request contains
  the **placeholder** form in BOTH the seeded `FunctionCallContent` arguments and the seeded
  `FunctionResultContent`, and contains the raw value nowhere. A build that tokenizes only the result fails
  this on the call half.
- `AgentToolExchangeStoreTests` -> **`MarkingReplayedIsConditional_SoOnlyOneCallerEverExecutes`**. Two
  sequential `TryMarkReplayedAsync` on one row -> true then false; then `Task.WhenAll` of two concurrent calls
  on a second row -> exactly one true. The structural half of Q2; if it degrades to an unconditional UPDATE,
  both return true and this reds.
- `UnattendedApprovalParkTests` -> **`AReplayThatFaults_SeedsTheStepWithTheFailure_AndIsNeverRetried`**. The
  route's `Execute` throws on the replay (new `faultOnExecute` flag on `Build`). Assert the run still reaches
  Completed (a replay must not fail the step), `ReplayedAt` is non-null (consumed, not retried), `ResultText`
  contains the failure text, the resumed step's request carries that text as a `FunctionResultContent`, and a
  timeline row exists with `AgentTimelineOutcome.Error`.
- `UnattendedApprovalParkTests` -> **`DecliningAPark_PurgesTheParkedCall_AndReplaysNothing`**. Park, resume
  with `declineToolApproval: true`. Assert zero rows remain for `write_file`, `write_file` never appears in
  `probe.ExecutedNames`, the re-run's gate result contains "Denied", and a timeline row carries
  `ToolGateDecision.DeniedForRun`. Pins both halves of the purge rule.
- `UnattendedApprovalParkTests` -> **`AMultiCallPark_ReplaysEveryCallInCallOrder`**. Two parked `write_file`
  calls in one round with distinct paths, then Continue. Assert the probe recorded the paths of the first two
  executions as A then B — the persisted `Seq` order, not a set — and that both rows carry `ReplayedAt`. A
  store that replayed newest-first, or replayed only `PendingToolArguments[0]`, goes red.

**Note for Slice 2's owner and for anyone reading the trace.** `round: 1` on the replay means a step can show
two round-1 timeline rows for the same `CallId` — the park (`DecidedAt` null) and the grant
(`GrantedByName`). That is the truth and `Seq` orders them. Slice 2's `LiveParkRowId` is unaffected: the park
row stays the newest `ParkedForApproval` row only while the run is still parked on that tool.

---

### Slice 9 — B3: the withheld-call lifecycle, and supersede

Mostly already carried by Slice 7's `WithheldCall` write and Slice 8's `ToolName == _approvedTool` predicate.
What this slice adds is the correct LIFECYCLE for a withheld row and the supersede rule that makes it safe.

**Files**

- `src/Pia.Wpf/Services/BackgroundAssistantTurnRunner.cs`
- `src/Pia.Wpf/Services/AgentToolExchangeStore.cs`
- `src/Pia.Wpf/Services/HeadlessTurnExecutor.cs`

**What "survives the same way" actually means.** The reported run's `create_source` was withheld because the
run was already parked on `write_file`. Pressing Continue grants exactly ONE tool —
`ApplyToolApprovalDecisionAsync` appends `approvedTool` and nothing else — so the `create_source` withheld row
is NOT authorized by that press and is NOT replayed. It stays in the table unreplayed and unsuperseded, with
its full arguments (the document body the model composed). The re-run reissues `create_source`, the gate parks
on it a second time, the human presses Continue again, and NOW the predicate matches: the surviving withheld
row is replayed. That is strictly better than today, where the body was discarded and had to be regenerated,
and it is the only reading that does not grant a tool the human was never asked about.

**Why an already-granted withheld call must not replay.** The withheld arm fires for every pending call after
the park, including one the run already had a named grant for (the existing
`AGrantedCallAfterThePark_DoesNotRun_AndIsNotReplayedByTheResume` fact). Those calls are deliberately withheld
so the RE-RUN performs them exactly once. Since `_approvedTool` is by construction a tool the run did NOT hold
(a granted tool never parks), the predicate excludes them for free — no extra column, no gate re-resolution in
the withheld arm. Their rows are still persisted, because Slice 5's re-seed wants them as context: the model
should see that it asked for `update_todo` and was told "not run, the run is waiting".

**SUPERSEDE.** Without it, the second Continue above matches TWO rows for `create_source`: the stale withheld
one (arguments X) and the fresh parked one (arguments X'), and both replay — the source is created twice.
`SupersedeUnreplayedAsync(runId, distinctToolNames)` runs ONCE per persist pass, before the INSERTs, with the
statement from section 2. Once per pass, **not per row**, or four parked calls of one tool in a single round
would supersede each other. The rule in one sentence: a later park recording tool T makes every earlier
unreplayed row for T stale, and the newest arguments are the model's current intent.

**HARNESS.** The existing `DriveWithToolCall` drives one fixed script for every dispatch, so a two-park
scenario is not expressible. Extend `Build` to take a per-dispatch script (a `Queue<string[]>` of tool names,
or a `Func<int, string[]>` keyed on a dispatch counter) and have `DriveWithToolCall` dequeue one entry per
call into `GetChatCompletionWithToolsAsync`. This is the only non-trivial harness change group B needs, and
the supersede fact cannot be written without it.

**Tests (Slice 9)** — all in `UnattendedApprovalParkTests` unless noted:

- **`AWithheldUngrantedCall_SurvivesTheGrantOfTheParkedTool_WithItsArgumentsIntact`** — the exact defect from
  the reported run. Park on `write_file`, withhold `create_source` with a 50 000-char `content`
  (`secondToolName: "create_source"`, ungranted). Continue (grants `write_file` only). Assert: `write_file`'s
  row is replayed; `create_source`'s row is still present with `Kind == WithheldCall`, `ReplayedAt IS NULL`,
  `SupersededAt IS NULL`, and its deserialized `content` still the full 50 000 chars; and `create_source`
  never appears in `probe.ExecutedNames` as a result of the replay.
- **`AWithheldCallOfAnAlreadyGrantedTool_IsNotReplayed_AndStillRunsExactlyOnce`** — restates the pinned
  `AGrantedCallAfterThePark_...` fact over the new store: `secondToolName: "update_todo"`, GRANTED at launch,
  withheld by the park. After Continue: `probe.ExecutedNames.Count(n => n == "update_todo") == 1` (the re-run,
  not the replay) and that row's `ReplayedAt IS NULL`. A predicate that drifted back to
  `_grantedWrites.Contains(...)` turns this red, which is the point of the test.
- **`ASecondParkOnTheSameTool_SupersedesTheStaleWithheldRow_SoTheGrantWritesOnce`** — two-dispatch script:
  dispatch 1 parks `write_file` and withholds `create_source(X)`; Continue; dispatch 2 parks
  `create_source(X')`; Continue. Assert exactly ONE `create_source` execution attributable to a replay, that
  the X row carries `SupersededAt` and no `ReplayedAt`, and that the X' row carries `ReplayedAt` — the newest
  arguments won. Without `SupersededAt` both replay and the assertion counts two.

---

### Slice 10 — D1: refuse a `Vault/` write inside a run workspace

**Files**

- `src/Pia.Wpf/Services/VaultTargetPolicy.cs` (new)
- `src/Pia.Wpf/Infrastructure/AssistantWorkspace.cs`
- `src/Pia.Wpf/Services/FilesToolHandler.cs`

**New file** `src/Pia.Wpf/Services/VaultTargetPolicy.cs`, namespace `Pia.Services`,
`internal static class VaultTargetPolicy`, modelled on `RunScratchFolder.cs` in the same folder, which
likewise pairs a model-facing hint constant with the path convention its consumers share. The point of one
type is that the refusal (this slice) and the step hint (Slice 11) cannot come to name different tools.

Members for this slice:

```csharp
internal const string CreateSourceToolName = "create_source";
internal static string SuggestedReference(string anchorRoot, string resolvedPath);
internal static string WriteRefusal(string anchorRoot, string resolvedPath);
```

`SuggestedReference`:
`remainder = Path.GetRelativePath(AssistantWorkspace.VaultRootFor(anchorRoot), resolvedPath).Replace('\','/').Trim('/')`
inside try/catch (catch -> the generic `sources/<name>.md`). If `remainder` is empty, `"."` or starts with
`"../"` -> generic. **That is the load-bearing guard for the at-the-root case**: `write_file("Vault", ...)`
makes `GetRelativePath` return `"."`, which would otherwise render `create_source('sources/.', content)`. If
`remainder` starts with `sources/` (OrdinalIgnoreCase) -> return it verbatim, so `Vault/sources/urlaub/2026.md`
yields the exact call `create_source('sources/urlaub/2026.md', ...)`. Else -> `"sources/" +
Path.GetFileName(remainder)`, falling back to generic when that leaf is empty. Forward slashes throughout —
it is a vault ref, which is what `VaultReference.NormalizePath` produces and what
`MemoryService.TryResolveSourceScope` compares with `rel.StartsWith("sources/")` (`MemoryService.cs:930`).

`WriteRefusal` returns one unlocalized, model-facing string interpolating
`AssistantWorkspace.VaultSubfolderName` rather than a literal:

> Error: this run works in an isolated workspace that does not contain the memory vault, so a file written
> under 'Vault/' here reaches no vault and is dropped when the run finishes. Call
> create_source('<suggestion>', content) to add a new vault source, or update_source(reference, content) to
> correct one that already exists. To keep this as a working file instead, write it outside 'Vault/'.

**`AssistantWorkspace.cs`** — add one member beside `VaultRootFor` (`:37-38`):
`public static bool IsAtOrInsideVaultOf(string filesFolder, string candidateFullPath)`, deriving
`var vaultRoot = VaultRootFor(filesFolder)` and returning
`candidateFullPath.Equals(vaultRoot, OrdinalIgnoreCase) || candidateFullPath.StartsWith(SafeFolderPath.WithTrailingSeparator(vaultRoot), OrdinalIgnoreCase)`.
This is byte-for-byte the shape `WorkingDirectoryService.cs:88-92` already uses to refuse a working directory
rooted at the vault, and the shape `RunWorkspaceService.IsInsideOrEqual` (`:1126-1139`) uses against
`VaultRootFor` for the copy-in and promote exclusions. Purely lexical — both inputs are already canonicalized
at the call site. **Consolidating the two existing copies is DECLINED**, for the reason
`AgentPlanner.cs:470-477` already declines the analogous consolidation; only the new call site is wired. One
short XML doc line: "True for the vault under `filesFolder` and anything inside it."

**`FilesToolHandler.PrepareWriteFile`** (signature `:963`). Insert BETWEEN the `SensitivePathGuard` block
(ends `:987`) and the `MaxWriteChars` check (`:989`) — grouped with the other path guards and deliberately
ahead of the size cap, so a 512 K vault write is told to use `create_source` rather than "too large":

```csharp
// The vault is never provisioned into a run workspace, and the promote walk drops anything under it.
var vaultAnchor = TaskAmbient.Current?.WorkspaceRoot is null ? null : root;
if (vaultAnchor is not null && AssistantWorkspace.IsAtOrInsideVaultOf(vaultAnchor, safePath))
{
    _logger.LogWarning("write_file rejected: the run workspace has no memory vault");
    _logger.SensitiveDebug("write_file vault-target path: {Path}", safePath);
    return WriteFailure(VaultTargetPolicy.WriteRefusal(vaultAnchor, safePath));
}
```

That is the only surviving comment this step adds; the WHY (the vault is absent and the write would be
discarded) is not visible from the code. `vaultAnchor` is the prepare-time capture — non-null means BOTH
"armed" and "the folder to anchor at" — so the deferred closure never reads `TaskAmbient`, per the documented
rule at `:1017-1023` for `taskId` and `touch`.

**Why anchor at `root` and not at the raw ambient**: `root` has already been canonicalized by
`SafeFolderPath.NormalizeWorkspaceRoot` exactly as `safePath` was, which is what keeps the prefix comparison
symmetric; the raw ambient string may be an uncanonicalized spelling and would miss.

Then thread it through the deferred path: change the `Execute:` lambda at `:1029` to pass `vaultAnchor`, add
`string? vaultAnchor` as the **9th positional parameter** of `ExecuteWriteAsync` (`:1040-1042`), immediately
before the optional `Action<FileTouch>? touch = null`, and after the existing `SensitivePathGuard` re-check
(`:1048-1049`) add

```csharp
if (vaultAnchor is not null && AssistantWorkspace.IsAtOrInsideVaultOf(vaultAnchor, finalPath))
    return Task.FromResult<object?>(WriteResult.Failed(VaultTargetPolicy.WriteRefusal(vaultAnchor, finalPath)));
```

for the same TOCTOU reason the two guards above it are re-checked (a reparse point planted between prepare and
execute could make `finalPath` land inside the vault subtree while `safePath` did not). Armed-ness is frozen
at prepare, so the re-check can never flip an approved write into a refusal. `PrepareWriteFile:1029` is
`ExecuteWriteAsync`'s only call site, so no other code moves.

**Which path is tested, and the answer to the above-or-below question.** `safePath` — the resolved,
canonicalized, containment-checked target — against `VaultRootFor(root)`. The premise that a working subpath
could put the vault above or below `root` **does not arise under a run workspace**: whenever `WorkspaceRoot`
is set, `WorkingSubpath` is null, stated independently three times — `HeadlessTurnExecutor.cs:510` sets every
step ambient with `WorkingSubpath: null` and `BeginRunAsync` states it as an explicit assignment
(`ctx.WorkingSubpath = null`, `:205-206`); `ChatSession.cs:737` passes
`spec.WorkspaceRoot is null ? WorkingDirectory : null` under the comment "an isolated run's workspace root
already IS the narrowed root"; and `StepTurnSpec.WorkspaceRoot`'s own doc says the same. So when the guard is
armed, `root == NormalizeWorkspaceRoot(workspaceRoot)` and `VaultRootFor(root)` is the ONE reachable vault
subtree. Anchoring at `root` also keeps the guard correct if that invariant is ever relaxed, because
`<root>\Vault` is the spelling a model writing relative paths actually produces.

**Behaviour on lookalikes.** `vault/x.md` IS refused — on Windows `<root>\vault` and `<root>\Vault` are the
same directory, so OrdinalIgnoreCase is the only correct comparison, not a widening. `Vault Backups/x.md` is
ALLOWED — the boundary carries a trailing separator. `docs/Vault/x.md` and `VaultNotes.md` are ALLOWED — the
boundary is root-anchored, which is what Q4's "every other workspace write is unchanged" requires. A write AT
`<root>\Vault` (path `"Vault"`) is refused, matching `WorkingDirectoryService`'s equals-or-inside shape.

**`delete_file` is deliberately NOT covered.** Primary reason: the refusal's actionable half has no referent —
there is no vault-source delete tool to name (`forget(reference)` removes a memory record or page, not a
staged source) — so a symmetric refusal could only say "don't", and delete can never silently write to the
wrong store, only fail. Corroborating reason: the vault is never copied into a workspace
(`RunWorkspaceService.CopyInAsync` excludes `VaultRootFor(sourceRoot)` and `VaultRootFor(settingsFolder)`,
`:818-826`), and D1 now prevents `write_file` from creating one, so `PrepareDeleteFile`'s existing
`File.Exists` arm (`:1235-1236`) already answers "File 'Vault/x' not found." for every reachable case. The
asymmetry is pinned by a test rather than assumed.

**Localization: none needed.** The refusal is model-facing only. It fires only inside an agent run;
`Pending` is null so no action card is built; a tool result never reaches `_persisted` (the type-enforced
guardrail) and never reaches `session.Messages` on the live twin. It also matches every other refusal in
`PrepareWriteFile`, all of which are unlocalized English literals. The path fragment inside it is user content
but is only ever handed back to the model that supplied it; the log lines keep the scalar reason at Warning
and the path under `SensitiveDebug`.

**Why not `SensitivePathGuard`** — the code already says so. Its `BuildAllowedExceptions` doc
(`SensitivePathGuard.cs:139-157`) states "The vault gets no entry here — full file-tool access by design ...
That narrowing is a PROVISIONING decision in RunWorkspaceService, not an entry here." Adding Vault to the
denylist would break the interactive path (the vault lives under the sandbox and interactive `write_file` into
it must stay allowed) and `MarkdownExportService`, which writes to `<folder>\Vault\sources\Exports` (pinned by
`MarkdownExportServiceTests:185`).

**Tests (Slice 10)**

New file `tests/Pia.Wpf.Tests/Services/FilesToolHandlerVaultTargetTests.cs`. Harness: the plain temp-dir
shape of `FilesToolHandlerScratchTests` (a temp path is outside every `SensitivePathGuard` blocked root, so no
`PiaPathsStatic` collection is needed) plus `FilesToolHandlerWorkspaceEscapeTests`' `TaskAmbient.Current = new
TaskContext(Guid.NewGuid(), WorkingSubpath: null, OnFileTouched: null, WorkspaceRoot: _runRoot)` setup and its
reflection `Prop<T>` reader for the private `WriteResult`.

- **`Write_UnderVault_InARunWorkspace_IsRefused_AndNamesBothMemoryTools`** — `path="Vault/sources/urlaub.md"`:
  pending is null, result non-null, success false, error contains `create_source('sources/urlaub.md'` and
  `update_source`, and no file exists at `<runRoot>\Vault\sources\urlaub.md`.
- **`Write_UnderVault_Interactive_IsStillWritten`** — the Q4 "interactive path byte-for-byte unchanged" proof.
  `TaskAmbient.Current = null`, root = an interactive folder with a real `<root>\Vault\sources` dir:
  `path="Vault/sources/urlaub.md"` returns a pending action, `Execute()` reports success, and the file exists
  on disk.
- **`Write_ToAVaultLookalike_InARunWorkspace_IsAllowed`** — `[Theory]` over `"Vault Backups/x.md"`,
  `"docs/Vault/x.md"`, `"VaultNotes.md"`, `"deliverable.md"`: each returns a pending action (result null),
  `Execute()` succeeds, and the file lands under the run root. Also the non-vacuity control for the refusal
  cases.
- **`Write_VaultSpellingVariants_AreRefused`** — `[Theory]` over `"vault/x.md"` (case),
  `"Vault\sources\x.md"` (backslash), `"./Vault/x.md"`, the absolute `<runRoot>\Vault\x.md`, and `"Vault"`
  itself. For `"Vault"` the error contains the generic `sources/<name>.md` form, pinning the "."-remainder
  guard rather than a rendered `sources/.`.
- **`Write_UnderVaultSources_SuggestsTheExactCall`** — `path="Vault/sources/urlaub/2026.md"`: the error
  contains `create_source('sources/urlaub/2026.md'`, so the model gets a call it can issue unchanged.
- **`Delete_UnderVault_InARunWorkspace_IsNotGivenTheVaultRefusal`** — records the deliberate asymmetry. With
  `<runRoot>\Vault\sources\x.md` present, `delete_file` returns a pending action and no result mentioning
  `create_source`; with it absent, the result is the existing not-found string and still never mentions
  `create_source`.

New file `tests/Pia.Wpf.Tests/Services/VaultTargetPolicyTests.cs`:

- **`SuggestedReference_Theory`** — pure-function table against `anchorRoot=@"C:\ws"`:
  `<ws>\Vault\sources\a.md` -> `sources/a.md`; `<ws>\Vault\sources\sub\a.md` -> `sources/sub/a.md`;
  `<ws>\Vault\a.md` -> `sources/a.md`; `<ws>\Vault\deep\a.md` -> `sources/a.md`; `<ws>\Vault` -> the generic
  form. Plus `WriteRefusal` contains `create_source`, `update_source` and `VaultSubfolderName`.

`tests/Pia.Wpf.Tests/Vault/AssistantWorkspaceTests.cs`:

- **`IsAtOrInsideVaultOf_Theory`** — beside the existing `VaultRootFor` test (`:29`). `filesFolder=@"C:\x"`:
  `C:\x\Vault` true; `C:\x\Vault\sources\a.md` true; `C:\x\vault\a.md` true; `C:\x\Vault Backups\a.md` false;
  `C:\x\docs\Vault\a.md` false; `C:\x\VaultNotes.md` false; `C:\x\a.md` false; `C:\x` false.

**Accepted risk, stated.** In worktree mode a repo that independently tracks a top-level `Vault/` directory
IS reachable in the workspace, and a write into it is now refused. Accepted per Q4's "only that" (which scopes
WHICH paths, not an exception inside vault paths): the alternative — disarming the guard when `<root>\Vault`
exists on disk — makes one tool call answer differently depending on provisioning mode and mutable disk
state, and would silently stop firing in the reported scenario. Note also that when the repo toplevel IS the
files folder, that `Vault/` is the real memory vault and refusing is correct. The failure mode is a legible,
actionable error; reopen Q4 if it bites a real user.

**Stronger justification than the defect doc gives.** Root cause B is DATA LOSS, not only confusion:
`RunWorkspaceService.CollectPromotableFiles` builds its exclusion list as `VaultRootFor(runRoot)` and
`VaultRootFor(destination)` (`:542-547`), so in copy mode anything the model writes under `<runRoot>\Vault\...`
is dropped by the promote walk and disappears with the workspace at teardown. And `CopyInAsync` excludes both
vault roots (`:818-826`), so `<runRoot>\Vault` never exists in copy mode in the first place. A `Vault/` write
in a run workspace is therefore silently discarded today. That is the sentence the refusal text is built on.

---

### Slice 11 — D3: the vault step hint, through ONE shared instruction composer

This slice creates `AgentStepInstruction.Compose`, which Slice 15 then widens. Doing the extraction once,
here, is why D3 precedes E1.

**Files**

- `src/Pia.Wpf/Services/VaultTargetPolicy.cs`
- `src/Pia.Wpf/Services/AgentStepInstruction.cs` (new)
- `src/Pia.Wpf/Services/HeadlessTurnExecutor.cs`
- `src/Pia.Wpf/ViewModels/Models/ChatSession.cs`

**The two composition sites, which must change together.** They are parity twins with an existing parity test
each, so a one-sided change leaves the live path silently un-hinted with nothing failing:

- `HeadlessTurnExecutor.BuildInstruction` (`:834-840`), called once from `ExecuteStepAsync:353` as
  `ctx.AppendNudge(BuildInstruction(step.Ordinal, step.Intent ?? string.Empty, step.ExpectedArtifact))`.
- `ChatSession.BuildStepChatMessagesAsync` (`:982-987`), the `else` arm of the `UseGoalVerbatim` branch.

**New file** `src/Pia.Wpf/Services/AgentStepInstruction.cs`, namespace `Pia.Services`,
`internal static class AgentStepInstruction`. DELETE `HeadlessTurnExecutor.BuildInstruction` and replace the
inline `ChatSession` block with a call. Signature for this slice:

```csharp
internal static string Compose(int ordinal, string intent, string? expectedArtifact,
    string? workspaceRoot, IEnumerable<AITool>? tools);
```

Body order, so today's `Expected:` text stays where the compaction test corpus expects it:
`$"Execute step {ordinal + 1}: {intent}."`; then `" Expected: {expectedArtifact}"` when non-empty; then
`" " + AgentToolCarryover.ReReadHint + " " + RunScratchFolder.StepHint` exactly as `:839` does today; then
`if (VaultTargetPolicy.StepHintApplies(workspaceRoot, tools)) instruction += " " + VaultTargetPolicy.StepHint;`.

Call sites: `HeadlessTurnExecutor:353` passes `_workspaceRoot` and `setup.TurnSetup.Tools` (`setup` is the
`StepPersonaSetup` already resolved at `:344-348`; `AgentStepTools.WithStepResultTool` only ever APPENDS to
that list, so the base list is the right and earliest reader). `ChatSession:982` passes `spec.WorkspaceRoot`
(the same discriminator that file already uses at `:737`) and `spec.Tools`. The `UseGoalVerbatim` arm
(`:979-981`) stays untouched — the planner-degrade turn has no step. `VaultTargetPolicy` is `internal` in
`Pia.Services`, and `ChatSession` already imports that namespace for `RunScratchFolder` and
`AgentToolCarryover`, so no new using in either file.

**New `VaultTargetPolicy` members**

```csharp
internal const string StepHint = "...";
internal static bool StepHintApplies(string? workspaceRoot, IEnumerable<AITool>? tools) =>
    !string.IsNullOrEmpty(workspaceRoot)
    && tools is not null
    && tools.Any(t => string.Equals(t.Name, CreateSourceToolName, StringComparison.Ordinal));
```

`StepHint`, with the same one-line doc its two neighbours carry ("Model-facing, so deliberately
unlocalized."):

> The working folder is NOT the user's memory vault and the vault is not part of it: a file you write here
> never reaches the vault, and a 'Vault/' path in the working folder is refused. Put a vault document there
> with create_source and an explicit vault-relative path under 'sources/' — e.g.
> create_source('sources/<subfolder>/<name>.md', content) — never a subfolder you leave implicit, and report
> that same reference as emit_step_result's artifact_ref.

**Does the instruction know the goal names the vault? No, and it must not try to.** `BuildInstruction`
receives only `(ordinal, intent, expectedArtifact)`; `ctx.Goal` is reachable at the call site, but detecting
"names the vault" from it is a keyword sniff over free user text in any language — the reported run's goal was
German ("20260831_Fehlzeitenuebersicht.xlsx ... in den Vault"), and Vault, Ablage, Wissensspeicher, Gedaechtnis
and a bare "merken" are all the same intent. The honest substitute is the pattern the file already uses for
this class of question: gate on the RESOLVED TOOL LIST plus the workspace scope, so "described" and "offered"
cannot drift — the same argument `AgentStepTools.OffersStepResultTool` / `OffersRequestUserInputTool` are built
on (`StepOutcomeSignal.cs:178-215`, and the executor's own comment at `:443-449`: "Armed IFF offered — derived
from the resolved list"). The workspace half keeps the hint exactly where Slice 10's refusal can fire; the
tool half stops the hint naming a tool a run without the memory plugin does not have.

**The hint deliberately does NOT tell the model to call `request_user_input`.**
`AgentStepTools.BuildRequestUserInputTool`'s own description (`StepOutcomeSignal.cs:189-211`) says calling it
ABANDONS the step and that `emit_step_result` with `succeeded=false` "is almost always the right choice", and
`CanRequestUserInput(parentRunId)` withholds it from a delegated child entirely. A hint that pushed the ask
would fight the tool that owns that decision. D3's "never a guess" is delivered as "never a subfolder you
leave implicit" plus the `artifact_ref` report, which makes the choice explicit and auditable; whether to ask
stays governed by the ask tool's description.

**NOT DONE, deliberately: no change to `AgentPlanner.BuildPlanMessages`.** Its instruction "include an
expectedArtifact only when the step will write files, naming exactly the files it will write, relative to the
working folder" (`AgentPlanner.cs:786`) is what `AgentVerifier`'s artifact probe depends on. Pushing a vault
reference into `expectedArtifact` would make the probe stat `<workspaceRoot>\sources\...` and report a false
NOT FOUND. See the open risk; the probe side is Slice 16's.

**Tests (Slice 11)**

- `tests/Pia.Wpf.Tests/Services/VaultTargetPolicyTests.cs` -> **`StepHintApplies_Theory`**:
  (null workspaceRoot, [create_source]) false; (`C:\ws`, null tools) false; (`C:\ws`, ["noop"]) false;
  (`C:\ws`, ["noop","create_source"]) true; ("", ["create_source"]) false. Tools built with
  `AIFunctionFactory.Create(() => string.Empty, name)`, the way `ChatSessionStepTurnTests.Spec` and the
  `DurabilityHarness` already build theirs. Plus
  **`StepHint_NamesCreateSourceAndSourcesAndArtifactRef`** asserting the constant contains `create_source`,
  `sources/` and `artifact_ref` and does NOT contain `request_user_input`.
- New file `tests/Pia.Wpf.Tests/Services/AgentStepInstructionTests.cs` ->
  **`Compose_WithNoWorkspace_IsTodaysStringExactly`**: with `workspaceRoot: null` the composed instruction
  equals `Execute step 1: do it. Expected: r.md ` + `ReReadHint` + ` ` + `RunScratchFolder.StepHint`. This is
  the byte-compatibility pin for the extraction.
- `tests/Pia.Wpf.Tests/Services/HeadlessTurnExecutorTests.cs` ->
  **`TheStepInstruction_CarriesTheVaultTargetHint_OnlyInAWorkspace`**, modelled on the existing
  `TheStepInstruction_TellsTheModelToReReadAClearedResult` (`:1687-1698`). Re-stub `h.Composer.PrepareTurn`
  **locally inside the test** (not the shared `DurabilityHarness`, which Slice 6 already reshaped) to return
  an `AssistantTurnSetup` whose `Tools` contains `create_source` with `SupportsTools` true, then run one step
  with `executor.Initialize(workspaceRoot: <temp dir>, ["write_file"], h.Provider)` and assert the captured
  `ChatRole.User` message contains `VaultTargetPolicy.StepHint`; a second case with `workspaceRoot: null`
  asserts `DoesNotContain`; a third with a tool list of `["noop"]` only asserts `DoesNotContain`. Also assert
  `ReReadHint` is still present, so the restructured tail did not drop a hint.
- `tests/Pia.Wpf.Tests/ViewModels/ChatSessionStepTurnTests.cs` ->
  **`TheLiveStepInstruction_CarriesTheVaultTargetHint_OnlyInAWorkspace`**, the parity twin, beside the
  existing `TheLiveStepInstruction_CarriesTheReReadHint` (`:126`). Extend the private `Spec(...)` factory
  (`:33-46`) with two optional trailing params (`string? workspaceRoot = null`, `string? extraToolName = null`)
  feeding `StepTurnSpec.WorkspaceRoot` and appending one extra `AITool` to `Tools`; assert the captured user
  message contains the hint with both halves present and does not with either half missing. Slice 15 extends
  the same helper — merge the params, do not duplicate them.

---

### Slice 12 — G1: carry an untruncated approval description to the render surface

**Files**

- `src/Pia.Wpf/Services/ToolApprovalArguments.cs`
- `src/Pia.Wpf/ViewModels/RunProgressViewModel.cs`
- `src/Pia.Wpf/ViewModels/AssistantViewModel.cs`

**The renderer.** `ToolApprovalArguments.cs` today is 46 lines: `MaxValueChars = 120`,
`MaxTotalChars = 400`, `Describe(FunctionCallContent)`, `Join(IReadOnlyList<string>)`, private `Cap`. Leave
all four members **byte-for-byte untouched** — they produce the envelope string that `AgentRunOrchestrator`
writes into `AgentRuns.ExtraJson` and that `AgentRunOrchestratorArmTests:116` and `UnattendedApprovalParkTests`
pin. `RunPauseEnvelope.ReadApprovalArgs` is not itself a truncation site; treating it as the thing to fix
would break those tests for nothing.

ADD below `Join`:

```csharp
internal const int MaxDetailValueChars = 4000;
internal const int MaxDetailTotalChars = 8000;
internal readonly record struct Detail(string Text, bool Shortened);
internal static Detail? DescribeDetail(string? argumentsJson);
```

`DescribeDetail` parses the persisted arguments object with `JsonDocument`; returns null for null/blank/
malformed input and for a root whose `ValueKind` is not `Object` — the same swallowing discipline
`RunPauseEnvelope` documents on all three of its readers. For each member in `EnumerateObject()` (document
order, never re-sorted): `raw = ValueKind == String ? GetString() ?? "" : GetRawText()`, so a numeric, bool,
array or object argument is **rendered** rather than dropped, unlike `Describe`'s `_ => null` arm;
`value = Cap(raw, MaxDetailValueChars)`; `shortened |= raw.Length > MaxDetailValueChars` — compare the **RAW**
length, because `Cap` returns max+1 chars and a raw of exactly max+1 would otherwise read as un-shortened;
`line = $"{name}={value}"`.

Greedy fit against a running budget initialised to `MaxDetailTotalChars`: if `line.Length > budget`, append
`$"{name}=…"` and set `shortened = true` **WITHOUT** decrementing the budget, so a short decisive argument
after a huge one still renders in full and **no argument is ever silently dropped** — the reader always sees
the complete key list. Otherwise append the line and subtract. Join with `'\n'`. Return null when no lines
were produced.

**The per-value cap is exactly half the total on purpose**: the FIRST argument can never consume the whole
budget, which is the failure mode that matters here because `write_file`'s `content` usually precedes `path`.

**Issue 1.1 is not only a LENGTH problem.** `Describe` renders only string-valued arguments, so the collapsed
line already understates a call with numeric, boolean, array or object arguments even far below 120 chars.
`DescribeDetail` must render non-string values via `GetRawText()` or "the full call" would still be partial.

**`RunProgressViewModel` changes.** Add `IAgentToolExchangeStore? toolCalls = null` as the new **LAST** ctor
parameter, after `INavigationService? navigation = null` (ctor at `:652-699`); field
`private readonly IAgentToolExchangeStore? _toolCalls;`. Null means no detail ever loads, i.e. the panel is
byte-identical to today. New members beside `ApprovalToolArguments` (`:155-175`):

```csharp
[ObservableProperty][NotifyPropertyChangedFor(nameof(HasApprovalDetail))]
[NotifyPropertyChangedFor(nameof(ShowApprovalDetail))] private string? _approvalDetailText;
[ObservableProperty] private bool _isApprovalDetailShortened;
[ObservableProperty][NotifyPropertyChangedFor(nameof(ShowApprovalDetail))]
[NotifyPropertyChangedFor(nameof(ApprovalDetailToggleLabel))] private bool _isApprovalDetailExpanded;

public bool HasApprovalDetail => ApprovalDetailText is not null;
public bool ShowApprovalDetail => IsApprovalDetailExpanded && HasApprovalDetail;
public string ApprovalDetailToggleLabel =>
    _localization[IsApprovalDetailExpanded ? "Run_ToolApproval_HideFullCall" : "Run_ToolApproval_ShowFullCall"];
[RelayCommand] private void ToggleApprovalDetail() => IsApprovalDetailExpanded = !IsApprovalDetailExpanded;
internal Task? ApprovalDetailLoadTask { get; private set; }
private static string? ApprovalParkTool(AgentRun run);
private async Task LoadApprovalDetailAsync(string toolName);
private void ApplyApprovalDetail(ToolApprovalArguments.Detail? detail, string toolName);
```

Extract the three-term park test `Project` inlines at `:1003-1006` into `ApprovalParkTool(AgentRun run)` and
call it from BOTH `Project` and `RefreshAsync`, so the two can never disagree. In `Project`, when
`ApprovalParkTool(run)` is null **also clear** `ApprovalDetailText` / `IsApprovalDetailShortened` /
`IsApprovalDetailExpanded` — both resume claims `SET ExtraJson=NULL`, so a cleared envelope is the reliable
end-of-park signal.

**MERGE ORDER NOTE — this is the one real F/G conflict.** Slice 2 inserted
`RefreshApprovalDerivation();` immediately after `:1009`. Slice 12 extracts `:1003-1006` into
`ApprovalParkTool` and adds its own clearing branch. **Slice 12 must preserve Slice 2's call**, and it must
still run after both approval assignments. Final shape of that region: `SyncSteps`, the three approval
assignments (now via `ApprovalParkTool`), the clearing branch, then `RefreshApprovalDerivation();`.

**The load kick**, in `RefreshAsync` after the existing `_uiContext.Post(_ => Project(run, children), null)`
(`:779`) and before the terminal-only workspace read: fire only when `_toolCalls is not null`,
`ApprovalParkTool(run)` is non-null, `!_approvalDetailRead`, and
`ApprovalDetailLoadTask is not { IsCompleted: false }`.

**LATCH ON SUCCESS, NOT ON ATTEMPT.** `_approvalDetailRead = true` is set inside the load only after a
matching row came back; a null row leaves it false so the next `RunChanged` retries. That is the difference
from `_settledTraceRead`, which guards a state that cannot un-happen: this row is written by Slice 7's code
path and its commit ordering against the `AgentRuns` row flipping to `WaitingForInput` is not visible from
here, so an attempt-latch would close on the first projection and the fold row would never appear. While
parked the run is stopped, so `RunChanged` is rare and the retry is free.

`LoadApprovalDetailAsync` mirrors `LoadTimelineAsync` (`:1701-1727`) exactly:
`await Task.Run(() => _toolCalls!.GetParkedCallAsync(_runId, toolName))` to keep the store's connection lock
and a possibly-512 K string off the dispatcher; a catch that `LogWarning`s the RUN ID ONLY and leaves the
panel with no detail; then a UI-thread apply through `_uiContext.Post`. `ApplyApprovalDetail` **re-checks on
the UI thread** that `IsToolApprovalPause` is still true and `ApprovalToolName` still equals the `toolName`
the load was issued for, and assigns nothing otherwise — a resume that landed mid-read posts `Project` (which
clears) ahead of the apply, and without the re-check the detail box would reappear on a Running run.

**Privacy.** The detail text is user content — never logged. The only permitted log line carries scalars (run
id, char count). Note that Kind 3/4 rows are DETOKENIZED, so this surface shows the user their own real
content, which is the point; it is local UI and reaches no provider.

**`AssistantViewModel`** (`:518-520`, the sole production construction site, positional): add
`IAgentToolExchangeStore? toolCalls = null` as the new last ctor parameter (after
`IFileDialogService? fileDialogService = null`), field `_toolCalls`, and pass it as the 13th positional
argument to `new RunProgressViewModel(...)` after `_navigationService`. Same trailing-and-defaulted discipline
that ctor already documents six times over.

`IAgentToolExchangeStore` living in `Pia.Services.Interfaces` is what lets a view model hold it:
`DependencyInjectionTests.ViewModels_MustNotInject_InfrastructureTypes` (`:169`) and `LayerDependencyTests`
forbid only `Pia.Infrastructure`, and `IAgentTimelineService` is the exact precedent.

**Tests (Slice 12)**

New file `tests/Pia.Wpf.Tests/Services/ToolApprovalArgumentsTests.cs` — the first tests this type has ever
had. `InternalsVisibleTo(Pia.Wpf.Tests)` is already set in `Pia.Wpf.csproj:71`.

- **`DescribeDetail_RendersEveryArgumentOnePerLine_IncludingNonStringValues`** — for
  `{"path":"a/b.md","count":42,"flags":[1,2]}`: `Text` splits on `'\n'` into exactly
  `["path=a/b.md","count=42","flags=[1,2]"]` in document order, `Shortened` false.
- **`DescribeDetail_CapsOneValueAtHalfTheTotal_SoALaterArgumentSurvives`** — a 20 000-char `content` followed
  by `path=x/y.md`: the content line is `MaxDetailValueChars + 1` chars ending in the ellipsis, the line
  `path=x/y.md` is present verbatim, `Shortened` true.
- **`DescribeDetail_NamesEveryArgumentEvenWhenTheTotalCapBites`** — three 4 000-char values: line 1 in full,
  lines 2 and 3 as `k2=…` / `k3=…` (key present, value dropped), `Text.Length` under
  `MaxDetailTotalChars + 16`, `Shortened` true.
- **`DescribeDetail_ReadsNullForAbsentMalformedOrNonObjectJson`** — null, `""`, `"   "`, `"not json"`,
  `"[1,2]"` and `"{}"` all return null.

New file `tests/Pia.Wpf.Tests/ViewModels/RunProgressViewModelApprovalDetailTests.cs`. Kept out of
`RunProgressViewModelSteeringTests.cs` and `RunProgressViewModelTimelineTests.cs` so Slice 2's rewrites and
this slice do not share a merge target.

- **`ToolApprovalPark_LoadsTheParkedCall_AndKeepsTheCappedLineAsTheCollapsedReading`** — envelope with the
  capped `args` member plus a store row whose `ArgumentsJson` carries a 20 000-char `content`. After
  `RefreshAsync` and awaiting `ApprovalDetailLoadTask`: `HasApprovalDetail` true, `ApprovalDetailText` contains
  the path and a `'\n'`, `IsApprovalDetailShortened` true, AND `ApprovalToolArguments` is still exactly the
  envelope's capped string. The last clause pins the collapsed line untouched.
- **`ApprovalDetail_RetriesOnTheNextProjection_WhenTheStoreHasNoRowYet`** — `GetParkedCallAsync` returns null
  on the first call and a matching row on the second; two `RefreshAsync` calls leave `HasApprovalDetail` true
  and the substitute `Received(2)`. Pins latch-on-success; an attempt-latch would leave the fold row
  permanently absent whenever Slice 7 commits the row after the run row flips.
- **`ApprovalDetail_LoadedAfterTheParkCleared_IsNotApplied`** — the store substitute blocks on a
  `TaskCompletionSource`; while it is in flight the run is re-read as `Running` with `ExtraJson` null and
  `RefreshAsync` runs again; then the gate is released. `HasApprovalDetail` stays false. Fails without the
  UI-thread re-check.
- **`ApprovalDetail_IsClearedWhenTheRunLeavesThePark`**.
- **`WithoutTheStore_ThePanelOffersNoApprovalDetail`** — VM built with `toolCalls: null` over the same parked
  run: `HasApprovalDetail` false, `ApprovalDetailLoadTask` null.
- **`ToggleApprovalDetail_FlipsTheExpansionAndTheLabel`** — with a detail loaded, `ShowApprovalDetail` false
  and the label is the show key (the stubbed localization returns the key); after
  `ToggleApprovalDetailCommand.Execute(null)`, `ShowApprovalDetail` true and the label is the hide key; and
  with no detail loaded `ShowApprovalDetail` stays false even when expanded.

---

### Slice 13 — G2: expand the run panel's approval line

**Files**

- `src/Pia.Wpf/Controls/Assistant/RunProgressPanel.xaml`
- `src/Pia.Wpf/Resources/Strings/ViewStrings.resx` + `.de.resx` + `.fr.resx`
- `tests/Pia.Wpf.Tests/Views/ViewAutomationIdTests.cs` (the row bump, same change)

**What stays.** The band's approval line at `:232-240` is untouched: `Text="{Binding ApprovalTargetLine}"`
(the localized "Affects {0}" over the 400-capped envelope string), `TextWrapping="Wrap"`,
`ToolTip="{Binding ApprovalToolArguments}"`, visible on `HasApprovalTarget`. That IS the collapsed reading.
The disclosure is a separate, self-collapsing affordance, so a park with no stored row renders the panel
exactly as today and no binding is duplicated (`RunProgressPanelParseTests` relies on single-occurrence
anchors).

**Where.** NOT in the band — the band's action column (`Grid.Column=3`) is `VerticalAlignment="Center"`, so a
220 px detail box in the band's text column would float Continue/Deny in the middle of a tall tinted bar.
Insert instead in the card BODY, inside the `IsCardExpanded` StackPanel, immediately BEFORE the
`<!-- Region D · steering note -->` Border at `:335-357`. The body already reads top-to-bottom B, skeleton, D,
C, and D is the block that asks the user to decide.

**Two elements.** (1) The fold row — a `Button` with `Style="{StaticResource RunFoldRowStyle}"`,
`Margin="14,10,14,0"`, `Command="{Binding ToggleApprovalDetailCommand}"`,
`AutomationProperties.AutomationId="Run_ApprovalDetailToggle"`, and
`Visibility="{Binding HasApprovalDetail, Converter={StaticResource BooleanToVisibilityConverter}}"`, containing
the same horizontal `StackPanel` shape as `Run_ShowEarlierSteps` (`:362-373`): a
`ui:SymbolIcon FontSize="12" Width="16" Margin="0,0,9,0"` whose `Symbol` comes from an inline Style (Setter
`ChevronDown24`, DataTrigger on `IsApprovalDetailExpanded` = True -> `ChevronUp24`, both already used in this
file's two toggle templates), plus `TextBlock Text="{Binding ApprovalDetailToggleLabel}"
FontSize="{StaticResource RunMetaSize}" Foreground="{DynamicResource TextMutedBrush}"`.

(2) The detail box — a `Border Margin="14,6,14,0" Padding="10,8" CornerRadius="6" BorderThickness="1"
Background="{DynamicResource SurfaceSunkBrush}" BorderBrush="{DynamicResource BorderBrush_}"
Visibility="{Binding ShowApprovalDetail, Converter=...}"` wrapping a StackPanel of a
`ScrollViewer MaxHeight="220" VerticalScrollBarVisibility="Auto" HorizontalScrollBarVisibility="Disabled"`
containing `TextBlock Text="{Binding ApprovalDetailText}" FontSize="{StaticResource RunMetaSize}"
TextWrapping="Wrap" FontFamily="{DynamicResource PiaMonoFont}" Foreground="{DynamicResource TextMutedBrush}"`,
then `TextBlock Style="{StaticResource RunTraceNoteStyle}" Margin="0,6,0,0"
Text="{loc:Str Run_ToolApproval_DetailShortened}" Visibility="{Binding IsApprovalDetailShortened,
Converter=...}"`.

Every resource named here (`RunFoldRowStyle`, `RunTraceNoteStyle`, `RunMetaSize`, `PiaMonoFont`,
`SurfaceSunkBrush`, `BorderBrush_`, `TextMutedBrush`, `BooleanToVisibilityConverter`) is already used earlier
in this same file, which the file's own comment at `:670-673` requires because an unresolved lookup throws
only at template instantiation.

**A `Button` + `[RelayCommand]`, NOT a `ToggleButton` and NOT a framework `Expander`.** This is
load-bearing, not taste: `RunProgressPanelParseTests.EveryControlTemplateApplies_UnderARealLayoutPass` pins
`Assert.Equal(4, sectionHeaders)` over `FindVisual<ToggleButton>(panel)` — the VISUAL tree, which realizes
Collapsed elements — and an `Expander`'s default template contains a `ToggleButton`, so either shape would red
that assert in a slice that otherwise touches no counted control. The panel also deliberately avoids the
framework `Expander` for its own stated reason (`RunProgressPanel.xaml:40-41`): the content has to stay in the
LOGICAL tree whether or not it is expanded, because `RunProgressPanelParseTests` walks it. The clickable fold
row plus a `Visibility`-bound `Border` IS this panel's disclosure idiom, which is what checklist step G2 means
functionally.

**Why these numbers.** Source: verbatim and uncapped in the store (Q1) — a `write_file` payload can be 512 K
chars (`FilesToolHandler.MaxWriteChars = 512*1024`). Envelope / collapsed line: unchanged 120 per value, 400
total. Panel display cap: 4 000 per value, 8 000 total (Slice 12). 8 000 wrapped chars in the panel's ~62-char
column is about 130 lines, roughly 2 600 px of formatted text, i.e. about ten 220 px viewport-heights of
deliberate, opt-in scrolling — enough that a path list, a prompt or a `remember` payload reads end to end, and
few enough that a single non-virtualizing `TextBlock` formats in the noise. A 512 K payload is ~8 500 lines:
it would answer no question a rail can ask and would put a multi-second measure inside the chat's message list
on every expand. The `ScrollViewer` is what bounds the CARD: without it the 8 000-char `TextBlock` adds
~2 600 px to the run card's height inside the transcript. `DiffHunkBuilder.MaxRows` behind `FileDiffCard`'s
`ScrollViewer MaxHeight="360"` is the house precedent for exactly this reasoning.

**Localization** — three new keys in all three resx files:

- `Run_ToolApproval_ShowFullCall` — EN "Show the full call"; DE "Vollstaendigen Aufruf anzeigen" (real
  a-umlaut); FR "Afficher l'appel complet".
- `Run_ToolApproval_HideFullCall` — EN "Hide the full call"; DE "Vollstaendigen Aufruf ausblenden"; FR
  "Masquer l'appel complet".
- `Run_ToolApproval_DetailShortened` — EN "Shortened for display — the run kept the whole call."; DE
  "Fuer die Anzeige gekuerzt – der Lauf hat den vollstaendigen Aufruf gespeichert."; FR "Abrege pour
  l'affichage – l'execution a conserve l'appel complet." **That last line is the point of the step**: the
  ellipsis in the original screenshot was source-side truncation with nothing saying so.

`LocalizationTests.AllTranslations_MustBeComplete` fails on a key present in one locale only, and
`AllXamlLocalizationKeys_MustExistInResources` / `AllCodeLocalizationKeys_MustExistInResources` cover the
`loc:Str` and `_localization["..."]` uses.

**Automation.** Id `Run_ApprovalDetailToggle` — a literal, because the control is not inside an
`ItemsControl` — with the panel's own `Run_` prefix and disjoint from every existing id under a prefix match
(`Run_CardToggle`, `Run_TimelineToggle`, `Run_ChildrenToggle`, `Run_ChildToggle_{0}`, `Run_ShowEarlierSteps`,
`Run_ShowLaterSteps`). Bump the row in `ViewAutomationIdTests.cs:80` in the SAME change:
`[InlineData(typeof(Pia.Controls.Assistant.RunProgressPanel), 22, 10, "PiaPersonaAvatar")]`. The walk collects
`ButtonBase`, so +1 inspected; the per-item floor stays 10 because the new id is literal and outside any
`ItemTemplate`. If the measured total comes out other than 22, set the row to the measured value — it is a
floor, so a stale number passes silently, which is exactly what the same-change rule exists to prevent.

`RunProgressPanelParseTests.EveryNonTemplatedBindingPath_ResolvesOnTheViewModelThatHostsThePanel` walks every
new binding: `ApprovalDetailText`, `ShowApprovalDetail`, `HasApprovalDetail`, `IsApprovalDetailShortened`,
`IsApprovalDetailExpanded`, `ApprovalDetailToggleLabel` and `ToggleApprovalDetailCommand` must all be PUBLIC
on `RunProgressViewModel` or the walk reports UNRESOLVED. `MinimumBoundPaths` is a floor, so no edit there.

**Tests (Slice 13)** — new file `tests/Pia.Wpf.Tests/Views/RunProgressApprovalDetailPanelTests.cs`,
`[Collection("WpfApplicationStatic")]`, over a real `RunProgressPanel` bound to a VM with the interpolating
localization factory. Find elements by binding path via `BindingPathWalker.FindLogical` + `PathOf`, the pattern
`ProbePlanApprovalCard` uses.

- **`TheFullCallFoldRow_AppearsOnlyWhenThereIsADetail_AndRevealsIt`** — before: both the Button and the Border
  are Collapsed (the Collapsed readings are the ones that bite, since `Visibility` defaults to Visible). After
  setting `ApprovalDetailText`: the Button is Visible, the Border still Collapsed. After executing the
  command: the Border is Visible and the bound `TextBlock` carries the text with its `'\n'` intact.
- **`TheExpandedDetail_ScrollsInsteadOfGrowingTheRunCard`** — an 8 000-char `ApprovalDetailText` (spaces every
  ~8 chars plus newlines, so it wraps like real arguments), expanded, then a real
  `Measure(640, infinity)` / `Arrange` / `UpdateLayout`. The `ScrollViewer`'s `ActualHeight` is <= 220, its
  content `TextBlock`'s `DesiredSize.Height` is far greater (proving the `ScrollViewer`, not merely a short
  string), and `ScrollableHeight > 0`.
- **`TheShortenedNote_ShowsOnlyWhenTheDisplayCapBit`**.

**Deferred deliberately, not overlooked**: no copy affordance. The detail is a `TextBlock`, so the full text
can be read but not selected. A read-only `ui:TextBox` would add a `TextBoxBase` to the
`ViewAutomationIdTests` count and caret/IME cost over 8 000 chars.

**Known gap, flagged rather than designed around**: the tool-approval Flow card has no route to the run panel.
Its text link approves (`ToolApprovalRunAction` -> `ResumeAsync`) and its bar is Approve/Deny, so "see the
whole call" is reachable only by opening the run some other way.

---

### Slice 14 — E1(a): seed each step with the artifacts already declared (data half)

Inert on its own — nothing reads the new state yet.

**Files**

- `src/Pia.Wpf/Services/RunContext.cs`
- `src/Pia.Wpf/Services/AgentRunOrchestrator.cs`

**What is already available and needs NO work.** (1) The planner's declaration for every COMPLETED step —
`CompletedStepSummary.ExpectedArtifact`, set by `RunContext.RecordStep` (`:152-162`) and re-seeded on resume
from the persisted `AgentSteps.ExpectedArtifact` column (`AgentRunOrchestrator.cs:943`). (2) The artifact a
completed step SAID it produced — `CompletedStepSummary.Outcome.ArtifactRef`, persisted into
`AgentSteps.ExtraJson` as `artifactRef` by `AgentRunService.RecordStepResultAsync` and re-seeded by
`StepExtraJson.ArtifactRefOf(s)` (`AgentRunOrchestrator.cs:944-946`). **Group E needs no new SQLite table, no
new column, and nothing from Slice 4's store.** E1's `Deps: C3` is an ORDERING dependency (de-duplicate a real
case, not a symptom), not a data one — do not block E waiting on the store.

**What is MISSING** is the declared artifacts of the steps that have NOT run yet. That is the half that closes
the observed defect: step 1 overshot into step 2's deliverable, and at step 1's instruction time the
produced-list is empty, so a prior-artifacts-only prompt would not have prevented it.

**Do NOT read `run.Plan` for it.** `run` is the argument captured once in `RunAsync` (`:141-149`) and its
`Plan` snapshot is stale after every `SafeReplaceSteps` (`:250`, `:293`) and after any paused-user plan edit.
Mirror the seam the codebase already uses for exactly this problem:
`ctx.SetSkippedTitles(await SafeSkippedTitlesAsync(...))` at `:283`, fed by `:1826-1841`.

**Steps**

1. `RunContext.cs`, immediately after `SetSkippedTitles` (`:197`) so the two replace-never-append accessors
   read together — plus a file-scope record struct after the class (a **record struct** is allowed in
   `Pia.Services` because `RecordTypes_MustNotLiveInTheServicesRootNamespace` filters on `!t.IsValueType`):

   ```csharp
   public readonly record struct PlannedStepArtifact(int Ordinal, string Artifact);   // file scope
   public IReadOnlyList<PlannedStepArtifact> PlannedArtifacts { get; private set; } = [];
   public void SetPlannedArtifacts(IReadOnlyList<PlannedStepArtifact> artifacts) => PlannedArtifacts = artifacts;
   ```

   One short comment on the setter only: it replaces rather than appends, because each seed is a fresh read of
   the persisted plan.
2. `AgentRunOrchestrator.cs`: add
   `private async Task<IReadOnlyList<PlannedStepArtifact>> SafePlannedArtifactsAsync(Guid runId,
   CancellationToken ct)` directly below `SafeSkippedTitlesAsync` (`:1841`), byte-for-byte the same
   failure-isolated shape: `GetAsync`, null -> `[]`,
   `catch (Exception ex) { _logger.LogWarning(ex, "Run bookkeeping (reading the planned artifacts of {RunId}) failed", runId); return []; }`.
   Filter `s.Status == AgentStepStatus.Pending && !string.IsNullOrWhiteSpace(s.ExpectedArtifact)`,
   `OrderBy(s => s.Ordinal)`, project.
3. Seed it from ONE new line in the drain loop, between `inflightStepId = step.Id;` (`:411`) and the
   `using (_logger.BeginScope("step {StepOrdinal}", ...))` block (`:423`):
   `ctx.SetPlannedArtifacts(await SafePlannedArtifactsAsync(run.Id, cts.Token).ConfigureAwait(false));`

   The placement is load-bearing three ways: it is AFTER the fan-out branch (which `continue`s or returns at
   `:323-407`), so a delegated group never pays for it; it is AFTER
   `SafeSetStepStatus(step.Id, Running)` (`:410`), so the `Pending` filter already excludes the current step;
   and it is per-step rather than per-dispatch because a user can edit a pending step's `ExpectedArtifact`
   while the run is paused — the same argument the code already makes for skipped titles at `:277-282`.
4. **LOG NOTHING on the happy path** — artifact names are user content and this seam has no sensitive channel.
   The warning arm carries only the run id.

**Tests (Slice 14)** — new file `tests/Pia.Wpf.Tests/Services/AgentRunPlannedArtifactSeedTests.cs`. Harness is
a trimmed copy of `AgentRunNudgeParityTests.HeadlessHarness` / `OrchestratorHarness`, per the ArtifactProbe
README's stated convention on duplicated harnesses.

- **`EachStep_SeesTheDeclaredArtifactsOfTheStillPendingSteps_AndNeverItsOwn`** — a 3-step plan through the
  real `AgentRunService` plus a capturing `IAgentTurnExecutor`: at ordinal 0, `ctx.PlannedArtifacts` holds
  steps 1 and 2's artifacts and not step 0's; at ordinal 2 it is empty.
- **`ThePlannedArtifactsAreReadFreshPerStep_NotOncePerDispatch`** — the capturing executor rewrites step 2's
  `ExpectedArtifact` from inside step 1's turn; step 2's dispatch sees the NEW value.
- **`AFaultingPlanReadLeavesThePlannedArtifactsEmpty_AndTheRunStillCompletes`** — an `IAgentRunService` wrapper
  whose `GetAsync` throws (the `FaultyRunService` shape at `AgentRunOrchestratorTests.cs:146`).
- **`ThePlannedArtifactSeed_PutsNoArtifactNameInTheLog`** — with a `CapturingLogger<AgentRunOrchestrator>`, no
  entry contains the artifact name.

---

### Slice 15 — E1(b): the prompt half, by widening the shared composer

**Files**

- `src/Pia.Wpf/Services/AgentStepInstruction.cs`
- `src/Pia.Wpf/Services/HeadlessTurnExecutor.cs` (one call site)
- `src/Pia.Wpf/ViewModels/Models/ChatSession.cs` (one call site)

`Compose` gains a trailing `RunContext ctx` parameter. `ctx` is already in scope at both call sites (verified:
`HeadlessTurnExecutor.ExecuteStepAsync` uses `ctx.AppendNudge(...)`, and
`ChatSession.BuildStepChatMessagesAsync` uses `ctx.Goal` and `ctx.AppendNudge(instruction)`). Final signature:

```csharp
internal static string Compose(int ordinal, string intent, string? expectedArtifact,
    string? workspaceRoot, IEnumerable<AITool>? tools, RunContext ctx);
```

New members:

```csharp
internal const int MaxSeededPerBlock = 6;
internal const int MaxSeededArtifactChars = 120;
internal const string ProducedHeader = "Deliverables this run has ALREADY produced (do not create any of these again under a different name — if one needs changing, write to the same path):";
internal const string ReservedHeader = "Deliverables RESERVED for later steps of this plan (another step produces them; do not produce them here):";
internal const string OwnDeliverableRule = "The artifact named above is the only new deliverable this step may create.";
private static List<string> Produced(RunContext ctx);
private static List<string> Reserved(RunContext ctx, int ordinal);
```

Body order becomes: `Execute step N: intent.`; `Expected: X` when non-empty;
`AppendBlock(ProducedHeader, Produced(ctx))`; `AppendBlock(ReservedHeader, Reserved(ctx, ordinal))`;
`" " + OwnDeliverableRule` **ONLY** when `expectedArtifact` is non-empty (a step that declares nothing must not
be told its nothing is the only deliverable); then `ReReadHint` + `RunScratchFolder.StepHint`; then Slice 11's
conditional `VaultTargetPolicy.StepHint`. `AppendBlock` emits `" {header} {a}; {b}; {c}."` and is a no-op on
an empty list, so a first step with no history composes to today's string plus `OwnDeliverableRule`.

`Produced`: `ctx.CompletedSteps.Where(c => c.Succeeded && c.Outcome?.Succeeded != false)` — **the `Succeeded`
filter is REQUIRED**, because `ctx.RecordStep(step, r)` at `AgentRunOrchestrator.cs:501` is unconditional and
records failures too (`AgentVerifier.OutcomeTag` has "failed, declared" and "failed, observed" arms). Seeding a
failed step's promised artifact as "already produced" would tell the next step not to create a file that does
not exist — worse than saying nothing. Then
`.Select(c => StepOutcomeStore.Clamp(c.Outcome?.ArtifactRef ?? c.ExpectedArtifact, MaxSeededArtifactChars))`
— reported ref PREFERRED over the declaration, because it is what actually got written — drop nulls,
`.Distinct(StringComparer.OrdinalIgnoreCase)`, `.TakeLast(MaxSeededPerBlock)`.

`Reserved`: `ctx.PlannedArtifacts.Where(p => p.Ordinal != ordinal).OrderBy(p => p.Ordinal)` — the ordinal
exclusion is belt-and-braces behind Slice 14's `Pending` filter and is the guard against forbidding a step from
doing its own job — then `Clamp`, drop nulls, `Distinct(OrdinalIgnoreCase)`, `.Take(MaxSeededPerBlock)`.

Reuse `StepOutcomeStore.Clamp` (`StepOutcomeSignal.cs:110-118`, `internal`, same namespace) rather than writing
a fourth flatten/trim/head-cap: it also flattens CR/LF/TAB, which is what stops a model-supplied artifact name
forging structure inside the instruction.

**The caps are not cosmetic.** `AgentContextCompactor` PINS the newest user message and charges it
`length / 4` against the input window (`AgentContextCompactor.cs:113-131` and the `pinnedCost` block at
`:137-150`). This slice lengthens exactly that message, so without the 6-entry / 120-char caps a 20-step run
pays several hundred pinned tokens on every step, on top of the budget the compactor is trying to protect.

**EXPLICITLY OUT OF SCOPE**, so an integrator does not "fix" it: `AgentRunOrchestrator.BuildChildGoal`
(`:1400-1408`) is a third site with the same `Expected:` shape and must NOT call the composer. Its own doc
comment says why — a child run plans its own decomposition of that work rather than executing one step of the
parent's plan, so a parent's produced/reserved lists are not its constraints.

**Tests (Slice 15)** — `tests/Pia.Wpf.Tests/Services/AgentStepInstructionTests.cs` (created in Slice 11):

- **`Compose_WithNoHistory_CarriesTheExpectedArtifactAndBothHints`** — an empty `RunContext` composes to
  today's string plus `OwnDeliverableRule`, and contains NEITHER header.
- **`Compose_PrefersTheReportedArtifactOverThePlannerDeclaration`**.
- **`Compose_OmitsAFailedStepsArtifactFromTheProducedList`** — with `Succeeded: false`, and separately with
  `Outcome.Succeeded == false`.
- **`Compose_ExcludesTheCurrentStepsOwnReservedArtifact`**.
- **`Compose_CapsBothBlocks_AtSixEntriesAndOneHundredTwentyCharsEach`** — 20 produced plus 20 reserved yield
  exactly 6 per block; a 400-char name truncates to 120 plus the ellipsis; and the whole composed instruction
  stays under a stated ceiling (`Assert.True(instruction.Length < 1600)`) so the compactor's pinned charge is
  bounded. **Do not raise the caps without re-reading this test.**
- **`Compose_FlattensNewlinesInASeededArtifactName`** — an artifact ref containing `"\n- step 9 declared:"`
  composes to a single-line entry with no CR/LF.
- **`Compose_WithNoExpectedArtifact_OmitsTheOwnDeliverableRule`**.
- **`Compose_DeduplicatesTheSameArtifactNamedByTwoSteps`** — `out/Report.MD` and `out/report.md` yield one
  entry.

Plus the two end-to-end parity facts:

- `tests/Pia.Wpf.Tests/Services/AgentRunPlannedArtifactSeedTests.cs` ->
  **`TheHeadlessStepInstruction_CarriesBothSeededBlocks`**.
- `tests/Pia.Wpf.Tests/ViewModels/ChatSessionStepTurnTests.cs` ->
  **`TheLiveStepInstruction_CarriesBothSeededBlocks`**, beside the existing `..._CarriesTheReReadHint`
  (`:124-145`) and Slice 11's twin. Proves both call sites go through the one composer.

**Watch list, not expected to break**: `tests/Pia.Wpf.Tests/Integration/Compaction/SyntheticTranscript.cs`
(`:273`, `:411`) and `AgentContextCompactorTests.cs:220` build step-instruction lookalikes by hand and assert
on `Execute step N` / `Expected: report.md` substrings, which `Compose` preserves verbatim. They are the first
place to look if a compaction test reddens.

---

### Slice 16 — E2: probe both artifact channels, and flag near-duplicate deliverables

**Files**

- `src/Pia.Wpf/Services/AgentVerifier.cs`
- `tests/Pia.Wpf.Tests/Integration/ArtifactProbe/README.md` (its "What this does NOT measure" bullet becomes
  false in the same commit)

**The open question, answered from the code.** Both hypotheses in the defect doc are wrong; it is a third
thing — a whole unprobed channel.

- The resume does NOT lose the declared ref. `SafeSeedResumeContext` passes it through explicitly at
  `AgentRunOrchestrator.cs:943` (`s.ExpectedArtifact, FromEarlierSegment: true,`), sourced from the persisted
  `AgentSteps.ExpectedArtifact` column, and
  `AgentVerifierTests.VerifyAsync_ResumedRun_SeededStepIsPresentedAsExecuted_AndItsArtifactIsProbed`
  (`:507-536`) already asserts `declared: early.md → found (512 B, modified ` for exactly that shape and
  passes today.
- The probe does NOT de-duplicate across steps. `ProbeDeclarations` is a plain
  `foreach (var c in declared)` (`AgentVerifier.cs:394`) emitting one fact line per declaration; the only
  de-dup anywhere is inside `FileCandidates` (`:526-528`), OrdinalIgnoreCase, within ONE declaration.
- Therefore `declared=1` means exactly one of the three `ctx.CompletedSteps` entries had a non-blank
  `ExpectedArtifact` — the filter at `:267` is `Where(c => !string.IsNullOrWhiteSpace(c.ExpectedArtifact))`,
  and `AgentPlanner`'s schema tells the planner to "Omit when the step produces nothing checkable"
  (`AgentPlanner.cs:159`).
- `artifactReported=True` is a DIFFERENT FIELD: `!string.IsNullOrWhiteSpace(claim?.ArtifactRef)`
  (`HeadlessTurnExecutor.cs:643-646`), i.e. the step's own `emit_step_result` `artifact_ref`. The verifier
  renders it as prose only (`produced: {c.Outcome.ArtifactRef}` at `AgentVerifier.cs:205`) and **never probes
  it**. `tests/Pia.Wpf.Tests/Integration/ArtifactProbe/README.md:27` states this as a known gap in its own
  words: "Nothing here touches the artifact a step reports about *itself*. That is a different channel, and it
  is not probed at all."

So `declared=1 probed=1 found=1` alongside three `artifactReported=True` lines is fully consistent with a
healthy resume and a non-de-duplicating probe. RESIDUAL, and it does not affect the fix: which of the three
steps carried the one declaration cannot be established from this machine — `%LOCALAPPDATA%\Pia\history.db`
has 0 rows in `AgentRuns`/`AgentSteps` and `%LOCALAPPDATA%\Pia\runs` is empty, so run `9c942a8e` is gone, and
plan text is `SensitiveDebug` so it is absent from the log.

**The change, all in `AgentVerifier.cs`**

1. New private types beside `ArtifactProbeTally` (`:369-376`):
   `private readonly record struct ProbeTarget(int Ordinal, string Title, string? Declared, string? Reported, bool ReportedSameAsDeclared);`
   and
   `private readonly record struct FoundArtifact(int Ordinal, string Candidate, string Resolved, long Size);`.
   Widen the tally with `Reported`, `ReportedSame` and `DupPairs`. It stays a `readonly record struct`, so the
   existing `return (null, default, fallback)` at `:294` keeps compiling.
2. `TryBuildArtifactFactsAsync` (`:262`) — replace the filter at `:267` with a target projection:
   `var targets = ctx.CompletedSteps.Select(BuildTarget).Where(t => t.Declared is not null || t.Reported is not null).ToList();`
   plus `private static ProbeTarget BuildTarget(CompletedStepSummary c)`: `Declared` = trimmed
   `ExpectedArtifact` or null; `Reported` = trimmed `Outcome?.ArtifactRef` or null, then set to null with
   `ReportedSameAsDeclared = true` when it equals `Declared` OrdinalIgnoreCase (one channel, one stat call).
   Rename the local `declared` to `targets` through the method; the two skip messages at `:281` and `:304`
   keep their shape with the noun changed to "step(s) with a declared or reported artifact" (nothing asserts
   those strings).
3. `ProbeDeclarations(string root, List<ProbeTarget> targets)` — extract today's inner per-declaration body
   (`:400-441`) into
   `private static string ProbeOneDeclaration(string root, string declaration, ProbeCounters counters, List<FoundArtifact> found, int ordinal)`
   returning ONLY the outcome text, so both channels share one body. `private sealed class ProbeCounters`
   holds the mutable ints (a class rather than a `ref struct` so the extraction stays a two-line change at
   each call). Render ONE line per target, **declared half first** — that ordering is also the probe-budget
   priority, so today's planner-declaration coverage can never shrink:

   - `- step {N} "{Title}" declared: {D} → {out}`
   - `- step {N} "{Title}" declared: {D} → {out}; reported: {R} → {out}`
   - `- step {N} "{Title}" reported: {R} → {out}`

   Built with `string.Join("; ", halves)`, so a **declared-only line is BYTE-IDENTICAL to today's** and every
   existing `Assert.Contains("declared: … → …")` survives.
4. **CAPS — do NOT touch `MaxProbedPaths = 12`** (`:238`). Raising it would break
   `VerifyAsync_ManyDeclarations_AreBounded_ByProbeAndReportCaps` (`:236-249`),
   `VerifyAsync_ProbeLine_SeparatesTheReportCapFromTheProbeBudget` (`:276-297`),
   `VerifyAsync_ProbeLine_CountsCandidatesNotDeclarations` (`:299-320`) and
   `DeclarationCorpusReplayTests.ProbeBudget_Exhausted_...` (`:109`) for coverage only past 12 candidates,
   where behaviour already degrades with a stated arm. All four use contexts with no `Outcome`, so at 12 they
   stay byte-identical. `MaxReportedDeclarations = 20` (`:239`) now caps STEP LINES — rename it
   `MaxReportedSteps` (private, cosmetic, optional) and count `OverReportCap` in TARGETS (declared plus
   reported halves of the skipped steps), which keeps the existing "(N further declared artifact(s) not
   probed ...)" sentence truthful and keeps `fileShaped + notFileShaped + overReportCap == declared + reported`
   exact.
5. `Probe` (`:469-487`) — widen the return to `(string Text, ProbeOutcome Kind, string? Resolved, long Size)`;
   the `Found` arm supplies `resolved` and `file.Length`, every other arm `(null, 0)`. `ProbeOneDeclaration`
   appends a `FoundArtifact` for each `Found`.

6. **NEAR-DUPLICATE DETECTION** —
   `private static List<string> DuplicateFactLines(string root, List<FoundArtifact> found)`, run after the
   target loop, all metadata (no file contents read).
   `private const int MaxDuplicatePairs = 3; private const double MinSizeRatio = 0.5; private const int MinSharedTokenChars = 4;`
   A pair (a,b) is flagged only when ALL hold — every conjunct kills a real false-positive class, and the miss
   direction is the chosen one:

   1. both probed `Found` (so both files exist);
   2. `a.Ordinal != b.Ordinal` (a step legitimately producing two files is never flagged);
   3. `!string.Equals(a.Resolved, b.Resolved, OrdinalIgnoreCase)` (two spellings of ONE file is one file);
   4. same `Path.GetExtension`, OrdinalIgnoreCase;
   5. `Math.Min(sizes) / (double)Math.Max(sizes) >= 0.5`, with `Max == 0` skipped;
   6. at least one shared name token — `NameTokens(candidate)` takes
      `Path.GetFileNameWithoutExtension`, splits on `!char.IsLetterOrDigit` (Unicode-aware, so
      "urlaubsuebersicht" stays one token) and keeps runs of >= 4 chars into a
      `HashSet<string>(StringComparer.OrdinalIgnoreCase)`;
   7. NEITHER is under the scratch folder —
      `RunScratchFolder.Contains(Path.GetRelativePath(root, f.Resolved))`. It **MUST** be the root-relative
      path, not the candidate token and not the absolute path: `RunScratchFolder.Contains`
      (`RunScratchFolder.cs:31-38`) documents itself as taking a path relative to the working root, so feeding
      it an absolute path silently no-ops. Wrap `GetRelativePath` in a try/catch returning `string.Empty`.

   **The fact line, verbatim** (two implementers will otherwise write two different strings, and
   `TheDuplicateFileNames_NeverReachTheReleaseVisibleTallyLine` needs a literal to match on):

   ```
   - possible duplicate deliverable: step {A} "{candA}" ({sizeA} B) and step {B} "{candB}" ({sizeB} B) — same file type, similar size, overlapping names
   ```

   `{A}`/`{B}` are `Ordinal + 1`, matching the existing `- step {N} "{Title}"` lines. Both candidate names go
   through the existing `Truncate(Flatten(...))`.

   **Validated against the real pair**: `Urlaubsuebersicht_2026_pro_Mitarbeiter.md` (5 245 B, step 1) vs
   `Mitarbeiter_Urlaubszeiten_Zusammenfassung.md` (6 776 B, step 2) — different steps, both `.md`, ratio
   5245/6776 = 0.774 which is >= 0.5, shared token `mitarbeiter` (11 chars). It fires, **conditional on**
   step 1's `artifact_ref` naming the 5 245-byte file (step 1 also wrote 907 B and 1 232 B, and only the ref it
   declared is probed).

7. **The hint sentence, verbatim.** It goes INSIDE the facts string (appended by `ProbeDeclarations` after the
   pair lines), not into `BuildVerifyMessages`, so it appears only when a pair exists:

   ```
   A "possible duplicate deliverable" line is a HINT, not a finding: two steps each produced a similarly named and similarly sized file of the same type. Decide from the step results whether the plan called for both, or whether one step re-produced another step's deliverable under a new name.
   ```

   Same non-automatic-failure shape as the NOT FOUND sentence at `:139`.
   **`VerdictResult` is NOT touched and no mechanical fail is added**: the class doc (`:26-29`) guarantees the
   probe can never itself fail a verdict because the LLM still renders it, and `SafeVerify` (`:903-920`)
   degrades to ACCEPT on any fault. A false duplicate costs the critic one sentence to dismiss, never a failed
   run.
8. The release-visible tally line (`:309-312`) gains `reported=`, `reportedSame=` and `dupPairs=`.
   **`declared=` KEEPS its meaning** (planner declarations) so an existing log reader does not silently
   re-interpret it. Counts only — the pair's file names ride the prompt and the existing
   `SensitiveDebug` facts line (`:314`) and nothing else.

**Tests (Slice 16)** — `tests/Pia.Wpf.Tests/Services/AgentVerifierTests.cs`:

- **`VerifyAsync_ReportedArtifact_IsProbed_EvenWithNoPlannerDeclaration`** — a step with
  `Outcome.ArtifactRef="out/x.md"` (file on disk) and `ExpectedArtifact=null` now yields the block at all, with
  the reported half found in `LastPrompt` and `reported=1` in `ProbeLine`. The direct regression pin for the
  open question: today the block is omitted entirely.
- **`VerifyAsync_ResumedRun_ReportedArtifactOfASeededStep_IsProbed`** — `SeedCompletedSteps` with
  `FromEarlierSegment: true` and Outcome-only, mirroring what `SafeSeedResumeContext` builds at
  `AgentRunOrchestrator.cs:944-946`. Pins that the resume channel carries BOTH artifact fields.
- **`VerifyAsync_ReportedArtifactEqualToTheDeclaredOne_IsProbedOnce`** — one arrow, no reported half,
  `probed=1`, `reportedSame=1`, `reported=0`. No doubled stat call.
- **`VerifyAsync_DeclaredAndReportedDiffer_RenderOnOneLineDeclaredFirst`** — the declared half comes first
  (probe-budget priority) and both share one step line.
- **`VerifyAsync_ProbeLine_TalliesFoundMissingAndNonFileDeclarations`** — **EDIT** of the existing test at
  `:252-272`, which asserts the ENTIRE tally string. New expectation adds `reported=0 reportedSame=0` after
  `declared=4` and `dupPairs=0` at the end. Keep its `Assert.DoesNotContain("report.md", ProbeLine)` guard.
- **`VerifyAsync_ProbeLine_KeepsTheTargetInvariant_AcrossBothChannels`** — with both channels populated:
  `fileShaped + notFileShaped + overReportCap == declared + reported`, and
  `found + notFound + folder + unresolvable + uninspectable == probed`. The existing
  `VerifyAsync_ProbeLine_CountsCandidatesNotDeclarations` needs no edit, since `reported=0` there.

New file `tests/Pia.Wpf.Tests/Integration/ArtifactProbe/NearDuplicateDeliverableTests.cs`:

- **`TheObservedPair_TwoStepsOneDeliverable_IsFlagged`** — the real pair above, both files written to the
  throwaway root. One duplicate line naming both files and both step numbers, `dupPairs=1`, and the HINT
  sentence in the same System prompt.
- **`ALegitimateSecondArtifact_IsNeverFlagged`** — `[Theory]` over the six false-positive classes each conjunct
  kills: different extension; no shared 4-char-or-longer token; size ratio below 0.5; both files declared by
  the SAME step; one file named twice by two steps under different spellings resolving to the same path; and a
  scratch-folder pair. Every row asserts no duplicate line and `dupPairs=0`.
- **`AFlaggedDuplicate_NeverChangesTheVerdict`** — with the duplicate present and the fake emitting
  `passed=true`, `VerifyAsync` still returns Passed with the model's own reason.
- **`TheDuplicateFileNames_NeverReachTheReleaseVisibleTallyLine`** — `ProbeLine` contains `dupPairs=1` and
  neither German filename.
- **`TheDuplicateFactsAreLoggedThroughTheSensitiveChannelOnly`** — source-text scan of `AgentVerifier.cs`, the
  house pattern from `AgentVerifierTests.TheProbeRootFallbackIsLoggedThroughTheSensitiveChannelOnly`
  (`:585-598`): no plain logger line mentions "duplicate", and the only carrier of the pair text is the
  `SensitiveDebug` artifact-probe-facts call.

**Also in this slice**: rewrite the "What this does NOT measure" bullet at
`tests/Pia.Wpf.Tests/Integration/ArtifactProbe/README.md:27` — "Nothing here touches the artifact a step
reports about *itself*. That is a different channel, and it is not probed at all" becomes false — and add the
two new outcome-arm rows (the reported half, and the possible-duplicate line) to that README's "Outcome arms
and who pins them" table.

**Rejected guard, and the reason matters.** A "no explicit dependency between the two steps" conjunct must NOT
be added: the observed pair is a summarize step following an extract step — almost certainly linked by
`DependsOnJson` — so that guard would suppress the exact case this slice exists to catch. It would also require
widening `CompletedStepSummary` with a `DependsOn` member plus `RunContext.RecordStep` and
`SafeSeedResumeContext`, for a discriminator that points the wrong way.

**Promotion is not available to the verifier.** `SafePromote` runs AFTER `SafeVerify`
(`AgentRunOrchestrator.cs:866-869`), so at verify time nothing is promoted and the promoted-file list cannot be
a source. The check therefore only sees artifacts a step NAMED through one of the two channels — which is why
the defect doc's "both were promoted" cannot be the discriminator.

**Extraction hazard.** Moving ~40 lines of dense counter bookkeeping into `ProbeOneDeclaration` is the risky
part. `probed >= MaxProbedPaths` is checked **twice** today (once per declaration at `:411`, once per candidate
at `:421`) and **both checks must survive the extraction** or the budget silently stops binding.

**What "probe EVERY declared artifact" can honestly mean.** `MaxProbedPaths = 12` is a deliberate bound on a
verify turn's filesystem work with a stated "not probed (probe budget reached)" arm. It stays at 12; the
declared half is ordered before the reported half within each step line so today's coverage never shrinks. The
checklist's phrase is therefore true up to the cap, exactly as it already was.

---

## 4. Shared-file edit order

Every file touched by more than one slice, in the order it must be edited, with whether the later edit depends
on the earlier one's shape.

### `src/Pia.Wpf/Infrastructure/SqliteContext.cs`

**Order: Slice 4 only.** The merged DDL from section 2 is written ONCE, with group B's columns
(the `ParkedCall`/`WithheldCall` kinds, `DisplayArgs`, `ReplayedAt`, `SupersededAt`) present from day one. That
is the whole point of merging: Slices 7-9 add no columns and need no `MigrateSchema` entry. If an implementer
finds themselves adding a column in Slice 7, the merge was done wrong.

### `src/Pia.Wpf/Services/BackgroundAssistantTurnRunner.cs`

**Order: Slice 1, then 5, then 7, then 8, then 9.** Dependencies:

- Slice 1 adds `dispatch.Stop?.RequestStop();` as the FIRST statement of the withheld-because-parked arm
  (`:474`), the withheld-because-asking arm (`:489`) and `case ToolGateOutcome.Park:` (`:611`), and deletes one
  false clause from the comment at `:468-473`.
- Slice 5 touches a DIFFERENT method (`RunExchangeAsync`: one trailing optional param, two lines in the
  `case ToolRoundExchange` arm). No interaction.
- Slice 7 adds `approvals.Record(BuildParkedCall(...));` to two of the same three arms Slice 1 touched, plus
  one new private static helper. **Same lines, different statements — a textual merge only.** Slice 7 must not
  move the arm bodies without carrying Slice 1's lines.
- Slice 8 adds one new public `ReplayToolCallAsync`, forwarding to the existing `HandleToolCallAsync`. It
  depends on Slice 1's shape only in that `new ToolDispatchContext(round)` must stay source-compatible with the
  optional trailing param — it is, and a replay must NOT pass a stop signal.
- Slice 9 changes no code here beyond what Slice 7 wrote.

### `src/Pia.Wpf/Services/HeadlessTurnExecutor.cs`

**Order: Slice 5, then 7, then 8, then 11.** The highest-collision file in the plan.

- **Slice 5** adds the ctor param and field, `ExchangeScope`, the trailing param on `RunExchangeStepAsync` plus
  its three call sites, the re-seed with `ReadCarriedAsync` and `CarriedToolExchanges` in `BeginRunAsync`, the
  seal after the `_persisted.Add` block, and the purge in `EndRunAsync`.
- **Slice 7** adds `PersistParkedCallsAsync` and awaits it from both park exits. **Depends on Slice 5** for the
  ctor param and `_exchangeStore` — ONE parameter total, not two.
- **Slice 8** adds `_approvedTool` and `_replayAttempted`, the trailing `Initialize` param,
  `ReplayApprovedParkedCallsAsync`, `StepAmbient()` extracted from `RunExchangeStepAsync:510-511`, and one
  awaited line in `ExecuteStepAsync`. **Depends on Slice 7** for the rows to exist.
- **Slice 11** deletes `BuildInstruction` (`:834-840`) and rewrites the `ExecuteStepAsync:353` call to
  `AgentStepInstruction.Compose(...)`. Slice 8 inserted its replay line into the SAME method a few lines above;
  both are single statements, and the replay line stays ABOVE the `return await RunExchangeStepAsync(...)`.
- Slice 15 touches only the `Compose(...)` argument list at `:353`, adding `ctx`.

### `src/Pia.Wpf/Services/ToolApprovalStore.cs` and `ToolApprovalArguments.cs`

**No B/G collision materializes.** Slice 7 edits `ToolApprovalStore.cs` only (nested `ParkedCall` record,
`Record`, `RecordedCalls`, `DroppedRecords`, two cap constants, one trimmed class remark; `Park` untouched).
Slice 12 edits `ToolApprovalArguments.cs` only (appends `DescribeDetail` and its two constants below `Join`;
the existing 120/400 constants, `Describe`, `Join` and `Cap` untouched) — group B *calls* `Describe` but does
not edit that file. Different files, no order constraint. Slice 2 reads `PendingToolName`'s first-call-wins
rule and is unaffected by either.

### `src/Pia.Wpf/ViewModels/RunProgressViewModel.cs`

**Order: Slice 2, then Slice 12.** The one real line-level conflict in the plan.

- Slice 2 inserts `RefreshApprovalDerivation();` immediately after `:1009` and rewrites `ApplyTimelineAsync`,
  `ApplyDecisionSummary`, `Project(AgentTimelineEvent, bool)` and `ApplyChildTimelineAsync`, deletes
  `Severity(ToolGateDecision)` and adds `SeverityForKey`, `RowLabelKey`, `LiveParkRowId`, `RenderTimelineRows`,
  `RefreshApprovalDerivation` and `DecisionCategories`.
- Slice 12 extracts `:1003-1006` into `ApprovalParkTool(AgentRun)`, adds a clearing branch when the park is
  gone, adds the ctor param and the six detail members, and adds the load kick in `RefreshAsync`.
- **Slice 12 must preserve Slice 2's `RefreshApprovalDerivation();` call and keep it last in that region.**
  Final order inside `Project(AgentRun, ...)`: `SyncSteps(run.Plan)`, the three approval assignments (now via
  `ApprovalParkTool`), the detail-clearing branch, `RefreshApprovalDerivation();`.
- Otherwise disjoint: Slice 2 works around `:1734-2107`, Slice 12 around `:155-175`, `:652-699` and `:1701+`.

### The step-instruction composition site

`HeadlessTurnExecutor.BuildInstruction` (`:834-840`) plus the inline twin in
`ChatSession.BuildStepChatMessagesAsync` (`:982-987`). **Order: Slice 11, then Slice 15.** Slice 11 CREATES
`AgentStepInstruction.Compose(ordinal, intent, expectedArtifact, workspaceRoot, tools)` and deletes both
duplicated builders. Slice 15 ADDS a trailing `RunContext ctx` parameter and the two artifact blocks. The later
edit depends entirely on the earlier one's shape; the other order means extracting the same two builders twice.
`ChatSession.cs` is otherwise untouched by this plan — Slice 5 does not edit it, and the live twin's
`_stepToolExchanges` splice at `:966-974` is adjacent, not overlapping.

### `src/Pia.Wpf/Bootstrapper.cs`

**Slice 4 only.** ONE line. Slices 7, 8 and 12 all consume the same registration and add nothing.

### `src/Pia.Wpf/Resources/Strings/ViewStrings.resx` and its `.de` / `.fr` siblings

**Order: Slice 2, then Slice 13.** Slice 2 adds two keys; Slice 13 adds three. Different lines in the same
three files; expect a trivial merge. Slice 2's rewrite of
`LocalizationTests.EveryDecisionPillKeyResolvesInAllThreeLocales` is not touched by Slice 13.

### `tests/Pia.Wpf.Tests/Services/UnattendedApprovalParkTests.cs`

**Order: Slice 1, then 7, then 8, then 9.** Slice 1 upgrades `DriveWithToolCall` (`:1005-1030`) to pass a real
`ToolLoopStopSignal` and adds `StopAfterFirstCall` to `ToolProbe` (`:872`). Slice 7 extends `Build` (register
the store, pass it to the launcher ctor, add `firstContent`). Slice 8 adds `faultOnExecute`. Slice 9 converts
`DriveWithToolCall` to a per-dispatch script queue — **that conversion must keep dispatching the same-round
second call unconditionally**, or Slice 1's fact and every existing `secondToolName` fact break.

### `tests/Pia.Wpf.Tests/Services/HeadlessTurnExecutorTests.cs`

**Order: Slice 5, then 6, then 11.** Slice 6 owns the `DurabilityHarness` change — hoist `Plugins` and
`Permissions` out of `NewExecutor` into harness fields, add `Exchanges`, add `ArmApprovalPark`. **Land it once,
there.** Slice 11 adds one test that re-stubs `h.Composer.PrepareTurn` LOCALLY rather than touching the shared
harness again.

### `tests/Pia.Wpf.Tests/ViewModels/ChatSessionStepTurnTests.cs`

**Order: Slice 11, then Slice 15.** Slice 11 extends the private `Spec(...)` factory (`:33-46`) with
`workspaceRoot` and `extraToolName`; Slice 15 adds its parity test against the same helper. Merge the params,
do not duplicate them.

### `tests/Pia.Wpf.Tests/Views/ViewAutomationIdTests.cs`

**Slice 13 only** — the `RunProgressPanel` row `(21, 10)` becomes `(22, 10)`. The `FlowView` row `(10, 10)` is
deliberately unchanged by Slice 3.

### `src/Pia.Wpf/Services/AssistantChatRetentionService.cs`

**Slice 4 only.** One line beside the existing `PruneTimelineAsync` call site (`:116-118`).

### `tests/Pia.Wpf.Tests/Services/AgentToolExchangeStoreTests.cs`

**Order: Slice 4, then 7, then 8, then 9.** Each later slice appends its own facts; nobody rewrites another's.
Kept deliberately separate from `UnattendedApprovalParkTests.cs` so store-level and launcher-level facts are
not one merge target.

---

## 5. Resolved contradictions

Every claim a designer or a source doc raised, checked against the code, with the verdict. **The code wins
everywhere below; nothing follows the doc against the code.**

1. **"`ToolDispatchContext` gains a settable stop flag" (checklist A1) — FOLLOW THE CODE.**
   `IAiClientService.cs:24` declares `public readonly record struct ToolDispatchContext(int Round)`. A handler
   receives a by-value copy, so any mutation is invisible to the loop, and `readonly` forbids the setter
   outright. Fixed by a REFERENCE-TYPE field (`ToolLoopStopSignal`), not a settable one. Slice 1.
2. **Q3's "a parked call is just a call with no result" — FOLLOW THE CODE.** The Park arm at
   `BackgroundAssistantTurnRunner.cs:611-626` RETURNS A STRING, which `AiClientService` wraps in a
   `FunctionResultContent` and `Capture` snapshots as a complete pair. "No result" cannot be the
   discriminator; the table carries an explicit `Kind`. Sections 1 and 2.
3. **Q3's "one new local table for both" — HONOURED, but with TWO WRITERS.** Not taste:
   `TokenizingAiClientService.WrapToolHandler` hands the gate a detokenized copy and tokenizes the result
   before the loop sees it, so B's rows and C's rows are different content for the same call. B cannot replay
   from a C row. Section 1.
4. **Q1's "FK-cascaded off `AgentRuns` so it is purged with the run" — OVER-PROMISES; CODE WINS.** There is no
   `DELETE FROM AgentRuns` anywhere in `src/`; the only cascade into `AgentRuns` comes from `AssistantChats`.
   Closed by the four-mechanism purge rule in section 2 — the terminal `PurgeRunAsync` is what actually makes
   "purged with the run" true.
5. **Q1's "FK-cascaded" cannot extend to `StepId` — CODE WINS.** `AgentRunService.ReplaceStepsAsync` runs
   `DELETE FROM AgentSteps WHERE RunId=@RunId` on every replan, so a cascading `StepId` would wipe the payload
   of steps that already ran and a non-cascading one would throw inside a swallowing `Safe*` wrapper. `StepId`
   is a plain nullable column, the same call `AgentTimelineEvents.StepId` already documents.
6. **Q2's "replay OR seed" as alternatives — NOT ALTERNATIVES.** Replaying without seeding leaves the model
   unable to see that its call ran, so it reissues and the side effect happens twice. Slice 8 does both: replay
   for the effect, seed for the model's first view.
7. **B3's "the second and later calls must survive the same way" — CANNOT mean "replayed by the same Continue
   press".** `ApplyToolApprovalDecisionAsync` appends exactly ONE tool name to the grant set, so a Continue for
   `write_file` never authorizes `create_source`; replaying it would execute a tool the human was never asked
   about. It means persisted verbatim now, replay-eligible when that tool is itself approved — which needs the
   `SupersededAt` rule the checklist does not mention. Slice 9.
8. **B3 must NOT extend replay to a withheld call of an ALREADY-GRANTED tool.** The withheld-because-parked arm
   fires for granted calls too, and
   `UnattendedApprovalParkTests.AGrantedCallAfterThePark_DoesNotRun_AndIsNotReplayedByTheResume` pins that such
   a call runs exactly once, in the re-run. A grant-set-based predicate turns that test red and double-creates
   a todo; the `ToolName == approvedTool` predicate keeps it green. Slices 8 and 9.
9. **The defect doc's "`ToolApprovalStore` records one string — the tool name" — NARROW.** It also accumulates
   `PendingToolArguments` (the capped display strings) and `ParkedCalls`, and `ParkedCalls` counts WITHHELD
   calls of other tools too, because the withheld arm calls `Park(pending.ToolName, ...)`. So the reported
   run's log line "(2 parked call(s))" was one park plus one withhold, not two parks. Consistent with
   first-wins `PendingToolName`, but the count is not a count of parks.
10. **The defect doc's issue-4 framing — INCOMPLETE.** The same loop also feeds `TimelineExceptionBadge` and
    `TimelineExceptionSeverity`, i.e. the collapsed-header badge the reader sees WITHOUT expanding. Fixing the
    count fixes both; a design that touched only the pill list would have left the badge wrong. Slice 2.
11. **The defect doc's "its count is capped at 1" — IMPLEMENTED AS STRUCTURE, NOT A CAP.** A cap would be a
    clamp over a wrong number. `LiveParkRowId` yields at most one row id, which is sound only because the gate
    writes exactly ONE `ParkedForApproval` row per park (`if (parked)`).
12. **The checklist's F3 invariant "Awaiting <= 1 while parked" is VACUOUS as an assertion** — it passes when
    the pill never appears, which is exactly the ordering bug Slice 2 fixes. Every test asserts `== 1` on a
    parked run.
13. **Q4 is HONOURED, and the premise behind its hard question is FALSE under a run workspace.** "A working
    subpath narrows root, so a Vault folder could sit above or below it" cannot happen: whenever
    `WorkspaceRoot` is set, `WorkingSubpath` is null, stated independently at `HeadlessTurnExecutor.cs:510`
    and `:205-206`, at `ChatSession.cs:737`, and in `StepTurnSpec.WorkspaceRoot`'s doc. So `VaultRootFor(root)`
    is the ONE reachable vault subtree, and anchoring there is exact rather than a compromise. Slice 10.
14. **The task brief's "built-in plugin configs load from CODE, not from the DB" — TRUE OF THE READ PATH
    ONLY.** `PluginService.LoadPersistedPlugins:123` does skip `PreloadedPluginIds`, but the skip in
    `ApplyServerPluginsAsync:349` is inside the DELETIONS loop only. The upsert loop below it does
    `_pluginConfigs[plugin.Id] = plugin` (`:369`) and then `existing.ApplyServerMetadata(plugin)` (`:377`),
    which overwrites a built-in handler's system-prompt addition from the server's ConfigJson. Memory, todo and
    reminder ARE server-seeded per the class doc, so the already-written vault sentence at
    `BuiltInPluginDefaults.cs:45` can be replaced at runtime by server text. **D2 is safe only because files
    is client-only** — verified in that class doc. A test asserting the CODE default does not guarantee the
    runtime prompt for the memory half.
15. **D3's checklist wording reads as a plan-level artifact rule and cannot safely be one.** "A goal naming the
    vault without a target folder must resolve to sources/subfolder" implies the plan declares a
    `sources/...` `expectedArtifact`. `AgentVerifier.TryBuildArtifactFactsAsync` probes exactly
    `CompletedSteps.Where(c => !string.IsNullOrWhiteSpace(c.ExpectedArtifact))` (`:267`) against
    `ctx.WorkspaceRoot` (`:276`), and `AgentPlanner.BuildPlanMessages:786` explicitly requires artifacts
    "relative to the working folder" — so a vault reference in `expectedArtifact` is probed under the workspace
    root and reported NOT FOUND. Implemented as a step-instruction rule only, with no planner change. Slice 11.
16. **E2's open question — BOTH stated hypotheses are wrong.** The resume does not lose the ref
    (`AgentRunOrchestrator.cs:943`, and `AgentVerifierTests:507-536` already passes today), and the probe does
    not de-duplicate (`AgentVerifier.cs:394` is a plain foreach; the only de-dup is inside `FileCandidates`,
    within ONE declaration). The real cause is a third thing: the `emit_step_result` `artifact_ref` channel is
    rendered as prose (`:205`) and never probed, which the ArtifactProbe README already states in its own
    words at `:27`. Slice 16.
17. **The checklist's "flag near-duplicate deliverables in the verdict" — NOT IMPLEMENTABLE AS WRITTEN.** The
    probe cannot reach the verdict: `AgentVerifier`'s class doc (`:26-29`) guarantees it can never itself fail
    a verdict, and `SafeVerify` degrades to ACCEPT on any fault. Landed as a FACT LINE plus one hint sentence;
    `VerdictResult` untouched. Slice 16.
18. **The checklist's "probe EVERY declared artifact" — TRUE ONLY UP TO `MaxProbedPaths = 12`, as it already
    was.** The cap stays at 12 and the declared half is ordered first so today's coverage never shrinks.
    Raising it would break four tests that hard-code 12, for coverage only past 12 candidates where behaviour
    already degrades with a stated arm.
19. **G2's "an expander over `ApprovalTargetLine`" — FOLLOW THE CODE.**
    `RunProgressPanelParseTests.EveryControlTemplateApplies_UnderARealLayoutPass` pins
    `Assert.Equal(4, sectionHeaders)` over `FindVisual<ToggleButton>`, and an `Expander`'s default template
    contains a `ToggleButton`. The panel also documents at `:40-41` why it avoids the framework `Expander`:
    the content must stay in the LOGICAL tree whether or not it is expanded. Landed as this panel's own
    disclosure idiom. Slice 13.
20. **G3's `Deps: G1` does not hold.** The Flow body's SOURCE is unchanged, so G3 satisfies its own checklist
    text with no dependency. Moved to Slice 3.
21. **"The Flow card is a virtualized `ItemsControl`" — FALSE.** `FlowView.xaml:378-388` is a plain
    `ItemsControl` in a `ScrollViewer`, no `ItemsPanel`, no `VirtualizingStackPanel`, and the same
    `DataTemplate` is instantiated again by the arrival peek at `:508`. Nothing virtualizes, which is why
    `MaxLines` is the right mechanism rather than `MaxHeight`.
22. **E1's `Deps: C3` is an ORDERING dependency, not a data one.** Both artifact channels already persist
    (`AgentSteps.ExpectedArtifact`; `ExtraJson.artifactRef`) and already re-seed on resume
    (`AgentRunOrchestrator.cs:943-946`). Group E needs no new table, no new column and nothing from Slice 4.
23. **Issue 1.1 is not only a LENGTH problem.** `ToolApprovalArguments.Describe` renders only string-valued
    arguments, so the collapsed line already understates a call with numeric, boolean, array or object
    arguments even far below 120 chars. Slice 12.
24. **`RunPauseEnvelope.ReadApprovalArgs` is NOT a truncation site.** It faithfully returns whatever `Join`
    capped at 400. Slice 12 adds a second read path and changes neither the envelope writer nor the reader, so
    `AgentRunOrchestratorArmTests:116` and the `UnattendedApprovalParkTests` envelope assertions stay green.
25. **`AgentToolCarryover.Capture` caps only STRING results.** A non-string result — which the route contract
    explicitly allows — falls through uncapped. The checklist assumes the carried payload is bounded; it is
    bounded only for strings. Slice 4 adds the missing per-row bound rather than changing `Capture`, whose
    in-memory behaviour is not this plan's to alter.
26. **A round whose tool dispatch THROWS yields no `ToolRoundExchange` at all** — the yield sits AFTER
    `await DispatchToolCallsAsync`. So no seam, per-round or per-step, can persist that round, and the
    fault-after-park arm has no `exchange` variable assigned at all. Named as a known lossy edge of Slice 5,
    not fixed.
27. **Four doc comments become false and are trimmed in the slice that falsifies them.**
    `HeadlessRunLauncher.ApplyToolApprovalDecisionAsync`'s "The pending CALL cannot be replayed" (`:857-860`)
    and `StepTurnResult.ApprovalRequiredTool`'s "the resumed step re-issues the call"
    (`Interfaces/IAgentTurnExecutor.cs:154-158`), both in Slice 8; `ToolApprovalStore`'s "nothing here is
    durable — the only thing that survives is the tool NAME", Slice 7; and
    `BackgroundAssistantTurnRunner.cs:468-473`'s "and then continues to the next round" clause, Slice 1. Two
    one-expression doc fixes go with them: `IAgentTimelineService.cs:60` and `IAiClientService.cs:9`, both
    Slice 1. `docs/agent_run_e2e/2026-08-27-cross-step-tool-context.md` section 5 and its "does not persist
    tool exchanges" bullet are marked superseded in Slice 4.
28. **Line-number drift in the source docs, corrected once.** `HeadlessTurnExecutor.cs` is 841 lines, not
    ~960; its guardrail block is `:466-480` (the doc says `:472-479`) and the tool-exchange append is `:660` as
    documented. The `AiClientService` round-loop tool branch is `:392-406` (the doc says `:391-406`); the
    context construction is `:621-625` inside `DispatchToolCallsAsync`, which spans `:572-638`. In
    `BackgroundAssistantTurnRunner` the withheld-because-parked block is `:474-486`, withheld-because-asking is
    `:489-495`, and `case ToolGateOutcome.Park:` opens at `:611`.

### The one open question — SETTLED 2026-08-31 by the owner: ADD THE FOURTH ARM

**The owner chose to add it.** `request_user_input` DOES stop the loop. Slice 1 therefore ships **four**
stop arms, not three, and the analysis below is kept only for the trade-off it records — its
"keep three arms" recommendation is superseded.

Binding consequences for Slice 1:

- A fourth `dispatch.Stop?.RequestStop();` goes in the `request_user_input` pre-route arm
  (`BackgroundAssistantTurnRunner.cs:432-437`), before `UserInputRequestStore.Record` returns.
- The withheld-because-asking arm (`:489`) becomes **same-round-only** by construction: once the ask stops
  the loop, a pending write can only still arrive from a LATER call in the SAME round, never from a later
  round. Re-reason it on that basis rather than leaving the old wording, which assumed a following round.
- `MidPlanAskTests.Drive`'s round-2 `FollowUpTool` double (`:535-539`) drives a sequence that can no longer
  occur. Rework it into a same-round two-call sequence rather than deleting the coverage — the withheld arm
  must still be pinned, on the only path that can now reach it.
- `AskAlone_DoesNotRaiseTheLoopStopSignal` inverts: rename it to
  `AskAlone_RaisesTheLoopStopSignal` and assert the signal IS raised. That test is the recorded fact for
  this decision, so it must state the decision that was actually made.

The original analysis, retained:



**Should the `request_user_input` pre-route also stop the loop (a fourth arm in Slice 1)?** Slice 1 stops the
loop when a pending WRITE arrives after an ask (the withheld-because-asking arm, `:489`), but not when the
model calls `request_user_input` and then merely keeps talking: that pre-route arm (`:432-437` ->
`UserInputRequestStore.Record`) returns the purely advisory `Accepted` string, and `HeadlessTurnExecutor` reads
`userInput?.Question` only after the whole exchange unwinds. So `request_user_input` keeps issue 1's unbounded
latency in exactly that case — the observed run's 14:08:23 ask.

**Recommendation: keep three arms**, as the defect doc specifies. A fourth would also make the
withheld-because-asking arm same-round-only, turning `MidPlanAskTests.Drive`'s round-2 `FollowUpTool` double
(`:535-539`) into a sequence that can no longer occur. Slice 1's `AskAlone_DoesNotRaiseTheLoopStopSignal` pins
whichever answer the owner picks, so the decision is recorded as a fact either way.

---

## 6. What must not regress

Seven invariants, the slice that most endangers each, and the test that guards it.

### 1. The interactive tool path is byte-for-byte unchanged

**Riskiest slice: 10.** `FilesToolHandler.PrepareWriteFile` is shared with the interactive path, and this is
the only slice that edits it.

Guard: `FilesToolHandlerVaultTargetTests.Write_UnderVault_Interactive_IsStillWritten` (explicit positive
proof — `TaskAmbient.Current = null`, the write lands on disk) plus
`Write_ToAVaultLookalike_InARunWorkspace_IsAllowed`, and every existing `FilesToolHandlerWrite*` and
`ChatSession*` suite unmodified.

Also proved by construction for Slice 1: `ToolGateOutcome.Park` is unreachable interactively
(`ChatSession.cs:1336-1349`, `IsTopLevelUserRun: false` hardcoded at `:1338`), `ToolApprovalStore` is
constructed in exactly one place (`HeadlessTurnExecutor:424`), and Slice 1 touches no file under
`src/Pia.Wpf/ViewModels/`, so `ChatSession.cs:1019` / `:1052` and `AssistantViewModel.cs:2141` keep compiling
on the new optional param and can never see a non-null `Stop`.

### 2. The `_messages` / `_persisted` guardrail

**Riskiest slices: 5 and 8** — 5 adds a THIRD source into `_messages`, 8 adds a fourth.

Guards: `HeadlessTurnExecutorTests.ParkedMidStep_ThePayloadNeverReachesTheCloudSyncedChat` (Slice 6);
`AgentToolExchangeStoreTests.TheStoresPublicSurface_NamesNoSyncAssistantChatType` (the type-level backstop);
and the existing `CarriedToolExchanges_DoNotReachThePersistedChat`, unmodified. The property is
one-directional and type-enforced: both new sources write `ChatMessage`, a type `_persisted` cannot hold, and
`BuildChatSnapshot`'s `Messages = [.. _persisted]` remains the only route to the DB.

### 3. The timeline stays INSERT-only

**Riskiest slice: 2** (it is the only slice about timeline rows). Slice 2 touches no store at all — it is a
view-model-only change.

Guards: `AgentTimelineServiceTests` and `AgentTimelinePrivacyTests` unmodified, and
`SqliteContextTests.AgentTimelineEvents_HasExactlyTheMetadataColumns` unmodified.

**Clarifying line, so nobody conflates them:** the NEW table is *not* insert-only — `ReplayedAt`,
`SupersededAt` and `AnchorMessageId` are UPDATEs. That does not breach the timeline rule, because it is a
different table with a different contract (payload-bearing versus metadata-only), which
`AgentToolExchanges_HasExactlyTheseColumns`' comment states out loud.

### 4. At-most-once tool execution

**Riskiest slice: 8.**

Guards, in order of strength:
`AgentToolExchangeStoreTests.MarkingReplayedIsConditional_SoOnlyOneCallerEverExecutes` (the structural half —
conditional UPDATE, rows-affected == 1, tested under concurrency);
`UnattendedApprovalParkTests.AWithheldCallOfAnAlreadyGrantedTool_IsNotReplayed_AndStillRunsExactlyOnce`;
`ASecondParkOnTheSameTool_SupersedesTheStaleWithheldRow_SoTheGrantWritesOnce`; and the existing
`AGrantedCallAfterThePark_DoesNotRun_AndIsNotReplayedByTheResume`, which must stay green **unmodified** — it
is the fact that catches a predicate drifting back to the grant set.

Also: Slice 1 must not `break` the per-call `foreach`, or the withhold arm stops answering same-round calls
and at-most-once loses its enforcement point. Guarded by the paired-content assertion in
`ToolHandler_RequestsStop_FinishesTheExchangeAfterOneRound`.

### 5. The cloud-sync boundary

**Riskiest slice: 4** (the DDL is where a payload column could be added to the wrong table) **and 5** (the
re-seed is where chat content and store content meet).

Guards: `SqliteContextTests.AgentToolExchanges_HasExactlyTheseColumns` (an exact-set assertion — adding a
column fails here rather than passing review) alongside the unmodified
`AgentTimelineEvents_HasExactlyTheMetadataColumns`;
`AgentToolExchangeStoreTests.TheStoresPublicSurface_NamesNoSyncAssistantChatType`; and
`ParkedMidStep_ThePayloadNeverReachesTheCloudSyncedChat`. The only fact that crosses from the chat side to the
store side is one scalar `Guid` (`AnchorMessageId`).

### 6. Tokenization correctness — the new invariant, guarded by nothing today

**Riskiest slice: 8.** Slice 8's seeded call/result pair bypasses `TokenizingAiClientService` entirely and is
built from Kind 3/4 rows, which are DETOKENIZED. Without the fix, real user content reaches the provider raw
on the next round — the precise defect `WrapToolHandler`'s own comment says it exists to prevent.

Guard: **the new** `UnattendedApprovalParkTests.AReplayedCallIsSeededInItsTokenizedForm` — with
`Privacy.TokenizationEnabled` on, the resumed request must carry the placeholder form in BOTH the seeded
`FunctionCallContent` arguments and the seeded `FunctionResultContent`, and the raw value nowhere. A build that
tokenizes only the result fails on the call half. Supporting guard:
`TokenizingAiClientServiceTests.RelaysTheStopSignalToTheInnerHandler` (Slice 1) keeps the decorator in the
loop at all.

### 7. The zero-warning bar

**Riskiest slices: 1, 2 and 10** — each has one specific mechanical hazard:

- **Slice 1**: `ToolDispatchContext`'s new parameter must stay **trailing and optional**, or all 67 positional
  `new ToolDispatchContext(1)` sites in `tests/` break.
- **Slice 2**: deleting `Severity(ToolGateDecision)` is only safe because grep found exactly five call sites,
  all in `RunProgressViewModel.cs`. **Re-verify at integration** — another slice's branch could add one. And
  `DecisionLabelKey`'s signature must not change at all: `LocalizationTests.cs:243` consumes it as a method
  group, so an added or optional parameter is CS0123.
- **Slice 10**: `ExecuteWriteAsync` gains a 9th positional parameter AHEAD of an optional one.
  `PrepareWriteFile:1029` is its only call site, so nothing else moves, but the lambda must keep passing
  `vaultAnchor`.

Guard: `dotnet build -t:Rebuild -v:n` in Debug AND Release per slice, reading the count off MSBuild's
`N Warning(s)` summary line (at `-v:n` every warning is printed twice, so grepping the log double-counts).
WPF re-reports `src/` warnings under a generated `Pia.Wpf_<hash>_wpftmp.csproj`; fixing the source clears both.

**Hard failures that are not warnings but will stop a slice dead:**
`LocalizationTests.EveryDecisionPillKeyResolvesInAllThreeLocales`'s
`Assert.Equal(categories, keys.Length)` at seven pill keys (Slice 2 — the rewrite is mandatory);
`LocalizationTests.AllTranslations_MustBeComplete` on a key added to fewer than three locales (Slices 2, 13);
`DiRegistrationTests.AllServiceInterfaces_MustHaveRegisteredImplementation` on the unregistered store
(Slice 4); `NamingConventionTests.RecordTypes_MustNotLiveInTheServicesRootNamespace` if
`AgentToolExchangeRow` lands in `Pia.Services` (Slice 4);
`RunProgressPanelParseTests.EveryControlTemplateApplies_UnderARealLayoutPass`'s
`Assert.Equal(4, sectionHeaders)` if the fold row becomes a `ToggleButton` or an `Expander` (Slice 13); and
`RunProgressPanelParseTests.EveryNonTemplatedBindingPath_ResolvesOnTheViewModelThatHostsThePanel` if any of
Slice 13's seven new bindings is not PUBLIC on `RunProgressViewModel`.

---

## 6a. Corrections found while building slices 1-3

Errors in THIS plan, found by the implementers against the real code. Later slices must not re-inherit
them.

| # | The plan said | The code says | Bearing |
|---|---|---|---|
| 1 | Slice 3 bounds `FlowView.xaml`'s body with `MaxLines="3"`, and argues for it over `MaxHeight`. | **WPF's `TextBlock` has no `MaxLines`** — that is a UWP/WinUI property; WPF has it only on `TextBox`. The plan's markup would not have compiled. | Shipped as `LineHeight="16"` + `LineStackingStrategy="BlockLineHeight"` + `MaxHeight="48"`, pinned by `FlowCardBodyBoundsTests` against the font's natural line height. **Slice 13 (G2) must not reach for `MaxLines` either.** |
| 2 | Section 6 claims the interactive gate and the voice handler "can never see a non-null `Stop`". | `AiClientService` constructs a signal on **every** round, so `ChatSession.HandleToolCall` and `AssistantViewModel.HandleVoiceModeToolCall` DO receive a non-null `Stop`. | Behaviour is still unchanged, because neither calls `RequestStop()`. Only the plan's *justification* was wrong. Do not "fix" it by special-casing a null. |
| 3 | Slice 1 inverts an existing `AskAlone_DoesNotRaiseTheLoopStopSignal`, and adds a sibling of `RelaysTheDispatchContextToTheInnerHandler`. | Neither test existed. The first was itself part of Slice 1's new work; the second is a mis-naming of the test at those line numbers. | Written directly rather than renamed. A named-test reference in a later slice is a hypothesis, not a fact — grep before trusting it. |
| 4 | Slice 1's reworked `MidPlanAskTests.Drive` has four contexts at rounds 1/1/2/3 sharing one signal. | A round-3 context sharing a round-1 signal is a shape **production cannot produce** — the loop builds one signal per round. | All four moved to round 1. Once the ask stops the loop there is no later round for either double. |

### Arm attribution — do not over-read a green suite

Under four arms, arms 2 (withheld-because-parked) and 3 (withheld-because-asking) are
**redundant-by-construction**: the loop has already been told to stop by arm 1 or arm 4. With one signal
per round, no test can attribute the raised flag to arms 2 or 3. They are pinned through their own
observables instead — the advisory string that came back, and an empty executed-tool list. Arm 1 is
attributed by a snapshot taken between a round's two calls, arm 4 by an ask being the turn's only call.

---

## 7. Open risks

Carried forward, in rough order of how likely each is to need an owner decision.

1. ~~**`request_user_input` keeps issue 1's unbounded latency.**~~ **CLOSED** — the owner chose the fourth
   stop arm, so a bare `request_user_input` now stops the loop too and issue 1 is fixed for every park
   reason. The residual risk moves to the test rework it forces: `MidPlanAskTests.Drive`'s round-2 double
   drives an unreachable sequence and must be reworked into a same-round two-call one, not deleted. See
   section 5.
2. **A withheld row can outlive several park/resume cycles holding up to 512 K chars of user content that was
   never executed and may never be.** `MaxRecordedCalls` / `MaxRecordedArgumentChars` bound one park; the
   cross-park accumulation is bounded only by the number of parks, plus the terminal purge. The cheapest
   tightening — supersede EVERY unreplayed row of the run on each new park — would drop the surviving
   `create_source` that Slice 9 exists to keep, so it is a deliberate trade, not an oversight.
3. **Slice 16 (E2) can make a correct run look "unverified" once Slice 11 (D3) lands.** A step that obeys the
   vault hint and routes its deliverable to the vault leaves its declared working-folder artifact absent, and
   `AgentVerifier`'s probe reports NOT FOUND against `ctx.WorkspaceRoot`. The hint's `artifact_ref` clause is
   the only channel that carries the truth today, and Slice 16 probes that channel — so landing 16 after 11 is
   what closes it. If 16 is deferred, 11 ships with this gap.
4. **Worktree mode with a repo that independently tracks a top-level `Vault/`**: that folder IS reachable and a
   write into it is now refused. Accepted per Q4; the failure mode is a legible, actionable error. Reopen Q4 if
   it bites a real user. A resumed pre-Slice-10 workspace may also already contain `<ws>\Vault\...` from a run
   that wrote there before the guard existed; those files stay and remain unpromotable.
5. **The 4 000 000-char per-run cap is a judgement call.** It is per PARKED run and purged at terminal settle,
   so the steady-state cost of a healthy install is zero, but a pathological run that writes seven 512 K files
   will silently stop recording, and one Information line is the only signal.
6. **`BeginRunAsync` grows a second await against a second store before the transcript seed.** If the store
   hangs, the resume hangs; `busy_timeout=3000` plus the failure isolation bound it, but it is one more thing
   on the resume critical path. The per-step seal is likewise on the step critical path rather than
   fire-and-forget — one indexed UPDATE beside the existing full chat replace.
7. **Tail placement for unanchored groups puts the pre-park exchanges AFTER a user row the person typed during
   the park.** Chronologically slightly off; deliberate, and justified by `ClearOldResults`' newest-K-by-position
   rule.
8. **`RenderTimelineRows` replaces every `TimelineRowViewModel`, so a rebuild resets the trace scroll
   position.** Bounded by the identity guard to park-state transitions (about two per park), which is exactly
   when the user's attention is being redirected anyway.
9. **A trace truncated at `MaxEventsPerRun` (501) may not contain the park row**, so `LiveParkRowId` returns
   null and a genuinely parked run shows no Awaiting pill. Pre-existing for any trace-derived pill; the user is
   not stranded, because the Continue/Deny buttons and the activity line come from the pause envelope.
10. **Slice 12's whole surface is invisible with no failure if the DI registration is missing.**
    `AssistantViewModel` is DI-resolved, so an unregistered `IAgentToolExchangeStore` binds to the default null
    and `HasApprovalDetail` is false forever. Slice 4 owns the registration; the guard is
    `DiRegistrationTests`.
11. **Slice 12 depends on a commit-ordering fact it cannot see**: the Kind 3 row must be committed before the
    `AgentRuns` row flips to `WaitingForInput`, or the first projection reads nothing. Latch-on-success makes
    that non-fatal (the next `RunChanged` retries) rather than silent. A park recorded before Slice 7 shipped
    behaves exactly that way, by design.
12. **`Produced` prefers `Outcome.ArtifactRef` over `ExpectedArtifact`** (Slice 15). On a step that declared
    success but named a wrong path, the seeded list names a file that does not exist. That is the correct bias —
    it is what the step claims it wrote — and Slice 16's probe is what catches the lie, but the two halves of
    group E disagree by design when a step misreports.
13. **Adding the reported channel makes the artifact block appear on runs that previously got none** (Slice 16).
    Two candidate tests were checked and neither breaks — `VerifyAsync_NoDeclaredArtifacts_OmitsTheBlockEntirely`
    (`:337`) uses a context with no `Outcome`, and
    `VerifyAsync_TellsTheCriticWhetherEachStepDeclaredItsOwnOutcome` (`:541`) asserts only `Contains` on the
    USER prompt — but re-check if any slice adds more verifier tests.
14. **No copy affordance on the approval detail** (Slice 13): the text can be read but not selected. Deferred
    deliberately — a read-only `ui:TextBox` adds a `TextBoxBase` to the automation-id count and caret/IME cost
    over 8 000 chars.
15. **The tool-approval Flow card has no route to the run panel**, so "see the whole call" is reachable only by
    opening the run some other way. Out of scope for this plan; flagged rather than silently accepted.
16. **Slice 16's extraction of `ProbeOneDeclaration` moves ~40 lines of counter bookkeeping**, and
    `probed >= MaxProbedPaths` is checked in two places today. Both checks must survive or the probe budget
    silently stops binding.
17. **The end-to-end check is not automated.** Re-run the original goal: one park, dialog inside a second, a
    resume that keeps the extracted data, one summary file, in the vault, and a completed run showing zero
    pending approvals. A vault-writing e2e is safe on a throwaway profile **only if the profile also patches
    `assistantFilesFolder`** — `PIA_DATA_DIR` alone does not redirect the vault, because
    `Bootstrapper.InitializeAssistantFoldersAsync` calls
    `paths.SetRoot(AssistantWorkspace.VaultRootFor(settings.AssistantFilesFolder))` at `:321`. Verify the
    resolved root on the running instance before the first live vault write.
