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

`D1` is **shipped, tested and reachable by a human today.** It is not scaffolding behind a flag.

| Piece | Path |
|---|---|
| Visual-tree walker | `src/Pia.Wpf/Helpers/TourTargetWalker.cs` (108 lines) |
| The DTO | `src/Pia.Wpf/Models/TourTarget.cs` |
| Service + interface | `src/Pia.Wpf/Services/TourTargetCollector.cs`, `Services/Interfaces/ITourTargetCollector.cs` |
| DI registration | `src/Pia.Wpf/Bootstrapper.cs:437` |
| The debug command | `MainWindowViewModel.DumpTourTargetsAsync` (`:437`) |
| Its keybinding | `MainWindow.xaml.cs:38` — **Ctrl+Shift+F12** |
| Tests | `tests/Pia.Wpf.Tests/Views/TourTargetWalkerTests.cs` (363 lines) |

Pressing Ctrl+Shift+F12 logs the tourable elements of the active view and copies a JSON dump to the
clipboard.

**Keep it.** Two reasons, and neither depends on the tour ever shipping:

1. **It is the instrument `D7` needs.** `D7` (AutomationId gap-fill) is the one D row that was never gated on
   `D-Q1`, and the walker is exactly how you find a surface that has no id — it collects elements *with* a
   non-empty `AutomationId`, so what it omits is the gap list. That makes it useful to
   `docs/ui_automation/ui-automation-playbook.md` and to `ViewAutomationIdTests` regardless.
2. **It costs nothing to carry.** One singleton, one debug command, no behaviour depends on it, and the tests
   are ordinary gate tests with no UI thread requirement beyond the existing view harness.

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

- **The walker's assumptions about the visual tree and view names.** It walks from the active window and
  attributes each element to an owning view. Adding a top-level view is six magic-string edits in this
  codebase (see the `project_toplevel_view_wiring` note in the repo's practice memory), so a view added while
  parked may be collected under the wrong name or not at all. **Press Ctrl+Shift+F12 on every top-level view
  and check the `View` field before writing any resolution code.**
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

**`D7` is severable and should be treated as such.** It is `S`, it needs no desktop pass beyond a normal UI
run, it does not depend on `D-Q1`, and it pays into `ui-automation-playbook.md` and UI-test coverage whether or
not a tour is ever built. It stays on the checklist as an ordinary open row; the rest of the track is parked
here.

## 6. How to un-park this

1. Answer `D-Q1` in writing — in the checklist's decision-gates table, not only here.
2. Re-read the plan doc. It is self-contained and still current; this doc does not replace it.
3. Run the §4 rot checks, starting with Ctrl+Shift+F12 on every top-level view.
4. Build `D2 → D3 → D5` as the slice. Stop after `D5` and look at it before committing to `D6`/`D8`.
5. Move the rows back into the checklist's `D` section as you go, ticking each in the commit that lands it.

**One thing not to do:** do not resume this ahead of the two `High` items still sitting unplanned — the error
layer on the failure card and consented Send Diagnostics. They are one feature area (failure legibility), they
are cheaper, and the tour's own plan doc argues they should land first.
