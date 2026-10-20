# Clear Local Synced Data on Account Switch — Implementation Plan

**Status:** Ready for execution
**Owner:** Marco Altmann
**Written:** 2026-10-20
**Origin:** [2026-10-20-local-data-cleanup-design.md](2026-10-20-local-data-cleanup-design.md) (approved)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. Tick the matching step in [2026-10-20-local-data-cleanup-checklist.md](2026-10-20-local-data-cleanup-checklist.md) in the commit that lands it.

**Goal:** Let the user drop every local copy of synced data at sign-out or before sign-in, guarded by a marker file so an interrupted clear finishes at the next start before any sign-in.

**Architecture:** Choosing *clear* writes a marker file and restarts Pia. Early in `Bootstrapper.InitializeAsync`, before any store is read, `LocalDataResetStartup` sees the marker and runs `LocalDataWiper`, which empties every synced store with plain SQL and file operations — never the per-entity deletes, which would delete on the server. The prompts are shared by `AccountSettingsViewModel` and `FirstRunWizardViewModel` through a static `AccountSwitchPrompts` helper over an `ILocalDataResetService`. The first sync after a clear omits the settings row so the account's preferences win.

**Tech Stack:** .NET 10 WPF, CommunityToolkit.Mvvm, Microsoft.Data.Sqlite (encrypted `history.db`), xunit v3, NSubstitute.

**Spec:** [2026-10-20-local-data-cleanup-design.md](2026-10-20-local-data-cleanup-design.md)

**Branch:** `feature/account-switch-cleanup`, stacked on `feature/e2ee-error-handling` (it provides `IDialogService.ShowChoiceDialogAsync` and the device-revoked dialog). Its PR merges after that one.

## Global Constraints

- Namespaces use `Pia` (not `Pia.Wpf`). 4-space C#, `var` for apparent types, `_camelCase` fields.
- Paths only through `Pia.Paths.PiaPaths`; a routed path is a **property**, never `static readonly` or `{ get; } = …`.
- Never call the per-entity `Delete*Async` methods, `AssistantChatService.DeleteAllAsync` or `KanbanColumnService.DeleteAsync` for the clear — each one deletes on the server.
- Privacy-first logging: log counts and ids only; no titles, names, URLs or provider names (`SensitiveDebug` if ever needed).
- Comment discipline from `CLAUDE.md`: default no comment, one short line when the why is hidden, no history, no task ids.
- Strings go into `ViewStrings.resx`, `ViewStrings.de.resx` and `ViewStrings.fr.resx` together. Keys are looked up by string; the `*.Designer.cs` files are not touched.
- No new interactive controls (the dialogs reuse `IDialogService`), so no AutomationId changes.
- Test gate: build, then run the **built exe** with a redirect, never a pipe, never `dotnet test`. One filter kind per run (`-class` and `-namespace` together run 0 tests).
- Done means `0 Warning(s)` / `0 Error(s)` on `dotnet build -t:Rebuild -v:n` in Debug **and** Release, and `Failed: 0` on the unfiltered exe run.

Test commands used below (`<Class>` is the fully qualified test class):

```bash
dotnet build tests/Pia.Wpf.Tests/Pia.Wpf.Tests.csproj -v:q
tests/Pia.Wpf.Tests/bin/Debug/net10.0-windows10.0.17763.0/Pia.Wpf.Tests.exe -class "<Class>" > test.log 2>&1
grep -E "TEST EXECUTION SUMMARY" -A 2 test.log
```

## Review Focus

1. **Restart from inside the first-run wizard.** The wizard is a modal `ShowDialog`; a restart closes it, and `App.ShowFirstRunWizardAsync` then marks the wizard completed. Expected: the wizard comes back after the restart. → Task 9 tests `App.ShouldMarkWizardCompletedOnClose`.
2. **Crash between writing the marker and signing out.** Tokens are still in `settings.json` on the next start. Expected: the clear also signs the device out locally. → Task 3 test `Wipe_ClearsLeftoverSignInState`.
3. **A clear that throws at startup** (locked or unreadable `history.db`). Expected: Pia still starts, the marker stays, and every sign-in refuses and offers a restart. → Task 4 test `RunIfPending_WhenWipeThrows_KeepsMarkerAndDoesNotThrow`, Task 7 test `ConfirmSignIn_WhenResetPending_OffersRestartAndNeverSignsIn`.
4. **A second run over a half-cleared store.** Expected: converges to the same end state. → Task 3 test `Wipe_RunTwice_Converges`.
5. **Chat flush when chat cloud sync never started** (capability off, or the worker still in its startup delay). Expected: no hang — `true` when chat sync is disabled, `false` at the timeout. → Task 5 tests.

---

### Task 1: Marker file and its path

**Files:**
- Modify: `src/Pia.Wpf/Paths/PiaPaths.cs`
- Create: `src/Pia.Wpf/Services/LocalDataReset/LocalDataResetMarker.cs`
- Test: `tests/Pia.Wpf.Tests/Services/LocalDataReset/LocalDataResetMarkerTests.cs`
- Modify: `tests/Pia.Wpf.Tests/Infrastructure/PiaPathsTests.cs`

**Interfaces:**
- Produces: `PiaPaths.LocalDataResetMarkerPath : string` (property, `Path.Combine(RoamingDataDirectory, "pending-local-data-reset")`).
- Produces: `internal static class Pia.Services.LocalDataReset.LocalDataResetMarker { static bool IsPending { get; } static void Write(); static void Clear(); }`.

- [ ] **Step 1: Write the failing tests** — `[Collection("PiaPathsStatic")]`, each test under `PiaPaths.OverrideForTests(tempRoaming, tempLocal)`:

```csharp
[Fact] public void IsPending_WithNoMarker_IsFalse() => Assert.False(LocalDataResetMarker.IsPending);

[Fact] public void Write_ThenIsPending_IsTrue_AndLeavesNoTempFile()
{
    LocalDataResetMarker.Write();
    Assert.True(LocalDataResetMarker.IsPending);
    Assert.Equal([PiaPaths.LocalDataResetMarkerPath],
        Directory.GetFiles(PiaPaths.RoamingDataDirectory, "pending-local-data-reset*"));
}

[Fact] public void Write_Twice_IsHarmless() { LocalDataResetMarker.Write(); LocalDataResetMarker.Write(); Assert.True(LocalDataResetMarker.IsPending); }

[Fact] public void Clear_RemovesTheMarker_AndIsSafeWhenAbsent()
{
    LocalDataResetMarker.Clear();
    LocalDataResetMarker.Write();
    LocalDataResetMarker.Clear();
    Assert.False(LocalDataResetMarker.IsPending);
}
```

In `PiaPathsTests`, add `LocalDataResetMarkerPath_FollowsTheRoamingOverride`: under an override, `Assert.Equal(Path.Combine(roaming, "pending-local-data-reset"), PiaPaths.LocalDataResetMarkerPath)`.

- [ ] **Step 2: Build and run both classes; expect compile failure** (`LocalDataResetMarker` / `LocalDataResetMarkerPath` missing).
- [ ] **Step 3: Implement.** `Write` creates the roaming directory, writes a UTC timestamp to `<path>.tmp`, then `File.Move(tmp, path, overwrite: true)`. `Clear` deletes if present.
- [ ] **Step 4: Run both classes; expect PASS.**
- [ ] **Step 5: Commit** — `feat: add the local-data reset marker`.

---

### Task 2: Account and synced-preference reset on `AppSettings`

**Files:**
- Modify: `src/Pia.Wpf/Models/AppSettings.cs`
- Modify: `src/Pia.Wpf/Services/AuthService.cs` (`LogoutAsync` settings block)
- Test: `tests/Pia.Wpf.Tests/Models/AppSettingsLocalResetTests.cs`

**Interfaces:**
- Produces on `AppSettings`:
  - `bool SuppressSettingsPushAfterReset { get; set; }` — one line of doc: set by a local-data clear; the next pushes omit Settings until a pull has applied the account's.
  - `internal static readonly IReadOnlyList<string> SyncedPreferenceNames` — the 13 names: `DefaultOutputAction, DefaultTemplateId, WhisperModel, AutoTypeDelayMs, Theme, StartMinimized, TargetLanguage, TargetSpeechLanguage, DefaultWindowMode, ModeProviderDefaults, ModePersonaDefaults, UseSameProviderForAllModes, AssistantDefaultWorkingDirectory`.
  - `void ClearAccountState()` — exactly what `LogoutAsync` writes to settings today: `SyncEnabled=false`; null `EncryptedAccessToken`, `EncryptedRefreshToken`, `SyncUserId`, `SyncUserEmail`, `SyncUserDisplayName`, `SyncProvider`, `LastSyncTimestamp`, `AssistantChatsBackfilledAt`, `LastPushedSettingsHash`, `LastChatPullETag`, `LastPullETag`; `ClientPolicyInitialized=false`; `ForgetE2EEKey()`.
  - `void ResetSyncedData()` — copies each `SyncedPreferenceNames` property from a fresh `new AppSettings()` by reflection; resets `ManagedPersonaStoreInitialized=false`, `LastCatalogVersion=null`, `AssistantChatsEncryptedResendAt=null`, `BlankedSyncRowRepairAt=null`; sets `SuppressSettingsPushAfterReset=true`.

- [ ] **Step 1: Write the failing tests:**

```csharp
[Fact] public void SyncedPreferenceNames_MatchTheSyncPayloadExactly()
{
    var payloadNames = SyncMapper.BuildSettingsPlainPayload(new AppSettings())
        .GetType().GetProperties().Select(p => p.Name).Order();
    Assert.Equal(payloadNames, AppSettings.SyncedPreferenceNames.Order());
}

[Fact] public void ResetSyncedData_RestoresTheDefaultPayload_AndKeepsDeviceState()
{
    var s = new AppSettings { Theme = AppTheme.Dark, DefaultTemplateId = Guid.NewGuid(), AutoTypeDelayMs = 999,
        StartMinimized = true, AssistantDefaultWorkingDirectory = "X", HasCompletedFirstRunWizard = true,
        ServerUrl = "https://example.test", SyncDeviceId = "dev-1", LastCatalogVersion = 7,
        ManagedPersonaStoreInitialized = true };
    s.ModeProviderDefaults[WindowMode.Assistant] = Guid.NewGuid();
    s.ModePersonaDefaults[WindowMode.Assistant] = Guid.NewGuid();

    s.ResetSyncedData();

    Assert.Equal(JsonSerializer.Serialize(SyncMapper.BuildSettingsPlainPayload(new AppSettings())),
                 JsonSerializer.Serialize(SyncMapper.BuildSettingsPlainPayload(s)));
    Assert.True(s.HasCompletedFirstRunWizard);
    Assert.Equal("https://example.test", s.ServerUrl);
    Assert.Equal("dev-1", s.SyncDeviceId);
    Assert.Null(s.LastCatalogVersion);
    Assert.False(s.ManagedPersonaStoreInitialized);
    Assert.True(s.SuppressSettingsPushAfterReset);
}

[Fact] public void ClearAccountState_DropsTokensIdentityCursorsAndKey()
{
    var s = new AppSettings { SyncEnabled = true, EncryptedRefreshToken = "r", EncryptedAccessToken = "a",
        SyncUserId = "u", LastSyncTimestamp = DateTime.UtcNow, LastPullETag = "e", IsE2EEEnabled = true,
        E2EEEncryptedUmk = "k" };
    s.ClearAccountState();
    Assert.False(s.SyncEnabled); Assert.Null(s.EncryptedRefreshToken); Assert.Null(s.EncryptedAccessToken);
    Assert.Null(s.SyncUserId); Assert.Null(s.LastSyncTimestamp); Assert.Null(s.LastPullETag);
    Assert.False(s.IsE2EEEnabled); Assert.Null(s.E2EEEncryptedUmk);
}
```

Use any `WindowMode` member that exists if `Assistant` does not.

- [ ] **Step 2: Build and run `Pia.Tests.Models.AppSettingsLocalResetTests`; expect compile failure.**
- [ ] **Step 3: Implement the four members; replace the settings block in `AuthService.LogoutAsync` with `settings.ClearAccountState();`.** Bring the touched comments in `LogoutAsync` into line with the comment rules.
- [ ] **Step 4: Run `AppSettingsLocalResetTests`, then `Pia.Tests.Services.AuthServiceE2EEKeyTests` and `Pia.Tests.Services.AuthServiceChatBackfillMarkerTests`; expect PASS.**
- [ ] **Step 5: Commit** — `feat: add account-state and synced-preference resets to AppSettings`.

---

### Task 3: `LocalDataWiper` — the clear itself

**Files:**
- Create: `src/Pia.Wpf/Services/LocalDataReset/LocalDataWiper.cs`
- Modify: `src/Pia.Wpf/Infrastructure/SqliteContext.cs` (extract the kanban seed)
- Modify: `src/Pia.Wpf/Services/JsonPersistenceService.cs` (`JsonOptions` becomes `protected internal`)
- Test: `tests/Pia.Wpf.Tests/Services/LocalDataReset/LocalDataWiperTests.cs`

**Interfaces:**
- Consumes: `AppSettings.ClearAccountState()`, `AppSettings.ResetSyncedData()` (Task 2).
- Produces:
  - `internal static void SqliteContext.SeedDefaultKanbanColumns(SqliteConnection connection, SqliteTransaction? transaction, DateTime stampUtc)` — the two inserts `EnsureSchema` runs today (`…0001` "To Do" sort 0 default-view; `…0002` "Closed" sort `2147483647` closed). `EnsureSchema` calls it with `DateTime.UtcNow` when the table is empty.
  - `public sealed class LocalDataWiper(SqliteContext db, ISettingsService settingsService, ILogger<LocalDataWiper> logger)` with `public Task WipeAsync()`.

`WipeAsync` does, in this order:

1. **Roaming files** under `PiaPaths.RoamingDataDirectory`: delete `templates.json`, `pending-sync-deletes.json`, `policy-cache.json` if present. Rewrite `providers.json` keeping only rows with `ProviderType == AiProviderType.PiaCloud`, read and written with `JsonPersistenceService<List<AiProvider>>.JsonOptions`; leave the file alone if it is missing.
2. **Settings**: `GetSettingsAsync()`, then `ClearAccountState()`, `ResetSyncedData()`, `SaveSettingsAsync()`.
3. **`history.db`**, one transaction on `db.GetConnection()`:
   `DELETE FROM Personas; DELETE FROM ManagedPersonas; DELETE FROM Sessions; DELETE FROM AssistantChats; DELETE FROM AssistantChatsFts; DELETE FROM Memories; DELETE FROM Todos; DELETE FROM KanbanColumns; DELETE FROM ScheduledJobs WHERE Kind <> 'MeetingAttendance';`
   then `SeedDefaultKanbanColumns(conn, tx, DateTime.UnixEpoch)`;
   then for `Plugins`: select `Id, ConfigJson`; delete rows whose id is not in `BuiltInPluginDefaults.PreloadedPluginIds` and whose `ConfigJson` is not `LocalMcpConfig.IsLocal`; `UPDATE Plugins SET UserEnabled = NULL` for the preloaded ids.
   Chat messages and agent runs go with `AssistantChats` by FK cascade, as in `AssistantChatService.DeleteAllUnderGateAsync`; `MemoriesFts` follows its triggers.
4. Log one information line with row counts per table.

- [ ] **Step 1: Write the failing tests.** `[Collection("PiaPathsStatic")]`. The fixture overrides both roots to temp folders, creates `new SqliteContext(Path.Combine(local, "history.db"))`, a real `SettingsService(NullLogger, Substitute.For<IPolicyService>())`, and seeds through raw SQL / files:
  - one row each in `Personas`, `ManagedPersonas`, `Sessions`, `AssistantChats` (+ one `AssistantChatMessages` row and its FTS row), `Memories`, `Todos`, a third `KanbanColumns` row plus the seed `…0001` renamed to "Inbox", `ScheduledJobs` with `Kind='Research'` and `Kind='MeetingAttendance'`, `Reminders`, `Plugins` rows for: a preloaded id with `UserEnabled=0`, a server-catalog id, a local MCP config (built with `LocalMcpConfig`);
  - `templates.json`, `pending-sync-deletes.json`, `policy-cache.json`, a `providers.json` with one PiaCloud row and one OpenAI row;
  - settings with `Theme=Dark`, `EncryptedRefreshToken="r"`, `SyncEnabled=true`, `HasCompletedFirstRunWizard=true`, `SyncDeviceId="dev-1"`.

  Tests:

```csharp
[Fact] public async Task Wipe_EmptiesEverySyncedTable()
// Personas, ManagedPersonas, Sessions, AssistantChats, AssistantChatMessages, AssistantChatsFts, Memories, MemoriesFts, Todos → COUNT(*) == 0

[Fact] public async Task Wipe_ReseedsKanbanColumnsWithEpochStamps()
// exactly two rows: ('…0001','To Do',0,IsDefaultView=1) and ('…0002','Closed',2147483647,IsClosedColumn=1); both UpdatedAt == DateTime.UnixEpoch.ToString("O")

[Fact] public async Task Wipe_KeepsDeviceLocalRows()
// Reminders count 1; ScheduledJobs has only the MeetingAttendance row; local MCP plugin row present

[Fact] public async Task Wipe_ResetsServerPluginsButKeepsBuiltIns()
// server-catalog row gone; preloaded row present with UserEnabled IS NULL

[Fact] public async Task Wipe_ClearsSyncedFilesAndKeepsOnlyPiaCloudProvider()
// templates.json, pending-sync-deletes.json, policy-cache.json absent; providers.json deserializes to one PiaCloud row

[Fact] public async Task Wipe_ClearsLeftoverSignInState()
// reloaded settings (fresh SettingsService): SyncEnabled false, EncryptedRefreshToken null, Theme == default,
// SuppressSettingsPushAfterReset true, HasCompletedFirstRunWizard true, SyncDeviceId "dev-1"

[Fact] public async Task Wipe_RunTwice_Converges()
// second WipeAsync on a fresh wiper/context does not throw; same asserts as above hold
```

- [ ] **Step 2: Build and run `Pia.Tests.Services.LocalDataReset.LocalDataWiperTests`; expect compile failure.**
- [ ] **Step 3: Extract `SeedDefaultKanbanColumns`, widen `JsonOptions`, implement `LocalDataWiper`.**
- [ ] **Step 4: Run `LocalDataWiperTests` and `Pia.Tests.Infrastructure.SqliteContextTests`; expect PASS.**
- [ ] **Step 5: Commit** — `feat: add the local synced-data wiper`.

---

### Task 4: Run a pending clear at startup

**Files:**
- Create: `src/Pia.Wpf/Services/LocalDataReset/LocalDataResetStartup.cs`
- Modify: `src/Pia.Wpf/Bootstrapper.cs` (register `LocalDataWiper` as singleton; call the startup hook right after the "Data directories" log line, before `IPolicyService` is resolved)
- Test: `tests/Pia.Wpf.Tests/Services/LocalDataReset/LocalDataResetStartupTests.cs`
- Test: `tests/Pia.Wpf.Tests/Architecture/BootstrapperGraphValidationTests.cs` (add a test)

**Interfaces:**
- Consumes: `LocalDataResetMarker` (Task 1), `LocalDataWiper` (Task 3).
- Produces: `internal static class LocalDataResetStartup { static Task<bool> RunIfPendingAsync(Func<Task> wipe, ILogger logger); }` — returns `true` when a clear ran and the marker was removed. Bootstrapper passes `() => _serviceProvider.GetRequiredService<LocalDataWiper>().WipeAsync()`.

- [ ] **Step 1: Write the failing tests** (`[Collection("PiaPathsStatic")]`, temp override):

```csharp
[Fact] public async Task RunIfPending_WithoutMarker_DoesNotWipe()
{ var calls = 0; Assert.False(await LocalDataResetStartup.RunIfPendingAsync(() => { calls++; return Task.CompletedTask; }, NullLogger.Instance)); Assert.Equal(0, calls); }

[Fact] public async Task RunIfPending_WithMarker_WipesThenClearsMarker()
{ LocalDataResetMarker.Write(); var calls = 0;
  Assert.True(await LocalDataResetStartup.RunIfPendingAsync(() => { calls++; return Task.CompletedTask; }, NullLogger.Instance));
  Assert.Equal(1, calls); Assert.False(LocalDataResetMarker.IsPending); }

[Fact] public async Task RunIfPending_WhenWipeThrows_KeepsMarkerAndDoesNotThrow()
{ LocalDataResetMarker.Write();
  Assert.False(await LocalDataResetStartup.RunIfPendingAsync(() => throw new IOException("locked"), NullLogger.Instance));
  Assert.True(LocalDataResetMarker.IsPending); }
```

In `BootstrapperGraphValidationTests` add `TheLocalDataWiper_DoesNotPullInAuthService`: walk constructor parameters transitively from `LocalDataWiper` through the `ServiceDescriptor.ImplementationType`s of `BuildConfiguredServices()` (skip `ILogger<>`; for a type with several public constructors, walk the one the container picks — the longest whose parameters are all registered or `ILogger<>` (`PolicyService` and `SqliteContext` resolve through their logger-only constructors); fail on a factory descriptor so nothing hides behind a lambda), and assert `typeof(IAuthService)` is never reached. Also assert the walk visited `SqliteContext` and `ISettingsService` (non-vacuity).

- [ ] **Step 2: Build and run both classes; expect compile failure.**
- [ ] **Step 3: Implement `RunIfPendingAsync`** (log error on throw, keep the marker); **wire it into `Bootstrapper.InitializeAsync`.**
- [ ] **Step 4: Run both classes; expect PASS.**
- [ ] **Step 5: Commit** — `feat: finish a pending local-data clear at startup`.

---

### Task 5: Chat sync flush

**Files:**
- Create: `src/Pia.Wpf/Services/Interfaces/IChatSyncFlusher.cs`
- Modify: `src/Pia.Wpf/Services/AssistantChatSyncService.cs`
- Modify: `src/Pia.Wpf/Bootstrapper.cs` (`services.AddSingleton<IChatSyncFlusher>(sp => sp.GetRequiredService<AssistantChatSyncService>())`)
- Test: `tests/Pia.Wpf.Tests/Services/AssistantChatSyncServiceTests.cs`

**Interfaces:**
- Produces: `public interface IChatSyncFlusher { Task<bool> FlushAsync(TimeSpan timeout, CancellationToken ct = default); }` — `true` when every queued chat change and the initial chat upload have reached the server, or when chat cloud sync is off for this session.

Implementation, keeping every push on the worker loop:
- `DrainAsync` returns `bool` — `false` if any `ProcessOpAsync` returned `false` or threw.
- A `_disabled` flag is set when `RunStartupCycleAsync` returns `false` (capability off).
- `FlushAsync`: return `true` if `_disabled`; return `false` if not signed in. Otherwise register a `TaskCompletionSource<bool>` (under `_stateLock`), write to `_signal`, and wait for it or `timeout` (→ `false`).
- In the loop, after the drain and the backfill step, complete every waiting flush with `drainOk && (await _settingsService.GetSettingsAsync()).AssistantChatsBackfilledAt is not null`.

- [ ] **Step 1: Write the failing tests** using the existing fixture and its startup/backfill overrides:

```csharp
[Fact] public async Task Flush_AfterQueuedUpsertsReachTheServer_ReturnsTrue()
[Fact] public async Task Flush_WhenAnUpsertFails_ReturnsFalse()          // handler answers 500 for the PUT
[Fact] public async Task Flush_WhenCapabilityIsOff_ReturnsTrueImmediately()
[Fact] public async Task Flush_BeforeTheWorkerIsListening_ReturnsFalseAtTheTimeout() // startup delay > timeout; assert elapsed < 2 s with a 200 ms timeout
[Fact] public async Task Flush_WithBackfillStillOwed_ReturnsFalse()     // AssistantChatsBackfilledAt stays null (rate-limited 429)
```

- [ ] **Step 2: Build and run `Pia.Tests.Services.AssistantChatSyncServiceTests`; expect the new tests to fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run the class; expect PASS (old and new).**
- [ ] **Step 5: Commit** — `feat: let callers flush assistant chat sync and learn whether it reached the server`.

---

### Task 6: `ILocalDataResetService`

**Files:**
- Create: `src/Pia.Wpf/Services/Interfaces/ILocalDataResetService.cs`
- Create: `src/Pia.Wpf/Services/LocalDataReset/LocalDataResetService.cs`
- Modify: `src/Pia.Wpf/Models/SyncResult.cs` (add `bool PushSucceeded` as the last positional parameter) and its constructors in `SyncClientService.SyncNowAsync` and in tests
- Modify: `src/Pia.Wpf/Services/AppRestartService.cs` (log line: `"Restarting Pia"`)
- Modify: `src/Pia.Wpf/Bootstrapper.cs` (`AddSingleton<ILocalDataResetService, LocalDataResetService>()`)
- Test: `tests/Pia.Wpf.Tests/Services/LocalDataReset/LocalDataResetServiceTests.cs`

**Interfaces:**
- Consumes: `LocalDataResetMarker` (Task 1), `IChatSyncFlusher` (Task 5).
- Produces:

```csharp
public enum ApiKeyNotice { None, WillBeLost, MayBeLost }
// A struct, so an unstubbed test double answers "no notices" instead of null.
public readonly record struct LocalDataNotices(bool VaultHasMemories, ApiKeyNotice ApiKeys);

public interface ILocalDataResetService
{
    bool IsResetPending { get; }
    Task<bool> HasSyncedLocalDataAsync();
    Task<LocalDataNotices> GetNoticesAsync();
    Task<bool> UploadPendingChangesAsync();
    void ScheduleReset();
    Task RestartAsync();
}
```

`LocalDataResetService(SqliteContext db, ITemplateService templates, IProviderService providers, ISettingsService settings, IMemoryService memory, IAuthService auth, ISyncClientService sync, IChatSyncFlusher chats, IAppRestartService restart)`:
- `IsResetPending` → `LocalDataResetMarker.IsPending`; `ScheduleReset` → `LocalDataResetMarker.Write()`; `RestartAsync` → `restart.RestartAsync()`.
- `HasSyncedLocalDataAsync` → `true` if any: a template with `!IsBuiltIn`; a provider with `ProviderType != PiaCloud`; a row in `Personas`, `Sessions`, `AssistantChats`, `Memories` or `Todos`; a `ScheduledJobs` row with `Kind <> 'MeetingAttendance'`; a `KanbanColumns` row that is not exactly `('…0001','To Do',0)` or `('…0002','Closed',2147483647)`.
- `GetNoticesAsync` → `VaultHasMemories = (await memory.ListMemoriesAsync()).Items.Count > 0`. `ApiKeys`: `None` if no non-PiaCloud provider has an `EncryptedApiKey`; else `MayBeLost` when `!auth.IsLoggedIn`; else `None` when `settings.IsE2EEEnabled`; else `WillBeLost`. (Signed out, `IsE2EEEnabled` has already been reset by `ForgetE2EEKey`, so the account's mode is unknown.)
- `UploadPendingChangesAsync` → remember `sync.IsSyncActive`; `await sync.StopBackgroundSyncAndWaitAsync()`; `var r = await sync.SyncNowAsync()`; `var chatsOk = await chats.FlushAsync(TimeSpan.FromSeconds(30))`; in `finally`, `StartBackgroundSync()` if it was active; return `r is { PushSucceeded: true } && chatsOk`.
- `SyncNowAsync` passes the real `pushSucceeded` into `SyncResult`.

- [ ] **Step 1: Write the failing tests** (temp `SqliteContext`, substitutes for the rest):

```csharp
[Fact] public async Task HasSyncedLocalData_FreshInstall_IsFalse()          // seeds only, built-in templates, PiaCloud provider
[Fact] public async Task HasSyncedLocalData_RenamedSeedColumn_IsTrue()
[Fact] public async Task HasSyncedLocalData_OnlyMeetingJob_IsFalse()
[Theory, InlineData("Sessions"), InlineData("AssistantChats"), InlineData("Memories"), InlineData("Todos"), InlineData("Personas")]
public async Task HasSyncedLocalData_AnyRowInSyncedTable_IsTrue(string table)
[Fact] public async Task Notices_VaultWithItems_ReportsMemories()
[Theory, InlineData(true, true, ApiKeyNotice.None), InlineData(true, false, ApiKeyNotice.WillBeLost), InlineData(false, false, ApiKeyNotice.MayBeLost)]
public async Task Notices_ApiKeys(bool loggedIn, bool e2ee, ApiKeyNotice expected)   // one OpenAI provider with EncryptedApiKey
[Fact] public async Task Upload_PushFailed_ReturnsFalse_AndRestartsBackgroundSync()  // SyncResult(…, PushSucceeded: false); IsSyncActive true → StartBackgroundSync received
[Fact] public async Task Upload_SyncSkipped_ReturnsFalse()                           // SyncNowAsync returns null (E2EE not ready)
[Fact] public async Task Upload_PushAndChatsOk_ReturnsTrue()
```

Add to `SyncClientServiceTransferOptimizationTests`: `DeltaPush_ServerRejects_ReportsPushNotSucceeded` (push answers 500 → `result!.PushSucceeded == false`).

- [ ] **Step 2: Build and run `Pia.Tests.Services.LocalDataReset.LocalDataResetServiceTests`; expect compile failure.**
- [ ] **Step 3: Implement; fix every `new SyncResult(` site** (`grep -rn "new SyncResult(" src tests`).
- [ ] **Step 4: Run `LocalDataResetServiceTests`, `SyncClientServiceTransferOptimizationTests`, `AppRestartServiceTests`; expect PASS.**
- [ ] **Step 5: Commit** — `feat: add the local-data reset service`.

---

### Task 7: Dialog text, close label and the shared prompt flow

**Files:**
- Modify: `src/Pia.Wpf/Services/Interfaces/IDialogService.cs`, `src/Pia.Wpf/Services/DialogService.cs`
- Create: `src/Pia.Wpf/ViewModels/AccountSwitchPrompts.cs`
- Modify: `src/Pia.Wpf/Resources/Strings/ViewStrings.resx`, `ViewStrings.de.resx`, `ViewStrings.fr.resx`
- Test: `tests/Pia.Wpf.Tests/ViewModels/AccountSwitchPromptsTests.cs`

**Interfaces:**
- Consumes: `ILocalDataResetService`, `LocalDataNotices`, `ApiKeyNotice` (Task 6).
- Produces:
  - `Task<DialogChoice> ShowChoiceDialogAsync(string title, string message, string primaryText, string secondaryText, string? closeText = null)` — `null` keeps `Common_Close`. Existing callers and NSubstitute setups keep matching because they pass no fifth argument.
  - ```csharp
    internal static class AccountSwitchPrompts
    {
        /// <summary>False when the sign-in must not go ahead: cancelled, or a clear was scheduled.</summary>
        public static Task<bool> ConfirmSignInAsync(ILocalDataResetService reset, IDialogService dialogs, ILocalizationService loc);
        public static Task SignOutAsync(ILocalDataResetService reset, IDialogService dialogs, ILocalizationService loc, bool uploadFirst, Func<Task> signOut);
        public static Task OfferClearAfterDeletionAsync(ILocalDataResetService reset, IDialogService dialogs, ILocalizationService loc);
        internal static string ComposeMessage(ILocalizationService loc, string messageKey, LocalDataNotices notices);
    }
    ```

Flows:
- **ConfirmSignInAsync** — if `IsResetPending`: `ShowConfirmationDialogAsync(LocalReset_Pending_Title, LocalReset_Pending_Message)`; on yes `RestartAsync()`; return `false` either way. Else if `!HasSyncedLocalDataAsync()` return `true` with no dialog. Else `ShowChoiceDialogAsync(LocalReset_SignIn_Title, ComposeMessage(…SignIn_Message…), LocalReset_SignIn_Keep, LocalReset_SignIn_Clear, Common_Cancel)`: Primary → `true`; Secondary → `ScheduleReset()`, `RestartAsync()`, `false`; Dismissed → `false`.
- **SignOutAsync** — `ShowChoiceDialogAsync(LocalReset_SignOut_Title, ComposeMessage(…SignOut_Message…), LocalReset_SignOut_Clear, LocalReset_SignOut_Keep, Common_Cancel)`. Dismissed → return. If `uploadFirst` and `!await UploadPendingChangesAsync()` and the choice was *clear*: `ShowConfirmationDialogAsync(LocalReset_UploadFailed_Title, LocalReset_UploadFailed_Message)`; on no → return. Then, on *clear*, `ScheduleReset()`; then `await signOut()`; then, on *clear*, `RestartAsync()`.
- **OfferClearAfterDeletionAsync** — `ShowChoiceDialogAsync(LocalReset_Deleted_Title, ComposeMessage(…Deleted_Message…), LocalReset_Deleted_Clear, LocalReset_Deleted_Keep)`; Primary → `ScheduleReset()`, `RestartAsync()`.
- **ComposeMessage** — the base text, then a blank line and `LocalReset_Notice_Vault` when `VaultHasMemories`, then `LocalReset_Notice_KeysLost` for `WillBeLost` or `LocalReset_Notice_KeysMaybeLost` for `MayBeLost`.

English copy (translate faithfully into de and fr; same keys):

| Key | Text |
|---|---|
| `LocalReset_SignOut_Title` | Sign out of Pia Cloud |
| `LocalReset_SignOut_Message` | Do you also want to remove this account's data from this device? This covers chats, history, todos, templates, personas, your own AI providers and synced preferences. Everything stays in your Pia Cloud account and comes back when you sign in again. Clearing restarts Pia. |
| `LocalReset_SignOut_Clear` | Sign out and clear |
| `LocalReset_SignOut_Keep` | Sign out and keep |
| `LocalReset_SignIn_Title` | Before you sign in |
| `LocalReset_SignIn_Message` | There is data on this device. If you keep it, it is uploaded into the account you sign in to. If you clear it first, Pia removes it from this device, including changes made while you were signed out, and restarts. You then need to sign in again. |
| `LocalReset_SignIn_Keep` | Keep and sign in |
| `LocalReset_SignIn_Clear` | Clear first |
| `LocalReset_Notice_Vault` | Pia's memory on this device is not removed and stays available after you switch accounts. |
| `LocalReset_Notice_KeysLost` | API keys for your own AI providers are not stored in Pia Cloud. You will need to enter them again. |
| `LocalReset_Notice_KeysMaybeLost` | API keys for your own AI providers are removed from this device. Unless your account uses end-to-end encryption, you will need to enter them again. |
| `LocalReset_UploadFailed_Title` | Couldn't upload your latest changes |
| `LocalReset_UploadFailed_Message` | Some changes on this device have not reached Pia Cloud. Clear this device anyway? Changes that were not uploaded are lost. |
| `LocalReset_Pending_Title` | Local data not removed yet |
| `LocalReset_Pending_Message` | Pia couldn't finish removing local data. Restart Pia now to try again? |
| `LocalReset_Deleted_Title` | Account deleted |
| `LocalReset_Deleted_Message` | Your Pia Cloud account has been deleted. Do you also want to remove its data from this device? Clearing restarts Pia. |
| `LocalReset_Deleted_Clear` | Clear this device |
| `LocalReset_Deleted_Keep` | Keep on this device |

- [ ] **Step 1: Write the failing tests** (substitutes; `loc[key]` returns the key):

```csharp
[Fact] public async Task ConfirmSignIn_NoLocalData_ProceedsWithoutAsking()          // true; ShowChoiceDialogAsync not received
[Fact] public async Task ConfirmSignIn_Keep_Proceeds()                              // Primary → true; ScheduleReset not received
[Fact] public async Task ConfirmSignIn_ClearFirst_SchedulesAndRestarts_AndStops()   // Secondary → false; Received.InOrder(ScheduleReset, RestartAsync)
[Fact] public async Task ConfirmSignIn_Cancel_Stops()                               // Dismissed → false; nothing scheduled
[Fact] public async Task ConfirmSignIn_WhenResetPending_OffersRestartAndNeverSignsIn() // confirmation yes → RestartAsync; false; HasSyncedLocalDataAsync not received
[Fact] public async Task SignOut_Clear_UploadsSchedulesSignsOutThenRestarts()       // Received.InOrder(Upload, ScheduleReset, signOut, RestartAsync)
[Fact] public async Task SignOut_Keep_UploadsAndSignsOut_WithoutReset()
[Fact] public async Task SignOut_Keep_UploadFailed_StillSignsOut_WithoutAsking()
[Fact] public async Task SignOut_Clear_UploadFailed_Declined_DoesNothing()          // signOut not invoked; ScheduleReset not received
[Fact] public async Task SignOut_WithoutUpload_NeverUploads()                       // uploadFirst false
[Fact] public async Task SignOut_Cancel_DoesNothing()
[Fact] public async Task ComposeMessage_AddsOnlyTheNoticesThatApply()               // (false, None) → base only; (true, MayBeLost) → base + Vault + KeysMaybeLost, in that order
[Fact] public void EveryLocalResetKey_ExistsInAllThreeLanguages()                   // load the three ViewStrings resx files as XML; same LocalReset_* key set, non-empty values
```

- [ ] **Step 2: Build and run `Pia.Tests.ViewModels.AccountSwitchPromptsTests`; expect compile failure.**
- [ ] **Step 3: Implement the close-text parameter, `AccountSwitchPrompts`, and the 19 keys in all three files.**
- [ ] **Step 4: Run `AccountSwitchPromptsTests` and `Pia.Tests.ViewModels.AccountSettingsDeviceGoneTests`; expect PASS.**
- [ ] **Step 5: Commit** — `feat: add the account-switch prompts and their strings`.

---

### Task 8: Wire the prompts into Account settings

**Files:**
- Modify: `src/Pia.Wpf/ViewModels/AccountSettingsViewModel.cs`
- Modify: `src/Pia.Wpf/ViewModels/SettingsViewModel.cs` (new constructor parameter, passed through)
- Modify the construction sites: `tests/Pia.Wpf.Tests/ViewModels/AccountSettingsAccountDataTests.cs`, `AccountSettingsBusinessProfileTests.cs`, `AccountSettingsCreditsTests.cs`, `AccountSettingsDeviceGoneTests.cs`, `AccountSettingsE2EENoticeTests.cs`, `E2EESignInKeyRestoreTests.cs`, `SettingsPolicyReloadTests.cs`, and the one `new SettingsViewModel(` site
- Test: `tests/Pia.Wpf.Tests/ViewModels/AccountSettingsLocalDataResetTests.cs`

**Interfaces:**
- Consumes: `ILocalDataResetService` (Task 6), `AccountSwitchPrompts` (Task 7).
- Produces: `AccountSettingsViewModel` gains a final constructor parameter `ILocalDataResetService localDataReset`.

Changes:
- `LoginWithGoogleAsync`, `LoginWithMicrosoftAsync`, `LoginWithEntraIdAsync`: first line `if (!await AccountSwitchPrompts.ConfirmSignInAsync(_localDataReset, _dialogService, _localizationService)) return;`. `LoginWithPasswordAsync`: the same, after the empty-field check.
- Rename the current `SyncLogoutAsync` body to `private async Task SignOutAsync()`. The `[RelayCommand] SyncLogoutAsync` (keeps `SyncLogoutCommand` for `AccountView.xaml`) becomes `AccountSwitchPrompts.SignOutAsync(…, uploadFirst: true, SignOutAsync)`.
- Device-revoked handler, `DialogChoice.Secondary`: `AccountSwitchPrompts.SignOutAsync(…, uploadFirst: false, SignOutAsync)`.
- `DeleteAccountAsync`, `Deleted` branch: `await SignOutAsync();`, then the snackbar, then `await AccountSwitchPrompts.OfferClearAfterDeletionAsync(…)`.

- [ ] **Step 1: Write the failing tests:**

```csharp
[Fact] public async Task SignIn_WhenClearFirstChosen_NeverReachesAuth()     // HasSyncedLocalData true, dialog Secondary → LoginAsync("google") not received
[Fact] public async Task SignIn_WithNoLocalData_SignsIn()                   // LoginAsync("google") received once
[Fact] public async Task SignOutButton_Keep_UploadsThenLogsOut()            // Received.InOrder(UploadPendingChangesAsync, LogoutAsync); ScheduleReset not received
[Fact] public async Task RevokedSignOut_PromptsWithoutUploading()           // first choice dialog Secondary (revoked), second Primary (clear) → UploadPendingChangesAsync not received; ScheduleReset, LogoutAsync, RestartAsync received
[Fact] public async Task DeletedAccount_SignsOutThenOffersClear()          // DeleteAsync → Deleted; LogoutAsync received; choice with LocalReset_Deleted_Title shown
```

Match the dialogs on their title argument (`"LocalReset_SignOut_Title"`, `"Settings_E2EE_Gone_Title"`, …) so each stub answers only its own dialog.

- [ ] **Step 2: Build; expect compile failure in the new tests.**
- [ ] **Step 3: Implement; add `Substitute.For<ILocalDataResetService>()` to every listed construction site.** An unconfigured substitute returns `false` from `HasSyncedLocalDataAsync`, so existing sign-in tests are unaffected. One existing test does change: `AccountSettingsDeviceGoneTests.ChoosingSignOut_EndsTheSession` now meets the sign-out prompt after the revoked dialog, whose call carries a fifth `closeText` argument its four-argument stub does not match — stub the `LocalReset_SignOut_Title` dialog to answer `Secondary` (keep) so `LogoutAsync` is still received.
- [ ] **Step 4: Run `AccountSettingsLocalDataResetTests` and each modified test class; expect PASS.**
- [ ] **Step 5: Commit** — `feat: ask about local data on account sign-in and sign-out`.

---

### Task 9: Wire the sign-in prompt into the first-run wizard

**Files:**
- Modify: `src/Pia.Wpf/ViewModels/FirstRunWizardViewModel.cs`
- Modify: `src/Pia.Wpf/App.xaml.cs` (`ShowFirstRunWizardAsync`)
- Modify the construction sites: `tests/Pia.Wpf.Tests/ViewModels/E2EESignInKeyRestoreTests.cs`, `FirstRunWizardProviderLockTests.cs`, `tests/Pia.Wpf.Tests/Views/FirstRunWizardWindowParseTests.cs`, and any fixture in `FirstRunWizardViewModelTests.cs`
- Test: `tests/Pia.Wpf.Tests/ViewModels/FirstRunWizardLocalDataResetTests.cs`

**Interfaces:**
- Consumes: `AccountSwitchPrompts.ConfirmSignInAsync` (Task 7).
- Produces: `FirstRunWizardViewModel` gains `IDialogService dialogService` and `ILocalDataResetService localDataReset` as its last two constructor parameters (the wizard's DI scope resolves the scoped `IDialogService` bound to `FirstRunWizardWindow`'s dialog host).
- Produces: `internal static bool App.ShouldMarkWizardCompletedOnClose(bool? dialogResult, bool resetPending) => dialogResult != true && !resetPending;` used by `ShowFirstRunWizardAsync` with `LocalDataResetMarker.IsPending`.

Changes: `LoginAsync(string provider)` and `LoginWithPasswordAsync` (after its empty-field check) start with the same `ConfirmSignInAsync` guard as Task 8.

- [ ] **Step 1: Write the failing tests:**

```csharp
[Fact] public async Task WizardSignIn_WhenClearFirstChosen_NeverReachesAuth()
[Fact] public async Task WizardPasswordSignIn_WhenCancelled_NeverReachesAuth()
[Theory, InlineData(null, false, true), InlineData(false, false, true), InlineData(true, false, false), InlineData(null, true, false)]
public void ShouldMarkWizardCompletedOnClose(bool? dialogResult, bool resetPending, bool expected)
```

- [ ] **Step 2: Build; expect compile failure.**
- [ ] **Step 3: Implement; update the construction sites with substitutes.**
- [ ] **Step 4: Run `FirstRunWizardLocalDataResetTests`, `FirstRunWizardViewModelTests`, `FirstRunWizardProviderLockTests`, `FirstRunWizardWindowParseTests`, `E2EESignInKeyRestoreTests`; expect PASS.**
- [ ] **Step 5: Commit** — `feat: ask about local data before signing in from the first-run wizard`.

---

### Task 10: The first sync after a clear keeps the account's preferences

**Files:**
- Modify: `src/Pia.Wpf/Services/SyncClientService.cs` (`PerformFirstSyncMigrationAsync`, `PushChangesAsync`, `SyncNowAsync`)
- Test: `tests/Pia.Wpf.Tests/Services/SyncClientServiceTransferOptimizationTests.cs`

**Interfaces:**
- Consumes: `AppSettings.SuppressSettingsPushAfterReset` (Task 2).

Changes:
- First sync: `Settings` stays `null` in every batch while the flag is set, and the settings hash is not persisted.
- Delta push: treat settings as unchanged while the flag is set.
- Both paths: in the save that advances `LastSyncTimestamp` after a successful pull, also set `SuppressSettingsPushAfterReset = false`.

- [ ] **Step 1: Write the failing tests:**

```csharp
[Fact] public async Task FirstSync_AfterReset_OmitsSettings_ThenClearsTheFlag()   // decompress the captured push: no "settings" property; after a 200 pull, flag false
[Fact] public async Task FirstSync_AfterReset_PullFails_KeepsTheFlag()
[Fact] public async Task DeltaPush_AfterReset_OmitsSettings()                     // LastPushedSettingsHash null, flag true → body has no settings
[Fact] public async Task FirstSync_WithoutReset_StillSendsSettings()               // regression guard
```

- [ ] **Step 2: Build and run the class; expect the new tests to fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run the class and `Pia.Tests.Services.SyncClientServiceE2EEWindowTests`; expect PASS.**
- [ ] **Step 5: Commit** — `fix: never push reset preferences over the account's on the first sync after a clear`.

---

### Task 11: Gate and manual check

**Files:** none new; ticks the checklist.

- [ ] **Step 1: Zero warnings.** `dotnet build -t:Rebuild -v:n > build-debug.log 2>&1` and `dotnet build -t:Rebuild -c Release -v:n > build-release.log 2>&1`; each summary reads `0 Warning(s)` and `0 Error(s)`.
- [ ] **Step 2: Full test gate.** `tests/Pia.Wpf.Tests/bin/Debug/net10.0-windows10.0.17763.0/Pia.Wpf.Tests.exe > test.log 2>&1`; summary shows `Failed: 0`. A failure in a known flaky class (see `xunit-exe-filters-and-across-kinds` memory) gets one re-run of that class before anything else.
- [ ] **Step 3: Manual run against a throwaway profile** (`PIA_DATA_DIR` / `PIA_LOCAL_DATA_DIR` set to temp folders, the e2e stack on 18081 — never the 8081 dev stack). Sign in, create a template and a todo, sign out → *Sign out and clear* → Pia restarts → template and todo gone, wizard state intact. Sign in to a second account with data present → *Clear first* → restart → sign in again, no prompt. Close the wizard via *Clear first* → the wizard returns after the restart.
- [ ] **Step 4: Commit** the ticked checklist — `docs: tick the account-switch cleanup checklist`.
