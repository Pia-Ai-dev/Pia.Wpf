# The guided-tour track, parked — what it costs to resume

**Status:** parked by the owner, 2026-08-24. Not cancelled. **Owner:** Marco Altmann.
**Written:** 2026-08-24.
**Origin:** the `D` group of
[`../hermes_checkup/2026-08-22-hermes-followup-checklist.md`](../hermes_checkup/2026-08-22-hermes-followup-checklist.md),
whose rows `D2`–`D8` this doc replaces as the track's entry point. The design it defers to is
[`../hermes_checkup/2026-08-22-guided-tour-tool-plan.md`](../hermes_checkup/2026-08-22-guided-tour-tool-plan.md)
— still accurate, still executable, and **not superseded by this doc**.

**Why this doc exists.** `D1` shipped. Parking a track that has already put live code and a keybinding into
the app is not the same as never starting it, and a checklist row cannot carry what a resumer needs. This is
the resume point: what exists, what the one unanswered question is, what will have rotted, and what it costs.

---

## 1. What was decided

The owner chose to skip the D-track rather than answer its gate. That is a scheduling decision, not a
judgement on the feature: nothing measured says the tour is a bad idea. The plan doc's own risk table
predicted this outcome — *"It's the flashiest item and jumps the queue… the cheaper items in the review
(blueprints, error surface, diagnostics) should land first"* — and blueprints landed (`C1`–`C5`, `C7`) while
the error surface and diagnostics did not. Resuming the tour ahead of those two would repeat the mistake the
plan warned about.

## 2. What is already in the product

`D1` is shipped and its walker is well tested. Three qualifications, all of which an earlier draft of this doc
got wrong:

- **It exists in DEBUG builds only.** `DumpTourTargetsAsync` and its clipboard write are inside `#if DEBUG` in
  `MainWindowViewModel`, and the sole invoker is an `#if DEBUG` `KeyBinding` in code-behind
  (`MainWindow.xaml.cs:36-39`). The walker, the collector and the DI registration do still ship in Release —
  the command that dumps them does not.
- **The key-press path is untested and, as far as the repo shows, has never been observed running.**
  `TourTargetCollector` has **zero tests** — and it is the one piece that touches `Application.Current.Windows`
  / `IsActive` (`:54`) and marshals through `IUiDispatcher.PostAsync` (`:44`). The 18 green tests cover the pure
  walker and the ViewModel contract, not the seam between the keystroke and the walk.
- **It cannot find *missing* AutomationIds.** `IsOffered` requires a non-empty id
  (`TourTargetWalker.cs:88`), so an element without one is simply absent from the dump. The gap detector is
  `ViewAutomationIdTests`' `IdKind.Missing/Empty` and the playbook's own "Known gaps" section — not this.

| Piece | Path |
|---|---|
| Visual-tree walker | `src/Pia.Wpf/Helpers/TourTargetWalker.cs` (108 lines) |
| The DTO | `src/Pia.Wpf/Models/TourTarget.cs` |
| Service + interface | `src/Pia.Wpf/Services/TourTargetCollector.cs`, `Services/Interfaces/ITourTargetCollector.cs` |
| DI registration | `src/Pia.Wpf/Bootstrapper.cs:447` |
| The debug command | `MainWindowViewModel.DumpTourTargetsAsync` — inside `#if DEBUG` |
| Its keybinding | `MainWindow.xaml.cs:38` — **Ctrl+Shift+F12** |
| Tests | `tests/Pia.Wpf.Tests/Views/TourTargetWalkerTests.cs` (363 lines) |

In a Debug build, Ctrl+Shift+F12 logs the tourable elements of the active view and copies a JSON dump to the
clipboard.

**Keep it.** Two reasons, and neither depends on the tour ever shipping:

1. **It is the repo's only *runtime* AutomationId inventory,** and its blind spots are complementary to the
   static `ViewAutomationIdTests` sweep rather than overlapping it. It sees three things that sweep
   structurally cannot: ids on `ListBoxItem` containers set via `ItemContainerStyle`, ids inside a
   `ControlTemplate` whose `OnApplyTemplate` never fires under `Activator.CreateInstance`, and realized
   virtualized rows — all three are named known gaps in the playbook today. It also reports duplicate ids
   separately, which makes the CLAUDE.md trap (a literal id inside an `ItemsControl`, so every row reports the
   same one) visible in a single paste. And because `Describe` falls back to `GetType().Name` when no
   automation peer exists (`TourTargetWalker.cs:104`), a `ControlType` that is not a UIA control-type name is a
   mechanical **dead-id** signal — exactly the `ui:InfoBar` failure class the playbook records.
   **What it is not:** a way to find ids that are missing. Its contribution to `D7` is the *confirmation* half —
   does the id I just added actually surface at runtime — which the InfoBar case proves is not a given.
2. **It costs nothing to carry, and removing it costs more than keeping it.** One singleton, one debug command,
   nothing depends on it. Removal touches 8 files including two edits that are not tour-local: the `Collector`
   entry in the shared approved-suffix list (`NamingConventionTests.cs:31-34`) and two `MainWindowViewModel`
   ctor params with their test call site.

**One condition on "keep" being an asset rather than a liability, and it is not done yet:** add a line to
`docs/ui_automation/ui-automation-playbook.md` naming the keybinding, what it dumps, and its three blind spots
(popup-hosted ids, unrealized virtualized rows, missing ids). Today the only record of Ctrl+Shift+F12 outside
the source is a commit message and a superseded batch brief — and the playbook is the doc a UI-automation
session is told to read first. **This is how the thing rots to nobody knowing it exists.**

### The landmine this track armed, since defused

The clipboard write used to sit outside `#if DEBUG` while only its sole invoker was gated, so the moment `D3`
registered a Release-reachable invoker, every `AutomationId` **and every `Name`** — todo titles, chat titles,
i.e. user content by CLAUDE.md's own list — would have gone to the clipboard in a shipped build. That is closed:
the command and its clipboard write are now inside one `#if DEBUG`, the `MainWindowViewModelTests` that exercise
them are guarded to match, and `TourDumpDebugOnlyRuleTests.TheTourDumpIsCompiledOutOfRelease` reads the source
and fails unless that guard directly encloses both the method and the write with no directive splitting them.

So `D3` cannot re-arm it by accident, and there is no decision left owing here. What `D3` does still owe is the
same question one level up: the *walk* is Release-safe, but anything that ships a rendering of it — to a model,
to a log, to the clipboard — is carrying user content and needs its own answer.

## 3. The one question that has to be answered first

**`D-Q1`: is the goal onboarding, or arbitrary "where do I…" questions?** Unanswered, and it is the plan's own
§10 question 1. It is not a detail — it decides whether this is **a tool or a control**:

- **Onboarding** ⇒ a canned tour, no LLM in the loop. Far cheaper. `D3`'s `ITourToolHandler` is unnecessary;
  what you build instead is a static step list plus `D2`'s spotlight.
- **Arbitrary questions** ⇒ the generic tool as planned. This is the version that can explain surfaces nobody
  wrote a tour for, including ones added after it shipped — which is the whole argument of the plan's §2.

Do not start `D3` before answering it. Three of the plan's four open questions (narration placement, whether a
step may change app state, voice) are downstream of it and cheap to settle once it is decided.

**`D2` is not blocked by it** — a spotlight adorner is needed either way, and the plan calls it the first
visible result. It is also an `M` whose value is entirely contingent: as an `Enabler` for `D3`–`D6` it is worth
building, and on its own it is worth nothing. **Building `D2` before answering `D-Q1` buys a demo, not a
feature.**

## 4. What will have rotted by the time this resumes

Ordered by how likely it is to bite. Re-verify before trusting any of it.

- **`OwningView` is the most fragile assumption in the whole thing, and `D5` is built entirely on it.** The
  rule is "outermost `UserControl` below the root" (`TourTargetWalker.cs:53-54`), which holds only because
  Pia's views are `UserControl`s hosted directly by a `DataTemplate`. Wrap the content host in one shared
  shell `UserControl`, or move a view to a `Page`/custom `ContentControl`, and **every** target in the window
  starts reporting the wrapper's name. It is tested only against synthetic `OuterTestView`/`InnerTestView`,
  never a real view — and note that chrome elements legitimately report `NavigationSidebarView` rather than the
  content view, which reads like a bug if you are not expecting it. **Press Ctrl+Shift+F12 on every top-level
  view and check the `OwningView` field before writing any resolution code.**
- **The 200-target cap is much closer than it was.** `MaxTargets = 200` (`TourTargetWalker.cs:14`), and it
  truncates in visual-tree order, so what is lost is the *tail* rather than the least important. The plan doc
  counted 183 `AutomationId`s across 20 XAML files; a grep now finds ~348 occurrences, 48 in one settings view
  alone, and per-item ids multiply per row. **A dump read as an inventory without checking `Truncated` will be
  quietly wrong on a busy window.**
- **The keybinding is the un-gated half of the DI pair.** Deleting the `Bootstrapper` registration fails the
  gate (`BootstrapperGraphValidationTests` builds the real graph with `ValidateOnBuild`), but **nothing in the
  gate touches `MainWindow.xaml.cs`** — those four lines can be dropped in any code-behind refactor and the
  feature becomes silently unreachable while still compiling and still passing all 18 tests, because the
  ViewModel tests invoke the command directly and never the binding. **This is the likeliest way D1 rots to
  zero.**
- **AutomationId coverage moves constantly.** Every new interactive control is supposed to get one plus an
  `[InlineData]` row in `ViewAutomationIdTests`, so the target inventory the walker returns is a moving
  target — in the good direction, but it means any *count* recorded now is stale. `D7`'s gap list has to be
  regenerated, never inherited.
- **The gating mechanism.** §5.2 depends on `BuiltInPluginHandler`'s `isAvailable: Func<bool>?` predicate at
  `Services/Plugins/BuiltInPluginHandler.cs:41`. Confirm that seam still exists and still returns `[]` when
  false; the tool-access surface has been reworked twice since the plan was written.
- **`INavigationService.NavigateToAsync<TVm>()` and `INavigationAware`.** §5.5's resolve → navigate → await →
  re-resolve sequence assumes both. Routes in this app are magic strings rather than an enum, so check the
  actual navigation surface rather than the plan's description of it.
- **Still true and worth re-reading, not re-deriving:** Pia has **no `Adorner` usage anywhere**, so §5.4 is
  genuinely new code; and the plan's §4 ("three things that collapse in WPF") and §6 ("what is actually hard")
  are about WPF, not about Pia, so they do not rot.

## 5. What it costs to resume

Straight from the checklist's own ratings, with `D1` removed:

| Row | What | Effort | Value | Gated by |
|---|---|---|---|---|
| `D2` | Spotlight adorner + popover | **M** | Enabler | — (but see §3) |
| `D3` | `ITourToolHandler`, `isAvailable` gating, Esc | **M** | High | `D1`, `D2`, **`D-Q1`** |
| `D4` | `start` / `next` / `prev` + paging chrome | **S** | Med | `D3` |
| `D5` | Cross-view navigation | **M** | **High** | `D3` |
| `D6` | Virtualized-list scroll-into-view, overlay layers | **M** | Med | `D5` |
| `D7` | AutomationId gap-fill | **S** | Med | `D1` — **ungated** |
| `D8` | Recorded UI script, two-step tour end to end | **S** | Med | `D4`, `D5` |

Roughly **3–4 weeks** for `D2`→`D6`, and it needs a **desktop session** throughout — an adorner, a popover and
scroll-into-view cannot be verified headlessly, and `D8` is a WinWright recording. That desktop requirement is
the real cost, not the line count.

`D2 → D3 → D5` is the vertical slice worth building: it demos the whole idea and lands the row the plan calls
"where the real value is". `D4`, `D6` and `D8` are polish and proof on top of it.

**`D7` stays open, but as a tag-along rather than scheduled work.** It is `S`, it does not depend on `D-Q1`,
and it pays into `ui-automation-playbook.md` and UI-test coverage whether or not a tour is ever built — but
picking it up *as its own row* is reopening a track that was just parked. Fold its substance (add the id, bump
the `[InlineData]` count, update the playbook's "Known gaps") into the next UI change instead. Generate its gap
list from `ViewAutomationIdTests`' `IdKind.Missing` and the playbook, **not** from a Ctrl+Shift+F12 dump — the
walker only reports ids that already exist.

## 6. How to un-park this

1. Answer `D-Q1` in writing — in the checklist's decision-gates table, not only here.
2. Re-read the plan doc. It is self-contained and still current; this doc does not replace it.
3. Run the §4 rot checks, starting with Ctrl+Shift+F12 on every top-level view.
4. Build `D2 → D3 → D5` as the slice. Stop after `D5` and look at it before committing to `D6`/`D8`.
5. Move the rows back into the checklist's `D` section as you go, ticking each in the commit that lands it.

**One thing not to do:** do not resume this ahead of the two `High` items still sitting unplanned — the error
layer on the failure card and consented Send Diagnostics. They are one feature area (failure legibility), they
are cheaper, and the tour's own plan doc argues they should land first.
