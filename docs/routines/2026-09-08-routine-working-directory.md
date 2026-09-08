# Routine working directory — implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: use `superpowers:subagent-driven-development`
> (recommended) or `superpowers:executing-plans` to work through this task by task. The steps are
> `- [ ]` checkboxes; tick them in the commit that lands them.

**Status:** not started. **Owner:** Marco Altmann. **Written:** 2026-09-08.
**Origin:** owner request of 2026-09-08 — *"for routines we need to add the option to setup the
working directory like we offer it in new chats in assistant view"* — brainstormed the same day.
Three decisions were taken in that conversation and are binding here (§1).
**Tracking surface:** [`2026-09-08-routine-working-directory-checklist.md`](2026-09-08-routine-working-directory-checklist.md).

**Goal:** give a routine (a `ScheduledJob`) its own working directory, picked with the same
drill-down folder picker the assistant view offers a new chat, and make both dispatch legs actually
run in it.

**Architecture:** one new device-local column on `ScheduledJobs`; the AgentTask leg passes it as the
already-existing `HeadlessRunRequest.WorkingSubpath`; the Research leg gains the same field on
`BackgroundTurnRequest` and threads it into the turn's `TaskContext` and onto the chat it writes; the
chat chip's inline picker markup is extracted into a shared `UserControl` and rehosted in the
routines editor.

**Tech stack:** WPF (net10.0-windows), CommunityToolkit.Mvvm source generators, WPF-UI (`ui:`),
Microsoft.Data.Sqlite (hand-written SQL, PRAGMA-detected column migrations), xunit v3 + NSubstitute.

---

## 1. Binding decisions

| # | Decision | Consequence |
|---|---|---|
| D1 | **Extract the picker into a shared `UserControl`**, do not duplicate its ~150 lines of XAML + code-behind. | Task 4 exists at all, and `PiaChatTitleChip` is edited even though nothing about the chat chip is changing. |
| D2 | **A new routine seeds its folder from `AppSettings.AssistantDefaultWorkingDirectory`** (default `"Playground"`), the same setting a new chat uses. | Task 5 caches the resolved default in `RefreshAsync`. A routine already on disk has `NULL` and stays at the sandbox root — nothing re-anchors silently. |
| D3 | **Both dispatch legs honour the folder**, not just the cheap one. | Task 3 exists; without it the field would do nothing on `Research`, which is the kind the whole blueprint catalog produces. |

## 2. Global constraints

Every task's requirements implicitly include these.

- **Zero-warning policy.** A feature is not commit-ready until a *rebuild* reports `0 Warning(s)` and
  `0 Error(s)` in **both** Debug and Release: `dotnet build -t:Rebuild -v:n` and again with
  `-c Release`. Read the count off MSBuild's `N Warning(s)` summary line. WPF re-reports `src/`
  warnings under a generated `Pia.Wpf_<hash>_wpftmp.csproj`; fixing the source clears both.
- **Test gate.** `dotnet test` with **no filter**, bar is `failed: 0`. Live-provider tests are
  `Explicit` and report as `Not Run`; that is correct. Do not add `--filter-not-namespace`.
- **Comment discipline.** Default to no comment. A surviving comment or `<summary>` is **one short
  line**, only when the WHY is non-obvious. Never cite this plan, a task number, or a decision ID in
  source — `D2`, `Task 3`, `§1` in a code comment is a plan failure. State the underlying fact in
  plain language instead.
- **Privacy-first logging.** A working-directory path is a **user-named item**. Log it only through
  `_logger.SensitiveDebug(...)`, never `LogInformation`. Log the routine's `Id` freely.
- **Namespaces are `Pia.*`, not `Pia.Wpf.*`.** 4-space C# indent, 2-space XAML indent.
- **Automation ids.** Every new `ButtonBase`/`ComboBox`/`TextBoxBase`/`PasswordBox`/`Slider`/
  `Expander`/`TabItem` in a `UserControl` needs an `AutomationProperties.AutomationId`, and
  `tests/Pia.Wpf.Tests/Views/ViewAutomationIdTests.cs` must be updated in the same change.
- **Localization is trilingual.** Every new resx key lands in `ViewStrings.resx`,
  `ViewStrings.de.resx` *and* `ViewStrings.fr.resx` (or the `MessageStrings` trio), or
  `tests/Pia.Wpf.Tests/Architecture/LocalizationTests.cs` fails.
- **Stored path convention.** Sandbox-**relative**, **forward slashes**, `null`/empty = the sandbox
  root. This is the convention `ChatSession.SetWorkingDirectory`, `TaskContext.WorkingSubpath` and
  `IWorkingDirectoryService` already use; do not invent a second one.

## 3. What already exists (do not rebuild it)

Read these before starting — the plan leans on all of them.

- `src/Pia.Wpf/Services/IWorkingDirectoryService.cs` — `ListSubfolders`, `EnsureSubfolder`,
  `ResolveAbsolutePath`. Already registered as a singleton in `Bootstrapper.cs:566`.
- `src/Pia.Wpf/ViewModels/WorkingDirectoryPickerViewModel.cs` — the whole drill-down state machine
  (crumbs, entries, inline folder creation, `WorkingDirectoryChosen`). **Reused verbatim.** Note its
  constructor comment: it deliberately does *not* enumerate at construction, because
  `ListSubfolders` blocks on an async settings load; enumeration happens in `InitializeFrom`/
  `Refresh`, which the host calls when its popup opens.
- `src/Pia.Wpf/Services/Interfaces/IHeadlessRunLauncher.cs:38` —
  `HeadlessRunRequest.WorkingSubpath` **already exists** and is already honoured:
  `RunWorkspaceService.ResolveSourceRootAsync` (`RunWorkspaceService.cs:1086`) narrows the seed root
  to it, and `PromoteAsync` (`:240`) copies back to the narrowed `SourceRoot` it recorded. So the
  AgentTask leg is genuinely a one-line change with correct write-back semantics.
- `src/Pia.Wpf/ViewModels/AssistantViewModel.cs:486` — `ApplyDefaultWorkingDirectoryAsync`, the
  pattern D2 mirrors: `EnsureSubfolder(settings.AssistantDefaultWorkingDirectory)`, an empty result
  means "unusable or root, leave it alone".

## 4. File structure

| File | Change | Responsibility |
|---|---|---|
| `src/Pia.Wpf/Models/ScheduledJob.cs` | modify | the new `WorkingDirectory` property |
| `src/Pia.Wpf/Infrastructure/SqliteContext.cs` | modify | `CREATE TABLE` column + PRAGMA-guarded `ALTER` |
| `src/Pia.Wpf/Services/Interfaces/IScheduledJobService.cs` | modify | `workingDirectory` on `CreateAsync`/`UpdateAsync` |
| `src/Pia.Wpf/Services/ScheduledJobService.cs` | modify | persist, read, normalize; keep it off the sync SET list |
| `src/Pia.Wpf/Services/ScheduledJobBackgroundService.cs` | modify | forward the folder on both dispatch legs |
| `src/Pia.Wpf/Services/Interfaces/IBackgroundAssistantTurnRunner.cs` | modify | `WorkingSubpath` on `BackgroundTurnRequest` |
| `src/Pia.Wpf/Services/BackgroundAssistantTurnRunner.cs` | modify | ambient + all three chat writes |
| `src/Pia.Wpf/Controls/Shared/PiaWorkingDirectoryPicker.xaml(.cs)` | **create** | the extracted drill-down picker, host-agnostic |
| `src/Pia.Wpf/Controls/Assistant/PiaChatTitleChip.xaml(.cs)` | modify | rehost onto the extracted control |
| `src/Pia.Wpf/ViewModels/RoutinesViewModel.cs` | modify | editor field, picker VM, default seeding, save |
| `src/Pia.Wpf/Views/RoutinesView.xaml(.cs)` | modify | editor button + popup, detail-pane line |
| `src/Pia.Wpf/Resources/Strings/ViewStrings{,.de,.fr}.resx` | modify | the four new routines strings |
| `docs/release_notes/RELEASE.md` | modify | the user-facing bullet |

Test files, in the order the tasks touch them:

`tests/Pia.Wpf.Tests/Services/ScheduledJobServiceTests.cs` ·
`tests/Pia.Wpf.Tests/Services/ScheduledJobBackgroundServiceTests.cs` ·
`tests/Pia.Wpf.Tests/Services/BackgroundAssistantTurnRunnerTests.cs` ·
`tests/Pia.Wpf.Tests/Views/ViewAutomationIdTests.cs` ·
`tests/Pia.Wpf.Tests/Views/ChatTitleChipInteractionTests.cs` (must stay green, not edited) ·
`tests/Pia.Wpf.Tests/ViewModels/RoutinesViewModelTests.cs`

**Three test fakes implement `IScheduledJobService` and will not compile until they carry the new
parameter** — `ScheduledJobBackgroundServiceTests.cs:1762`, `ScheduledJobToolHandlerTests.cs`,
`D5PausePremiseTests.cs`. Task 1 fixes all three.

---

## Task 1: persist the folder on a routine

**Files:**
- Modify: `src/Pia.Wpf/Models/ScheduledJob.cs` (end of the class, after `MeetingConsentAckAt`)
- Modify: `src/Pia.Wpf/Infrastructure/SqliteContext.cs:358` and `:765-847`
- Modify: `src/Pia.Wpf/Services/Interfaces/IScheduledJobService.cs:7-22` and `:67-77`
- Modify: `src/Pia.Wpf/Services/ScheduledJobService.cs` (`CreateAsync`, `UpdateAsync`, `InsertAsync`, `ReadAsync`, `AddJobParameters`, `MapJob`, the `UpsertFromSyncAsync` comment at `:697`)
- Modify: `tests/Pia.Wpf.Tests/Services/ScheduledJobBackgroundServiceTests.cs:1762`, `tests/Pia.Wpf.Tests/Services/ScheduledJobToolHandlerTests.cs`, `tests/Pia.Wpf.Tests/Services/D5PausePremiseTests.cs` (fake signatures only)
- Test: `tests/Pia.Wpf.Tests/Services/ScheduledJobServiceTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `ScheduledJob.WorkingDirectory` (`string?`);
  `IScheduledJobService.CreateAsync(..., string? workingDirectory = null)` and
  `IScheduledJobService.UpdateAsync(..., string? workingDirectory = null)`, where on **update**
  `null` = leave unchanged and `""` = clear to the sandbox root.

- [x] **Step 1: Write the failing tests**

Append to `tests/Pia.Wpf.Tests/Services/ScheduledJobServiceTests.cs`. Its constructor already builds
a real `SqliteContext` over a temp file plus a `ScheduledJobService`, so these exercise the real SQL.

```csharp
    [Fact]
    public async Task CreateAsync_RoundTripsTheWorkingDirectory()
    {
        var job = await _service.CreateAsync("TEST_Wd", "q", RecurrenceType.Daily, new TimeOnly(8, 0),
            workingDirectory: "Reports/Weekly");

        var fetched = await _service.GetAsync(job.Id);
        Assert.Equal("Reports/Weekly", fetched!.WorkingDirectory);
    }

    [Fact]
    public async Task CreateAsync_NormalizesBackslashesAndEdgeSlashes()
    {
        var job = await _service.CreateAsync("TEST_WdNorm", "q", RecurrenceType.Daily, new TimeOnly(8, 0),
            workingDirectory: @"\Reports\Weekly\");

        var fetched = await _service.GetAsync(job.Id);
        Assert.Equal("Reports/Weekly", fetched!.WorkingDirectory);
    }

    [Fact]
    public async Task CreateAsync_WithNoWorkingDirectory_StoresNull()
    {
        var job = await _service.CreateAsync("TEST_WdNone", "q", RecurrenceType.Daily, new TimeOnly(8, 0));

        var fetched = await _service.GetAsync(job.Id);
        Assert.Null(fetched!.WorkingDirectory);
    }

    [Fact]
    public async Task UpdateAsync_NullLeavesTheWorkingDirectoryAlone()
    {
        var job = await _service.CreateAsync("TEST_WdKeep", "q", RecurrenceType.Daily, new TimeOnly(8, 0),
            workingDirectory: "Reports");

        await _service.UpdateAsync(job.Id, name: "TEST_WdKeep2");

        var fetched = await _service.GetAsync(job.Id);
        Assert.Equal("Reports", fetched!.WorkingDirectory);
    }

    [Fact]
    public async Task UpdateAsync_EmptyStringClearsToTheSandboxRoot()
    {
        var job = await _service.CreateAsync("TEST_WdClear", "q", RecurrenceType.Daily, new TimeOnly(8, 0),
            workingDirectory: "Reports");

        await _service.UpdateAsync(job.Id, workingDirectory: string.Empty);

        var fetched = await _service.GetAsync(job.Id);
        Assert.Null(fetched!.WorkingDirectory);
    }

    /// <summary>The folder is this machine's; a pull must not be able to null it, like the persona pin.</summary>
    [Fact]
    public async Task UpsertFromSyncAsync_LeavesTheWorkingDirectoryAlone()
    {
        var job = await _service.CreateAsync("TEST_WdSync", "q", RecurrenceType.Daily, new TimeOnly(8, 0),
            workingDirectory: "Reports");

        var fromPeer = await _service.GetAsync(job.Id);
        fromPeer!.WorkingDirectory = null;
        fromPeer.Name = "TEST_WdSyncRenamedByPeer";
        await _service.UpsertFromSyncAsync(fromPeer);

        var fetched = await _service.GetAsync(job.Id);
        Assert.Equal("TEST_WdSyncRenamedByPeer", fetched!.Name);
        Assert.Equal("Reports", fetched.WorkingDirectory);
    }
```

Check `UpsertFromSyncAsync`'s real signature on `IScheduledJobService` before writing that last test
and match it; if it takes something other than a bare `ScheduledJob`, adapt the call and keep the
assertion.

- [x] **Step 2: Run the tests to verify they fail**

```bash
dotnet test --filter-class "Pia.Tests.Services.ScheduledJobServiceTests"
```

Expected: compile error `'ScheduledJob' does not contain a definition for 'WorkingDirectory'`.

- [x] **Step 3: Add the model property**

In `src/Pia.Wpf/Models/ScheduledJob.cs`, after `MeetingConsentAckAt`:

```csharp
    /// <summary>Folder this routine's run works in, relative to the assistant-files sandbox (forward
    /// slashes); null = the sandbox root. Device-local — a peer's path would name nothing here.</summary>
    public string? WorkingDirectory { get; set; }
```

- [x] **Step 4: Add the column and its migration**

In `src/Pia.Wpf/Infrastructure/SqliteContext.cs`, inside `CREATE TABLE IF NOT EXISTS ScheduledJobs`,
put a comma after `MeetingConsentAckAt TEXT NULL` and add the new column last:

```sql
                -- Sandbox-relative folder the run works in; NULL = the sandbox root. Device-local like the
                -- pins above: the folder tree belongs to this machine.
                WorkingDirectory TEXT NULL
```

Then in the `PRAGMA table_info(ScheduledJobs)` block near `:765`, add the flag beside its siblings:

```csharp
        var hasJobWorkingDirectory = false;
```

inside the reader loop:

```csharp
                else if (col == "WorkingDirectory") hasJobWorkingDirectory = true;
```

and after the `MeetingConsentAckAt` ALTER:

```csharp
        if (!hasJobWorkingDirectory)
        {
            using var addCol = _connection.CreateCommand();
            addCol.CommandText = "ALTER TABLE ScheduledJobs ADD COLUMN WorkingDirectory TEXT NULL";
            addCol.ExecuteNonQuery();
        }
```

- [x] **Step 5: Widen the service contract**

In `src/Pia.Wpf/Services/Interfaces/IScheduledJobService.cs`, add a trailing, defaulted parameter to
**both** methods so no existing caller breaks.

`CreateAsync` — after `DateTime? meetingConsentAckAt = null`:

```csharp
        // Sandbox-relative folder the run works in; null/empty = the sandbox root. Device-local, like the
        // pins above.
        string? workingDirectory = null);
```

`UpdateAsync` — after `DateTime? meetingConsentAckAt = null`:

```csharp
        // null leaves it unchanged; EMPTY clears it back to the sandbox root. It cannot borrow the Guid.Empty
        // trick the pins use, and a path is never legitimately empty, so empty is free to be the sentinel.
        string? workingDirectory = null);
```

- [x] **Step 6: Persist and read it**

All in `src/Pia.Wpf/Services/ScheduledJobService.cs`. Mirror the new parameter onto `CreateAsync` and
`UpdateAsync`, then:

`CreateAsync`, in the object initializer after `MeetingConsentAckAt = meetingConsentAckAt,`:

```csharp
            WorkingDirectory = NormalizeWorkingDirectory(workingDirectory),
```

`UpdateAsync`, after the `meetingConsentAckAt` line:

```csharp
        if (workingDirectory is not null) existing.WorkingDirectory = NormalizeWorkingDirectory(workingDirectory);
```

Add the helper next to `ParseReasoningEffort`:

```csharp
    /// <summary>Empty means the sandbox root, and the stored form is forward-slashed — the same convention
    /// every other sandbox-relative path in the app uses.</summary>
    private static string? NormalizeWorkingDirectory(string? value)
    {
        var trimmed = value?.Trim().Replace('\\', '/').Trim('/');
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
```

`InsertAsync` — add `WorkingDirectory` to the column list and `@WorkingDirectory` to the `VALUES`
list, both last.

`UpdateAsync`'s `UPDATE ... SET` — append `, WorkingDirectory=@WorkingDirectory` after
`MeetingConsentAckAt=@MeetingConsentAckAt`, and bind it below the other parameters:

```csharp
        command.Parameters.AddWithValue("@WorkingDirectory",
            existing.WorkingDirectory is not null ? (object)existing.WorkingDirectory : DBNull.Value);
```

`ReadAsync`'s `SELECT` — append `, WorkingDirectory` last, making it **index 26**.

`AddJobParameters` — after the `@MeetingConsentAckAt` bind:

```csharp
        command.Parameters.AddWithValue("@WorkingDirectory",
            job.WorkingDirectory is not null ? (object)job.WorkingDirectory : DBNull.Value);
```

`MapJob` — after `MeetingConsentAckAt`:

```csharp
        WorkingDirectory = r.IsDBNull(26) ? null : r.GetString(26),
```

`UpsertFromSyncAsync` — **do not** touch the SET list. Extend the comment above it so the new column
is named among the device-local exclusions:

```csharp
        // PersonaId, ReasoningEffort, BlueprintKey and WorkingDirectory are absent from the SET list on
        // purpose: the server drops fields it does not know, so writing them here would null a local value
        // on the first push→pull cycle.
```

- [x] **Step 7: Fix the three test fakes**

Each of `ScheduledJobBackgroundServiceTests.cs` (`FakeJobService`, ~`:1762`),
`ScheduledJobToolHandlerTests.cs` and `D5PausePremiseTests.cs` implements `IScheduledJobService`.
Add `string? workingDirectory = null` as the last parameter of their `CreateAsync` and `UpdateAsync`
overrides. Where a fake records what it was called with, record the new value too; where it throws
`NotImplementedException`, leave it throwing.

- [x] **Step 8: Run the tests to verify they pass**

```bash
dotnet test --filter-class "Pia.Tests.Services.ScheduledJobServiceTests"
```

Expected: PASS, `failed: 0`.

- [x] **Step 9: Commit**

```bash
git add src/Pia.Wpf/Models/ScheduledJob.cs src/Pia.Wpf/Infrastructure/SqliteContext.cs \
        src/Pia.Wpf/Services/Interfaces/IScheduledJobService.cs src/Pia.Wpf/Services/ScheduledJobService.cs \
        tests/Pia.Wpf.Tests/Services/ScheduledJobServiceTests.cs \
        tests/Pia.Wpf.Tests/Services/ScheduledJobBackgroundServiceTests.cs \
        tests/Pia.Wpf.Tests/Services/ScheduledJobToolHandlerTests.cs \
        tests/Pia.Wpf.Tests/Services/D5PausePremiseTests.cs
git commit -m "feat(routines): store a per-routine working directory"
```

---

## Task 2: the AgentTask leg runs in the folder

**Files:**
- Modify: `src/Pia.Wpf/Services/ScheduledJobBackgroundService.cs:643-655` (`ExecuteAgentTaskAsync`)
- Test: `tests/Pia.Wpf.Tests/Services/ScheduledJobBackgroundServiceTests.cs`

**Interfaces:**
- Consumes: `ScheduledJob.WorkingDirectory` (Task 1).
- Produces: nothing new — it fills the existing `HeadlessRunRequest.WorkingSubpath`.

- [ ] **Step 1: Write the failing test**

Add to `tests/Pia.Wpf.Tests/Services/ScheduledJobBackgroundServiceTests.cs`. The launcher is an
NSubstitute mock in this fixture, so assert on the request it received:

```csharp
    [Fact]
    public async Task AgentTaskLeg_ForwardsTheRoutinesWorkingDirectoryAsTheRunWorkspaceSubpath()
    {
        var jobs = new FakeJobService();
        var job = NewDueJob();
        job.Kind = ScheduledJobKind.AgentTask;
        job.WorkingDirectory = "Reports/Weekly";
        jobs.SeedDue(job);

        var launcher = Substitute.For<IHeadlessRunLauncher>();
        launcher.LaunchAsync(Arg.Any<HeadlessRunRequest>(), Arg.Any<CancellationToken>())
            .Returns(new HeadlessRunHandle(Guid.NewGuid(), Guid.NewGuid(), Task.CompletedTask));

        var bg = new ScheduledJobBackgroundService(
            jobs, new FakeScopeFactory(new FakeServiceProvider().Add<IBackgroundAssistantTurnRunner>(new FakeRunner())),
            new FakeProviderResolver(NewProvider()), new FakeNotificationSurface(),
            launcher, NewSettings(), Substitute.For<IAgentRunService>(),
            Substitute.For<IScheduledMeetingRecorder>(), Substitute.For<IBackgroundMeetingSessions>(),
            NullLogger<ScheduledJobBackgroundService>.Instance);

        await TickAndSettleAsync(bg, CancellationToken.None);

        await launcher.Received(1).LaunchAsync(
            Arg.Is<HeadlessRunRequest>(r => r.WorkingSubpath == "Reports/Weekly"),
            Arg.Any<CancellationToken>());
    }
```

Copy the constructor argument list from a neighbouring AgentTask test in the same file rather than
trusting the one above verbatim — this service's constructor has grown before and will again.

- [ ] **Step 2: Run the test to verify it fails**

```bash
dotnet test --filter-method "*AgentTaskLeg_ForwardsTheRoutinesWorkingDirectory*"
```

Expected: FAIL — the received request has `WorkingSubpath == null`.

- [ ] **Step 3: Forward the folder**

In `ExecuteAgentTaskAsync`, inside `new HeadlessRunRequest(...)`, insert one named argument between
`Budget:` and `PersonaId:`:

```csharp
                Budget: budget,
                WorkingSubpath: job.WorkingDirectory,
                PersonaId: job.PersonaId,
```

No comment here — the parameter's own XML doc on `HeadlessRunRequest` already explains what a
subpath does.

- [ ] **Step 4: Run the test to verify it passes**

```bash
dotnet test --filter-method "*AgentTaskLeg_ForwardsTheRoutinesWorkingDirectory*"
```

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Pia.Wpf/Services/ScheduledJobBackgroundService.cs \
        tests/Pia.Wpf.Tests/Services/ScheduledJobBackgroundServiceTests.cs
git commit -m "feat(routines): run an agent routine in its working directory"
```

---

## Task 3: the Research leg runs in the folder

The default routine kind. `BackgroundAssistantTurnRunner` currently hard-codes `WorkingSubpath: null`
into its `TaskContext`, so its file tools always resolve against the sandbox root; and it writes the
produced chat with no `WorkingDirectory`, so reopening a routine's result chat lands at the root
rather than where the routine worked.

**Verified premise — do not re-derive it.** `FilesToolHandler` has two roots. The tool-execution path
(`FilesToolHandler.cs:218`) resolves against `TaskAmbient.Current?.WorkingSubpath`, which is exactly
what Step 4 sets. The other, `ActiveUiWorkingSubpath` (`:382`), scopes `@Files` autocomplete only and
runs outside any turn, so it cannot shadow a background turn's folder. The ambient is therefore the
whole mechanism, and the test in Step 1 that asserts on it is testing the real thing.

**Out of scope, deliberately:** the runner calls `_promptComposer.PrepareTurn` **without**
`environmentRoot` today, so the model is told no absolute root at all — narrowed or not. Leaving that
alone is a no-op for this feature (the tools resolve relative paths against the ambient subpath
regardless) and keeps `IFilesToolHandler` out of the runner's constructor. Do not add it.

**Files:**
- Modify: `src/Pia.Wpf/Services/Interfaces/IBackgroundAssistantTurnRunner.cs` (`BackgroundTurnRequest`)
- Modify: `src/Pia.Wpf/Services/BackgroundAssistantTurnRunner.cs:81` (stub chat), `:159` (ambient), `:219` (success chat), `:739` (failed chat)
- Modify: `src/Pia.Wpf/Services/ScheduledJobBackgroundService.cs:860-871` (`RunResearchTurnAsync`)
- Test: `tests/Pia.Wpf.Tests/Services/BackgroundAssistantTurnRunnerTests.cs`, `tests/Pia.Wpf.Tests/Services/ScheduledJobBackgroundServiceTests.cs`

**Interfaces:**
- Consumes: `ScheduledJob.WorkingDirectory` (Task 1).
- Produces: `BackgroundTurnRequest.WorkingSubpath` (`string?`, init-only, default `null`).

- [ ] **Step 1: Write the failing tests**

In `tests/Pia.Wpf.Tests/Services/ScheduledJobBackgroundServiceTests.cs` — `FakeRunner` already
captures `LastRequest`:

```csharp
    [Fact]
    public async Task ResearchLeg_ForwardsTheRoutinesWorkingDirectory()
    {
        var jobs = new FakeJobService();
        var job = NewDueJob();
        job.Kind = ScheduledJobKind.Research;
        job.WorkingDirectory = "Reports/Weekly";
        jobs.SeedDue(job);

        var runner = new FakeRunner { Result = new BackgroundTurnResult(Guid.NewGuid(), true, null) };
        var bg = new ScheduledJobBackgroundService(
            jobs, new FakeScopeFactory(new FakeServiceProvider().Add<IBackgroundAssistantTurnRunner>(runner)),
            new FakeProviderResolver(NewProvider()), new FakeNotificationSurface(),
            Substitute.For<IHeadlessRunLauncher>(), NewSettings(), Substitute.For<IAgentRunService>(),
            Substitute.For<IScheduledMeetingRecorder>(), Substitute.For<IBackgroundMeetingSessions>(),
            NullLogger<ScheduledJobBackgroundService>.Instance);

        await TickAndSettleAsync(bg, CancellationToken.None);

        Assert.Equal("Reports/Weekly", runner.LastRequest!.WorkingSubpath);
    }
```

In `tests/Pia.Wpf.Tests/Services/BackgroundAssistantTurnRunnerTests.cs`, add one test that the
ambient is set during the turn and one that the saved chat carries the folder. **Read that file's
existing harness first and reuse it** — it already builds the runner with substituted collaborators
and captures the `SyncAssistantChat` handed to `IAssistantChatService.SaveAsync`. The shape:

```csharp
    [Fact]
    public async Task RunAsync_PutsTheRequestedSubpathOnTheTurnsAmbientContext()
    {
        string? observed = null;
        // Captured inside the AI call, which runs while the ambient bracket is open. Match the harness's
        // existing stub of GetChatCompletionWithToolsAsync argument-for-argument.
        aiClient
            .GetChatCompletionWithToolsAsync(/* … */)
            .Returns(_ => { observed = TaskAmbient.Current.WorkingSubpath; return SomeReply(); });

        await runner.RunAsync(NewRequest() with { WorkingSubpath = "Reports" }, CancellationToken.None);

        Assert.Equal("Reports", observed);
    }

    [Fact]
    public async Task RunAsync_StampsTheSubpathOnTheChatItSaves()
    {
        await runner.RunAsync(NewRequest() with { WorkingSubpath = "Reports" }, CancellationToken.None);

        await chatService.Received().SaveAsync(
            Arg.Is<SyncAssistantChat>(c => c.WorkingDirectory == "Reports"), Arg.Any<CancellationToken>());
    }
```

`BackgroundTurnRequest` is a `record` with `required`/`init` members, so `with { … }` works only if
the harness has a factory returning one; if it builds the request inline, set the property there.

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test --filter-class "Pia.Tests.Services.BackgroundAssistantTurnRunnerTests"
```

Expected: compile error — `BackgroundTurnRequest` has no `WorkingSubpath`.

- [ ] **Step 3: Add the request field**

In `src/Pia.Wpf/Services/Interfaces/IBackgroundAssistantTurnRunner.cs`, after `ReasoningEffort`:

```csharp
    /// <summary>
    /// Folder this turn works in, relative to the assistant-files sandbox (forward slashes);
    /// null = the sandbox root.
    /// </summary>
    public string? WorkingSubpath { get; init; }
```

- [ ] **Step 4: Honour it in the runner**

In `src/Pia.Wpf/Services/BackgroundAssistantTurnRunner.cs`, replace the hard-coded null in the
ambient (around `:159`):

```csharp
            TaskAmbient.Current = new TaskContext(run?.Id ?? chatId, request.WorkingSubpath, OnFileTouched: null, ChatId: chatId,
                UnattendedGranter: AssignmentGranter.ForUnattendedRun(
                    request.Trigger, request.TriggerRef, run?.Id ?? chatId));
```

Add the same line to **all three** `SyncAssistantChat` initializers — the stub (`:81`), the success
chat (`:219`) and `PersistFailedTurnAsync`'s (`:739`) — right after `ProviderId = request.Provider.Id,`:

```csharp
                WorkingDirectory = request.WorkingSubpath,
```

All three matter: each `SaveAsync` here is a full replace, so a chat object missing the field would
have it nulled by the next write.

- [ ] **Step 5: Forward it from the scheduler**

In `src/Pia.Wpf/Services/ScheduledJobBackgroundService.cs`, in `RunResearchTurnAsync`'s
`new BackgroundTurnRequest { … }`, after `ReasoningEffort = job.ReasoningEffort,`:

```csharp
                    WorkingSubpath = job.WorkingDirectory,
```

- [ ] **Step 6: Run the tests to verify they pass**

```bash
dotnet test --filter-class "Pia.Tests.Services.BackgroundAssistantTurnRunnerTests"
dotnet test --filter-class "Pia.Tests.Services.ScheduledJobBackgroundServiceTests"
```

Expected: PASS on both.

- [ ] **Step 7: Commit**

```bash
git add src/Pia.Wpf/Services/Interfaces/IBackgroundAssistantTurnRunner.cs \
        src/Pia.Wpf/Services/BackgroundAssistantTurnRunner.cs \
        src/Pia.Wpf/Services/ScheduledJobBackgroundService.cs \
        tests/Pia.Wpf.Tests/Services/BackgroundAssistantTurnRunnerTests.cs \
        tests/Pia.Wpf.Tests/Services/ScheduledJobBackgroundServiceTests.cs
git commit -m "feat(routines): run a research routine in its working directory"
```

---

## Task 4: extract the picker into a shared control

Pure refactor — **no behaviour change is allowed here.** Every existing `ChatChip_*` automation id
must survive byte-identical, and every **assertion** in `ChatTitleChipInteractionTests` must survive
unedited.

One of its lines is not an assertion and does have to move.
`ChatTitleChipInteractionTests.cs:116` reaches the entries list with
`_chip.FindName("WorkingDirEntries")`, and after the extraction that name lives in the child
control's own XAML name scope, so the chip's `FindName` returns null. Re-route the lookup — and only
the lookup — through the child:

```csharp
            var picker = (PiaWorkingDirectoryPicker)_chip.FindName("WorkingDirPicker")!;
            var list = (ListBox)picker.FindName("WorkingDirEntries")!;
```

Its `Assert.Equal("Beta,Gamma|Beta", landed)` and the two `[Theory]` popup tests (which drive
`WorkingDirPopup` and `ChatChip_WorkingDir`, both of which stay on the chip) must not change at all.
If an assertion has to move to make the suite green, the extraction is wrong.

**Files:**
- Create: `src/Pia.Wpf/Controls/Shared/PiaWorkingDirectoryPicker.xaml`
- Create: `src/Pia.Wpf/Controls/Shared/PiaWorkingDirectoryPicker.xaml.cs`
- Modify: `src/Pia.Wpf/Controls/Assistant/PiaChatTitleChip.xaml:220-455` (the popup body) and `PiaChatTitleChip.xaml.cs` (remove the moved handlers)
- Modify: `tests/Pia.Wpf.Tests/Views/ViewAutomationIdTests.cs`
- Modify: `tests/Pia.Wpf.Tests/Views/ChatTitleChipInteractionTests.cs:116` — the `FindName` lookup only, never an assertion

**Interfaces:**
- Consumes: a `WorkingDirectoryPickerViewModel` as its `DataContext` (unchanged, supplied by the host).
- Produces: `Pia.Controls.Shared.PiaWorkingDirectoryPicker` with
  `public string AutomationIdPrefix { get; set; }` (dependency property, default `"WorkingDir"`),
  `public event EventHandler? CloseRequested;` (raised on Escape) and
  `public void FocusEntries();` (the host calls it from its `Popup.Opened`).

- [ ] **Step 1: Create the control's XAML**

`src/Pia.Wpf/Controls/Shared/PiaWorkingDirectoryPicker.xaml` — move the **inner content** of the
chip's popup `Border`, i.e. everything from the `<Grid>` with the three `RowDefinition`s down to its
closing `</Grid>`. The `Popup` and the `Border` (with its shadow, radius and `MinWidth`) stay with
each host, because placement and chrome differ per host.

```xml
<UserControl x:Class="Pia.Controls.Shared.PiaWorkingDirectoryPicker"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:ui="http://schemas.lepo.co/wpfui/2022/xaml"
             xmlns:loc="clr-namespace:Pia.Localization"
             xmlns:vmm="clr-namespace:Pia.ViewModels.Models"
             xmlns:local="clr-namespace:Pia.Controls.Shared"
             x:Name="PickerRoot">
  <Grid>
    <Grid.RowDefinitions>
      <RowDefinition Height="Auto" />
      <RowDefinition Height="Auto" />
      <RowDefinition Height="*" />
    </Grid.RowDefinitions>
    <!-- … the crumb header, the inline new-folder row and the entries ListBox, moved verbatim … -->
  </Grid>
</UserControl>
```

Every automation id in the moved markup becomes prefix-bound so the two hosts stay disjoint:

```xml
AutomationProperties.AutomationId="{Binding AutomationIdPrefix, ElementName=PickerRoot, StringFormat='{}{0}_AddFolder'}"
AutomationProperties.AutomationId="{Binding AutomationIdPrefix, ElementName=PickerRoot, StringFormat='{}{0}_NewFolderName'}"
AutomationProperties.AutomationId="{Binding AutomationIdPrefix, ElementName=PickerRoot, StringFormat='{}{0}_NewFolderConfirm'}"
AutomationProperties.AutomationId="{Binding AutomationIdPrefix, ElementName=PickerRoot, StringFormat='{}{0}_NewFolderCancel'}"
AutomationProperties.AutomationId="{Binding AutomationIdPrefix, ElementName=PickerRoot, StringFormat='{}{0}_FolderEntries'}"
```

The crumb id inside the `ItemsControl.ItemTemplate` needs both the prefix and the row's index, and
`ElementName` does not reach out of a template, so it becomes a `MultiBinding` in property-element
form on the crumb `Button`:

```xml
<AutomationProperties.AutomationId>
  <MultiBinding StringFormat="{}{0}_Crumb_{1}">
    <Binding Path="AutomationIdPrefix"
             RelativeSource="{RelativeSource AncestorType=local:PiaWorkingDirectoryPicker}" />
    <Binding Path="Index" />
  </MultiBinding>
</AutomationProperties.AutomationId>
```

Keep the existing `loc:Str AssistantChat_WorkingDir_*` keys exactly as they are. They read fine in
both hosts, and renaming them would churn three resx files for nothing.

- [ ] **Step 2: Create the control's code-behind**

`src/Pia.Wpf/Controls/Shared/PiaWorkingDirectoryPicker.xaml.cs` — move `NewFolderButton_Click`,
`NewFolderTextBox_PreviewKeyDown`, `ConfirmCreateFolderButton_Click`, `CancelCreateFolderButton_Click`,
`ParkFocusThenSettle`, `SettleFocusAfterCreate`, `WorkingDirEntries_PreviewKeyDown`,
`WorkingDirEntries_PreviewMouseLeftButtonUp`, `FocusFirstEntry` and `FindAncestor<T>` out of
`PiaChatTitleChip.xaml.cs` **verbatim**, with two edits: every
`if (DataContext is not ChatTitleChipViewModel vm) … vm.WorkingDirectoryPicker` becomes
`if (DataContext is not WorkingDirectoryPickerViewModel picker)`, and the Escape arm raises the
event instead of poking a host view-model.

Keep every existing comment on those members — they record why focus is parked before the inline row
collapses, and why `FocusFirstEntry` runs synchronously. Losing them loses the reason.

```csharp
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Pia.ViewModels;

namespace Pia.Controls.Shared;

/// <summary>Drill-down folder picker over a <see cref="WorkingDirectoryPickerViewModel"/> DataContext.
/// Hosted inside each caller's own Popup, so placement and chrome stay with the host.</summary>
public partial class PiaWorkingDirectoryPicker : UserControl
{
    /// <summary>Stem for every automation id inside, so two hosts stay distinguishable to a script.</summary>
    public static readonly DependencyProperty AutomationIdPrefixProperty =
        DependencyProperty.Register(nameof(AutomationIdPrefix), typeof(string),
            typeof(PiaWorkingDirectoryPicker), new PropertyMetadata("WorkingDir"));

    public string AutomationIdPrefix
    {
        get => (string)GetValue(AutomationIdPrefixProperty);
        set => SetValue(AutomationIdPrefixProperty, value);
    }

    /// <summary>Escape was pressed in the folder list; the host closes its popup and takes focus back.</summary>
    public event EventHandler? CloseRequested;

    public PiaWorkingDirectoryPicker() => InitializeComponent();

    /// <summary>Highlight and focus the first folder row; the host calls this from its Popup's Opened.</summary>
    public void FocusEntries() => FocusFirstEntry();

    // … the moved members, unchanged apart from the DataContext cast …
}
```

The Escape arm of `WorkingDirEntries_PreviewKeyDown` becomes:

```csharp
            case Key.Escape:
                CloseRequested?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
                break;
```

- [ ] **Step 3: Rehost the chip on it**

In `src/Pia.Wpf/Controls/Assistant/PiaChatTitleChip.xaml`, keep the `Popup` and its `Border`
(including `DataContext="{Binding WorkingDirectoryPicker}"`) and replace the whole inner `Grid` with:

```xml
              <shared:PiaWorkingDirectoryPicker x:Name="WorkingDirPicker"
                                                AutomationIdPrefix="ChatChip"
                                                CloseRequested="WorkingDirPicker_CloseRequested" />
```

Add `xmlns:shared="clr-namespace:Pia.Controls.Shared"` to the `UserControl` header.
`AutomationIdPrefix="ChatChip"` is what keeps every existing id byte-identical — do not change it.

In `PiaChatTitleChip.xaml.cs`, delete the moved members and leave:

```csharp
    private void WorkingDirPopup_Opened(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(WorkingDirPicker.FocusEntries));

    private void WorkingDirPicker_CloseRequested(object? sender, EventArgs e)
    {
        if (DataContext is ChatTitleChipViewModel vm)
        {
            vm.IsPickerOpen = false;
            WorkingDirButton.Focus();
        }
    }
```

`WorkingDirButton_Click` and `WorkingDirPopup_Closed` stay as they are.

- [ ] **Step 4: Update the automation-id inventory**

`ViewAutomationIdTests` stops the walk at every nested `UserControl` and pins the nested set by name,
so extracting one **must** be reflected in two rows and add a third.

1. `Pia.Controls.Assistant.PiaChatTitleChip` — add `PiaWorkingDirectoryPicker` to its nested list.
   The list is compared **sorted ordinal**, so it becomes
   `"PiaAssistantChatRowContent,PiaWorkingDirectoryPicker"`. Lower its inspected and per-item floors
   by however many controls moved out.
2. Add a row for the new control:
   `[InlineData(typeof(Pia.Controls.Shared.PiaWorkingDirectoryPicker), <n>, <m>, "")]`.

Do not guess `<n>`/`<m>`. Put a deliberately-too-high number in, run the test, and read the real
count out of the failure message (`only N interactive controls were inspected …`), then set it.
Note that a prefix-bound id is a `Binding`, so the test classifies it `IdKind.PerItem` even outside
an `ItemTemplate` — this control's per-item floor will be higher than the crumb alone.

- [ ] **Step 5: Run the affected tests**

```bash
dotnet test --filter-class "Pia.Tests.Views.ViewAutomationIdTests"
dotnet test --filter-class "Pia.Tests.Views.ChatTitleChipInteractionTests"
```

Expected: PASS on both, with `ChatTitleChipInteractionTests` changed only by the one `FindName`
re-route above. That is the evidence the extraction changed no behaviour; if an assertion has to
move to make it green, fix the control, not the test.

If the child's `x:Name`d elements turn out to be unreachable from the tests project, add
`x:FieldModifier="internal"` to `WorkingDirEntries` in the new control's XAML rather than widening
anything else.

- [ ] **Step 6: Commit**

```bash
git add src/Pia.Wpf/Controls/Shared/PiaWorkingDirectoryPicker.xaml \
        src/Pia.Wpf/Controls/Shared/PiaWorkingDirectoryPicker.xaml.cs \
        src/Pia.Wpf/Controls/Assistant/PiaChatTitleChip.xaml \
        src/Pia.Wpf/Controls/Assistant/PiaChatTitleChip.xaml.cs \
        tests/Pia.Wpf.Tests/Views/ViewAutomationIdTests.cs \
        tests/Pia.Wpf.Tests/Views/ChatTitleChipInteractionTests.cs
git commit -m "refactor(assistant): extract the working-directory picker into a shared control"
```

---

## Task 5: the routines editor knows a working directory

View-model only; the XAML lands in Task 6.

**Files:**
- Modify: `src/Pia.Wpf/ViewModels/RoutinesViewModel.cs`
- Modify: `tests/Pia.Wpf.Tests/ViewModels/RoutinesViewModelTests.cs:117` and `:1143`, `tests/Pia.Wpf.Tests/Services/RoutineBlueprintCatalogTests.cs:469` (constructor call sites)
- Test: `tests/Pia.Wpf.Tests/ViewModels/RoutinesViewModelTests.cs`

**Interfaces:**
- Consumes: `IWorkingDirectoryService`, `ISettingsService`, `WorkingDirectoryPickerViewModel`,
  `IScheduledJobService.CreateAsync/UpdateAsync(..., workingDirectory:)` (Task 1).
- Produces, on `RoutinesViewModel`: `string? EditWorkingDirectory`,
  `string EditWorkingDirectoryDisplay`, `bool IsWorkingDirPickerOpen`,
  `WorkingDirectoryPickerViewModel WorkingDirectoryPicker`; on `RoutineRow`:
  `string? WorkingDirectory`, `bool HasWorkingDirectory`, `string WorkingDirectoryLabel`.

- [ ] **Step 1: Write the failing tests**

In `tests/Pia.Wpf.Tests/ViewModels/RoutinesViewModelTests.cs`. `CreateSut()` and its `Sut` record
need the two new substitutes exposed (see Step 2); `JobWith(...)` is a small local helper returning a
`ScheduledJob` with the given folder plus the required `Name`/`Query` — write it beside the existing
helpers.

```csharp
    [Fact]
    public async Task StartCreate_SeedsTheWorkingDirectoryFromTheDefaultSetting()
    {
        var sut = CreateSut();
        sut.WorkingDirectories.EnsureSubfolder("Playground").Returns("Playground");
        await sut.Vm.RefreshAsync();

        sut.Vm.StartCreateCommand.Execute(null);

        Assert.Equal("Playground", sut.Vm.EditWorkingDirectory);
    }

    [Fact]
    public async Task StartEdit_SeedsTheWorkingDirectoryFromTheStoredRow()
    {
        var sut = CreateSut();
        sut.Jobs.GetAllAsync().Returns([JobWith(workingDirectory: "Reports/Weekly")]);
        await sut.Vm.RefreshAsync();
        sut.Vm.SelectedJob = sut.Vm.Jobs[0];

        sut.Vm.StartEditCommand.Execute(null);

        Assert.Equal("Reports/Weekly", sut.Vm.EditWorkingDirectory);
    }

    /// <summary>A routine that predates the column must not be re-anchored by an unrelated edit.</summary>
    [Fact]
    public async Task StartEdit_OnARowWithNoFolder_StaysAtTheSandboxRoot()
    {
        var sut = CreateSut();
        sut.Jobs.GetAllAsync().Returns([JobWith(workingDirectory: null)]);
        await sut.Vm.RefreshAsync();
        sut.Vm.SelectedJob = sut.Vm.Jobs[0];

        sut.Vm.StartEditCommand.Execute(null);

        Assert.Null(sut.Vm.EditWorkingDirectory);
    }

    [Fact]
    public async Task SaveAsync_OnCreate_PassesTheChosenFolder()
    {
        var sut = CreateSut();
        await sut.Vm.RefreshAsync();
        sut.Vm.StartCreateCommand.Execute(null);
        sut.Vm.EditName = "R";
        sut.Vm.EditQuery = "q";
        sut.Vm.EditWorkingDirectory = "Reports";

        await sut.Vm.SaveCommand.ExecuteAsync(null);

        await sut.Jobs.Received().CreateAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<RecurrenceType>(), Arg.Any<TimeOnly>(),
            Arg.Any<DayOfWeek?>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<DateTime?>(),
            Arg.Any<Guid?>(), Arg.Any<IReadOnlyCollection<string>?>(), Arg.Any<ScheduledJobKind>(),
            Arg.Any<bool>(), Arg.Any<Guid?>(), Arg.Any<ReasoningEffort?>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<DateTime?>(), "Reports");
    }

    /// <summary>Empty is the CLEAR sentinel on update; null would silently leave the old folder in place.</summary>
    [Fact]
    public async Task SaveAsync_OnUpdate_ClearingTheFolderSendsEmptyNotNull()
    {
        var sut = CreateSut();
        sut.Jobs.GetAllAsync().Returns([JobWith(workingDirectory: "Reports")]);
        await sut.Vm.RefreshAsync();
        sut.Vm.SelectedJob = sut.Vm.Jobs[0];
        sut.Vm.StartEditCommand.Execute(null);
        sut.Vm.EditWorkingDirectory = null;

        await sut.Vm.SaveCommand.ExecuteAsync(null);

        await sut.Jobs.Received().UpdateAsync(
            Arg.Any<Guid>(), workingDirectory: string.Empty,
            name: Arg.Any<string>(), query: Arg.Any<string>(), recurrence: Arg.Any<RecurrenceType?>(),
            timeOfDay: Arg.Any<TimeOnly?>(), dayOfWeek: Arg.Any<DayOfWeek?>(), dayOfMonth: Arg.Any<int?>(),
            month: Arg.Any<int?>(), providerId: Arg.Any<Guid?>(),
            grantedTools: Arg.Any<IReadOnlyCollection<string>?>(), specificDate: Arg.Any<DateTime?>(),
            kind: Arg.Any<ScheduledJobKind?>(), quietOnSuccess: Arg.Any<bool?>(),
            personaId: Arg.Any<Guid?>(), reasoningEffort: Arg.Any<ReasoningEffort?>(),
            clearReasoningEffort: Arg.Any<bool>(), meetingUrl: Arg.Any<string?>(),
            meetingConsentAckAt: Arg.Any<DateTime?>());
    }

    /// <summary>The folder is not one of the fields the AI draft fills, so touching it must not spend the
    /// latch that lets a draft still set the schedule.</summary>
    [Fact]
    public async Task ChangingTheWorkingDirectory_DoesNotBlockTheDraftFromSettingTheSchedule()
    {
        var sut = CreateSut();
        await sut.Vm.RefreshAsync();
        sut.Vm.StartCreateCommand.Execute(null);
        sut.Vm.EditWorkingDirectory = "Reports";
        sut.Vm.EditDescription = "weekly report";
        sut.Drafting
            .GenerateRoutineDraftAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<RoutineDraftTool>>(), Arg.Any<Guid?>())
            .Returns(Draft(recurrence: RecurrenceType.Weekly));

        await sut.Vm.GenerateDraftCommand.ExecuteAsync(null);

        Assert.Equal(RecurrenceType.Weekly, sut.Vm.EditRecurrence);
    }
```

NSubstitute matches optional arguments positionally, so the `CreateAsync` assertion must list
**every** parameter — count them against the interface before running — and the `UpdateAsync` one
must use named arguments throughout.

- [ ] **Step 2: Widen the constructor and the three call sites**

In `RoutinesViewModel`, add two required parameters **before** `ILogger`, so the optional
`textOptimization` stays last:

```csharp
        IWorkingDirectoryService workingDirectories,
        ISettingsService settings,
        ILogger<RoutinesViewModel> logger,
        ITextOptimizationService? textOptimization = null)
```

Store them as `_workingDirectories` and `_settings` (the names Step 4 uses), and construct the
picker in the body:

```csharp
        WorkingDirectoryPicker = new WorkingDirectoryPickerViewModel(workingDirectories);
        WorkingDirectoryPicker.WorkingDirectoryChosen += (_, path) =>
            EditWorkingDirectory = string.IsNullOrEmpty(path) ? null : path;
```

DI needs no change — `RoutinesViewModel` is registered by type at `Bootstrapper.cs:958` and both
services are already in the container.

Update the three call sites: `RoutinesViewModelTests.cs:117` and `:1143`,
`RoutineBlueprintCatalogTests.cs:469`. Substitutes are fine
(`Substitute.For<IWorkingDirectoryService>()`, `Substitute.For<ISettingsService>()`), but the
settings substitute **must** return real settings —
`settings.GetSettingsAsync().Returns(new AppSettings())` — or `RefreshAsync` throws on a null.

- [ ] **Step 3: Add the editor state**

```csharp
    /// <summary>Folder the routine's run works in, relative to the assistant-files sandbox; null = its root.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EditWorkingDirectoryDisplay))]
    private string? _editWorkingDirectory;

    /// <summary>Backslash form for the button, matching the chat chip's pill: <c>\</c> at root.</summary>
    public string EditWorkingDirectoryDisplay =>
        string.IsNullOrEmpty(EditWorkingDirectory) ? "\\" : "\\" + EditWorkingDirectory.Replace('/', '\\');

    [ObservableProperty]
    private bool _isWorkingDirPickerOpen;

    public WorkingDirectoryPickerViewModel WorkingDirectoryPicker { get; }
```

Open the picker at the current folder rather than wherever the last session left it, and enumerate
only then — the picker view-model's own constructor comment explains why enumeration must not happen
earlier:

```csharp
    partial void OnIsWorkingDirPickerOpenChanged(bool value)
    {
        if (value) WorkingDirectoryPicker.InitializeFrom(EditWorkingDirectory);
    }
```

**No open command.** The button toggles from the view's code-behind by reading the `Popup`, not this
flag — see Task 6 Step 3 and the comment at the top of `PiaChatTitleChip.xaml.cs`, which records why:
a dismissal the flag misses leaves the next press toggling a stale value and opening nothing.
`ChatTitleChipInteractionTests` exists because that bug shipped once already.

**Do not** call `PickersTouched()` from `OnEditWorkingDirectoryChanged`. That latch exists so the AI
draft may still fill schedule fields the user has not chosen; the folder is not one of them, and
spending the latch here would silently stop `GenerateDraftAsync` from setting recurrence, day, time
and effort. The last test in Step 1 is what holds that line.

- [ ] **Step 4: Cache and apply the default**

Add the field:

```csharp
    /// <summary>The folder a NEW routine opens on, resolved once per load. A routine already on disk keeps
    /// whatever it stored, including nothing.</summary>
    private string? _defaultWorkingDirectory;
```

and resolve it in `RefreshAsync`, off the UI thread beside the other loads:

```csharp
            var appSettings = await _settings.GetSettingsAsync();
            _defaultWorkingDirectory = _workingDirectories.EnsureSubfolder(appSettings.AssistantDefaultWorkingDirectory);
```

Then seed the two create paths and read the row in the edit path:

- `StartCreate`, beside `EditMeetingConsent = false;` → `EditWorkingDirectory = _defaultWorkingDirectory;`
- the blueprint path (`StartFromBlueprint`, around `:855`) → the same line.
- `StartEdit` → `EditWorkingDirectory = row.WorkingDirectory;`

- [ ] **Step 5: Carry the folder on the row**

In `RoutineRow` (`RoutinesViewModel.cs:1558`), after the meeting members:

```csharp
    /// <summary>Sandbox-relative folder this routine works in; null = the sandbox root. USER CONTENT.</summary>
    public string? WorkingDirectory { get; init; }

    public bool HasWorkingDirectory => !string.IsNullOrEmpty(WorkingDirectory);

    /// <summary>Backslash form for the detail pane, matching the chat chip's pill.</summary>
    public string WorkingDirectoryLabel =>
        HasWorkingDirectory ? "\\" + WorkingDirectory!.Replace('/', '\\') : "\\";
```

and in `BuildRow`, beside `MeetingConsentAckAt = job.MeetingConsentAckAt,`:

```csharp
            WorkingDirectory = job.WorkingDirectory,
```

- [ ] **Step 6: Send it on save**

In `SaveAsync`, before `IsBusy = true;`:

```csharp
        // Empty CLEARS on update — null there means "leave unchanged", which would strand a folder the user
        // has just cleared. Create takes the null as-is.
        var workingDirectory = EditWorkingDirectory;
```

Add `workingDirectory: workingDirectory ?? string.Empty` to the `UpdateAsync` call and
`workingDirectory: workingDirectory` to the `CreateAsync` call.

Extend the two existing `SensitiveDebug` lines rather than adding a log line — a folder name is user
content and must not reach `LogInformation`:

```csharp
                _logger.SensitiveDebug("Updated scheduled job {Id} name: {Name} goal: {Goal} persona: {Persona} folder: {Folder}",
                    id, EditName, EditQuery, EditPersona?.Name, workingDirectory);
```

- [ ] **Step 7: Run the tests to verify they pass**

```bash
dotnet test --filter-class "Pia.Tests.ViewModels.RoutinesViewModelTests"
```

Expected: PASS.

- [ ] **Step 8: Commit**

```bash
git add src/Pia.Wpf/ViewModels/RoutinesViewModel.cs \
        tests/Pia.Wpf.Tests/ViewModels/RoutinesViewModelTests.cs \
        tests/Pia.Wpf.Tests/Services/RoutineBlueprintCatalogTests.cs
git commit -m "feat(routines): editor state for a routine's working directory"
```

---

## Task 6: the routines editor shows the picker

**Files:**
- Modify: `src/Pia.Wpf/Views/RoutinesView.xaml` — header namespace, the editor block at `:840-863`, the detail pane at `:487-500`
- Modify: `src/Pia.Wpf/Views/RoutinesView.xaml.cs` — two new handlers
- Modify: `src/Pia.Wpf/Resources/Strings/ViewStrings.resx`, `ViewStrings.de.resx`, `ViewStrings.fr.resx`
- Modify: `tests/Pia.Wpf.Tests/Views/ViewAutomationIdTests.cs`
- Modify: `docs/ui_automation/ui-automation-playbook.md` — `ViewAutomationIdTests` names it the single source of truth for the stable ids, so `Routines_Field_WorkingDir`, `Routines_Detail_WorkingDir` and the `Routines_WorkingDir_*` family are listed there

**Interfaces:**
- Consumes: `RoutinesViewModel.EditWorkingDirectory{,Display}`, `IsWorkingDirPickerOpen`,
  `WorkingDirectoryPicker`, `EditorPinsEnabled`;
  `RoutineRow.HasWorkingDirectory`/`WorkingDirectoryLabel`; `PiaWorkingDirectoryPicker` (Task 4).
- Produces: the automation ids `Routines_Field_WorkingDir` (the button),
  `Routines_Detail_WorkingDir` (the detail line) and the picker's `Routines_WorkingDir_*` family.

- [ ] **Step 1: Add the four strings, in all three locales**

`Routines_Field_WorkingDir` · `Routines_Field_WorkingDir_Hint` · `Routines_Field_WorkingDir_Tooltip`
· `Routines_Detail_WorkingDir`. Put them next to the existing `Routines_Field_Persona*` block in each
file so the three stay diffable side by side.

| Key | en | de | fr |
|---|---|---|---|
| `Routines_Field_WorkingDir` | `Working folder` | `Arbeitsordner` | `Dossier de travail` |
| `Routines_Field_WorkingDir_Hint` | `Files this routine reads and writes stay in this folder. Stored on this device only.` | `Dateien, die diese Routine liest und schreibt, bleiben in diesem Ordner. Wird nur auf diesem Gerät gespeichert.` | `Les fichiers que cette routine lit et écrit restent dans ce dossier. Enregistré uniquement sur cet appareil.` |
| `Routines_Field_WorkingDir_Tooltip` | `Choose the working folder` | `Arbeitsordner wählen` | `Choisir le dossier de travail` |
| `Routines_Detail_WorkingDir` | `Working folder` | `Arbeitsordner` | `Dossier de travail` |

- [ ] **Step 2: Add the editor control**

The `RoutinesView` header already declares `xmlns:shared="clr-namespace:Pia.Controls.Shared"` —
confirm it rather than adding a duplicate.

Inside the non-meeting `StackPanel` (`:805`), in the `Grid` that currently holds Effort alone
(`:840-862`), fill the empty right-hand `Grid.Column="2"` so the field pairs with Effort instead of
owning a row of its own:

```xml
                  <StackPanel Grid.Column="2">
                    <TextBlock Text="{loc:Str Routines_Field_WorkingDir}"
                               Style="{StaticResource PiaSettingsSectionLabelStyle}"/>
                    <ui:Button x:Name="RoutineWorkingDirButton"
                               Appearance="Secondary"
                               HorizontalAlignment="Stretch"
                               HorizontalContentAlignment="Left"
                               Margin="0,2,0,4"
                               Cursor="Hand"
                               IsEnabled="{Binding EditorPinsEnabled}"
                               Click="RoutineWorkingDirButton_Click"
                               AutomationProperties.AutomationId="Routines_Field_WorkingDir"
                               ToolTip="{loc:Str Routines_Field_WorkingDir_Tooltip}">
                      <StackPanel Orientation="Horizontal">
                        <ui:SymbolIcon Symbol="Folder24" FontSize="15" Margin="0,0,8,0"
                                       Foreground="{DynamicResource TextFillColorSecondaryBrush}"/>
                        <TextBlock Text="{Binding EditWorkingDirectoryDisplay}"
                                   FontSize="13"
                                   TextTrimming="CharacterEllipsis"
                                   VerticalAlignment="Center"/>
                      </StackPanel>
                    </ui:Button>
                    <TextBlock Text="{loc:Str Routines_Field_WorkingDir_Hint}"
                               Style="{StaticResource PiaSettingsDescriptionStyle}"
                               TextWrapping="Wrap"
                               Margin="0,0,0,4"/>

                    <Popup x:Name="RoutineWorkingDirPopup"
                           IsOpen="{Binding IsWorkingDirPickerOpen, Mode=TwoWay}"
                           PlacementTarget="{Binding ElementName=RoutineWorkingDirButton}"
                           Placement="Bottom"
                           StaysOpen="False"
                           AllowsTransparency="True"
                           PopupAnimation="Fade"
                           Opened="RoutineWorkingDirPopup_Opened"
                           Closed="RoutineWorkingDirPopup_Closed">
                      <Border Background="{DynamicResource SurfaceBrush}"
                              BorderBrush="{DynamicResource BorderBrush_}"
                              BorderThickness="1"
                              CornerRadius="{StaticResource LargeRadius}"
                              Padding="6"
                              MinWidth="320"
                              MaxWidth="380"
                              MaxHeight="360"
                              DataContext="{Binding WorkingDirectoryPicker}">
                        <Border.Effect>
                          <DropShadowEffect Color="#0F1729" BlurRadius="18" Opacity="0.16" ShadowDepth="4"/>
                        </Border.Effect>
                        <shared:PiaWorkingDirectoryPicker x:Name="RoutineWorkingDirPicker"
                                                          AutomationIdPrefix="Routines_WorkingDir"
                                                          CloseRequested="RoutineWorkingDirPicker_CloseRequested"/>
                      </Border>
                    </Popup>
                  </StackPanel>
```

The `Border`'s `DataContext` hop is what hands the control its `WorkingDirectoryPickerViewModel`;
without it every binding inside resolves against `RoutinesViewModel` and the picker renders empty.

- [ ] **Step 3: Add the four code-behind handlers**

In `src/Pia.Wpf/Views/RoutinesView.xaml.cs`. Copy the chip's toggle discipline exactly — the press
reads the **popup**, and the `Closed` handler pulls the flag down behind every dismissal path,
including the outside click that `StaysOpen="False"` handles without telling the view-model:

```csharp
    // Read the POPUP, not the flag (and see the Closed handler): a dismissal the flag misses leaves the
    // next press toggling a stale value and opening nothing.
    private void RoutineWorkingDirButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is RoutinesViewModel vm)
            vm.IsWorkingDirPickerOpen = !RoutineWorkingDirPopup.IsOpen;
    }

    private void RoutineWorkingDirPopup_Opened(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(RoutineWorkingDirPicker.FocusEntries));

    private void RoutineWorkingDirPopup_Closed(object? sender, EventArgs e)
    {
        if (DataContext is RoutinesViewModel vm)
            vm.IsWorkingDirPickerOpen = false;
    }

    private void RoutineWorkingDirPicker_CloseRequested(object? sender, EventArgs e)
    {
        if (DataContext is RoutinesViewModel vm)
        {
            vm.IsWorkingDirPickerOpen = false;
            RoutineWorkingDirButton.Focus();
        }
    }
```

Add `using System.Windows.Threading;` and `using Pia.ViewModels;` if the file lacks them.

- [ ] **Step 4: Show it on the detail pane**

After the Effort block in the detail `ScrollViewer` (around `:495`), matching the pin blocks'
show-only-when-set shape:

```xml
              <TextBlock Text="{loc:Str Routines_Detail_WorkingDir}"
                         Style="{StaticResource PiaSettingsSectionLabelStyle}"
                         Margin="0,18,0,6"
                         Visibility="{Binding SelectedJob.HasWorkingDirectory, Converter={StaticResource BooleanToVisibilityConverter}}"/>
              <TextBlock Text="{Binding SelectedJob.WorkingDirectoryLabel}"
                         Style="{StaticResource PiaDetailValueStyle}"
                         Visibility="{Binding SelectedJob.HasWorkingDirectory, Converter={StaticResource BooleanToVisibilityConverter}}"
                         AutomationProperties.AutomationId="Routines_Detail_WorkingDir"/>
```

A `TextBlock` is not one of the control types `ViewAutomationIdTests` inspects, so this id is for UI
scripts only and moves no floor.

- [ ] **Step 5: Update the automation-id inventory**

`Pia.Views.RoutinesView`'s row gains one inspected control (the button) and one nested view. Nested
names are compared **sorted ordinal**:

```csharp
    [InlineData(typeof(Pia.Views.RoutinesView), 22, 1,
        "PiaEmptyState,PiaHelpHint,PiaRoutinesSearchBar,PiaWorkingDirectoryPicker")]
```

Confirm `22` against the failure message rather than trusting the arithmetic. The nested view is only
discovered if the `Popup`'s content is realised during the logical-tree walk — if the run reports the
nested set unchanged, the walk did not reach inside the `Popup`; leave the nested list as it was and
say so in the commit message rather than forcing it.

- [ ] **Step 6: Run the tests**

```bash
dotnet test --filter-class "Pia.Tests.Views.ViewAutomationIdTests"
dotnet test --filter-class "Pia.Tests.Architecture.LocalizationTests"
dotnet test --filter-class "Pia.Tests.Views.RoutinesViewCursorTests"
```

Expected: PASS on all three. `LocalizationTests` is what catches a key added to one resx and not the
other two.

- [ ] **Step 7: Commit**

```bash
git add src/Pia.Wpf/Views/RoutinesView.xaml src/Pia.Wpf/Views/RoutinesView.xaml.cs \
        src/Pia.Wpf/Resources/Strings/ViewStrings.resx \
        src/Pia.Wpf/Resources/Strings/ViewStrings.de.resx \
        src/Pia.Wpf/Resources/Strings/ViewStrings.fr.resx \
        tests/Pia.Wpf.Tests/Views/ViewAutomationIdTests.cs \
        docs/ui_automation/ui-automation-playbook.md
git commit -m "feat(routines): pick a routine's working directory in the editor"
```

---

## Task 7: release notes and the gate

**Files:**
- Modify: `docs/release_notes/RELEASE.md`
- Modify: `docs/routines/2026-09-08-routine-working-directory-checklist.md` (tick the boxes)

- [ ] **Step 1: Write the release-notes bullet**

Read `docs/release_notes/README.md` first — hard-wrap at 80, one bullet level, no tables, at most four
lines per bullet. Add one bullet under the appropriate existing heading:

```markdown
- Routines now have their own working folder, picked with the same folder browser
  new chats use. A routine reads and writes inside that folder instead of the whole
  assistant files folder, and the folder is remembered on this device only. Routines
  you already have keep working across the whole folder as before.
```

- [ ] **Step 2: Rebuild clean, both configurations**

```bash
dotnet build -t:Rebuild -v:n
dotnet build -t:Rebuild -v:n -c Release
```

Expected: `0 Warning(s)` and `0 Error(s)` on the MSBuild summary line of **each**. A warning is
blocking. If one is genuinely wrong for the code, suppress it with a scoped
`#pragma warning disable <ID>` / `restore` plus a one-line reason — never a project-wide `<NoWarn>`.

- [ ] **Step 3: Run the whole gate**

```bash
dotnet test
```

Expected: `failed: 0`. The suite is ~5900 tests and takes about a minute; the live-provider tests
reporting `Not Run` is correct.

- [ ] **Step 4: Commit**

```bash
git add docs/release_notes/RELEASE.md docs/routines/2026-09-08-routine-working-directory-checklist.md
git commit -m "docs(routines): note the working-directory option"
```

---

## 5. Deliberately out of scope

Each of these is a defensible follow-up, not an omission. Say so if asked; do not quietly do them.

- **`ScheduledJobToolHandler` / `RoutineDraft`.** A routine Pia creates for you takes no working
  directory and lands at the sandbox root, exactly as today. Seeding it from the setting the way the
  editor does would change AI-created routine behaviour, which nobody asked for.
- **`environmentRoot` on the Research leg.** See the note at the head of Task 3.
- **Sync.** The folder never reaches `SyncScheduledJob`, for the same reason `PersonaId` does not:
  the server drops fields it does not know, so a push→pull cycle would null it. A peer device shows
  the routine and runs nothing.
- **A folder for the `MeetingAttendance` kind.** A meeting files a vault source, not chat files. The
  field lives inside the block already hidden for meetings, so it is simply not offered.
- **Moving existing routines.** Every routine already on disk has `NULL` and keeps working across the
  whole assistant files folder. There is no migration and no prompt.

## 6. Verification beyond the gate

`dotnet test` never launches the app, so one manual pass is owed before this is called done. It is on
the checklist as its own step.

1. Routines → New routine → pick a blueprint. The Working-folder button reads `\Playground`.
2. Drill into a subfolder, create one inline, save. Reopen the routine: the folder is still there.
3. Run it now. The produced chat's own working-directory pill shows the same folder, and any file the
   routine wrote is inside it — not at the root of the assistant files folder.
4. Switch the routine to the AgentTask kind, run it now, and confirm the run's files are promoted into
   that folder rather than the sandbox root.
5. Open a routine created before this change: the button reads `\` and nothing moved.
