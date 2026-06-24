# Flow — Design Spec

- **Date:** 2026-06-22
- **Status:** Approved (brainstorm) — ready for implementation planning
- **Branch:** `feature/snackbar_rework`
- **Author:** Marco Altmann (with Claude Code)

## 1. Problem & goal

Pia today scatters transient user feedback across several disconnected surfaces: the WPF-UI snackbar (`ISnackbarService` + `SnackbarActionHelper`), a hand-rolled in-app `Border` toast (`INotificationService`), and Windows toasts raised from three notifier classes. Nothing persists, nothing aggregates, and things that flash by while the user is heads-down are simply lost.

**Flow** replaces the snackbar and the hand-rolled in-app toast with a single, persistent, automatically-managed list of "what needs your attention right now," and additionally aggregates Windows toasts, background-chat alerts, and upcoming-deadline todos. It is intended to become a core element of the app that helps the user stay on track in a multitasking environment.

Flow is built in **one pass** covering all four sources.

## 2. Decisions captured during brainstorming

| # | Decision | Choice |
|---|----------|--------|
| Shape | How Flow lives in the window | **Hybrid** — a peeking right-edge handle; click to expand the rail as an overlay; pinnable to a docked column |
| Arrival | New-item announcement | A **subtle peek out of the handle** (small, never covers content or the cursor zone), not a top-left/top-right toast |
| Noticeability | High-severity behavior | **Tiered** — Info/Success whisper-peek then auto-expire; Error & ActionRequired peek assertively and persist in the rail until resolved |
| Visual | Item card direction | **Calm & minimal** (Direction A): thin severity stripe, monochrome glyphs, accent text-action links; reward via motion/craft, not color |
| Lifecycle | Expired transient items | **Vanish from Flow** (no archive); the underlying entity persists in its own home; dismissing from Flow never deletes the entity |
| Persistence | Survive app restart | **Yes** — persist the live *persistent* items to SQLite; reload + re-validate on launch |
| Auto-manage | Repeated/related events | **Smart** — one live item per source entity; newer state wins; auto-retract when the entity resolves |
| Scope | First shippable version | **Everything in one pass** — all four sources + UI + poller |

## 3. Concept

Flow is a persistent, automatically-managed list of attention items on the right edge of every Pia window. Each item carries a **severity**, a relative **timestamp**, an optional typed **deep-link action**, and a **lifetime** (transient = auto-expires; persistent = stays until resolved or discarded).

## 4. Surface & motion

- **Peeking edge handle** — a slim vertical handle on the window's right edge with a count badge. It is a new ZIndex sibling inside the `MainWindow` root `<Grid>` (alongside `RootSnackbarPresenter` / `RootDialogOverlayHost`).
- **Expand on click** — clicking the handle (or the titlebar bell) slides the rail out as an overlay flyout. **📌 Pin** docks it as a permanent column (content reflows narrower). Pinned state is persisted in app settings. A **titlebar bell** mirrors the count as a secondary, discoverable entry point.
- **Arrival peek** — a new item makes a compact card emerge from the handle, then retract; the badge pulses. The peek never covers the composer or the area where the mouse is clicking.
- **Tiered noticeability**:
  - *Info / Success* — whisper-peek (~3–4s), then auto-expire and leave Flow.
  - *Error / ActionRequired* — peek more assertively (larger, accented, lingers until the user glances/acknowledges) and stay parked in the rail until resolved.
- **Item card (calm/minimal — Direction A)** — thin left **severity stripe**, monochrome **source glyph**, **title**, one-line **body**, relative **time**, an accent **text-action link**, and a hover-revealed **✕** (dismiss).
- **Reward via craft** — right-edge slide storyboard adapted from `Resources/Styles/Snackbar.xaml`; dismiss = fade + neighbors collapse up; "Clear all" = quick cascade; badge count tween; handle pulse on arrival. All theme-aware via `DynamicResource`.
- **Empty state** — no badge; the expanded rail shows a quiet "You're all caught up."
- **Badge semantics** — the handle/bell badge counts **live unresolved (persistent) items** (the actionable backlog), not a transient "unread" tally.

## 5. Data model (`Pia.Models`)

- **`FlowItem`** — `Id` (Guid), `CreatedAt` (DateTimeOffset), `Severity` (FlowSeverity), `Source` (FlowSource), `Title` (string), `Body` (string), `DedupKey` (string?), `Lifetime` (FlowLifetime), `IsRead` (bool), `Action` (FlowAction?), `Durable` (bool — whether the item is written to SQLite and reloaded on restart; see §6).
- **`FlowSeverity`** (enum) — `Info · Success · Warning · Error · ActionRequired`. The single target of every source's severity vocabulary.
- **`FlowSource`** (enum) — `Snackbar · InAppToast · BackgroundChat · Reminder · ScheduledJob · TodoDeadline`. Extensible (the user's "and so on").
- **`FlowLifetime`** — `Transient(TimeSpan)` | `Persistent`.
- **`FlowAction`** — a typed deep-link (never a flattened string), each with a `Label`:
  - `OpenChat(Guid chatId)` — via `IWindowManagerService.ShowAssistantChat`
  - `OpenBriefing(Guid entryId)` — via `IWindowManagerService.ShowResearchHistoryWithEntry`
  - `OpenTodo(Guid todoId)` — via navigation to the todo board
  - `ReminderSnooze(Guid reminderId)` / `ReminderDismiss(Guid reminderId)` — via `IReminderService`
  - `Invoke(Action)` — preserves the snackbar `onAction` callback. **Non-serializable** (wraps a live delegate): an item whose `Action` is `Invoke` is always `Durable = false` and never written to disk (§6). The id-carrying variants (`OpenChat`/`OpenBriefing`/`OpenTodo`/`Reminder*`) are re-derivable and survive a reload.
- **`FlowItemDraft`** — the publish-time input the adapters build; `FlowService` stamps `Id`/`CreatedAt` and enforces the `Durable` invariant.

## 6. Lifecycle & auto-management

- **Transient items** expire on a timer and are removed — **not archived**. They are **in-memory only** (never written to disk).
- **Persistent items** stay until the user dismisses them **or** the underlying entity resolves (auto-retract).
- **Two axes — lifetime vs. durability.** *Lifetime* (Transient/Persistent) governs in-session behavior; *durability* (`FlowItem.Durable`) governs surviving a restart. They are independent:
  - **Durable items** (`Durable = true`) are written to SQLite and **reloaded + re-validated** on launch (e.g. is the todo still pending and within the window? is the chat still unread?). `FlowService` enforces the invariant that an item may be `Durable` only if `Lifetime == Persistent` **and** it is **entity-backed** (non-null `DedupKey`) **and** its `Action` is null or an id-carrying re-derivable action (never `Invoke`). In practice this is exactly the entity-backed sources: background-chat, reminder, scheduled-job, todo-deadline.
  - **Session-only items** (`Durable = false`) live only in memory and are gone on restart. This covers **all transient items** *and* the **snackbar / in-app-toast** items — even when their lifetime is Persistent. Rationale: their `Invoke` actions can't be serialized, they have no backing entity to re-validate, and a stale "Web search failed" from a previous session is not actionable. (This reconciles the two brainstorm answers: "the live set survives restart" means the **entity-backed** live set — a todo that entered Flow while the app was closed — not last session's transient confirmations.)
- **Smart dedup** — at most one live item per `DedupKey` (chatId / todoId / reminderId / jobId). Re-publishing with an existing key **updates the existing item in place** (newer state wins) instead of stacking. **Snackbar / in-app items are published with a null `DedupKey` and are exempt** from the one-live-item rule (they have no entity to collapse against); they clear only by user dismissal, transient expiry, or capacity eviction (below).
- **Bounded capacity** — the live store is capped (default **50** items). On overflow, `FlowService` evicts in this order: oldest expired/transient first, then oldest **read** non-`ActionRequired`/`Error` items. `ActionRequired` and `Error` items are never auto-evicted (only dismissed by the user or auto-retracted). This bounds the entity-less persistent backlog the snackbar/in-app sources can accumulate within a session.
- **Auto-retract triggers** (entity-backed items only):
  - `ITodoService.TodoChanged` → todo completed/deleted/due-moved retracts or updates its item.
  - Chat opened / activated / read → retract its item.
  - Reminder dismissed → retract its item.
- **Dismissing from Flow never mutates the source entity** — a todo dismissed from the rail still lives on the board; a chat alert dismissed from Flow does not change the chat.

## 7. Architecture & ingestion

- **Singleton `IFlowService` / `FlowStore`** — the canonical, thread-safe item collection. Owns dedup, auto-retract, expiry timers, and persistence. API:
  - `Publish(FlowItemDraft)`, `MarkRead(Guid)`, `Dismiss(Guid)`, `Retract(string dedupKey)`, `Clear()`, an observable snapshot, and change notifications for presenters.
- **Per-window `FlowPresenter`** (the rail control) registered in `MainWindow.xaml.cs` via `flowService.RegisterPresenter(...)`, mirroring the existing `snackbarService.SetSnackbarPresenter(RootSnackbarPresenter)` pattern. The singleton store routes **arrival peeks to the foreground window** (resolved via `Application.Current.Windows`, exactly as today's toast surfaces resolve a target). Rail content is the shared store, identical across windows.
- **`FlowViewModel`** (scoped per window) and **`FlowItemViewModel`** — observable collection, live count, expand/pin state, dismiss/clear commands; the item VM exposes severity→brush, formatted time, and the `[RelayCommand]` that executes `FlowAction`.
- **No message bus** — Flow defines its own ingestion surface, consistent with the codebase's injected-singleton-"surface"-service convention (there is no `IMessenger`/mediator in use).

### Source adapters

- **Snackbar — capture via 2 chokepoints, not ~87 producer edits**:
  - Reimplement `ISnackbarService` with a Pia class that publishes to `IFlowService` instead of driving the WPF-UI presenter (swap the `Bootstrapper` registration at the scoped `ISnackbarService` line).
  - Rewrite `SnackbarActionHelper.ShowWithAction` to publish an `ActionRequired` FlowItem carrying the `onAction` callback as `FlowAction.Invoke`.
  - This captures all ~87 producer call sites untouched. The WPF-UI slide-in is retired; Flow renders its own transient peek.
- **In-app toast** — re-implement `INotificationService` over `IFlowService` (zero call-site changes); retire the hand-rolled `Border` toast.
- **Windows toasts** — decorate the three notifier singletons (`IBackgroundChatNotifier`, `IScheduledJobNotificationSurface`, `ReminderBackgroundService`) so each `Notify*` call *also* publishes a FlowItem. The Windows toast still fires ("additionally"); structured ids (chatId / entryId+jobId / reminderId) are preserved for the `FlowAction`.
- **Background chat** — decorating `IBackgroundChatNotifier` covers 100% of background alerts (the single chokepoint is `ChatSessionManager.OnSessionStateChanged` → `NotifyStateChange`). For richer payloads (failure reason, tool-success detail), also subscribe `ChatSession.RunFailed` / `ToolSucceeded` in `ChatSessionManager.CreateSession`.
- **Todo deadline** — a new **`TodoDeadlineBackgroundService : BackgroundService`** (`PeriodicTimer`, registered `AddSingleton` next to the existing pollers, started in `App.xaml.cs`). A new `ITodoService.GetDueWithinAsync(window)` returns pending todos whose `DueDate` is within 24h, **filtering in C# after parsing** to avoid the `DueDate` `Kind=Unspecified` / lexicographic-string-comparison bug (DueDate is local-midnight with no offset; reminders write an offset — a naive SQL `<=` would mismatch formats). Publishes a `Warning` "due within 24h" persistent item, deduped per todo; retracts on `TodoChanged`.

### Threading

The singleton store is thread-safe; all presenter/visual updates marshal to the UI thread via `Application.Current.Dispatcher` (as the existing notification surfaces do). Background producers (pollers, notifier singletons) publish from non-UI threads.

## 8. Severity / lifetime per source

| Source | Severity | Lifetime | Durable? | Dedup key | Action |
|---|---|---|---|---|---|
| Snackbar success / copy | Success | Transient ~4s | No | — (null) | Invoke (if any) |
| Snackbar danger / caution | Error / Warning | Persistent | No (session) | — (null) | Invoke |
| Action snackbar (`ShowWithAction`) | ActionRequired | Persistent | No (session) | — (null) | Invoke (`onAction`) |
| In-app `ShowToast` | Info | Transient ~3s | No | — (null) | — |
| In-app `ShowSuccess` | Success | Transient ~3s | No | — (null) | — |
| In-app `ShowError` | Error | Persistent | No (session) | — (null) | — |
| Bg-chat `WaitingForTool` | ActionRequired | Persistent | Yes | chatId | OpenChat |
| Bg-chat `Completed` | Success | Persistent (until read) | Yes | chatId | OpenChat → retract on open |
| Bg-chat `Error` | Error | Persistent | Yes | chatId | OpenChat |
| Reminder fired | ActionRequired | Persistent | Yes | reminderId | Snooze / Dismiss |
| Scheduled job success | Success | Persistent (until read) | Yes | jobId | OpenBriefing |
| Scheduled job failure | Error | Persistent | Yes | jobId | OpenBriefing (falls back to research-history root if no `entryId`) |
| Todo deadline (24h) | Warning | Persistent | Yes | todoId | OpenTodo → retract on complete |

The three brainstorm-listed sources plus the in-app toast collapse onto this single table; entity-backed rows (Durable = Yes, id-carrying actions) survive restart and auto-retract, while snackbar/in-app rows (Durable = No) are session-only with null dedup keys (§6).

## 9. Edge cases (baked in)

- **FirstRunWizardWindow** (a second snackbar host, predating the main shell) gets the **transient-peek fallback only** — no persistent rail/handle. It still needs the snackbar replacement so it doesn't break when the WPF-UI snackbar is retired.
- **Reminder ↔ todo-deadline overlap** — a todo's `LinkedReminderId` means a reminder may already cover it. The todo-deadline item **dedups against `LinkedReminderId`** (suppress if a linked reminder will fire) so the same deadline doesn't surface twice.
- **Privacy logging (CLAUDE.md hard rule)** — Flow is content-heavy (chat titles, todo titles, reminder descriptions — all on the sensitive list). All logging in `FlowService` and the adapters uses `SensitiveDebug`/`SafeUrl`; only ids and enums are logged at default level. Local SQLite storage of item content is acceptable (local-only).
- **Localization** — all Flow-generated strings (headers, empty state, action labels, default titles) use the existing localization resources; item titles/bodies coming from sources are passed through.

## 10. Unit decomposition

- **Models** — `FlowItem`, `FlowSeverity`, `FlowSource`, `FlowLifetime`, `FlowAction`, `FlowItemDraft`.
- **Services** — `IFlowService` / `FlowService` (singleton store), `FlowPersistenceStore` (SQLite read/write + idempotent migration via the `SqliteContext` `PRAGMA`/`ALTER` pattern), severity/action mappers, and the source adapters: the Pia snackbar service, `INotificationService`-over-Flow, the three toast-notifier decorators, and `TodoDeadlineBackgroundService`.
- **ViewModels** — `FlowViewModel`, `FlowItemViewModel`.
- **Views / Controls** — `FlowView` (handle + rail + peek), `FlowItemControl`, and `Flow.xaml` storyboard/style resources (reusing `PiaCardStyle`, severity `*`/`*Soft` tokens).
- **Wiring** — `Bootstrapper` registrations; window host + `RegisterPresenter`; `App.xaml` `DataType→View` `DataTemplate`; poller start in `App.xaml.cs`; `SqliteContext` migration; localization strings; any new `PiaTokens` entries.

## 11. Testing

- Severity-mapping table (each source vocabulary → `FlowSeverity`), including the in-app `ShowToast`/`ShowError`/`ShowSuccess` rows.
- Dedup + auto-retract (re-publish same key updates in place; entity-resolution retracts; null-`DedupKey` snackbar items are exempt and do not collapse).
- Todo due-window query — `DueDate` `Kind` correctness (the load-bearing parsing case).
- Durability invariant — `FlowService` rejects/forces `Durable = false` for non-entity-backed or `Invoke`-action items; only `Durable` items are written to SQLite.
- Persistence round-trip + reload-and-revalidate on startup (durable items reload; session-only items do not).
- Bounded capacity — eviction order honored; `Error`/`ActionRequired` never auto-evicted.
- Lifetime expiry (transient removed on timer; persistent not).
- Snackbar-funnel capture (both `ISnackbarService.Show` and `SnackbarActionHelper` paths produce FlowItems).

## 12. Deferred (explicitly out of scope for this pass)

- **Inline tool-confirmation in Flow** for `WaitingForTool` items — v1 deep-links the user to the chat to confirm there; inline confirm is a later enhancement.
- **Cross-device / cloud sync** of Flow — local-only.
- **User-configurable per-source rules** (custom severities/lifetimes) — fixed mapping in v1.

## Appendix — key integration points (from codebase recon)

- Snackbar service registration: `Bootstrapper.cs` (`AddScoped<ISnackbarService, …>`); action path: `Helpers/SnackbarActionHelper.cs`.
- In-app toast: `Services/NotificationService.cs` / `INotificationService` (`Bootstrapper` Singleton).
- Toast notifier singletons: `Services/BackgroundChatNotificationSurface.cs`, `Services/ScheduledJobNotificationSurface.cs`, `Services/ReminderBackgroundService.cs`.
- Background-chat chokepoint: `ViewModels/Models/ChatSessionManager.cs` (`OnSessionStateChanged` → `NotifyStateChange`); rich events in `ChatSessionEvents.cs`.
- Todo: `Models/TodoItem.cs` (`DueDate`, `LinkedReminderId`), `Services/TodoService.cs` (`GetPendingAsync`, ISO-8601 round-trip), `Infrastructure/SqliteContext.cs` (`IX_Todos_DueDate`, migration pattern).
- Shell dock site: `MainWindow.xaml` root `<Grid>` overlay siblings; per-window wiring in `MainWindow.xaml.cs`; second host `Views/FirstRunWizardWindow.xaml(.cs)`.
- Theming: `Resources/Theme/PiaTokens.Light.xaml` / `.Dark.xaml`, `Resources/Theme/PiaStyles.xaml` (`PiaCardStyle`), `Resources/Styles/Snackbar.xaml` (slide storyboard), `Services/ThemeService.cs`.
- DI root: `Bootstrapper.ConfigureServices`; background-service start: `App.xaml.cs`.
