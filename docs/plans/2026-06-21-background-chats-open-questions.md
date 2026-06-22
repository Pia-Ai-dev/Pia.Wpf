# Background chats — open questions & follow-ups

**Self-contained handoff for a later session.** This records every unresolved
decision, chosen default, and known limitation from the *first shot* of the
"background-running assistant chats" feature. You should be able to act on this
without any prior conversation context.

## ✅ Resolved (2026-06-22 session)

All open product/UX questions (A) and code-default confirmations (B) were worked
through with the product owner. **Two decisions went *against* the original
recommendation** (A2, B4) — recorded here as *decided*, not as recommended.

| # | Decision | vs. recommendation |
|---|---|---|
| **A1** | Keep the default: notify background `Error` + `WaitingForTool` + `Completed`; `Running`/`Idle` suppressed. | matches |
| **A2** | **Keep current (a): group-by-state shows _all_ chats, action-needed first.** The large `Idle` bucket is accepted; revisit "live-only + collapsed Saved" later only if it feels empty. | **against** (rec was (b)) |
| **A3** | Keep current: Clear/New cancels the active in-flight turn; other live sessions keep running. | matches |
| **A4** | Keep current: a user-cancelled turn maps to `Idle` (no distinct `Cancelled` state). | matches |
| **A5** | Keep current: `Completed` clears to `Idle` only on true activation, not on history-preview. | matches |
| **A6** | Keep current: background turns suppress follow-up suggestions and TTS. | matches |
| **A7** | **Add the reaper — IMPLEMENTED in this change.** Keep the ~8 most-recently-active sessions; reap non-active `Idle`/`Error` ones beyond the window. | matches (now built) |
| **B1** | Keep the `AsyncLocal` token-map isolation. ⚠️ **Still owes the manual multi-turn smoke-test in the running app.** | matches |
| **B2** | Accept the `Pia.ViewModels.Models` placement (the `Pia.Services` plan variant is not buildable against NetArchTest). | matches |
| **B3** | Keep current: a background failure never touches the foreground input box. | matches |
| **B4** | **Accept the machine de/fr translations** for the 18 new keys (no human-review gate). | **against** (rec was human review) |

**`C1` and `C4` are now also implemented this session** (single-owner CTS for the
setup-window cancel; non-active-scoped persist for the reaped-setup-failure toast —
see their IMPLEMENTED notes below). `C2` (by-design deferrals) and `C3` (cosmetic
cleanup) remain open and need no decision. New tree state: WPF build
**0 warnings / 0 errors** (one pre-existing CS8629 nullability warning in
`StartTurnAsync` was cleaned up incidentally); feature suites grew to
`ChatSessionManagerTests` 18 + `ChatSessionStateMachineTests` 6 +
`TokenMapIsolationTests` 4 = 28; full unit suite **508 / 508** green (excludes the
env-dependent `Integration.Providers.*`).

## What shipped (first shot)

Feature: assistant chats keep **running in the background** when the user
switches between chats; each chat carries a **state** (Idle / Running /
WaitingForTool / Completed / Error) shown as an indicator; chats can be
**grouped by state**; a **background** (non-active) chat that changes state
fires a **notification with a deep link** that re-activates it inside the
**single** assistant window.

- **Branch:** `feature/background-chats` (cut from `feature/personas`).
- **Design/plan:** `docs/plans/2026-06-21-background-chats.md` (read this first
  for the full architecture, the four pinned decisions, and the step sequence).
- **Architecture in one paragraph:** the run loop was extracted out of
  `AssistantViewModel` into a per-chat `ChatSession` (owns its own
  `ObservableCollection<AssistantMessage> Messages`, a per-turn
  `CancellationTokenSource`, an observable `ChatState`, and `RunTurnAsync`). A
  scoped `ChatSessionManager` holds live sessions keyed by chat id, designates
  `ActiveSession`, runs turns detached (UI-thread-affine `SafeFireAndForget`,
  **no** `Task.Run`), persists via `IAssistantChatService`, and routes
  background-only state transitions to `IBackgroundChatNotifier`.
  `AssistantViewModel` is now a thin view: `Messages` is a settable
  `[ObservableProperty]` re-pointed on switch; send/clear/cancel/resume delegate
  to the manager. Token-map (PII) isolation across concurrent turns uses an
  `AsyncLocal` ambient map (`TokenMapAmbient` + `TokenizingAiClientService`).
- **Status at handoff:** `dotnet build` green (0 warnings/0 errors); 20/20 new
  unit tests pass (`ChatSessionStateMachineTests`, `ChatSessionManagerTests`,
  `TokenMapIsolationTests`). Full suite 525 pass / 18 fail / 13 skip — the 18
  failures are all `Integration.Providers.*` (OpenRouter 401, vLLM 15 s timeout)
  and are environment-dependent (no API keys / no local LLM servers); they were
  **not** confirmed to fail identically on `feature/personas`, so verify that if
  it matters.

## ⚠️ Must be smoke-tested in the running app

Background continuation and the notification deep-link are verified by **code
inspection + unit tests on the mechanism only** — there is **no** automated
end-to-end runtime test, because UI automation (winwright) is not used and the
run loop needs a live UI dispatcher. The non-cancellation mechanism is the
live-attach branch in `ChatSessionManager.ActivateAsync` (≈ line 180–190):
`TryGetLive(chatId)` → `SetActive(live)` and return, with **no** `.Cancel()` and
**no** reload. `SetActive` (≈ line 168) also never cancels.

**Manual test:** start a turn in chat A → switch to chat B → confirm A keeps
streaming → confirm A pings you (toast) when it finishes or needs a tool →
click the toast → confirm it returns to A *in the same window* and reveals any
pending action card.

---

## A. Genuine product / UX decisions — STILL OPEN

### A1. Notification gating — which background transitions fire a toast?
- **Current default (in code):** notify on non-active `Error` + `WaitingForTool`
  + `Completed`; `Running`/`Idle` suppressed (Running would spam). Routing gate
  is in `ChatSessionManager.OnSessionStateChanged` → `IBackgroundChatNotifier`
  (`BackgroundChatNotificationSurface`).
- **Options:** (a) keep default; (b) action-needed only (`WaitingForTool` +
  `Error`), since `Completed` is already visible via the badge; (c) make it a
  user setting.
- **Recommendation:** keep default now; add a setting later.

### A2. Group-by-state scope — show all chats or only live sessions?
- **Current default:** "Group by state" shows *all* chats. Persisted-but-not-live
  chats map to `Idle`, so the common case is a huge `Idle` bucket with
  action-needed states ordered first. Lives in `AssistantHistoryViewModel`
  (`ChatGroupMode` toggle + the state branch of `RebuildGroups`).
- **Options:** (a) show all, action-needed first (current); (b) show only live
  sessions under their state + a separate collapsed "Saved chats" bucket;
  (c) hide the state-grouping option entirely when there are no live sessions.
- **Recommendation:** (b) — far higher signal; the all-Idle view is low-value.

### A3. Clear / New Chat during a running turn — cancel or background it?
- **Current default:** clearing/new cancels the current in-flight turn (exact
  parity with today's behavior); it does **not** cancel *other* live sessions.
- **Options:** (a) cancel on clear (current); (b) background the running turn on
  clear (it keeps running while a fresh chat opens) — arguably more in the
  spirit of the feature.
- **Recommendation:** keep (a) for the first shot; revisit with A1/notification
  UX.

### A4. Cancelled vs Idle — distinct indicator?
- **Current behavior:** a user-cancelled turn (`OperationCanceledException`)
  maps to `Idle` (matches today's "Cancelled" snackbar; no distinct state). A
  user who cancels chat A, leaves, and returns sees `Idle` with no cue that A
  was cancelled vs simply idle.
- **Options:** (a) leave as Idle; (b) add a `Cancelled` state / transient cue.
- **Recommendation:** leave as Idle unless users report confusion.

### A5. Completed badge — when does it clear?
- **Current behavior:** `Completed` clears to `Idle` **only on true activation**
  (`ChatSessionManager.SetActive`, ≈ line 173–175). Previewing the chat in the
  history inspector (`AssistantHistoryViewModel.LoadSelectedChatDetailAsync`)
  shows the transcript **without** activating, so the badge stays `Completed`.
- **Options:** (a) clear only on activation (current; "preview ≠ acknowledge");
  (b) also clear when the inspector loads a `Completed` live session.
- **Recommendation:** keep (a).

### A6. Background turns — follow-up suggestions & TTS?
- **Current behavior:** suppressed for background (off-screen) turns; they are
  active-view-only concerns. A backgrounded completion neither speaks nor
  fetches follow-up suggestions.
- **Options:** (a) keep suppressed (generate follow-ups lazily on activate, or
  skip); (b) always generate follow-ups so they're ready on activate; (c) make
  it a setting.
- **Recommendation:** keep (a).

### A7. Live-session retention / reaper (sizing decision)
- **Current behavior:** v1 **never** evicts — `ChatSessionManager` retains every
  `ChatSession` created during the app run. Bounded by user behavior, not capped.
  Each session holds an `AssistantMessage` collection (possibly with image
  attachments), so heavy multi-chat sessions grow memory unbounded.
- **Options:** (a) no reaper (current); (b) keep last N live sessions, reap
  older non-active Idle/Error ones; (c) time-based eviction of non-active
  sessions older than N minutes.
- **Recommendation:** add (b) with N≈8 before this is considered shippable.
- **✅ DECISION (2026-06-22) — IMPLEMENTED:** option (b), `MaxRetainedSessions = 8`.
  The reaper runs at the end of `ChatSessionManager.SetActive` (the sole
  session-accumulation point — both `GetOrCreateActiveForNewChat` and
  `ActivateAsync` route through it; a long-lived single chat never grows the set).
  It keeps the 8 most-recently-active sessions (LRU via a per-session
  `LastActivatedSequence` stamp, bumped in `SetActive`) and retires only
  **non-active `Idle`/`Error`** sessions beyond the window. In-flight
  (`Running`/`WaitingForTool`) and **unread `Completed`** sessions are never reaped,
  so the live count is a *soft* cap that can briefly exceed 8. The
  previously-active session is protected by its second-highest stamp (holds for any
  N ≥ 2 — see the in-code note). Retired sessions are unsubscribed + disposed and
  removed from both `_sessions`/`_allSessions`; their finished turns are already
  persisted, so a later `ActivateAsync` re-hydrates from the store (no data loss).
  Reaping logs id/state/count only (CLAUDE.md privacy). Covered by
  `ChatSessionManagerTests`: `Reaper_OverCap_DropsOldestIdle_KeepsRecentAndActive`,
  `Reaper_NeverDropsUnreadCompletedSession`,
  `Reaper_ReapedIdleSession_RehydratesFromStoreOnActivate`.

---

## B. Defaults chosen in code — CONFIRM or OVERRIDE

### B1. Token-map (PII) isolation under true concurrency  *(load-bearing)*
- **Chosen (in code):** `AsyncLocal` ambient "current token map" per session
  (`TokenMapAmbient`), which `TokenizingAiClientService` prefers for an in-flight
  turn — so concurrent background turns don't corrupt each other's
  detokenization. Preserves true background concurrency for tokenization-enabled
  (privacy) users. There is a `TokenMapIsolationTests` regression test.
- **Alternatives:** (a) serialize whole tokenizing turns behind one
  `SemaphoreSlim` (simpler, but only one tokenizing turn runs at a time —
  silently defeats the concurrency headline); (b) disable background
  continuation entirely when `Privacy.TokenizationEnabled`.
- **Recommendation:** keep the `AsyncLocal` approach; confirm it behaves under a
  real multi-turn manual test.

### B2. Where the session types live (architecture-test deviation)
- **Chosen (in code):** all session types are in **`Pia.ViewModels.Models`**
  (`ChatSession`, `ChatSessionManager`, `IChatSessionManager`,
  `ChatSessionEvents`). The written plan said `Pia.Services` /
  `Pia.Services.Interfaces`, but that is **not buildable** against the repo's
  NetArchTest suite: a `Manager` suffix is rejected in `Pia.Services`, and
  "Services must not depend on ViewModels" forbids a service referencing
  `ChatSession`.
- **Alternative:** whitelist `ChatSessionManager` in `LayerDependencyTests` /
  `NamingConventionTests` and keep it in `Pia.Services` — **weakens a real
  invariant; not recommended.**
- **Recommendation:** accept the `Pia.ViewModels.Models` placement.

### B3. Vision-rejection input restore for background failures
- **Chosen (in code):** a background turn that fails (timeout / truncated /
  vision-rejected / generic) **never** touches the foreground chat's input box.
  The vision-rejection path's `InputText` restore is applied **only** when the
  failing session is the `ActiveSession` (routed via a `RunFailed` payload).
  `Messages.Remove` stays session-local.
- **Decision:** confirm this is the desired behavior (the rejected prompt for a
  background chat is recoverable only by activating that chat). Alt: stash the
  prompt on the session and offer "restore" on activation.
- **Recommendation:** keep current; consider the stash+restore later.

### B4. de / fr translations
- **Chosen (in code):** the 18 new resx keys were translated to German and
  French **inline by the model** (Pia convention forbids English-only keys),
  across `MessageStrings.resx` / `.de.resx` / `.fr.resx`.
- **Decision:** accept the machine translations, or route the new keys through
  translation review.
- **Recommendation:** route through a human translator before release.

---

## C. Known limitations / deferrals (FYI — no decision needed to proceed)

### C1. Cancel lost during the setup-await window  *(deferred bug)*
A `Cancel` click during `ChatSessionManager.StartTurnAsync`'s setup awaits
(settings/persona/provider resolution, ≈ lines 264–322, before `RunTurnAsync`
creates the CTS) is silently lost: the per-turn CTS doesn't exist yet and the
run loop is dispatched with `CancellationToken.None`. **After** the related
major fix (setup now wrapped in try/catch so a setup *exception* can't wedge the
session in `Running`), the worst case is a single lost cancel-click in a brief
window with the turn still completing — **no hang/wedge**. A clean fix needs
single-owner CTS surgery (create the CTS before the setup awaits and have
`RunTurnAsync` reuse rather than recreate it — currently `ChatSession.cs` ≈ line
153 unconditionally reassigns `Cts`, which would orphan a pre-created one).
Below the bar for the first shot; fix when revisiting cancellation.

> **✅ IMPLEMENTED (2026-06-22):** single-owner CTS surgery done. New
> `ChatSession.BeginTurn()` creates the per-turn CTS up front; the manager calls it
> **before** the setup awaits, and `RunTurnAsync` reuses it (`Cts ??=`, falling back
> to create-link only for direct/test callers). The setup APIs take no
> `CancellationToken` (verified), so the awaits themselves can't be interrupted —
> instead `RunTurnAsync` calls `token.ThrowIfCancellationRequested()` at the top of
> its `try`, routing a setup-window cancel into the existing
> `OperationCanceledException` catch → settles **Idle** with the Cancelled snackbar,
> **without** ever calling the AI client. The empty-response branch in the `finally`
> is guarded with `!token.IsCancellationRequested` so a cancelled turn no longer
> double-reports an "empty response" over the Cancelled snackbar. Setup-failure paths
> release the pre-created CTS via `DisposeCts()`. Test:
> `ChatSessionStateMachineTests.Cancel_DuringSetupWindow_AbortsTurn_NoAiCall_NoEmptyResponse`.

### C2. Deferred by design
- `Queued` state + a real per-session turn queue (v1 runs one turn per session;
  `CanExecuteSendMessage` blocks re-send while `Running`).
- Group-by-state **inside the chip flyout** (only the full history view, title
  chip, and quick-switcher carry state today; the flyout's
  `ChatChipItemViewModel` is a plain record with no live state).
- Syncing `ChatState` to the cloud (runtime-only by design).
- Highlighting *which* message awaits confirmation (needs an additive flag on
  `AssistantMessage`).

### C3. Cosmetic / cleanup
- Several injected fields on `AssistantViewModel` are now used only by the voice
  path after the extraction (e.g. `_aiClientService`, `_pluginService`,
  `_tokenMapService`, `_promptComposer`). They compile and pass tests but are
  dead-ish weight — clean them in a `simplify` pass.
- `Error` indicator reuses `WarnSoftBrush` fill + `PiaDangerBrush` foreground
  (no dedicated `DangerSoftBrush` token exists in the theme dictionaries). Add a
  proper error-soft token if a distinct look is wanted.

### C4. Reaped setup-failure Error sessions — dead toast link  *(reaper edge, deferred)*
The two setup-failure paths in `ChatSessionManager.StartTurnAsync` (no provider
configured; setup exception) set `Error` and fire the background notifier but
`return` **before** `RunTurnAsync`, so they never raise `TurnCompleted` and are
therefore **never persisted** — yet the session is keyed in `_sessions` with
content. If the A7 reaper later drops such a session (it is a non-active `Error`),
a click on its earlier Error toast resolves `ActivateAsync` → `TryGetLive` null →
store `GetAsync` null → **returns `null`**: a dead toast link, **not** a crash.
The caller (`AssistantViewModel.ResumeChatAsync:474`) ignores the return value,
and `ActivateAsync_MissingChat_ReturnsNull` is already a tested contract, so the
null is tolerated. Narrow case (a backgrounded first-turn setup failure that then
ages out of an 8-session window) and acceptable to defer; the clean fix is to
persist on the setup-failure paths so the re-hydration safety-net applies
universally.

> **✅ IMPLEMENTED (2026-06-22):** both setup-failure paths now call
> `FinalizeFailedSetupAsync(session)`, which releases the per-turn CTS (`DisposeCts`,
> shared with C1) and persists the errored chat **only when the session is non-active**
> (`!IsSessionActive`). The notifier gate already only fires for non-active sessions, so
> that is the *only* case where a dead toast link could occur — scoping the persist there
> fixes it precisely while leaving the common **foreground** no-provider case unpersisted
> (no junk history for an unconfigured user, exactly as before). LLM auto-title is
> suppressed for the errored chat (`AutoTitleApplied = true`); `DeriveChatTitle` still
> derives a title from the user message. `FinalizeFailedSetupAsync` also wraps the
> awaited persist in try/catch (it runs synchronously under the send command, unlike
> the normal fire-and-forget persist, so it must not rethrow on a failure path). Tests:
> `StartTurnAsync_NoProvider_BackgroundSession_PersistsForRecovery`,
> `StartTurnAsync_NoProvider_ActiveSession_DoesNotPersist`, and
> `StartTurnAsync_SetupThrows_BackgroundSession_FailsGracefully`.

---

## How to resume

1. Read `docs/plans/2026-06-21-background-chats.md` for the architecture and the
   four pinned decisions.
2. Do the manual smoke-test above before building further — it's the only
   unverified part of the headline.
3. ✅ All **A**/**B** decisions are resolved (see **Resolved** at top); **A7**'s
   reaper is implemented + unit-tested. No product decisions remain open.
4. The remaining open *work* is verification, not decisions: the **headline
   smoke-test** (step 2) and the **B1** token-map multi-turn test in the running
   app — both need a live UI and are manual (winwright is not used here).
5. ✅ `C1` (lost cancel in the setup-await window) and `C4` (reaped setup-failure
   Error → dead toast link) are now both **implemented** (see their IMPLEMENTED
   notes above). `C2` (by-design deferrals) and `C3` (cosmetic cleanup) remain the
   only open follow-ups.
