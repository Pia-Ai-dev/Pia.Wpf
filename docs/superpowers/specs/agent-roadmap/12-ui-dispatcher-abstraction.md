# Batch 12 — The UI-dispatcher abstraction (`IUiDispatcher`) — 📋 SPEC, NOT BUILT

**Phase 2 · Size M · own batch · written against `feature/agent-run-spine` at `6852e2f`**

Approved as its own batch, so nothing here is applied yet. Every line reference below was read at `6852e2f`
and re-verified once adversarially; where a claim is an inference rather than an inspection it says so, and
§3's **Step 0** exists to settle the one inference that decides the acceptance criterion. Re-anchor by content
if the tree has moved.

> **Baseline this batch must not regress:** `dotnet build -p:EnableWindowsTargeting=true --no-incremental`
> → 0 errors, **exactly 194 warnings**. `dotnet test tests/Pia.Wpf.Tests/Pia.Wpf.Tests.csproj --
> --filter-not-namespace "Pia.Wpf.Tests.Integration.Providers"` → **2157 total, 0 failed, 2156 passed,
> 1 skipped**, on Windows 11. net10.0-windows tests **do** execute on this machine; any older note in these
> specs claiming otherwise is stale.

## Why this batch exists

No test in this suite parses a `View`. So in `AssistantView.xaml` an unresolvable `StaticResource`, a missing
`loc:Str` key, or a **misspelled `Binding` path** is invisible to a green build *and* a green suite: markup
compilation catches malformed XAML and unknown types/properties, but resource-key resolution and binding paths
are runtime concerns, and a wrong binding path fails **silently**. That was confirmed, not assumed — a
deliberately misspelled path on the composer hint produced the always-visible failure with the build still at
**0 errors**.

A view-parse test was built and **withdrawn**. Parsing a View needs an `Application` whose `Resources` carry
App.xaml's converters (`App.xaml:34` declares `BooleanToVisibilityConverter`, which the hint's `Visibility`
binding at `AssistantView.xaml:498` resolves as a `StaticResource`), and `Application.Current` is
**process-wide**. Creating it makes `App.Current.Dispatcher` real — owned by the test's STA thread — so every
ViewModel that marshals through it stops running its work inline (today it takes the null-`App.Current`
synchronous fallback). Measured cost: **42 `MeetingAttendeeViewModelTests` failures** (that file has 48 test
methods).

So the blocker is not the view test. The blocker is that the ViewModel layer's threading behaviour depends on
a **process-global static**. This batch removes that dependency; the view test is what proves it.

## What ships

| # | What | Files |
|---|---|---|
| 0 | **Step 0 measurement** (§3) — no code, decides #6 and #8's criterion | 0 |
| 1 | `IUiDispatcher` (`Post` / `PostAsync` / `PostOrRun`) in `Pia.Services.Interfaces` | +1 |
| 2 | `UiDispatcherService` in `Pia.Services` — the only new place `Application.Current.Dispatcher` is read | +1 |
| 3 | `Bootstrapper` registers it `AddSingleton` (mandatory — see the DI rule below) | 1 |
| 4 | Unit 1: `VoiceModeViewModel`, then `AssistantViewModel` migrated | 2 |
| 5 | Unit 2: `TranscriptOverlayViewModel.DispatchToUi`, then `MeetingAttendeeViewModel` migrated | 2 |
| 6 | Exemption names deleted from `DependencyInjectionTests.cs:24-27` — **how many, Step 0 decides** | 1 |
| 7 | `InlineUiDispatcher` test double (runs inline) + the 4 test construction sites | 3 |
| 8 | `AssistantViewParseTests` — the acceptance test, + the stale comment at `MeetingAttendeeViewModelTests.cs:18-19` | +1, 1 |

## 1. The inventory

Exhaustive sweep of `src/` (`*.cs`) for `App.Current`, `Application.Current` and `Dispatcher`.
`App.Current`/`Application.Current`: **42 lines / 16 files**. `Dispatcher`: **69 lines / 35 files**.
**`src/Pia.Shared` has zero hits** (net10.0, no WPF reference) — it was checked. Category totals below were
recounted line by line.

### (a) ViewModel marshaling a callback onto the UI thread — **8 expressions in 3 files. This is the batch.**

- `AssistantViewModel.cs` — **6**: `:317` (`await App.Current.Dispatcher.InvokeAsync`, default working dir),
  `:371` (`OnActiveRunChanged`), `:376` (`OnForeignRunActiveChanged`), `:498` (personas, awaited so
  `_isLoadingPersonas` still guards), `:1007` (**blocking `.Invoke`** inside a `Task.Run(async …)` whose
  `try/catch` logs, TTS init), `:1468` (follow-up suggestions).
- `TranscriptOverlayViewModel.cs:416` — **1**, the `DispatchToUi` seam (method at `:414`):
  `System.Windows.Application.Current?.Dispatcher`, `CheckAccess()` → inline, else `BeginInvoke`, whole thing
  in a try/catch that logs (`:417-425`). **9 callers** route through it: same file `:163`, `:253`, `:318`;
  `MeetingAttendeeViewModel.cs:135`, `:165`, `:175`, `:197` (passed on as an `Action<Action>` to
  `SpeakerModelDownloadUi`), `:224`, `:361`. It is that file's **only** `System.Windows` token — fully
  qualified, no `using` — so after migration the file greps clean.
- `VoiceModeViewModel.cs:59` — **1**, `Dispatcher.CurrentDispatcher` into the `_dispatcher` field (`:21`);
  used at `:124` and `:164`. Its `System.Windows` tokens are exactly `:4` (`using System.Windows.Threading;`),
  `:21`, `:59`, `:124`, `:164` — so it too goes fully clean.

Nothing else in `Pia.ViewModels` qualifies. `TodoViewModel.cs:192` and
`ViewModels/Models/SpeakerModelDownloadUi.cs:50` are **comments** mentioning a dispatcher, no call.

**`MeetingAttendeeViewModel` has no `System.Windows` token of its own** — no `using`, no fully-qualified use.
Everything it does goes through the inherited `DispatchToUi`.

### (b) A View / code-behind / behavior touching its own dispatcher — **38 lines in 21 files. Do not touch.**

A `UserControl` posting to its own `Dispatcher`, or holding a `DispatcherTimer`, is correct WPF and is not
DI-resolved. 18 `*.xaml.cs` + 3 Behaviors:
`KanbanDragDropBehavior:28,116` · `DragDropReorderBehavior:24,97` · `AtCommandAutocompleteBehavior:22,122,213`
· `VoiceModeOverlay:35` · `FirstRunWizardWindow:31` · `AssistantView:100` · `OptimizeView:68,72` ·
`MeetingAttendeeOverlay:81,120` · `Views/Controls/RecordingIndicator:11,77` ·
`Views/Controls/DialogOverlayHost:44` · `Dialogs/FolderMoveContentDialog:23` ·
`Dialogs/ModelDownloadContentDialog:33` · `Dialogs/RecordingContentDialog:24` ·
`Dialogs/Overlay/RecordingOverlayPanel:25` · `Dialogs/Overlay/OptimizingOverlayPanel:10,19` ·
`Dialogs/OptimizingContentDialog:10,22` · `Controls/Assistant/PiaChatTitleChip:35,50,110,152,162,189` ·
`Controls/Assistant/PiaChatQuickSwitcher:17,22` · `Controls/Memory/PiaMemoryCategoryCard:77` ·
`Controls/Markdown/CodeBlockControl:27,76` · `Controls/MarkdownMessageControl:26,59`.

### (c) Genuine `Application`-level use — **27 lines in 13 files. Must NOT be abstracted.**

Theme-aware resource lookup: `Converters/ChatStateConverters:44` (+`:14` doc) ·
`Converters/VaultCategoryColorConverter:77,85` · `Converters/ReminderStatusToBrushConverter:33` ·
`Converters/MemoryTypeToBrushConverter:26,30` · `Converters/RunProgressConverters:41,85,110` ·
`Controls/Markdown/PiaMarkdownRenderer:178` · `Controls/Markdown/CodeBlockPalette:77,82,83`.
Merged theme dictionaries: `ThemeService:116,139`. Process shutdown: `TrayIconService:335`.
Window enumeration / `MainWindow`: `WindowManagerService:265,266` ·
`ScheduledJobNotificationSurface:175,177,183` · `BackgroundChatNotificationSurface:159,161`.
A managed window's own dispatcher inside a service: `WindowManagerService:106,289`
(their `DispatcherPriority` continuation lines `:108`/`:295` are the same two statements and are not counted
separately, which is why the total is 27 and not 29).
The global exception hook: `App.xaml.cs:69`.

`Application.Current` **is** the resource/window/shutdown root; abstracting that would be a second, larger
batch with no test to buy. Converters are not DI-resolved and their `TryFindResource` is already null-safe.

### (d) Service layer marshaling onto the UI thread — **11 sites in 7 files. Eligible, NOT required.**

`AgentRunNotificationSurface:74,182` · `BackgroundChatNotificationSurface:77,231` ·
`ScheduledJobNotificationSurface:122,228` · `OutputService:31` · `ThemeService:90` · `TrayIconService:322` ·
`WindowManagerService:227,235-237`.

Same static dependency, but these are Services, not covered by the ViewModel rule, and **none is constructed
as a real instance anywhere in the suite except `WindowManagerService`, once** (`WindowManagerServiceTests.cs:24`
— verified by grepping every `new <Service>(` for all seven). **Two of them are load-bearing for the
acceptance test anyway:**

- **`OutputService.cs:31` is the one site with no null guard** — `Application.Current.Dispatcher.Invoke(...)`.
  Today that is an NRE if a test ever reaches it; with a live `Application` it becomes a **blocking
  cross-thread `Invoke`**.
- `WindowManagerServiceTests.ShowAgentRun_MissingRun_RetractsStaleItem_AndDoesNotThrow` (`:31`) today takes the
  null branch at `WindowManagerService.cs:227-229`. With a live App it instead
  `await dispatcher.InvokeAsync(() => ShowStaleRunToast(runId))`. It still *passes* (no window is active, so
  `TryFindForegroundSnackbarPresenter` returns null and the toast is a no-op) — **but only if that dispatcher
  is running.** On a non-pumping dispatcher both this and `OutputService` **hang** rather than fail.

That is why "the shared STA thread's dispatcher must be *running*" is a correctness requirement of the
acceptance test, not a convenience.

### (e) Already abstracted — why 14 ViewModels are fine and need no work

`UiThreadViewModel.cs`: field `:17`, capture `_sync = SynchronizationContext.Current` at `:21`, `Post` `:34`,
`PostAsync` `:47`, `PostOrRun` `:68`; **no captured context → run inline**, which is exactly the fallback the
migrated VMs need to keep. Two details worth carrying: it already exposes `HasUiContext` at `:28` (a *capture*
probe, not a thread probe), and its "must be constructed on the UI thread" throw at `:19-24` is **opt-in**
(`requireUiThread` defaults to `false`) — which is what keeps option B in §6 structurally available.
**14 files** derive from it. Explicit captures of the same idiom outside it: `RunProgressViewModel.cs:119`,
`ViewModels/Models/ChatSessionManager.cs:139`, `ViewModels/Models/LiveTurnExecutor.cs:34`,
`Services/Plugins/PluginIconLoaderService.cs:19`. `IAgentRunService.cs:45` documents the inverse contract
(captures nothing, callable from any thread).

So the layer is already **two-thirds converted by convention** — this batch finishes it.

### The rule that already says so, and its exemption list

`DependencyInjectionTests.ViewModels_MustNotReference_SystemWindows` (`:13-32`) asserts *"ViewModels must not
reference System.Windows (use SynchronizationContext instead)"* — and carries **four hand-maintained name
exemptions** at `:24-27`: `VoiceModeViewModel`, `AssistantViewModel`, `TranscriptOverlayViewModel`,
`MeetingAttendeeViewModel`.

Note for the next reader: the general claim that this repo's architecture tests have no exemption lists is
true of `NamingConventionTests` and `LayerDependencyTests` — it is **false of this one**.

**But the list's own rationale is unreliable, and the batch must not build on it.** The comment at `:15-20`
says AssistantViewModel is flagged *"transitively because it creates VoiceModeViewModel"*. That cannot be the
mechanism: constructing a `Pia.ViewModels` type is not a reference to `System.Windows`. AssistantViewModel is
flagged **directly**, and for two independent reasons (next section). Symmetrically,
`MeetingAttendeeViewModel` has no `System.Windows` token of its own, so its entry is either base-type
dependency resolution or vestigial. The list was written from reasoning, not from a run. §3's Step 0 replaces
the reasoning with a measurement.

### The dependency that dispatcher migration does NOT remove

`AssistantViewModel` references `System.Windows` for a second, unrelated reason — the clipboard image paste:

- `:7` `using System.Windows.Media.Imaging;`
- `:183` `public IAsyncRelayCommand<BitmapSource> HandleImagePastedCommand { get; }`
- `:265` `new AsyncRelayCommand<BitmapSource>(ExecuteHandleImagePasted)`
- `:1079-1088` `private async Task ExecuteHandleImagePasted(BitmapSource? source)`, which calls
  `source.CanFreeze` / `source.Freeze()` and hands the bitmap to `ImageAttachmentProcessor.TryPrepare`.
  Invoked from `AssistantView.xaml.cs:153-154`.

Those four references are **inspected fact**. What is *not* measured here is the consequence: whether
`NetArchTest.Rules` 1.3.2's `HaveDependencyOn("System.Windows")` flags a reference to
`System.Windows.Media.Imaging.BitmapSource`. It almost certainly does (prefix matching), in which case
`AssistantViewModel`'s exemption **survives this batch** and only its comment changes. Do not guess — Step 0
measures it in one minute.

## 2. The abstraction

```csharp
// src/Pia.Wpf/Services/Interfaces/IUiDispatcher.cs — namespace Pia.Services.Interfaces
public interface IUiDispatcher
{
    void Post(Action action);          // fire-and-forget onto the UI thread
    Task PostAsync(Action action);     // awaited, so the caller observes it applied
    void PostOrRun(Action action);     // inline when already on the UI thread
}
```

Three members, mirroring `UiThreadViewModel` so the two idioms read identically.

**No `IsOnUiThread` probe.** Checked every call site: `DispatchToUi`'s `CheckAccess()` is exactly
`PostOrRun`; `VoiceModeViewModel`'s two `BeginInvoke`s are `Post`; the six `AssistantViewModel` sites are
`Post`/`PostAsync`. **Nothing needs the boolean itself**, and exposing it invites a check-then-act race.
(`UiThreadViewModel:28`'s `HasUiContext` is not a counter-example — it answers "was a context captured", not
"am I on the UI thread".) Add a probe only when a caller appears that genuinely needs one.

**Production:** `UiDispatcherService` (`src/Pia.Wpf/Services/UiDispatcherService.cs`, `Pia.Services`), reading
`Application.Current?.Dispatcher` per call — **not** cached in a field, because DI resolution order versus
`Application` construction is not something a ViewModel should depend on. Null dispatcher → run inline
(preserves today's fallback). `PostOrRun` → `CheckAccess()` then inline. It keeps the try/catch-and-log that
`TranscriptOverlayViewModel.cs:417-425` has today, so a marshal failure still cannot take down a caller.

**Test double:** `InlineUiDispatcher` in the test project — all three members invoke inline, `PostAsync`
returns `Task.CompletedTask`. This **restores today's behaviour rather than changing it**: the null-`App.Current`
path already runs inline, so every existing assertion keeps holding, and it does so *deterministically*
instead of by accident of a static being null.

**Checked against the architecture rules — all five:**

- `DiRegistrationTests.AllServiceInterfaces_MustHaveRegisteredImplementation` (`:25`) sweeps every interface in
  `Pia.Services.Interfaces`, so `IUiDispatcher` **must** be registered in `Bootstrapper.ConfigureServices`
  (`AddSingleton`, next to `ILocalizationService` at `Bootstrapper.cs:517`). Forgetting it is a red test, not
  a runtime surprise. (That test only inspects the `IServiceCollection`; it never builds a provider.)
- `DependencyInjectionTests.ViewModels_MustOnlyInject_InterfacesOrViewModels` (`:79`) — it is an interface. ✅
- `NamingConventionTests.ServiceClasses_MustFollowNamingConvention` (`:26`) — the suffix allow-list at
  `:32-39` has no "Dispatcher", so the class is `UiDispatcherService`. ✅ (rejected alternative in §6)
- `LayerDependencyTests.Services_ShouldNot_DependOn_ViewModels` (`:22`) — the implementation depends on
  `System.Windows` only, and no rule forbids that for Services (six of them already do). ✅
- `MvvmPatternTests.ViewModel_InjectedFields_MustBeReadonly` (`:39`) — the new `_uiDispatcher` field must be
  `readonly`. · `AsyncSafetyTests` — no `async void` introduced.

Both VMs that gain a ctor parameter are DI-registered (`Bootstrapper.cs:586` `AddScoped<AssistantViewModel>()`,
`:587` `AddScoped<MeetingAttendeeViewModel>()`), so a singleton `IUiDispatcher` resolves into them with no
registration change beyond #3.

## 3. The migration order

### Step 0 — measure the exemption list before writing any code

Delete all four names from `DependencyInjectionTests.cs:24-27` **locally and throwaway** (do not commit), run
*only* `ViewModels_MustNotReference_SystemWindows`, and write down which types it names and — from the
failure message — on what. That answers, as fact rather than inference:

1. Does `AssistantViewModel` get flagged for `System.Windows.Media.Imaging` alone? If yes (expected), its
   exemption stays and this batch deletes **three** names, replacing its comment with the real reason
   (`BitmapSource`, `:7/:183/:265/:1079`) instead of the wrong one (`VoiceModeViewModel`).
2. Is `MeetingAttendeeViewModel` flagged only via its base? If it is not flagged at all with the base still
   dirty, its entry is vestigial and can go in Unit 2 regardless.
3. Is `VoiceModeViewModel` / `TranscriptOverlayViewModel` flagged on exactly the tokens listed in §1(a)?

Then restore the four names and start Unit 1. Ten lines of notes here prevent a whole unit ending in a red
test nobody predicted.

### Unit 1 — `VoiceModeViewModel` first, then `AssistantViewModel`

Smallest blast radius in the repo: 2 use sites, **0 test construction sites**, not DI-registered (constructed
at `AssistantViewModel.cs:1324`), 6-param ctor → 7. Then AssistantViewModel's 6 sites (29-param ctor → 30).
Delete whichever names Step 0 says are now clean.

Migrating `VoiceModeViewModel` also removes a latent bug: `Dispatcher.CurrentDispatcher` **creates** a
dispatcher for whatever thread constructs the VM, so if that construction ever moved off the UI thread the
`BeginInvoke`s would queue to a dispatcher nobody pumps — silently. Injection makes the target explicit.

### Unit 2 — `TranscriptOverlayViewModel`, then `MeetingAttendeeViewModel`

The base is where the work is: replace the body of `DispatchToUi` with `_uiDispatcher.PostOrRun(...)` and
**keep the method**, its name, its `protected` visibility and its try/catch. All 9 callers, and the
`Action<Action>` seam that `SpeakerModelDownloadUi` (`MeetingAttendeeViewModel.cs:197`; the only
`new SpeakerModelDownloadUi(` in the tree) is fed, then change **zero** lines.
`TranscriptOverlayViewModel` is `abstract` with a `protected` ctor (`:70-74`, 4 params → 5);
`MeetingAttendeeViewModel` (`:93-99`, 6 params → 7, `: base(...)` at `:100`) is its **only** subclass, so
exactly one `base(...)` call updates.

**`MeetingAttendeeViewModel` is the load-bearing one: 42 of the 48 tests in
`MeetingAttendeeViewModelTests.cs` depend on the current inline fallback.** Do Unit 2 in one commit and run
the full suite before any exemption deletion. If those 42 stay green with `InlineUiDispatcher` wired, the
abstraction is behaviour-preserving; if any goes red, the double is wrong, not the tests. Unit 2 also owns the
stale header comment at `MeetingAttendeeViewModelTests.cs:18-19` ("DispatchToUi runs inline when there is no
WPF Application") — once the acceptance test exists there *is* one in the process, so rewrite it to say the
double is what makes the tests inline.

**Then, and only then**, the acceptance test. Run the full suite immediately after the first commit that
creates a real `Application` — categories (c) and (d) are the residual risk set, and the suite is the only
instrument that can name what else notices.

## 4. The cost

Counted from the tree, not estimated.

- **New files: 3** (`IUiDispatcher.cs`, `UiDispatcherService.cs`, `InlineUiDispatcher.cs` in tests) +1 for
  the acceptance test. **Edited: 6** (3 VMs + `Bootstrapper.cs` + `DependencyInjectionTests.cs` + the
  transcript base) plus 3 test files.
- **Constructor signatures changed: 4** — `VoiceModeViewModel` (6→7), `AssistantViewModel` (29→30),
  `TranscriptOverlayViewModel` (4→5, `protected`), `MeetingAttendeeViewModel` (6→7).
- **Test construction sites: 4**, all in 2 files — `AssistantViewModelLeverTests.cs:39` (Meeting) and `:47`
  (Assistant), `MeetingAttendeeViewModelTests.cs:741` and `:763`. `new VoiceModeViewModel(` appears in
  **tests zero times** (only `AssistantViewModel.cs:1324`); `new TranscriptOverlayViewModel(` zero (abstract).
- **NSubstitute / hand-written fakes that break: none.** This adds a *new* parameter rather than widening an
  existing interface, so unlike Batch 11's `Arg.Any<AgentContextBudget?>()` churn across 6 stub sites, no
  existing `Substitute.For<>` or hand-rolled stub needs re-stubbing. `InlineUiDispatcher` is a 10-line class,
  not a mock.
- **Warnings: must stay at 194.** Watch xUnit1051 (pass `TestContext.Current.CancellationToken`), xUnit2013
  (`Assert.Single`), and nullable on `Application.Current?.Dispatcher`.

## 5. Acceptance

**Primary, and independent of any NetArchTest matching semantics:** `git grep -n "App\.Current\|Application\.Current"
-- src/Pia.Wpf/ViewModels/` returns **nothing**, and a committed test parses `AssistantView` and asserts the
composer hint's `Visibility` tracks `ForeignRunActive` — with the full suite still green (2157+, 0 failed) and
the build at 194 warnings. Both migration targets were verified to reach that state: `TranscriptOverlayViewModel:416`
is its file's only `System.Windows` token, and `VoiceModeViewModel`'s are exactly `:4/:21/:59/:124/:164`.

**Secondary, and conditional on Step 0:** the exemption names at `DependencyInjectionTests.cs:24-27` that
Step 0 showed to be dispatcher-only are deleted, and any name that must stay (expected:
`AssistantViewModel`, for `BitmapSource`) has its comment rewritten to the reason that actually applies.

*Optional, contingent on Step 0's output:* if `AssistantViewModel` must stay exempt from the blanket rule, a
narrower second `[Fact]` asserting it does not depend on `System.Windows.Threading` or
`System.Windows.Application` would keep the dispatcher ban enforced for it. This rests on the **same**
unmeasured matching semantics as the question Step 0 settles, so decide it after Step 0 and verify it by
running, not by reading.

### The withdrawn test design, as the starting point

It worked before it was withdrawn. Reproduce it, do not re-invent it:

1. **ONE shared long-lived STA thread with a RUNNING dispatcher.** `static Lazy<Dispatcher>`; background
   thread, `SetApartmentState(STA)`, `IsBackground = true`, signal the created `Dispatcher.CurrentDispatcher`
   out, then `Dispatcher.Run()`. **Created once, never shut down** — `Application.Current` cannot be torn
   down, and its merged Wpf.Ui dictionaries are thread-owned. A thread-per-test design dies on the **second**
   test with *"Initialization of `Wpf.Ui.Controls.Button` threw an exception"*. `Dispatcher.Run` is not
   optional: see the `OutputService` / `WindowManagerService` hazard in category (d) — an unpumped dispatcher
   turns a blocking `Invoke` into a hang. Prior art for the STA-thread plumbing (not for the App or the
   dispatcher loop, which none of them has): `EmojiInlineBuilderTests.cs:70`, `EmojiInkBoundsTests.cs:117`,
   `EmojiImageRendererTests.cs:144`.
2. **Create the real `Pia.App` on that thread and call ONLY `InitializeComponent()`.** Never `Run()`,
   never `OnStartup` — `App.xaml.cs:36`'s `OnStartup` awaits `Bootstrapper.InitializeAsync()`, opens the
   database and shows windows. `App.xaml.cs:28-29` is the precedent: `new App(); app.InitializeComponent();`.
   Guard it so it happens at most once per process.
3. **Assume concurrency, because the runner does.** There is no `xunit.runner.json` in
   `tests/Pia.Wpf.Tests` and no `[assembly: CollectionBehavior]`, so xunit v3 parallelises test **collections**
   by default. The `Application` this test creates is therefore visible to other collections running at the
   same time, and that cannot be ordered around — it has to be **behaviour-neutral**, which is exactly what
   the migration buys and what the full-suite run after the first App-creating commit verifies.
4. **Marshal every fact onto that thread.** Construct the view *and* its DataContext there and return only
   the assertion's value (a `Visibility`, a `string`) back to the test thread.
5. **`Pump()` after every bound-property change.** Binding values do not transfer until the queue drains:
   push a `DispatcherFrame`, `BeginInvoke(DispatcherPriority.SystemIdle, () => frame.Continue = false)`,
   `Dispatcher.PushFrame(frame)`. Without it the test asserts the property default and looks like a pass.
6. **DataContext is the REAL `AssistantViewModel`.** The claim this batch makes is that real ViewModels are
   safe under a live `Application`; a lightweight INPC stub would sidestep exactly that and stop being a
   regression test for the migration. Reuse is **not** free, though: the builder is
   `private AssistantViewModel CreateSut()` at `AssistantViewModelLeverTests.cs:31` — an *instance* method
   backed by six instance `Substitute.For<>` fields at `:24-29`. So either the acceptance test builds its own
   VM (29 args, of which `MeetingAttendeeViewModel` and `ChatTitleChipViewModel` are the point), or the
   extraction lifts those six fields into parameters of a shared internal builder. Note that `CreateSut`
   already installs a `SynchronizationContext` when none exists (`:34-35`) — the acceptance test's thread
   will have a real one, which is the behaviour under test.
7. **Locate the hint by its rendered text**, as the withdrawn test did — walk the visual/logical tree for a
   `TextBlock` whose `Text` equals the EN resource *"A background run is writing to this chat. Sending resumes
   when it finishes."* (`ViewStrings.resx:99`; de/fr at `:99` of their files, both real translations). That
   single assertion covers three failure modes at once: the view parses, the `loc:Str` key resolves, and the
   binding path is right. Flip `ForeignRunActive` false→true, `Pump()`, assert `Collapsed` → `Visible`.

**Record in the test's own comment why it exists:** a deliberately misspelled binding path was **confirmed**
to produce a silently always-visible hint with the build still at **0 errors**. That is the regression this
test catches and nothing else in the suite can.

**Free extra, worth taking:** `LocalizationSource.cs:46` returns the literal `"[Key]"` for an unknown key, and
`StrExtension.ProvideValue` binds `[{Key}]` against the static `LocalizationSource.Instance` (no DI) whose
`_culture` defaults to `InvariantCulture`, i.e. the neutral EN resx. So a sweep asserting that no
`TextBlock.Text` in the parsed tree matches `^\[\w+\]$` is a **missing-`loc:Str`-key detector for the whole
view**, for about five lines.

## 6. Decisions, including the rejected ones

- **`IUiDispatcher` injection, not "make the four VMs derive from `UiThreadViewModel`".** All three classes
  derive from `ObservableObject` directly (`AssistantViewModel.cs:24`, `TranscriptOverlayViewModel.cs:30`,
  `VoiceModeViewModel.cs:14`), and `UiThreadViewModel`'s UI-thread throw is opt-in (`:19-24`,
  `requireUiThread: false` by default), so option B is structurally *available* and would cost **zero** ctor
  changes and zero test-site changes. It is rejected because it keeps the behaviour tied to **ambient state at
  construction time**: `UiThreadViewModel.cs:21` captures whatever `SynchronizationContext.Current` is on the
  constructing thread, so once a live `Application` exists on a *different* thread than the test's, what a VM
  does depends on where it happened to be built. The maintenance cost of that coupling is already visible —
  **seven** test files set or null the ambient context by hand to get determinism:
  `AssistantHistoryViewModelFilterTests.cs:45`, `RunProgressViewModelTests.cs:67`,
  `ChatSessionManagerTests.cs:45-46`, `ChatTitleChipFlyoutGroupingTests.cs:32-33`,
  `FlowViewModelReconcileTests.cs:46`, `AssistantViewModelLeverTests.cs:34-35`,
  `LiveTurnExecutorPlannedRunTests.cs:146-147`. An injected dispatcher is stated, not sniffed.
- **`UiThreadViewModel` and its 14 subclasses are NOT touched.** They work, their tests pin them, and
  rewriting them would multiply this batch's blast radius for no new capability. The two idioms coexist, with
  identical member names; converging them is a later, optional cleanup.
- **`UiDispatcherService`, not `UiDispatcher` + a new entry in `allowedSuffixes`.** Adding "Dispatcher" to
  `NamingConventionTests.cs:32-39` would be defensible (the list already admits Planner/Orchestrator/
  Launcher/Executor), but editing an architecture rule to admit the code you are writing is the wrong default,
  and the suffix buys nothing here.
- **`AssistantViewModel.cs:1007` becomes an awaited `PostAsync`, not a `Post`.** It is a blocking `.Invoke`
  inside a `Task.Run(async …)` whose `try/catch` logs the failure; fire-and-forget `Post` would move
  exceptions **out** of that catch. Await it and both the ordering and the error handling are preserved.
- **Category (d) is out of scope, deliberately.** The 7 service files can adopt `IUiDispatcher` later; they
  are not blocking the view test and are not covered by the ViewModel rule. Recording the two hazards is
  enough for now.
- **No `IsOnUiThread` probe**, as in §2.
- **The exemption list's rationale is not used as an argument.** Its comment is wrong about
  `AssistantViewModel` (§1), so the batch measures (Step 0) instead of inheriting the reasoning.

## 7. The risk the owner should weigh

The real prize is not the view test. It is that a whole layer stops depending on a process-global static:
after this batch a ViewModel's threading behaviour is a **constructor argument**, which means it is
substitutable, greppable and reviewable. Batch 10's `ForeignRunActive` marshaling and Batch 11's compaction
both had to reason about "which thread is this on"; that reasoning becomes checkable.

The cost is breadth: 4 constructor signatures and 4 test sites in one batch, and **a half-migrated layer is
worse than either end state** — some ViewModels marshaling through the injected dispatcher and others still
through `App.Current` means two threading models in one window, and the failure that combination produces
(work silently queued to a dispatcher nobody pumps) is exactly the class of bug that is invisible to a green
suite.

**How to avoid that, concretely:** the exemption list is the ratchet — a name can only be deleted once its VM
actually stops referencing `System.Windows`, and until then the rule is red. That mechanism holds regardless
of *why* each name is currently on the list, which is the point: do **not** rely on the "two transitive
pairings" story (the comment's version of it is provably wrong for `AssistantViewModel`). Instead: run Step 0,
ship each unit as one commit with its now-justified exemption deletions **inside** that commit, and run the
full suite between the two units. Add the `git grep` check from §5 to the definition of done, because it is
the one criterion that does not depend on how NetArchTest resolves a namespace prefix.

## Still open after this batch

The other Views are still unparsed by any test — `AssistantView` is the first, not the last, and the same
misspelled-binding hazard remains everywhere else · category (d)'s 11 service sites still read
`Application.Current` · `OutputService.cs:31` still has no null guard · `AssistantViewModel` probably still
names `BitmapSource`; closing that means moving the clipboard→attachment conversion out of the VM (the call
site is `AssistantView.xaml.cs:153-154`), which is a separate change and is not designed here · abstracting
category (c) (resources / windows / shutdown) is a separate, larger batch with no test to buy · and the shared
STA thread is a process-wide singleton that can never be torn down, so a future test that needs a *different*
`Application` configuration cannot have one.
