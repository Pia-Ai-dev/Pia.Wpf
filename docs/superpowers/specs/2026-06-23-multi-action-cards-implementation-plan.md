# Multi-action Cards — Implementation Plan (Spec 1)

> **For agentic workers:** REQUIRED: use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a card render multiple actions as buttons via one shared, presentational `CardDecisionBar`; ship it on Flow reminder cards (Snooze + Done) and restyle the assistant Action Card footer onto it (no behavior change).

**Architecture:** A dumb `CardDecisionBar` UserControl renders an `IEnumerable<DecisionButton>` with per-`Emphasis` styling; disabled state via `Command.CanExecute`. Flow gains a per-item `FlowItemViewModel` (derives reminder decisions, owns `IsBusy`); `FlowViewModel.Items` becomes an Id-keyed reconcile of wrappers. `ActionCardInfo` exposes a (still binary) `Decisions` list.

**Tech Stack:** WPF / net10.0-windows, CommunityToolkit.Mvvm, WPF-UI (`ui:Button`), xunit.v3 + NSubstitute + plain `Assert`.

Derived from `2026-06-23-multi-action-cards-design.md` + ground-truthed recon. Branch: `feature/snackbar_rework`.

## Key recon findings (ground-truthed)

1. **FlowViewModel** (`ViewModels/Flow/FlowViewModel.cs`): ctor injects `IFlowService, IWindowManagerService, IReminderService, ISettingsService, INavigationService, ILogger` (32-53), captures `SynchronizationContext` (46). `Items` = `ObservableCollection<FlowItem>` (56). `Rebuild()` (185-193) is **clear + re-add** from `_flow.Snapshot`, called on `_flow.Changed` via `Post(Rebuild)` (173). `ItemArrived` is `EventHandler<FlowItem>` (59), raised `Post(() => ItemArrived?.Invoke(this, item))` (175). `ExecuteItemAction` switches on `FlowAction` (108-145); reminder cases at 133-140 (`SnoozeAsync(.,10min)` + `_flow.Dismiss`; `DismissAsync` + `_flow.Dismiss`). DI `AddScoped` (Bootstrapper.cs:362).
2. **FlowView.xaml** `FlowItemCardTemplate` (100-219): `ActionLink` `ui:Button` (158-171) `Appearance=Transparent`, `Content={Binding Action.Label}`, `Command={Binding DataContext.ExecuteItemActionCommand, RelativeSource ItemsControl}`, `CommandParameter={Binding}` (whole item). `DismissButton` (175-188) `Opacity=0`, revealed on `Card.IsMouseOver` (193-195). `PeekItems` ItemsControl (433-435) uses the same `FlowItemCardTemplate`.
3. **FlowView.xaml.cs** `OnItemArrived` (72-102): `PeekItems.ItemsSource = new[] { item }` (88), type `FlowItem[]`; cleared to `null` in `OnPeekCompleted` (110).
4. **ActionCardControl.xaml** footer (213-238): right-aligned `StackPanel`; Decline `Appearance=Secondary` `Command=DeclineCommand` `{loc:Str ActionCard_Decline}`; Accept `Appearance=Primary` (DataTrigger → `Caution` when `IsDestructive`) `Command=AcceptCommand` `{loc:Str ActionCard_Accept}`. The whole pending block collapses on `IsResolved` (27-31).
5. **ReminderBackgroundService.cs** `PublishFlowItem` (101-122): `Action = new ReminderSnoozeAction(reminder.Id, _localizationService["Flow_Action_Snooze"])` (114) — label baked at publish.
6. **Localization**: `Flow_Action_Snooze` + `Flow_Action_Dismiss` exist in all three `Resources/Strings/ViewStrings.resx` / `.de.resx` / `.fr.resx`. XAML: `{loc:Str Key}`; C#: `_localizationService["Key"]`. **`Flow_Action_Done` must be added to all three.**
7. **Tests**: xunit.v3, plain `Assert.*`, factory `Create()` pattern, `NullLogger<T>.Instance`, NSubstitute + hand fakes (e.g. `tests/.../Services/Flow/FlowServiceTests.cs`). `NamingConventionTests` enforces suffixes only in the **ViewModels** and **Services** namespaces — `FlowItemViewModel` is fine; `CardDecisionBar`/`DecisionButton`/`DecisionEmphasis` live in **Controls** (unaffected). Controls need no DI.

## Design decisions (refine the spec)

- **`DecisionEmphasis` → WPF-UI appearance** (no new tokens): `Primary→Primary`, `Default→Secondary`, `Danger→Caution` — exactly the ActionCard footer mapping (recon #4).
- **`CardDecisionBar`** = `Controls/Cards/CardDecisionBar.xaml(.cs)`, a UserControl with one DP `ItemsSource : IEnumerable<DecisionButton>`. Body = `ItemsControl` (horizontal `StackPanel`, right-aligned) whose item template is a `ui:Button` with `Content={Binding Label}`, `Command={Binding Command}`, and `Appearance` chosen by `Emphasis` (DataTriggers). No code-behind state.
- **`DecisionButton`** = `Controls/Cards/DecisionButton.cs`, plain class: `string Label`, `DecisionEmphasis Emphasis`, `ICommand Command`. (No `Parameter` — unused; design §4.)
- **`FlowItemViewModel`** = `ViewModels/Flow/FlowItemViewModel.cs`, wraps a `FlowItem`. Passthrough (raise `PropertyChanged` on rewrap): `Title, Body, Severity, CreatedAt, IsRead, Action`. New: `Decisions : IReadOnlyList<DecisionButton>`, `HasDecisions`, `IsBusy`. It **owns the per-item action logic** (moved out of `FlowViewModel.ExecuteItemAction`): `ExecuteActionCommand` (nav links), `DismissCommand` (the ✕), and async `SnoozeCommand`/`DoneCommand` (reminder). The async commands set `IsBusy`, `CanExecute = !IsBusy`, dismiss on success, keep + reset on failure. Ctor takes the deps it needs (`IFlowService, IReminderService, IWindowManagerService, INavigationService, ILogger`) supplied by `FlowViewModel`, stored in **`readonly` fields** (`MvvmPatternTests.ViewModel_InjectedFields_MustBeReadonly`). Note: `ActionCardInfo.Decisions`/`FlowItemViewModel.Decisions` reference `DecisionButton` (Models/ViewModels → Controls) — a deliberate, test-clean direction (`DecisionButton` is a view-side DTO holding only `string`/enum/`ICommand`; no layer test forbids it).
- **Reminder derivation**: when `Source == FlowSource.Reminder`, `Decisions = [ new(Snooze, Default, SnoozeCommand), new(Done, Primary, DoneCommand) ]` (labels `Flow_Action_Snooze` / `Flow_Action_Done`); reminderId from `DedupKey` (`Guid.Parse`). Other sources → empty.
- **`FlowViewModel.Items` → `ObservableCollection<FlowItemViewModel>`**; `Rebuild()` becomes **`Reconcile()`**: index existing wrappers by `FlowItem.Id`; walk `_flow.Snapshot` in order — reuse the wrapper (rebind its `FlowItem`, raise passthrough `PropertyChanged`) or create one; move/insert to the snapshot index (preserve newest-first); remove wrappers whose Id is gone. **Never** clear+re-add — that would drop `IsBusy` of an in-flight reminder decision mid-call.
- **`ItemArrived` payload → `FlowItemViewModel`**: `OnFlowItemArrived` reconciles first (so the wrapper exists), looks up the wrapper by Id, and raises `ItemArrived(wrapper)`. `FlowView.xaml.cs` sets `PeekItems.ItemsSource = new[] { vm }` (the *same* instance as in the rail, so `IsBusy` is shared).
- **`ActionCardInfo.Decisions`** (Plan 1, binary): `[ new(Decline, Default, DeclineCommand), new(Accept, IsDestructive ? Danger : Primary, AcceptCommand) ]`. `ActionCardControl.xaml` footer (213-238) is replaced by `<cards:CardDecisionBar ItemsSource="{Binding Decisions}" />`; the `IsResolved` collapse trigger and resolved panel stay. Gate/TCS untouched.

## File plan

**Create**
- `Controls/Cards/DecisionEmphasis.cs` — enum `Primary, Default, Danger`.
- `Controls/Cards/DecisionButton.cs` — `Label`/`Emphasis`/`Command`.
- `Controls/Cards/CardDecisionBar.xaml(.cs)` — the presentational bar.
- `ViewModels/Flow/FlowItemViewModel.cs` — wrapper + decisions + IsBusy + commands.
- `tests/Pia.Wpf.Tests/ViewModels/Flow/FlowItemViewModelTests.cs`.

**Modify**
- `ViewModels/Flow/FlowViewModel.cs` — `Items` retype; `Rebuild`→`Reconcile`; construct wrappers; `ItemArrived` payload→wrapper; remove the reminder/nav branches now living in the wrapper (keep panel commands).
- `Controls/Flow/FlowView.xaml` — `FlowItemCardTemplate`: keep `ActionLink` (nav) bound to `Action`; add `<cards:CardDecisionBar ItemsSource="{Binding Decisions}" Visibility="{Binding HasDecisions,...}"/>`; bind `DismissButton` visibility off `HasDecisions` (collapse when decisions exist); rebind command targets to the item VM (commands now on the wrapper, not the ItemsControl DataContext).
- `Controls/Flow/FlowView.xaml.cs` — `OnItemArrived(FlowItemViewModel)`; `PeekItems.ItemsSource = new[] { vm }`.
- `Services/ReminderBackgroundService.cs` — drop `Action = ReminderSnoozeAction(...)` (114); leave the rest (decisions are derived).
- `Models/ActionCardInfo.cs` — add `Decisions` (binary) computed from `IsDestructive` + existing commands.
- `Controls/ActionCardControl.xaml` — footer → `CardDecisionBar`.
- `Resources/Strings/ViewStrings{,.de,.fr}.resx` — add `Flow_Action_Done`.

## Task sequence

### Chunk 1 — Shared control
- [ ] Add `DecisionEmphasis`, `DecisionButton`.
- [ ] Build `CardDecisionBar.xaml(.cs)` (ItemsSource DP; emphasis→appearance DataTriggers). Build solution: `dotnet build`.

### Chunk 2 — FlowItemViewModel (TDD)
- [ ] **Test first** (`FlowItemViewModelTests`): reminder item → `Decisions` = [Snooze(Default), Done(Primary)] with localized labels; non-reminder → empty; `HasDecisions` correct. Run → fails.
- [ ] Implement wrapper + derivation. Run → passes.
- [ ] **Test**: `SnoozeCommand` calls `IReminderService.SnoozeAsync(id, 10min)` then `IFlowService.Dismiss(id)`; `DoneCommand` calls `DismissAsync` then `Dismiss`. (NSubstitute `.Received()`.)
- [ ] **Test**: `IsBusy` true during an awaited call, false after; while busy the command `CanExecute` is false (no double-invoke).
- [ ] **Test**: when `SnoozeAsync` throws, the item is **not** dismissed, `IsBusy` resets, error is logged. Implement; run → passes. Commit.

### Chunk 3 — FlowViewModel reconcile
- [ ] **Test**: `Reconcile` reuses the same wrapper instance for an unchanged Id (identity preserved) and preserves newest-first order; a removed Id drops its wrapper.
- [ ] **Test (concurrency)**: a `Changed` event arriving while a wrapper’s `IsBusy==true` keeps that wrapper instance and its `IsBusy`.
- [ ] Retype `Items`; implement `Reconcile`; wire `ItemArrived`→wrapper; move per-item logic to wrapper. Run all VM tests + `dotnet build`. Commit.

### Chunk 4 — Views + reminder + restyle
- [ ] Add `Flow_Action_Done` to all three resx.
- [ ] Update `FlowView.xaml` (CardDecisionBar + hide ✕ on `HasDecisions` + command retargeting) and `FlowView.xaml.cs` (peek wrapper).
- [ ] Drop the snooze `Action` in `ReminderBackgroundService`.
- [ ] Add `ActionCardInfo.Decisions`; replace `ActionCardControl.xaml` footer with `CardDecisionBar`.
- [ ] **Test**: `ActionCardInfo.Decisions` = [Decline(Default), Accept(Primary)]; `IsDestructive` → Accept emphasis `Danger`.
- [ ] Run full suite + build. Manual smoke: reminder fires → card shows Snooze/Done, no ✕; assistant write tool → Accept/Decline look identical. Commit.

## Tests (summary)
FlowItemViewModel: reminder decision derivation, command wiring, IsBusy gating, failure-keeps-card. FlowViewModel: Id-keyed reconcile identity + order, mid-busy concurrency. ActionCardInfo: binary Decisions + destructive emphasis. Regression: existing `ChatSessionStateMachineTests` (Accept/Decline still resolve the bool gate) stay green.

## Open questions (surface on completion)
1. Does `IFlowService.Snapshot` return live `FlowItem` instances or clones? (Affects whether `Reconcile` rebinds the same instance or swaps it; verify before implementing Chunk 3.)
2. Event ordering: does the store raise `Changed` before `ItemArrived` for a new item? If not, `OnFlowItemArrived` must reconcile/create the wrapper itself before raising.
3. Should non-reminder Flow sources adopt decisions later (e.g. background-chat)? Out of scope here; the mechanism is ready.
