# Plan — Background assistant chats + per-chat state

**Goal:** let assistant conversations keep running when the user switches
between chats, surface a per-chat **state** (Idle / Running / WaitingForTool /
Error / Completed), let the user **group history by state** (alongside the
existing date grouping), and **notify** when a background (non-active) chat
changes state, with a clickable link that activates that chat inside the single
assistant window.

**Why:** today the whole run loop lives in `AssistantViewModel`, owns one
shared `_streamingCts`, and switching chats (`ResumeChatAsync`, line ~737, and
`OnNavigatedTo(Guid)`, line ~890) **cancels the in-flight stream and reloads
from SQLite**. That throws away a running turn. A blocked tool-confirmation in a
chat the user navigated away from is silently cancelled. Users want to fire a
long turn, switch to another chat, and come back (or be pinged) when it lands.

**Predecessor:** [`assistant-chat-history.md`](./assistant-chat-history.md) —
the SQLite store, `SyncAssistantChat` DTOs, title chip, quick switcher, and full
history view this plan builds on already shipped.

---

## The four pinned decisions (re-confirmed)

1. **Side-effect split (Decision 1)** — which inline UI effects stay active-VM-only
   vs. route to the background notifier. Resolved in the side-effect table
   (§ side-effect split): snackbar/TTS/followups/title/**InputText-restore** are
   active-VM-only; `Messages.Remove` stays session-local; background state changes
   route to `IBackgroundChatNotifier`; auto-title `SetTitle` goes through
   `SessionTitleChanged` (is-active gated), replacing the old `_currentChatId`
   guard.
2. **Manager lifetime (Decision 2)** — `AddScoped` per assistant window (not
   singleton), because it injects scoped `IActionCardBuilder`/`ITokenMapService`.
   Lifetime table verified against `Bootstrapper.cs` (`ISuggestionService` /
   `IAiClientService` are Transient).
3. **Message-list swap (Decision 3)** — `Messages` becomes a settable
   `[ObservableProperty]` re-pointed to `ActiveSession.Messages`;
   `OnMessagesChanged` swaps the `CollectionChanged` subscription so `HasMessages`
   stays live. Smallest XAML delta.
4. **Activation semantics (Decision 4)** — `ActivateAsync` live-attaches an
   in-flight session **without cancel/reload** (the headline). Gated by token-map
   isolation; the `ChatState` enum is its data model.

## The seam (architecture)

Extract a per-chat **`ChatSession`** that owns one conversation's runtime state
and **relocates the run loop** out of `AssistantViewModel`. A scoped-per-window
**`ChatSessionManager`** holds the live sessions, designates the active one,
runs turns, raises state events, and persists via `IAssistantChatService`.
`AssistantViewModel` becomes a **thin view** onto the active session.

```
ChatSessionManager (scoped per assistant window)
 ├─ Sessions: Dictionary<Guid, ChatSession>   (live, in-flight or recently finished)
 ├─ ActiveSession                              (the one the visible VM mirrors)
 ├─ events: ActiveChanged, SessionStateChanged, SessionTitleChanged
 └─ persists via IAssistantChatService (singleton)

ChatSession (one per live chat)
 ├─ Id, CreatedAt, ProviderId, Title, autoTitleApplied flag
 ├─ Messages : ObservableCollection<AssistantMessage>   (its OWN list)
 ├─ Cts : CancellationTokenSource                        (its OWN, per-chat)
 ├─ State : ChatState                                    (raises StateChanged)
 ├─ IsStreaming
 ├─ events: StateChanged, TitleChanged, TurnCompleted, ToolSucceeded, RunFailed
 └─ RunTurnAsync(userText, atCommands, turnSetup, …)     ← the relocated loop

AssistantViewModel (scoped, cached in NavigationService._viewModelCache)
 ├─ subscribes to the ACTIVE session only
 ├─ Messages / IsStreaming / HasMessages proxy ActiveSession
 └─ performs scoped UI effects (snackbar, TTS, followups, ChatTitleChip)
```

### Why scoped-per-window, not singleton (Decision 2 — derived, not guessed)

Confirmed against `Bootstrapper.cs` (lines 234–340):

| Run-loop collaborator | Lifetime |
|---|---|
| `IActionCardBuilder` | **Scoped** (`@264`) |
| `ITokenMapService` | **Scoped** (`@281`) |
| `IVoiceInputService` | Scoped (`@322`) |
| `INavigationService` | Scoped (`@318`) |
| `ISnackbarService` | Scoped (`@197`) |
| `IAssistantPromptComposer`, `IChatTitleService`, `IOutputService`, `ISuggestionService`, `IAiClientService` | Transient (`@262, @263, @326, @229, @221`) |
| `IProviderService`, `IPersonaService`, `ISettingsService`, `IPluginService`, `ITtsService` | Singleton |
| `IAssistantChatService`, `INotificationService`, `IWindowManagerService` | Singleton |

> Corrected against `Bootstrapper.cs` line-by-line: `ISuggestionService` is
> **Transient** (`@229`), not Singleton, and `IAiClientService` is Transient
> (`@221`, the `TokenizingAiClientService` decorator). Neither changes the
> conclusion — transient/singleton both inject into a scoped manager fine; the
> only illegal direction is scoped-into-singleton.

`ChatSession` must build action cards (`IActionCardBuilder`) and detokenize
(`ITokenMapService`), both **scoped**. A **singleton** manager injecting those
would be a captive-dependency error (`InvalidOperationException: Cannot consume
scoped service … from singleton`). Therefore the manager is **`AddScoped`
(per assistant window)**, and it injects the scoped/transient collaborators
directly.

"Sessions must outlive a VM navigation" is **already satisfied**:
`AssistantViewModel` is cached in `NavigationService._viewModelCache` and the
window's DI scope lives for the window's lifetime (`WindowManagerService`
creates one scope per `WindowMode`, reused on `ShowWindow`). Navigating
Assistant → History → Assistant returns the **same** VM and the **same** scope,
so a scoped manager survives the round-trip. There is exactly one assistant
window, so there is no cross-window reason to go singleton.

**Cross-cutting (singleton) work** that a scoped manager cannot do directly —
firing background notifications — goes through the **singleton**
`INotificationService` and a new **singleton** `IBackgroundChatNotifier`
(modeled on `ScheduledJobNotificationSurface`). Scoped→singleton is legal; the
trap is only the reverse.

---

## Deliverables

1. `ChatState` enum + `ChatStateChangedEventArgs`.
2. `ChatSession` (owns Messages, Cts, State; hosts `RunTurnAsync`).
3. `ChatSessionManager` (scoped) + `IChatSessionManager`.
4. `AssistantViewModel` delegates to the manager/active session; existing
   send / resume / clear / cancel / regenerate / voice behavior preserved.
5. DI registration; **green build + green tests** at this point (mustHave).
6. **Per-row wrapper VM** (`AssistantChatRowViewModel`) + **state indicator**:
   badge on the title chip + state glyph in history rows and quick-switcher rows;
   `ChatState` → glyph/brush/label converters using DynamicResource Pia tokens.
   (The wrapper VM is a prerequisite — there is no row VM today; history rows bind
   the raw `SyncAssistantChat` DTO.)
7. **Group-by-state** toggle in `AssistantHistoryViewModel` (history view only;
   chip flyout deferred), alongside the existing `HistoryDateBucket` grouping.
8. **Background-continue-on-switch**: switching to another live session does
   **not** cancel the others; turns finish in the background.
9. **State-change notifications** for background sessions, with a toast link
   that activates the chat (revealing a pending action card).
10. de/fr resx for every new key.

---

## Data model

### `ChatState` enum (Decision 4)

New file `src/Pia.Wpf/Models/ChatState.cs` (CRLF):

```csharp
namespace Pia.Models;

public enum ChatState
{
    Idle,           // no turn in flight; default for a persisted-but-not-live chat
    Running,        // a turn is streaming / tool-calling
    WaitingForTool, // blocked on an action-card confirmation (no timeout)
    Completed,      // a background turn finished with an unread result
    Error,          // last turn ended in a handled error
}
```

Five states. `Queued` is **deferred** — v1 runs one turn per session at a time;
re-sending while `Running` is already blocked by `CanExecuteSendMessage`
(`!IsStreaming`). `Completed` exists specifically for "finished in the
background, result unread"; the active session that reaches `Idle` in the
foreground never enters `Completed` (the user is looking at it).

**`Completed` → `Idle` clear point:** when a session becomes the active session
(`ChatSessionManager.SetActive`), if its state is `Completed` it transitions to
`Idle` (the result is now "read"). **This is the only auto-clear edge — true
activation only; previewing a finished background chat in the history inspector
(`AssistantHistoryViewModel.LoadSelectedChatDetailAsync`, `:432`, which shows the
full transcript *without* activating the session) does NOT clear `Completed`.**
This is a deliberate decision (preview ≠ acknowledge); it is recorded as an
openQuestion in case product wants inspector-preview to also clear. If we later
decide preview counts, add a second clear edge in `LoadSelectedChatDetailAsync`
when the loaded chat's live session is `Completed`.

### State transition points (exact, in `ChatSession.RunTurnAsync`)

The loop relocated from `ExecuteSendMessage` sets state at these points:

| Transition | Where | Notes |
|---|---|---|
| → `Running` | top of `RunTurnAsync`, right after adding the assistant `AssistantMessage` and setting `IsStreaming = true` (was line ~309) | replaces nothing user-visible |
| → `WaitingForTool` | in `HandleToolCall`, **immediately before** `await card.WaitForUserDecisionAsync()` (was line ~659) | canonical background-waiting case; **no timeout** |
| back → `Running` | right after the `WaitForUserDecisionAsync()` returns (confirmed or declined) and after executing the action | so the next tool/segment shows Running |
| → `Error` | each `catch` block (timeout / truncated / vision / generic) in `RunTurnAsync` (was lines ~410–456) | `OperationCanceledException` → **not** Error; it returns to `Idle` (user cancelled) |
| → `Completed` (background) **or** `Idle` (active) | `finally` of `RunTurnAsync` (was line ~457), via the **synchronous** `manager.FinalizeTurn(session)` block | if this session is **not** the manager's `ActiveSession` and the turn produced content without error → `Completed`; otherwise `Idle`. **The `isActive` read, the terminal `SetState`, and the notifier-routing decision run in one synchronous block on the UI thread with no `await` between them** (see Threading) so they serialize against `SetActive` and cannot misclassify a chat the user just activated |

Every state write goes through a single `ChatSession.SetState(ChatState)` that
no-ops on unchanged value and raises `StateChanged`. Logging at the transition
is **Information** with `{ChatId}`, `{State}`, and a UTC timestamp only — never
the title or content (CLAUDE.md). The manager re-raises as
`SessionStateChanged(chatId, oldState, newState, isActive)`.

`WaitingForTool` is the linchpin: a background chat blocked on a tool card is
the primary thing a notification must point at.

**`IsStreaming` ↔ `ChatState` (regression-critical):** `IsStreaming` is **true
for both `Running` and `WaitingForTool`** — a turn is *in flight* the whole time,
including while blocked on a tool card. This mirrors today exactly:
`ExecuteSendMessage` is one continuous `await`, so during
`WaitForUserDecisionAsync()` the send button stays disabled
(`CanExecuteSendMessage` = `!IsStreaming`), Cancel stays available, and
`CanEnterVoiceMode` (`!IsStreaming`) stays false. If the proxied `IsStreaming`
dropped during `WaitingForTool`, all three would flip mid-turn — a behavior
regression in the "preserve existing behavior" seam. `IsStreaming` is **false**
only in `Idle`, `Completed`, and `Error`. `ChatSession.IsStreaming` is derived
from `State` (true ⇔ `Running` or `WaitingForTool`) and the VM proxies it.

### `AssistantMessage` (additive only — Hard rule c)

**No changes required.** Per-chat state lives on `ChatSession`, not on the
message. (If a future "which message is awaiting confirmation" highlight is
wanted, add a bool to `AssistantMessage` then — out of scope here.)

### DTO / persistence

`SyncAssistantChat` is **not** extended with state — state is runtime-only and
not synced. A persisted-but-not-live chat has **no** live `ChatState`; the
indicator/grouping maps it to **`Idle`** (see Group-by-state). This keeps the
server wire contract untouched and avoids a schema bump.

---

## `ChatSession`

New file `src/Pia.Wpf/ViewModels/ChatSession.cs` (CRLF). Plain class (not an
`ObservableObject` view model — it is a runtime model the VM mirrors), but it
raises events. The supporting types — `ChatTurnRequest` and the event-arg
records (`ChatStateChangedEventArgs`, `SessionStateChangedEventArgs`,
`SessionTitleChangedEventArgs`, `ToolSucceededEventArgs`, `RunFailedEventArgs`)
— live in `src/Pia.Wpf/ViewModels/ChatSessionEvents.cs` (CRLF).

`Id` is `Guid?` and stays null until the first persist (exactly like today's
`_currentChatId`). A session enters the manager's keyed `Sessions` dictionary
when it gets its Id on first persist; a brand-new unsaved chat simply isn't in
the dict yet (nothing navigates to it by id), which is correct.

```csharp
public sealed class ChatSession : IDisposable
{
    public Guid? Id { get; private set; }                // null until first persist (mirrors today's _currentChatId)
    public DateTime CreatedAt { get; private set; }
    public Guid? ProviderId { get; private set; }
    public string? Title { get; private set; }
    public ObservableCollection<AssistantMessage> Messages { get; } = new();
    public ChatState State { get; private set; } = ChatState.Idle;
    public bool IsStreaming { get; private set; }
    internal CancellationTokenSource? Cts { get; private set; }
    internal bool AutoTitleApplied { get; set; }

    public event EventHandler<ChatStateChangedEventArgs>? StateChanged;
    public event EventHandler<string?>? TitleChanged;
    public event EventHandler? TurnCompleted;            // active VM: persist + followups host
    public event EventHandler<ToolSucceededEventArgs>? ToolSucceeded; // active VM: success snackbar
    public event EventHandler<RunFailedEventArgs>? RunFailed;          // active VM: error snackbar

    public async Task RunTurnAsync(ChatTurnRequest request, CancellationToken external);
    public void Cancel();                                 // Cts?.Cancel() + cancel pending cards
    internal void SetState(ChatState next);               // no-op on unchanged; raises StateChanged
}
```

`RunTurnAsync` receives a **`ChatTurnRequest`** (the already-resolved persona,
provider, `turnSetup`, atCommands, tokenizationEnabled, the user/assistant
`AssistantMessage` instances) so the session does **not** need scoped
collaborators that produce *content effects* — the manager prepares the turn and
injects `IActionCardBuilder` (scoped, available because the manager is scoped)
into the session at construction. **`ITokenMapService` is NOT shared into the
session** — each session uses its **own** map via the manager-owned factory (see
**Token-map isolation**), to prevent cross-chat PII namespace collisions once
turns run concurrently (Step 4).

**Side-effect split inside the loop (Decision 1):** the run loop today calls
scoped UI effects inline. After relocation:

| Inline effect today (line) | Routing after relocation |
|---|---|
| `_snackbarService.Show(...)` in catch/finally (~418, 429, 433, 455, 463) | `ChatSession` raises `RunFailed` / `TurnCompleted`; **active** VM shows the snackbar, **background** turns route to `IBackgroundChatNotifier` via the manager (no scoped snackbar) |
| tool-success `_snackbarService.Show(...)` (~674) | raise `ToolSucceeded`; active VM snackbars, background → notifier |
| **vision-disabled catch composer restore (~440–443):** `Messages.Remove(assistantMessage)` + `Messages.Remove(userMessage)` + `HasMessages = Messages.Count > 0` + `InputText = userMessage.Content` | **Split.** `Messages.Remove(...)` is **session-local** — stays inside `ChatSession` (operates on its own `Messages`, fine even in background). The `HasMessages` recompute and `InputText = userMessage.Content` are **active-VM-only composer state**: `ChatSession` raises `RunFailed` with a payload `{ RestoreInputText = userMessage.Content, IsVisionRejection = true }`. The VM applies the `InputText` restore **only when the failing session is `manager.ActiveSession`**; for a background vision-rejection it no-ops the restore (the message pair is already dropped). This prevents a background turn from clobbering whatever the user is typing in the visible chat. |
| `GenerateFollowupsAsync(...)` (~408) | stays a VM concern: VM subscribes to active `TurnCompleted` and runs followups against the active session's last message. Background sessions do **not** generate followups (deferred; flag) |
| TTS `SpeakMessageAsync` (~485) | active VM only, on `TurnCompleted`, gated on `IsTtsEnabled` |
| `ChatTitleChip.SetTitle(...)` — persist path (~520) **and auto-title path (`RenameChatAsync`, ~576)** | **Both** route through `SessionTitleChanged`. The manager raises `SessionTitleChanged(session)` for the owning session; the active VM forwards to `ChatTitleChip` **only when that session is `manager.ActiveSession`** (an is-active check). This **replaces** the old `if (_disposed || _currentChatId != chatId) return;` guard at `RenameChatAsync` (`AssistantViewModel.cs:575`) — auto-title is fire-and-forget and a background chat's late title must not overwrite the foreground chip. Displaying a title is allowed (CLAUDE.md forbids *logging* it, not showing it). |
| `App.Current.Dispatcher.InvokeAsync` for `ActionCards.Add` (~654) and `Suggestions.Add` (~1302), plus the **un-dispatched** `Sources.Add` in `ApplyWebCitations` (~1320) and the synchronous `Messages.Add` prefix (~302, 306) and `assistantMessage.Content =` streaming writes (~394) | **No new dispatch needed** because of the UI-thread-affinity invariant below. The two existing `InvokeAsync` sites stay (harmless, redundant). `Sources.Add` (currently un-marshalled) remains correct precisely because the loop never leaves the UI thread. See Threading. |

**Threading — the load-bearing invariant (do not leave to the implementer):**

`RunTurnAsync` is **UI-thread-affine**. The manager starts a turn by calling
`session.RunTurnAsync(...).SafeFireAndForget(logger)` **directly from the UI
thread** (the send command runs on the UI dispatcher). `SafeFireAndForget`
(`Helpers/TaskExtensions.cs:11`) merely `await`s the task — it uses **no
`Task.Run` and no `ConfigureAwait(false)`** — so the synchronous prefix
(`Messages.Add(userMessage/assistantMessage)`) and **every** `await`
continuation in the loop (streaming `assistantMessage.Content =`,
`Sources.Add`, the terminal `FinalizeTurn`) resume on the captured UI
`SynchronizationContext`. All four bound collections — `Messages`, `Sources`,
`ActionCards`, `Suggestions` — therefore mutate on the UI thread automatically,
and the active session's `ItemsControl ItemsSource={Binding Messages}`
(`AssistantView.xaml:117`) never throws the cross-thread `CollectionView`
`NotSupportedException`.

> **Hard rule for the implementer:** the word "background" in this plan means
> *the turn's task is not awaited by the send command (fire-and-forget) so the
> user can navigate away* — it does **NOT** mean `Task.Run`. **No `Task.Run`
> and no `ConfigureAwait(false)` anywhere in `RunTurnAsync` or the manager's
> turn-driving code.** If a future change genuinely needs off-thread work, it
> must either marshal **all four** collection mutations via
> `App.Current.Dispatcher` *and* add `Sources.Add` to the dispatched set, **or**
> call `BindingOperations.EnableCollectionSynchronization` on each session's
> `Messages`. v1 takes neither path — it keeps the loop UI-affine.

Because the loop is UI-affine, `StateChanged` is raised on the UI thread during
a turn. For the rare transition raised off-thread (e.g. a `Cancel()` invoked
from a non-UI caller), the **manager** marshals state events to the captured UI
sync-context before re-raising, so subscribers (indicator, notifier) never touch
UI off-thread. The terminal `FinalizeTurn` block (isActive read → decide
Completed/Idle → `SetState` → notifier route) is a single **synchronous** block
with no `await` inside it, so it serializes against `SetActive` (also UI thread)
under the dispatcher's single-threaded ordering — closing the
completion-vs-switch race without a separate marshalling mechanism.

---

## `ChatSessionManager`

New files:
- `src/Pia.Wpf/Services/Interfaces/IChatSessionManager.cs`
- `src/Pia.Wpf/Services/ChatSessionManager.cs`

```csharp
public interface IChatSessionManager
{
    ChatSession? ActiveSession { get; }
    IReadOnlyCollection<ChatSession> LiveSessions { get; }

    event EventHandler<ChatSession?>? ActiveChanged;
    event EventHandler<SessionStateChangedEventArgs>? SessionStateChanged;
    event EventHandler<SessionTitleChangedEventArgs>? SessionTitleChanged;

    ChatSession GetOrCreateActiveForNewChat();          // NewChat / Clear
    ChatSession? TryGetLive(Guid chatId);               // is this chat already running?
    Task<ChatSession> ActivateAsync(Guid chatId);       // resume: attach live OR load from DB
    void SetActive(ChatSession session);                // swap active; clears Completed→Idle
    Task StartTurnAsync(ChatSession session, ChatTurnRequest request); // run in background
    ChatState GetState(Guid chatId);                    // live state or Idle if not live
}
```

`ChatSessionManager` is `AddScoped`. It captures the UI `SynchronizationContext`
on construction with the **same guard the established pattern uses**
(`AssistantHistoryViewModel.cs:91`), so an off-thread first-resolution
regression fails loud instead of silently dispatching to the wrong thread:

```csharp
_syncContext = SynchronizationContext.Current
    ?? throw new InvalidOperationException("ChatSessionManager must be created on the UI thread");
```

It injects: `IAssistantChatService` (singleton, persist), `IActionCardBuilder`
(scoped, passed to sessions), the per-session token-map factory (see
**Token-map isolation** below), `IBackgroundChatNotifier` (singleton),
`ILoggerFactory`. It implements `IDisposable` (see **Disposal**).

**`ActivateAsync(chatId)` (Decision 4 — the branch that stops cancellation):**

```
1. live = TryGetLive(chatId)
2. if (live is not null):
       SetActive(live)                  // swap active; DO NOT cancel, DO NOT reload
       return live                      // its Messages already hold any pending action card
3. else:
       chat = await _chatService.GetAsync(chatId)
       session = new ChatSession(chat)  // hydrate Messages from DTO via AssistantMessageMapper
       register in Sessions
       SetActive(session)
       await _chatService.TouchLastAccessedAsync(chatId)
       return session
```

This is exactly the behavior that today's `ResumeChatAsync` violates: step 2 is
new and **must not** cancel `_streamingCts` or reload. Because each session owns
its own `Messages`, activating a live `WaitingForTool` session **reveals its
pending action card for free** — the card already lives in that session's
`Messages` collection, so swapping the active session swaps the card into view.
(This is the part reviewers will doubt; it is correct precisely because
`Messages` is per-session, not shared.)

`StartTurnAsync` is **called on the UI thread** by the send command and does
`session.RunTurnAsync(request, session.Cts.Token).SafeFireAndForget(logger);`
(see Threading invariant — **no `Task.Run`**). It returns synchronously so the
send command completes and the user can navigate away while the turn streams on
the UI dispatcher's continuations. The manager subscribes to each session's
`StateChanged`; the **terminal** decision is made inside the synchronous
`FinalizeTurn(session)` block (isActive read → Completed/Idle → `SetState` →
notifier route, no `await` inside), and for non-terminal transitions, when the
changed session is **not** `ActiveSession` and the new state is
`Error`/`WaitingForTool`/`Completed`, it routes to `IBackgroundChatNotifier`.

**Session retirement:** a session is removed from `Sessions` when it is not
active **and** its state is `Idle` or `Error` after a `TurnCompleted` and the
user has activated a different chat — i.e. we keep finished background sessions
around until acknowledged (`Completed` cleared to `Idle` on activate), then a
small reaper drops non-active `Idle`/`Error` sessions older than N (default keep
last 8 live sessions; flag as openQuestion).

> **✅ IMPLEMENTED (2026-06-22, open-question A7 resolved):** the reaper ships. It
> runs at the end of `ChatSessionManager.SetActive` (the sole session-accumulation
> point) with `MaxRetainedSessions = 8`: keep the 8 most-recently-active sessions
> (LRU via a per-session `LastActivatedSequence` stamp), reap only non-active
> `Idle`/`Error` ones beyond the window. In-flight (`Running`/`WaitingForTool`) and
> unread `Completed` sessions are **never** dropped (soft cap). Retired sessions
> are unsubscribed + disposed; their finished turns are already persisted, so a
> later `ActivateAsync` re-hydrates from the store (no data loss). This supersedes
> the original "v1 never auto-evicts; reaper is shouldHave" framing. See the
> open-questions doc's Resolved table and A7, and the reaper unit tests in
> `ChatSessionManagerTests`.

### Token-map isolation (Decision 4 dependency — the concurrent-turns PII gate)

**This blocks Step 4 (concurrent background turns) and lands *with* the
"don't-cancel-on-switch" flip, never after it.**

`TokenMapService` issues **counter-based** tokens (`[Person_1]`, `[Person_2]`,
… built from `_categoryCounters`, `TokenMapService.cs:31-46`) and `Clear()`
**resets those counters to zero** (`:197-203`). The `TokenizingAiClientService`
decorator tokenizes/detokenizes on **every** turn. Today exactly one turn runs
at a time, so the namespace is safe. After Step 4 two sessions can be `Running`
concurrently and **share one token namespace**, producing real cross-chat PII
contamination: chat A maps `Alice → [Person_1]`, is backgrounded; the user
`NewChat`s (`ExecuteClearConversation` → `_tokenMapService.Clear()` +
`InitializeAsync`, `AssistantViewModel.cs:722-730`), or a memory write re-inits
(`:679-683`), or chat B maps `Bob → [Person_1]`; when A's stream returns
`[Person_1]` its final `Detokenize` (the safety net at `:477-479`) now resolves
to **Bob** — B's PII injected into A's transcript, persisted, and re-sent.

> **Decorator reality (verified against `TokenizingAiClientService.cs:41-47`):**
> the decorator does **not** use the window-scope `ITokenMapService` the VM
> injects. It lazily calls `_scopeFactory.CreateScope()` and resolves its **own**
> `ITokenMapService` (cached in `_tokenMapService`). So today there are already
> *two* maps — the decorator's (used during the AI call) and the VM-injected one
> (used for the `:479` safety-net detok, `DetokenizeForDisplay`, memory re-init,
> and `Clear()`/`InitializeAsync` on new-chat). Both stay coherent only because
> they tokenize the same source deterministically and one turn runs at a time. A
> per-`ChatSession` instance the manager constructs **will not flow into the
> decorator** without extra plumbing — this is why a naive "give each session its
> own map" is unbuildable as worded.

**v1 mechanism — AsyncLocal ambient "current turn map":**

1. **`ChatSession` owns its `ITokenMapService` as a field** (`session.TokenMap`),
   constructed via a manager-owned factory (`Func<ITokenMapService>` — the scoped
   registration becomes the factory the manager calls) and `InitializeAsync()`-ed
   at session creation. **This field is the durable handle for all out-of-turn
   token-map work** (see steps 4–5); it exists whether or not a turn is running.
2. Introduce a static `TokenMapAmbient` holding an
   `AsyncLocal<ITokenMapService?> Current`. `AsyncLocal` flows down each logical
   async turn and is **isolated across interleaved turns** even though they share
   the UI thread (each `await` continuation carries its own `ExecutionContext`),
   which is exactly the WaitingForTool-await scenario. **`AsyncLocal` is purely
   the decorator's reach-around for the *in-flight* turn** — nothing else reads it.
3. `ChatSession.RunTurnAsync` sets `TokenMapAmbient.Current = this.TokenMap` for
   the duration of the turn (restore to previous in `finally`), so the decorator
   can find this turn's map.
4. The decorator's `TryGetTokenMapService()` (`:41-47`) is changed to prefer
   `TokenMapAmbient.Current` when set, falling back to its lazily-created scope
   map when null (preserves all non-assistant callers — Optimize, voice
   one-shots — unchanged).
5. **All direct (non-decorator) token-map calls go through the session field, not
   AsyncLocal**, because they run on UI-thread command paths *outside* any
   `RunTurnAsync` async flow where `TokenMapAmbient.Current` would be `null`:
   - safety-net detok (`:479`), `DetokenizeForDisplay` (`:698`), memory re-init
     (`:681/:1363`) → `session.TokenMap` of the owning session (in-turn, this is
     the active session).
   - `ExecuteClearConversation`'s `Clear()`+`InitializeAsync` (`:724-727`) →
     `manager.ActiveSession.TokenMap` only — never a global reset, so clearing one
     chat cannot poison another's namespace.

> Under the fallback openQuestion (b) whole-turn serialization, only one turn
> tokenizes/detokenizes at a time, so the AsyncLocal reach-around mostly
> dissolves — the session field alone suffices and the decorator can keep
> resolving its single scope map. (a) is preferred because it preserves the
> concurrent-background headline.

**Fallback if (3)/(4) prove too invasive to land safely:** keep a single
manager-owned gate and **serialize whole turns** (one `SemaphoreSlim` around the
entire `RunTurnAsync`, not per-op) so only one tokenizing turn runs at a time.
This degrades the headline "run concurrently in the background" to "queued in
the background" and is captured as an **openQuestion** (a) AsyncLocal-isolation
vs (b) whole-turn serialization. A per-op semaphore (the previous draft) is
**rejected** — it prevents dictionary corruption but not namespace reuse, so it
does **not** fix the PII leak.

**Regression test (gates Step 4):** two sessions tokenize distinct PII,
interleave a `Clear()` + re-init between them, assert neither session's
`Detokenize` resolves the other's value.

### Disposal (`ChatSessionManager : IDisposable`)

`ActionCardInfo.WaitForUserDecisionAsync()` is a bare `TaskCompletionSource`
with **no timeout** (`ActionCardInfo.cs:64`), resolved only by UI
Accept/Decline/Cancel. A background session sitting in `WaitingForTool` at
shutdown is an abandoned task blocked forever unless someone cancels it. The VM
`Dispose` **no longer** cancels live sessions (that would defeat
background-continue) — so the **manager** must:

- implement `IDisposable`; in `Dispose`, iterate **all** live sessions calling
  `session.Cancel()`, which cancels the per-session `Cts` **and** any pending
  action cards (the existing `CancelPendingActionCards` pattern at
  `AssistantViewModel.cs:789-797`, relocated into `ChatSession`).
- **Per-session `Cts` lifecycle:** created at `RunTurnAsync` entry, disposed in
  **that turn's** `finally`. `Cancel()` only *cancels* — it never disposes a
  `Cts` the running turn still holds (the turn's own `finally` disposes it).
- The manager is `AddScoped` and resolved **into** the window scope (via VM
  injection), so `IServiceScope.Dispose` disposes it at
  `WindowManagerService.CloseAndDisposeAll` → `ManagedWindow.Dispose` →
  `Scope.Dispose` (`WindowManagerService.cs:239-252`, `ManagedWindow.cs:20-23`).
  Verify the manager is genuinely resolved into the scope (it is, via the VM
  ctor param) so its `Dispose` actually runs at shutdown.

---

## `AssistantViewModel` becomes a thin view

### Decision 3 — how the visible VM swaps message lists

`Messages` is get-only today and bound in `AssistantView.xaml`
(`ItemsControl ItemsSource="{Binding Messages}"`, line 117; `HasMessages` at
lines 53 & 111). **Chosen approach: make `Messages` a settable
`[ObservableProperty]` that points at `ActiveSession.Messages`.** This is the
smallest XAML delta — the existing `{Binding Messages}` keeps working; only the
backing field changes from a readonly `new()` to a property reassigned on
active-session swap.

```csharp
// before:  public ObservableCollection<AssistantMessage> Messages { get; } = new();
// after:
[ObservableProperty]
private ObservableCollection<AssistantMessage> _messages = new();
```

On `ActiveChanged`/activate, the VM does:

```csharp
Messages = active.Messages;            // re-points the ItemsControl (triggers OnMessagesChanged)
IsStreaming = active.IsStreaming;      // proxy
HasMessages = active.Messages.Count > 0;
ActiveState = active.State;            // proxy (badge)
ChatTitleChip.SetTitle(active.Title);
// move session StateChanged / TitleChanged / TurnCompleted / ToolSucceeded /
// RunFailed handlers to THIS session, unsubscribe from the previous
```

`IsStreaming`, `HasMessages`, and `ActiveState` stay `[ObservableProperty]` on
the VM but are **driven by the active session** (proxied on activate and on the
session's `StateChanged`). The command `CanExecute` wiring (`OnPropertyChanged`
→ `SendMessageCommand.NotifyCanExecuteChanged`, line ~262) is unchanged because
it keys off `IsStreaming`.

**`HasMessages` must track the live collection (advisor's catch).** Today
`HasMessages` is maintained imperatively (set true on `Add` at `:303`,
recomputed on `Remove` at `:442/:764/:833`). When `Messages` becomes a swappable
property, the VM must **re-wire the `CollectionChanged` subscription**, not just
the session events: on swap, unsubscribe `CollectionChanged` from the **old**
`Messages` and subscribe it on the **new**, with the handler doing
`HasMessages = Messages.Count > 0`. The generated `partial void
OnMessagesChanged(ObservableCollection<AssistantMessage>? oldValue,
ObservableCollection<AssistantMessage> newValue)` is the natural place to do the
unsubscribe-old / subscribe-new swap, so a background session's list growing or
shrinking while it is active keeps `HasMessages` correct.

### Every `Messages.*` / `IsStreaming` / `HasMessages` call site that must move

Enumerated so the seam genuinely preserves behavior (advisor's sweep):

| Member | Today | After |
|---|---|---|
| `ExecuteSendMessage` (~288) | adds user+assistant msgs, sets `IsStreaming`, runs loop | builds `ChatTurnRequest` against `ActiveSession`, calls `manager.StartTurnAsync`; the **session** adds messages + sets state; VM just clears input/attachment |
| `PersistCurrentChatAsync` (~492) | reads `Messages`, `_currentChatId` etc. | moves into the **manager** (keyed by session); VM no longer persists directly. Manager persists on `TurnCompleted` and on activate-touch |
| `ExecuteClearConversation` / `NewChat` (~706/733) | cancels CTS, clears `Messages`, nulls `_currentChat*` | `manager.GetOrCreateActiveForNewChat()`; VM re-points `Messages`; **does not** cancel other live sessions |
| `ResumeChatAsync` (~735) | cancels CTS + reload | `await manager.ActivateAsync(id)` (the new branch) |
| `OnNavigatedTo(Guid)` (~886, **`async void`**) | handles the Guid branch then `ResumeChatAsync` | **Move the Guid-activation branch into `OnNavigatedToAsync`.** `NavigationService` awaits `OnNavigatedToAsync` (`NavigationService.cs:92`) but only fire-calls the sync `async void OnNavigatedTo` (`:91`) — an exception on the toast→activate path (`ActivateAsync` awaits `IAssistantChatService.GetAsync` on the no-live-session branch) would be swallowed by `async void` and crash unobserved. Routing it through `OnNavigatedToAsync` means the await is observed and logged. The sync `OnNavigatedTo` keeps the non-Guid setup (RandomizeSuggestions, string/selection params). |
| `ExecuteCancelStreaming` (~700) | `_streamingCts.Cancel()` | `ActiveSession.Cancel()` (cancels only the active session) |
| `ExecuteRegenerateMessage` (~815) | mutates `Messages`, re-sends | operates on `ActiveSession.Messages` + `manager.StartTurnAsync` |
| `AddVoiceModeConversation` (~1373) | `Messages.Add` | `ActiveSession.Messages.Add` |
| `StreamVoiceModeResponse` (~1187) | iterates `Messages` for history | iterates `ActiveSession.Messages`; voice mode stays auto-approve and **active-only** (voice never runs in background) |
| `SpeakMessageAsync` (~1128) | iterates `Messages` | iterates `ActiveSession.Messages` |
| `Dispose` (~1384) | cancels `_streamingCts` | unsubscribes from the active session's events + `Messages.CollectionChanged`; **does not** cancel live sessions (the **manager** owns them and cancels every session's `Cts` + pending cards in **its** `IDisposable.Dispose`, run when the window scope is disposed — see Disposal). This is what lets Assistant→History→Assistant not kill a running turn. |

`ChatTitleChip` construction (line ~212) is unchanged; its `_resumeChat`
callback now calls `manager.ActivateAsync` via the VM (so the live-attach branch
applies to the chip flyout and quick switcher too).

---

## State indicator UI (shouldHave)

### Converters

New file `src/Pia.Wpf/Converters/ChatStateConverters.cs` (CRLF), mirroring
`ReminderStatusToBrushConverter` (resolve Pia design tokens via
`Application.Current.TryFindResource`, so theme changes flow through
DynamicResource):

```csharp
// ChatStateToBrushConverter : IValueConverter  (Kind = Background | Foreground)
//   Idle           -> ("SurfaceMutedBrush", "TextMutedBrush")
//   Running        -> ("PiaAccentSoftBrush", "PiaAccentBrush")
//   WaitingForTool -> ("WarnSoftBrush",      "WarnBrush")
//   Completed      -> ("SuccessSoftBrush",   "PiaSuccessBrush")
//   Error          -> ("WarnSoftBrush",      "PiaDangerBrush")
//
// VERIFIED token names against PiaTokens.Dark.xaml: SurfaceMutedBrush(@63),
// TextMutedBrush(@68), PiaAccentBrush(@57), PiaAccentSoftBrush(@59),
// WarnBrush(@74), WarnSoftBrush(@75), SuccessSoftBrush(@72), PiaSuccessBrush(@71),
// PiaDangerBrush(@73) all EXIST. There is NO `DangerSoftBrush` and NO
// `DangerBrush` (the previous draft was wrong). Error therefore uses the
// existing PiaDangerBrush foreground over the WarnSoftBrush soft fill. If a true
// danger-soft fill is wanted later, add a `DangerSoftBrush` token (mirroring
// SuccessSoftBrush) to BOTH PiaTokens.Dark.xaml and PiaTokens.Light.xaml — but
// v1 reuses WarnSoftBrush to avoid a token addition. Use these exact names.
//
// ChatStateToGlyphConverter : IValueConverter   -> SymbolRegular / glyph string
//   Idle -> (none/dot), Running -> spinner glyph, WaitingForTool -> hand/pause,
//   Completed -> checkmark, Error -> error glyph
//
// ChatStateToLabelConverter : IValueConverter   -> _localizationService[...] via
//   a static localization accessor, OR an EnumToLocalizedStringConverter reuse
//   (already exists) keyed "ChatState_<Name>"
```

Token names are now pinned (verified, see converter comment) — no token
discovery left for the implementer. Error reuses `PiaDangerBrush` foreground +
`WarnSoftBrush` fill; the absent `DangerSoftBrush`/`DangerBrush` pair from the
previous draft is **not** used.

### Badge on the title chip

Add a small state pill to the chip control (`PiaChatTitleChip.xaml` from the
history feature). Bind to a new `AssistantViewModel.ActiveState`
(`[ObservableProperty] ChatState` proxied from the active session's
`StateChanged`). Pill = `Border` (Background = `ChatStateToBrushConverter`) +
glyph + `ChatStateToLabelConverter` text. Hidden when `Idle` (no clutter).

### History rows — **a real per-row wrapper VM is required** (advisor's catch)

**There is no row VM today.** `AssistantHistoryViewModel.Chats`
(`:38`), `AssistantChatGroupViewModel.Items` (`:490`), `SelectedChat` (`:59`),
and `SelectedChatDetail` (`:62`) are all `ObservableCollection<SyncAssistantChat>`
/ `SyncAssistantChat` — the raw `Pia.Shared` DTO — bound through a 4-level XAML
chain with **no wrapper**: `ChatGroups` → `PiaAssistantChatGroupCard`
(`ItemsSource={Binding Items}`, `PiaAssistantChatGroupCard.xaml:64`) → `ListBox`
→ `PiaAssistantChatRow` (`DataTemplate`, `:74`) → `PiaAssistantChatRowContent`,
all with `DataContext = SyncAssistantChat`. `SyncAssistantChat` is a Shared sync
DTO with no observable state member and **must not be extended** (Hard rule c;
plan "DTO / persistence" section). So `RowState` "computed on the row VM" has
**nowhere to live**, and live re-resolve on `SessionStateChanged` is impossible
against a raw-DTO `DataContext` — the glyph could never update when a background
chat transitions while the history view is open, which is the exact feature
scenario. This is real Step-2/3 churn, not a one-line property add.

**Introduce** `src/Pia.Wpf/ViewModels/AssistantChatRowViewModel.cs` (CRLF):

```csharp
public sealed partial class AssistantChatRowViewModel : ObservableObject
{
    public SyncAssistantChat Chat { get; }
    public Guid Id => Chat.Id;
    public string? Title => Chat.Title;       // proxied for the row XAML
    public DateTime UpdatedAt => Chat.UpdatedAt;
    [ObservableProperty] private ChatState _state;   // live, refreshed on SessionStateChanged
    public AssistantChatRowViewModel(SyncAssistantChat chat, ChatState seed) { Chat = chat; _state = seed; }
}
```

Required rewiring (Step 2 / Step 3 churn — enumerate so it is not hidden):

- `AssistantHistoryViewModel.Chats` and `AssistantChatGroupViewModel.Items`
  become `ObservableCollection<AssistantChatRowViewModel>`.
- `RebuildGroups()` wraps each chat: `new AssistantChatRowViewModel(chat,
  manager.GetState(chat.Id))` (live state or `Idle` if not live).
- Inject `IChatSessionManager` into `AssistantHistoryViewModel` (same scope —
  legal). Subscribe `SessionStateChanged`; on it, find the matching row VM by
  `Id` and set its `State` (marshal via the existing `_syncContext`). Unsubscribe
  in `Dispose`.
- **`SelectedChat`/`SelectedChatDetail` and the read paths key off the DTO and
  must unwrap.** `SelectedChat` becomes `AssistantChatRowViewModel?` (it is set
  from the `ListBox` selection, now a row VM); `LoadSelectedChatDetailAsync`
  (`:432-465`), `CanExecuteWithSelection`, `ExportChat`, and `ResumeChat` read
  `SelectedChat.Chat.Id` / `SelectedChat.Chat` instead of `SelectedChat.Id`.
  `SelectedChatDetail` stays `SyncAssistantChat?` (it is the loaded full chat).
- **Re-point the row XAML** `PiaAssistantChatRow` / `PiaAssistantChatRowContent`
  / `PiaAssistantChatGroupCard` bindings from `{Binding Title}` etc. to
  `{Binding Chat.Title}` / `{Binding Title}` (the proxy) and add the state glyph
  bound to `{Binding State}` via the converters above.

### Quick-switcher rows (the easy half — already correct)

- `QuickSwitcherMatchViewModel` **is already** an `ObservableObject`
  (`ChatTitleChipViewModel.cs:371`), so the plan's original instruction holds:
  add `[ObservableProperty] ChatState State`, populate from
  `manager.GetState(id)` when building matches, refresh on `SessionStateChanged`.
- The flyout's `ChatChipItemViewModel` (`ChatChipModels.cs:14`) is a **plain
  record** with no change notification. The flyout state badge is **deferred**
  (the flyout is date-only today); if it is ever un-deferred, convert
  `ChatChipItemViewModel` from `record` to `ObservableObject` first. Flagged.

State glyph renders left of the title using the converters above.

---

## Group-by-state (shouldHave)

`AssistantHistoryViewModel` gets a grouping toggle:

```csharp
public enum ChatGroupMode { Date, State }
[ObservableProperty] private ChatGroupMode _groupMode = ChatGroupMode.Date;
```

- A segmented toggle / two `RadioButton`s in `AssistantHistoryView.xaml` header
  ("By date" / "By state"). On change, `RebuildGroups()` branches.
- `RebuildGroups()` keeps the existing date path. New state path groups by each
  row VM's `State` (seeded from `manager.GetState(chat.Id)` at wrap time, see the
  row-VM section):
  - **Live** chats use their live `ChatState`.
  - **Persisted-but-not-live** chats (the common case in history) have no live
    state → bucket as **`Idle`** (spec demands this mapping be explicit). Order:
    `WaitingForTool` → `Running` → `Error` → `Completed` → `Idle` (action-needed
    first).
  - Because the items are now `AssistantChatRowViewModel`, a live
    `SessionStateChanged` updates a row's `State` in place; if grouping is "By
    state", the handler also re-buckets (re-run `RebuildGroups` on a state
    change while `GroupMode == State`).
- `AssistantChatGroupViewModel` currently keys on `HistoryDateBucket Bucket`.
  Generalize to a `string GroupKey` + keep `Bucket` for the date path, or add a
  parallel `ChatState? StateBucket`. Minimal: add `ChatState? StateBucket` and
  branch `RebuildGroups` on `GroupMode`; expand-state dictionary keys off
  whichever is non-null.
- Group headers localized: `ChatState_Group_<Name>` keys.

The chip flyout's group-by-state is **deferred** (flyout is date-only today;
adding a second axis there is low value vs. the full history view). Flag.

---

## Background state-change notifications (shouldHave)

New files:
- `src/Pia.Wpf/Services/Interfaces/IBackgroundChatNotifier.cs`
- `src/Pia.Wpf/Services/BackgroundChatNotifier.cs`  (**singleton**)

Modeled directly on `ScheduledJobNotificationSurface`:

```csharp
public interface IBackgroundChatNotifier
{
    void NotifyStateChange(Guid chatId, string displayTitle, ChatState state);
}
```

`BackgroundChatNotifier` injects `INotificationService` (singleton, in-app
toast), `IWindowManagerService` (singleton), `ILocalizationService`,
`ILogger`. It:

1. Registers `ToastNotificationManagerCompat.OnActivated` eagerly in its ctor
   (same reason as the scheduled-job surface — catch cross-session clicks).
2. `NotifyStateChange` builds a `ToastContentBuilder` with the chat title in the
   body (displaying a title is allowed; **logging** it is not) and a button
   `AddArgument("action", "openChat")` + `AddArgument("chatId", id)`. Also fires
   the in-app `INotificationService.ShowToast(...)` on the dispatcher (mirrors
   the scheduled-job in-app fallback).
3. `OnActivated` parses `action == "openChat"`, then on the dispatcher calls a
   new `IWindowManagerService.ShowAssistantChat(Guid chatId)`. **Do NOT copy
   `BringMainWindowForward`** from `ScheduledJobNotificationSurface.cs:241-247`.
   That helper activates `Application.Current.MainWindow`, which — with
   Optimize/Research windows also open (`WindowManagerService` keys one window
   per `WindowMode`) — may be a **different** mode's window, racing/contradicting
   the `ShowWindow(Assistant)` activation. `ShowWindow(WindowMode.Assistant)`
   already shows + activates + focuses the single Assistant window
   (`WindowManagerService.cs:49-55` on reuse, `:114-118` on create). If extra
   foregrounding is ever needed, activate the **specific** managed Assistant
   window (`_windows[WindowMode.Assistant].Window.Activate()`), never
   `Application.Current.MainWindow`.

**Manager → notifier policy (Decision 1, background half):** the manager calls
`NotifyStateChange` **only** when the changed session is **not** the active one
**and** the new state is one of **`Error`, `WaitingForTool`, `Completed`**.
`Running`/`Idle` never notify (Running fires constantly — that would be spam).
This is a user-facing default; flagged as openQuestion.

### New `WindowManagerService.ShowAssistantChat(Guid)`

Add next to `ShowResearchHistoryWithEntry` (line 191), same shape:

```csharp
public void ShowAssistantChat(Guid chatId)
{
    ShowWindow(WindowMode.Assistant);                 // reuse the single window
    if (!_windows.TryGetValue(WindowMode.Assistant, out var managed)) return;
    var nav = managed.Scope.ServiceProvider.GetRequiredService<INavigationService>();
    nav.NavigateTo<AssistantViewModel, Guid>(chatId); // OnNavigatedTo(Guid) → manager.ActivateAsync
}
```

This satisfies **Hard rule b (one window)**: `ShowWindow(Assistant)` reuses the
existing window if open; navigation happens **inside** it. The toast-click path
is therefore: **toast → `OnActivated` → `ShowAssistantChat(id)` → `ShowWindow`
(reuse, which activates/focuses the Assistant window) →
`NavigateTo<AssistantViewModel, Guid>` → `OnNavigatedToAsync(Guid)` (awaited by
`NavigationService`, exceptions observed) → `manager.ActivateAsync(id)` →
live-attach branch → Messages swap reveals the pending `WaitingForTool` action
card.** No `BringMainWindowForward`. Add the method to `IWindowManagerService`.

---

## DI registration

In `Bootstrapper.cs`:

```csharp
// Singleton (cross-window notification surface; mirrors ScheduledJobNotificationSurface @242)
services.AddSingleton<IBackgroundChatNotifier, BackgroundChatNotifier>();

// Scoped per window (injects scoped IActionCardBuilder + ITokenMapService — MUST be scoped)
services.AddScoped<IChatSessionManager, ChatSessionManager>();
```

`AssistantViewModel`, `AssistantHistoryViewModel` (both already `AddScoped`)
gain an `IChatSessionManager` ctor param. No lifetime changes elsewhere.

---

## Localization

New keys in `MessageStrings.resx` + `.de.resx` + `.fr.resx` (no English-only —
Pia convention):

| Key | English |
|---|---|
| `ChatState_Idle` | Idle |
| `ChatState_Running` | Running |
| `ChatState_WaitingForTool` | Waiting for confirmation |
| `ChatState_Completed` | Done |
| `ChatState_Error` | Error |
| `ChatState_Group_Idle` | Idle |
| `ChatState_Group_Running` | Running |
| `ChatState_Group_WaitingForTool` | Waiting for confirmation |
| `ChatState_Group_Completed` | Completed |
| `ChatState_Group_Error` | Errored |
| `AssistantHistory_GroupBy_Date` | By date |
| `AssistantHistory_GroupBy_State` | By state |
| `Notification_BackgroundChat_Title` | Background chat update |
| `Notification_BackgroundChat_WaitingForTool` | "{0}" needs your confirmation |
| `Notification_BackgroundChat_Completed` | "{0}" finished |
| `Notification_BackgroundChat_Error` | "{0}" ran into an error |
| `Notification_OpenChat` | Open chat |

German + French translations land in the same change.

---

## Privacy logging (CLAUDE.md — enforced)

- State transitions: `LogInformation("Chat {ChatId} state {State} at {Utc}", …)`
  — **id / enum / timestamp only**, never title or content.
- Chat title: `SensitiveDebug` only when logged. A toast/badge **displaying** the
  title is fine (ScheduledJobNotificationSurface already shows `job.Name`).
- Tool args/results inside the relocated loop: keep the existing
  `SensitiveDebug` calls (lines ~641, ~624 `#if DEBUG`) verbatim.
- No new full URLs introduced.

---

## Sequencing (each step ships green)

### Step 1 — Core seam (mustHave; **green build + green tests**)
- `ChatState`, `ChatSession`, `IChatSessionManager`/`ChatSessionManager`,
  `ChatTurnRequest`, event-arg types. Manager `: IDisposable` with the
  UI-sync-context guard; `ChatSessionManager` registered `AddScoped`.
- Relocate the run loop from `ExecuteSendMessage` into `ChatSession.RunTurnAsync`
  **UI-thread-affine** (no `Task.Run`, no `ConfigureAwait(false)`); move
  `PersistCurrentChatAsync` + `TryStartAutoTitle` into the manager.
- `AssistantViewModel` delegates: `Messages` settable `[ObservableProperty]` with
  `OnMessagesChanged` swapping the `CollectionChanged` subscription (keeps
  `HasMessages` live); proxies `IsStreaming` / `HasMessages` / `ActiveState`;
  rewire send/resume/clear/cancel/regenerate/voice per the call-site table;
  Guid-activation moves into `OnNavigatedToAsync`.
- Decision-1 routing wired: `RunFailed`/`TurnCompleted`/`ToolSucceeded`/
  `SessionTitleChanged` events; active-VM-only snackbar/TTS/followups/InputText-
  restore/title-forward; vision-catch `Messages.Remove` stays session-local.
- **v1 switch behavior is still SINGLE-ACTIVE**: `ActivateAsync` keeps today's
  semantics for now (a switch may still settle the prior turn) so the seam lands
  without the background-routing risk. The *plumbing* (per-session Cts, state,
  events) exists; the "don't cancel on switch" flip is Step 4.
- DI registration.
- **Acceptance for Step 1:** `dotnet build` clean; `dotnet test` green; send /
  resume / clear / cancel / regenerate / voice all behave exactly as before.

### Step 2 — Row-VM wrapper + indicator UI (foundational for 2 & 3)
- **First** introduce `AssistantChatRowViewModel` and re-point
  `AssistantHistoryViewModel.Chats`, `AssistantChatGroupViewModel.Items`,
  `SelectedChat`/read paths, and the `PiaAssistantChatRow` / `…RowContent` /
  `PiaAssistantChatGroupCard` XAML bindings (per the History-rows section). The
  indicator and group-by-state are **not buildable** until this wrapper exists.
- Converters (pinned token names) + chip badge + history/switcher row glyphs.
- `ActiveState` proxy wired; `IChatSessionManager` injected into the history VM
  and `ChatTitleChipViewModel`; `SessionStateChanged` subscriptions marshalled
  via `_syncContext`, updating the matching row VM's `State` in place.

### Step 3 — Group-by-state
- `ChatGroupMode` toggle + `RebuildGroups` state branch (over the row VMs) +
  persisted→Idle mapping + re-bucket on `SessionStateChanged` while in state
  mode + group-header keys.

### Step 4 — Background-continue-on-switch + notifications + token-map gate (the headline)
- **Token-map isolation lands FIRST in this step and gates the flip**: implement
  the AsyncLocal ambient current-map (or the whole-turn-serialization fallback),
  with the cross-contamination regression test green, **before** enabling
  concurrent turns. The concurrency flip must not merge against the shared map.
- Flip `ActivateAsync` to the **live-attach** branch (Decision 4): no cancel, no
  reload when a live session exists.
- `StartTurnAsync` runs turns detached (UI-affine `SafeFireAndForget`); the
  synchronous `FinalizeTurn` block decides Completed/Idle and routes non-active
  state changes to `IBackgroundChatNotifier`.
- `BackgroundChatNotifier` + `WindowManagerService.ShowAssistantChat` +
  `OnActivated` link (no `BringMainWindowForward`).
- Side-effect split fully enforced (no scoped snackbar fires for background
  turns); a background vision-rejection does not touch the foreground composer.

Steps 1–3 are independently shippable but **the headline feature is NOT delivered
until Step 4 merges** — Steps 1–3 deliberately preserve the cancel-on-switch
behavior. Splitting the "don't cancel" flip + token-map gate to Step 4 keeps the
risky behavior change isolated and the seam green first.

### Deferred (would risk the build or are low-value v1)
- `Queued` state + a real turn queue per session.
- Background sessions generating follow-up suggestions / TTS.
- Group-by-state **and** state badge inside the chip flyout (history view only
  for v1; flyout's `ChatChipItemViewModel` is a plain record — converting it to
  `ObservableObject` is the prerequisite).
- Session reaper / live-session eviction policy (unbounded live sessions in v1).
- Syncing `ChatState` to the cloud (it is runtime-only by design).
- Highlighting *which* message awaits confirmation (needs an `AssistantMessage`
  flag — additive, but defer).
- A dedicated `DangerSoftBrush` token (v1 reuses `WarnSoftBrush` for Error fill).
- Inspector-preview clearing `Completed` (v1 clears only on true activation).

### Post-step cleanup
After each step, run the `simplify` skill scoped to that step's `git diff
--name-only`, per the predecessor plan's recipe.

---

## Acceptance (dotnet build + dotnet test + observable behavior — NO UI automation)

**Build/test gate (every step):**
- `dotnet build` from `C:/projects/Pia.Wpf` exits 0, no new warnings.
- `dotnet test` (xUnit v3 / MTP) green.

**Unit tests to add (Step 1):**
- `ChatSession` state machine: a fake turn that streams text → `Running` then
  `Idle`; a turn that throws a handled exception → `Error`; `OperationCanceled`
  → `Idle` (not `Error`); a turn that blocks on a stubbed action card →
  `WaitingForTool`, then `Running` after the decision, then `Idle`.
- A background (non-active) session reaching end-of-turn with content →
  `Completed`; activating it → `Idle`.
- `ChatSessionManager.ActivateAsync`: live session present → returns the **same
  instance** without invoking `IAssistantChatService.GetAsync` (verify via a
  stub that records calls) and **without** cancelling its `Cts`; no live session
  → loads from the stubbed chat service and touches `LastAccessedAt`.
- `ChatSessionManager` routes a non-active `WaitingForTool`/`Completed`/`Error`
  transition to a stubbed `IBackgroundChatNotifier`, and routes nothing for
  `Running`/`Idle` or for the active session.
- `AssistantMessageMapper` round-trip unchanged (regression guard — no DTO
  change).

**Unit tests to add (Step 4 — gate the flip):**
- **Token-map cross-contamination guard:** two sessions tokenize distinct PII,
  interleave a `Clear()` + re-init between them, assert **neither** session's
  `Detokenize` resolves the other's value. (This test must pass before the
  concurrency flip merges.)
- **Finalize-vs-switch race:** activate a session while its turn is finalizing
  (drive `FinalizeTurn` and `SetActive` in the contrived order); assert the
  just-activated session ends **`Idle`** with **no** background toast, and a
  session left in background ends **`Completed`** with exactly one toast.

**Observable behavior (manual, no winwright):**
- Start a long turn in chat A, switch to chat B (after Step 4): chat A keeps
  streaming; a notification fires when A reaches `Completed`/`WaitingForTool`;
  clicking it returns to chat A in the **same** window with A's messages and any
  pending action card visible. No second window ever opens.
- Switch away from a chat blocked on a tool card (after Step 4): the card stays
  pending; the chip/row shows `WaitingForTool`; activating the chat reveals the
  card and confirming it resumes the turn.
- History view "By state" groups action-needed chats first; persisted chats with
  no live turn appear under `Idle`.
- Release-log check: `pia-*.log` contains **no** chat titles or message content
  from the new state-transition / notification code paths (grep to verify).

---

## Risks / notes

- **Captive dependency** — the manager **must** be scoped; injecting scoped
  `IActionCardBuilder`/`ITokenMapService` into a singleton manager throws at
  resolve time. Verified against Bootstrapper; tests resolve the scope to catch
  regressions.
- **`ITokenMapService` is a counter-based PII namespace that `Clear()` resets**
  — concurrent background turns sharing one map is a real **cross-chat PII leak**
  (chat A's `[Person_1]` detokenizes to chat B's value after B re-uses the
  namespace). The previous "one `SemaphoreSlim` per op" idea is **rejected** (it
  stops dictionary corruption, not namespace reuse). v1 fix is the AsyncLocal
  ambient current-map (per-logical-turn isolation), with whole-turn
  serialization as the documented fallback — see **Token-map isolation**. This
  gates Step 4 and ships with a regression test. openQuestion: (a) AsyncLocal vs
  (b) whole-turn serialization if the decorator change proves too invasive.
- **`OperationCanceledException` is not Error** — cancelling the active turn (or
  cancelling pending cards on clear) must land in `Idle`, matching today's
  snackbar-only "Cancelled" behavior. The catch ordering in `RunTurnAsync` must
  keep the `OperationCanceledException` arm before the generic `Exception` arm
  (as today).
- **Error design tokens** — resolved: `DangerSoftBrush`/`DangerBrush` do **not**
  exist; v1 uses `PiaDangerBrush` (foreground) + `WarnSoftBrush` (fill). No token
  discovery left for the implementer (names pinned in the converter spec).
- **Followups/TTS for background turns** — intentionally suppressed in v1
  (active-only). A background turn that completes does not speak or fetch
  followups; followups generate lazily if/when the user activates it (or are
  simply skipped). Flag.
- **`Dispose` must not cancel live sessions** — only the **manager** tears them
  down, and it must implement `IDisposable` and cancel every session's `Cts` +
  pending action cards (a `WaitingForTool` session at shutdown is an abandoned
  `TaskCompletionSource` otherwise). See **Disposal**. The VM's `Dispose` only
  unsubscribes (events + `Messages.CollectionChanged`).
- **Finalize-vs-switch race** — the terminal `isActive` read → Completed/Idle →
  `SetState` → notifier-route is one **synchronous** block on the UI thread
  (`FinalizeTurn`), serialized against `SetActive`. No `await` may appear inside
  it (persist *after*; if an await is unavoidable, re-read `isActive` post-await).
- **Notification spam** — even gated to Error/WaitingForTool/Completed, a chat
  that flaps could notify repeatedly. v1: de-dupe by only notifying on a
  *changed* state (the `SetState` no-op-on-unchanged already enforces one event
  per real transition).
- **Unbounded live-session memory** — v1 never auto-evicts; the manager holds
  every session created this app-run. Bounded by user behavior but not capped.
  A reaper (keep last N) is shouldHave / openQuestion. Residual risk noted.
- **Row-VM churn** — the per-row `AssistantChatRowViewModel` is real Step-2
  scope: it re-points `Chats`, `Items`, `SelectedChat`, the read/export paths,
  and 3 XAML controls. Indicator + group-by-state depend on it.
- **Single-active v1 (Step 1) vs. background (Step 4)** — Step 1 keeps
  single-active so the seam is green and side-effect routing is verified before
  the behavior flip. **The headline feature is not delivered until Step 4
  merges.** Do not merge Step 4 until (a) the active/background snackbar split is
  proven (a background turn must not pop a snackbar on the wrong view) **and**
  (b) the token-map cross-contamination regression test is green.
