# Plan — A Guided-Tour Tool: Let the Assistant Point at the UI

**Status:** planned, not started. Self-contained: everything needed to execute it is below.
**Owner:** unassigned. **Written:** 2026-08-22.
**Origin:** §3.4 of [`2026-08-22-hermes-update-review.md`](2026-08-22-hermes-update-review.md).

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
