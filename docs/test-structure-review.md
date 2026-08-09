# Test project structure review

Scope: structural/organizational review of the repo's test projects.

> **Status 2026-08-09 — worked through. Read this header before the body: grounding refuted several
> claims below, and the body has been left as originally written so the corrections are visible.**
>
> **Done:** #1 (silent skips → `Assert.Skip`), #3 (dead runner packages dropped, MTP-native coverage
> wired, runsettings deleted), #4 both halves (`Directory.Build.props` + `TreatWarningsAsErrors`;
> network gate moved into code via xunit `Explicit`), #5 (namespaces normalised to `Pia.Tests.*`),
> #6 (one taxonomy — `Unit/` dissolved into subject folders), #7 (the consolidatable test doubles),
> #8 (comment sweep), #10 (the delay barriers that were real).
>
> **Not done, deliberately:** the CI test job (#2) — owner excluded it from scope.
>
> **Corrections the body gets wrong:**
> - **#0 / item 7 (split `Pia.Shared` tests) — premise does not hold.** Only **2 of the 65** files are
>   pure-Shared (5 tests total); the other 63 need types from `src/Pia.Wpf`. And `Pia.Shared` has
>   **zero method declarations** project-wide — 41 classes / 2 records / 1 enum / 2 static catalogs of
>   get-set DTOs, one computed property. There is no "Shared logic" to test on a non-Windows agent;
>   those 65 are client tests that use Shared DTOs as test data. `OutputType=Exe` is also not a smell:
>   xunit.v3 3.2.2 **hard-errors** without it, so a new project would carry it too. The defensible
>   remedy — declaring the `Pia.Shared` `ProjectReference` instead of inheriting it transitively — is
>   done.
> - **#10 (sleep-based sync) — 3 of the 4 cited sites are correct as written.** `HeadlessRunLauncherTests.cs:773`,
>   `JsonlConsentAuditLogTests.cs:71` and `E2EEOnboardingViewModelTests.cs:226` are each followed by a
>   *negative* assertion, where "nothing happened" has no state to signal and a `TaskCompletionSource`
>   is structurally impossible. Only `HeadlessRunLauncherTests.cs:799` was a real defect (and it was a
>   silent-pass, not merely slow). Two sites the review **missed** were fixed instead:
>   `TaskExtensionsTests.cs:34` and `:77`. The "missing cancellation token" point is also backwards —
>   there are **11** tokenless `Task.Delay` calls, so `:304` is consistent with its siblings.
> - **#6 — `Helpers/` is not the worst offender; it needs no change.** All four of its subjects live in
>   `src/Pia.Wpf/Helpers/`, so it already mirrors a src folder 1:1 under the subject-first taxonomy the
>   review itself recommends.
> - **#6 — the `MistralProviderHandlerTests` name collision is a reporting ambiguity, not a build
>   error.** The two copies sit in distinct namespaces before and after the rename.
> - **#6 — the "1,717 lines" belongs to `ScheduledJobBackgroundServiceTests.cs`,** not
>   `ScheduledJobServiceTests.cs` (726 lines). The argument survives; the evidence was misattributed.
> - **#7 — `Harness` is not consolidatable.** Only 9 of the 13 share a shape, and those differ in
>   member exposure, `NewRunAsync` arity, `Build` name/arity, and one uses `AgentRunTrigger.Schedule`
>   where the rest use `User`. The other 4 are unrelated types sharing an identifier.
> - **#8 — the census is an undercount, not an error.** Beyond `<para>` and `Batch NN` there are also
>   `T<n>-<n>`, `T-WORD-<n>`, `hermes #<n>`, `Phase <n>` and bare decision IDs. Also:
>   `ToolPipelineTestBase.cs:32`'s "keep in sync with `AssistantViewModel.BuildSystemPrompt` (line 20)"
>   was false three ways — that type no longer exists, the line was wrong, and 40 of the 41 prompt lines
>   had already drifted from production.
>
> **Still open, and worth a look:** `AssistantChatConcurrencyTests.DeleteAllAsync_WithAnotherConnectionCommittingThroughout_Completes`
> is genuinely flaky under full-suite load (1 failure in 12 full runs; 5/5 clean in isolation), and
> `E2EEOnboardingViewModelTests.GoBack_WhilePolling_ShouldStopPolling` is **vacuous** — `PollInterval`
> is a hard-coded 5s and the loop delays before its first poll, so the test passes even if
> `StopPolling()` were a no-op. Fixing it needs `PollInterval` injectable, which is a production
> change and out of scope for a test cleanup.

## 0. There is only one test project

The prompt said "each test project". There is exactly one:

| Project | Framework | Files | Lines |
|---|---|---|---|
| `tests/Pia.Wpf.Tests` | `net10.0-windows10.0.17763.0`, `UseWPF=true` | 375 `.cs` | 93,185 |

It contains 2,906 `[Fact]` + 183 `[Theory]` attribute occurrences and covers **both** production projects. `Pia.Shared` (`net10.0`, platform-agnostic) has no test project of its own — 65 test files `using Pia.Shared`, all through a Windows-only, WPF-hosting, `OutputType=Exe` assembly. Shared's logic cannot be tested on a non-Windows agent, cannot be tested without dragging in the WPF stack, and its test failures are indistinguishable from client failures in the run output.

Worse, the test project's only `ProjectReference` is to `Pia.Wpf` — it reaches `Pia.Shared` **transitively**, through the client. So 65 files test a project the test assembly never declares a dependency on. Nothing stops a Shared test from accidentally depending on client types, and if `Pia.Wpf` ever drops the Shared reference the tests break for a reason unrelated to what they assert.

That single-assembly choice is the root cause of most of what follows.

---

## Findings, most severe first

### 1. Nine tests report green without executing (BLOCKING)

`Integration/ToolPipelineTestBase.cs:20` defines `ShouldSkip => string.IsNullOrEmpty(ApiKey)`. Consumers use it like this:

```csharp
// Integration/TodoToolIntegrationTests.cs:13
if (ShouldSkip) return;
```

An early `return` from a `[Fact]` is a **pass**, not a skip. With `PIA_TEST_API_KEY` unset — the normal state on every dev machine — these tests execute zero assertions and are counted as passing:

- `Integration/TodoToolIntegrationTests.cs:13, 45, 86`
- `Integration/ReminderToolIntegrationTests.cs:13, 50`

Same anti-pattern, different gate, in `Unit/EmbeddingServiceSemanticTests.cs:31, 51, 68, 88`:

```csharp
var svc = CreateIfAvailable();
if (svc is null) return; // model not on disk — skip
```

The comment says "skip"; the runner says "passed".

**The correct pattern is already the repo's norm.** xunit v3's dynamic skip is used in four separate areas — 29 `Assert.Skip` call sites across the six `Integration/Providers` files, 12 in `Services/GitToolHandlerRealGitTests.cs`, 3 in `Emoji/`, 1 in `Helpers/GitProcessRunnerWiringTests.cs`:

```csharp
// Integration/Providers/MistralProviderHandlerTests.cs:36
if (provider is null) { Assert.Skip("PIA_TEST_MISTRAL_KEY not set"); return; }
```

So the nine silent returns are outliers against an established convention, not an un-migrated majority. Fix is mechanical: replace them with `Assert.Skip(reason)`. Until then the pass count overstates real coverage and nobody can tell by how much without reading source.

### 2. CI never runs the test suite (BLOCKING)

`.github/workflows/build-and-release.yml` is the only build workflow. Its steps are: checkout → setup-dotnet → Azure OIDC login → AzureSignTool → **`dotnet restore src/Pia.Wpf/Pia.Wpf.csproj`** → publish → sign → Velopack → MSI → git-cliff → GitHub Release.

Grepping the whole `.github/workflows` directory for `dotnet test` or `dotnet build` returns nothing. The restore is scoped to the client `.csproj`, so the test project is never compiled in CI — `Pia.Shared` is built, but only as a transitive dependency of the `publish` step. The ~3,000 tests run only when a human remembers to run them locally.

This makes finding #1 considerably worse: no automated gate exists that would ever have surfaced the silently-passing tests.

The "zero-warning policy" in `CLAUDE.md` is likewise enforced entirely by human discipline — the publish step doesn't fail on warnings, `TreatWarningsAsErrors` / `WarningsAsErrors` appear in **no** `.csproj` or `.props` file, and there is no `Directory.Build.props` at all.

### 3. Test-runner packages that cannot function

`Pia.Wpf.Tests.csproj` references:

| Package | Status |
|---|---|
| `xunit.v3` 3.2.2 | works (MTP entry point is generated) |
| `xunit.runner.visualstudio` 3.1.5 | **inert** — VSTest adapter, needs `Microsoft.NET.Test.Sdk` |
| `coverlet.collector` 10.0.1 | **inert** — VSTest data collector, needs `Microsoft.NET.Test.Sdk` |

`Microsoft.NET.Test.Sdk` is absent, and `global.json` pins `"test": { "runner": "Microsoft.Testing.Platform" }`. So the project is MTP-only and both VSTest-side packages are dead weight that misleads anyone reading the csproj into thinking coverage is wired up. This is the concrete reason the coverage setup has stayed unresolved: `coverlet.collector` will never emit anything under MTP.

Either drop both packages and adopt an MTP-native coverage path (`Microsoft.Testing.Extensions.CodeCoverage`), or add `Microsoft.NET.Test.Sdk` and go back to VSTest. The current state is neither.

Related dead config: **`Pia.Wpf.runsettings` is referenced by nothing.** Grep across every `.csproj`, `.props`, workflow, `.ps1`, and `.json` in the repo finds zero references and no `RunSettingsFilePath` property. Its `<TestSessionTimeout>300000</TestSessionTimeout>` has never applied to a single run.

### 4. Network-dependent tests gated by a caller-supplied string

`Integration/Providers` hits real provider APIs. The suite is kept green by passing `--filter-not-namespace "Pia.Wpf.Tests.Integration.Providers"` on the command line. The gate lives in the invocation, not the code, so correctness depends on every caller — human or script — remembering the flag. Nothing in the repo enforces or even documents it in the csproj.

`[Trait("Category", "Integration")]` exists and would be the natural mechanism, but it is applied to only **4** test classes out of 375 files — and notably *not* to the provider tests it would matter most for. Either make the trait consistent and filter on it, or move network tests to a separate project.

### 5. Two namespace conventions in one assembly

`RootNamespace` is `Pia.Tests`. Most files honor it, but ~35 files use `Pia.Wpf.Tests.*`:

| Namespace | Files |
|---|---|
| `Pia.Tests.*` | ~340 |
| `Pia.Wpf.Tests.Unit` | 13 |
| `Pia.Wpf.Tests.Unit.Providers` | 11 |
| `Pia.Wpf.Tests.Integration.Providers` | 8 |
| `Pia.Wpf.Tests.Services` | 2 |
| `Pia.Wpf.Tests.Infrastructure` | 1 |

This is not cosmetic — it is precisely why the network-test filter in finding #4 has to spell out `Pia.Wpf.Tests.Integration.Providers`, a prefix no other folder shares. A namespace-based filter over an inconsistent namespace tree is a trap: move a file and the gate silently stops applying.

Also note `src/` uses `Pia` (not `Pia.Wpf`) per `CLAUDE.md`; the `Pia.Wpf.Tests.*` files contradict the project's own stated convention.

### 6. Two orthogonal folder taxonomies, applied by accident of history

The root mixes **test-type** folders with **subject** folders:

```
Unit/          Integration/          <- by test type
Services/  ViewModels/  Views/  Vault/  Wiki/  Models/  Sync/  E2EE/  Consent/  ...   <- by subject
Architecture/                        <- by test type (NetArchTest rules)
Helpers/                             <- ambiguous: contains GitLocatorTests, VsCodeLauncherTests (subject), not helpers
```

`Unit/AssistantChatServiceTests.cs` and `Services/AssistantChat*Tests.cs` are the same kind of test in different trees. `Unit/ScheduledJobServiceTests.cs` (1,717 lines) tests a service, but lives under `Unit/` while every other service test lives under `Services/`. Placement tracks *when* a file was written, not *what* it tests — so there is no reliable answer to "where does a new test for X go?", and the tree keeps bifurcating.

`Helpers/` is the worst offender: the name promises shared infrastructure, but it contains five test classes and exactly one non-test file.

The collision this produces is visible in the type names. `MistralProviderHandlerTests` exists **twice** — `Unit/Providers/MistralProviderHandlerTests.cs` and `Integration/Providers/MistralProviderHandlerTests.cs`. The unit/integration split is defensible; giving both halves the identical class name is not. In a failure report, IDE test tree, or log line, "MistralProviderHandlerTests.ChatAsync_…" is ambiguous between a hermetic test and one that bills a real Mistral API call. Suffix them (`…HandlerLiveTests`) or let the taxonomy carry the distinction — not both half-way.

Pick one axis. Subject-first with a `[Trait]` for type is the lower-churn option given `Services/` already holds 134 files.

### 7. No shared test-double library — the same fakes are re-declared 6–13×

There is no `Fakes/`, `Doubles/`, or `TestInfrastructure/` folder. Test doubles are declared as nested/private types inside whichever test file needed one first, then copy-pasted. Verified declaration counts (anchored to real `class`/`record` declarations, not comment prose):

| Type | Distinct files declaring it |
|---|---|
| `Harness` | **13** |
| `InlineSyncContext` | **10** |
| `FakePlanner` | **10** |
| `StubEmbeddingService` | **9** |
| `CapturingHandler` | **8** |
| `RecordingExecutor` | **6** |
| `TestSettingsService` | 4 |
| `MockHttpMessageHandler` | 2 |
| `CapturingLogger` | 2 |

The `CapturingLogger` case shows the failure mode plainly: a shared one exists at the project root (`CapturingLogger.cs`), and `Services/AgentPlannerTests.cs` declares a second one anyway. Same for `FakeVerifier` — `Services/FakeVerifier.cs` is the shared copy, and `Services/AgentRunGraceTurnTests.cs` and `Services/AgentRunScopeCorrelationTests.cs` each redeclare it. Extracting a shared double doesn't help if nothing points authors at it.

`InlineSyncContext` across 10 `ViewModels/` files is the highest-value consolidation: it is the synchronization-context shim every VM test needs, and 10 independent copies means 10 places to fix when the dispatcher contract changes.

Note also that `Integration/ToolPipelineTestBase.cs:124-149` and `Integration/Providers/ProviderIntegrationFixture.cs:33-57` are near-verbatim duplicates — the same 8-handler array, the same `DpapiHelper`/`IHttpClientFactory`/`ISettingsService`/`IAuthService` substitute setup, the same `AiClientService` construction.

### 8. `CLAUDE.md`'s comment discipline is violated at scale in the test tree

The project's own rules ban multi-paragraph comments, `<para>` blocks, and task-ID citations. In `tests/`:

- **110 files** contain a `<para>` block — explicitly named as banned.
- **132 files** carry **390** `Batch NN` / `§N` occurrences — explicitly named as banned. (Raw grep hits; a few may sit in string literals rather than comments, but spot checks landed in XML-doc every time.)

`Views/WpfApplicationCollection.cs` violates both in one XML-doc: a 12-line `<para>` essay that also cites "Batch 12's migration". `Integration/ToolPipelineTestBase.cs:146` cites "T1-2".

The recent comment-cutting commits (`d25c6d98`, `c275c552`, …) covered only files this branch touched, so the untouched majority still carries the essays. Worth noting the rule exists precisely because these rot: `ToolPipelineTestBase.cs:32` says *"Keep in sync with AssistantViewModel.BuildSystemPrompt (line 20)"* — a hardcoded line number, plus a 42-line verbatim copy of a production system prompt that has no mechanism keeping it in sync.

### 9. Oversized test files

Ten files exceed 1,000 lines:

| File | Lines |
|---|---|
| `Services/HeadlessRunLauncherTests.cs` | 2,107 |
| `ViewModels/ChatSessionManagerTests.cs` | 2,088 |
| `Unit/ScheduledJobBackgroundServiceTests.cs` | 1,717 |
| `Services/HeadlessTurnExecutorTests.cs` | 1,467 |
| `Services/AgentRunOrchestratorTests.cs` | 1,375 |
| `Services/AgentRunOrchestratorCascadePauseTests.cs` | 1,200 |
| `Services/UnattendedApprovalParkTests.cs` | 1,178 |
| `ViewModels/LiveTurnExecutorPlannedRunTests.cs` | 1,093 |
| `Services/AgentRunOrchestratorFanOutTests.cs` | 1,066 |
| `Services/AgentPlannerTests.cs` | 1,063 |

The `AgentRunOrchestrator*` family has already been split by *scenario* (CascadePause, FanOut, UserPause, UserPauseLive, GraceTurn, ClarificationResume, ResumeNoRePlan) — which is the right instinct — but each shard re-declares its own `Harness` and `FakePlanner`, so the split multiplied the duplication in finding #7 instead of factoring it out. Splitting without extracting the shared harness first is what produced 13 `Harness` types.

### 10. Sleep-based synchronization in test bodies

85 `Task.Delay` call sites. Most are legitimate — `Task.Delay(Timeout.Infinite, ct)` inside a fake step that must stay in-flight until cancelled is the correct construct, and that accounts for the majority. But a real subset are wall-clock barriers in test bodies:

- `Services/HeadlessRunLauncherTests.cs:773` — `await Task.Delay(300, ct)`
- `Services/HeadlessRunLauncherTests.cs:799` — `await Task.Delay(100, …); // let it enter the planner`
- `E2EE/E2EEOnboardingViewModelTests.cs:226` — `await Task.Delay(100, …)`
- `Consent/JsonlConsentAuditLogTests.cs:71` — `await Task.Delay(50, …)`

These pass on a fast dev box and are the first things to fail on a loaded CI agent — which is exactly the environment finding #2 says doesn't exist yet. Fixing #2 will surface these. Prefer a `TaskCompletionSource` signalled from the fake over a fixed delay.

`E2EE/E2EEOnboardingViewModelTests.cs:304` additionally omits the cancellation token (`await Task.Delay(50);`), inconsistent with the `TestContext.Current.CancellationToken` convention used everywhere else.

---

## What is done well

Worth stating, since the list above is one-sided:

- **`Architecture/`** — 15 NetArchTest rule files (`LayerDependencyTests`, `MvvmPatternTests`, `NamingConventionTests`, `AsyncSafetyTests`, `DiRegistrationTests`) enforce the conventions `CLAUDE.md` describes. That is the right way to keep a 375-file suite honest, and most repos this size don't have it.
- **`Views/WpfApplicationCollection.cs`** — the process-wide WPF `Application` is correctly isolated behind `[CollectionDefinition(DisableParallelization = true)]`, and the doc-comment (policy violations aside) is honest about what the mechanism does *not* guarantee.
- **`Integration/Providers/*`** — correct `Assert.Skip` gating and a clean per-provider env-var lookup table in `ProviderTestEnvironment`.
- **`TestContext.Current.CancellationToken`** is threaded through the overwhelming majority of async tests.
- `TestResults/` is correctly gitignored (`.gitignore:67`).

---

## Suggested order of work

| # | Item | Effort |
|---|---|---|
| 1 | Replace 9 silent `return`s with `Assert.Skip` (#1) | trivial |
| 2 | Add a `dotnet test` job to CI (#2) | small |
| 3 | Drop `coverlet.collector` + `xunit.runner.visualstudio`, or add `Microsoft.NET.Test.Sdk`; delete or wire up `Pia.Wpf.runsettings` (#3) | small |
| 4 | Add `Directory.Build.props` with `TreatWarningsAsErrors` (#2) | small |
| 5 | Extract `TestInfrastructure/` and consolidate `InlineSyncContext`, `Harness`, `FakePlanner`, `StubEmbeddingService`, `CapturingHandler` (#7) | medium |
| 6 | Normalize namespaces to `Pia.Tests.*`, then convert the network gate from namespace-filter to `[Trait]` (#4, #5) | medium |
| 7 | Split `Pia.Shared` tests into their own `net10.0` project (#0) | medium |
| 8 | Sweep `<para>` and `Batch NN` out of the test tree (#8) | medium, mechanical |
| 9 | Replace the four wall-clock delays with `TaskCompletionSource` signals (#10) | small |
| 10 | Settle on one folder taxonomy (#6) | large, do last |

Items 1–4 are the ones that change whether a green run means anything.
