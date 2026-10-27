# Clear local synced data on account switch — design

**Status:** Approved by the owner, 2026-10-20 — next: implementation plan
**Owner:** Marco Altmann
**Written:** 2026-10-20
**Origin:** Owner request (brainstorming session, 2026-10-20): switching one device between a
private and a work Pia Cloud account leaves the two accounts' data mixed.

## Why

One person, two Pia Cloud accounts (private and work), one device, switching between them now
and then. Today sign-out clears only auth and sync-cursor state (`AuthService.LogoutAsync`); every
template, chat, memory, todo and so on stays on disk. The next sign-in runs
`SyncClientService.PerformFirstSyncMigrationAsync`, which pushes **every** local row with no
dirty filter into whichever account just signed in, then pulls that account's data on top. The
private account's chats land in the work account and the other way round.

The fix is to let the user drop the local copy of everything that syncs when leaving an account,
and to make sure an interrupted drop finishes before any account can sign in.

## Goals

- **At sign-out**, the user may choose to clear all local data of the kinds Pia Cloud syncs.
- **Before sign-in**, if such data is on the device, the user may choose to clear it first.
- **A persistent marker** guarantees a chosen clear completes before any sign-in or first-run
  setup — including after a crash, a kill, or a power loss mid-way.
- After a clear, signing in to either account restores that account's data by a normal full
  download. That re-download on every switch is accepted.

## Non-goals

- No account awareness. Pia does not remember which account the local data came from and does not
  skip the question when signing back into the same account. It asks whenever local synced data
  exists. (Owner: "we do not need to make it smart.")
- No per-account local profiles; one local store, emptied on request.
- Nothing that does not sync is touched (see *What stays*).
- The existing *Settings → General → Reset app data* button (`GeneralSettingsViewModel.ResetAppDataAsync`)
  is out of scope and unchanged.

## Anti-goals

- **A clear must never delete anything on the server.** Every existing per-entity delete
  (`Delete*Async` with the default `trackForSync: true`, `KanbanColumnService.DeleteAsync`, which
  always tracks, and `AssistantChatService.DeleteAllAsync`, which raises server DELETEs through
  `ChatsChanged`) would turn a local clear into an account wipe. None of them may be used.
- **Keeping must not feel dangerous.** For someone who used Pia offline and is signing in for the
  first time, *keep* is the normal answer; the wording must not push them toward clearing.
- A half-finished clear must never be followed by a sign-in. The marker is what makes that true.

## Decided by the owner

1. Sign-out offers *clear local synced data*; declining is today's behaviour.
2. A clear is guarded by a persistent flag and runs before any new profile can be set up.
3. Sign-in offers the same clear beforehand, on every sign-in path.
4. No smart account matching — ask whenever local synced data exists.
5. Clear only synced items, and all of them — including the synced preference fields in
   settings (theme, languages, default template, …). Nothing beyond the synced set.
6. On the sign-out path, run one last sync before clearing so unsynced edits are not lost. If that
   sync fails, warn and let the user choose to clear anyway or cancel.
7. Device-only data stays; full re-download after a switch is fine.
8. Pia's memory vault (the notes `remember`/`recall` work on) does not sync, so it stays. When it
   holds memories, the dialogs must say so correctly — never claim memories are removed.
9. Device-only fields on synced rows are lost with the row and are not preserved: provider API
   keys on accounts without E2EE (`SyncMapper.ToSyncProvider` never sends them), the provider
   tuning fields `SyncMapper.PreserveDeviceLocalProviderFields` carries over, and the device-only
   scheduled-job columns. The user re-enters them after a switch. No side store.
10. Both dialogs say up front that choosing *clear* restarts Pia, and the sign-in dialog says the
    user has to sign in again after the restart.

## Design

### Core call: marker file → restart → clear at early startup

*Designer's call — user-visible, flagged for the owner.* Choosing *clear* writes a marker file and
restarts Pia. On the next start, `Bootstrapper.InitializeAsync` sees the marker and clears the
stores **before** anything else reads them, then deletes the marker.

Why not clear in-process:

- The settings, providers and templates JSON stores serve a cached instance
  (`JsonPersistenceService._cached`) with no public reset; deleting their data underneath leaves
  stale copies that get written back.
- `AssistantChatSyncService` keeps an in-memory op queue (`_desired`) and each window's
  `ChatSessionManager` can re-save a deleted chat at the end of a turn. Either can resurrect a
  cleared chat or push it into the next account.
- `SyncDeleteTrackerService` loads its pending deletes once in its constructor; deleting the file
  does not clear the in-memory set.
- Open `history.db` connections (chat service, flow store, run stores) make file-level deletion
  impossible while running.

A restart discards all of that state for free. The marker check runs after `BuildServiceProvider`
and before the first store read (`IPolicyService.GetPolicyAsync`, currently the first thing
resolved after the bootstrap log lines). That is before `AuthService` is constructed (first
resolved through the `AutoIngestService` → `IProviderService` chain), before the first-run wizard
decision in `App.OnStartup`, and before any sign-in UI — which is the owner's "before a new
profile can be set up" guarantee.

Restart through `IAppRestartService.RestartAsync` (stops background sync, tears down the tray,
calls `App.RequestRestart`), not the `Process.Start` + `Environment.Exit` that *Reset app data*
uses. Its log line says "to apply a changed policy"; make the reason a parameter or neutral.

### The marker

- A file in the roaming data root, next to `settings.json`, exposed as a `PiaPaths` **property**
  (CLAUDE.md: never a `static readonly` field). Roaming so it travels with the sign-in state it
  belongs to. *Designer's call.*
- Not a field in `settings.json`: that file is itself a clear target and is read through a cached
  service; the startup check must be a plain `File.Exists` with nothing resolved.
- Presence is the whole signal. Write it atomically (temp file + move) so a crash cannot leave a
  half-written marker that a reader misjudges.
- Deleted only after every store has been cleared successfully.

### What gets cleared

Everything the two sync engines push or pull for an account. Implement the clear as plain local
writes — SQL statements and file operations — never through the per-entity service deletes.

**Roaming root (`%APPDATA%\Pia`, routed through `PiaPaths`):**

| Store | Action |
|---|---|
| `templates.json` | Delete. It holds custom templates only; built-ins are generated. |
| `providers.json` | Remove every row except the Pia Cloud provider. `App.OnStartup` re-runs `EnsureBuiltInProviderAsync` and `RepairModeDefaultsAsync` afterwards. |
| `pending-sync-deletes.json` | Delete. Pending deletes belong to the account being left. |
| `settings.json` | Field-level reset, below. Never delete the file: it holds `HasCompletedFirstRunWizard`, `ServerUrl`, `SyncDeviceId`, `AssistantFilesFolder` and other device state. |
| `policy-cache.json` | Already cleared by `LogoutAsync`; clear again in case sign-out never completed. |

**Settings fields reset to their `new AppSettings()` defaults:**

- The 13 synced fields — exactly the list in `SyncMapper.BuildSettingsPlainPayload`
  (`DefaultOutputAction`, `DefaultTemplateId`, `WhisperModel`, `AutoTypeDelayMs`, `Theme`,
  `StartMinimized`, `TargetLanguage`, `TargetSpeechLanguage`, `DefaultWindowMode`,
  `ModeProviderDefaults`, `ModePersonaDefaults`, `UseSameProviderForAllModes`,
  `AssistantDefaultWorkingDirectory`). Derive the list from that method or guard it with a test,
  so a newly synced field cannot be missed.
- Account-scoped sync latches that `LogoutAsync` does not reset today:
  `ManagedPersonaStoreInitialized`, `LastCatalogVersion`, `AssistantChatsEncryptedResendAt`,
  `BlankedSyncRowRepairAt`.
- Everything `LogoutAsync` clears (tokens, `SyncEnabled`, user id/email/name/provider, sync
  cursors and ETags, `LastPushedSettingsHash`, `ClientPolicyInitialized`, the E2EE key). Normally
  already done; repeated here for the crash case described under *Order of operations*.

**`history.db` (`%LOCALAPPDATA%\Pia`, encrypted):**

| Table | Action |
|---|---|
| `Personas` | Delete all rows (built-ins are in memory only). |
| `ManagedPersonas` | Delete all rows; re-pulled from the account (latch reset above). |
| `Sessions` | Delete all rows. |
| `AssistantChats` + `AssistantChatMessages` | Delete all rows; messages and `AgentRuns` with its children follow by FK cascade. `AssistantChatsFts` is not trigger-maintained — clear it explicitly. |
| `Memories` | Delete all rows; `MemoriesFts` and the `Embedding` column go with them (triggers). |
| `Todos` | Delete all rows. |
| `KanbanColumns` | Delete all rows, then re-insert the two seed columns exactly as `SqliteContext.EnsureSchema` seeds them (fixed ids `…0001` "To Do", `…0002` "Closed"), but with an old `UpdatedAt` (Unix epoch) — see *First sync after a clear*. Never leave the table empty: `GetDefaultViewColumnAsync` / `GetClosedColumnAsync` throw on an empty table and the todo pull calls them. `EnsureSchema` only seeds an empty table on the first connection of a process, so the clear must do it itself. Extract the seed into one shared method. |
| `ScheduledJobs` | Delete rows whose kind is not `MeetingAttendance` (those are device-local and never sync). |
| `Plugins` | Delete server-catalog rows that are not preloaded built-ins (`BuiltInPluginDefaults.PreloadedPluginIds`); reset `UserEnabled` on the preloaded ones. Keep user-configured local MCP servers — they are not part of sync. The builder confirms how local MCP rows are told apart (`Kind` / `IsPreloaded`). |

Open the database through `SqliteContext` (its constructor needs only a logger and runs
`HistoryDatabaseEncryption.Prepare`), and run the deletes in one transaction. Resolving it early
must not pull in `AuthService`; a test should pin that.

Rows elsewhere that point at cleared rows (`FlowItems.ActionEntityId`, `ScheduledJobs.ProviderId`,
`Personas.PreferredProviderId`, …) are left alone: readers already tolerate missing targets, and
`DefaultTemplateId` / the mode-default maps are in the reset set.

### First sync after a clear

Signing in after a clear must restore the account's data, not overwrite it with this device's
fresh defaults. Two things in today's first sync would do exactly that:

- `PerformFirstSyncMigrationAsync` always pushes the settings row before it pulls, stamped
  `ModifiedAt = now`, and the server stores a pushed settings row **unconditionally** — no
  timestamp comparison (`Pia.Server/Sync/SyncService.cs`, the "Process settings" block). The
  account's theme, languages, default template and mode defaults would reset on every one of its
  devices.
- The reseeded kanban columns would be pushed too. The server does compare timestamps for
  columns, so seeding them with an epoch `UpdatedAt` lets the account's renamed columns win.

Required: the clear sets a one-shot latch in settings (*designer's call*). While it is set, every
push — the first-sync migration and the delta push alike, since a manual *Sync now* can run
before the first sync — omits `Settings`. The latch is cleared in the same save that advances
`LastSyncTimestamp` after a successful pull, i.e. once the account's settings have been applied. A brand-new device has the
same settings problem today; fixing that is outside this change.

### What stays

So nobody "helpfully" widens the clear:

- The markdown memory vault and the assistant files folder (not synced by this client), and the
  `Chunks` / `ChunksFts` / `IngestState` index over it.
- `Reminders`, `MeetingAttendance` scheduled jobs, `FlowItems`.
- Device E2EE keys in the Windows key store, `e2ee-confirmed-approvers.json`, `SyncDeviceId`.
- `Backups\`, logs, diagnostics, recordings, consent audit and evidence, models, plugins folder.
- Every non-synced settings field.

### "Is there local synced data?"

The sign-in prompt appears only if at least one of these holds; otherwise sign-in proceeds as
today with no question:

- a custom template;
- a provider other than Pia Cloud;
- a user persona;
- any row in `Sessions`, `AssistantChats`, `Memories` or `Todos`;
- a kanban column other than the two seeds, or a seed column whose name or order differs from
  the seed;
- a scheduled job that is not `MeetingAttendance`.

Settings fields do not count — they always exist. The PersonalProfile memory that the first-run
wizard and OAuth sign-in create will trigger the question; acceptable under "not smart".

### Sign-out

Paths that **prompt**:

| Path | Final sync first? |
|---|---|
| *Sign out* button (`AccountSettingsViewModel.SyncLogoutCommand`) | Yes |
| *Sign out* choice in the device-revoked dialog | No — the device can no longer push |
| After a successful *Delete account* | No — the account is gone |

Paths that **do not prompt**: `AuthService.SignOutUnexpectedlyAsync` (session expired or
rejected) and the policy-disallowed sign-in method at startup. The sign-in prompt covers them.

Dialog — reuse `IDialogService.ShowChoiceDialogAsync`:

- Title: *Sign out of Pia Cloud*
- Message: says that clearing removes this device's copy of the synced data (name the kinds:
  chats, history, todos, templates, personas, AI providers, synced preferences), that the data
  stays in the account and returns on the next sign-in, and that Pia restarts to finish.
- Conditional lines, shown only when they apply (same rule for the sign-in dialog):
  - The memory vault holds memories → *Pia's memory on this device is not removed and stays
    available after you switch accounts.* Never list "memories" among the removed kinds.
  - E2EE is off and a provider that would be cleared has an API key → *API keys for your own AI
    providers are not stored in Pia Cloud; you will need to enter them again.*
  - At sign-in nobody is signed in and `ForgetE2EEKey` has already reset `IsE2EEEnabled`, so the
    account's mode is unknown. If a provider has an API key, the line says the keys must be
    re-entered *unless the account uses end-to-end encryption*.
- Primary: *Sign out and clear* · Secondary: *Sign out and keep* · Third: *Cancel* (nothing happens).

`ShowChoiceDialogAsync` hard-codes its third button to `Common_Close`. Add an optional
close-button text parameter so this dialog reads *Cancel*. *Designer's call.*

For *Delete account* the choice is only clear/keep (the sign-out is not optional); reuse the same
dialog with a message that says the account is deleted. *Designer's call.*

### Sign-in

Ask **before** authentication starts — inside each command that starts a sign-in, ahead of the
`AuthService.Login*Async` call, not in `HandlePostLogin*` (by then the account is already signed in).

- `AccountSettingsViewModel`: Google, Microsoft, Entra ID, email/password.
- `FirstRunWizardViewModel`: `LoginAsync(provider)`, `LoginWithPasswordAsync`.
- Registration only opens the browser; the user then signs in through one of the above. Business
  profile submission and `HandleAccountAlreadyEncryptedAsync` are post-auth and do not ask.
- `FirstRunWizardViewModel` does not inject `IDialogService` today; add it. The wizard window has
  a `ContentDialogHost`.

Dialog:

- Title: *Before you sign in*
- Message: explains there is data on this device, that *Keep* uploads it into the account being
  signed into, and that *Clear* removes it from this device first (Pia restarts).
- Primary: *Keep and sign in* · Secondary: *Clear first* · Third: *Cancel*.
- *Keep* is primary on purpose (anti-goal 2).

*Clear first* writes the marker and restarts. After the restart the user starts the sign-in again;
with nothing local any more, no question is asked. Say so in the dialog text, and say plainly
that changes made on this device while signed out are removed too — nothing can be uploaded at
this point, because no account is signed in.

The restart closes the first-run wizard, and closing the wizard currently counts as *skip* and
marks it completed (`App.ShowFirstRunWizardAsync`). Guard that, so the wizard reappears after a
clear started from inside it.

### Order of operations

Sign-out from the button — the final sync runs for **both** *clear* and *keep* (*designer's
call*), so data kept on the device is also safe in the account if the user clears it later at a
sign-in:

1. Stop background sync and wait (`StopBackgroundSyncAndWaitAsync`), so the final sync is not
   skipped for a cycle already holding the sync lock.
2. Final sync, covering both engines:
   - `SyncClientService.SyncNowAsync`. It can report success while the push failed; judge by the
     push outcome, not the returned result.
   - `AssistantChatSyncService`: its queue (`_desired`) is in memory only, failed uploads are
     logged and dropped, and the initial upload of older chats (`AssistantChatsBackfilledAt`) is
     rate-limited and can span launches. Add a flush-and-wait with a timeout that reports whether
     every chat reached the server; un-uploaded or not-yet-backfilled chats count as a failure.
     The chat worker itself need not be stopped: after sign-out it can no longer push, and on
     *clear* the restart ends it.
3. If any part failed, or was skipped because E2EE is not ready: on *clear*, show *Couldn't upload
   your latest changes* with *Clear anyway* / *Cancel*; on *keep*, sign out anyway (nothing is
   lost — the data stays on the device). *Cancel* resumes background sync.
4. On *clear*: write the marker.
5. `AuthService.LogoutAsync` (revokes the refresh token on the server).
6. On *clear*: restart.

Writing the marker before sign-out means a crash between 3 and 4 still clears on the next start.
That start must also finish the sign-out locally (the settings reset above clears the tokens); the
server-side revoke is lost in that one case, which is acceptable — the token is dropped locally.

Sign-in with *clear*: write the marker, restart.

Startup with the marker present: clear every store, then delete the marker, then continue the
normal startup. Log counts only (privacy rules in CLAUDE.md — no titles, names or URLs).

### Failure handling

- If the startup clear throws, leave the marker in place, log the error, and continue starting.
- While the marker exists, every sign-in command refuses with a message (*Pia couldn't finish
  removing local data. Restart Pia to try again.*) offering a restart. Background sync cannot run
  because the user is signed out.
- The clear must be idempotent: running it again on a half-cleared store converges.

## Constraints

- Strings in `ViewStrings.resx`, `ViewStrings.de.resx` and `ViewStrings.fr.resx`.
- No new interactive controls are planned; if any are added, they need AutomationIds and rows in
  `ViewAutomationIdTests`.
- Paths through `PiaPaths` only; never `Environment.GetFolderPath`. Add the marker to
  `PiaPathsTests` / `DataDirectoryRoutingTests`.
- Privacy-first logging: counts and ids at most.
- Zero warnings in Debug and Release; test gate `Failed: 0` via the built exe.
- ViewModels reach the new logic through a service interface; follow the existing layer rule.
- This is M-sized: the plan phase also writes a `-checklist.md` in this folder.

## Left to the builder

- Names and shape of the new service(s) and of the marker property.
- Exact dialog copy, within the intent above.
- Whether the startup clear lives in its own class called from `Bootstrapper` (recommended, so it
  is testable against a temp `PIA_DATA_DIR` / `PIA_LOCAL_DATA_DIR` and a temp `history.db`).
- Test layout. At minimum: clear empties every listed store and leaves every *What stays* item;
  kanban seeds survive; a marker left by a simulated crash is honoured on the next start; a failing
  clear keeps the marker and blocks sign-in; the "has data" check; the settings field list matches
  `BuildSettingsPlainPayload`; the post-clear first sync omits `Settings` and clears its latch
  only on success; the vault and API-key lines appear exactly when they apply.

## Follow-ups outside this change

- The Pia.Docs desktop guide's sign-out/sign-in text likely needs a line; the `help-corpus` skill
  checks that on the release branch.
