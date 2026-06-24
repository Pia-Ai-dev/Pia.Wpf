# Flow — Implementation Plan

Derived from `2026-06-22-flow-design.md` + full codebase recon (9-agent sweep, ground-truthed).
Branch: `feature/snackbar_rework`.

## Key recon findings that refine the spec

1. **`ISnackbarService` is a Wpf.Ui NuGet interface**, not a Pia interface. Its single content method
   is `void Show(string title, string message, ControlAppearance appearance, IconElement? icon, TimeSpan timeout)`
   plus `SetSnackbarPresenter`/`GetSnackbarPresenter`/`DefaultTimeOut`. Shorter `Show` overloads are
   extension methods that delegate to the 5-arg interface member. → We **implement** the interface with a
   Pia class `FlowSnackbarService : ISnackbarService` and swap the scoped registration. Capturing the 5-arg
   `Show` captures all ~85 producer calls (extensions funnel through it). `SetSnackbarPresenter` becomes a
   no-op; `GetSnackbarPresenter` returns null (the action helper is rewritten to not use it).
2. **`SnackbarActionHelper.ShowWithAction`** (static, `Helpers/`) is rewritten to publish an `ActionRequired`
   FlowItem carrying `onAction` as `FlowAction.Invoke`. It no longer touches a presenter.
3. **`INotificationService`** (Pia, singleton) = `ShowToast(msg,dur=3000)`, `ShowError(msg,dur=5000)`,
   `ShowSuccess(msg,dur=3000)`. Re-implemented as `FlowNotificationService` publishing FlowItems.
4. **Notifiers**: `IBackgroundChatNotifier.NotifyStateChange(Guid chatId, string displayTitle, ChatState state)`
   and `IScheduledJobNotificationSurface.NotifySuccess(ScheduledJob, ResearchHistoryEntry)` /
   `NotifyFailure(ScheduledJob, Guid resultEntryId, string reason)` are **decorated** (wrap real impl, publish
   then delegate). `ReminderBackgroundService` has **no interface** (concrete singleton) → inject `IFlowService`
   directly and publish at the fire site.
5. **ChatState** = `Idle, Running, WaitingForTool, Completed, Error`. Only WaitingForTool/Completed/Error notify.
6. **TodoItem.DueDate is `DateTime?`** (not DateTimeOffset), stored `ToString("O")`, read `DateTime.Parse`.
   UI writes local-midnight (Kind=Unspecified). → `GetDueWithinAsync(TimeSpan)` filters **in C#** comparing
   against `DateTime.Now` (local wall-clock) to avoid the Kind/lexicographic mismatch.
7. **SQLite**: one `EnsureSchema()` with `CREATE TABLE IF NOT EXISTS` blocks (lazy, first connection). Adding a
   new `FlowItems` table there is automatically the migration for existing DBs. Guid→TEXT, enum→INTEGER,
   DateTime(Offset)→`ToString("O")`/Parse, bool→INTEGER, nullable→`DBNull.Value`.
8. **Shell**: MainWindow root `<Grid>` has overlay siblings `RootDialogOverlayHost` (Z=20),
   `RootContentDialogPresenter`, `RootSnackbarPresenter` (Z=15), `TitleBar`. Flow rail = new sibling at Z=16.
   Per-window presenters wired in code-behind constructor (e.g. `snackbarService.SetSnackbarPresenter(...)`).
9. **DI**: scoped WPF-UI services (line 197), singletons block (235-323), scoped services (325-333),
   scoped VMs (340-353), transient windows (358-360). `ValidateScopes=true` in DEBUG → a **singleton must not
   inject scoped deps**. `IFlowService` is a singleton injecting only singletons + logger + persistence.
10. **Background services** are `AddSingleton<Concrete>()` + manual `StartAsync`/`StopAsync` in App.xaml.cs
    (NOT `AddHostedService`).
11. **Theming tokens** (DynamicResource): `PiaSuccessBrush`/`SuccessSoftBrush`, `PiaDangerBrush`/`DangerSoftBrush`,
    `WarnBrush`/`WarnSoftBrush`, `PiaAccentBrush`/`PiaAccentSoftBrush`, `SurfaceBrush`/`SurfaceMutedBrush`/
    `SurfaceSunkBrush`, `TextDefaultBrush`/`TextMutedBrush`/`TextSubtleBrush`, `BorderBrush_` (trailing `_`),
    `BorderStrongBrush`. `PiaCardStyle` exists. No Info token → Info maps to `TextSubtleBrush`/`PiaAccentBrush`.
12. **Localization**: services inject `ILocalizationService` (indexer `[key]` + `Format(key,args)`); XAML uses
    `{loc:Str Key}` (LocalizationSource singleton). Base strings in `ViewStrings.resx` / `MessageStrings.resx`.

## Design decisions (deviations from spec, all minor)

- **Peek routing**: no store-level `RegisterPresenter` window routing. Each window's `FlowViewModel` subscribes
  to the singleton store's events; on `ItemArrived` it raises a VM event; the `FlowView` code-behind plays the
  peek storyboard **only if its window `IsActive`** (foreground). Non-foreground windows update silently. This
  keeps the store UI-agnostic and is simpler/correct.
- **Cross-session re-validation** (durability): durable items reload on startup. Re-validation is feasible where
  cheap: TodoDeadline items reconcile on the poller's immediate first tick; Reminder items reconcile against
  `IReminderService.GetActiveAsync()`. Chat/ScheduledJob durable items reload as-is and resolve via in-session
  auto-retract or user action (no cheap "still unread?" signal exists across a restart). **OPEN QUESTION** — see plan tail.
- **Rich ChatSession events** (`RunFailed`/`ToolSucceeded`) are **deferred**: the `IBackgroundChatNotifier`
  decorator already covers 100% of background alerts with a localized title. Richer bodies are a later enhancement.

## File plan

### Models (`src/Pia.Wpf/Models/Flow/`)
- `FlowSeverity.cs` — enum `Info, Success, Warning, Error, ActionRequired`.
- `FlowSource.cs` — enum `Snackbar, InAppToast, BackgroundChat, Reminder, ScheduledJob, TodoDeadline`.
- `FlowLifetime.cs` — readonly struct: `IsPersistent` + `Duration` (TimeSpan?); factories `Persistent`, `Transient(ts)`.
- `FlowAction.cs` — abstract record + subtypes: `OpenChat(Guid)`, `OpenBriefing(Guid)`, `OpenTodo(Guid)`,
  `ReminderSnooze(Guid)`, `ReminderDismiss(Guid)`, `Invoke(Action)`; each has `Label`. `Invoke` is non-serializable.
- `FlowItem.cs` — `Id, CreatedAt(DateTimeOffset), Severity, Source, Title, Body, DedupKey?, Lifetime, IsRead,
  Action?, Durable`.
- `FlowItemDraft.cs` — publish-time input (no Id/CreatedAt; service stamps + enforces Durable invariant).

### Services
- `Services/Flow/FlowSeverityMapper.cs` — `ControlAppearance→FlowSeverity`, `ChatState→FlowSeverity`.
- `Services/Interfaces/IFlowService.cs` — `Publish(draft)→FlowItem`, `MarkRead(id)`, `Dismiss(id)`,
  `Retract(dedupKey)`, `Clear()`, `IReadOnlyList<FlowItem> Snapshot`, `event Changed`, `event ItemArrived`,
  `Task LoadAsync()`.
- `Services/Flow/FlowService.cs` — thread-safe store (lock). Dedup by DedupKey (null = exempt), newer-state-wins
  update-in-place, capacity 50 with eviction order, transient expiry timer, durability invariant enforcement,
  write-through to persistence for durable items. Marshals events; presenters marshal to dispatcher.
- `Services/Flow/IFlowPersistenceStore.cs` + `FlowPersistenceStore.cs` — SQLite read/upsert/delete of durable items.
- `Infrastructure/SqliteContext.cs` — add `FlowItems` table + indexes in `EnsureSchema`.

### Adapters
- `Services/Flow/FlowSnackbarService.cs : ISnackbarService` (replaces scoped registration).
- `Services/Flow/FlowNotificationService.cs : INotificationService` (replaces singleton registration).
- `Services/Flow/FlowBackgroundChatNotifier.cs : IBackgroundChatNotifier` (decorator over `BackgroundChatNotificationSurface`).
- `Services/Flow/FlowScheduledJobNotificationSurface.cs : IScheduledJobNotificationSurface` (decorator).
- `Services/ReminderBackgroundService.cs` — inject `IFlowService`, publish at fire site.
- `Services/Flow/TodoDeadlineBackgroundService.cs : BackgroundService` — PeriodicTimer, GetDueWithinAsync(24h),
  publish Warning per todo (dedup todoId, suppress if LinkedReminderId set), reconcile/retract on TodoChanged + each tick.
- `Services/TodoService.cs` + `ITodoService` — add `GetDueWithinAsync(TimeSpan)`.
- `Helpers/SnackbarActionHelper.cs` — rewrite to publish ActionRequired Invoke FlowItem (needs `IFlowService`).
  Callers pass service; or it resolves from a static. Use static accessor via `Bootstrapper.ServiceProvider`
  (it's static) to avoid editing ~5 call sites. **VERIFY** Bootstrapper.ServiceProvider is accessible.

### ViewModels
- `ViewModels/Flow/FlowItemViewModel.cs` — wraps FlowItem; SeverityBrush, SourceGlyph, RelativeTime,
  `[RelayCommand]` ExecuteAction, `[RelayCommand]` Dismiss.
- `ViewModels/Flow/FlowViewModel.cs` (scoped) — ObservableCollection<FlowItemViewModel>, LiveCount (badge),
  IsExpanded, IsPinned (persisted via ISettingsService), Dismiss/Clear/ToggleExpand/TogglePin commands,
  subscribes to IFlowService, marshals to dispatcher, raises ItemArrived.

### Views / Controls
- `Controls/Flow/FlowView.xaml(.cs)` — handle + count badge + rail (overlay/docked) + arrival peek; foreground-gated peek.
- `Controls/Flow/FlowItemControl.xaml(.cs)` — calm/minimal card (stripe, glyph, title, body, time, action link, ✕).
- `Resources/Styles/Flow.xaml` — storyboards (slide/peek/pulse), reuse PiaCardStyle + severity tokens, theme-aware.

### Wiring
- `Bootstrapper.cs` — swap ISnackbarService → FlowSnackbarService; swap INotificationService → FlowNotificationService;
  wrap IBackgroundChatNotifier + IScheduledJobNotificationSurface with decorators; register IFlowService,
  IFlowPersistenceStore (singletons), TodoDeadlineBackgroundService (singleton), FlowViewModel (scoped).
- `App.xaml` — `<DataTemplate DataType FlowViewModel → FlowView>`; merge Flow.xaml.
- `App.xaml.cs` — `await flowService.LoadAsync()`; start/stop TodoDeadlineBackgroundService.
- `MainWindow.xaml(.cs)` — add `RootFlowView` sibling (Z=16), bell in TitleBar; resolve scoped FlowViewModel, set DataContext.
- `Views/FirstRunWizardWindow` — transient-peek fallback only (snackbar replacement still works; no rail/handle).
- `Models/AppSettings.cs` — add `FlowPinned` bool.
- `Resources/Theme/PiaTokens.{Light,Dark}.xaml` — only if new tokens needed (prefer reuse).
- `Resources/Strings/ViewStrings.resx` (+ MessageStrings) — Flow_* keys (headers, empty state, action labels, default titles).

### Tests (`tests/Pia.Wpf.Tests/`, xunit.v3 + NSubstitute + plain Assert)
- Severity-mapping table (snackbar ControlAppearance, chat ChatState, in-app methods).
- Dedup + auto-retract (re-publish updates in place; retract; null-DedupKey exempt).
- Todo due-window query Kind correctness.
- Durability invariant (force Durable=false for non-entity-backed / Invoke).
- Persistence round-trip + reload.
- Bounded capacity eviction order (Error/ActionRequired never evicted).
- Lifetime expiry.
- Snackbar funnel capture (Show + ShowWithAction both produce FlowItems).

## Severity / lifetime mapping (from §8) — authoritative table
(see design spec §8; ControlAppearance: Success→Success/Transient~4s, Caution→Warning/Persistent,
Danger→Error/Persistent, other→Info/Transient~4s. ShowWithAction→ActionRequired/Persistent/Invoke.
In-app ShowToast→Info/Transient3s, ShowSuccess→Success/Transient3s, ShowError→Error/Persistent.)

## Open questions (to surface on completion)
1. Cross-session re-validation for chat/scheduled-job durable items (no cheap "still unread" signal).
2. Should the rich ChatSession `RunFailed`/`ToolSucceeded` payloads be wired in v1 (touches ChatSessionManager)?
3. Bell placement in the constrained WPF-UI TitleBar (DPI/caption-button collision).
4. Pin scope: global setting vs per-window.

## Post-implementation status (2026-06-23)

**Done & verified.** Full solution builds (0 errors). 592 tests pass — incl. 39 Flow, 25 architecture,
70 ViewModel. The 18 failing tests are pre-existing `Integration.Providers.*` (OpenRouter/VLlm) live-endpoint
tests that fail only for lack of network/credentials; unrelated to Flow (confirmed: zero non-Provider failures).

**Adversarial code review (19 agents, 12 verified findings) — all resolved:**
- HIGH — missing `CollapseCommand` (rail could never close): added `Collapse` `[RelayCommand]`.
- MED — persistence read a mutable shared item outside the lock (torn row): now clones a snapshot under the lock.
- MED — a fresh item could evict itself + phantom peek: `EvictIfNeeded` excludes the just-published item.
- MED — pinned rail didn't reflow content (§4): added `BoolToReflowMarginConverter`; content column reserves the rail width when pinned.
- MED — chat alert not auto-retracted on open via toast/direct-nav: added a retract in `ChatSessionManager.SetActive` (the single chokepoint for all open paths) — covers cross-session too.
- LOW — eviction by list order not `CreatedAt` after a dedup bump: now orders by `CreatedAt` per tier.
- LOW ×2 — dedup durability downgrade orphaned the row: delete-through on true→false.
- LOW — sweep timer dispose race: `_disposed` guard + `Timer.Dispose(WaitHandle)` join.
- LOW — `FlowView` didn't re-attach `ItemArrived` after Unloaded/Loaded: symmetric Attach/Detach + `Loaded` handler.
- LOW — scheduled-job failure briefing lacked the empty-`entryId` root fallback in the Flow path: added the `Guid.Empty` guard.

**Consciously deferred (documented limitations, not defects):**
- **Cross-session re-validation** for *reminder* and *scheduled-job* durable items (finding #6). A fired reminder is
  auto-dismissed at fire time, so its reloaded Flow item legitimately means "fired but not acknowledged in the rail"
  and persists until rail-dismissed — only stale if the user acted via the *Windows toast* last session. A job-success
  item opened via the Flow link retracts; opened elsewhere it lingers until read. The *chat* case is fully handled by
  the `SetActive` retract. A full per-entity startup reconciler is the natural follow-up.
- **Titlebar bell** (secondary entry point, §4) — deferred to avoid fragile WPF-UI `TitleBar` surgery; the right-edge
  handle is the primary surface.
- **Rich ChatSession `RunFailed`/`ToolSucceeded` payloads** (§7 "also subscribe") — the `IBackgroundChatNotifier`
  decorator already covers 100% of background alerts; richer bodies are a later enhancement.
- **FirstRunWizard peek** (§9) — the wizard's snackbars publish to the Flow store safely (no crash) but show no
  in-wizard peek (it has no `FlowView`); persistent items appear once the main shell opens.

**Notable deviations from the spec mechanics (same outcomes):**
- The three Windows-toast notifiers are *modified in place* to publish the rich Flow item (not wrapped as DI
  decorators) — pure decoration would have double-published, since each internally calls `INotificationService`,
  which now routes to Flow. Windows toasts still fire additionally.
- `FlowPersistenceStore` uses its *own* dedicated SQLite connection (not the shared `SqliteContext` connection),
  because Flow publishes from background threads; the shared connection has UI-thread affinity.
- Arrival-peek routing is presenter-gated on window foreground (View checks `Window.IsActive`) rather than a
  store-level `RegisterPresenter`, keeping the store UI-agnostic.
- `NamingConventionTests` allowed-suffix list extended with `Store` (for the spec-named `FlowPersistenceStore`).
