# Applying a server-delivered policy sooner — design

**Status:** Analysis complete. All design decisions settled by the owner 2026-08-20; nothing open. NOT implemented — review pending.
**Date:** 2026-08-20
**Repo:** `C:\projects\Pia.Wpf`, branch `feature/agent-run-spine`. Server checked at `C:\projects\Pia`, branch `feature/group-client-policy`.
**Predecessor:** `docs/2026-08-20-policy-apply-latency-analysis-brief.md` (grounding) and
`docs/2026-08-20-group-client-policy-wpf-handoff.md` (the shipped contract, whose §5/§8 this reverses).

---

## 1. The decision, in one paragraph

**Apply live. The restart surface is a safety net for a set of one.** A per-key audit of all 100
settable `AppSettings` properties says that of the 70 an admin can actually reach from the server, **41
already take effect mid-session with no _per-key_ wiring** (they need the shared coordinator of §6.3, and
nothing else), **20 need a small, named piece of wiring each, 4 have no runtime meaning, and exactly 1 —
`privacy` — genuinely cannot be applied without a restart** (and even that becomes live if a latch in
`TokenizingAiClientService` is reset). The owner has chosen a **blocking overlay with no dismiss and no
"Not now"** for that residue (§6.4). Because there is no escape hatch, the safety moves into *when the
overlay appears*: it must defer while a recording, an agent run or a streaming turn is live, or it paints
over the only controls that could save them. Drive it from an explicitly declared restart-required key
list with a test that keeps the list honest — under a forcing overlay a misclassified key blocks the app.

The most user-visible win is the one the brief buried in its §2: **today a fresh enterprise install needs
two launches** — one to sign in and receive the policy, one to apply it. Phase 1 removes that.

Detection latency is unchanged and is the real floor: **10 s after start, then 5 min growing to a 15 min
cap.** "Live" means "within one sync cycle", never "immediately". No push channel exists and §10 of the
brief rules out a server change, so any UI copy promising instant enforcement would be wrong.

---

## 2. The brief's blocking question is answered: yes

`AdminGroupService.SetClientPolicyAsync` calls `versionCache.BumpCatalogExplicitAsync(db)` immediately
after saving the document, with the comment *"Group is not a watched type in SyncStateInterceptor, so this
write bumps nothing on its own and the pull's catalog fast-skip would keep serving the old document
indefinitely."* The document reliably arrives. The brief's *"if it does not, that is the real bug and the
rest of this brief is premature"* is void — do not re-open it.

Two consequences worth pricing, both invisible from the client:

- `BumpCatalogExplicitAsync` bumps a **global** counter, and `CatalogVersionMix.ForGroup` hashes that
  counter together with the group id. So **one group's policy write invalidates the catalog token for
  every user in every group** on the instance — a fleet-wide forced full-catalog re-pull per policy edit.
  That is the cost ceiling on any "pull more often" latency lever below.
- `SyncClientPolicySnapshot.UpdatedAt` reaches the client but is dropped at the call site, is `null`
  whenever the document is `{}`, and is a server clock. Do not use it as the change signal.

---

## 3. Corrections to the brief

| Brief says | Actually |
|---|---|
| `Is…Enforced` getters live in 5 VMs incl. `FirstRunWizardViewModel` | 11 getters in **4** VMs. `FirstRunWizardViewModel` has none — its policy surface is 4 `IsLoginProviderAllowed` getters plus a provider-step lock |
| "8 view models" hold the lock surface | **8** VMs hold `PolicyLock Policy`; **27** `Policy[X]` bindings across 4 XAML files |
| The unsafe-to-restart enumeration is a required output | Still required, for a different reason. No safe-moment machine is needed (the user presses the button), but the owner's "no *Not now*" makes the list the overlay's **deferral gate** — §6.4, §7.2 |
| `IsThemeEnforced` is part of the lock surface | It is **dead** — declared at `GeneralSettingsViewModel.cs:72`, the only reference in `src/` or `tests/`. And it could not have been bound there: **there is no theme control in Settings** — the toggle is a nav sidebar item on `MainWindowViewModel`. Fixed in §11.1 |
| The value half is nearly free because `GetSettingsAsync` re-applies policy | Nearly free, but **only with a `Save`**. `SettingsChanged` is raised from `SaveSettingsAsync` alone (`SettingsService.cs:48`); `GetSettingsAsync` applies the policy silently. Every event-fed consumer needs the raise |

---

## 4. Answers to the brief's §9 questions

1. **Does the server bump `catalogVersion` on a policy write?** Yes — §2.
2. **What is the actual target?** "Within one sync cycle" is the only achievable target: 10 s to 15 min,
   set by `SyncClientService`'s cadence. §9 lists two optional client-only levers if that is too slow.
3. **Is an unprompted restart acceptable?** No — the owner's wording is "force the *user* to restart",
   so the user always presses the button. Brief option 3 (automatic restart at a safe moment) is dropped,
   and with it most of the brief's §6.
4. **What is the complete unsafe-to-restart list?** Enumerated in §7.2. It is load-bearing after all —
   not to pick a safe moment for an automatic restart, but as the **deferral gate on the overlay** (§6.4),
   because the owner's "no Not now" removes the escape hatch that would otherwise cover it. The critical
   entry: a live meeting transcript exists only in memory and cannot be saved while recording
   (`TranscriptOverlayViewModel.cs:426`, `CanSaveTranscript() => !IsRunning && …`).
5. **If the user declines indefinitely, what happens?** They cannot — the overlay has no dismiss, so the
   app is unusable until they restart. Everything live (essentially all of it) is already applied by then;
   only the declared residue is waiting. Because the flag is derived from "the applied document ≠ the
   arrived document", a restart dissolves the condition and a *second* change re-arms the overlay
   automatically. Nothing restarts itself.
6. **Does the answer differ for `enforce` vs `defaults`?** Yes, and it falls out of a proof rather than a
   preference: **removing a key from `enforce` moves no value** (nothing records the pre-enforcement value,
   so the enforced value simply becomes the user's value). An unpin is therefore a **lock-only** change and
   is always live-applicable, never restart-worthy. Only keys whose value actually *moves* and whose
   consumer cannot re-read it can ever need a restart.
7. **Does this change the logout path?** Yes, and it is mandatory, not optional — §8.4.

---

## 5. The classification

Method: enumerate all 100 settable `AppSettings` properties (the predicate `PolicyService` itself uses),
grep every read of each, and judge whether a mid-session value change reaches the consumer. Every "live"
verdict was then adversarially re-checked by a second pass instructed to default to `restart-required`;
**22 verdicts were corrected**, all but three in the conservative direction. The full per-key table is
`docs/2026-08-20-policy-key-liveness-inventory.md`.

Of the 100, **31 are in `ClientPolicyContract.DeniedKeys`** and can never arrive from the server
(`PolicyService` loads the server layer with `allowDeviceSettableKeys: false`, and the server refuses to
store them). That leaves **70 server-reachable keys**:

| verdict | count | meaning |
|---|---|---|
| `live-already` | 41 | an existing consumer re-reads per use or on `SettingsChanged`. Covered by the coordinator's `Get`+`Save` alone |
| `live-with-work` | 20 | one named piece of wiring each (§5.2) |
| `no-runtime-effect` | 4 | `DefaultProviderId`, `LastActiveView`, `FlowPinned`, `TodoColumnWidths` |
| `restart-required` | 5 | of which only **1** matters (§5.1) |

### 5.1 The restart-required five, and why only one counts

| key | why | verdict for the overlay |
|---|---|---|
| `Privacy` | `TokenizingAiClientService.IsEnabledAsync` latches `_enabled` and `_initialized` on first use and never resets; the instances are transient but held as `readonly` fields by `AssistantViewModel`, `ChatSessionManager`, `ProviderService` and `SuggestionService`, so in practice the latch lasts the process. `PiiKeywords` are snapshotted per chat session. | **Restart required** — or fix the latch (§8.6) and it is live |
| `StartMinimized` | Read once at `App.xaml.cs:132` | **No overlay.** Nothing misbehaves in-session; the value governs the next launch, which is when it is read anyway |
| `HasCompletedFirstRunWizard` | Read only in the startup path | **No overlay.** Same reasoning; setting it mid-session is meaningless as a value change |
| `MeetingAttendeeEnabled` | Sole reads are `AssistantViewModel.cs:409-410`, in `ApplyMeetingFeaturePolicyAsync`, fire-and-forget from the ctor | **Reclassify to `live-with-work`** — one `SettingsChanged` handler re-invoking that existing method |
| `DirectTranscriptionEnabled` | Same two lines | Same |

So the honest restart list for server-delivered policy is **`{ Privacy }`**, and zero if §8.6 lands. The
device-file layer (`policy.json` dropped by GPO/Intune) genuinely *is* restart-only — there is no
`FileSystemWatcher` on it — but nothing detects a mid-session drop either, so the overlay would never fire
for it as designed. That layer stays out of scope.

### 5.2 The 20 `live-with-work` keys, grouped by the work

**(A) Covered by the coordinator's `Save` alone** — an existing `SettingsChanged` subscriber already does
the right thing, it just never gets raised today: `DefaultTemplateId` (`OptimizeViewModel.cs:476`),
`AlwaysAllowedTools` (`ToolPermissionService.cs:200`), `AssistantFileToolsEnabled` and
`AssistantGitToolsEnabled` (value half: `FilesToolHandler.cs:99` / `GitToolHandler.cs:78`, plus the
per-turn `GetAllTools` lazy delegate and `PluginService`'s route rebuild).

> This group is the reason §3's last row matters. Without the `Save`, an enforced
> `AssistantFileToolsEnabled = false` leaves every `write_file` route registered and executable for the
> life of the process while the UI reports it as enforced. `FilesToolHandler.HandleToolCallAsync` never
> consults `IsAvailable` — its only guard is folder existence.

**(B) One new `SettingsChanged` subscriber each**, in the service or VM that already owns the live-change
primitive:

| key(s) | subscriber | primitive that already exists |
|---|---|---|
| `LaunchAtStartup` | new handler | `IAutostartService.Enable/Disable`; lift the `App.xaml.cs:121-123` reconcile into it |
| `OptimizeHotkey`, `AssistantHotkey`, `FastPathHotkey` | `TrayIconService` (has no subscription today) | `UpdateHotkey` / `UpdateFastPathHotkey` |
| `UiLanguage` | new handler | `ILocalizationService.SetLanguage` — the live swap already works |
| `TtsVoiceModelKey` | `TtsService` | `LoadVoiceAsync`; needs a not-downloaded fallback |
| `AutoIngestSources` | `AutoIngestService` | `Stop()` / `RestartAsync` |
| `TargetLanguage` | `OptimizeViewModel` | extend the existing `OnSettingsChanged` |
| `TtsEnabled`, `AssistantSuggestionsEnabled`, `AssistantAgentModeDefault`, `AssistantDefaultWorkingDirectory`, `MeetingAttendeeEnabled`, `DirectTranscriptionEnabled` | `AssistantViewModel` | the existing `OnNavigatedToAsync` / `ApplyMeetingFeaturePolicyAsync` / `SeedAgentModeFromSettings` bodies |
| `AllowProviderManagement`, `AllowPersonaManagement` | `ProvidersSettingsViewModel` / `PersonaSettingsViewModel` | re-run `InitializeAsync`'s gate assignment |

Two traps in group (B): hotkey *hints* are already live via `MainWindowViewModel.UpdateHotkeyHints`, so
today a policy change would display the new chord while the old one stays registered; and there is **no
"reopen the window" escape hatch** — `WindowManagerService` cancels `Closing` into a hide, so a scoped VM
lives for the process and stale state never self-heals.

**(C) Needs a policy-specific route, not a plain subscriber:**

- **`AssistantFilesFolder`** — a raw value write is split-brain. Tools re-point live but the vault root is
  set only by `VaultPathProvider.SetRoot` at bootstrap or inside
  `AssistantFolderRelocationService.MoveAsync`, which also moves files, restarts both watchers and
  reindexes. A policy change must be routed through `MoveAsync` (and through
  `AssistantFolderValidator`), or tools end up sandboxed to the new path while vault, index and watchers
  stay on the old root.
- **`AllowedSyncProviders`** — unique: *nothing* reads the `AppSettings` property. Enforcement runs
  entirely through `PolicyService.IsLoginProviderAllowed`, consumed by the 4+4 login-visibility getters on
  `AccountSettingsViewModel` and `FirstRunWizardViewModel`. Those are plain computed properties that
  nothing re-raises, and they are **not** reachable via `PolicyLock` — so this is the one server-reachable
  live-lock surface that the `PolicyLock` mechanism (§6.2) does not cover.

---

## 6. Design

Four separable pieces. Order matters: 6.0 must precede everything.

### 6.0 Pre-existing bug that must be fixed first

**`ApplyPolicy` aliases reference-typed enforce values instead of copying them.**
`PolicyService.cs:150` is `prop.SetValue(userSettings, prop.GetValue(enforce))`, so after an enforce pass
`settings.Privacy` **is** `_cached.Enforce.Privacy` — the same object, for the rest of the process. Then
`PrivacySettingsViewModel.SavePrivacySettingsAsync` mutates it in place:
`settings.Privacy.TokenizationEnabled = TokenizationEnabled`. The tokenization checkbox is policy-locked
at `GeneralView.xaml:487`, but the *Add keyword* button next to it has no `IsEnabled` lock and calls the
same save unconditionally.

Failure: policy enforces `privacy.tokenizationEnabled = true`; a Settings page opened before it landed
still shows the stale `false`; the user adds one PII keyword; the write lands **inside the admin's enforce
object**; every later `ApplyPolicy` re-asserts the user's `false`. Enforcement is silently inverted
process-wide with the document still cached and reported as applied.

This is live **today** on the restart path — it does not need live apply to fire. It also generalises to
every reference-typed enforce value (`Privacy`, `ModeProviderDefaults`, `ModePersonaDefaults`,
`AgentPersonaRoster`, `AllowedSyncProviders`, `AlwaysAllowedTools`, `BlockedBuiltInPersonas`,
`TodoColumnWidths`). Fix: deep-copy on apply (the existing `SerializeValue` JSON helper is already the
repo's answer to "compare a collection-typed setting", so a serialise/deserialise clone is in keeping).
No test covers it — `PolicyServiceTests` omits `privacy` from its enforce block.

Cloning per apply means `settings.Privacy` is a **new object after every `GetSettingsAsync`**, so anything
holding a cached sub-object reference across a `Get` would silently go stale. Checked: nothing does —
every reader takes a scalar off the sub-object (`settings.Privacy.TokenizationEnabled`) or enumerates it
inline. The clone is safe as scoped.

### 6.1 Detection and re-merge (`PolicyService`, `IPolicyService`)

The re-merge already exists: `LoadPolicyAsync` re-reads both layers and rebuilds both key sets. What is
missing is a change test, an atomic publish, and an event.

- **Change test:** compare `ClientPolicyContract.Normalize(document)` against the last **successfully
  published** document, ordinally. Not against the cache record — if anything between the cache write and
  the publish throws (a throwing `PolicyChanged` subscriber will), the baseline advances while the policy
  does not, and the change is stranded *permanently* because the document never changes again. Hold the
  baseline in its own field, assigned only on the path that publishes.
- **Do not canonicalise via a JSON round-trip.** The server stores and serves the normalised string
  verbatim, so consecutive pulls are byte-identical and ordinal comparison does not thrash. The failure
  modes are asymmetric: a false "changed" costs one idempotent re-merge; a false "unchanged" is the bug
  this work exists to remove.
- **Publish one immutable snapshot behind one field.** Today `_cached`, `_enforcedProperties` and
  `_defaultedProperties` are three fields assigned at three moments, and `ApplyPolicy` reads `_cached`
  then iterates `_enforcedProperties`. Under a background re-merge that is **silent data loss**: for a key
  present only in the new set, the old merged `Enforce` object never had it set, so
  `prop.GetValue(enforce)` returns a **built-in `AppSettings` default** and writes it over the user's
  value — which `SaveSettingsAsync` then persists. Publish `(Merged, Enforced, Defaulted, Record,
  ServerDocument)` as one record swapped with `Volatile.Write`, read once per public call with
  `Volatile.Read`. `_cacheRecord` must be in the snapshot too, and `PersistCacheRecord` must take the
  record it mutated as a parameter (after a `Delete()`, the field and the mutated dictionary diverge and
  an applied-default record is silently lost).
- **Writer gate:** a `SemaphoreSlim(1,1)` held across load+publish in `GetPasswordless…`/`GetPolicyAsync`,
  `ReplaceServerPolicyAsync` and `ClearServerPolicyAsync` (the body awaits file I/O, so not `lock`).
  Reader side takes no lock. **Release the gate before raising** — a subscriber's `GetSettingsAsync`
  reaches `GetPolicyAsync`, and `SemaphoreSlim` is not reentrant.
- **Degenerate case that must not count as a change:** if the snapshot is null (`GetPolicyAsync` has never
  run this process), store the document and return without publishing or raising. This is load-bearing:
  it is what keeps the ~20 existing tests that seed through a throwaway service green, and in production
  it never fires because `Bootstrapper.cs:84` awaits `GetPolicyAsync` 10 s before the first pull.
- **The event:**
  ```csharp
  event EventHandler<PolicyChangedEventArgs>? PolicyChanged;   // ValuesChanged, EnforcementChanged
  ```
  Both sets are needed. `EnforcementChanged` (symmetric difference of the `Enforced` sets) is exactly the
  keys whose locks must re-raise. `ValuesChanged` is what the restart decision keys off — without it the
  overlay would fire on every policy edit, including a pure unpin, which §4.6 proves is never
  restart-worthy.
- **One subscriber, not many — the ordering must be structural.** `PolicyChanged` has exactly one
  subscriber: the coordinator (§6.3). `PolicyLock` and the overlay must **not** subscribe to it directly.
  Multicast invocation order is subscription order, i.e. construction order, so "coordinator first" would
  hold only by accident — the coordinator is built at bootstrap and the `PolicyLock`s when a Settings
  window's VM graph is built. Open a second Settings window, reorder a DI registration, or add a
  subscriber, and the user watches their stale value grey out. The coordinator owns the sequence and fires
  a second, distinct notification (`LocksChanged`) after the value move; `PolicyLock` subscribes to that
  one. Every test would pass under the accidental ordering, which is exactly why it has to be structural.
- **Raise on the pull thread, outside the gate, wrapped in try/catch + `LogWarning`.** The repo's
  convention for this exact shape is raise-off-thread / marshal-in-subscriber (`PersonaService` →
  `AssistantViewModel` via `IUiDispatcher`). Do **not** inject `IUiDispatcher` into `PolicyService` (ctor
  ripple into tests) and do not give it an `ISettingsService` (DI cycle). The try/catch is not optional:
  an unwrapped raise propagates out of the pull and aborts the catalog-version/ETag persist that the
  "keep the old ETag on a throw" test exists to protect.
- `SyncClientService` needs **no structural change** — `ReplaceServerPolicyAsync` is already called at the
  right point, after every other apply and before the token persist, so "a throw leaves both conditional
  tokens unchanged and the next pull refetches" holds for the re-merge for free.

### 6.2 The lock half

Two routes were measured empirically (realized markup, both binding forms, with a no-raise negative
control) rather than reasoned about. **Both work.** `OnPropertyChanged(nameof(Policy))` does invalidate
level 0 of the `Policy[X]` path and WPF re-evaluates the indexer, despite `Policy` being a get-only
auto-property returning the same reference; and `PolicyLock : INotifyPropertyChanged` raising
`Binding.IndexerName` works too. The `DataContext.Policy[X]` + `RelativeSource` form behaves identically
under both — that candidate disqualifier is dead.

**Take route B: `PolicyLock` implements `INotifyPropertyChanged`.** One file covers all 27 indexer
bindings, it is agnostic to both markup forms, a future 9th settings VM inherits the behaviour instead of
silently not getting it, and the subscribed delegate targets a 16-byte object with no back-reference to
the ViewModel (route A roots the whole VM graph off the singleton).

Route B is **not** the whole lock surface, and the "1 file vs 8" framing oversells it: the 11
`Is…Enforced` getters, `IsServerUrlEditable`, and the 8 login-visibility getters are separate binding
targets that need their own per-VM raises either way. So the real work is *route B plus 4 VM handlers*.
Notes:

- `IsThemeEnforced` is dead — delete it, do not plumb it.
- `IsServerUrlEnforced` is unbound; the bound property is the derived `IsServerUrlEditable`. Raising the
  wrong one changes nothing on screen. This is the one getter not reducible to the indexer form.
- 9 of the 11 getters *are* reducible to `Policy[X]`. Collapsing them is worth doing but **as its own
  commit** — it touches 4 view files and would swamp the mechanism diff.
- Both stale doc comments must be rewritten in the same commit as the mechanism, because each states the
  reversed decision as fact: `PolicyLock.cs:8` ("Policy is loaded once per process, so this deliberately
  raises no change notification") and `IPolicyService.cs:33-34` ("A live re-apply would move values while
  every enforcement getter still reported the old lock state, so a changed pin waits for restart").
- **Order: rebuild the sets → move the values → raise the locks.** Raise first and the user watches their
  old value get greyed out.
- No settings sub-VM subscribes to `SettingsChanged` today, so with the Settings page open the *displayed*
  values do not change while the controls grey out. Worse, the next unrelated toggle writes the whole
  stale mirror back; an enforced key survives (re-applied on save) but a **`defaults` key is clobbered
  permanently** — the re-merge already recorded the new value in `AppliedDefaults`, so the stale mirror
  matches neither the built-in nor the applied default and the mechanism concludes "the user changed it"
  forever. **Owner decision 2026-08-20: fix this in Phase 1.** A `SettingsChanged` reload in each of the 8
  settings sub-VMs under the existing `_isLoading` guard, with per-window unsubscription — note none of
  them implements `IDisposable` today and 7 already hold never-removed handlers on singleton services, so
  the unsubscribe story has to be built rather than followed. Doing this in Phase 1 rather than later is
  the right call precisely because the damage it prevents is silent and per-device irreversible.

### 6.3 The coordinator

`PolicyService` cannot move values itself. A small coordinator is the sole `PolicyChanged` subscriber and
does:

```csharp
var settings = await _settingsService.GetSettingsAsync();   // applies the new policy to the shared instance
await _settingsService.SaveSettingsAsync(settings);         // applies again, persists, and raises SettingsChanged
```

The `Get` alone already mutates the one shared `AppSettings` every component holds (`LoadAsync` returns
`_cached` by reference). The **`Save` is what fans out to the 8 known `SettingsChanged` subscribers**, and
without it group (A) of §5.2 silently does nothing. `ApplyDefaults` is idempotent across the double apply
(the second pass suppresses the record write when the serialised value is unchanged), which is already
pinned by an existing test.

**Do the value move on the pull thread — marshal only the lock notification and the overlay flag.**
`GetSettingsAsync`/`SaveSettingsAsync` are file I/O, not UI-thread-bound. Keeping the move on the pull
thread keeps it ordered *inside* the pull, and marshalling it is what would create the window where a
dispatcher-queued lock raise beats a dispatcher-queued value move.

One re-entrancy to expect, verified and benign: `OptimizeViewModel.OnSettingsChanged` moves
`SelectedTemplateId`, whose `PropertyChanged` handler schedules a 500 ms debounced draft save — so the
coordinator's one save produces a **second** `SettingsChanged` fan-out half a second later. It does not
loop (`_lastKnownDefaultTemplateId` is updated before the move, so the second pass short-circuits) and
`ApplyPolicy` is idempotent, but an implementer should not be surprised by the double fan-out.
`SelectedLanguage` has the same shape, which matters for the `TargetLanguage` wiring in §5.2 group (B).

Relying on the pull's own conditional save instead would be the single most likely way this ships a bug:
that save is gated on `newCatalogVersion.HasValue || latch || storePullETag`, and it also runs ~550 lines
*earlier* in the page than the policy arrival, re-applying the **old** policy.

### 6.4 The restart surface

**Owner decision (2026-08-20): a blocking overlay with no dismiss and no "Not now."** The only action is
Restart.

**Reuse the existing overlay mechanism — `DialogOverlayHost` — not the setup overlay.** There are two
overlay surfaces in `MainWindow` and they cover different things:

| | `ShowSetupOverlay` (`MainWindow.xaml:125-190`) | `DialogOverlayHost` (`MainWindow.xaml:191-192`) |
|---|---|---|
| position | inside `Grid.Column="1"`, `Grid.Row="1"` span 3 | direct child of the **outer** `Grid` — sibling of the column grid |
| covers | content only; sidebar and title bar stay live | **everything**: sidebar, title bar, content |
| `Panel.ZIndex` | 10 | **20** — the topmost layer (snackbar 15, `FlowView` 16, `TitleBar`/`ContentDialogHost` default 0) |
| shape | bespoke markup, state-driven `Visibility` binding | generic host + `OverlayDialogPanel`, imperative `Task<TResult> ShowAsync(...)` |

So the mechanism already in the app goes the **"cover everything"** way, which is the forcing behaviour.
That settles the sidebar question by construction — no new geometry, no `MainWindow.xaml` row
renumbering, and nothing new in a file that has **no parse test**. A policy panel is a subclass of
`OverlayDialogPanel` plus `{loc:Str …}` keys in all three `ViewStrings` resx files (parity is
test-enforced; do not copy the update bar, whose text is hardcoded English).

Four things to get right when reusing it:

1. **Override `OnEscapePressed` to a no-op.** `DialogOverlayHost.OnPreviewKeyDown` routes Escape to
   `panel.OnEscapePressed()`, whose base implementation raises `OverlayDialogResult.Close` — i.e. the
   mechanism ships with a built-in "Not now". It is `virtual` and nothing overrides it today, so the policy
   panel must be the first. Miss this and the forcing overlay is dismissible by one keystroke.
2. **Declare only `PrimaryButtonText`.** The template collapses `PART_SecondaryButton` and
   `PART_CloseButton` when their text is null (`Resources/Styles/OverlayDialog.xaml:49-102`), so "one
   button, no escape" is declarative — no template surgery.
3. **Show it in every window.** `IDialogOverlayService` is `AddScoped` (`Bootstrapper.cs:399`) and each
   `MainWindow` hands its own host to its own scope (`MainWindow.xaml.cs:37`), so there is no
   last-writer-wins bug — but equally, one `ShowAsync` blocks one window. Both windows need the call, and
   a window opened *later* needs it on open (which is what the ctor-seeding below is for).
4. **It is a request/response modal, not a bound `Show…` flag.** `ShowAsync` awaits a result, animates
   out and collapses. That fits here — there is no dismiss, so it is shown once and the awaited result
   means "Restart" — but it does mean the trigger is a *call*, not a `Visibility` binding, so the deferral
   gate below decides *when to call*, not what a converter evaluates.

**Because there is no escape hatch, the safety moves into _when the overlay appears_.** That is not a
softening of the decision — it is the only way to honour it without destroying user work. The overlay
must **defer**, not appear-and-trap, while any of §7.2's work-loss states is active:

```
CanShowPolicyRestartOverlay = RestartRequired   // evaluated to decide WHEN to call ShowAsync
    && !directTranscription.IsActive        // State is Starting/Running/Stopping
    && !meetingAttendee.IsActive            // State is Joining/InLobby/Attending/Stopping
    && !executingRuns.IsAnyExecuting        // one-line add on ExecutingRunStore
    && !anyLiveSession.IsStreaming          // ChatSession.State is Running or WaitingForTool
```

Without that gate the overlay is not merely rude, it is destructive, and `DialogOverlayHost`'s
cover-everything geometry makes it more so than the setup overlay would: the meeting and transcription
overlays are in-window controls hosted inside `AssistantView`, so a ZIndex-20 scrim paints over the live
transcript and the **Stop / Save-to-vault** controls — and a live transcript exists only in memory and
*cannot* be saved while recording (§7.2). With no dismiss and no Escape, a user in that state would have
no way to rescue it. The gate needs a re-evaluation trigger on each of those states changing (all
singletons except the chat sessions, and there is at most one Assistant window, so its own scope is the
complete answer).

**Do not reuse `IsOnFeatureView`.** It is `true` throughout a recording, which is precisely backwards
here. The gate above replaces it.

**State lives on the singleton `IPolicyService`, never on `MainWindowViewModel`.** There can be two
`MainWindow`s, each with its own scoped VM — which is why the existing update bar's dismissal is
per-window today (arguably already a bug), and why a forcing overlay in only one window would be worse.
Copy the `IsE2EEOnboardingRequired` consumption pattern exactly: seed from the property in the ctor (so a
window opened *after* the event still shows it), subscribe to the event, unsubscribe in `Dispose`,
marshal with `Post`. Per §6.1 that is its own event (`RestartRequiredChanged`), set by the coordinator
after the value move — not a second subscriber on `PolicyChanged`.

**`RestartRequired` is in-memory and deliberately not persisted.** The condition is "the applied document
≠ the arrived document", so a restart dissolves it and a second, different change re-arms it for free.
There is no dismissal flag to persist. `CachedClientPolicy.UpdatedAt` exists and is written nowhere;
leave it that way and say why.

**Drive the overlay from a declared list, and pin the list with a test.** This is the durable answer to
the brief's §8 rot problem, and it matters more under a forcing overlay than it would under a banner — a
key misclassified as restart-required now blocks the app:

```csharp
// The keys whose value cannot take effect until the process restarts.
private static readonly IReadOnlySet<string> RestartRequiredKeys = new HashSet<string> { nameof(AppSettings.Privacy) };
```

Trigger: `ValuesChanged` intersects `RestartRequiredKeys`, and for `privacy` the latch condition below
also holds. Note `EnforcementChanged` is deliberately **not** part of the condition — §4.6 proves an unpin
moves no value, so a lock-only change must never raise the overlay. Then add a test asserting that every
non-denied settable `AppSettings` property is either in a declared live set or in `RestartRequiredKeys`,
so adding a 101st property forces an explicit classification instead of silently landing in the blocking
bucket. `PolicyBindingNameTests` is the precedent for reading the surface out of the source and pinning it.

**The tray-resident user is out of scope, deliberately.** Windows are hidden rather than closed, so with
no window shown there is nothing to paint and nothing is forced. **Owner decision: not a problem** — the
point of the overlay is to prevent *unwanted usage*, and from the tray the app is not being used. The
ctor-seeding above makes the overlay appear the moment a window is shown, which is exactly when usage
starts. Do not add a tray bridge or force a window open; record this so nobody later "fixes" it.

**The first-run wizard: let it finish, then let the main window carry the overlay.** Owner decision. Do
not try to live-refresh the wizard. The reason it cannot be fixed in place is structural, not effort:
`App.xaml.cs:126-130` awaits the modal wizard *before* any `MainWindow` exists, and — more fundamentally —
**a group policy only exists after sign-in**, so at the moment the wizard renders its login buttons there
is no server policy to filter them by. Only the device `policy.json` layer can gate that step, and it
already does. So:

- The wizard's login step is inherently pre-policy for the server layer. Its
  `IsLocalLoginVisible` / `IsGoogleLoginVisible` / `IsMicrosoftLoginVisible` / `IsEntraIdLoginVisible`
  getters reflect the device layer only, which is correct and needs no change.
- The wizard should be **aware that the option set can change once the cloud connection is made**, and its
  completion path opens the main UI as it does today. Any restart-required residue then surfaces there,
  through the normal `RestartRequiredChanged` → `ShowAsync` path. `FirstRunWizardViewModel` is
  `AddTransient`, so a wizard opened again later reads the policy fresh anyway.

**`privacy`'s restart requirement is CONDITIONAL — gate it on the latch (owner decision 2026-08-20).**
`privacy` is on the restart list *only because* `TokenizingAiClientService` latches
`_enabled`/`_initialized`, and that latch is taken on the first AI request through the decorator. On a
fresh enterprise install the policy arrives ~10 s after login, typically before any such request, so the
value applies live and a restart would be blocking for nothing.

So `RestartRequiredKeys` is not a flat set — `privacy` contributes only when the tokenization decision has
already been latched this process:

```csharp
// Only reachable while something has already latched the tokenization decision; before that the
// value still applies live.
bool RequiresRestart(string key) => key == nameof(AppSettings.Privacy) && TokenizationLatch.IsLatched;
```

One process-wide flag, set where `_enabled` is first assigned. The fresh-install path stays overlay-free;
a genuine mid-session change after the user has run a turn still forces. Without this gate the very first
thing a new enterprise user sees after finishing the wizard is a blocking restart overlay — re-creating,
for one key, the two-launch experience §1 exists to remove.

The flag must be **process-wide, not per-instance**: the decorator is registered transient and held as a
`readonly` field by four long-lived owners (`AssistantViewModel`, `ChatSessionManager`, `ProviderService`,
`SuggestionService`), so an instance-level flag would answer for one of them at random. This is also the
groundwork for eventually emptying the restart list altogether (§8.6): a latch you can *observe* is one
step from a latch you can *reset*.

### 6.5 The live-change notice

**Owner decision: a dismissable Flow message** — *"Your organisation updated Pia's settings."* Not a
snackbar. The reason it is the better fit is structural: `IFlowService` is documented as *"a singleton
shared across all windows"*, whereas the snackbar presenter is per-window markup
(`MainWindow.xaml:193-198`), so a snackbar would show in one window and not the other — the same
divergence that makes the update bar's dismissal per-window today.

Shape it as a `…NotificationSurface`, which is the established convention for a Flow producer
(`AgentRunNotificationSurface`, `AssignmentNotificationSurface`, `ScheduledJobNotificationSurface`,
`BackgroundChatNotificationSurface`). Concretely:

```csharp
_flowService.Publish(new FlowItemDraft
{
    Severity = FlowSeverity.Info,          // ActionRequired is the overlay's register, not this one
    Source   = FlowSource.Policy,          // append-only enum — add the member at the END
    Title    = _localization["Flow_PolicyUpdated_Title"],
    Body     = _localization["Flow_PolicyUpdated_Body"],
    Lifetime = FlowLifetime.Persistent,    // stays until the user dismisses it
    // RequestDurable stays false: not entity-backed, so the durability invariant would force it false
    // anyway — and correctly, since after a restart the change is applied and the notice is moot.
});
```

Points that matter:

- **`FlowSource` is persisted as an int and is append-only** — the existing members carry a comment
  saying so. Add `Policy` at the end; never reorder.
- **`Dismiss(Guid)` already exists** on `IFlowService`, so "dismissable" needs no new mechanism.
- **`FlowLifetime.Persistent`, not `Transient`** — a policy change the user has not seen should wait for
  them, not expire after a few seconds.
- **No `DedupKey`.** The key is documented as an entity id and null is explicitly allowed for
  non-entity items; a policy notice has no entity. Consequence to accept: two changes in one session
  produce two items. If that churns, the alternative is a fixed synthetic key so the second change
  updates the first item in place — but then a dismissed notice is silently replaced rather than
  re-announced.
- **Publish it from the coordinator, after the value move**, on the same ordering rule as everything else
  in §6.1: values first, then locks, then the notice. Publishing before the move announces a change the
  app has not made yet.
- **Do not publish on a withdrawal-only or unpin-only change** unless the copy is reworded — §4.6 shows
  an unpin moves no value, so "we updated your settings" would be a lie. Key the notice on
  `ValuesChanged` being non-empty, the same input the overlay uses.
- New keys go in all three `ViewStrings` resx files (parity is test-enforced). `Settings_ManagedByOrganization`
  already exists in en/de/fr as the vocabulary to match.

---

## 7. The restart command

### 7.1 Mechanism

`IUpdateService.ApplyUpdateAndRestart` is **not** usable — it throws without a staged Velopack asset, and
Velopack's own doc says it exits immediately without giving you a chance to save state. Do not extend
`IUpdateService`; a policy restart is not an update. `ResetAppDataAsync`'s
`Process.Start` + `Environment.Exit(0)` is the reusable *shape* but skips `App.OnExit` entirely.

Recommended: a new `IAppRestartService` singleton that does its **own awaited** pre-exit sequence, then
`Application.Current.Shutdown()`, with the relaunch spawned from `App.Main` after `app.Run()` returns:

```csharp
public async Task RestartAsync()
{
    if (Interlocked.Exchange(ref _latched, 1) != 0) return;   // a double-click must not spawn twice
    await _sync.StopBackgroundSyncAndWaitAsync();
    _trayIcon.PrepareForExit();                               // CloseAndDisposeAll + Unregister
    App.RequestRestart();
    Application.Current.Shutdown();                           // ShutdownMode is OnExplicitShutdown
}
```

Why its own sequence rather than trusting `App.OnExit`: `OnExit` is `async void` with seven awaits and
`CloseAndDisposeAll` dead last, so everything after the first real await races process death. The repo
already declines to trust it — `TrayIconService.ExitApplication` front-loads exactly that tail work
before calling `Shutdown()`.

Points that belong in the implementation:

- `Environment.ProcessPath` is the established relaunch path here (`AutostartService` registers it);
  guard for null and surface "please restart Pia" rather than pretending. The child inherits the
  environment, so `PIA_DATA_DIR` survives and the UI-test harness keeps its throwaway profile. Forward no
  args (Pia reads none; only Velopack consumes `Main`'s args).
- `Environment.Exit(0)` right after the spawn keeps two-instances-on-one-profile down to milliseconds.
  That matters: `JsonPersistenceService.SaveAsync` is an unguarded write over an in-memory cache, so an
  overlap means last-writer-wins on `settings.json`.
- There is **no single-instance mutex in `src/Pia.Wpf` today**, but `feature/right_click` adds
  `App.xaml.cs` single-instance wiring. If that lands without honouring a `--wait-pid` handshake *before*
  acquiring the mutex, this restart silently degrades into a quit. Because the spawn lives in exactly one
  place, that is a one-line fix when the branch arrives — write the constraint down for that work.
- `ITrayIconService` declares only three members and the concrete type is not DI-resolvable, so
  `PrepareForExit()` has to be added to the interface. No test fakes it, so the ripple stays in `src/`.

### 7.2 What a restart costs, and what needs no flush

**Needs nothing** (already durable): the policy document itself (`ReplaceServerPolicyAsync` awaits the
file write before returning), the `AppliedDefaults` record (written synchronously), every JSON store, and
SQLite (WAL, committed transactions survive a kill — do **not** add a checkpoint or dispose the
`ServiceProvider`).

**Already lost on every exit path today** — pre-existing, do not invent new shutdown work: the log tail
(NReco drains only on provider dispose and nobody disposes the factory), and window geometry / last
active view (`SaveWindowStateAsync` is a fire-and-forget `Task.Run`). Calling `CloseAndDisposeAll` early
narrows the geometry window; nothing can await it.

**Genuine losses the user must be able to avoid:**

| state | query | cost of a restart |
|---|---|---|
| live meeting / direct transcription | `IDirectTranscriptionService.State`, `IMeetingAttendeeService.State` (both singletons) | **Transcript destroyed.** It lives only in `Bubbles` and cannot be saved while running |
| agent run in flight | none exists; `!_chatByRun.IsEmpty` on `ExecutingRunStore` is a one-line add, but it is documented as biased toward missing | Run settled to `Cancelled` at next start. `WaitingForInput`/`Paused` survive resumable |
| streaming turn / tool call awaiting approval | `ChatSession.IsStreaming` — scoped, but there is at most one Assistant window, so its own scope is the complete answer | Turn lost |
| unsaved composer text | none | Optimize draft **survives** (debounced to `settings.DraftText`); the Assistant composer does **not** |
| sync push in flight | none public. `IsSyncActive` means "background sync is enabled", not "a cycle is running" | **Nothing.** Pushed data is already local and cursors persist only after a successful apply — worst case is a re-push. Do not build a push flush |

**This table is the overlay's deferral gate, not just documentation of the command's cost.** Under a
dismissible banner the safety could have lived in the button (confirm-if-recording); with no dismiss and
no "Not now" it has to live in the visibility condition, or the overlay covers the very controls that
would rescue the work. The first three rows are the gate (§6.4); the last two are informational — the
Assistant composer's unsent text is lost and nothing can be done about that from here, and a sync push in
flight costs nothing.

Note the two asymmetries an implementer will be tempted to smooth over: `ExecutingRunStore` is documented
as biased toward *missing*, so a gate built on it can let the overlay through while a run is genuinely
alive — acceptable for a UI hint, and the run is `Cancelled` rather than corrupted; and the sync-push row
must not grow a flush, because there is nothing to flush.

---

## 8. Consequences and asymmetries

1. **`ClearServerPolicyAsync` must go live too.** It deletes the cache file but leaves the merged policy
   and both key sets populated, so today a logged-out user keeps the previous group's enforcement until
   restart. Once arrival is live, that asymmetry is indefensible. Route it through the same
   rebuild-and-publish path. It is provably **overlay-free**: a clear only removes keys from `enforce`, and
   §4.6 shows an unpin moves no value.
2. **A key removed from `enforce` does not restore the user's prior value.** Confirmed: nothing records
   the displaced value. The enforced value simply becomes the user's. Identical on the restart path — live
   apply only makes it visible sooner. State it as intended behaviour; do not fix it here.
3. **Withdrawal (`{}`) unlocks but does not revert.** Same root cause. So a user told to restart for a
   withdrawal would see nothing change — another reason the overlay must key on `ValuesChanged`.
4. **Whole-object replacement for the composite keys.** `enforce.privacy`,
   `enforce.modeProviderDefaults`, `enforce.modePersonaDefaults`, `enforce.agentPersonaRoster` and
   `enforce.todoColumnWidths` replace the entire object; there is no per-entry merge. An admin who pins
   one mode's provider silently wipes the user's mapping for the other. Pre-existing; worth a doc note.
5. **`enforce.defaultProviderId` is a silent no-op.** `MigrateFromLegacyDefault` runs one line before
   `ApplyPolicy` on every load and folds the value into `ModeProviderDefaults`, nulling the field. The key
   an admin must actually use is `modeProviderDefaults` (not denied). This belongs in the
   `enterprise-policy` reference docs.
6. **The `Privacy` latch is the only thing standing between this design and a zero-key restart list.**
   Resetting `TokenizingAiClientService._enabled`/`_initialized` and re-running
   `TokenMapService.InitializeAsync` on `SettingsChanged` would make it live — but the decorator is
   transient with no registry, so there is nothing to reset from a handler. That is the design problem to
   solve if the owner wants the overlay gone entirely.
7. **Cross-device stranding — pre-existing, narrow, and ACCEPTED (owner decision 2026-08-20).** A policy
   value written into `AppSettings` changes `SyncMapper.ComputeSettingsHash`, which flips the push gate, so
   it is pushed as the **user's own settings row** and pulled onto other devices as a user value. On a
   device that receives the row before the policy, that value matches neither the built-in default nor any
   `AppliedDefaults` entry, so a *later* admin change to the same `defaults` key is refused forever
   ("the user changed it").
   Two facts that bound it, both verified: the synced projection is only **13 of the 100 keys**
   (`BuildSettingsPlainPayload`, `SyncMapper.cs:827-844`) — `DefaultOutputAction`, `DefaultTemplateId`,
   `WhisperModel`, `AutoTypeDelayMs`, `Theme`, `StartMinimized`, `TargetLanguage`, `TargetSpeechLanguage`,
   `DefaultWindowMode`, `ModeProviderDefaults`, `ModePersonaDefaults`, `UseSameProviderForAllModes`,
   `AssistantDefaultWorkingDirectory`; and it is **not created by this design** — policy applies at startup
   today, the values land in `AppSettings`, and the next push sends them. Live apply tightens the window,
   it does not open it.
   Rejected alternatives, recorded so they are not re-proposed as fixes: excluding policy-derived keys from
   the push would need the hash to know which keys policy set and would risk suppressing a legitimate user
   change to the same key; hardening the receive side (recording policy-set values into `AppliedDefaults`
   when a synced row arrives) fixes the real stranding but touches `SyncMapper`'s apply path for a
   pre-existing edge case. Neither is worth it now — document and move on.
8. **Docs to amend, not silently diverge from:** `docs/2026-08-20-group-client-policy-wpf-handoff.md` §5
   ("Do not add change notification to the Is…Enforced getters to make it live") and §8 (which lists live
   application as out of scope) are both reversed by this design. The log line "effective at the next
   start" and `PolicyService`'s "cached" line also need to distinguish stored-and-applied from
   stored-unchanged.

---

## 9. Optional latency levers (client-only, no server change)

Neither is required; both are cheap and both cost fleet-wide catalog re-pulls (§2).

- **Pull on window activation / app focus.** `SyncNowAsync()` is already public and already called by the
  Account settings "Sync now" button. Makes "the user comes back to Pia" ≈ immediate detection.
- **Cap the idle backoff lower.** The 15 min ceiling is only reached after consecutive idle cycles;
  lowering `MaxSyncInterval` narrows the worst case at the cost of more no-op pulls.

---

## 10. Suggested phasing

| phase | content | outcome |
|---|---|---|
| **0** | Deep-copy reference-typed enforce values (§6.0) | Closes a live enforcement-defeat bug that exists today |
| **1** | Snapshot + change detection + `PolicyChanged` + coordinator + `PolicyLock` INPC + the 4 VM raises + `ClearServerPolicyAsync` symmetry + the declared restart list and its test + the `DialogOverlayHost` panel and its deferral gate + the tokenization latch flag + the Flow notice (§6.5) + the 8 settings sub-VM reload handlers + the `Theme` command lock (§11.1) | **41 keys live, all locks live**, Settings page self-consistent, residue flagged, no rot |
| **2** | The group (B) subscribers, one per service (§5.2) | 14 more keys live |
| **3** | The specials: `AssistantFilesFolder` via `MoveAsync`, `AllowedSyncProviders` login-visibility, and making the tokenization latch *resettable* rather than merely observable | Restart list empties; overlay becomes vestigial-by-design |
| **later** | Collapse the 9 reducible `Is…Enforced` bindings into `Policy[X]` | Cleanup |

Test surface: exactly **one** existing test must be inverted
(`PolicyServiceTests.ReplaceServerPolicyAsync_DoesNotChangeEnforcementInTheSameProcess` — keep its restart
leg as an "and it still resolves the same way after a restart" check). Two more should be strengthened to
assert on the same instance rather than only a restarted one. The ~20 other server-layer tests seed
through a throwaway service whose snapshot is null, so §6.1's degenerate-case guard keeps them green;
every `SyncClientPolicyTests` case mocks `IPolicyService` and asserts on the call, not the effect. State
that reasoning in the commit message — a reviewer will assume otherwise.

A binding test that can actually fail must: realize the real view, locate the element by declared binding
path (not index), drive a real `PolicyService`, `Run` → `Pump()` → `Run`, assert the baseline chain and
`BindingExpression.Status` first, cover **both** markup forms, assert the new *value* as well as the lock,
and include a no-raise negative control. Without the last one, the test can pass off an incidental
re-read.

---

## 11. Decisions made, and what is still open

**Settled 2026-08-20 by the owner:**

- **Restart surface: a blocking overlay, no dismiss, no "Not now."** Recorded in §6.4. My
  recommendation had been a dismissible banner; the owner overrode it, so the safety moved from the
  button into the overlay's deferral gate.
- **Mechanism: reuse `DialogOverlayHost`**, the app's existing overlay surface. It is a child of
  `MainWindow`'s outer grid at `Panel.ZIndex="20"`, so it already covers the sidebar and the title bar —
  the sidebar question is answered by the mechanism rather than by a new geometry decision. Its
  `OnEscapePressed` must be overridden to a no-op, or Escape dismisses the forcing overlay.
- **Restart is user-initiated.** No automatic restart at a safe moment (§4.3).
- **Tray-resident users are not forced, and that is fine** — the goal is to prevent unwanted *usage*, and
  from the tray the app is not in use. Not a gap; do not add a bridge.
- **The first-run wizard is not live-refreshed.** It cannot be: a group policy only exists after sign-in,
  so its login step is inherently device-layer-only. The wizard completes, opens the main UI, and the
  overlay surfaces there.
- **`privacy`'s restart requirement is gated on the tokenization latch** (§6.4), so a fresh install
  applies live and only a genuine mid-session change forces a restart.
- **The live-change notice is a dismissable Flow message**, not a snackbar (§6.5) — `IFlowService` is a
  cross-window singleton, the snackbar presenter is per-window markup.
- **The settings sub-VM value refresh is in Phase 1** (§6.2), because the `defaults` clobber it prevents
  is silent and per-device irreversible.
- **The cross-device stranding is accepted and documented** (§8.7) — pre-existing, confined to the 13
  synced keys, and both candidate fixes cost more than the edge case is worth.
- **Scope: no implementation yet** — review this document first.

- **An enforced `Theme` must lock its control** — the gap closes rather than the dead getter being
  deleted. See below for the corrected mechanism.

**Nothing is open. Awaiting review before implementation.**

### 11.1 Locking an enforced `Theme` — the mechanism, corrected

The obvious reading ("bind `IsThemeEnforced` in `GeneralView.xaml`") is not available: **there is no theme
control in Settings.** `GeneralView.xaml` has nothing theme-related. The toggle is a nav sidebar item
(`NavigationSidebarView.xaml:266-275`) bound to `MainWindowViewModel.ToggleThemeCommand`, while
`IsThemeEnforced` lives on `GeneralSettingsViewModel` — a VM with no reach to it. That mismatch is almost
certainly why the getter was never wired.

Do it on the command instead:

- Give `ToggleThemeCommand` a `canExecute` predicate of `!_policyService.IsEnforced(nameof(AppSettings.Theme))`.
  It is currently `new AsyncRelayCommand(ExecuteToggleThemeAsync)` (`MainWindowViewModel.cs:117`).
- Call `ToggleThemeCommand.NotifyCanExecuteChanged()` from the policy-change handler
  `MainWindowViewModel` already needs for the overlay flag (§6.4), so no new subscription.
- **Delete the orphaned `GeneralSettingsViewModel.cs:72` getter.** With the lock on the command it has no
  purpose, and leaving it is how the next reader concludes the theme lock is handled in Settings.

Why the command and not `IsEnabled`: the command is bound **twice** at that site — on the
`ui:NavigationViewItem` (`:266`) and again on the inner `Button` (`:270`) — so a single `CanExecute`
disables both, whereas an `IsEnabled` binding would have to be duplicated. It also sidesteps setting a
DP on a `NavigationViewItem` container, which inherits into its content.

One consequence to accept: a disabled nav item greys out with no explanation, which is the §8 "enforces
invisibly" gap in miniature. The Flow notice (§6.5) is what covers it — the user is told the organisation
changed settings, and the theme toggle being dead is then legible rather than broken.
