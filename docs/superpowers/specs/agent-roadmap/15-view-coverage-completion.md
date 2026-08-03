# Batch 15 — View-coverage completion (the last twelve unparsed views)

**Phase 3 cleanup · Size S–M · Work on `feature/agent-run-spine`**, after
[Batch 14](14-view-coverage-debt.md) (shipped `86934c9`→`fa331ec`) and the managed-personas merge
(`cf571e51`, recorded as a snapshot addendum in [`00-OVERVIEW.md`](00-OVERVIEW.md), not as a batch)

Batch 12 made a view parseable, Batch 13 made the host survive more than seven of them, Batch 14 built the
shared walker and then proved — by attack, not by reading — that a walk rooted at a reflected *type* does not
catch a `DataContext` **re-host**. This batch spends the remaining headroom on the twelve views none of them
reached, on that same walker, with a host guard for every one.

**This batch SHORTENS the Rank-1 manual round and adds NOTHING to it.** No new string, no new control, no
behaviour: `git diff --stat <base>..<head> -- src/` must come out **empty**, measured, exactly as Batch 14's
did. It is the third piece of work on this branch to move that number down.

**Read the scope caution before the scope.** Batch 14's spec named seven settings views and G4 shipped five,
because `SettingsView.xaml` hosts only six and two of the named seven are not among them — the spec's recipe
was literally unexecutable for those two. The twelve below were enumerated from
`00-OVERVIEW.md:1049`–`:1052` and re-derived from disk (`13` top-level `Views/*.xaml` + `8` in
`Views/SettingsViews/`, minus the 7 already parsed and minus `SettingsView`, which is **constructed** by
`ViewHostDataContextTests` but never **walked**). If a group turns out to be unexecutable as written, narrow
it and say so in the batch record rather than forcing it.

## Goal

Bring every top-level `View` in the repo under a binding-path walk with a host-`DataContext` guard, so that
"a misspelled binding path is invisible to a green build and a green suite" stops being true of the
application shell. After this batch the residue is stated as a named set of **shapes** (`DataTemplate`
content, `Style.Triggers`, `RelativeSource`/`ElementName`, `loc:Str` through
`Content=`/`ToolTip=`/`Header=`) rather than as a set of **files**.

## Scope — four work groups

The twelve, with the host each one's root ViewModel comes from. The host column is the batch's real content:
three distinct hosting shapes appear here and only one of them is the shape Batch 14 built the recipe for.

| # | View | Host shape | Root ViewModel |
|---|------|-----------|----------------|
| 1 | `Views/AssistantHistoryView.xaml` | `App.xaml` `DataTemplate` | `AssistantHistoryViewModel` |
| 2 | `Views/HistoryView.xaml` | `App.xaml` `DataTemplate` | `HistoryViewModel` |
| 3 | `Views/MemoryView.xaml` | `App.xaml` `DataTemplate` | `MemoryViewModel` |
| 4 | `Views/OptimizeView.xaml` (**top-level**, not the settings view of the same name) | `App.xaml` `DataTemplate` | `OptimizeViewModel` |
| 5 | `Views/RemindersView.xaml` | `App.xaml` `DataTemplate` | `RemindersViewModel` |
| 6 | `Views/SettingsView.xaml` | `App.xaml` `DataTemplate` | `SettingsViewModel` |
| 7 | `Views/TodoView.xaml` | `App.xaml` `DataTemplate` | `TodoViewModel` |
| 8 | `Views/VoiceModeOverlay.xaml` | `AssistantView.xaml:574`, `DataContext="{Binding VoiceMode}"` | `VoiceModeViewModel` |
| 9 | `Views/MeetingAttendeeOverlay.xaml` | `AssistantView.xaml:582`, `DataContext="{Binding MeetingAttendee}"` | `MeetingAttendeeViewModel` |
| 10 | `Views/TodoPanelControl.xaml` | `AssistantView.xaml:521` **and** `OptimizeView.xaml:483`, **no** `DataContext` binding at either; the ctor NULLS it and `OnLoaded` assigns from the scoped provider | `TodoViewModel` |
| 11 | `Views/NavigationSidebarView.xaml` | `MainWindow.xaml:40`, no `DataContext` binding — **inherits** | `MainWindowViewModel` |
| 12 | `Views/FirstRunWizardWindow.xaml` | none in markup: DI-resolved, ctor sets `DataContext = viewModel` | `FirstRunWizardViewModel` |

### G1 — The seven `App.xaml` `DataTemplate` views, and a host guard that is stronger than G4's was

Rows 1–7. Every one carries `nav:ViewModelLocator.AutoWireViewModel="True"` **and** has an `App.xaml`
`DataTemplate` keyed on its ViewModel type; the template is what actually supplies the DataContext in the
running app (see G4 for what the attached property does, which is not what its name says).

One parse fact per view, on the Batch 14 recipe: construct, `BindingPathWalker.Describe(view, rootType)`,
assert a non-vacuity floor **well under** the measured count, assert **zero** `UNRESOLVED`.

**The host guard for these is a different and better shape than `ViewHostDataContextTests`' reflection**, and
this is the group's one genuinely new technique. The host relationship is not a property name to reflect off
— it is a resource in a dictionary. Read it:

```csharp
var template = (DataTemplate)Application.Current.Resources[new DataTemplateKey(typeof(HistoryViewModel))];
var view = template.LoadContent();          // the real host mapping, executed
Assert.IsType<Pia.Views.HistoryView>(view);
```

That catches a re-typed or deleted `DataTemplate` — the exact class of defect D1 caught for the settings
views — and it hands back the view instance the walk then uses, so the parse and the host check cannot drift
apart by construction. `LoadContent()` is one batch old (Batch 14 G2/G3) and needs no new machinery.

Extend `ViewHostDataContextTests` with the seven pairs rather than starting a new file: it is already "the
one fact in this repo that opens a HOST view's markup", and a second file with the same job invites the two
to disagree. Keep its non-vacuity count check — **seven templates must be FOUND**, not merely left
uncontradicted.

### G2 — The three views nested inside `AssistantView` / `OptimizeView`

Rows 8–10, and they are three different problems wearing one label.

**8 and 9 are the ordinary shape** and the existing recipe fits: the host site declares
`DataContext="{Binding VoiceMode}"` / `{Binding MeetingAttendee}`, so reflect the root off
`AssistantViewModel.VoiceMode` / `.MeetingAttendee` and add both to `ViewHostDataContextTests`' host walk of
`Pia.Views.AssistantView` — which that file already constructs for the `RunProgressPanel` check, so this is
two more sites on a tree it already builds. `VoiceMode` is declared `VoiceModeViewModel?`; there is nothing
to unwrap — the annotation is metadata and `PropertyType` is `VoiceModeViewModel`, so it compares directly.

**10 is the interesting one and it inverts the guard.** `TodoPanelControl`'s ctor sets `DataContext = null`
with a comment saying why — to *break* inheritance from the hosting view — and `OnLoaded` assigns a
`TodoViewModel` from the window's scoped provider. So its correctness condition is the opposite of every
other row: the two host sites must **NOT** bind `DataContext`, **and** the ctor must keep nulling it. If a
future edit removes that line the panel silently inherits `AssistantViewModel` at one site and
`OptimizeViewModel` at the other, and every path in it mis-binds with the build at zero warnings. Pin all
three halves:

- the walk, rooted at `TodoViewModel`, zero `UNRESOLVED`;
- `BindingPathWalker.BoundPath(panel, FrameworkElement.DataContextProperty)` is **null** at both host sites
  (`AssistantView.xaml:521`, `OptimizeView.xaml:483`) — found by type on the parsed host tree, not by index;
- a freshly constructed `TodoPanelControl` has a **null** `DataContext`.

The last one looks trivial and is the load-bearing one: it is the only assertion that reds if the ctor line
goes away.

### G3 — The two views with no markup host

**Row 11, `NavigationSidebarView`.** Hosted at `MainWindow.xaml:40` with no `DataContext`, so it inherits
`MainWindow`'s, which `MainWindow.xaml.cs:29` assigns as `MainWindowViewModel`. The host guard is therefore
in **code**, not markup, and the honest fact says so: assert the sidebar is a logical child of a constructed
`MainWindow`… **if** `MainWindow` can be constructed on the host thread at all. It takes a ViewModel and an
`IServiceProvider` and its ctor reaches `RootFlowView.DataContext = serviceProvider.GetRequiredService<…>()`.
**Try it; if it does not construct cleanly, do NOT force it** — fall back to walking
`NavigationSidebarView` alone against `typeof(MainWindowViewModel)` and record in the file that the host
relationship is asserted by reading `MainWindow.xaml:40` + `MainWindow.xaml.cs:29`, which is weaker and must
say so rather than imply a guard it does not have.

**Row 12, `FirstRunWizardWindow`.** A `FluentWindow`, DI-resolved, ctor sets `DataContext = viewModel` and
then subscribes to `_viewModel.WizardCompleted`, so a `null!` ViewModel NREs — a real
`FirstRunWizardViewModel` is required. Its ctor is nine interfaces plus two concrete ViewModels
(`E2EEOnboardingViewModel`, `E2EESetupStepViewModel`), and **both of those are all-interface ctors**, so the
whole chain substitutes. Two hazards to check before building on it, both empirical:

1. constructing a `Window` on the never-torn-down STA host — it is never `Show()`n, so no HWND, and
   `ShutdownMode` is already `OnExplicitShutdown`; but it does enter `Application.Current.Windows`, and this
   is the first `Window` any test creates;
2. the wizard's six steps live in `Views/WizardSteps/`, which every "unparsed views" statement on this branch
   explicitly excludes — walking the window reaches them if they are logical children, which would silently
   widen the batch. Decide it deliberately: either scope the walk to the window's own paths, or take the
   steps and say the set grew.

If (1) misbehaves in any way — a hang, a second `Application`, a suite-order dependency — **drop row 12 and
record it**. One deferred window is a smaller cost than a flaky host, and this branch has paid the flaky-host
price once already (Batch 13).

### G4 — Pin the `AutoWireViewModel` premise before anything rests on it

Eight views carry `nav:ViewModelLocator.AutoWireViewModel="True"`.
`ViewHostDataContextTests` states it "no-ops here (no Window, no service provider, so it only defers to a
Loaded that never fires)", and G1 depends on that statement being true — if the locator ever resolved a real
ViewModel at parse time, these facts would be walking a tree with a DataContext nobody intended.

**There is a stronger reading of that code that must be checked by execution, not by argument.**
`ViewModelLocator.GetViewModelType` is:

```csharp
viewName.Replace(".Views.", ".ViewModels.").Replace("View", "ViewModel")
```

`string.Replace` replaces **every** occurrence, so `Pia.Views.HistoryView` becomes
`Pia.ViewModels.HistoryView` and then `Pia.ViewModelModels.HistoryViewModel` — a namespace that does not
exist — which would make `GetViewModelType` return `null` for **every** view and the attached property inert
regardless of whether a provider is present. That is a reading of the source, and this batch must not act on
it as a finding until a test says so.

**But be precise about what a test here can and cannot settle, because the obvious fact is confounded.**
`GetViewModelType` is `private static` and unreachable. The only observable is: set the attached property on
a constructed view and read `DataContext`. That stays null under **both** candidate mechanisms — the
existing comment's (`GetScopedProvider` returns null, so it defers to a `Loaded` that never fires) and the
`Replace` reading above (`GetViewModelType` returns null for every view, so the attached property is inert
whatever the provider does). Discriminating between them needs `_serviceProvider` non-null, i.e. calling
`ViewModelLocator.Initialize` and mutating **process-wide static state** inside a shared-host collection.
**Do not.** That hazard is larger than the finding.

So write the fact G1 actually needs and no more: `AutoWireViewModel="True"` on a constructed view leaves
`DataContext` null, with a comment naming **both** mechanisms and saying which one is unproven. Verified as a
prerequisite: nothing in `tests/` calls `ViewModelLocator.Initialize` or `SetScopedServiceProvider`, so
`_serviceProvider` is null for the whole suite regardless of collection order.

**Report the `Replace` reading to the owner as a code-reading finding, explicitly unproven**, and do NOT fix
it. Fixing it would give eight views a DataContext they do not have today and turn a zero-production-change
batch into a behavioural one. File it, name it, leave it.

## Decisions

1. **One file per view**, named `<View>ParseTests.cs` under `tests/Pia.Wpf.Tests/Views/`, matching the seven
   that exist. Twelve short files beat one long one: a failure names its view in the class name.
2. **Non-vacuity floors are floors, not counts** — measure, then set the constant well under it, as
   `GeneralViewParseTests` documents. A floor that equals the measurement turns every ordinary markup edit
   into a test edit.
3. **Host guards extend `ViewHostDataContextTests`**; no second host file.
4. **The `AutoWireViewModel` finding is reported, not fixed** (G4).
5. **Rows 11 and 12 may be narrowed on evidence** (G3), and a narrowing is recorded in the batch record with
   the reason, not silently dropped.

## Guardrails

- **Zero production change.** `git diff --stat <base>..<head> -- src/` empty, measured and quoted. If a view
  cannot be parsed without a `src/` edit, that is a finding to report, not a licence to edit.
- **Zero warnings, Debug and Release, under `-t:Rebuild`.** Count `Roslyn.bincore.csc.exe /noconfig` (expect
  6), not `CoreCompile:` headers. A batch that adds thirty test files is exactly the batch that walks into
  the xUnit-analyzer trap.
- **Every new fact must be able to fail.** For at least one view per group, demonstrate the red: misspell a
  binding path in a scratch copy, or neutralise the mechanism, and show the fact goes red — then revert. A
  parse test that passes on a broken view is the failure mode this whole line of work exists to prevent, and
  Batch 14 shipped exactly that and only caught it by attack.
- **The host thread is shared and never torn down.** Twelve more parses is the largest single increase this
  host has taken. Watch for the Batch 13 signature — a 60 s timeout on whichever test runs *next*, never on
  the new one — and if it appears, that is the batch's real finding.
- **Do not widen into `Dialogs/`, `Dialogs/Overlay/`, `WizardSteps/` or `Views/Controls/`.** They are outside
  every statement of this debt on this branch, and G3's row 12 is the one place they can leak in.

## Acceptance

- All twelve views (or a narrowed set, with each narrowing recorded and reasoned) carry a parse fact with a
  non-vacuity floor and zero `UNRESOLVED`.
- Every parsed view has a host guard, or an explicit written statement of why its host relationship is
  asserted by reading rather than by execution.
- The `AutoWireViewModel` premise is settled by execution and reported.
- Debug and Release `-t:Rebuild` at `0 Warning(s) / 0 Error(s)`; suite `failed: 0`, with the known
  `AssistantChatConcurrencyTests` intermittent re-run isolated if it fires.
- `git diff --stat -- src/` empty.
- `00-OVERVIEW.md` updated: the batch row, the chain measured at every stop, and — the part that matters most
  — which Rank-1 items this shortens, **by name**, with none claimed closed that is not.
