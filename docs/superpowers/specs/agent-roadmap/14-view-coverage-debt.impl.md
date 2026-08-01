# Batch 14 — View-coverage debt · IMPLEMENTATION SPEC

Companion to [`14-view-coverage-debt.md`](14-view-coverage-debt.md). Written 2026-08-01 against the as-built
code at `2266bf7` on `feature/agent-run-spine`, from five grounding passes that read the real files and one
that **executed a build and a test run** (D1). Where this file and the batch file disagree, **this file wins,
and §0 says why for every case.**

This batch writes tests. It changes no production behaviour, ships no string, no control, no log line and no
persisted anything. Its whole value is that five surfaces which can fail *silently at 0 warnings* stop being
able to. Read §2 (hazards) before every group and §3 (the gate) at every commit.

---

## 0. Where the spec was wrong

Thirteen disagreements. Each is *spec claim → code at `2266bf7` → what this plan does about it*. **Do not
quietly adapt any of these; the corrected form is the instruction.**

| # | Spec claim | Code at `2266bf7` | What this plan does |
|---|---|---|---|
| **W1** | G1 (`:26`): the four helpers are at `SettingsAssistantViewParseTests.cs:148`–`:219`. | The four **bodies** are exact (`Walk` signature opens at `:148`, `ResolvePathType`'s closing brace is `:219`). But `Walk`'s XML doc comment runs `:135`–`:147`, and **three of G1's four "load-bearing properties" are documented there and nowhere else**. G1 also says "carry them verbatim, *with their comments*" — the range as written drops exactly those comments. | **Move `:135`–`:219`, not `:148`–`:219`.** See §5. |
| **W2** | G1 (`:38`–`:39`), echoed by the code's own doc at `:139`–`:141`: "a null context makes descendants **unknown**, not **failed**, so one bad re-root is one finding rather than a cascade." | **False as implemented.** `:158`–`:160` and `:175`–`:176` both set `Resolves = false` when `contextType is null`. The caller renders `Resolves ? "ok" : "UNRESOLVED"` (`:71`) and filters `EndsWith("UNRESOLVED")` (`:92`). "unknown" exists only as the `ContextType` **label string** (`contextType?.Name ?? "unknown"`), never in the boolean the assertion reads. A null context therefore *does* cascade. Latent today only because no re-root currently fails. | **Carry the code verbatim, false comment included. Do NOT introduce a tri-state `Resolves`.** Making the comment true is a behaviour change and destroys G1's own proof ("facts stay green, no assertion edited"). Logged as open question **Q1** for a later batch. |
| **W3** | G1 names **four** helpers. | There is a **fifth**, `FindLogical<T>` (`:221`–`:231`), used by the second fact at `:117` — and it is **already duplicated byte-identically** at `AssistantViewParseTests.cs:400`–`:408`. G3's render fact and G4's anchor lookups both need it. | **Move five, not four**, and delete the duplicate in `AssistantViewParseTests`. If G1 moves only four, G3 makes a **third** copy. Decision **D4**. |
| **W4** | G2 (`:46`): the template is `AssistantView.xaml:545`–`:601`. | Off at both ends. `<ItemsControl>` `:545`; `<ItemsControl.ItemTemplate>` `:546`; `<DataTemplate>` `:547`; `</DataTemplate>` `:601`; `</ItemsControl.ItemTemplate>` `:602`; `</ItemsControl>` `:603`. | Real ranges: **`DataTemplate` element `:547`–`:601`, whole `ItemsControl` `:545`–`:603`.** Cosmetic, but the red-demo line numbers in §6 depend on getting the frame right. |
| **W5** | G2 (`:47`) cites a precedent named `RunProgressPanel_RendersATimelineRow_…WithItsPersonaAvatar`. | No such fact. Two **differently-prefixed** ones: `RunProgressPanel_RendersATimelineRow_WithItsStepOutcomeAndDecision` (`AssistantViewParseTests.cs:222`) and `RunProgressPanel_RendersAStepRow_WithItsPersonaAvatar` (`:321`). | Both names corrected throughout this file. Copy the *shapes* at `:223`–`:304` and `:322`–`:394`. |
| **W6** | D1 (`:115`–`:116`): "the *parsed* panel's `ItemsControl` has `ItemsSource="{Binding Jobs}"` bound, and adding to `Items` while `ItemsSource` is set **throws** — so (a) probably has to mean a *throwaway*". | **Measured false.** With no DataContext on the view the `{Binding Jobs}` never produces a value, `ItemsSource` stays `null`, and `Items.Add(...)` on the parsed control **succeeds**. The `InvalidOperationException: Items collection must be empty before using ItemsSource.` appears only in the *reverse* order (Items non-empty, then assign `ItemsSource`). | **The conclusion survives; the reason does not.** Use a throwaway because it is smaller and needs no view, not because the parsed one throws. Stated so a builder who sees a green `Items.Add` on the parsed control does not conclude the grounding was wrong. |
| **W7** | G3 (`:76`): the panel is "the surface Batches 06, 07 and the consolidation pass all added lines to **without any of them being parsed**." | **False at HEAD.** The panel is parsed today, twice, in shipped Batch-13 facts (`AssistantViewParseTests.cs:262`, `:338`: `panel = new RunProgressPanel { DataContext = vm };`) — and the same spec cites those two precedents by name at `:47`. | The true claim is narrower: **no *binding-path walk* has ever run over the panel.** Write that in the new test's doc comment. The spec's sentence would make the fact's own justification a falsehood. |
| **W8** | G3 (`:71`) names `RunProgressPanel.xaml` with no directory; the surrounding prose reads as a `Views/` file. | Real path is `src/Pia.Wpf/Controls/Assistant/RunProgressPanel.xaml`, class `Pia.Controls.Assistant.RunProgressPanel`. | Use the real path. `RunProgressPanel.xaml:68`/`:71` are **exact** — verified. |
| **W9** | G3 (`:85`): "the panel carries **at least three** `ItemsControl`s". | **Four.** `:74` (`Steps`), `:131` (`Timeline`), `:184` (`Children`), and `:220` (`Timeline`, item-scoped) — the fourth is nested *inside* the children `DataTemplate` and is doubly unreachable. | "At least three" is technically true and practically misleading. §7 states four, and that the fourth never realizes, which is what makes any `.Single()` over `ItemsControl` safe in this panel. |
| **W10** | G4 (`:99`): "Each root type is read by REFLECTION off the property **`SettingsView.xaml` hosts the view with**". | `SettingsView.xaml` (164 lines) instantiates exactly **six** settings views: `ProvidersView:84`, `OptimizeView:97`, `AssistantView:110`, `GeneralView:123`, `AccountView:136`, `PluginsView:149`. **`PersonasView` and `E2EEOnboardingView` are not in it.** `PersonasView` is hosted at `Views/SettingsViews/AssistantView.xaml:163` → `AssistantSettingsViewModel.PersonasVm`. `E2EEOnboardingView` is hosted at `AccountView.xaml:218` **and** `WizardSteps/AccountSetupStep.xaml:269`, with **no `DataContext` at either site** and **no hosting property at all**. | The recipe is unexecutable for two of the seven. See **W11** and **D5**. |
| **W11** | G4 (`:96`) lists seven views to parse, and (`:100`–`:102`) warns that hardcoding the root is "the one way this whole technique can go green while proving nothing". | Obeying `:99` literally for `PersonasView` walks **straight into that trap**: `SettingsViewModel.PersonasVm` *does* exist (`SettingsViewModel.cs:22`, type `PersonaSettingsViewModel`, constructed at `:65`) but **no markup binds it**. The reflection succeeds, the type matches the real host's by coincidence, every path passes, and the fact proves nothing. Separately, **both views already have coverage**: `PersonasView`'s 2 paths are inside the currently-green settings-Assistant walk (which re-roots at `AssistantView.xaml:163` and asserts `=PersonasVm` at `:89`), and `E2EEOnboardingView`'s 15 paths are walked as a plain logical child of `AccountView`. `PersonasView`'s standalone floor would be **2**. | **G4 writes FIVE files, not seven.** Decision **D5**. The two omitted views get one cheap assertion each in the file that already covers them, which is strictly more than a new file would add. |
| **W12** | `Walk`'s own doc comment `:143`: "the ItemsControl-ancestor command binding **at :221**" (singular). | `AssistantView.xaml:221` is still that binding (`RevokeCommand`) — the reference holds — but there are now **five** such bindings: `:221`, `:583`, `:588`, `:593`, `:596`. | Carry the comment **verbatim** in G1 (behaviour-unchanged move). G2 may append one sentence to its *own* file's doc noting the fifth (`RevokeCommand` at `:221`) stays uncovered even after G2. Do not edit the moved comment in G1. |
| **W13** | The roadmap's own bullet at `00-OVERVIEW.md:1687` lists the row template's item-scoped paths as three (`ToggleLabel`, `CanRunNow`, `StatusLabel`); the batch spec (`:49`–`:55`) corrects it to ten. | The batch spec's ten are **exactly right** — all ten present, none named that is not there. But the spec's list itself **omits four empty-path bindings**: `CommandParameter="{Binding}"` at `:584`, `:589`, `:594`, `:597`, which bind the whole `ScheduledJobRow` and are what actually delivers the row to each command. | Ten paths **+ four `CommandParameter` identities + four command identities = 18 assertions**, not ten. §6. |

**One more, not a disagreement but a live falsehood Batch 14 inherits:** `00-OVERVIEW.md:1457` says the three
consolidation-pass items are "all unreachable from any test because there is no View test (R11)". False at
HEAD and more false after G3. Corrected in the docs commit — **without** implying the items themselves moved
(Appendix A, F3).

---

## 1. Decisions

### D1 — the four `RelativeSource` command bindings: **(a)**, and it did *not* collapse into (c)

**Verdict, in the spec's own label: (a) — give the loaded row a real `ItemsControl` ancestor.** By the spec's
own pre-committed hinge (`:117`–`:118`, "if it turns out to need the parsed control, (a) has collapsed into
(c)"), it does **not**: the winning shape needs no parsed control as a *host*, no `ApplyTemplate`, no measure
pass, no layout, no frame push. It is `Run(mutate)` → `Pump()` → `Run(observe)`, the shipped precedent exactly.

**The mechanism (Ground D variant V3, recommended):** a **throwaway** `new ItemsControl { ItemTemplate =
parsed.ItemTemplate, DataContext = vm }` with `ItemsSource` left unset, then
`host.Items.Add(templateLoadedRow)`. Adding a `UIElement` to `Items` makes the `ItemsControl` its **logical
parent immediately**, and that parent is what `RelativeSource={RelativeSource AncestorType=ItemsControl}`
resolves against. The parsed control is still needed — but only as the **source of the `ItemTemplate`**, which
is the whole point (the test must drive the *shipped* template, not a copy).

**The evidence that settled it — three executed controls, not an argument:**

| Control | Measured result |
|---|---|
| **GREEN** — unmodified XAML, row parented to a throwaway `ItemsControl` | all four `BindingExpression.Status=Active`, `ResolvedSource=ScheduledJobsSettingsViewModel`, **`ReferenceEquals(button.Command, vm.XCommand) == true`** for all four |
| **RED** — `AssistantView.xaml:583` `StartEditCommand` → `StartEditCommandX`, `-t:Rebuild` | build still `0 Warning(s) 0 Error(s)`; the fact **FAILED**: *"DataContext.StartEditCommandX did not resolve to the SAME command instance … (BindingExpression.Status=PathError)"*. That is the silent class G2 exists for, caught. |
| **NULL** — the same loaded row with **no** parent | all four `Status=PathError`, `ResolvedSource=<null>`, `Command=<null>`. Proves the *parenting* is what does the work, not something incidental. |

Second red, for the `IsEnabled` half: `:587` `StatusIsKnown` → `StatusIsKnownX`, rebuild → two
`Assert.False() Failure`. Both typos reverted; `git diff -- src/` came back **0 lines**.

**Fallback, measured identical (V1)** — only if a reviewer objects to relying on item-is-its-own-container
semantics: a two-line `private sealed class AdoptingItemsControl : ItemsControl { internal void Adopt(object
c) => AddLogicalChild(c); }`. Byte-identical probe output. Do not reach for it unprompted.

**Dead ends, measured, do not re-attempt:** `IItemContainerGenerator.StartAt`/`GenerateNext`/
`PrepareItemContainer` without layout (container exists, content never realized, `buttons=0`); a bare
code-constructed `ItemsControl` + `ItemsSource` + `ApplyTemplate`+`Measure`+`Arrange` (`applyTemplate=False`,
no `Template` → no `ItemsPresenter` → no panel → no generation). **(c) itself works** on the parsed control
once its own `{Binding Jobs}` resolves — but it needs a full layout pass and buys nothing over (a).

**Do NOT close this by widening `TargetsDataContext`.** That filter is what keeps every `loc:Str` markup
extension out of scope; breaking it to reach four bindings puts localization back in the path walker.

### D2 — per-view non-vacuity floor, not a shared one (decided by the spec; instantiated here)

A shared floor lets a large view's path count cover for a small view whose walk died at a templated
container — and G4 measured exactly how bad that gets: `PersonasView` would have a floor of **2**. The floor
lives in the **calling** test, is derived from a number the builder **measures and prints**, and is set
**well under** it. §8 gives the per-view band. Where a static grounding count exists it is the *expected*
value, not the source of the floor: if the live walk measures materially less, that is a **finding** (a
container stopped reporting logical children), not a reason to lower the number.

### D3 — no production code changes; a red fact is a **found defect**

If a new fact goes red against real markup, fix it, and the commit body says **the fact found it**, not that
it was written for it. This batch's value is partly that nobody knows whether these paths are all correct.
Two calibrations:

- **G2 and G3 are expected to be GREEN on first run.** All ten row paths + four commands resolve
  (`ScheduledJobRow` `ScheduledJobsSettingsViewModel.cs:449`–`:494`, commands `:243`/`:342`/`:375`/`:404`);
  all 23 distinct panel paths resolve on `RunProgressViewModel`. Report them honestly as **regression
  protection**, not as bug-finders. That is exactly why their RED demos are mandatory and non-trivial.
- **G4 is where a red is plausible.** Path resolution for the 129 walker-visible paths across the five views
  is deliberately **unverified**. Likeliest first reds, named without any claim that they are broken:
  `OptimizeView.xaml:20` `ProvidersVm.GoToProvidersTabCommand`, `PluginsView.xaml:26` `GoToAccountCommand`,
  `ProvidersView.xaml:85` `GoToCloudSyncCommand`.

Escalate rather than fix if the repair is more than a rename or a one-line binding correction.

### D4 — G1 moves **five** members and de-duplicates the existing copy (new; W3)

`Walk` · `BoundPath` · `TargetsDataContext` · `ResolvePathType` · `FindLogical<T>`. `FindLogical<T>` already
exists twice; leaving it behind guarantees a third and a fourth copy. `FindTextBlocks`
(`AssistantViewParseTests.cs:191`–`:199`) **stays where it is** — it is a named specialization with its own
load-bearing doc comment about logical-vs-visual walks, and moving it is scope creep with no caller outside
that file.

### D5 — G4 is **five** views, not seven (new; W10/W11)

**Write:** `GeneralView` · `AccountView` · `ProvidersView` · `OptimizeView` · `PluginsView`.
**Do not write:** `PersonasView` (already inside the green settings-Assistant walk; standalone floor 2;
the spec's reflection recipe walks into its own documented trap) and `E2EEOnboardingView` (already walked as
a logical child of `AccountView`, under the correct root, with no hosting property to reflect off).

Instead, **two cheap assertions that are strictly stronger than the two files would have been**:

- In `SettingsAssistantViewParseTests` (G4's last commit, an **added** assertion — see §5's constraint):
  `Assert.Contains(bindings, b => b.Contains("=AddPersonaCommand [PersonaSettingsViewModel]"));`
  This turns PersonasView's coverage from *incidental* into *asserted*, and it is the two-halves shape
  (`=PersonasVm ` at `:89` is already the first half). **Verify the exact projected string by printing the
  array first** — do not guess the `[ContextType]` label.
- In the new `AccountViewParseTests`: a duck-type fact naming the real hazard —
  `E2EEOnboardingView.xaml`'s 15 paths are all prefixed `OnboardingViewModel.` and are written against
  *whatever host DataContext happens to expose a member of that name*. Nothing — no interface, no base
  class — enforces it. `AccountSettingsViewModel.OnboardingViewModel` (`:28`) and
  `FirstRunWizardViewModel.OnboardingViewModel` (`:115`) are the two. Renaming the first breaks the settings
  page while the wizard keeps working, silently. Assert **both** hosts expose a public instance property
  named `OnboardingViewModel` of the **same** type.

### D6 — the three `00-OVERVIEW.md` scope calls, made here rather than deferred (new)

Ground E surfaced three edits that are adjacent to Batch 14 but not caused by it and handed them up as scope
questions. **They are in scope. Make them, and label them.**

1. **`:569`, the Rank-1 row.** It still says "Phase 3 lengthened it by FOURTEEN items and shortened nothing"
   and has **never registered Batch 13's shortening of four**. Editing it for 14 means crediting 13 in the
   same sentence. Do it — leaving it means the rank table reports a number two batches have moved.
2. **`:582`, Batch 13's shipped row**, "the only batch so far that made Rank 1 **shorter**". False the moment
   14 ships. Amend to "the first batch…".
3. **`:1227` (Batch 04 item 1) and `:1385`/`:1394` (Batch 05's toggle debt)** still claim no test parses
   `Views/SettingsViews/AssistantView.xaml` and that "until someone writes that one, the debt stands". Both
   false at HEAD; `:1487` already names them as things that "can stop saying" what they say. Fix them, and
   label the fix a **CORRECTION, not a shortening** (Appendix A, F7) — Batch 13 already took their silent
   halves and Batch 14 takes nothing further.

### D7 — G1 adds one member beyond the move: `Describe` (new)

The projection string at `SettingsAssistantViewParseTests.cs:71` is **contract**: `=Path ` and
`[ContextType]` are what `:81`–`:90` match on, and the trailing `ok`/`UNRESOLVED` is what `:92` matches on.
G3 + G4 would otherwise copy that format string **six more times**, and one drifted copy silently breaks the
`Assert.Contains` anchors in the file that drifted. So `BindingPathWalker` also exposes:

```csharp
internal static string[] Describe(DependencyObject root, Type? contextType) =>
    Walk(root, contextType)
        .Select(b => $"{b.Element}.{b.Property}={b.Path} [{b.ContextType}] {(b.Resolves ? "ok" : "UNRESOLVED")}")
        .ToArray();
```

This changes no behaviour and edits no assertion — it moves an existing expression, character for character,
into the one place it belongs. `Walk` stays exposed for any caller that wants the tuples.

**One compile-shaped constraint on every caller: `Describe` must be invoked INSIDE the `WpfStaHost.Run`
lambda, never around it.** `Walk` is a lazy iterator and `Describe`'s `.ToArray()` is what forces it; forcing
it off the STA thread touches `DependencyObject`s from the wrong thread. Every snippet in §7 and §8 has the
call inside the lambda — `WpfStaHost.Run(() => BindingPathWalker.Describe(new …(), root))`. Do not
refactor it to `BindingPathWalker.Describe(WpfStaHost.Run(() => new …()), root)`; that compiles and is wrong.

---

## 2. Hazards — read this block before every group

Each is an instruction, not a warning.

1. **Rebuild, always: `dotnet build -t:Rebuild`.** An incremental build reuses stale BAML, so a XAML
   red-before/green-after keeps testing markup that is no longer on disk. Batch 13 lost time to exactly this
   and recorded it as an opened item. There is no such thing as a quick incremental check of a XAML change in
   this batch.
2. **Write every new file with CRLF.** Repo `.cs` and `.md` are CRLF; the `Write` tool emits LF. Convert
   immediately after writing (`unix2dos <file>` or equivalent) and verify before committing.
3. **`WpfStaHost.Pump()` throws when called on the host thread.** The idiom is `Run(mutate)` → `Pump()` →
   `Run(observe)`. Note the asymmetry that will trip you: `Run` on the host thread silently runs *inline*
   (`WpfStaHost.cs:175`–`:176`), `Pump` throws `InvalidOperationException` (`:231`–`:237`). There is **no
   `Run(Action)` overload** — every lambda ends `return 0;`.
4. **Dispose every `RunProgressViewModel` a fact constructs, in a `finally`, inside a `Run` body.** It
   subscribes to `IAgentRunService.RunChanged` in its ctor (`RunProgressViewModel.cs:275`) and the host
   outlives every test; the withdrawn row-render fact leaked one and that is how one fact reaches into
   another. Keep the `finally` shape for `ScheduledJobsSettingsViewModel` too even though it is **not**
   `IDisposable` today — it costs one line and the day it gains a subscription the shape is already right.
5. **`[Collection("WpfApplicationStatic")]` on every new view-test class.** The host is process-global
   (`WpfApplicationCollection.cs:21`, `DisableParallelization = true`). The collection holds **6** facts at
   `2266bf7`; a full Batch 14 adds **10** (G1 none · G2 two · G3 two · G4 five + the duck-type fact), taking
   it to **16**. `WpfStaHost.cs:208`–`:213` records that an *eighth frame-pushing* fact
   took the gate from 0/3 to 2/3 failing before the `Pump()` rewrite fixed it. Every technique in this plan
   adds **zero** frame pushes and **zero** layout passes — keep it that way, and if the gate goes
   intermittent, that is the first thing to re-read.
6. **After every red demo, `git diff --stat -- src/` must be EMPTY before the group commit, and the commit
   body must state that you ran it.** A builder that dies between "inject typo" and "revert" otherwise ships
   broken markup at 0 warnings.
7. **A rule over an EMPTY set is vacuously true.** Every path-walk fact needs a non-vacuity floor in the
   **calling** test, never in the helper. "No unresolved paths" over a walk that found nothing passes forever.
8. **A DP at its default value is not evidence.** This is the same hazard as (7) at the level of a single
   assertion, and it was **measured** twice: `Button.IsEnabled` defaults to `True`, so asserting `true` for
   `StatusIsKnown`/`CanRunNow` passes whether the binding exists or not; `TextBlock.Visibility` defaults to
   `Visible`, so asserting `Visible` for `HasOutputBranch` passes whether the binding exists or not. **Every
   boolean-to-DP path must be observed in the direction that can only be reached THROUGH the binding**
   (§6 row B, §7 the pre-mutation observation).
9. **Identify elements by their binding PATH, never by index and never by `Content`/`Text`.** The measured
   walk order in the jobs row came out `Delete | Run now | Disable | Edit` — reversed from markup — and every
   button label is `loc:Str`-dependent. Use
   `BindingOperations.GetBinding(el, SomeProperty) is Binding { Path.Path: "X" }` with `.Single(...)` as the
   guard. (Ground D's own `ReadRow` violates this by finding the not-owned line via its English text; do not
   copy that line.)
10. **Fully qualify view type names.** `Pia.Views.OptimizeView` and `Pia.Views.SettingsViews.OptimizeView`
    both exist and both compile; same trap for `AssistantView`, which `SettingsAssistantViewParseTests.cs:12`
    already records. Write `new Pia.Views.SettingsViews.OptimizeView()`.
11. **The test namespace is `Pia.Tests.Views`, not `Pia.Wpf.Tests.Views`.** A guessed FQN in
    `--filter-class` matches zero tests and reports success. Read the executed `total:` every time.
12. **Culture: derive, never hardcode a formatted date.** `NextFireAt` is a non-nullable `DateTime` with
    `StringFormat=g`; WPF formats it with the element's `Language` (default en-US — verified: no `xml:lang`
    or `Language=` anywhere in `AssistantView.xaml`) while a test computing `dt.ToString("g")` uses
    `CurrentCulture`. On a de-DE box those differ. Use
    `row.NextFireAt.ToString("g", CultureInfo.GetCultureInfo("en-US"))`.
13. **The shared `ILocalizationService` stub returns the KEY.** `loc.Format(...)` stubbed as
    `ci => (string)ci[0]` makes `OutputBranchNote` render literally `"Run_Output_Branch"`. G3's render fact
    must override it locally or its assertion never proves the branch name reaches the string (§7).
14. **Do not lift `D1_Survey`** from the experiment file — it ends in a deliberate `Assert.Fail`. Do not lift
    `Assert.Equal("PathError", …)` either; `BindingExpressionBase.Status` is a WPF implementation detail, and
    it does not discriminate anyway (both "no ancestor" and "wrong path" report `PathError`). Assert
    `Command is null` / command identity instead.

**Reference material for lifting (read-only, uncommitted, NOT to be shipped):**
`C:\projects\Pia.Wpf\.claude\worktrees\wf_38446be0-7ee-4\tests\Pia.Wpf.Tests\Views\JobsRowAncestorExperimentTests.cs`
— the D1 experiment at `2266bf7`. Lift `Build()`, `NewRow()`, `ProbeCommands`, the two green facts. Delete
nothing in the main checkout; that worktree is disposable.

---

## 3. THE GATE — run this verbatim at every group commit

```
dotnet build -t:Rebuild -v:n
dotnet build -t:Rebuild -v:n -c Release
dotnet test tests/Pia.Wpf.Tests/Pia.Wpf.Tests.csproj -- --filter-not-namespace "Pia.Wpf.Tests.Integration.Providers"
```

- **`0 Warning(s)` and `0 Error(s)` in BOTH configurations**, read off MSBuild's `N Warning(s)` summary line.
  At `-v:n` every warning prints **twice** (inline + summary), so grepping the log double-counts.
- **Confirm the rebuild was genuine.** Redirect the build to a log and check
  `grep -c "Roslyn.bincore.csc.exe /noconfig" build.log` **== 6** (4 code assemblies + 2 satellite de/fr
  resource compiles). Do **not** count `CoreCompile:` lines — parallel MSBuild reprints that header on every
  node resume and you will read 12.
- **The suite must reach `failed: 0`.**

**MEASURED BASELINE on the clean tree at `2266bf7`** (measured by the orchestrator, not read from a doc):

> Debug `0 Warning(s) / 0 Error(s)` · Release `0 Warning(s) / 0 Error(s)` ·
> suite **2723 total / 0 failed / 2722 passed / 1 skipped**

**EVERY GROUP RECORDS ITS OWN POST-COMMIT TOTAL** in the commit body, so the final report closes the
arithmetic as a *measured chain* (`2723 → … → N`) the way Batch 13's entry does. Not inferred from a diff,
not extrapolated from the number of `[Fact]`s added.

**Two known intermittents — do not chase them.** Re-run the class isolated and say in the commit that you
did:

- `AssistantChatConcurrencyTests.DeleteAllAsync_WithAnotherConnectionCommittingThroughout_Completes`
- `TaskExtensionsTests.SafeFireAndForget_SlowTask_DoesNotBlock`

Isolated re-run form (note the namespace, hazard 11):
`dotnet test tests/Pia.Wpf.Tests/Pia.Wpf.Tests.csproj --no-build -- --filter-class "Pia.Tests.Views.<Class>"`

---

## 4. Ordering and dependency

```
G1  (helper)  ──►  G2  (jobs row)
              ──►  G3  (run panel)
              ──►  G4  (five settings views, one commit each)  ──►  docs commit (LAST)
```

- **G1 first, non-negotiable.** G2 does not strictly need it (it uses `FindLogical` + `BindingOperations`
  directly), but G3 and G4 both consume `Describe`/`Walk`, and doing G1 last would mean rewriting them.
- **G2 and G3 are independent of each other.** Either order. G2 first is marginally better: its technique is
  the one with executed evidence behind it, so it is the cheapest confidence-builder.
- **G4 is the group to drop if the round runs short**, and it subdivides **one view per file, one commit per
  view**, in this order — richest first, because a partial G4 should keep the views whose floors are worth
  something:

  | order | view | measured (static) walker-visible paths | why here |
  |---|---|---|---|
  | 1 | `GeneralView` | **40** (34 + 6 under a re-root) | richest; the only one with an internal re-root, so it exercises the two-halves assertion |
  | 2 | `AccountView` | **61** at runtime (46 own + 15 from the nested `E2EEOnboardingView`) | largest surface; carries the D5 duck-type fact |
  | 3 | `ProvidersView` | **12** | |
  | 4 | `OptimizeView` | **8** | carries the cross-VM hop `ProvidersVm.GoToProvidersTabCommand` |
  | 5 | `PluginsView` | **6** | smallest; weakest floor; first to drop |

  **A partial G4 is coherent** because each commit is a self-contained file plus its own floor, and the only
  cross-view artefact — the `:1748`/`:778` residue *number* in `00-OVERVIEW.md` — is written in the **docs
  commit, which is always last**. If G4 lands 3 of 5, the docs commit says 3.
- **The docs commit is the last commit of the batch**, after the final G4 commit, because two of its edits
  quote a count that is not known until then (Appendix A items 6 and 14).

---

## 5. G1 — lift the binding-path walk into a shared `BindingPathWalker`

**Commit boundary:** one commit. No new `[Fact]`. The suite total **must be unchanged at 2723** — that
invariance *is* the proof the move was mechanical.

**Commit subject:** `Tests: lift the binding-path walk into one BindingPathWalker`

**Commit body must state:** suite total unchanged at 2723/0; no assertion edited in either existing file;
`FindLogical<T>` de-duplicated from two copies to one; `Describe` added (D7) and why; the `Resolves`-on-null
behaviour carried verbatim including the comment that misdescribes it (W2/Q1).

### Files

| Action | Path |
|---|---|
| **NEW** | `tests/Pia.Wpf.Tests/Views/BindingPathWalker.cs` |
| **MOD** | `tests/Pia.Wpf.Tests/Views/SettingsAssistantViewParseTests.cs` |
| **MOD** | `tests/Pia.Wpf.Tests/Views/AssistantViewParseTests.cs` |

### `BindingPathWalker.cs`

- `namespace Pia.Tests.Views;` (file-scoped), `internal static class BindingPathWalker`.
- Usings, and **only** these three: `System.Reflection` (`BindingFlags`), `System.Windows`
  (`DependencyObject`, `FrameworkElement`, `LogicalTreeHelper`, `DependencyProperty`), `System.Windows.Data`
  (`Binding`, `BindingExpression`). It needs no `System.Windows.Controls`, no `Xunit`, no `Pia.ViewModels`.
- Move **`SettingsAssistantViewParseTests.cs:135`–`:219`** (W1 — *including* `Walk`'s doc comment at
  `:135`–`:147`) and **`:221`–`:231`** (`FindLogical<T>`, W3/D4), changing `private static` → `internal
  static` and **nothing else**. Not the comments, not the tuple element names, not the order of the two
  `yield return`s, not the `if (property == FrameworkElement.DataContextProperty) continue;` guard.
- Add `Describe` per **D7**.
- **The load-bearing details, restated so a "tidy-up" cannot eat them:**
  - The tuple is `(string Element, string Property, string Path, string ContextType, bool Resolves)` — those
    **names and that order** are contract for `Describe`'s format string, which is contract for the
    `Assert.Contains` anchors in three files.
  - **Ordering inside `Walk` is load-bearing.** The `DataContext` binding is resolved at `:155` *before* the
    local-value loop at `:164` and *before* the recursion at `:179`; `:168`'s skip only works because `:155`
    already consumed it. Do not reorder, do not fold the DataContext case into the loop.
  - `TargetsDataContext` is applied **twice** — at `:170` (every non-DataContext property) and inside
    `BoundPath` at `:189` (the DataContext binding itself). The second application means a `DataContext`
    bound with `RelativeSource`/`ElementName`/`Source` is treated as **no re-root at all**, and the subtree
    keeps its parent context. That asymmetry is deliberate; carry it.
  - **MultiBinding is skipped as a side effect** of the `is not BindingExpression` type test at `:169`/`:188`
    (a `MultiBinding` yields a `MultiBindingExpression`). There is no explicit MultiBinding check, though the
    doc comment reads as though there is. Carry both the mechanism and the comment.
  - The moved `FindLogical<T>` doc must be **`SettingsAssistantViewParseTests`' wording** (`:221`–`:222`),
    not `AssistantViewParseTests`' (`:399`). The latter is `<see cref="FindTextBlocks"/>` — a private member
    of a class the walker no longer lives in, which raises **CS1574** and breaks the zero-warning gate.

### `SettingsAssistantViewParseTests.cs`

- Delete `:135`–`:231` (the whole helper block). The class now ends after the second `[Fact]`.
- `:69`–`:72` becomes
  `var bindings = WpfStaHost.Run(() => BindingPathWalker.Describe(new Pia.Views.SettingsViews.AssistantView(), root));`
- `:117` `FindLogical<TextBlock>(view!)` → `BindingPathWalker.FindLogical<TextBlock>(view!)`.
- Prune usings that go unused (`System.Windows.Data` at `:5` certainly; check `System.Windows` at `:3` —
  `FrameworkElement`/`LogicalTreeHelper` leave with the helpers but `DependencyObject` may not; and
  `System.Reflection` at `:1` stays for `BindingFlags` at `:65`). **Unused usings are a warning under this
  repo's gate.** Let the Release rebuild tell you; do not guess.
- **No assertion is edited.** `:67`, `:74`, `:81`–`:83`, `:89`–`:90`, `:93`, `:123`, `:125`, `:130` are
  byte-identical after this commit. `MinimumBoundPaths = 20` (`:48`) and `RosterHeaderText` (`:55`) stay in
  this file — the helper must not own a floor (D2). *G4's last commit may **add** one assertion here (D5);
  G1 may not.*

### `AssistantViewParseTests.cs`

- Delete its duplicate `FindLogical<T>` at `:399`–`:408` (doc comment included).
- Re-point `:262`, `:349`, `:366` to `BindingPathWalker.FindLogical<…>`.
- **Leave `FindTextBlocks` (`:180`–`:199`) exactly as it is** (D4), and leave every assertion alone.

### Red demo

G1 adds no fact, so there is no XAML typo to inject. **Its proof is different and it is stronger:** the two
existing files' six facts stay green with **zero assertion edits**, and the suite total stays **2723**. To
make that a demonstration rather than a claim, run the isolated classes before and after and paste both
counts into the commit body:

```
dotnet test tests/Pia.Wpf.Tests/Pia.Wpf.Tests.csproj --no-build -- --filter-class "Pia.Tests.Views.SettingsAssistantViewParseTests"
dotnet test tests/Pia.Wpf.Tests/Pia.Wpf.Tests.csproj --no-build -- --filter-class "Pia.Tests.Views.AssistantViewParseTests"
```

Optional 60-second sanity check that the moved walker is still *wired*, not just compiling: temporarily
change `Describe`'s `"ok"` literal to `"OK"` and confirm `SettingsAssistantViewParseTests` goes red on `:92`.
Revert. (This is a *test-side* edit, so hazard 6's `git diff -- src/` check is trivially satisfied — but run
it anyway.)

### G1 decides on its own / must escalate

- **On its own:** class name, file name, `internal` vs `public`, using-list, where in the file `Describe`
  sits, the exact re-pointing syntax.
- **Escalate:** any change to `Walk`'s observable behaviour; any edit to an existing assertion; any change to
  the tuple's element names or order; any temptation to "fix" W2's tri-state; moving `FindTextBlocks`; adding
  a floor or an assertion to the helper.

---

## 6. G2 — Batch 09's jobs row `DataTemplate`, commands included

**Commit boundary:** one commit, two new facts. **Commit subject:**
`Tests: pin the scheduled-jobs row template, its four ancestor commands included`

**Commit body must state:** D1 resolved to **(a)** with the mechanism named; the three red demos and their
messages; `git diff --stat -- src/` empty; the post-commit suite total.

**File:** **NEW** `tests/Pia.Wpf.Tests/Views/ScheduledJobsRowTemplateTests.cs`, class
`ScheduledJobsRowTemplateTests`, `[Collection("WpfApplicationStatic")]`, `namespace Pia.Tests.Views;`, CRLF.

### Shared setup (lift from the experiment file, hazard block reference)

Build once per fact inside a `WpfStaHost.Run` body:

1. `var view = new Pia.Views.SettingsViews.AssistantView();` — **no DataContext**, deliberately.
2. Find the jobs `ItemsControl` **by its declared `ItemsSource` path**, not by index and not by
   `ReferenceEquals` (the precedents' discriminator needs a constructed VM; this file constructs the VM but
   never assigns it to the view):
   ```csharp
   var parsed = BindingPathWalker.FindLogical<ItemsControl>(view)
       .Single(ic => (BindingOperations.GetBinding(ic, ItemsControl.ItemsSourceProperty) as Binding)
           ?.Path?.Path == "Jobs");
   ```
   `.Single()` is the non-vacuity guard, and it is load-bearing: `ComboBox : Selector : ItemsControl`, so a
   bare `FindLogical<ItemsControl>` over this view returns the three real `ItemsControl`s (`:183` Grants,
   `:457` roster, `:545` Jobs) **plus every ComboBox** (at least `:39`, `:108`, `:337`, `:632`, `:641`,
   `:664`). The tool-permissions `ItemsControl` at `:183` in particular has the same
   ancestor-command shape and must not be picked up.
3. `var vm = new ScheduledJobsSettingsViewModel(jobs, runner, providers, loc, NullLogger<SettingsViewModel>.Instance);`
   with `jobs.GetAllAsync()` → empty, `providers.GetProvidersAsync()` → empty, and the standard
   key-returning `ILocalizationService` stub. **Construct on the STA thread** — it is a `UiThreadViewModel`.
4. `var host = new ItemsControl { ItemTemplate = parsed.ItemTemplate, DataContext = vm };` —
   **`ItemsSource` left unset**, which is what keeps `Items` usable.
5. Per row: `var row = (FrameworkElement)parsed.ItemTemplate.LoadContent(); row.DataContext = <ScheduledJobRow>;
   host.Items.Add(row);`
6. `Run(...)` ends, then `WpfStaHost.Pump()`, then a separate `Run(observe)`.
7. `finally { WpfStaHost.Run(() => { (vm as IDisposable)?.Dispose(); return 0; }); }` — inert today
   (`ScheduledJobsSettingsViewModel` is `UiThreadViewModel : ObservableObject`, no ctor subscriptions), kept
   per hazard 4.

**Two rows, and this is the single most losable requirement in the batch.** Measured on a single all-true
row: `buttonIsEnabled = [True | True | True | True]`. `IsEnabled` defaults to `True`, so `StatusIsKnown` and
`CanRunNow` are **vacuous on row A alone** — the fact would go green with those two bindings deleted.

- **Row A** — `Name="Nightly digest"`, `Query="summarise today"`, `KindLabel="Agent task"`,
  `RecurrenceLabel="Daily"`, `StatusLabel="Active"`, `NextFireAt=new DateTime(2026,8,2,9,0,0,
  DateTimeKind.Local)`, `StatusIsKnown=true`, `ToggleLabel="Disable"`, `OwnedByThisDevice=true`.
- **Row B** — `Name="Foreign job"`, `StatusLabel="Unknown (7)"`, `Status=(ScheduledJobStatus)7`,
  **`StatusIsKnown=false`**, `ToggleLabel="Enable"`, **`OwnedByThisDevice=false`** (so
  `CanRunNow => OwnedByThisDevice && StatusIsKnown` is false too).

### Fact 1 — `JobsRowTemplate_BindsEveryItemScopedPath_AcrossTwoRowsThatDiscriminate`

Ten paths. Read each element **by its binding path** (hazard 9) with `.Single(...)` as the guard; a tiny
local `static string? PathOf(DependencyObject el, DependencyProperty dp) =>
(BindingOperations.GetBinding(el, dp) as Binding)?.Path?.Path;` keeps it to one line each.

| # | Path | Markup | Read via | Assertion | Non-vacuous because |
|---|---|---|---|---|---|
| 1 | `Name` | `:553` | `TextBlock.Text` | `"Nightly digest"` (A) | unbound `Text` is `""` |
| 2 | `Query` | `:557` | `TextBlock.Text` | `"summarise today"` (A) | ″ |
| 3 | `KindLabel` | `:562` | `TextBlock.Text` | `"Agent task"` (A) | ″ |
| 4 | `RecurrenceLabel` | `:565` | `TextBlock.Text` | `"Daily"` (A) | ″ |
| 5 | `StatusLabel` | `:568` | `TextBlock.Text` | `"Active"` (A) / `"Unknown (7)"` (B) | ″; B also pins the unknown-status render |
| 6 | `NextFireAt` (`StringFormat=g`) | `:571` | `TextBlock.Text` | `row.NextFireAt.ToString("g", CultureInfo.GetCultureInfo("en-US"))` — **derived**, never the observed `"8/2/2026 9:00 AM"` | ″ + hazard 12 |
| 7 | `OwnedByThisDevice` | `:579` | `TextBlock.Visibility`, found by `PathOf(tb, UIElement.VisibilityProperty) == "OwnedByThisDevice"` | **`Collapsed` on A** (the biting direction); `Visible` on B is the vacuous one — assert it for symmetry and say so in a comment | `Visibility` defaults to `Visible` |
| 8 | `ToggleLabel` | `:585` | **`ui:Button.Content`** — not a `TextBlock`; no template is applied, so read `button.Content?.ToString()` | `"Disable"` (A) / `"Enable"` (B) | `Content` defaults to `null` |
| 9 | `StatusIsKnown` | `:587` | Toggle `Button.IsEnabled` | **`false` on B** | hazard 8 |
| 10 | `CanRunNow` | `:592` | Run-now `Button.IsEnabled` | **`false` on B** | hazard 8 |

Non-vacuity guards for the fact as a whole: `Assert.Equal(4, buttons.Length)` on each row (the template's
four `ButtonBase`s were found at all), and every `.Single(...)` lookup above.

### Fact 2 — `JobsRowTemplate_ResolvesAllFourAncestorCommands_ToTheInstanceOnTheViewModel`

The four `RelativeSource AncestorType=ItemsControl` command paths (`:583`, `:588`, `:593`, `:596`), each
paired with `CommandParameter="{Binding}"` (`:584`, `:589`, `:594`, `:597`).

For each of the four buttons on row A, keyed by `PathOf(b, ButtonBase.CommandProperty)`:

- `Assert.True(ReferenceEquals(button.Command, expected))` where `expected` is `vm.StartEditCommand` /
  `vm.ToggleEnabledCommand` / `vm.RunNowCommand` / `vm.DeleteCommand`, with a message naming the path.
  **Command *identity* is the only thing that proves the technique** — a plain non-null check does not.
- `Assert.True(ReferenceEquals(button.CommandParameter, rowA))`.
  **Say in a comment that `CommandParameter` is NOT evidence for the ancestor technique**: it was `true` in
  the null control too, because `{Binding}` resolves off the row's own DataContext and needs no ancestor.
- `Assert.Equal(4, probes.Length)` plus one `Assert.Contains` per expected path string, so a renamed path
  cannot shrink the set silently.

**Why this fact is the one that matters, and it should be in the doc comment:** `DeleteCommand` is the **only
one of the four with zero C# references anywhere in the repo**. The other three are protected by compiled
call sites (`ScheduledJobsSettingsViewModelTests.cs:79`, `:111`, `:169`). Nothing but CommunityToolkit's
`Async`-suffix-stripping naming convention keeps `AssistantView.xaml:596` alive; renaming `DeleteAsync`
(`ScheduledJobsSettingsViewModel.cs:376`) breaks the Delete button silently, at 0 warnings, with every
ViewModel test green. After this fact, it breaks the **build**, because the fact names `vm.DeleteCommand` —
a harder stop than a red test.

Also note in the doc comment: the **fifth** `AncestorType=ItemsControl` command binding in this file,
`DataContext.RevokeCommand` at `AssistantView.xaml:221` (tool permissions), stays uncovered by this batch
(W12).

### RED demos — three, all executed before green

| # | Inject | Into | Expected red | Revert |
|---|---|---|---|---|
| R1 | `StartEditCommand` → `StartEditCommandX` | `src/Pia.Wpf/Views/SettingsViews/AssistantView.xaml:583` | Fact 2 fails: *"DataContext.StartEditCommandX did not resolve to the SAME command instance on ScheduledJobsSettingsViewModel (…)"*, **and** the `Assert.Contains` for `"DataContext.StartEditCommand"`. Build stays `0 Warning(s) 0 Error(s)` — that is the point. | revert, `-t:Rebuild` |
| R2 | `StatusIsKnown` → `StatusIsKnownX` | `…AssistantView.xaml:587` | Fact 1 fails on row B's toggle `IsEnabled` with `Assert.False() Failure` | ″ |
| R3 | `Name` → `NameX` | `…AssistantView.xaml:553` | Fact 1 fails: the `.Single(tb => PathOf(tb, TextBlock.TextProperty) == "Name")` lookup throws *"Sequence contains no matching element"*, which names the missing path | ″ |

R1 and R2 are **already measured green-then-red** at `2266bf7` (Ground D); re-run them anyway — the point of
the demo is that *this* file's facts bite, not that a throwaway experiment's did. R3 is new.

**After all three: `git diff --stat -- src/` must print nothing.** Then `-t:Rebuild` both configs, then the
gate.

### G2 decides on its own / must escalate

- **On its own:** fact names, row field values, the `PathOf` helper's shape, whether the two facts share one
  `Build()`, whether to keep the inert `finally`.
- **Escalate:** needing the parsed control as a *host* or any layout pass (that is (c), and D1 says it is
  unnecessary); any widening of `TargetsDataContext`; dropping any of the ten paths or four commands;
  a red that is a real production defect needing more than a rename (D3).

---

## 7. G3 — the run panel: every non-templated path, plus the branch line rendered

**Commit boundary:** one commit, two new facts. **Commit subject:**
`Tests: walk the run panel's non-templated bindings, and render the branch line`

**Commit body must state:** the **measured** tuple count and the floor derived from it; both red demos; W7's
correction (the panel *was* parsed, it was never *walked*); `git diff --stat -- src/` empty; the post-commit
suite total.

**File:** **NEW** `tests/Pia.Wpf.Tests/Views/RunProgressPanelParseTests.cs`, class
`RunProgressPanelParseTests`, `[Collection("WpfApplicationStatic")]`, `namespace Pia.Tests.Views;`, CRLF.

### Fact 1 — `EveryNonTemplatedBindingPath_ResolvesOnTheViewModelThatHostsThePanel`

**This fact needs no ViewModel at all** — no substitutes, no `finally`, no `Dispose`, no `IAgentRunService`.
Paths are read declaratively off `BindingExpression.ParentBinding.Path` and resolved by reflection, exactly
as `SettingsAssistantViewParseTests.cs:70` does with no DataContext ever set. Ground B is emphatic that a
builder who does not read this pays for the substitute setup twice. `new RunProgressPanel()` is already
proven to parse (`AssistantViewParseTests.cs:262`, `:338`), so every non-templated `StaticResource` in the
file resolves.

**Reflected root — do not hardcode `RunProgressViewModel`** (spec `:78`–`:83`):

```csharp
var root = typeof(AssistantViewModel)
    .GetProperty(nameof(AssistantViewModel.ActiveRunProgress), BindingFlags.Public | BindingFlags.Instance)!
    .PropertyType;
Assert.Equal(typeof(RunProgressViewModel), root);
```

`AssistantView.xaml:51` hosts the panel with `DataContext="{Binding ActiveRunProgress}"`. `ActiveRunProgress`
is **source-generated** from `[ObservableProperty] private RunProgressViewModel? _activeRunProgress;`
(`AssistantViewModel.cs:136`–`:137`). The `?` is an NRT annotation — compile-time metadata only, no distinct
runtime `Type` — so `.PropertyType` is `typeof(RunProgressViewModel)` and the assertion passes. Stated
because it looks like it should not. Bonus: `nameof(AssistantViewModel.ActiveRunProgress)` only compiles
because the generator emits the property, so removing `[ObservableProperty]` fails at **compile** time.

Then `var bindings = WpfStaHost.Run(() => BindingPathWalker.Describe(new RunProgressPanel(), root));`

**Floor: MEASURE FIRST.** Set `MinimumBoundPaths` to a `private const` in *this* file (D2 — never in the
helper) derived from the number the walk actually prints.

- **Expected measured value: 28** tuples (**23 distinct paths**; the walker yields **one tuple per bound DP**,
  not per distinct path — `State` is bound three times, `CanPublish`/`PublishNote`/`ChildrenNote` twice each).
  Ground B derived 28 by static parse; the arithmetic closes exactly against the file
  (28 walkable + 26 inside the four `ItemTemplate`s = 54 = every `{Binding` in the file).
- **Set the floor at 18** (~64 % of measured; the settings precedent uses 20 against a larger surface).
  **Do not exceed 22.**
- **If the live walk measures materially below 28, that is a finding, not a licence to lower the floor.**
  Report the number and stop. The most likely cause is a container that stopped reporting logical children.

**Named anchors** (a count alone is not non-vacuity — the settings precedent's `:81`–`:90` exist for this
reason). Four, each chosen for a distinct reason:

```csharp
Assert.Contains(bindings, b => b.Contains("=OutputBranchNote "));   // the surface R11 left open
Assert.Contains(bindings, b => b.Contains("=HasOutputBranch "));    // its visibility half
Assert.Contains(bindings, b => b.Contains("=LedgerSummary "));      // Batch 02's strip
Assert.Contains(bindings, b => b.Contains("=Children "));           // proves the walk reached the SECOND
                                                                    // Expander's content and did not stop
                                                                    // at the first
```

The `Children` anchor is the important one and deserves its comment: both `Expander`s' `Content` **is** in
the logical tree at parse time (`Expander : HeaderedContentControl : ContentControl` adds it regardless of
`IsExpanded`), and if that ever stopped being true the walk would silently lose tuples 19–28.

Then the standard unresolved sweep with a message naming
`src/Pia.Wpf/Controls/Assistant/RunProgressPanel.xaml`.

**Write into the doc comment, because the fact's own justification depends on it (W7):** the panel *is*
parsed today by two Batch-13 facts; what has never happened is a **binding-path walk** over it. And state the
scope limits: the four `ItemTemplate`s' 26 item-scoped bindings are out of reach (Steps `:75`–`:107`,
Timeline `:132`–`:165`, Children `:185`–`:253`, and a child-timeline `ItemsControl` at `:220` nested inside
the children template — doubly unreachable); `loc:Str` is out of scope by design; `DynamicResource` stores a
`ResourceReferenceExpression`, not a `BindingExpression`, and is invisible.

### Fact 2 — `RunProgressPanel_RendersTheOutputBranchLine_OnlyWhenTheRunHasABranch`

This is the half a path check cannot see, and **the naming of the fact is deliberate: it observes BOTH
states.** `TextBlock.Visibility` defaults to `Visible`, so asserting `Visible` after setting the branch is
**vacuous on its own** (hazard 8) — a deleted `Visibility` binding passes it. The **`Collapsed` observation
before the mutation is the one that bites.**

Shape (copy `AssistantViewParseTests.cs:335`–`:378`):

1. `Run(create)` — build the VM and `panel = new RunProgressPanel { DataContext = vm };`
2. `Pump()` — before this the panel's own bindings have not transferred.
3. `Run(observe)` — find the branch `TextBlock` by path
   (`.Single(tb => PathOf(tb, TextBlock.TextProperty) == "OutputBranchNote")`) and capture
   `Visibility` → **must be `Collapsed`** (`OutputBranchName` is null by default → `HasOutputBranch` false).
4. `Run(mutate)` — `vm.OutputBranchName = "pia/run/2026-08-01-abcdef";` **and nothing else.**
   `OutputBranchName` is `[ObservableProperty]` at `RunProgressViewModel.cs:187`–`:190` with
   `[NotifyPropertyChangedFor]` on **both** `HasOutputBranch` and `OutputBranchNote`, so the generated setter
   is the entire trigger — **no `IRunWorkspaceService` substitute is needed.**
5. `Pump()`
6. `Run(observe)` — `Visibility` → `Visible`, and `Text` → contains the branch name.
7. `finally { Run(() => { vm?.Dispose(); return 0; }); }` — hazard 4, non-negotiable.

**The localization trap, and G3 must not reuse `AssistantViewParseTests.CreateRunProgressViewModel` without
fixing it (hazard 13).** That helper stubs `loc.Format(...)` as `ci => (string)ci[0]`, i.e. it returns the
**key**. `OutputBranchNote` is `_localization.Format("Run_Output_Branch", OutputBranchName!)`
(`RunProgressViewModel.cs:198`), so with that stub the rendered text is literally `"Run_Output_Branch"` and
asserting it proves the *note property* was read but **never that the branch name reaches the string**.
G3 writes its own private factory with an interpolating stub, e.g.
`loc.Format(Arg.Any<string>(), Arg.Any<object[]>()).Returns(ci => $"{(string)ci[0]}|{string.Join(",", (object[])ci[1])}");`
and asserts the branch name is a substring of the rendered `Text`. **Say in the doc comment why this file
does not reuse the existing helper** — otherwise a future tidy-up merges them and re-vacuates the assertion.

Constructor for reference (`RunProgressViewModel.cs:253`–`:277`, 5 required + 3 trailing-optional, all
positional): `(IAgentRunService, Guid, ILocalizationService, IAgentRunResumeService, ILogger,
IAgentTimelineService? = null, IRunWorkspaceService? = null, IPersonaService? = null)`. Omit the trailing
three — omitting `IAgentTimelineService` is what keeps the fact store-less. **Construct on the STA thread**:
the ctor captures `SynchronizationContext.Current` (`:274`), subscribes `RunChanged` (`:275`) and fires
`RefreshAsync().SafeFireAndForget(...)` (`:276`).

### RED demos — two

| # | Inject | Into | Expected red | Revert |
|---|---|---|---|---|
| R4 | `OutputBranchNote` → `OutputBranchNoteX` | `src/Pia.Wpf/Controls/Assistant/RunProgressPanel.xaml:68` | Fact 1: the unresolved sweep lists `TextBlock.Text=OutputBranchNoteX [RunProgressViewModel] UNRESOLVED`, **and** the `=OutputBranchNote ` anchor fails. Fact 2: the `.Single(...)` lookup throws *"Sequence contains no matching element"* | revert, `-t:Rebuild` |
| R5 | `HasOutputBranch` → `HasOutputBranchX` | `…RunProgressPanel.xaml:71` | Fact 1: sweep + `=HasOutputBranch ` anchor. Fact 2: the **pre-mutation** observation fails — `Assert.Equal(Visibility.Collapsed, before)` reports `Visible`, because an unbound `Visibility` is `Visible`. **That is the demonstration that step 3 is not decorative.** | ″ |

Deliberately **not** used as injection points: `State`, `CanPublish`, `PublishNote`, `ChildrenNote` — each is
bound at two or three sites, so a one-line typo produces a partial red that is ambiguous to read. `:44`
`LedgerSummary` is a good third single-occurrence option if a third demo is wanted.

**After both: `git diff --stat -- src/` must print nothing.**

### G3 decides on its own / must escalate

- **On its own:** the floor within 18–22 once measured; the anchor set (keep at least `OutputBranchNote`,
  `HasOutputBranch` and one from the `Children` region); the local loc stub's exact format; whether the two
  facts share a factory.
- **Escalate:** a measured tuple count materially below 28; any need for a layout pass, an `ApplyTemplate` or
  a frame push; any temptation to hardcode `RunProgressViewModel` instead of reflecting; a red that is a real
  production defect needing more than a rename.

---

## 8. G4 — the five settings views nobody has parsed

**Commit boundary: ONE COMMIT PER VIEW**, in the order of §4. Each commit is a self-contained file with its
own measured floor, so a partial G4 is coherent and shippable.

**Commit subject template:** `Tests: parse the <X> settings view` — e.g. `Tests: parse the General settings
view`. If a commit fixes a defect the fact found (D3), the subject says so:
`Tests: parse the Optimize settings view, which found a dead command path`.

**Commit body must state, every time:** the **measured** path count and the floor derived from it; whether
the measured count matched the static expectation below (and if not, by how much and why); the red demo and
its message; `git diff --stat -- src/` empty; the post-commit suite total.

### Per-view file

`tests/Pia.Wpf.Tests/Views/<X>ParseTests.cs`, class `<X>ParseTests`, `[Collection("WpfApplicationStatic")]`,
`namespace Pia.Tests.Views;`, CRLF. One `[Fact]`:
`EveryBindingPath_ResolvesOnTheViewModelThatMarkupRootsItAt`.

Body, copying `SettingsAssistantViewParseTests.cs:60`–`:96` in shape:

```csharp
// The root DataContext is CHECKED, not assumed: SettingsView.xaml:<N> hosts this view with
// DataContext="{Binding <Vm>}", so the walk below is only sound while that property still has this type.
var root = typeof(SettingsViewModel)
    .GetProperty(nameof(SettingsViewModel.<Vm>), BindingFlags.Public | BindingFlags.Instance)!
    .PropertyType;
Assert.Equal(typeof(<VmType>), root);

var bindings = WpfStaHost.Run(() =>
    BindingPathWalker.Describe(new Pia.Views.SettingsViews.<X>(), root));

Assert.True(bindings.Length >= MinimumBoundPaths, "...");   // per-view const in THIS file (D2)
// named anchors …
var unresolved = bindings.Where(b => b.EndsWith("UNRESOLVED", StringComparison.Ordinal)).ToArray();
Assert.True(unresolved.Length == 0, "...");
```

**Procedure for every view, in this order — do not skip step 1:**

1. Write the fact with `MinimumBoundPaths = 1` and a temporary dump of `bindings`. Run it. **Read the actual
   count and the actual projected strings.**
2. Set the floor from the measured count (band below). Pick the named anchors **from the dump**, never from
   this document — the `[ContextType]` label in particular must be copied, not guessed.
3. Remove the dump. Run the red demo. Revert. Rebuild. Gate. Commit.

**The `Assert.Equal(typeof(<VmType>), root)` line is worth keeping even though its right-hand side is
hardcoded** — it is the precedent's shape (`SettingsAssistantViewParseTests.cs:67`) and it fails on a re-host
instead of silently walking the wrong type. **It is only meaningful while the reflected type is unique**,
which is the W11 trap seen from the other side: `PersonasView` is undetectable precisely because
`SettingsViewModel.PersonasVm` and `AssistantSettingsViewModel.PersonasVm` (`:35`) are *both*
`PersonaSettingsViewModel`. **Measured for the five, so the builder need not re-check:**
`SettingsViewModel.cs:16`–`:22` declares seven sub-VM properties with **seven distinct types**
(`ProvidersSettingsViewModel`, `OptimizeSettingsViewModel`, `AssistantSettingsViewModel`,
`GeneralSettingsViewModel`, `AccountSettingsViewModel`, `PluginsSettingsViewModel`,
`PersonaSettingsViewModel`). No two of G4's five collide, so the assertion bites for all five.

### The five, with hosts, floors and per-view specifics

| # | View (fully qualified) | Host site | Reflected root | Static count | **Floor** | Required anchors + specifics |
|---|---|---|---|---|---|---|
| 1 | `Pia.Views.SettingsViews.GeneralView` | `SettingsView.xaml:123` | `SettingsViewModel.GeneralVm` → `GeneralSettingsViewModel` | **40** (34 + 6) | **26** | **The two-halves assertion is mandatory here.** `GeneralView.xaml:452` re-roots: `<ScrollViewer … DataContext="{Binding PrivacyVm}">` → `GeneralSettingsViewModel.PrivacyVm` : `PrivacySettingsViewModel` (`GeneralSettingsViewModel.cs:27`), carrying the 6 paths at `:458`, `:487`, `:491`×2, `:502`, `:513`. Assert `=PrivacyVm ` **and** one path tagged `[PrivacySettingsViewModel]` — without the second, a section that stopped being walked still passes. **Read the failure message with W2 in hand:** this is the first new test in the repo over a view with an internal re-root, so if `PrivacyVm` itself ever fails to resolve, the null-context cascade reports **7 UNRESOLVED lines for 1 defect** (the re-root plus all 6 paths under it). Fix the re-root and re-run before touching any of the six. Everything sits in a `TabControl` (`:17`) with four `TabItem`s (`:19`, `:97`, `:230`, `:451`); **`TabItem` reachability is proven, not assumed** — the green settings-Assistant fact asserts two paths that live inside `<TabItem>`s. Out of reach: 24 template bindings, 4 `RelativeSource` (`:392`, `:411`, `:529`, `:541`), 3 `DataTrigger`s (`:399`, `:418`, `:431`). Local `UserControl.Resources` (`:8`–`:15`, incl. `EnumToLocalizedStringConverter`, `CategoryDisplayConverter`) must never be split from the file. |
| 2 | `Pia.Views.SettingsViews.AccountView` | `SettingsView.xaml:136` | `SettingsViewModel.AccountVm` → `AccountSettingsViewModel` | **61** at runtime (46 own + 15 nested) | **40** | Contains `E2EEOnboardingView` at `:218` as a plain logical child with **no `DataContext`**, so its 15 `OnboardingViewModel.`-prefixed paths are walked under `AccountSettingsViewModel` — **which is correct**. Anchor on one of them (e.g. an `=OnboardingViewModel.…` string from the dump) to prove the nested view was reached. **Plus the D5 duck-type fact** (below). Known gaps to name in the doc comment, not chase: 4 `<Condition Binding="…">` inside `MultiDataTrigger`s (`:86`, `:87`, `:230`, `:231`) are invisible; `x:Name="LoginPasswordBox"` (`:51`) is driven from code-behind, so `AccountSettingsViewModel.LoginPassword` is written by **no binding at all** and is permanently invisible to this technique. |
| 3 | `Pia.Views.SettingsViews.ProvidersView` | `SettingsView.xaml:84` | `SettingsViewModel.ProvidersVm` → `ProvidersSettingsViewModel` | **12** | **8** | 12 of 28 bindings are inside the `ItemTemplate` (`:120`–`:245`); 4 `RelativeSource AncestorType=UserControl` command bindings (`:188`×2, `:218`, `:230`) filtered by design. Watch `:85` `GoToCloudSyncCommand` — a plausible first red (D3). |
| 4 | `Pia.Views.SettingsViews.OptimizeView` | `SettingsView.xaml:97` | `SettingsViewModel.OptimizeVm` → `OptimizeSettingsViewModel` | **8** | **5** | **Fully qualify** — `Pia.Views.OptimizeView` also exists and compiles (hazard 10). Anchor on the cross-VM hop `:20` `{Binding ProvidersVm.GoToProvidersTabCommand}`, which resolves through `OptimizeSettingsViewModel.ProvidersVm` (`:70`, expression-bodied `=> _providersVm`) — the most interesting path in the file and a plausible first red. 11 of 23 in the template at `:105`+; 1 `DataTrigger` at `:56` invisible. |
| 5 | `Pia.Views.SettingsViews.PluginsView` | `SettingsView.xaml:149` | `SettingsViewModel.PluginsVm` → `PluginsSettingsViewModel` | **6** | **4** | Weakest floor in the batch — **say so in the doc comment** rather than let 4 read as meaningful. 12 of 19 bindings in the `ItemTemplate` (`:76`–`:127`), 1 `RelativeSource AncestorType=ItemsControl` (`:127`). Watch `:26` `GoToAccountCommand`. |

**The `AccountView` duck-type fact (D5)** — `E2EEOnboardingHosts_AllExposeAnOnboardingViewModelOfTheSameType`:

```csharp
// E2EEOnboardingView.xaml is instantiated at AccountView.xaml:218 and WizardSteps/AccountSetupStep.xaml:269
// with NO DataContext at either site, and every one of its 15 bindings is prefixed "OnboardingViewModel.".
// It is written against whatever host DataContext happens to expose a member of that name — no interface,
// no base class enforces it. Renaming the AccountSettingsViewModel one breaks the settings page while the
// wizard keeps working, silently.
```
Reflect `OnboardingViewModel` off **both** `AccountSettingsViewModel` (`:28`) and `FirstRunWizardViewModel`
(`:115`); assert both exist and that their `PropertyType`s are equal. That is strictly more coverage than a
standalone `E2EEOnboardingViewParseTests` would have produced.

**The `PersonasView` assertion (D5)** — one line **added** to `SettingsAssistantViewParseTests`, in the last
G4 commit, with a comment naming what it is for:
`Assert.Contains(bindings, b => b.Contains("=AddPersonaCommand [PersonaSettingsViewModel]"));`
(exact string **from the dump**). It pairs with the existing `=PersonasVm ` at `:89` as the two-halves shape,
and it converts PersonasView from incidentally-covered to asserted. **Note in the commit that G1 edited no
assertion and G4 adds one** — the two claims must not be confused later.

### Standalone loading is safe for all five — verified

`WpfStaHost.Start()` constructs `new Pia.App(); app.InitializeComponent();` (`WpfStaHost.cs:92`–`:93`)
without `Run()`/`OnStartup`, so `Application.Current.Resources` carries all of `App.xaml`'s merged
dictionaries and inline converters. An exhaustive `{StaticResource}` sweep across all seven candidate views
against (own `UserControl.Resources`) ∪ (App.xaml + merged, 225 keys) found **zero unresolved keys**.
`AccountView`, `ProvidersView` (and `PersonasView`, `E2EEOnboardingView`) have **no** local `Resources` at
all and depend entirely on `App.xaml` — if the host ever failed to create the `Application` they throw in the
constructor rather than failing an assertion. The only two keys the sweep flagged are
`BasedOn="{StaticResource {x:Type ui:Button}}"` at `GeneralView.xaml:397`/`:416`, a Wpf.Ui implicit-style key
resolved at style-application time. Not a parse hazard.

### RED demo — one per view, mandatory

Pick a **single-occurrence** path from the view's own dump (do not reuse an anchor that appears twice), rename
it in the `.xaml` with a trailing `X`, `-t:Rebuild`, and confirm **two** things fail: the unresolved sweep
message names `…X … UNRESOLVED`, and the corresponding `Assert.Contains` anchor. Revert, rebuild, confirm
`git diff --stat -- src/` is empty. Suggested injection points (verify they are single-occurrence in the dump
first): `GeneralView.xaml:458` (a `PrivacyVm`-scoped path — it also demonstrates the re-root),
`AccountView.xaml` one of the `OnboardingViewModel.`-prefixed lines (it demonstrates the nested walk),
`ProvidersView.xaml:85`, `OptimizeView.xaml:20`, `PluginsView.xaml:26`.

### G4 decides on its own / must escalate

- **On its own:** each per-view floor **from its measured count** within the band ~60–70 %; anchor selection
  from the dump; fact/file naming; dropping views 5, then 4, then 3 if the round runs short (and telling the
  docs commit the real number).
- **Escalate:** any measured count below 4; any red that needs more than a rename or a one-line binding fix
  (D3); any temptation to write a `PersonasViewParseTests` or `E2EEOnboardingViewParseTests` (D5); any
  temptation to reflect `PersonasView`'s root off `SettingsViewModel.PersonasVm` (W11 — it type-matches by
  coincidence and proves nothing).

---

## 9. The docs commit — last, after the final G4 commit

Not a work group; it is the batch's Acceptance clause. **Commit subject:**
`Docs: record Batch 14, and take three items off the Rank-1 round`

The full edit list is **Appendix A**, written as an edit list so Synthesize is not a scavenger hunt. Two of
its items quote a count that only exists after the last G4 commit, which is why this is last.

**The one structural adaptation** — Batch 13's block at `00-OVERVIEW.md:1474`–`:1490` is the style template,
and its lead reads "*FOUR of those items got SHORTER … Read "shorter" strictly — **none of the four is
closed***". **Batch 14 must not copy that clause.** Batch 14 has items coming **off outright** (the branch
line, and the jobs row given D1 = (a)), so the lead has to **split the count**: *"N items moved: M come off
outright and K are shortened."* Copying "none is closed" unchanged would be a false statement in the
*deflationary* direction, and this file's discipline is against inflation in **either**.

---

## 10. Acceptance

1. **Everything is covered by EXECUTED facts** — the jobs row (ten item-scoped paths + four command
   identities + four `CommandParameter` identities), the run panel's non-templated paths **and** its branch
   line rendered in both states, and the settings views G4 landed. Executed, not written: every new XAML fact
   has been **demonstrated RED before green**, with the typo, the file:line, the failure message and the
   revert recorded in its group's commit body.
2. **The gate is green in BOTH configurations under `-t:Rebuild`** — `0 Warning(s) / 0 Error(s)` Debug and
   Release, suite `failed: 0`, with the **measured chain** `2723 → … → N` recorded group by group (§3).
3. **`git diff --stat -- src/` is empty at every group commit**, stated in every commit body.
4. **`00-OVERVIEW.md`'s Rank-1 list is SHORTER BY NAME.** Three distinct items move: the **jobs row
   template** (one defect, written up in two places — `:1684` and `:1495` clause (a) — both edited
   identically), the **branch line** (`:1732`, off outright), and the **unparsed-views** bullet (`:1748`,
   shortened, plus its twin in the `:776`–`:811` callout). **R11 (`:1512`) goes MOSTLY CLOSED → CLOSED** —
   a required edit, but it is a *risk* in the accepted-risks subsection, **not** one of Phase 3's fourteen
   enumerated smoke items; counting it as a fourth shortened smoke item would be the inflation this file's
   discipline exists to prevent.
5. **Each shortened item is edited IN PLACE with the manual half that survives stated**, in Batch 13's strict
   style with the split-count adaptation of §9.
6. **Nothing is added to that list.** Batch 14 *names* three residual gaps it measured — the `Style.Triggers`
   blind spot (six `DataTrigger`s drive E2EE's whole state machine), `AccountSettingsViewModel.LoginPassword`
   (written from code-behind, bound by nothing, permanently invisible), and the `Run.Text` inline question —
   and they go in the new "Opened by Batch 14" section as **pre-existing gaps now measured**, explicitly not
   as new Rank-1 items.

---

## Appendix A — the `00-OVERVIEW.md` edit map

Fifteen edits. Line numbers are at `2266bf7` and shift as you go — **work bottom-up** (item 15 first, item 1
last) or re-locate by quoted text.

| # | Anchor | Kind | Edit |
|---|---|---|---|
| 1 | chronicle table `:82`–`:111`, last row is 20 at `:111` | ADD | Append **row 21**: `| 21 | \`<first>\` → \`<last>\` | **[Batch 14](14-view-coverage-debt.md)** — … | ✅ done |`. Pin first→last **inclusive**; do **not** pin →HEAD (Batch 09's row carries that correction), and do **not** fold the docs commit into the range if it lands after (`aa5beb9` set that precedent). Row 19 (Batch 13, `:110`) is the closest stylistic model and ends with a gate figure. |
| 2 | Rank-1 row `:569` | EDIT (**D6 scope call — make it**) | It still reads "Phase 3 lengthened it by FOURTEEN items and shortened nothing" and has never registered Batch 13's shortening of four. Credit **both**: 13 shortened four, 14 moves three (two off, one shortened). Add 14's post-batch gate figure. |
| 3 | Rank-2 row `:570` | MOVE | Batch 14 moves to the shipped block (`:572`–`:583` style: `| — | 14 | … | ✅ **shipped** \`first\`→\`last\` — <note> |`). Batch 08 becomes Rank 2. |
| 4 | `:582` | CORRECT (**D6**) | Batch 13's "the **only** batch so far that made Rank 1 **shorter**" → "the **first** batch…". False the moment 14 ships. |
| 5 | `:585`–`:597` | ADD | The Rank-2 promotion paragraph is future-tense ("Batch 14 **enters** at Rank 2 and pushes 08 to Rank 3"). Per this file's habit (documented at `:657`–`:660`, "each shipped batch retires the sentence that promoted it"), **leave the stale sentence and add the retiring one beneath it**. |
| 6 | `:776`–`:811` callout — item 1 at `:778`, plus `:792` and `:811` | SHORTEN (**write LAST**) | "Every other `View` in the repo still carries the full silent-misspelled-binding hazard, unchanged" is already stale at HEAD (two views parsed) and badly stale after G4. Narrow "XAML changes outside `AssistantView` still need manual smoke" (`:792`, `:811`) to **name the parsed set**. Use the same number as item 14. Not closed. |
| 7 | `:1227`–`:1229` (Batch 04 item 1) | **CORRECTION, not a shortening** (**D6**, F7) | Strike "nothing parses `Views/SettingsViews/AssistantView.xaml`" — false since Batch 13. The item's surviving half (*toggle it, restart, confirm it stuck*) is untouched by Batch 14. Label the edit as a correction in the summary block. |
| 8 | `:1385`–`:1404` (Batch 05 toggle debt), esp. `:1394` | **CORRECTION, not a shortening** (**D6**, F7) | Strike "never constructed by any test" and "Until someone writes that one, the debt stands". Both false at HEAD; `:1487` already names them. Surviving half unchanged. |
| 9 | `:1457`–`:1458` | CORRECT | "all unreachable from any test because there is no View test (R11)" is false at HEAD and more so after G3. Fix the parenthetical **without** implying items (iii), (iv), (v) moved — they do not (F2, F3). |
| 10 | `:1492`–`:1499`, **clause (a)** at `:1495` | REMOVE clause | "(a) the row `DataTemplate`'s bindings, which a logical walk cannot reach" — **deleted**, paragraph re-lettered/re-worded to (b)+(c) only. Clauses (b) (DE/FR of a German-heavy section) and (c) (two-device owner mismatch) **survive verbatim** (F1, F4). |
| 11 | `:1512`–`:1523` (R11) | MOSTLY CLOSED → **CLOSED** | The one clause holding it open is "**The branch line is the one that stays**". G3 removes it. Strike `MOSTLY`; rewrite that sentence to name Batch 14 G3; keep the original reasoning beneath, per this section's convention. All three named surfaces are now XAML-covered (avatar row ✅ 13, roster ✅ 13, branch line ✅ 14). |
| 12 | `:1684`–`:1697` (Batch 09, jobs row template) | **OFF OUTRIGHT** | Given D1 = (a), all fourteen paths are pinned. On the Batch 03 withdrawn-row-render precedent (`:1489`–`:1490`): it was never a round trip, it was a coverage gap standing in for a test nobody had written. *(Contingency: had D1 landed on (b), this would be SHORTENED with the surviving half "the four `RelativeSource` command bindings — a renamed command still breaks all four buttons silently, so click them by hand." It did not. Recorded so the reasoning is auditable.)* |
| 13 | `:1732`–`:1738` (Batch 13, branch line) | **OFF OUTRIGHT** | The bullet's own text says the gap is "a scope choice rather than a limitation … with no technical obstacle." G3's walk + render fact cover the whole content of the debt. No surviving manual half. |
| 14 | `:1748`–`:1751` (Batch 13, every other view unparsed) | SHORTEN (**write LAST**) | Be shorter *by name*, with the measured residue. `src/Pia.Wpf/Views/` holds **13** top-level view XAMLs + **8** in `SettingsViews/` = **21**. Parsed before this batch: **2** (`Views/AssistantView.xaml`, `Views/SettingsViews/AssistantView.xaml`; `Controls/Assistant/RunProgressPanel.xaml` was parsed transitively). After a **full** G4, state it in the two-number form the code actually supports: **7 of the 21 have a parse test of their own, and 9 of the 21 are walked** — the two extra being `PersonasView` (a logical child of the settings `AssistantView`) and `E2EEOnboardingView` (a logical child of `AccountView`), both now *asserted* rather than incidental per D5. **The settings folder is then complete.** Residue: the **twelve** non-settings views in `Views/` (`AssistantHistoryView`, `FirstRunWizardWindow`, `HistoryView`, `MeetingAttendeeOverlay`, `MemoryView`, `NavigationSidebarView`, `OptimizeView`, `RemindersView`, `SettingsView`, `TodoPanelControl`, `TodoView`, `VoiceModeOverlay`), plus **7** more in `Views/WizardSteps/` and the `Controls/` tree — neither of which the 21 counts, so do not let "9 of 21" read as 43 % of the repo's XAML. **If G4 partially landed, the number is the number that landed.** |
| 15 | after `:1751`, before the `---` at `:1753` | NEW SECTION | `### Opened by Batch 14 (2026-08-01) — known, reasoned, not closed`. Follow the 09→13 precedent: **blank line, then the heading, no `---` separator** (the rules at `:1406`, `:1667`, `:1753` are used before Phase 3 and before Batch 09, not between the two 2026-08-01 sections). Contains: the **split-count** summary block (§9) in `:1474`–`:1490`'s style; the corrections of items 7, 8, 9 labelled as corrections; the three measured-but-not-new gaps from Acceptance §6; and Q1–Q4 from Appendix C. |

### False shortenings — claims to reject, out loud, in the summary block

Ordered by how likely someone is to make them.

- **F1 — every DE/FR item. Batch 14 touches none.** 04-6, 03-5, §8-9, fix-ii, cons-iii's last clause,
  cons-iv's "in all three locales", Batch 09 clause (b). The technique runs against the **neutral (EN) resx
  by construction** (`SettingsAssistantViewParseTests.cs:51`–`:54`: no test calls `SetCulture`), and clipping
  is a **layout** property needing a measure pass, which every technique here deliberately avoids. G2 renders
  the German-heavy scheduled-jobs section and still proves nothing about it.
- **F2 — Phase 3 consolidation item (iv). Considered and REJECTED.** The one a reviewer will push back on,
  because G3's render fact is literally a sub-clause of "on success the branch line appears where the offer
  was". Discriminator: **G3 drives `HasOutputBranch = true` on a constructed ViewModel; (iv) is about a
  worktree run whose run-branch commit FAILED producing that state, and a Publish click retrying the
  commit.** Nothing in G3 exercises promotion, the `branchCommittedAtUtc` stamp, or the retry.
- **F3 — consolidation items (iii) and (v).** (iii) asks whether two lines "read as one coherent statement
  rather than as a contradiction" — a judgement. (v) asks that an *ordinary* run show none of the three note
  lines — a ViewModel-state fact about which run produces which note, not a XAML fact. Correcting `:1457`'s
  false parenthetical does **not** move them.
- **F4 — Batch 09 clause (c), the two-device owner-mismatch check.** High risk, because `OwnedByThisDevice`
  **is** one of G2's ten paths. G2 pins the path and renders the line with the flag forced false; it cannot
  produce a second device. The Batch 09 bullet at `:1698`–`:1701` states the discriminator itself. Same for
  `StatusIsKnown`/`StatusLabel`.
- **F5 — the `loc:Str` Expander header, and every `Content=`/`ToolTip=` loc key.** Someone will read "G3
  pins **every** non-templated path" and conclude the header is covered. It is not: `loc:Str` is an
  explicit-`Source` extension and `TargetsDataContext` skips explicit `Source` **by design**; `Header` is
  also invisible to a logical walk. Batch 03's `:1330`–`:1331` caveat and Batch 13's `:1739`–`:1747` bullets
  survive Batch 14 **unchanged**, and G4 inherits the same limit for all five views.
- **F6 — G1 shortens nothing.** A mechanical move of five private helpers, behaviour unchanged. The easiest
  credit to take falsely; say so explicitly.
- **F7 — 04-1 and Batch 05's toggle debt cannot be shortened *again*.** Batch 13 took their silent halves.
  Their surviving half is *"toggle it, restart, confirm it stuck"* — persistence to disk, which no parse or
  render fact reaches. Neither CheckBox is in a G4 view. Edits 7 and 8 above are **corrections of stale
  prose**, and must be labelled as such.
- **F8 — §8-6 and §8-7.** Already shortened by Batch 13. Residues are "persists across a restart" and
  "whether the glyph is legible at 20×20 and the accent ring looks right". Untouchable.
- **F9 — Batch 03's UI-thread-blocking item (`:1303`–`:1313`)**, which says in so many words "The manual
  smoke round owns it." A `LoadContent()` render on a detached element is not a perf measurement.
- **F10 — everything needing a live provider, a live MCP server, a real repo or a restart:** 04-2, 04-3,
  04-4, 04-5, 04-7, 03-1, 03-2, 03-3, 03-4, §8-1…§8-5, §8-8, fix-i. Batch 14 adds nothing to any of them.

---

## Appendix B — measured reference data

**Do not treat these as the source of a floor** (D2 — measure and print). They are the *expected* values; a
material divergence is a finding.

### B.1 — the jobs row template, `AssistantView.xaml:547`–`:601` (W4)

Ten item-scoped paths, with target DP — note only **six** land on `TextBlock.Text`, so a
`FindTextBlocks(row).Select(tb => tb.Text)` sweep copied from the precedent covers 1–6 (plus the `loc:Str`
line at `:574`) and **misses 7, 8, 9, 10 entirely**:

`Name`:553 Text · `Query`:557 Text · `KindLabel`:562 Text · `RecurrenceLabel`:565 Text · `StatusLabel`:568
Text · `NextFireAt`:571 Text (`StringFormat=g`) · `OwnedByThisDevice`:579 **Visibility** (via
`InverseBooleanToVisibilityConverter`) · `ToggleLabel`:585 **`ui:Button.Content`** · `StatusIsKnown`:587
**IsEnabled** · `CanRunNow`:592 **IsEnabled**.

Four commands `:583`/`:588`/`:593`/`:596`, four `CommandParameter="{Binding}"` `:584`/`:589`/`:594`/`:597`.

Resolution chain, all reflected, nothing hardcoded: `SettingsView.xaml:110` → `SettingsViewModel.AssistantVm`
: `AssistantSettingsViewModel` (`SettingsViewModel.cs:18`) → re-root at `AssistantView.xaml:525`
`DataContext="{Binding ScheduledJobsVm}"` → `AssistantSettingsViewModel.ScheduledJobsVm` (`:40`) →
`ScheduledJobsSettingsViewModel.Jobs` : `ObservableCollection<ScheduledJobRow>` (`:34`). All ten row members
exist on `ScheduledJobRow` (`ScheduledJobsSettingsViewModel.cs:449`–`:494`, `public sealed class`, plain, no
INPC, `required`/`init`); `CanRunNow` is computed (`:493`). All four commands are `[RelayCommand]`-generated
(`:243`, `:342`, `:375`, `:404`).

### B.2 — the run panel, 28 walkable tuples / 23 distinct paths

By surface: state chip `State`×3 (`:17`, `:18`, `:21`) · result note `TruncationNote`:24, `IsTruncated`:27 ·
continue `ContinueCommand`:33, `CanContinue`:35 · publish offer `PublishCommand`:40, `CanPublish`:42,
`CanPublish`:63 · ledger `LedgerSummary`:44 · activity `CurrentActivity`:50, `HasCurrentActivity`:53 ·
publish result `PublishNote`:64, `PublishNote`:67 · **branch line `OutputBranchNote`:68,
`HasOutputBranch`:71** · steps `Steps`:74 · timeline `IsTimelineExpanded`:117, `TimelineNote`:120,
`IsTimelineTruncated`:122, `HasNoTimeline`:125, `HasTimelineReadError`:130, `Timeline`:131 · children
`HasChildren`:179, `ChildrenNote`:181, `ChildrenNote`:183, `Children`:184.

**All 23 resolve on `RunProgressViewModel`** — verified member by member. The spec's "six surfaces"
(`:74`–`:75`) cover 12 of the 28; **the walk covers 16 more**, the timeline and children regions the spec's
list omits entirely.

Excluded (26, inside four `ItemTemplate`s): Steps `:75`–`:107` (9, `StepRowViewModel`), Timeline `:132`–`:165`
(5, `TimelineRowViewModel`), Children `:185`–`:253` (8, `ChildRunRowViewModel`), child-timeline `:221`–`:248`
(4, doubly unreachable). 28 + 26 = 54 = every `{Binding` in the file.

### B.3 — G4's five views

Static walker-visible counts, measured by `XDocument` parse at `2266bf7` (skipping `Style`/`Setter`/`*Trigger`/
`Condition`/`DataTemplate`/`ControlTemplate`/`*.Resources`/`*.ItemTemplate` subtrees and applying
`TargetsDataContext`), **not** by running WPF: General **40**, Account **46** own / **61** with the nested
E2EE view, Providers **12**, Optimize **8**, Plugins **6**. Total walker-visible across all seven candidate
views: 129.

**One known divergence risk, non-blocking:** `Run.Text` bindings inside a `TextBlock` have no precedent in
the existing green tests — `AccountView.xaml:164`, `:174`, `:184`, `:271` (4 of 46) and
`E2EEOnboardingView.xaml:235` (1 of 15). If `LogicalTreeHelper.GetChildren(TextBlock)` does not yield
`Inlines`, those five drop out of the live count. No floor above is threatened either way. Do not chase it;
report the measured number.

---

## Appendix C — open questions

**Q1 — the `Resolves`-on-null-context cascade (W2). Not settled; deliberately not fixed here.**
The comment says a null context makes descendants *unknown*; the code makes them *failed*. Latent today
because no re-root fails. Fixing it means a tri-state (`bool? Resolves` or a fourth `Unknown` label in the
projection), which changes the format string three files match on and the boolean one assertion filters —
i.e. it is a behaviour change and it breaks G1's own proof. **A follow-up batch owns it.** Whoever picks it
up should note that the first real failing re-root will produce N findings where the comment promises 1, and
that is the moment it becomes worth the churn.

**Q2 — `Style.Triggers` coverage is the next real hole, and G4 measured how big.** Six
`<DataTrigger Binding="{Binding OnboardingViewModel.State}">` blocks inside `StackPanel.Style`
(`E2EEOnboardingView.xaml:26`, `:80`, `:135`, `:189`, `:212`, `:247`) **are the state machine** that decides
which of six panels is visible, and a logical walk sees none of them. Add `GeneralView`'s 3, `AccountView`'s
4 `MultiDataTrigger` `<Condition>`s and `OptimizeView`'s 1. No technique in this batch reaches any of them.
Not in scope; named so the next batch does not rediscover it.

**Q3 — `AccountSettingsViewModel.LoginPassword` is written by no binding at all.**
`AccountView.xaml:51` is `x:Name="LoginPasswordBox"` and `AccountView.xaml.cs`'s
`LoginPasswordBox_PasswordChanged` pushes the value from code-behind. Permanently invisible to this
technique, and to any binding-path technique. It is a **gap, not a hazard** — record it, do not chase it.

**Q4 — how many facts can the `WpfApplicationStatic` collection hold?** It is at 6 at `2266bf7` and this
batch takes it to **16**. The measured record (`WpfStaHost.cs:208`–`:213`, 2026-08-01 at `fcfa7d5`) is that an
**eighth frame-pushing** fact failed 3-of-3 before the `Pump()` rewrite. Every technique here adds zero frame
pushes, so the count should be irrelevant — but nobody has measured 15. **If the gate goes intermittent
during this batch, re-read that record before blaming anything else**, and record the finding either way.

**Q5 — is `Describe`'s format string worth a test of its own?** Three files' `Assert.Contains` anchors depend
on `=Path ` and `[ContextType]` appearing exactly as they do. After G1 the format lives in one place, which
is the fix; a fact asserting the format itself would be a tautology over a constant. Left unwritten
deliberately. Named in case a reviewer asks.
