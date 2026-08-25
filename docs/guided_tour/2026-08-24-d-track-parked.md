# The guided-tour track, parked — what it costs to resume

**Status:** parked by the owner, 2026-08-24. Not cancelled. **Owner:** Marco Altmann.
**Written:** 2026-08-24. **Revised:** 2026-08-25 — the design plan folded in below as **Part II**.
**Origin:** the `D` group of
[`../hermes_checkup/2026-08-22-hermes-followup-checklist.md`](../hermes_checkup/2026-08-22-hermes-followup-checklist.md),
whose rows `D2`–`D8` this doc replaces as the track's entry point. The design it defers to is **Part II
below** — still accurate, still executable, and **not superseded by Part I**.

**Part I** (§1–§6) is the resume point: what shipped, what is open, what will have rotted, what it costs.
**Part II** is the executable design, carried unedited from the folded plan doc.

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

**One condition on "keep" being an asset rather than a liability — still open, re-verified unmet
2026-08-25:** add a line to `docs/ui_automation/ui-automation-playbook.md` naming the keybinding, what it
dumps, and its three blind spots (popup-hosted ids, unrealized virtualized rows, missing ids). Today the
only record of Ctrl+Shift+F12 outside the source is a commit message and a superseded batch brief — and the
playbook is the doc a UI-automation session is told to read first. **This is how the thing rots to nobody
knowing it exists.**
`grep -niE 'F12|TourTarget|Ctrl.Shift' docs/ui_automation/ui-automation-playbook.md` still returns nothing
(2026-08-25). Writing that line is not a doc-fold task — someone has to author it.

### The landmine this track armed, since defused

The clipboard write used to sit outside `#if DEBUG` while only its sole invoker was gated, so the moment `D3`
registered a Release-reachable invoker, every `AutomationId` **and every `Name`** — todo titles, chat titles,
i.e. user content by CLAUDE.md's own list — would have gone to the clipboard in a shipped build. That is closed:
the command and its clipboard write are now inside one `#if DEBUG`, the `MainWindowViewModelTests` that exercise
them are guarded to match, and `TourDumpDebugOnlyRuleTests.TheTourDumpIsCompiledOutOfRelease` reads the source
and fails unless that guard directly encloses both the method and the write with no directive splitting them.

So `D3` cannot re-arm it by accident, and there is no decision left owing on the clipboard write itself.
**Still open, 2026-08-25:** the same question one level up. The *walk* is Release-safe, but anything that ships
a rendering of it — to a model, to a log, to the clipboard — is carrying user content and needs its own
answer. `D3` cannot be registered without answering it for the surface it adds.

## 3. The one question that has to be answered first

**`D-Q1`: is the goal onboarding, or arbitrary "where do I…" questions?** Still unanswered — re-verified
2026-08-25 against the checklist's `D-Q1` decision-gate row. It is Part II's own §10 question 1. It is not a
detail — it decides whether this is **a tool or a control**:

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
  truncates in visual-tree order, so what is lost is the *tail* rather than the least important.
  **Correction to Part II, measured 2026-08-25:** Part II's §2 table claims "183
  `AutomationProperties.AutomationId` values across 20 XAML files". That is stale, and so is the "~348" an
  earlier draft of this doc carried. Today it is **351 occurrences across 50 XAML files** under
  `src/Pia.Wpf` (a 352nd hit is prose in a `FlowView.xaml` comment), **48 of them in
  `Views/SettingsViews/AssistantView.xaml` alone**. Those are *source* occurrences: a per-item
  `StringFormat` id multiplies per rendered row, so the runtime target count is higher again. Part II is
  carried unedited, so the figure is corrected here and not there. **A dump read as an inventory without
  checking `Truncated` will be quietly wrong on a busy window.**
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
2. Re-read **Part II**. It is self-contained and still current; Part I does not replace it.
3. Run the §4 rot checks, starting with Ctrl+Shift+F12 on every top-level view.
4. Build `D2 → D3 → D5` as the slice. Stop after `D5` and look at it before committing to `D6`/`D8`.
5. Move the rows back into the checklist's `D` section as you go, ticking each in the commit that lands it.

**One thing not to do — spent, 2026-08-25:** this used to say do not resume ahead of the two `High`
failure-legibility items. Both have shipped: the error layer on the failure card as `G2`–`G5`, and the log
bundle as `G1` — **export-only by owner decision, no upload path**, so there is no "Send Diagnostics" to wait
for. Nothing outside this track is holding it back now; the gate is `D-Q1` (§3).

---

# Part II — the design, unchanged

*Carried here unedited from `docs/hermes_checkup/2026-08-22-guided-tour-tool-plan.md` when that file was
folded away on 2026-08-25; it is the executable design half of this track, and every reference in Part I to
"the plan" or "the plan doc" means the text below.*

Two notes on reading it, neither of which changes a word of it. Its heading levels and section numbers are
its own — its `## 1.`–`## 10.` are not Part I's. And its one relative link,
`2026-08-22-hermes-update-review.md` in the Origin line, was written from `docs/hermes_checkup/`, so from
this folder it resolves as `../hermes_checkup/2026-08-22-hermes-update-review.md`. One figure in it is out
of date; the correction is in Part I §4, not below.

# Plan — A Guided-Tour Tool: Let the Assistant Point at the UI

**Status:** planned, not started. Self-contained: everything needed to execute it is below.
**Owner:** unassigned. **Written:** 2026-08-22.
**Origin:** §3.4 of [`2026-08-22-hermes-update-review.md`](../hermes_checkup/2026-08-22-hermes-update-review.md).

---

## 1. What this is

The assistant dims the window, spotlights one control, and attaches a popover explaining it. Either
it narrates step by step at its own pace, or it hands the user Next/Prev and lets them page through.

> User: *"where do I set up a second provider?"*
> Instead of describing a path through four screens, Pia walks them there and points at the button.

**One generic tool, no baked-in tours.** There is no `tours.json` to maintain. The assistant asks the
app what is currently on screen and composes the tour on the spot — so it can also explain surfaces
nobody wrote a tour for, including ones added after the tool shipped.

---

## 2. Why this is worth doing in Pia specifically

Every agent can answer questions about the world. A **desktop assistant is the only kind that can be
asked about itself** — and today Pia answers those questions the same way a web page would: with
prose describing a path.

Pia has already built both halves of the better answer, for entirely unrelated reasons:

| Half | Built for | Where |
|---|---|---|
| The app is **addressable** — 183 `AutomationProperties.AutomationId` values across 20 XAML files, with the stable ones catalogued | UI test automation (WinWright/UIA), 2026-08-16 onward | `docs/ui_automation/ui-automation-playbook.md`, `tests/ui-scripts/` |
| The **answers exist** — every how-to question is covered in the docs corpus and indexed into the server knowledge base (67 documents, link-check passing) | Support/KB work | `docs/user_questions/2026-08-16-ui-howto-coverage.md`; corpus lives in the sibling repo at `../Pia/src/Pia.Docs` |

The KB knows the answer. The AutomationIds make the app pointable. **Neither was built with tours in
mind, and the tour is the thing that joins them** — showing the answer in the running app instead of
reciting it.

It is also the natural successor to the static `PiaHelpHint` control added 2026-08-21: a help icon
answers *"what is this field"*; a tour answers *"where do I do X"*.

---

## 3. The reference implementation

Hermes, August 2026. Roughly 940 lines across two sides:

| File | Lines | Role |
|---|---|---|
| `tools/tour_tool.py` | 202 | Tool schema + a thin dispatcher |
| `apps/desktop/src/lib/tour/engine.ts` | 387 | Runs one tour action, on both surfaces |
| `apps/desktop/src/lib/tour/collect-targets.ts` | 159 | Discovers what a tour can point at |
| `apps/desktop/src/lib/tour/spotlight-blur.ts` | 191 | Blurred scrim workaround |

Actions: `targets | show | start | next | prev | stop`. Two surfaces: `app` (its own UI) and
`preview` (any page in its in-app browser).

### The one design decision that makes it work

Everything hinges on what `action="targets"` returns per element:

```ts
interface TourTarget {
  label: string                            // aria-label, title, alt, or text
  rect: [x, y, width, height]
  role: string
  selector: string
  stable: boolean   // ← the whole trick
}
```

`stable: true` means the selector keys off **identity** (`data-tour`, `id`, `data-testid`, a unique
`aria-label`) rather than position. A positional `div:nth-child(3) > button:nth-child(2)` matches
right now and breaks on the next re-render.

The collector scans in priority order — explicit `[data-tour]` markers, then landmarks/headings/roles,
then labelled interactive elements — drops anything smaller than 4×4 or off-screen, **verifies each
selector resolves back to the exact element it came from** (`doc.querySelector(selector) !== el` →
discard), and sorts stable-first. The tool description then instructs the model to prefer stable
selectors and re-scan when one stops matching.

That is a great deal of machinery to answer one question: *which handles can I trust?*

---

## 4. Three things that collapse in WPF

**1. The stability heuristic disappears.** `AutomationProperties.AutomationId` is the same concept as
`data-tour` — explicit, durable, non-positional — except it is a first-class WPF property rather than
a convention. So the four-tier fallback, the positional selector builder, the self-verification pass
and the `stable` boolean all go away. The rule becomes: **if it has an AutomationId it is tourable;
if it doesn't, it is not offered.** Stability by construction, not by scoring.

**2. The transport disappears.** Hermes needs a `tour.request` / `tour.respond` round-trip through its
gateway's blocking-prompt bridge because the agent is a Python process and the UI is Electron. Pia's
tool handlers run **in-process** — `FilesToolHandler`, `TodoToolHandler`, `ScheduledJobToolHandler`
already do. A tour handler needs `IUiDispatcher` (`Post` / `PostAsync` / `PostOrRun`) to hop to the UI
thread and nothing more.

**3. The blur workaround disappears.** `spotlight-blur.ts` is 191 lines compensating for
`backdrop-filter` clipping to an element's box rather than its fill. WPF has no such constraint, and
the blur is not load-bearing — **don't build it.** The dim scrim carries the effect.

**Net:** the WPF version is materially smaller than hermes's, and its central mechanism is stronger.

---

## 5. Design

### 5.1 Tool surface

Follow the existing handler shape: `ITourToolHandler.GetTools() -> IList<AITool>`, singleton in
`Bootstrapper.cs` next to the other `I*ToolHandler` registrations.

| Action | Args | Returns |
|---|---|---|
| `targets` | — | The tourable elements on the active view |
| `show` | `automationId?`, `title`, `text`, `side?` | Whether the target resolved |
| `start` | `steps[]`, `stepIndex?` | Whether the tour started |
| `next` / `prev` | — | New active index |
| `stop` | — | — |

`show` replaces the previous highlight (agent-paced narration, one call per beat, paired with a chat
message). `start` hands the user Next/Prev. A step with no `automationId` is a centred narration
popover — keep that: it is how a tour opens and closes.

`targets` returns per element: `AutomationId`, `Name`, `ControlType`, bounds, and the owning view.
No `stable` field — everything returned is stable by definition.

### 5.2 Gating — this tool is not "safe"

It mutates nothing, but it **takes over the screen**, so it is not read-only in the sense the tool
gate means. Two rules:

- **Interactive sessions only.** Register through `BuiltInPluginHandler`'s existing
  `isAvailable: Func<bool>?` predicate (`Services/Plugins/BuiltInPluginHandler.cs:41` — `GetTools()`
  returns `[]` when it is false). A headless or scheduled run must never hijack the user's screen.
  This mirrors hermes gating `desktop_ui` on the *session source*, and it needs no new mechanism.
- **Always escapable.** `Esc` stops any tour, unconditionally, without asking the agent. The stop path
  must not depend on a model turn completing.

### 5.3 Target discovery

Walk the visual tree from the active window; collect elements with a non-empty `AutomationId` that are
visible and hit-testable; return them with `Name` and `ControlType`. Cap the count.

`docs/ui_automation/ui-automation-playbook.md` is the existing registry of stable ids and should stay the source of
truth — if a surface a tour needs has no id, **the fix is to add the AutomationId**, which also makes
that surface testable. The two features reinforce each other; that is the point of §2.

### 5.4 Spotlight

**Pia has no `Adorner` usage anywhere today** — this is genuinely new code, though small.

One adorner on the window's `AdornerLayer` drawing a full-window rectangle minus the target:

```csharp
new CombinedGeometry(
    GeometryCombineMode.Exclude,
    new RectangleGeometry(windowBounds),
    new RectangleGeometry(targetBounds, radiusX: 6, radiusY: 6));
```

Rounded corners come free. Animate the cutout between steps with a `RectAnimation` so it glides
rather than jumps. The popover is a second adorner or a `Popup`, placed off the target's
`TransformToAncestor(window)` rect, auto-flipping when it would fall outside the window.

### 5.5 Navigation

A step may target a control on a view the user is not on. `INavigationService.NavigateToAsync<TVm>()`
exists; `INavigationAware` gives the load hook. The sequence per step is: resolve → if not found,
navigate to the owning view → await load → re-resolve → highlight → **fail cleanly if it still
doesn't resolve**, and tell the agent so, rather than highlighting nothing.

---

## 6. What is actually hard

Not the spotlight. These:

1. **Cross-view tours.** "Where do I add a provider?" means highlighting inside Settings while the
   user is on Todo. Navigate, wait for load, resolve. This is also **the most valuable part** — "where
   is X" is the question people actually ask — so it cannot be deferred to a later tier without
   gutting the feature.
2. **Virtualized lists.** An `ItemsControl` over a `VirtualizingStackPanel` has not realized the item
   you want to point at. Needs scroll-into-view-then-resolve, and a clean "not present" answer when
   the item genuinely isn't there.
3. **Overlays and modals.** `DialogOverlayService`, `MeetingAttendeeOverlay`, `VoiceModeOverlay`,
   `DirectTranscriptionOverlay`. Which `AdornerLayer` the spotlight attaches to decides whether it
   draws above or below them, and getting it wrong reads as broken rather than slightly off.
4. **Semantics, not handles.** `targets` gives ids and labels; the model needs to know what those
   controls *mean* to explain them. That is the KB's job (§2), and it lives in a **different repo** —
   so the quality of a tour's narration depends on retrieval that this plan does not own.

---

## 7. Scope

**In:** the `app` surface, agent-paced `show`, user-paced `start`, cross-view navigation, Esc-to-stop,
interactive-only gating.

**Out:**

- **The `preview` surface.** Hermes can tour any web page in its in-app browser. Pia has no such pane,
  and adding one to enable tours is the tail wagging the dog.
- **The blurred scrim** (§4.3).
- **Authored/canned tours.** The generic tool is the feature. A first-run tour is a *possible later
  consumer*, not part of this.
- **Driving the UI.** Highlighting only. Clicking on the user's behalf is a different feature with a
  different risk profile and would need the approval gate.

---

## 8. Work breakdown

| Step | Notes |
|---|---|
| 1 | Visual-tree target collector + a debug command that dumps `targets` for the active view — verifiable with no LLM in the loop |
| 2 | Spotlight adorner + popover, driven by a hardcoded id. **First visible result** |
| 3 | `ITourToolHandler` with `targets` / `show` / `stop`; `isAvailable` gating; Esc handler |
| 4 | `start` / `next` / `prev` with the paging chrome |
| 5 | Cross-view navigation and the resolve → navigate → re-resolve sequence |
| 6 | Virtualized-list scroll-into-view; overlay/adorner-layer handling |
| 7 | AutomationId gap-fill for surfaces a tour needs but cannot address — feeds `docs/ui_automation/ui-automation-playbook.md` |
| 8 | A recorded UI script in `tests/ui-scripts/` that runs a two-step tour end to end |

Steps 1–3 are the vertical slice and demo the whole idea. Step 5 is where the real value is.

---

## 9. Risks

| Risk | Mitigation |
|---|---|
| Agent invents AutomationIds instead of calling `targets` | The tool description says call `targets` first (hermes does the same); `show` returns an explicit not-found the model can act on. Never highlight "nothing" silently |
| A tour starts during a meeting recording or voice mode and covers the screen | Interactive gating is not enough — also refuse to start while a modal/overlay owns the screen, and always honour Esc |
| Spotlight drifts after a layout change | Re-resolve bounds on `LayoutUpdated` while a step is active; drop the highlight if the element vanishes rather than pointing at stale coordinates |
| Feature depends on KB quality that lives in another repo | Accept it. A tour with weak narration still shows the right control, which is most of the value |
| It's the flashiest item and jumps the queue | It is ranked **Nice / L** deliberately. The cheaper items in the review (blueprints, error surface, diagnostics) should land first |
| First adorner code in the codebase | Contained in one control; no existing behaviour depends on the adorner layer today |

---

## 10. Open questions for the owner

1. **Does the tour need the agent at all for the common case?** A canned first-run tour needs no LLM.
   The generic tool is what makes *arbitrary* questions answerable — but if the real goal is
   onboarding, a static tour is far cheaper. Worth deciding before step 3, because it changes whether
   this is a tool or a control.
2. **Narration in chat, in the popover, or both?** Hermes pairs each `show` with a chat message. In
   Pia the chat panel may be behind the spotlight scrim, which argues for the popover carrying the
   text and chat carrying only a summary.
3. **Does a tour step ever get to change app state** (expand a section, open a settings tab) to make
   its target reachable, or does it stop and ask the user to do it? Navigating between top-level views
   is assumed in §5.5; anything beyond that is a scope decision.
4. **Voice.** Pia has streaming TTS (`TtsService.SpeakChunkedAsync`). A spoken tour is a small
   increment on top and possibly the strongest version of the feature — but it is a separate decision.
