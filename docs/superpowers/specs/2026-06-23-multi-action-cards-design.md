# Multi-action Cards — Design Spec (Spec 1 of 2)

- **Date:** 2026-06-23
- **Status:** Approved (brainstorm) — ready for implementation planning
- **Branch:** `feature/snackbar_rework`
- **Author:** Marco Altmann (with Claude Code)
- **Builds on:** `2026-06-22-flow-design.md` (this realizes the per-item view-model named in §7/§10 and begins the "inline confirmation" line deferred in §12).
- **Paired with:** `2026-06-23-tool-permission-decisions-design.md` (Spec 2). Spec 1 ships first and is independently useful; Spec 2 layers the security-sensitive permission work on top of the control this spec introduces.

## 1. Problem & goal

A Flow card can carry exactly one action today: `FlowItem.Action` is a single `FlowAction?` rendered as one accent text-link (`FlowView.xaml` `FlowItemCardTemplate`). That is too narrow for items that present a **choice**. The clearest example already in the code: a fired reminder publishes `Action = ReminderSnoozeAction` (`ReminderBackgroundService.cs:114`) — so the card offers **Snooze** only, even though `ReminderDismissAction` is fully wired in the view-model (`FlowViewModel.cs:137-140`). To dismiss a reminder you must open it. Likewise, the assistant's **Action Cards** (`ActionCardControl.xaml`) hand-roll their own Accept/Decline footer, visually unrelated to Flow.

**Goal:** let a card render **multiple actions as buttons** so the user can *decide in place*, and unify the look of those buttons across both card surfaces (Flow rail/peek and assistant conversation). The first real consumer is reminders (Snooze + Done); the assistant Action Card is restyled onto the same control with **no behavioral change**.

## 2. Decisions captured during brainstorming

| # | Decision | Choice |
|---|----------|--------|
| Action taxonomy | What renders as a link vs a button | **Two categories.** *Navigation* ("pure links" — Open chat/briefing/todo) stay accent **text-links**, unchanged. *Decisions* (resolve in place) render as **buttons**. |
| Sharing | One model or one look | **Share the look, not the model.** The two surfaces have incompatible lifecycles (Flow = fire-and-forget async; Action Card = blocks a streaming tool call). They share only a presentational button row. **No common `ICardDecision` interface.** |
| Scope | Which consumers | **Generic control, grounded in two real consumers:** reminders (Flow) and assistant Action Cards. Not an empty framework. |
| Button look | A/B/C from brainstorm | **Match the assistant Action Card's existing footer** (Decline = `Default`/secondary, Accept = `Primary`, **`Danger`** = red destructive). One proven treatment, used in both places. |
| Reminder decisions | Snooze + what | **Snooze (Default) + Done (Primary).** "Done" (not "Dismiss") to avoid colliding with the card's local-clear ✕. |
| Dedup of "dismiss" | Two dismiss gestures | **Hide the hover-✕ when a card has decisions.** The buttons become the only way to resolve such a card. |
| Reminder persistence | Store the decisions? | **No.** Decisions are a function of the item *type*, so they are **re-derived on load** from `Source == Reminder` + `DedupKey` (the reminderId), exactly as the single action is re-derived today (`FlowPersistenceStore`). No schema change. |
| Action Card scope (this spec) | Behavior change? | **Restyle only.** The blocking `WaitForUserDecisionAsync()` gate is untouched. (The N-option permission feature is Spec 2.) |

## 3. Concept

Every card distinguishes two action categories:

- **Navigation links** — "take me into the item." Rendered as the existing accent text-link. Unchanged everywhere.
- **Decisions** — "resolve the item from here." Rendered as a **button row** via one shared, **strictly presentational** control, `CardDecisionBar`.

`CardDecisionBar` owns **no state and no lifecycle**. It renders buttons bound to commands the host supplies; disabled state comes from each command's `CanExecute` (WPF disables automatically). Each surface keeps its own wiring and lifecycle around the control.

## 4. Shared presentational layer (new — `src/Pia.Wpf/Controls/Cards/`)

- **`DecisionEmphasis`** (enum) — `Primary · Default · Danger`. Pure presentation.
- **`DecisionButton`** — a presentational descriptor: `Label` (string), `Emphasis` (`DecisionEmphasis`), `Command` (`ICommand`). It is a view-side DTO, **not** a domain decision; the host builds the list. (No `Parameter` member — neither consumer needs one: reminder commands close over the reminderId, Accept/Decline take none. Add it only when a consumer requires it.)
- **`CardDecisionBar`** (UserControl) — `ItemsSource` dependency property of `IEnumerable<DecisionButton>`. Renders a horizontal `ItemsControl` of buttons; per-item button style is selected by `Emphasis` (DataTrigger / style selector); `IsEnabled` binds to `Command.CanExecute`. No code-behind state.
- **Button styles** — three styles (`Primary`/`Default`/`Danger`) lifted from the assistant Action Card's current footer buttons into shared resources so both surfaces are pixel-identical. Theme-aware via `DynamicResource` (existing accent / caution tokens).

The control is deliberately dumb. The moment it would need to know about busy/resolved/gate state, that knowledge stays in the host instead — the two hosts disagree about all of it.

## 5. Consumer A — Reminders (Flow panel)

- **Per-item view-model.** Each card is presented through a `FlowItemViewModel` wrapping its `FlowItem` (this realizes the VM named in the Flow design §7/§10; today the template binds `FlowItem` directly and routes commands to `FlowViewModel` by `RelativeSource`). The VM passes through display fields (`Title`/`Body`/`Severity`/`CreatedAt`/`IsRead`/nav `Action`) and adds:
  - `Decisions : IReadOnlyList<DecisionButton>` — **derived**, not stored.
  - `HasDecisions : bool` — drives hiding the ✕.
  - `IsBusy : bool` — gates re-entrancy and disables the buttons mid-call (via `CanExecute`).
  - `FlowViewModel.Items` becomes `ObservableCollection<FlowItemViewModel>`. **The store→VM sync must become an Id-keyed reconcile, not the current clear-and-re-add `Rebuild()`** (`FlowViewModel.cs:185-193`). The reminder poller fires `Changed` every ~30s; if a store event during an in-flight `SnoozeAsync`/`DismissAsync` tore down and recreated the wrapper, `IsBusy` and the re-entrancy guard would be lost mid-call. Reconcile in place: update/keep existing wrappers by `FlowItem.Id`, add new, remove gone — **inserting each new wrapper at its snapshot-ordered position to preserve the newest-first ordering** that today's clear+re-add gets for free. This — not a bare type swap — is the real size of the refactor.
- **Reminder derivation.** When `Source == FlowSource.Reminder`, `Decisions = [ Snooze (Default), Done (Primary) ]`. The reminderId comes from `DedupKey` (already `reminder.Id.ToString()`). Commands reuse the existing logic at `FlowViewModel.cs:133-140`:
  - **Snooze** → `IReminderService.SnoozeAsync(reminderId, 10 min)` then dismiss the card.
  - **Done** → `IReminderService.DismissAsync(reminderId)` then dismiss the card.
- **Async behavior (banked — none exists today).** While a decision command runs, `IsBusy = true` disables both buttons. On **success** the card is dismissed (as the snooze/dismiss paths already do). On **failure** the card **stays**, buttons re-enable, and an error surfaces (existing snackbar/Flow error path). No optimistic removal.
- **`ReminderBackgroundService.cs:114` change.** Stop setting `Action = ReminderSnoozeAction(...)`. Reminder cards carry decisions only; the nav `Action` is `null`. The durable invariant still holds (`Durable ⇒ Action is null or re-derivable`), and decisions re-derive on reload from `Source == Reminder`. No persistence/schema change. ("Snooze"/"Done" labels: reuse `Flow_Action_Snooze`; add `Flow_Action_Done`.)
- **Hide the ✕ when decisions exist.** Template DataTrigger on `HasDecisions` collapses the hover-dismiss button — eliminating the "local clear ✕" vs "server Done" ambiguity.
- **Peek surface ("outside the panel").** The peek reuses `FlowItemCardTemplate`, but `FlowView.xaml.cs` (`OnItemArrived`, ~line 72/88) currently sets `PeekItems.ItemsSource = new[] { item }` with a **raw `FlowItem`**, and the `ItemArrived` event (`FlowViewModel.cs:59/175`) carries a raw `FlowItem`. Since the template will bind `FlowItemViewModel`, the peek must receive a **wrapped VM** too. **`ItemArrived` should carry the rail's existing `FlowItemViewModel` wrapper** (one instance shared between peek and rail) — not a freshly-wrapped copy, which would give the peek a separate `IsBusy` that the rail can't see. Otherwise the peek silently shows no buttons (raw item has no `Decisions`) or throws a binding mismatch. With the wrapper, decision buttons render in the peek; the peek already pauses its auto-dismiss timer on hover, so they remain actionable. Reminders are `Persistent`/`ActionRequired` and primarily live in the rail anyway.

## 6. Consumer B — Assistant Action Card (restyle only)

- Replace the hand-rolled Accept/Decline footer in `ActionCardControl.xaml` with `CardDecisionBar`.
- `ActionCardInfo` exposes `Decisions = [ Decline (Default), Accept (Primary, or Danger when IsDestructive) ]`, bound to the **existing** `AcceptCommand`/`DeclineCommand` (`ActionCardInfo.cs:66-82`).
- **Untouched:** the `TaskCompletionSource<bool>` gate (`WaitForUserDecisionAsync()`), `Cancel()` (`TrySetCanceled`), the resolved-state display (checkmark / status text via `ResolvedStatusText`), expand/collapse, and the run loop at `ChatSession.cs:435-484`. This stage changes **rendering only** and is the safe checkpoint before Spec 2 alters the gate.

## 7. What stays unchanged in Spec 1

The tool-execution gate and its `bool` contract, the chat run loop, the SQLite schema (no new columns), and app settings (no new keys). Spec 1 is pure UI + a Flow view-model refactor.

## 8. Unit decomposition

- **Controls** — `DecisionEmphasis`, `DecisionButton`, `CardDecisionBar` (+ shared button styles).
- **ViewModels** — new `FlowItemViewModel`; `FlowViewModel.Items` retyped to the wrapper with an **Id-keyed reconcile** replacing `Rebuild()`, reminder decision derivation + `IsBusy` orchestration; the `ItemArrived` event payload changes to carry the rail's existing `FlowItemViewModel` wrapper (shared instance, not a re-wrap).
- **Views** — `FlowView.xaml` `FlowItemCardTemplate` (bind VM, drop single-link for reminders → `CardDecisionBar`, DataTrigger to hide ✕); **`FlowView.xaml.cs`** (`OnItemArrived` peek wrapping); `ActionCardControl.xaml` (footer → `CardDecisionBar`).
- **Services** — `ReminderBackgroundService` (drop the single snooze action).
- **Resources** — `Flow_Action_Done` added to **all three** resx (`ViewStrings.resx`, `.de.resx`, `.fr.resx`), matching the existing `Flow_Action_Snooze`; shared button styles. (An unused `Flow_Action_Dismiss` already exists — left for "Done" deliberately; the planner may retire it.)

## 9. Testing

- `CardDecisionBar` renders N buttons with correct per-`Emphasis` style; a button disables when its command's `CanExecute` is false.
- `FlowItemViewModel` derives `[Snooze, Done]` for `Source == Reminder` and an empty list otherwise; `HasDecisions` correct.
- Snooze → `SnoozeAsync`; Done → `DismissAsync`; `IsBusy` prevents double-invoke; a thrown service call keeps the card and re-enables buttons.
- **Concurrency:** a store `Changed` event arriving mid-decision (Id-keyed reconcile) preserves the wrapper's `IsBusy` and the in-flight command — the card is not torn down and recreated.
- Durable reminder reload re-derives decisions (Action null, `Source == Reminder`, reminderId from `DedupKey`).
- ✕ hidden when `HasDecisions`.
- Action Card restyle: existing gate/state-machine test stays green (Accept/Decline still resolve the `bool` TCS); `IsDestructive` → Accept rendered as `Danger`.

## 10. Edge cases

- **Reminder without a parseable `DedupKey`** — defensive: no decisions, fall back to opening (should not occur; reminders always carry the id).
- **Localization** — all labels via existing resources; source-provided titles/bodies pass through (privacy rule unchanged: no sensitive content logged — CLAUDE.md).
- **Other Flow sources** — unaffected; they keep their single nav link and the ✕. Decisions are opt-in per source.

## 11. Deferred → Spec 2

N-option decisions (Allow once / Always allow / Decline), the gate `bool → choice` change, always-allow persistence, the pre-gate bypass, and the revocation UI. All of it consumes `CardDecisionBar` from this spec and is gated behind its own `/security-review`.

## Appendix — key integration points (from codebase recon)

- Flow card template & ✕: `src/Pia.Wpf/Controls/Flow/FlowView.xaml` (`FlowItemCardTemplate`, action-link + dismiss button).
- Flow VM & item actions: `src/Pia.Wpf/ViewModels/Flow/FlowViewModel.cs:107-151` (`ExecuteItemAction`, `DismissItem`), `Items` collection.
- Reminder publish: `src/Pia.Wpf/Services/ReminderBackgroundService.cs:107-116`.
- Flow models: `src/Pia.Wpf/Models/Flow/FlowItem.cs`, `FlowAction.cs` (`ReminderSnooze`/`ReminderDismiss`).
- Flow persistence (action re-derivation): `src/Pia.Wpf/Services/Flow/FlowPersistenceStore.cs` (`ToAction(kind, entityId, label)`).
- Assistant Action Card: `src/Pia.Wpf/Controls/ActionCardControl.xaml(.cs)`, `src/Pia.Wpf/Models/ActionCardInfo.cs` (Accept/Decline/Cancel commands, `IsDestructive`, resolved state).
- Reminder service: `IReminderService.SnoozeAsync` / `DismissAsync`.
- Theming: `Resources/Theme/PiaTokens.*`, `Resources/Theme/PiaStyles.xaml`, accent/caution tokens.
