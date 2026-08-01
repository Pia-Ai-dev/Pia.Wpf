# Batch 14 — view-coverage debt: adjudicated review

**Range reviewed:** `0c5cb42..17357e0` (nine commits, all under `tests/Pia.Wpf.Tests/Views/`).
**HEAD:** `17357e0` on `feature/agent-run-spine`. `git diff --stat 0c5cb42..HEAD -- src/` is **empty**;
working tree clean at review time.

**Five independent refutation lenses:** (1) vacuity, (2) red-demo integrity, (3) scope + D5 conformance,
(4) host hygiene + flake risk, (5) SIMPLIFY + empirical attack. Lenses 1–4 reasoned from the source;
**lens 5 executed** — two re-host experiments, three red demos and an instrumented tuple census, all
reverted. Where a read-only lens and lens 5 disagreed, lens 5's measurement decided; that happened once
(the host-`DataContext` finding, which lens 2 filed as a nit and lens 5 demonstrated as a green-on-broken).

**Measured gate at `17357e0` (orchestrator, this tree):** Debug `-t:Rebuild -v:n` → 0 Warning(s) /
0 Error(s), 6 genuine `csc.exe` invocations; Release identical. Suite run 1 → 2733 total / 1 failed
(the known `AssistantChatConcurrencyTests` intermittent, then 13/13 green isolated three times); run 2 →
2733 / 0 failed / 2732 passed / 1 skipped. Baseline 2723 / 0 failed. Chain 2723 → 2725 → 2727 → 2728 →
2730 → 2731 → 2732 → 2733 → 2733 (SIMPLIFY unchanged, as required). Net **+10** cases.

**Adjudicator's own verification:** re-read all ten changed test files, `BindingPathWalker.cs`,
`WpfStaHost.Run`/`Pump`, `AssistantView.xaml:545`–`:603`, `RunProgressPanel.xaml` (every `{Binding` site),
`SettingsView.xaml`'s six host sites, `ScheduledJobRow`, both spec documents, and all nine commit bodies;
re-derived the G1 byte-identity diff and the G3 tuple arithmetic independently.

---

## Headline verdict

**Batch 14 is sound, and the central risk did not materialise. No new fact is vacuous.** The trap the
batch spec names — a hardcoded root ViewModel type making every path check against the wrong type — was
avoided in all six parse facts; every one reflects the root off the hosting ViewModel property. No walk
dies early: lens 5's instrumented census measured 40 / 61 / 12 / 8 / 6 / 28 tuples against the six claimed
counts and every number matched exactly, with zero unresolved. Every default-valued-DP hazard has its
biting direction asserted (row B's two `False`s, row A's `Collapsed`, G3's pre-mutation `Collapsed`), and
lens 5 turned three of them red on demand by editing `src/`. The ten surfaces that could previously break
silently at 0 warnings now cannot — with one honest qualification: **the margin varies by nearly an order
of magnitude.** `AccountView` walks 42 distinct paths; `PluginsView` walks 4 distinct paths of which 2 are
its own anchors, so its sweep contributes exactly 2 unanchored distinct paths (the file self-discloses
"the weakest floor in the batch" but never states `distinct=4`). What the batch does **not** deliver is
the thing both specs say it delivers: the reflected root does not catch a re-host. That is the one
must-fix, and it is mostly a prose fix.

---

## Findings

| ID | Severity | Verdict | file:line | Defect | Lens(es) |
|----|----------|---------|-----------|--------|----------|
| D1 | **must-fix** | CONFIRMED | `GeneralViewParseTests.cs:49` (+4 siblings, `RunProgressPanelParseTests.cs:38`), `14-view-coverage-debt.impl.md:724` | The host-site `DataContext` binding is never read; the reflected root pins only the *property's type*. Six comments say "CHECKED, not assumed" and the impl spec says "it fails on a re-host". It does not. | 5 (executed), 3, 2 |
| D2 | should-fix | CONFIRMED | `ScheduledJobsRowTemplateTests.cs:225-226` (asserted `:208-211`) | The two `IsEnabled` paths are located by their **Command** path, and both fixture rows co-vary on all four row booleans — `StatusIsKnown` / `CanRunNow` / `OwnedByThisDevice` / `IsEnabled` are mutually interchangeable in either slot. | 1, 2, 3 |
| D3 | should-fix | CONFIRMED | `RunProgressPanelParseTests.cs:80-88` (floor `:36`) | Four anchors cover two of the six surfaces the batch spec named for G3; the floor is *exactly* the at-or-before-`:74` tuple count, so all ten post-Steps tuples are droppable without tripping anything. | 1, 3 |
| D4 | nit | CONFIRMED | `AccountViewParseTests.cs:80` | `E2EEOnboardingHosts_…` is the only new fact in the batch with no recorded red demo of any kind; two of its four assertions are `nameof`-tautological. | 2 |
| D5 | nit | CONFIRMED | `ScheduledJobsRowTemplateTests.cs:160`, `:268`; `RunProgressPanelParseTests.cs:121`, `:136` | `Pump()` sits inside a `try` whose `finally` marshals another bounded `Run(...)`; a wedged dispatcher makes the `finally` throw a second `TimeoutException` that **replaces** the first, discarding the message that names the real stage and doubling the wall clock. Pre-existing shape, count raised. | 4 |
| D6 | nit | CONFIRMED | commit `2e75d72` body vs `PluginsViewParseTests.cs:63`, `:69` | The red-demo failure sites are cited as `:58` / `:64`; the shipped file has them at `:63` / `:69` (uniform +5, consistent with the doc-comment parenthetical being written after the demo). Sole uncheckable citation in the batch. | 2 |
| D7 | nit | CONFIRMED | commits `512a9a2`, `2e75d72` | Two of five G4 commits silently corrected the impl spec's suggested injection point (`GeneralView.xaml:458`→`:459`, `PluginsView.xaml:26`→`:27`) while Providers and Optimize recorded theirs in a dedicated paragraph. | 2 |
| D8 | — | **REFUTED** | five G4 anchor sites | "The `[ContextType]` half of each anchor is undemonstrated." No input makes any test wrong; the filer concedes no scenario goes green. | 2 |

**Not adjudicated as findings** (recorded here so they are not mistaken for silence): lens 3's status note
that the docs commit is absent — real, but a batch-completion item, not a defect; and lens 4's cost
estimate (~1–3 s added to the serial group, ~3,600 lines of BAML), which is an estimate, not a measurement.

---

## D1 — must-fix · the host-site `DataContext` is never checked, and both specs claim it is

**Files.** `GeneralViewParseTests.cs:49-55`, `AccountViewParseTests.cs:41-47`,
`ProvidersViewParseTests.cs:42-48`, `OptimizeViewParseTests.cs:48-54`, `PluginsViewParseTests.cs:45-51`,
`RunProgressPanelParseTests.cs:38-71`. `SettingsAssistantViewParseTests.cs:58` inherits the shape from
Batch 13 and is out of this range. Prose: `14-view-coverage-debt.impl.md:724`,
`14-view-coverage-debt.md:99-102`.

**The defect, split in two because the fixes are different.**

*(a) A false claim of coverage — fix this first, it is nearly free.* Each fact reads a **ViewModel
property's type** and asserts it:

```csharp
var root = typeof(SettingsViewModel)
    .GetProperty(nameof(SettingsViewModel.GeneralVm), BindingFlags.Public | BindingFlags.Instance)!
    .PropertyType;
Assert.Equal(typeof(GeneralSettingsViewModel), root);
```

Nothing in any of the six files opens `SettingsView.xaml` or `AssistantView.xaml`. The comment's leading
clause — "The root DataContext is CHECKED, not assumed" — is false for the half it names; only its
trailing clause ("sound while that property still has this type") is accurate. The impl spec is worse and
unqualified: *"it fails on a re-host instead of silently walking the wrong type."* The batch spec
(`:99-102`) says a re-host "then fails the test instead of quietly checking every path against the wrong
ViewModel, **which is the one way this whole technique can go green while proving nothing**". The shipped
mitigation does not deliver the property the spec built it for. **The docs commit has not landed** —
`00-OVERVIEW.md` was last touched at `2266bf7` and none of Appendix A's edits are in — so this claim is
about to propagate.

*(b) An uncovered surface.* A `DataContext` re-host is invisible to every test in the repo.

**Concrete failure scenario.** Change `src/Pia.Wpf/Views/SettingsView.xaml:123` from
`<localSettings:GeneralView DataContext="{Binding GeneralVm}">` to `{Binding AccountVm}`. All 40 of
GeneralView's binding paths are dead at runtime — the tab renders empty controls — at 0 warnings.
`GeneralViewParseTests` still reflects `GeneralVm`, still gets `GeneralSettingsViewModel`, and stays
green. Identically for the other four hosts (`:84`, `:97`, `:136`, `:149`) and for
`AssistantView.xaml:51` `{Binding ActiveRunProgress}` → any other property, which kills all 28 panel
paths while G3 stays green.

**Evidence (CONFIRMED — read by the adjudicator *and* executed by lens 5, not laundered).** Read: the six
facts touch only `GetProperty(...).PropertyType` and `new <View>()`; `grep` over `tests/` for
`SettingsView` returns only these files' own doc-comment references — no test constructs
`Pia.Views.SettingsView`; `grep` for `GeneralVm|AccountVm|PluginsVm|OptimizeVm|ProvidersVm|
ActiveRunProgress` across `tests/` outside the new files returns three hits, none of which reads markup.
There is therefore no code path in the suite that can observe the host binding. Executed (lens 5, both
reverted, `git diff --stat` empty after each): `SettingsView.xaml:123` `GeneralVm`→`AccountVm`,
`-t:Rebuild`, `--filter-namespace "Pia.Tests.Views"` → **16/16 PASSED**; `AssistantView.xaml:51`
`ActiveRunProgress`→`VoiceMode` → **16/16 PASSED**.

**Why must-fix rather than should-fix.** Not because the surface is uncovered — a re-host blanks an entire
settings tab, so the *consequence* is loud even though the *test* is silent. It is must-fix because a
future builder reading `GeneralViewParseTests.cs:49` will believe the host site is guarded and will not
add a guard, and because the spec sentence that is about to land in `00-OVERVIEW.md` states the opposite
of what shipped. What the reflection *does* protect is real and should be kept: `nameof` makes a **rename**
of the VM property a compile error, and `Assert.Equal(typeof(...), root)` catches a **retype**.

**Recommended fix, in order.**

1. **Prose, mandatory.** Rewrite the leading clause in all six comments to what is true, e.g.
   `// The root type is CHECKED, not assumed — but only the TYPE: nothing here reads SettingsView.xaml,`
   `// so a re-host of this view onto a different property is NOT caught. See D1.`
   Correct `14-view-coverage-debt.impl.md:724` and do not carry `14-view-coverage-debt.md:99-102`'s
   "a future re-host then fails the test" into the docs commit.
2. **Coverage, cheap but verify before assuming it is one line.** The walker already resolves
   `FrameworkElement.DataContextProperty` first and yields
   `GeneralView.DataContext=GeneralVm [SettingsViewModel] ok`, so `BindingPathWalker.BoundPath(child,
   FrameworkElement.DataContextProperty)` is the right primitive and the anchor form is right. Two checks
   the fix pass must do rather than assume: **`SettingsView` has never been constructed in any test** —
   verify `new Pia.Views.SettingsView()` parses on the host before building a fact on it (the five
   children parse standalone, but the parent may reach App-level resources); and note the walk **recurses
   into all six children**, so a `Describe`-based fact needs its own floor and must not be sold as
   replacing the five per-view facts. A third check the `FindLogical` shape below implies: the six children
   must be reachable by a **logical** walk from `SettingsView`. If each host site sits inside a
   `TabItem.Content` they are unconditionally logical children (the same argument the batch made for
   `Expander`), but if any sits behind a `ContentTemplate` then `FindLogical<GeneralView>()` yields nothing
   and `.Single()` throws — check the six sites before writing the lookup. The narrower shape avoids the
   floor and recursion problems: one fact that constructs
   `SettingsView`, `FindLogical<GeneralView>().Single()` etc., and asserts
   `BoundPath(child, FrameworkElement.DataContextProperty) == nameof(SettingsViewModel.GeneralVm)` for
   each of the six.
3. For the panel half: `AssistantViewParseTests` has **no** path-walk fact today (its four facts are a
   composer-hint render, a localization-key sweep and two `RunProgressPanel` render facts that construct
   the panel directly). But `ParsedView_HasNoUnresolvedLocalizationKeys` (`:125`) already builds
   `new AssistantView { DataContext = vm }` on the host, so the guard is one added assertion there — or a
   VM-free three-liner: `new AssistantView()` → `FindLogical<RunProgressPanel>().Single()` →
   `Assert.Equal("ActiveRunProgress", BindingPathWalker.PathOf(panel, FrameworkElement.DataContextProperty))`.
4. Give the new fact its own red demo, since D1 exists precisely because this class of claim went
   undemonstrated.

---

## D2 — should-fix · the two row `IsEnabled` paths are not discriminated from each other

**File.** `tests/Pia.Wpf.Tests/Views/ScheduledJobsRowTemplateTests.cs:225-226` (lookups), `:208-211`
(assertions), fixtures `:82-86` and `:102-106`.

**Defect.** `JobsRowTemplate_BindsEveryItemScopedPath_AcrossTwoRowsThatDiscriminate` names ten
item-scoped paths and discriminates eight. Paths 9 and 10 are not read by their declared `IsEnabled`
binding path — the toggle and run-now buttons are located by their **Command** path
(`"DataContext.ToggleEnabledCommand"` / `"DataContext.RunNowCommand"`) and then `IsEnabled` is read off
them. Both fixture rows co-vary perfectly: `RowA` sets `StatusIsKnown` / `IsEnabled` / `OwnedByThisDevice`
all `true` (so `CanRunNow => OwnedByThisDevice && StatusIsKnown` is also `true`), `RowB` sets all of them
`false`. Any of the four row booleans is therefore interchangeable with any other, in either slot. Commit
`52f51a3` claims all ten paths are "each read by its declared binding PATH via a local `PathOf()` helper,
never by index or Content/Text (hazard 9)" — true for eight.

**Concrete failure scenario.** Change `src/Pia.Wpf/Views/SettingsViews/AssistantView.xaml:587` from
`IsEnabled="{Binding StatusIsKnown}"` to `IsEnabled="{Binding CanRunNow}"` (or swap `:587`↔`:592`, or point
either at `OwnedByThisDevice` or `IsEnabled`). Row A reads `True`, row B reads `False`, both `.Single()`
lookups still find their buttons by Command path — **both facts stay green at 0 warnings.** Behaviour
lost: the Enable/Disable toggle becomes gated on device ownership, so a second device can no longer toggle
a job whose status is known, contradicting `ScheduledJobRow`'s own doc comment
(`ScheduledJobsSettingsViewModel.cs:485-489`) that ownership gates *run now* specifically. Symmetrically,
`:592` → `{Binding StatusIsKnown}` re-enables Run now for foreign jobs. Honest scope: this is a UX
regression, not a safety hole — `ToggleEnabledAsync` early-returns on `!row.StatusIsKnown` and
`RunNowAsync` gets `NotOwner` back from the runner, so the button is a courtesy and the service call is
the guardrail.

**Evidence (CONFIRMED, read).** Read `AssistantView.xaml:545-603` — `:587` is
`IsEnabled="{Binding StatusIsKnown}"`, `:592` is `IsEnabled="{Binding CanRunNow}"`, and the four buttons
carry `Command="{Binding DataContext.<X>Command, RelativeSource={RelativeSource AncestorType=ItemsControl}}"`.
Read `ScheduledJobRow` (`ScheduledJobsSettingsViewModel.cs:448-493`): `CanRunNow => OwnedByThisDevice &&
StatusIsKnown`. Read the fixtures: `RowA` all-true, `RowB` all-false. The *deletion* case is separately
covered — lens 5 deleted `:592`'s `IsEnabled` and got a precise red at `:211` with `:210` staying green,
which also rules out `ICommandSource` coercion as the source of the `false` (all four are `[RelayCommand]`
with no `CanExecute` predicate). Only the *swap* is uncovered.

**Recommended fix — two lines in `Observe`, using the helper already imported, no third fixture row.**
Extend the returned tuple with the two paths and assert:

```csharp
Assert.Equal("StatusIsKnown", BindingPathWalker.PathOf(toggle, UIElement.IsEnabledProperty));
Assert.Equal("CanRunNow",     BindingPathWalker.PathOf(runNow, UIElement.IsEnabledProperty));
```

Not affected, checked individually: paths 1–6 are keyed by `PathOf(tb, TextBlock.TextProperty)` inside
`.Single(...)`; path 7 is keyed by `PathOf(tb, UIElement.VisibilityProperty) == "OwnedByThisDevice"`;
path 8's element is keyed by Command path and its two `Content` values are distinct strings. Renaming or
swapping any of those makes `.Single()` throw.

---

## D3 — should-fix · G3's anchors cover two of the six surfaces the batch spec named

**File.** `tests/Pia.Wpf.Tests/Views/RunProgressPanelParseTests.cs:80-88`; floor at `:36`.

**Defect.** The batch spec (`14-view-coverage-debt.md:74-75`) names six surfaces for G3 to pin: *the branch
line, the publish offer, the result note, the ledger strip, the state chip, the activity line.* The shipped
anchors are `=OutputBranchNote `, `=HasOutputBranch `, `=LedgerSummary `, `=Children ` — branch line and
ledger strip. All six are checked **today** by the zero-`UNRESOLVED` sweep, so this is not a present miss;
it is a future-drift hole. The floor makes it a real one: `MinimumBoundPaths = 18` is *exactly* the number
of walkable tuples at or before `:74` (`State`×3, `TruncationNote`, `IsTruncated`, `ContinueCommand`,
`CanContinue`, `PublishCommand`, `CanPublish`, `LedgerSummary`, `CurrentActivity`, `HasCurrentActivity`,
`CanPublish`, `PublishNote`×2, `OutputBranchNote`, `HasOutputBranch`, `Steps` = 18 of the measured 28), so
the floor gives **zero** protection to anything from the Steps list onward.

**Concrete failure scenario (two, same defect).** *(i)* Extract the timeline block
(`RunProgressPanel.xaml:116-168`) into its own templated control, or wrap its content in a
`ContentControl` + `ContentTemplate`. The walk loses 5 tuples (`TimelineNote`, `IsTimelineTruncated`,
`HasNoTimeline`, `HasTimelineReadError`, `Timeline`) → 23 ≥ 18 → floor green; all four anchors intact;
sweep empty → **fact green**, with those five paths now covered by nothing and a subsequent typo in any of
them invisible again — the exact state G3 exists to end. *(ii)* Wrap the header's left `StackPanel`
(`:14-28`, 5 tuples), the Continue button (`:33`, `:35`) and the activity line (`:50`, `:53`) into a
custom control → 19 ≥ 18, `LedgerSummary` at `:44` survives, all anchors intact → green, with the state
chip and the activity line uncovered.

**Sub-defect, folded in as a nit.** The comment at `:84-87` says the `=Children ` anchor "proves the walk
reached the SECOND Expander's content and did not stop at the first" and that losing it "would silently
lose tuples 19-28". The two `Expander`s are **siblings** (`:116`–`:168` and `:177`–), not nested. The
*mechanism* the comment names — `Expander` content ceasing to be a logical child — would take out both, so
the anchor does guard it; but the scope claim is overstated by half: reaching `Children` (tuples 25-28)
does not prove tuples 19-24 were walked.

**Evidence (CONFIRMED, read + arithmetic re-derived by the adjudicator).** Enumerated every `{Binding` site
in `RunProgressPanel.xaml` and classified it: 18 walkable tuples at/before `:74`, 6 in the first
`Expander`'s content, 4 in the second's, 26 inside four `ItemTemplate`s — 28 walkable, matching both the
commit body and lens 5's instrumented census (`tuples=28 distinct=23 unresolved=0`). Confirmed the
`Expander` sibling structure by reading the markup. Confirmed the impl spec §7 prescribed exactly these
four anchors and did not list the narrowing among §0's thirteen corrections — the builder conformed.

**Recommended fix — four one-line anchors, and DO NOT raise the floor.** The floor is *correct* at 18;
raising it to 26 would create exactly the edit-churn D2 (the decision, not the finding) deliberately
avoids. Add, next to the existing four:

```csharp
Assert.Contains(bindings, b => b.Contains("=TruncationNote "));   // the state chip's region (:24)
Assert.Contains(bindings, b => b.Contains("=CurrentActivity "));  // the activity line (:50)
Assert.Contains(bindings, b => b.Contains("=PublishCommand "));   // the publish offer (:40)
Assert.Contains(bindings, b => b.Contains("=TimelineNote "));     // the first Expander's content (:120)
```

All four are **single-occurrence**, per the impl spec's own anchor discipline. `=State ` (`:17`/`:18`/`:21`)
and `=CanPublish ` (`:42`/`:63`) would also work for the stated purpose — these anchors guard a *region*
against extraction, not a path — but each is bound twice or more, so an anchor on either survives removal of
one occurrence; the single-occurrence neighbours above are the better pick for the same regions.

and correct the `:84-87` comment to claim only the second `Expander`'s tuples. The same shape is milder but
present in `GeneralViewParseTests.cs` (both anchors live in the fourth of four sibling `TabItem`s, leaving
~33 tuples in tabs 1–3 against 14 units of slack) and `AccountViewParseTests.cs` (one anchor, inside the
nested E2EE view, 21 units of slack over 46 own tuples). One extra anchor per file from an unanchored
region closes those too; both are nits relative to G3.

---

## D4 — nit · the duck-type fact is the one new fact with no red demo

**File.** `tests/Pia.Wpf.Tests/Views/AccountViewParseTests.cs:80`.

**Defect.** Commit `165e085` records exactly one red — the `E2EEOnboardingView.xaml:157`
`RecoveryCodeInput`→`RecoveryCodeInputX` rename — and that reddens Fact 1 only. Fact 2
(`E2EEOnboardingHosts_AllExposeAnOnboardingViewModelOfTheSameType`) has no demonstrated red. Two of its
four assertions are near-tautological: `GetProperty(nameof(AccountSettingsViewModel.OnboardingViewModel))`
cannot return null for a rename because `nameof` stops compiling first, so `Assert.NotNull` at `:87`/`:88`
can only fire on a property→field or instance→static conversion. The commit body itself says the
degeneracy concern was closed "via advisor review" — by reasoning, never demonstrated.

**Concrete failure scenario.** Retype `AccountSettingsViewModel.cs:28` from
`public E2EEOnboardingViewModel OnboardingViewModel { get; }` to `public object OnboardingViewModel { get; }`.
Nothing in the record shows `Assert.Equal(typeof(E2EEOnboardingViewModel), …)` firing on that.

**Why nit, not should-fix (demotion from lens 2, reasoning stated).** The fact is **non-vacuous by
reading**: `:95` pins one side to a concrete `typeof(E2EEOnboardingViewModel)` before `:96` compares the
two, which is a strictly stronger discriminator than the runtime assertion it replaced, and three lenses
independently examined it and reached the same conclusion (lens 3 called it "over-delivers"). The gap is
evidential, not behavioural, and acceptance §10.1's wording — "every new **XAML** fact" — does not bite a
pure-reflection fact. **Fix:** one two-minute demo (retype the property to `object`, rebuild, observe
`:95` fail, revert) recorded in the docs commit.

---

## D5 — nit · `Pump()` inside a `try` whose `finally` marshals another `Run(...)`

**Files.** `ScheduledJobsRowTemplateTests.cs:160` (finally `:165-170`), `:268` (finally `:272-275`);
`RunProgressPanelParseTests.cs:121` and `:136` (finally `:145-150`).

**Defect.** An exception thrown from a `finally` **replaces** the in-flight one. If the host dispatcher
wedges — the defect class Batch 13 exists for, and the one Appendix C's Q4 tells the next reader to triage
first — `Pump()` throws `TimeoutException("The WPF STA host's queue did not drain to SystemIdle within
60s. Suspect work that re-queues itself at a priority above SystemIdle…")` (`WpfStaHost.cs:246-250`);
control enters the `finally`, which marshals `Dispose()` onto the same wedged dispatcher, burns a second
60 s and throws `TimeoutException("The WPF STA host did not finish a marshaled test body within 60s…")`
(`:184-188`). xunit reports the second. Net: **120 s instead of 60 s**, and the message naming the actual
stage is discarded.

**Evidence (CONFIRMED, read).** Both throw sites read in full; C# `finally` replacement semantics.
**Pre-existing shape, not introduced here** — `AssistantViewParseTests.cs:87`, `:102`, `:148`, `:345`,
`:362` already have it, while `:244` and `SettingsAssistantViewParseTests.cs:120` pump outside a `try`, so
it is not a universal house style either. Batch 14 takes it from 5 sites to 9. **Fix, if anyone bothers:**
move `Pump()` ahead of the `try`, or wrap the `finally` body so a second timeout does not mask the first.
Not worth a revert.

---

## D6 — nit · Plugins commit cites red-demo lines that are not in the shipped file

Commit `2e75d72` cites `PluginsViewParseTests.cs:58` (anchor) and `:64` (sweep); the committed file has
them at `:63` and `:69`. **CONFIRMED, executed** (`sed`/`grep` on the shipped file and on the commit body).
Uniform +5, exactly the length of the doc-comment parenthetical at `:22-26` that the commit says was added
after the demo — so this corroborates a real run rather than suggesting a fabricated one. Every other
citation in the batch lands exactly (Optimize `:67`/`:73`, Providers `:60`/`:66`, Personas `:89`/`:99`, G1's
`:89` at `86934c9`). The only defect is that it is the one citation a reviewer cannot check against the
shipped file. **Fix:** note the offset in the docs commit; nothing in `tests/` to change.

## D7 — nit · inconsistent disclosure of spec divergence

Providers recorded its `:85`→`:86` correction and Optimize its `:20`→`:21` in a dedicated "spec-prose
divergences" paragraph. General silently injected at `GeneralView.xaml:459` where the spec suggested `:458`,
and Plugins at `PluginsView.xaml:27` where it suggested `:26`. **CONFIRMED, executed:** `sed -n '458p'
GeneralView.xaml` and `sed -n '26p' PluginsView.xaml` are both the `Content="{loc:Str …}"` line, which is
out of walker scope — so moving one line down was *correct* in both cases, and Plugins' commit even records
a *different* off-by-a-few (its `:127`→`:130`/`:131` template range) while omitting its own. No test is
wrong; a reader reconciling impl spec §8 against the record finds two of five silently disagreeing with no
way to tell a deliberate correction from a typo. **Fix:** one sentence in the docs commit.

---

## REFUTED claims — recorded as results, with the evidence that killed each

**D8 · "The `[ContextType]` half of each G4 anchor is undemonstrated" (lens 2) — REFUTED as a defect.**
The filer's own entry states *"Concrete failure scenario: none that goes green"*: by the walker's W2
cascade a null re-root sets `Resolves=false` and stamps descendants `[unknown]`, so both the anchor and the
sweep fire. A finding with no input that makes the test wrong is not a defect. Recorded as an evidential
note: the claim that the two-halves assertion "proves the walk followed the re-root" rests on reading
`BindingPathWalker.Walk:40-47`, not on a demo.

**"A hardcoded root ViewModel type makes every path check against the wrong type" — the batch spec's named
'one way this whole technique can go green while proving nothing' — REFUTED.** All six parse facts reflect
the root off the hosting property and assert the concrete type. Zero hardcodes. Verified by reading all six
files and confirming the six `SettingsView.xaml` host sites (`:84/:97/:110/:123/:136/:149`) and
`AssistantView.xaml:51` match today's markup. (D1 is the *weaker cousin* of this, not this.)

**"A walk dies early at a templated container and reports 'no unresolved paths' over almost nothing" —
REFUTED, by measurement.** Lens 5 instrumented the walker with a temporary audit fact (since deleted; tree
clean) and measured `General 40 / Account 61 / Providers 12 / Optimize 8 / Plugins 6 / RunPanel 28`, every
one matching its claimed count, all with `unresolved=0`. Lenses 1 and 3 independently re-derived the same
counts statically from the markup. Not one number is inflated.

**"A DependencyProperty sitting at its default is being read as evidence" — REFUTED.** Every vacuous half
is asserted *and labelled as vacuous in a comment* (`ScheduledJobsRowTemplateTests.cs:192-195`, `:204-207`;
`RunProgressPanelParseTests.cs:99-101`), and the biting direction is present in every case. Lens 5 proved
it empirically: deleting `Visibility="{Binding HasOutputBranch, …}"` from `RunProgressPanel.xaml:71` →
**RED** at `:152` (`Expected: Collapsed / Actual: Visible`); deleting `IsEnabled="{Binding CanRunNow}"`
from `AssistantView.xaml:592` → **RED** at `:211`, with the sibling assertion at `:210` staying green.

**"The `UNRESOLVED` sweep is vacuous" — REFUTED, executed on the weakest surface.** Lens 5 renamed a
*non-anchor* path (`IsLoading`→`IsLoadingX` at `PluginsView.xaml:49` and `:55`) so only the sweep could
catch it → **RED** at `PluginsViewParseTests.cs:69`, naming both sites.

**"G1 is not a pure move / an assertion was edited / the walker was widened" — REFUTED, executed by the
adjudicator.** `git show 0c5cb42:…/SettingsAssistantViewParseTests.cs | sed -n '135,231p' | sed
's/private static/internal static/'` diffed against `BindingPathWalker.cs:20-116` returns **IDENTICAL** —
97 lines, whitespace included, covering `Walk`, `BoundPath`, `TargetsDataContext`, `ResolvePathType`,
`FindLogical<T>`, including W2's known-false comment. No one-character widening of `TargetsDataContext`
slipped in under the suite-total invariance. `Describe`'s format string is character-identical to the
inline projection it replaced.

**"D2 (per-view floor, never a shared one) was violated" — REFUTED.** Seven `private const int
MinimumBoundPaths` (18/26/40/8/5/4/20), every one in its own calling class; `BindingPathWalker.cs` contains
no `const`, no floor, no threshold. Only machinery was shared.

**"Elements are identified by index or by `Content`/`Text`" (hazard 9) — REFUTED.** Every lookup in G2 and
G3 is by binding path inside `.Single(...)`, including `Build()`'s jobs-`ItemsControl` discriminator
(`ItemsSource` path `== "Jobs"`, which correctly avoids the six-plus `ComboBox : ItemsControl` and the
`:183` grants control with the same ancestor-command shape).

**"G2's command fact is satisfiable without the ancestor technique" — REFUTED.** `ReferenceEquals(b.Command,
expected)` against the real `vm.<X>Command`, with `expected is not null &&` closing the
`ReferenceEquals(null, null)` hole; `Expected(...)` returns null for any unrecognised path, so a renamed
path, a deleted binding or a wrong `AncestorType` all turn it red. `Assert.Equal(4, probes.Length)` plus
four named `Assert.Contains` prevent silent shrinkage, and `CommandParameter` identity is asserted **and
explicitly disclaimed as non-evidence** for the ancestor technique.

**"G1's 'no assertion reads the `ok` literal' claim is wrong" — REFUTED, and the impl spec is wrong
instead.** No assertion in `SettingsAssistantViewParseTests.cs` reads `"ok"`; anchors match `=Path ` /
`[ContextType]` substrings that stop before it and the sweep filters `EndsWith("UNRESOLVED")`. The impl
spec's "60-second sanity check" at `:396-399` is a genuine spec inaccuracy; the substituted
inverted-ternary probe the builder used is strictly stronger.

**"D5 (the decision — PersonasView and E2EEOnboardingView) was dropped" — REFUTED.** Both halves landed and
(ii) over-delivers. `SettingsAssistantViewParseTests.cs:89` anchors
`=AddPersonaCommand [PersonaSettingsViewModel]` with its own red demo; `AccountViewParseTests.cs:95-96`
pins the concrete type before comparing, defeating the both-retyped-to-`object` case the promise as written
would have allowed. `PersonasView`'s two walker-visible paths and `E2EEOnboardingView`'s 15 are all inside
green sweeps.

**"This batch raises flake risk in the mechanism the 2026-08-01 record measured" — REFUTED.** That record is
specifically about *frame-pushing* facts. `grep` for `PushFrame|DoEvents|Dispatcher\.|InvokeAsync|
Thread.Sleep|Task.Delay|Application.Current` across all eight new files returns **empty**; zero new frame
pushes, zero nested loops, zero layout passes (`grep` for `ApplyTemplate|.Measure(|.Arrange(|UpdateLayout|
new Window|HwndSource` returns exactly one hit, the word `ApplyTemplate` inside a comment saying there
isn't one). All four new `Pump()` calls follow the bounded `Run(mutate) → Pump() → Run(observe)` idiom from
the test thread. The relevant variable is frame pushes 0→0, not fact count 6→16.

**"Something outside `tests/` moved, or a runner setting changed" — REFUTED.** `git diff --stat
0c5cb42..HEAD` is ten files, all under `tests/Pia.Wpf.Tests/Views/`. No `xunit.runner.json`, no `.csproj`,
no `AssemblyInfo`, no `CollectionBehavior`/`maxParallelThreads`. `[Collection("WpfApplicationStatic")]` is
on all seven new test classes; `BindingPathWalker` is correctly uncollected. All eight new/changed files are
CRLF (CR count == LF count, spot-verified).

**"A gate statement or a suite total is overclaimed" — REFUTED.** Every commit's Debug/Release
`0 Warning(s)/0 Error(s)` + 6-`csc.exe` claim and every post-commit total match the orchestrator's
independent measurement and the declared chain. New `[Fact]` counts sum to exactly 10 (Account 2, General 1,
Optimize 1, Plugins 1, Providers 1, RunProgressPanel 2, ScheduledJobs 2), matching 2723 → 2733; the two
pre-existing files' fact counts are unchanged from `0c5cb42`, so G1's "+0" is right.

---

## What this review did NOT cover

- **Only lens 5 executed anything.** Lenses 1–4 reasoned from the source. Their arithmetic was
  independently re-derived here for D3 and their code claims re-read for D1/D2/D5, but no read-only lens
  ran a test. D4, D6 and D7 are record-keeping findings verified against files, not against a run.
- **The adjudicator did not re-run the D1 experiment.** Read-verification is complete (no code path in the
  suite can observe a host `DataContext` binding) and lens 5 executed it twice; re-running would have
  dirtied `src/` for no new information.
- **The templated regions are reachable by no technique in this batch** (impl spec Q2). `RunProgressPanel`
  has 26 item-scoped bindings inside four `ItemTemplate`s (Steps `:75-107`, Timeline `:132-165`, Children
  `:185-253`, and a child-timeline `ItemsControl` at `:220` nested inside the children template). Nothing
  in Batch 14 sees them.
- **`Style.Triggers` / `DataTrigger` conditions are permanently invisible** to this technique: they are not
  local values on the element, so `GetLocalValueEnumerator` never yields them. Named per view in the files
  (General `:399`/`:418`/`:431`; Account's four `<Condition Binding="…">` at `:86`/`:87`/`:230`/`:231`;
  Optimize `:56`; six in `E2EEOnboardingView`). `x:Name="LoginPasswordBox"` (`AccountView.xaml:51`) is
  code-behind-driven and has no `Binding` at all.
- **Coverage strength is very uneven and was not scored.** `PluginsView` walks 6 tuples / **4 distinct
  paths**, two of which are its own anchors — so its sweep contributes 2 unanchored distinct paths. Compare
  `AccountView` at 42 distinct. "Ten surfaces can no longer fail silently" is true with very different
  margins per surface.
- **`ResolvePathType` does not check settability**, so a `Mode=TwoWay` binding onto a get-only property
  resolves "ok" and still fails at runtime. Inherited walker limitation, not introduced here; no G4 doc
  claims persistence coverage.
- **The fifth `AncestorType=ItemsControl` command binding** — the tool-permissions grants row's
  `DataContext.RevokeCommand` at `AssistantView.xaml:221` (W12) — remains uncovered by any XAML test, as
  the batch discloses.
- **No wall-clock measurement of the added cost.** `tests/Pia.Wpf.Tests/TestResults/` holds only three
  stale April coverage files; lens 4's ~1–3 s figure is an estimate.
- **The docs commit is absent.** `00-OVERVIEW.md` was last touched at `2266bf7` and still lists Batch 14 as
  future Rank-2 work (`:570`, `:587`); none of Appendix A's fifteen edits has landed, so acceptance clauses
  4/5/6 of both specs are open. Three numbers it needs, **re-derived by this adjudication** (lens 3 reported
  them; `find`/`ls` confirmed each): **7 of 21** views now have a parse test of their own (chat
  `AssistantView`, settings `AssistantView`, General, Account, Providers, Optimize, Plugins), **9 of 21** are
  walked (+`PersonasView`, +`E2EEOnboardingView`, both logical children), and `Views/SettingsViews/` is
  complete at **8 of 8** — both `PersonasView.xaml` and `E2EEOnboardingView.xaml` do live in
  `SettingsViews/`, which the 8-of-8 claim depends on. **State the denominator's basis:** 21 = the 13
  `Views/*.xaml` plus the 8 `Views/SettingsViews/*.xaml`. It is *not* all view markup under `Views/` —
  `find src/Pia.Wpf/Views -name "*.xaml"` returns **47**, the remaining 26 being `Dialogs/` (13),
  `Dialogs/Overlay/` (3), `WizardSteps/` (7) and `Views/Controls/` (3), none of which this batch touched or
  counted. D1's prose corrections must land in that commit.

---

## Tally

- **Findings filed across five lenses: 13** (lens 1: 2, lens 2: 6, lens 3: 3, lens 4: 1, lens 5: 1).
- **Distinct defects: 8.** The duplication was expected and is concentrated on the two strongest findings:
  D1 filed three times (lens 5 must-fix / lens 3 should-fix / lens 2 nit, plus lens 1 considered and
  chose not to file), D2 filed three times (lenses 1, 2, 3), D3 filed twice (lenses 1 and 3, naming
  different droppable regions of the same panel). **13 filings ≠ 8 defects — do not report the filing
  count as the defect count.**
- **CONFIRMED: 7** (D1 must-fix, D2 + D3 should-fix, D4–D7 nits).
- **REFUTED: 1** (D8, no scenario goes green — refuted on the filer's own concession, re-checked against
  `BindingPathWalker.Walk`).
- **Refutation attempts that came back clean: 12** major lenses of attack, listed above with the evidence
  that killed each. Every one is a result, not silence.
- **Severity downgrades made by the adjudicator, with reasons stated in-section:** lens 2's D4 should-fix →
  nit (the fact is non-vacuous by reading; the gap is evidential); lens 2's D8 nit → REFUTED (no failure
  scenario); lens 3's D3 "pins 2 of 6 surfaces" reframed from a spec-conformance violation to a
  future-drift coverage gap (the spec asked for one fact covering all six, which the sweep delivers today,
  and the impl spec prescribed exactly the four shipped anchors).
- **Severity upgrade:** D1 from lens 2's nit / lens 3's should-fix to **must-fix**, decided by lens 5's
  executed 16/16-green-on-broken plus the two false prose claims that are about to enter the docs commit.

### The must-fix list a fix pass must close

1. **D1 only.** In order: (a) correct the leading clause of the six "The root DataContext is CHECKED, not
   assumed" comments and the false sentences at `14-view-coverage-debt.impl.md:724` and
   `14-view-coverage-debt.md:99-102`, before the docs commit lands; (b) add a host-binding guard —
   `BoundPath(child, FrameworkElement.DataContextProperty)` against `nameof(SettingsViewModel.<X>Vm)` for
   the six settings hosts, and `PathOf(panel, FrameworkElement.DataContextProperty) == "ActiveRunProgress"`
   for the run panel — after verifying `new Pia.Views.SettingsView()` parses on the host; (c) give it a red
   demo.

D2 and D3 are cheap (two lines and four lines respectively) and worth doing in the same pass — D2
especially, before its shape is copied into the next row-template test — but neither blocks the batch.
