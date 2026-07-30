# Batch 12 — The UI-dispatcher abstraction (`IUiDispatcher`) — ✅ SHIPPED

**Phase 2 · Size M · `feature/agent-run-spine` · `1dced2f` → `cac8251`**
(four build commits + a two-commit review fix pass; see the chronicle in [`00-OVERVIEW.md`](00-OVERVIEW.md))

This file now describes **the code as built**. The spec's own predictions are kept wherever the build
overturned them, because each was plausible and would otherwise be re-proposed — including the two that were
*wrong about this batch's central question* and were only caught because §3's Step 0 was executed instead of
reasoned about.

> **Build:** `dotnet build -p:EnableWindowsTargeting=true --no-incremental` → **0 errors, exactly 194
> warnings**, re-measured at `cac8251` by this documentation pass. Every commit in the range held that bar;
> the batch adds **zero** warnings, and a per-site warning diff against `73e15e8` (built in a throwaway
> worktree) shows the same 186 unique warning sites on both sides — the count did not merely coincide.
>
> **⚠️ Tests: WRITTEN, NEVER EXECUTED — including the acceptance test that is the point of the batch.**
> This *reverses* the baseline note the spec opened with ("net10.0-windows tests **do** execute on this
> machine"). They execute on the owner's **Windows 11** box; they do not execute where this batch was
> authored. On macOS `dotnet test` fails before running anything — *"To install missing framework …
> `Microsoft.WindowsDesktop.App` … osx-arm64"*, **0 tests executed** — and it was deliberately not attempted
> again after that was confirmed. So the `2157 / 0 failed` figure the spec quoted is an inherited measurement
> from an earlier commit, and **nothing in this range has been run**. What *was* measured here, and how, is
> §3 and §9. The smoke list is §10.

---

## What shipped

| Commit | What |
|---|---|
| `1dced2f` | `IUiDispatcher` + `UiDispatcherService` + `InlineUiDispatcher` + the `AddSingleton` registration. Foundation only — no ViewModel migrated, so the interface's only consumer was DI |
| `2fc593a` | Unit 1: `VoiceModeViewModel` (2 sites) then `AssistantViewModel` (6 sites); the `VoiceModeViewModel` exemption deleted; the narrower `[Fact]` added |
| `d6dd73f` | Unit 2: `TranscriptOverlayViewModel.DispatchToUi` onto the injected dispatcher, `MeetingAttendeeViewModel` forwarded; the last two exemptions deleted |
| `aca30bd` | The acceptance test — `WpfStaHost` (one STA thread, running dispatcher, the process's only `Application`), `WpfApplicationCollection`, `AssistantViewParseTests` |
| `39025cc` | Fix pass: a **queued** marshal failure is logged instead of lost; the exemption comment's second root named; three doc corrections |
| `cac8251` | Fix pass: the STA host keeps pumping when its own startup fails; `UiDispatcherServiceTests` (5 facts) pins the three member semantics; the loc sweep gets a non-vacuity anchor |

**16 files, +981/−36.** 7 new (`IUiDispatcher.cs`, `UiDispatcherService.cs`, `InlineUiDispatcher.cs`,
`UiDispatcherServiceTests.cs`, `WpfStaHost.cs`, `WpfApplicationCollection.cs`, `AssistantViewParseTests.cs`),
9 edited (4 ViewModels + `UiThreadViewModel.cs` + `Bootstrapper.cs` + `DependencyInjectionTests.cs` +
2 ViewModel test files). §4's cost table predicted 4 new / 9 edited — see "Deviations".

---

## 1. Step 0: the measurement, and the technique that made it possible

**This is the most reusable thing in the batch, and nobody else in this repo knew it was possible.**

The spec's §3 ordered a measurement before any code: delete all four exemption names and run *only*
`ViewModels_MustNotReference_SystemWindows`. On macOS that is unrunnable through xunit — the whole test host
needs `Microsoft.WindowsDesktop.App`. But the rule itself does not:

**`NetArchTest.Rules` 1.3.2 targets `netstandard2.0` and is built on `Mono.Cecil` — pure static analysis over
the assembly's metadata, no runtime loading — and it exposes `Types.FromFile(string)` beside
`Types.InAssembly(Assembly)`.** So a throwaway **`net10.0`** console project (which *does* run on macOS) can
reference `NetArchTest.Rules` 1.3.2 + `Mono.Cecil` 0.11.5, take the path of the built
`net10.0-windows10.0.17763.0/Pia.Wpf.dll`, and execute the repo's **real** architecture rules verbatim — the
selection chain copied character for character out of the test file, only `InAssembly` swapped for `FromFile`.
Build the DLL first with `dotnet build -p:EnableWindowsTargeting=true --no-incremental`; the probe reads the
same bytes the Windows test host would.

That turned Step 0 from an inference into a measurement, and it was used again after every commit in this
range. It generalises: **five** of the repo's architecture-test files are NetArchTest-shaped and therefore
measurable this way on macOS — `DependencyInjectionTests`, `NamingConventionTests`, `LayerDependencyTests`,
`MvvmPatternTests`, `AsyncSafetyTests`. Two caveats, both hit during this batch:

- Rules that end in `.GetTypes()` and then apply LINQ over **reflection** `Type` objects (that is
  `NamingConventionTests`, `MvvmPatternTests`, `AsyncSafetyTests` and
  `ViewModels_MustOnlyInject_InterfacesOrViewModels`) cannot be run as written — reflection over `Pia.Wpf`
  outside the Windows test host throws `FileNotFoundException`. Two workarounds were used and both are sound
  if you say which one you used: run the NetArchTest half of the selection and **invert** the assertion so
  `FailingTypeNames` enumerates the scanned set (that is how "the naming rule really does scan
  `UiDispatcherService`, and 'Service' really is on its allow-list" was established), or re-implement the
  predicate directly over Cecil (`IsInitOnly` for the readonly-field rule, `AsyncStateMachineAttribute` +
  `void` for `async void`).
- **`DiRegistrationTests` and `BootstrapperGraphValidationTests` cannot be measured at all** — they *invoke*
  `Bootstrapper.ConfigureServices` by reflection, i.e. real execution. Those were verified by inspection, and
  this file says so rather than implying a run.

**One property of NetArchTest that a probe teaches you and reading does not: a rule over an EMPTY type set
returns `IsSuccessful = true`.** Every `HaveName(...)`-scoped fact therefore needs a non-vacuity guard or a
rename silently turns it green. That is why the new `[Fact]` carries `Assert.Single(target)`, and why every
probe run in this batch included a control expression against `System.Object`.

### The measured result

Blanket rule (`ResideInNamespace("Pia.ViewModels")`, `DoNotResideInNamespace("Pia.ViewModels.Models")`,
`ShouldNot().HaveDependencyOn(prefix)`) with **all four** exemption names deleted, against the pre-batch DLL:

| probe prefix | flagged types |
|---|---|
| `System.Windows` | Assistant, TranscriptOverlay, VoiceMode |
| `System.Windows.Threading` | Assistant, TranscriptOverlay, VoiceMode |
| `System.Windows.Application` | Assistant, TranscriptOverlay |
| `System.Windows.Media` | Assistant |
| `System.Windows.Media.Imaging` | Assistant |
| `System.Windows.Media.Imaging.BitmapSource` | Assistant |

**Three corrections came out of that, and all three changed what the batch did:**

1. **`AssistantViewModel` IS flagged for `System.Windows.Media.Imaging`** — prefix matching confirmed at full
   type-name depth, which the spec called "almost certainly" and refused to assume. So its exemption
   **survives this batch** and only its *comment* changed. The story it replaced ("flagged transitively
   because it creates `VoiceModeViewModel`") is provably not the mechanism: constructing a `Pia.ViewModels`
   type is not a reference to `System.Windows`.
2. **`MeetingAttendeeViewModel` was NEVER flagged — not even with its base still dirty.** Its entry was
   **vestigial**, and the reason is a fact about the tool worth carrying forward: **NetArchTest 1.3.2 does not
   resolve base-type dependencies transitively.** A ViewModel can inherit every `System.Windows` call it makes
   and this rule will not see it. (Which is also a limit on the rule: the ratchet only bites on the type that
   physically names the dependency.)
3. **The batch therefore deleted THREE names, not the four the §4 cost model implied** — `VoiceModeViewModel`
   in `2fc593a`, `TranscriptOverlayViewModel` + `MeetingAttendeeViewModel` in `d6dd73f`. The exemption list
   went 4 → **1**.

### And a fourth correction, found only in the fix pass

The replacement comment named `BitmapSource` as the single surviving root — **and repeated the sin of the
comment it replaced.** A Cecil dump of `AssistantViewModel`'s complete `System.Windows` dependency set returns
**two** entries:

```
System.Windows.Input.ICommand | System.Windows.Media.Imaging.BitmapSource
```

`BitmapSource` enters through member signatures only (no IL site); `ICommand` enters through two
`callvirt System.Void System.Windows.Input.ICommand::Execute(System.Object)` sites —
`OnMeetingAttendeeSummarizeRequested` (`SendMessageCommand.Execute(null)`) and `CancelPendingActionCards`
(`card.CancelCommand.Execute(null)`). So doing the refactor the comment prescribed (move the
clipboard→attachment conversion out of the VM) and then deleting the exemption on that strength would turn the
rule **red**, naming `AssistantViewModel`. Both roots are now named at `DependencyInjectionTests.cs`, with the
note that `Execute` is *declared on* `ICommand`, so casting to the toolkit's `IRelayCommand` changes nothing —
closing that half means calling the commands' own methods.

### The optional narrower `[Fact]` was taken, and proven to be an instrument

`AssistantViewModel_MustNotReference_DispatcherOrApplication` asserts the one surviving exemption cannot be
used as cover for reintroducing `App.Current.Dispatcher`. The spec said to decide it after Step 0 and verify
it *by running*, not by reading. Both halves of that were done: the pre-migration result was measured by
stashing the four changed files, rebuilding and re-probing (**False**, naming `AssistantViewModel`), so the
flip False → True across the migration is observed on both sides rather than asserted. And it is a real
detector, not a tautology: the same two prefixes flag `Pia.Services.OutputService` and
`Pia.Services.UiDispatcherService`, which genuinely do read `Application.Current.Dispatcher`.

---

## 2. As built

### The abstraction

```csharp
// src/Pia.Wpf/Services/Interfaces/IUiDispatcher.cs — namespace Pia.Services.Interfaces
void Post(Action action);          // queued when there is a live Application; never propagates
Task PostAsync(Action action);     // awaited, so the caller observes it applied — and its failure
void PostOrRun(Action action);     // inline when already on the UI thread (CheckAccess), else queued
```

Three members, mirroring `UiThreadViewModel`'s so the two idioms read identically. **No `IsOnUiThread`
probe** — nothing needed the boolean and exposing it invites a check-then-act race. Shipped exactly as spec'd.

`UiDispatcherService` (`Pia.Services`, `public sealed`, `ILogger<UiDispatcherService>`) re-reads
`Application.Current?.Dispatcher` **per call** — not cached, because DI resolution order versus `Application`
construction is not something a ViewModel should depend on — and a **null dispatcher runs the action inline**,
which is precisely the fallback the pre-batch code took under the test host. The file names no
`using System.Windows.Threading`. Registered `AddSingleton<IUiDispatcher, UiDispatcherService>()` inside
`ConfigureServices`, three lines after `AddSingleton<ILocalizationService, LocalizationService>()`.

**Error handling is deliberately asymmetric, and the fix pass had to correct it once (see §3):**
`PostAsync` has **no** try/catch — it returns `dispatcher.InvokeAsync(action).Task` so the fault propagates to
the awaiter, which is what the callers' own `try/catch` and `SafeFireAndForget` rely on. `Post` and
`PostOrRun` never propagate: their try/catch covers the **marshal call itself** and the inline fallback, and a
**queued** action's failure is picked up separately by `LogIfFaulted`, a continuation on the operation's
`Task` scheduled on `TaskScheduler.Default`.

### The migrated sites, and which member each became

- **`VoiceModeViewModel`** (6-param ctor → **7**): `using System.Windows.Threading`, the `Dispatcher`
  field and `Dispatcher.CurrentDispatcher` are gone; the file now has **zero** `System.Windows` tokens. Both
  sites are `Post`, never `PostOrRun` — `PostOrRun` would run the silence-timer lambda (which starts
  `TransitionToProcessingAsync`) **synchronously inside `Timer.Elapsed`**, reordering the state transition.
  Migrating it also removed a latent bug the spec called out: `Dispatcher.CurrentDispatcher` *creates* a
  dispatcher for the constructing thread, so off-UI-thread construction would have queued to a dispatcher
  nobody pumps — silently.
- **`AssistantViewModel`** (29 → **30**, `IUiDispatcher` appended last): `:374`/`:379` are `Post`
  (expression-bodied non-async void; the pre-batch code already discarded the operation, so this is
  byte-equivalent and the Batch-10 G3 off-thread `RunChanged` marshal is intact). `:320`, `:501`, `:1472` are
  **awaited** `PostAsync`, which is what keeps `:501`'s `_isLoadingPersonas` guard closed before its `finally`.
  `:1010` — the pre-batch **blocking `.Invoke`** — is also an awaited `PostAsync`: a bare `Post` would have
  moved the exception out of the `catch` that logs *"Failed to initialize TTS on navigation"*, which is that
  discarded `Task.Run`'s only error sink. `using System.Windows.Media.Imaging;` stays, by design.
- **`TranscriptOverlayViewModel.DispatchToUi`** is now a single `_uiDispatcher.PostOrRun(action)`. The method
  **keeps** its name, `protected` visibility, `void DispatchToUi(Action)` shape and its try/catch-and-log,
  because the signature is bound as an `Action<Action>` method group when `MeetingAttendeeViewModel` feeds
  `SpeakerModelDownloadUi`. Its protected ctor went 4 → **5** and the field is declared on the **base** — not
  optional: `MeetingAttendeeViewModel`'s ctor can reach `DispatchToUi` while wiring `_service.StateChanged`,
  and base ctors run first. **All nine callers and the `Action<Action>` seam changed zero lines**, confirmed by
  diff, not by claim.
- **`MeetingAttendeeViewModel`** (6 → **7**) forwards through its single `base(...)` call; it is
  `TranscriptOverlayViewModel`'s only subclass, so exactly one call updated.

`InlineUiDispatcher` (test project, `internal sealed`, `Pia.Tests.Services`) invokes all three members
synchronously inline and **catches nothing**. That restores today's behaviour rather than changing it: `new
Application` / `new App(` appears **nowhere** in the pre-batch test project, so `Application.Current` was
unconditionally null and `DispatchToUi` was unconditionally inline. No assertion in
`MeetingAttendeeViewModelTests` could have depended on the marshal being asynchronous — which is a negative
result with a mechanism, and it is why the double is behaviour-preserving *deterministically* instead of by
accident of a static being null.

### The acceptance test

`AssistantViewParseTests` (`[Collection("WpfApplicationStatic")]`, `DisableParallelization`) reproduces the
withdrawn design rather than reinventing it: **one** lazily created, never-shut-down background STA thread
with a **running** dispatcher (`WpfStaHost`), the process's only `System.Windows.Application` built with
`InitializeComponent()` only, the **real** `AssistantViewModel` (30 substituted/inline args) as DataContext,
and a `Pump()` that drains to `SystemIdle` before every bound read. Two facts: the composer hint is located
**by its rendered EN text** (which covers three failure modes at once — the view parses, the `loc:Str` key
resolves, the binding path is right) and its `Visibility` must go `Collapsed` → `Visible` across a
`ForeignRunActive` flip; plus a sweep asserting no `TextBlock` in the parsed logical tree renders a
`^\[\w+\]$` literal.

Beyond the design, **every wait in the host is bounded** (60 s startup hand-off, 60 s marshalled body,
startup exception captured and rethrown with the stage named), because a broken host must fail with a message
rather than hang the suite — xunit v3 applies no default per-test timeout. `Pump()` is deliberately *not*
bounded by a timer that releases the frame early: a partially drained queue is exactly the silent
vacuous-pass mode `Pump()` exists to prevent, so an undrainable frame surfaces as the marshalled-body timeout
instead. `Run<T>` waits on `((IAsyncResult)operation.Task).AsyncWaitHandle.WaitOne(timeout)` and then
`GetAwaiter().GetResult()` — `DispatcherOperation.Wait(TimeSpan)` leaves a body's exception unobserved (you
get a default-valued result and a baffling assertion failure) and `Task.Wait(TimeSpan)` would wrap it in an
`AggregateException`.

---

## 3. What the review pass changed, and why (`39025cc`, `cac8251`)

Four real defects, three of them invisible to the build and to every rule this repo has.

- **A queued action's exception was silently lost.** `Post`/`PostOrRun` replaced `BeginInvoke(action)` with
  `InvokeAsync(action)` and **discarded** the operation. `Dispatcher.UnhandledException` — where
  `App.xaml.cs`'s error MessageBox lives — is raised for `Invoke`/`BeginInvoke`, not for `InvokeAsync`, which
  captures the failure on `DispatcherOperation.Task` instead. This is **self-proving** from something the
  batch already relies on: `PostAsync` needs `InvokeAsync` to fault the operation's `Task`, and it cannot both
  capture and escape. Concretely: the MeetingAttendee reader thread → `AddUtterance` → `DispatchToUi` →
  queued `Bubbles` mutation → a `CollectionChanged`/converter throw would have shown a dialog before this
  batch and nothing at all after it. Fixed with `LogIfFaulted` (continuation on `TaskScheduler.Default`,
  because a shutting-down dispatcher is exactly when it fires), and the comment that asserted the opposite
  was replaced. **The proposed alternative — revert to `BeginInvoke` — was rejected**: `AssistantViewModel`'s
  `:374`/`:379` were *already* `InvokeAsync` before this batch, so reverting would have promoted a
  background-run progress-sync failure from silent to a production MessageBox. That is a behaviour change
  wearing the costume of a revert.
- **`WpfStaHost` could abandon a live `Application`.** `System.Windows.Application`'s ctor publishes
  `Application.Current` *before* `InitializeComponent()` can throw, and the catch's `return` skipped
  `Dispatcher.Run()`; `Lazy(ExecutionAndPublication)` then caches the failure. Net state: a process-wide
  `Application` whose `Dispatcher` belongs to a **dead** thread. `AssistantViewParseTests` would fail cleanly,
  but `WindowManagerServiceTests.ShowAgentRun_MissingRun_RetractsStaleItem_AndDoesNotThrow` awaits a real
  `DispatcherOperation` and `OutputService` does a blocking `Invoke` — both **hang forever**, which is the
  exact outcome this file was written to prevent, triggered by the batch's own #1 named unknown (App.xaml
  resolving under the xunit host). Now the catch records the cause and **falls through** to `Dispatcher.Run()`,
  which is re-entered if a queued exception escapes it (reachable, because App's own
  `DispatcherUnhandledException` net is installed inside `OnStartup`, which this host never calls), stopping
  only on `HasShutdownStarted`.
- **The loc-key sweep could pass vacuously.** `Assert.True(unresolved.Count == 0)` is equally true over an
  empty walk, so if `LogicalTreeHelper` ever stops descending, the sweep reports a clean pass over nothing —
  and the same batch had *already* added `Assert.Single(target)` to the new arch fact for exactly this reason.
  It now asserts `Assert.Contains(HintText, rendered)` first. **A numeric floor (`>= 4`, `> 20`) was
  rejected**: the real TextBlock count has never been measured on Windows, and a wrong constant is a false red
  on the owner's first run. Anchoring on a string the sibling fact already requires adds no new failure mode.
- **`UiDispatcherService` had zero coverage** — `grep UiDispatcherService -- tests/` returned nothing, and
  because `InlineUiDispatcher`'s three members are the same one-liner, **no test in the suite could
  distinguish `Post` from `PostOrRun`**. Every semantic the design spent paragraphs defending was pinned by
  comments alone. `aca30bd` had, as a side effect, built the instrument that makes this cheap, so
  `UiDispatcherServiceTests` now has 5 facts, all executed **on** the STA thread inside `WpfStaHost.Run` (no
  unbounded cross-thread await): `PostOrRun` inline before returning; `Post` queues and runs on the next
  `Pump()`; `PostAsync` queues and completes with the mutation applied; `PostAsync` faults with the action's
  own exception; `Post`'s throwing action reaches neither the caller nor the frame. The two throwing facts
  install a temporary `Dispatcher.UnhandledException` net so a wrong assumption is a red test rather than a
  dead test **process**.

Two consequences of the batch that are behaviour changes and are recorded rather than fixed:

- **`AssistantViewModel:1010` also changed dispatcher priority, `Send` → `Normal`.** `Dispatcher.Invoke(Action)`
  defaults to `Send` (jumps the queue); `InvokeAsync(Action)` defaults to `Normal`. So on a busy UI thread
  during navigation, `EnterVoiceModeCommand.NotifyCanExecuteChanged()` now runs behind already-queued Normal
  work. Nothing reads the result and it is the last statement in the body, so the impact is cosmetic — it is
  recorded because the batch's claim is behaviour preservation. If `Send` ordering ever matters, add a
  priority overload to `IUiDispatcher`; do not reintroduce `Dispatcher`.
- **The release log string for a failed UI dispatch moved.** With a live `Application` on the UI thread,
  `PostOrRun` catches an inline action's exception first, so `DispatchToUi`'s own catch becomes an outer net
  that no longer fires: a support bundle shows **"UI dispatch failed (PostOrRun)"** under
  `Pia.Services.UiDispatcherService`, not the historical `"Dispatcher invoke failed"` under the ViewModel's
  category. The method's comment says so. Under `InlineUiDispatcher` (which catches nothing) the ViewModel's
  catch is still the one that fires, with the original text — so a test asserting the old string still passes,
  which is the trap.

---

## 4. Deviations from the spec worth knowing

- **Nearly every anchor the spec quoted had moved by a few lines**, and each seam was located by content.
  Load-bearing ones: `ILocalizationService` really was at `Bootstrapper.cs:521` (the spec's `:517` was stale),
  so the registration landed at **`:525`**, and `AddScoped<AssistantViewModel>/<MeetingAttendeeViewModel>`
  shifted to `:594`/`:595`. Comments written during the batch cite **content, not line numbers**, precisely
  because this batch shifted three ViewModels and Unit 2 caught itself about to commit stale digits.
- **The spec's "42 of the 48 tests in `MeetingAttendeeViewModelTests` depend on the current inline fallback"
  reproduces nothing** and was not used. Counted: the file has 42 `[Fact]` + 6 `[Theory]` = **48 methods**,
  and 42 + 25 `[InlineData]` = **67 cases** — so the "42" is the `[Fact]` count wearing a dependency claim's
  clothes. The measured load-bearing subset is **31 methods (~46 of 67 cases)** whose assertions read state a
  `DispatchToUi` action mutated. That figure is now in the file's own header comment, over a denominator that
  was independently recounted.
- **New files are 7, not the 4 the cost table predicted.** `WpfStaHost.cs` and `WpfApplicationCollection.cs`
  are the acceptance test's plumbing (mandated by §5's design, written before §4's table existed), and
  `UiDispatcherServiceTests.cs` came from the review pass. `UiThreadViewModel.cs` is likewise an edited file
  the table did not predict — it gained a pointer to `IUiDispatcher` (below).
- **Ctor arities were exactly as predicted** — 7 / 30 / 5 / 7, verified by a top-level-comma count rather
  than by eye — and the spec's "**zero** broken NSubstitute fakes / hand-written stubs" prediction **held**:
  no `Substitute.For<>` targets any migrated ViewModel, and `MeetingAttendeeViewModel` is the only subclass in
  the tree. Adding a *new parameter* really does cost nothing beyond the construction sites, unlike Batch 11's
  widened interface.
- **Test construction sites: the 4 predicted were updated**, plus 2 in the new acceptance test = 6
  `new InlineUiDispatcher()`.
- **The spec's "free extra" over-promised its own coverage.** A `TextBlock`-only logical walk is *not* "a
  missing-`loc:Str`-key detector for the whole view": `AssistantView.xaml` has **22** `loc:Str` usages, of
  which `Text=` is **4**. The other 18 are `ToolTip=` (11), `Content=` (5, on `ui:Button` — a string that
  becomes a `TextBlock` only after template application, which this test deliberately never triggers),
  `PlaceholderText=` (1) and `Value=` (1), all structurally invisible without layout. The test's doc now says
  `TextBlock.Text` only. It is still the only instrument in the repo that catches the class of regression at
  all.
- **One doc pointer the spec did not ask for.** The batch left two sanctioned marshaling idioms with identical
  member names — 14 ViewModels on `UiThreadViewModel`'s captured `SynchronizationContext`, 4 on injected
  `IUiDispatcher` — and only `IUiDispatcher` knew about the other. Worse, `UiThreadViewModel`'s summary said
  the architecture test "rules out `Dispatcher`", which this batch made false. It now carries a
  choose-between rule: prefer `IUiDispatcher` when the ViewModel may be constructed off the UI thread or when
  its marshal target must be substitutable; prefer the base when the VM is always built on the UI thread and
  wants no extra ctor parameter. (A third idiom persists and was left alone: `RunProgressViewModel`'s
  `SynchronizationContext.Current ?? new SynchronizationContext()`, whose fallback posts to the **thread
  pool** rather than running inline.)
- **`WpfApplicationCollection`'s own doc was corrected.** "Every collection scheduled after this one" assumed
  xunit schedules the serial group late. It does not promise that: `DisableParallelization` decides that the
  collection does not *overlap* the parallel group, not which runs **first**. If it runs first,
  `Application.Current` is live for the entire remainder of the suite — i.e. the "deterministic total
  exposure" that §5's decision to reject `[assembly: AssemblyFixture]` was meant to avoid. Triage the first
  Windows run as *total* exposure, not as a concurrency race.

---

## 5. Decisions that held

Every one of these survived the build unchanged, and the reasons are the same ones the spec gave: injection
rather than "make the four VMs derive from `UiThreadViewModel`" (that keeps behaviour tied to ambient state at
construction time — seven test files already set or null the ambient context by hand to get determinism);
`UiThreadViewModel` and its 14 subclasses untouched; `UiDispatcherService`, not `UiDispatcher` plus a new
entry in `NamingConventionTests`' allow-list (measured: the rule *does* scan the new type and 'Service' *is*
allow-listed, so no rule needed editing — and no `Pia.Services` class ends in the non-allowed suffix
'Dispatcher'); `:1010` awaited rather than fire-and-forget; category (d) deliberately out of scope; no
`IsOnUiThread` probe; and the exemption list's rationale used as a *ratchet*, never as an argument.

`LayerDependencyTests` was checked and has **no opinion on `System.Windows` at all** — the only `System.Windows`
mention in the whole `Architecture/` folder is `DependencyInjectionTests`' ViewModel rule. `UiDispatcherService`
joins **13** pre-existing `Pia.Services` types that already depend on `System.Windows`; all six layer facts are
green.

---

## 6. The inventory, as it stands now

The pre-batch sweep of `src/**/*.cs` found `App.Current`/`Application.Current` on **42 lines / 16 files** and
`Dispatcher` on **69 lines / 35 files**. `src/Pia.Shared` has **zero** hits (net10.0, no WPF reference).
Post-batch the line count is still 42 — the 7 ViewModel sites are gone and are replaced by 3 real reads plus
comments inside the new service — and the categories are:

- **(a) ViewModel marshaling — was 8 expressions in 3 files. Now ZERO. This was the batch.**
- **(b) A View / code-behind / behavior touching its own dispatcher — 38 lines in 21 files. Do not touch.**
  A `UserControl` posting to its own `Dispatcher`, or holding a `DispatcherTimer`, is correct WPF and is not
  DI-resolved.
- **(c) Genuine `Application`-level use — 27 lines in 13 files. Must NOT be abstracted.** Theme-aware resource
  lookup in ~13 converters/renderers, merged theme dictionaries (`ThemeService`), process shutdown
  (`TrayIconService`), window enumeration / `MainWindow` (`WindowManagerService`, two notification surfaces),
  and the global exception hook (`App.xaml.cs`). `Application.Current` **is** the resource/window/shutdown
  root; abstracting it is a larger batch with no test to buy.
- **(d) Service-layer marshaling — 11 sites in 7 files. Eligible, NOT required, still open.**
  `AgentRunNotificationSurface` · `BackgroundChatNotificationSurface` · `ScheduledJobNotificationSurface` ·
  `OutputService` · `ThemeService` · `TrayIconService` · `WindowManagerService`. Two are load-bearing for the
  acceptance test and are **why a running dispatcher is a correctness requirement**: `OutputService`'s
  `Application.Current.Dispatcher.Invoke(...)` has **no null guard** at all, and
  `WindowManagerServiceTests`' one fact stops taking its null branch and awaits a real `DispatcherOperation`.
  On a non-pumping dispatcher both **hang** rather than fail.
- **(e) Already abstracted — 14 ViewModels on `UiThreadViewModel`** (+ explicit captures in
  `RunProgressViewModel`, `ChatSessionManager`, `LiveTurnExecutor`, `PluginIconLoaderService`).

A stronger fact than the batch aimed for, measured: **every** type under `Pia.ViewModels`, *including* the
rule-excluded `Pia.ViewModels.Models` namespace, is now clean of both `System.Windows.Threading` and
`System.Windows.Application`. The one type the probe flags under `.Models` is `ChatSession`, and a Cecil IL
dump shows the single reference is `ICommand::Execute` — pre-existing MVVM, not a dispatcher.

---

## 7. Acceptance — met on everything that can be measured here

**Primary (independent of NetArchTest semantics):**
`git grep -n "App\.Current\|Application\.Current" -- src/Pia.Wpf/ViewModels/` → **no output, exit 1.** The
ViewModel layer no longer reads the process-global dispatcher anywhere.
`git grep -n "System\.Windows" -- src/Pia.Wpf/ViewModels/` → **exactly 4 benign lines** (down from 6):
`AssistantViewModel.cs:7`'s `using System.Windows.Media.Imaging;` plus three comments in `FlowViewModel`,
`TodoViewModel` and `UiThreadViewModel`.

**Secondary (arch probe at `cac8251`, verbatim):**

```
[1] blanket AS COMMITTED                             -> True   failing=[]
[2] blanket ZERO exemptions                          -> False  failing=[Pia.ViewModels.AssistantViewModel]
[3] AssistantVM !Threading/!Application              -> True   failing=[]
[4a] CONTROL AssistantVM !Media.Imaging              -> False  failing=[Pia.ViewModels.AssistantViewModel]
[4b] CONTROL AssistantVM !Input                      -> False  failing=[Pia.ViewModels.AssistantViewModel]
[5] NON-VACUITY AssistantVM !System.Object           -> False  (so the HaveName selection really resolves)
[6] VoiceMode / TranscriptOverlay / MeetingAttendee  -> True × 9  (each × System.Windows/.Threading/.Application)
[7] INSTRUMENT OutputService, UiDispatcherService    -> False  (the [3] prefixes detect a real reader)
[8] AssistantViewModel COMPLETE System.Windows set: System.Windows.Input.ICommand | System.Windows.Media.Imaging.BitmapSource
```

`[2]` licenses all three deletions (nothing but `AssistantViewModel` is left to flag); `[4a]`/`[4b]` keep the
surviving exemption honest; `[6]` probes each deleted name **positively**, because "absent from a failing set"
is also what a misspelled selection produces.

**Build:** 0 errors, exactly 194 warnings.

---

## 8. NOT verified — read this before trusting anything behavioural

**No test in this batch has ever been executed. Not one.** `dotnet test` cannot run on the authoring machine
and was deliberately not attempted after that was confirmed. Specifically unexercised:

- The **entire runtime behaviour** of `WpfStaHost`: the STA thread start, `new Pia.App()` +
  `InitializeComponent()`, `Application.LoadComponent` resolving App.xaml's nested relative dictionaries under
  the xunit v3 host, the `Dispatcher.Run()` loop and the new re-entry path, and `Pump()`'s frame drain.
- Both `AssistantViewParseTests` facts, all 5 `UiDispatcherServiceTests` facts, and whether every
  `StaticResource` and `loc:Str` key in the eagerly realized regions actually resolves — this view's own 22
  `loc:Str` usages plus those of the child controls it instantiates. If one does not, test 1 fails with "the
  view failed to parse" and test 2 names the key — intended behaviour, but it may fail on the **first** run
  for a pre-existing defect rather than a regression.
- The **31 load-bearing `MeetingAttendeeViewModelTests` methods (~46 of 67 cases)**. They are green *by
  construction*: the pre-batch null-`Application` path invoked inline and `InlineUiDispatcher` invokes inline
  synchronously. If they go red the **double** is wrong, not the tests.
- All **21 `AssistantViewModelLeverTests`** facts — a compile-level dependency only (one added ctor argument),
  and the compile is proven, but no assertion ran. Note the batch also *arms* six previously-dead
  `AssistantViewModel` paths (they would have NRE'd on a null `App.Current`); inspection says no test reaches
  any of them (`EnsureSubfolder` is never stubbed, and the dispose tests raise their events after `Dispose()`),
  so `InlineUiDispatcher` can only arm paths the suite never entered — it cannot flip an existing assertion.
- `DiRegistrationTests.AllServiceInterfaces_MustHaveRegisteredImplementation` **and**
  `BootstrapperGraphValidationTests.ProductionServiceGraph_ResolvesAndRespectsScopes` — both execute
  `Bootstrapper`, so neither is measurable here. By inspection both are satisfied: `IUiDispatcher` is in
  `Pia.Services.Interfaces`, is not in the first test's `factoryCreated` allow-list, and is registered as a
  `ServiceType`; the singleton depends only on `ILogger<>`, and an `AddScoped` ViewModel taking a singleton is
  scope-legal.
- The **process-wide-`Application` blast radius**: ~13 category-(c) converters start returning real brushes
  instead of null, and category-(d) dispatcher reads stop taking their null branch. Inspection narrows this a
  long way — no test constructs any of those converters, `OutputService`, `ThemeService` or `TrayIconService`;
  all 15 `AgentRunNotificationSurfaceTests` enter through internal seams that bypass the dispatcher reads; and
  App.xaml's only top-level implicit style targets `controls:OverlayDialogPanel`, with MarkdownStyles' implicit
  styles nested inside a keyed `Style.Resources`, which largely closes the feared cross-thread-style collision.
  But **only Windows can settle it.**

---

## 9. The Windows smoke list, ordered by risk

> **✅ RUN 2026-07-30, and every item below came back green. Read this before the list, which is written in the
> future tense of a run that has now happened.** This batch was merged with Batch 05 (merge commit `d2e56e6`)
> and the suite was executed on Windows on the merged tree: **2232 total / 0 failed / 1 skipped**, in 24 s —
> nothing hung. Item by item, checked individually as well as in the full run: **1.**
> `WindowManagerServiceTests` 1/1, green rather than hung, so `WpfStaHost`'s `Dispatcher.Run()` does pump;
> **2.** `AssistantViewParseTests` 2/2, so the first-ever `new Pia.App()`, XAML parse and `Pump()` all work and
> the batch's headline claim is proven, not merely written; **3.** `UiDispatcherServiceTests` 5/5, so the
> `DispatcherOperation.Task` assumption holds; **4.** `MeetingAttendeeViewModelTests` 67/67, so
> `InlineUiDispatcher` is a correct double; **5.** `AssistantViewModelLeverTests` all green, so the `CreateSut`
> edit was fully applied; **6.** the two DI gates run and pass — `DependencyInjectionTests` reports 6 cases,
> which is **+1** on its pre-merge 5; **7.** `EmojiInlineBuilderTests` green under a live `Application`, which
> **narrows but does not close** it — xunit chooses the collection ordering, so one green observation is not a
> proof over orderings.
>
> **One number in this batch's own accounting was wrong:** §9 and the roadmap both described "7 facts" added
> here. The real delta is **+8** — 2 (`AssistantViewParseTests`) + 5 (`UiDispatcherServiceTests`) + **1**, the
> narrower dispatcher-ban `[Fact]` in `DependencyInjectionTests`, which the count missed by looking only at the
> two new files. 2224 + 8 = 2232, so the arithmetic closes exactly.
>
> The only failure seen at all was `TaskExtensionsTests.SafeFireAndForget_SlowTask_DoesNotBlock`, the
> pre-existing wall-clock flake in a file this batch never touched — it fired on two of three full runs and the
> third was clean; 4/4 green in isolation. See [`00-OVERVIEW.md`](00-OVERVIEW.md) for both corrections.

1. **`WindowManagerServiceTests.ShowAgentRun_MissingRun_RetractsStaleItem_AndDoesNotThrow`** — the one test in
   the suite that builds a real `Application`-dependent service. **Its failure signature is a HANG, not a red
   test.** If the run wedges, `WpfStaHost`'s `Dispatcher.Run()` is the first suspect: it passes if the host
   pumps, blocks forever if it does not. (This is the hazard `cac8251` exists to bound.)
2. **`AssistantViewParseTests.ComposerHint_Parses_AndTracksForeignRunActive`** and
   **`.ParsedView_HasNoUnresolvedLocalizationKeys`** — first-ever execution of the host, `new Pia.App()`, the
   XAML parse and `Pump()`. A parse failure names the resource or key; a wrong `Visibility` means the batch's
   headline claim is unproven, not that the hint is broken.
3. **`UiDispatcherServiceTests`** (5 facts) — first-ever execution against a live `Application`. A red
   `PostAsync_WhenTheActionThrows_FaultsTheReturnedTask` or
   `Post_WhenTheActionThrows_RunsItAndDoesNotReachTheCaller` means an assumption about
   `DispatcherOperation.Task` is wrong, which would also invalidate `LogIfFaulted`.
4. **The 31 load-bearing `MeetingAttendeeViewModelTests`** — failure signature is status/bubble assertions
   reading the **pre-mutation** value (e.g. `StatusText` still showing the ctor's `_Idle` seed, `Bubbles`
   empty). That means the double is wrong.
5. **All 21 `AssistantViewModelLeverTests`** — all-21-red, or a compile error naming argument count, means the
   `CreateSut` edit was half-applied.
6. **`DiRegistrationTests` + `BootstrapperGraphValidationTests`** — the two DI gates that cannot run on macOS.
7. **`EmojiInlineBuilderTests`** — the genuine unknown. `DisableParallelization` removes the *concurrent*
   case, not the *live-`Application`* case; the historical signature is *"Initialization of
   `Wpf.Ui.Controls.Button` threw an exception"*. `Wpf.Ui` 4.2.0's `ControlsDictionary` is the one part not
   read.
8. **Known flake, do not chase:** `TaskExtensionsTests.SafeFireAndForget_SlowTask_DoesNotBlock`.

Command: `dotnet test tests/Pia.Wpf.Tests/Pia.Wpf.Tests.csproj -- --filter-not-namespace
"Pia.Wpf.Tests.Integration.Providers"` (never pass `--nologo`). **Do not expect a specific total** — this
batch adds **7** xunit facts (5 + 2) on top of whatever the previous run reported; the delta is what matters,
and `failed: 0` is the bar.

**Manual smoke, because a unit suite cannot cover it:** enter and leave voice mode (both `Post` sites, one on
the audio thread); run a MeetingAttendee session with live transcription (the reader-thread `DispatchToUi`
path, now `PostOrRun`); start a background/headless run and watch the composer hint appear and the progress
panel sync (the two `Post` sites on the off-thread `RunChanged` marshal, i.e. Batch 10's G3); navigate with
TTS configured (the `:1010` site that changed from a blocking `Invoke` to an awaited `PostAsync` *and* from
`Send` to `Normal` priority); paste an image into the composer (the `BitmapSource` path the surviving
exemption protects, untouched but adjacent).

---

## Still open after this batch

- **Every other View is still unparsed.** `AssistantView` is the **first**, not the last, and the silent
  misspelled-binding hazard remains everywhere else. What the abstraction bought is that a second view test is
  now a ~20-line file, not a batch.
- **The loc-key sweep sees `TextBlock.Text` only** — 4 of this view's 22 `loc:Str` usages. `ToolTip`,
  `Content`, `PlaceholderText` and `Value` need template application, which the test deliberately does not do.
- **Category (d)'s 11 service sites still read `Application.Current`**, and `OutputService`'s is **still
  unguarded** — which this batch made *more* dangerous, not less: a live `Application` now exists in the test
  process, so that unguarded blocking `Invoke` is a hang rather than an NRE if any test ever reaches it. These
  seven files can adopt `IUiDispatcher` mechanically now that it exists.
- **`AssistantViewModel`'s exemption needs TWO refactors, not one** (measured): moving the
  clipboard→attachment conversion out of the VM (call site `AssistantView.xaml.cs`) **and** replacing the two
  `ICommand.Execute(null)` calls with the commands' own methods. Doing only the first leaves the rule red.
- **Abstracting category (c)** (resources / windows / shutdown) is a separate, larger batch with no test to
  buy.
- **The shared STA thread is a process-wide singleton that can never be torn down**, so a future test needing
  a *different* `Application` configuration cannot have one — and whether xunit schedules the serial group
  first or last is unmeasured, so worst-case exposure is the whole suite.
- **The ViewModel-level `Post`-vs-`PostOrRun` choice is still unpinned.** `UiDispatcherServiceTests` now pins
  the *service's* three semantics, but no ViewModel test would notice if `VoiceModeViewModel`'s silence-timer
  site were changed from `Post` to `PostOrRun` — `InlineUiDispatcher` collapses them by design.
- **NetArchTest 1.3.2 does not resolve base-type dependencies transitively**, so the ViewModel rule is a
  ratchet on the type that *physically names* the dependency. A future ViewModel can inherit a `System.Windows`
  dependency and stay green.
