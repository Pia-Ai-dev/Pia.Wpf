# Batch 14 — View-coverage debt (the tests three batches booked and one made possible)

**Phase 3 cleanup · Size S–M · Work on `feature/agent-run-spine`**, after [Batch 13](13-view-test-host.md)
(shipped `928e27e`→`09522be`) and [Batch 09](09-scheduler-ui.md) (shipped `c7020fe`→`ea77c95`)

Batch 13 fixed the `WpfStaHost` defect that made XAML-level facts impossible and then spent *part* of the
headroom. This batch spends the rest. Every item in it is something a shipped batch wrote down as **booked
rather than blocked** — nobody has to decide whether it is worth doing, only to do it.

**This batch SHORTENS the Rank-1 manual round and adds NOTHING to it.** It ships no user-visible change at
all: no new string, no new control, no behaviour. It is the second piece of work on this branch to move that
number down, and unlike Batch 13 it is not also paying for a defect fix. Read that as the whole justification
for its rank — the alternative at the same moment is Batch 08, which is size L and will lengthen Rank 1.

## Goal

Close the automatable half of the View-coverage debt: the Batch 09 jobs row, the run panel's branch line and
its non-templated region, and the seven settings views nobody has ever parsed — on a shared walker, so the
*next* view after this batch is genuinely a short file rather than a copy.

## Scope — four work groups

### G1 — Lift the binding-path walk out of one test file

`Walk` / `BoundPath` / `TargetsDataContext` / `ResolvePathType` are private to
`SettingsAssistantViewParseTests` (`:148`–`:219`). G4 needs seven copies of them or a helper; this is the
helper. Move them to a shared `BindingPathWalker` under `tests/Pia.Wpf.Tests/Views/`, **behaviour
unchanged**, and re-point the settings test at it (its facts must stay green with no assertion edited —
that is the proof the move was mechanical).

Four properties are load-bearing and each was learned the hard way. Carry them verbatim, with their comments:

- **Re-rooting on a local `DataContext` binding.** A settings page is not one DataContext; the first version
  of the settings test assumed it was and had to be rewritten.
- **`RelativeSource` / `ElementName` / explicit `Source` are skipped.** That filter is what keeps `loc:Str`
  (explicit Source) and the ItemsControl-ancestor command bindings out of scope. Widening it is not a free
  improvement — see D1.
- **A null context makes descendants *unknown*, not *failed*,** so one bad re-root is one finding rather than
  a cascade.
- **The non-vacuity floor stays in the CALLING test, not in the helper.** It is a per-view number and the
  helper must not own it; "no unresolved paths" over an empty walk is vacuously true, which is reachable.

### G2 — Batch 09's jobs row `DataTemplate`

The roadmap calls this "the FIRST thing to write when someone picks this up again". `Views/SettingsViews/
AssistantView.xaml:545`–`:601`, driven through `ItemTemplate.LoadContent()` exactly as
`RunProgressPanel_RendersATimelineRow_…` and `…_WithItsPersonaAvatar` already do.

**Ten item-scoped paths**, read off the markup rather than off the roadmap's list of them — which names
`ToggleLabel`, `CanRunNow`, `StatusLabel` and omits four more:

`Name` · `Query` · `KindLabel` · `RecurrenceLabel` · `StatusLabel` · `NextFireAt` (with
`StringFormat=g`) · `OwnedByThisDevice` (drives the *not owned here* line through
`InverseBooleanToVisibilityConverter`) · `ToggleLabel` · `StatusIsKnown` (`IsEnabled` on Toggle) ·
`CanRunNow` (`IsEnabled` on Run now).

**Plus four command paths that the precedent does not cover, and this is the group's whole design question.**
`DataContext.{StartEdit,ToggleEnabled,RunNow,Delete}Command`, each bound
`RelativeSource={RelativeSource AncestorType=ItemsControl}` with `CommandParameter="{Binding}"`.
`LoadContent()` returns a **detached** element with no ItemsControl ancestor, so those four resolve to
nothing in a loaded row. The two shipped precedents never met this because every path in their templates is
a plain DataContext path. See **D1**.

Worth stating because it is the failure this group prevents: a renamed command on
`ScheduledJobsSettingsViewModel` breaks all four buttons **silently**, with the build at 0 warnings and every
ViewModel test still green.

### G3 — The run panel's branch line, and the rest of its non-templated region

R11 named three run-panel surfaces; Batch 13 covered two and left this one "with no technical obstacle in
front of it". `RunProgressPanel.xaml:68`/`:71` — `OutputBranchNote` and `HasOutputBranch`.

Do it the **strong** way rather than the literal way. Run G1's walker over `RunProgressPanel` and pin every
non-templated path on the panel in one fact: the branch line, the publish offer, the result note, the ledger
strip, the state chip, the activity line. Same cost as a render fact for one `TextBlock`, and the panel is
the surface Batches 06, 07 and the consolidation pass all added lines to without any of them being parsed.

**Read the root type by reflection; do NOT hardcode `RunProgressViewModel`.** `AssistantView.xaml:51` hosts
the panel with `DataContext="{Binding ActiveRunProgress}"`, so the root is
`typeof(AssistantViewModel).GetProperty(nameof(AssistantViewModel.ActiveRunProgress)).PropertyType`, asserted
to be `RunProgressViewModel` exactly as the settings test asserts `SettingsViewModel.AssistantVm`. That test
documents hardcoding as *the one way this technique goes green while proving nothing*, and G4 below requires
the reflected form — G3 must not be the exception.

**Do not promise a path count.** The panel carries at least three `ItemsControl`s (steps, timeline, children)
whose contents are templated and therefore invisible to a logical walk, so the non-templated set may be
smaller than it looks. Count it when the walker runs and set the non-vacuity floor from the measured number,
well under it.

Then **one** render fact for the branch line itself, following the trace/avatar precedent, because "the text
appears when `HasOutputBranch` is true" is the half a path check cannot see. That is what takes Phase 3's
R11 to fully closed instead of mostly closed.

### G4 — The seven settings views nobody has parsed

`AccountView` · `E2EEOnboardingView` · `GeneralView` · `OptimizeView` · `PersonasView` · `PluginsView` ·
`ProvidersView`. Table-driven over `(view type, root ViewModel type)` on G1's walker.

**Corrected 2026-08-01 (as-built, D5). G4 shipped FIVE, not seven, and that NARROWED this section's stated
scope** — the owner was told and said proceed. Written: `GeneralView` (`512a9a2`) · `AccountView` (`165e085`) ·
`ProvidersView` (`53b1565`) · `OptimizeView` (`921efb9`) · `PluginsView` (`2e75d72`). Not written:
`PersonasView` and `E2EEOnboardingView`, because `SettingsView.xaml` instantiates exactly **six** settings
views (`ProvidersView:84`, `OptimizeView:97`, `AssistantView:110`, `GeneralView:123`, `AccountView:136`,
`PluginsView:149`) and **neither of those two is among them** — so the recipe in the next paragraph, "read the
root by REFLECTION off the property `SettingsView.xaml` hosts the view with", is *literally unexecutable* for
them. For `PersonasView` it is worse than unexecutable: obeying it walks straight into the trap this section
itself names, because `SettingsViewModel.PersonasVm` exists and type-matches the real host's type by
coincidence while **no markup binds it**, so every path would pass and the fact would prove nothing. Both views
are instead walked as logical children of parsed views (`PersonasView` inside the settings-`AssistantView`
walk; `E2EEOnboardingView` inside `AccountView`'s), each with one added assertion that is strictly stronger
than a standalone file: an `=AddPersonaCommand [PersonaSettingsViewModel]` anchor, and a duck-type fact pinning
both `E2EEOnboardingView` hosts to the same concrete `OnboardingViewModel` type. Read `14-view-coverage-debt.impl.md`
§1 D5 and W10/W11 for the full derivation. The **Goal** paragraph above says "seven settings views" for the
same reason and is stale in the same way.

**Each root type is read by REFLECTION off the property `SettingsView.xaml` hosts the view with**, the way
the settings Assistant test reads `SettingsViewModel.AssistantVm` and asserts its type — never hardcoded. A
future re-host then fails the test instead of quietly checking every path against the wrong ViewModel, which
is the one way this whole technique can go green while proving nothing.

**Corrected 2026-08-01 (post-review, D1).** The reasoning in the paragraph above is right and the trap it
names is real — hardcoding the root *is* the way this technique goes green while proving nothing, and all six
shipped facts avoid it. What the third sentence overstated is what the reflection *buys*: reading the root off
`SettingsViewModel.<X>Vm` catches a **rename** (`nameof` stops compiling) and a **retype**
(`Assert.Equal(typeof(...), root)`) — **not a re-host.** No parse test opens the host markup, so repointing
`SettingsView.xaml:123` at `{Binding AccountVm}` kills all 40 of GeneralView's paths at runtime, renders the
tab as empty controls, and leaves every Views fact green (measured 16/16, twice, at 0 warnings). **The
re-host is guarded by a separate fact**, `ViewHostDataContextTests`, which constructs the real hosts, reads
the `DataContext` binding path declared at each of the six `SettingsView.xaml` host sites plus
`AssistantView.xaml:51`, and asserts it found all six — so a walk that reaches none of them cannot pass
vacuously.

**Per-view non-vacuity floor, not a shared one** (D2, decided): a shared floor lets a large view's path count
cover for a small view whose walk died at a templated container.

**This is the group to drop if the round runs short.** G1–G3 are what shipped batches actually booked; G4 is
the scalable consequence, and half a G4 is still worth shipping — one view per file, committed as it lands.

## Decisions

- **D1 — the four `RelativeSource` command bindings (OPEN, decide empirically).** Options, best first:
  **(a) give the loaded row a real `ItemsControl` ancestor** so `AncestorType=ItemsControl` resolves for
  real. Strongest, and the only version that catches a renamed command. **Check the shape before committing
  to it:** the *parsed* panel's `ItemsControl` has `ItemsSource="{Binding Jobs}"` bound, and adding to
  `Items` while `ItemsSource` is set **throws** — so (a) probably has to mean a *throwaway* `ItemsControl`
  carrying the real one's `ItemTemplate` and `DataContext`, not the parsed one. If it turns out to need the
  parsed control, (a) has collapsed into (c) and should be called that.
  **(b) cover the ten DataContext paths by render and leave the four commands to the ViewModel tests**, with
  the gap named in the test's own doc comment. Honest, cheap, proves less.
  **(c) let the real `ItemsControl` generate its containers** — needs `ApplyTemplate` plus a measure pass,
  which is exactly what the precedent avoids and what Batch 13's opened items warn about.
  Do NOT close this by widening `TargetsDataContext` in G1's helper: that filter is load-bearing for
  `loc:Str`, and breaking it to reach four bindings would put every localization extension back in scope.
- **D2 — per-view non-vacuity floor.** Decided, above.
- **D3 — no production code changes.** If a new fact goes red against real markup, that is a **found
  defect**: fix it, and say in the commit that the fact found it rather than that it was written for it.
  This batch's value is partly that nobody knows whether these paths are all correct.

## Guardrails

- **Rebuild, always.** `dotnet build -t:Rebuild` in Debug **and** Release, 0 warnings both. An *incremental*
  build reuses stale BAML and will lie to you about a XAML-level red-before/green-after — Batch 13 lost time
  to exactly this and recorded it as an opened item.
- **Demonstrate every fact RED before green** by injecting a binding typo into the XAML and rebuilding, then
  reverting. This is the only kind of test where "it passes" and "it is checking something" are genuinely
  different claims.
- **Never call `WpfStaHost.Pump()` from inside a `Run` body** — it throws by design as of Batch 13, and the
  message names the remedy (`Run(mutate)` → `Pump()` → `Run(observe)`).
- **Dispose every ViewModel a fact constructs, in a `finally`.** `RunProgressViewModel` subscribes to
  `IAgentRunService.RunChanged` in its ctor; the withdrawn version of the row-render fact leaked one onto a
  host that outlives every test, which is how one fact reaches into another.
- `[Collection("WpfApplicationStatic")]` on every new view-test class — the host is process-global.
- Standing guardrails apply as always, though most are inert here: this batch adds no string, no log line and
  no persisted anything.

## Acceptance

The jobs row, the branch line, the run panel's non-templated paths and the remaining settings views are all
covered by executed facts; the gate is `failed: 0` with 0 warnings in **both** configurations under
`-t:Rebuild`; and `00-OVERVIEW.md`'s Rank-1 list is **shorter by name** — each item this batch shortens edited
in place, with the manual half that survives stated, in the strict style Batch 13 established. Nothing is
added to that list.
