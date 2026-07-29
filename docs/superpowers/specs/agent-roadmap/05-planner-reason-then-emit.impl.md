# Batch 05 — Planner reason-then-emit · IMPLEMENTATION SPEC

Executable spec derived from [`05-planner-reason-then-emit.md`](05-planner-reason-then-emit.md) plus a full
re-read of the code it touches. Branch: `feature/agent-run-spine`. **Design step only — no production code
was written for this document.**

Gate for the implementing agent:

```
dotnet build -p:EnableWindowsTargeting=true --no-incremental      # 0 errors, EXACTLY 194 warnings
dotnet test tests/Pia.Wpf.Tests/Pia.Wpf.Tests.csproj -- --filter-not-namespace "Pia.Wpf.Tests.Integration.Providers"
                                                                  # failed: 0 (baseline 2194 total / 0 failed / 1 skipped)
```

Never pass `--nologo` to `dotnet test`. Known flake, do not chase:
`TaskExtensionsTests.SafeFireAndForget_SlowTask_DoesNotBlock`.

---

## 0. Corrections to the 05 spec (read this first)

1. **`IProviderCapabilityService` is NOT the Responses-API vs Chat-Completions gate.** The 05 spec's
   "Key seams" bullet names it as the discriminator. It is not one: `IProviderCapabilityService`
   (`src/Pia.Wpf/Services/Interfaces/IProviderCapabilityService.cs`) exposes only
   `GetPlanningCapabilityAsync` → `PlanningCapability { Capable, Weak, Unknown }`. There is no API/transport
   discriminator anywhere on it. Per the roadmap's working pattern (**the code wins over the spec**), the
   discriminator is introduced by this batch as a flag on `IAiProviderHandler` (§2, D2) — the only place that
   actually knows what request shape goes out.
2. **"Chat-Completions vs Responses-API" is the wrong axis anyway.** The real question is narrower: *does this
   handler drop the configured reasoning effort when tools are attached?* Two Chat-Completions handlers
   (`OpenRouter`, `VLlm`) inject reasoning through a `DelegatingHandler` **unconditionally**, so they are
   already boosted under tools and must NOT two-call. Two others never send effort at all
   (`OpenAiCompatible`, `PiaCloud`), so a reasoning turn cannot boost anything for them. The affected set is
   exactly three: **AzureOpenAI, Ollama, Mistral**.
3. **Test-blindness claim, corrected.** `LocalizationTests.AllXamlLocalizationKeys_MustExistInResources`
   (`tests/Pia.Wpf.Tests/Architecture/LocalizationTests.cs:50`) DOES catch a `loc:Str` key that is missing
   from the resources, and `AllTranslations_MustBeComplete` (:113) DOES catch en/de/fr parity. What genuinely
   stays uncaught, because no test parses a View: the two `Binding` **paths** and the `StaticResource` style
   names in new XAML. Those are the manual-smoke items (§9), nothing more.

---

## 1. Verified recon (re-read 2026-07-29; cite these, not the batch brief)

| # | Fact | Location |
|---|---|---|
| R1 | `hasTools` = `provider.SupportsToolCalling && tools is { Count: > 0 }` — computed identically on the streaming path and inside `GetChatResponseAsync`. A `tools: null` call therefore yields `hasTools: false` and `CreateChatOptions(provider, hasTools: false)`. **No new plumbing in `AiClientService`/`IAiClientService` is needed.** | `AiClientService.cs:148`, `:443-444` |
| R2 | `ToOpenAi(effort, hasTools)` → `ShouldSend = !hasTools && effort is not null and not None`. Tool gate present. `ToOpenAiResponses(effort)` → gated on effort only. No tool gate. | `Services/Providers/ReasoningEffortMapping.cs:16,32,46-47` |
| R3 | `MistralProviderHandler.ShouldEmitReasoning`: `null` → off; model not in `ReasoningCapableModels` → off; explicit `None` → `(true, None)` **before** the tool check; then `if (hasTools) return (false, default);`; else `(true, High)`. So turning reasoning ON is dropped under tools. | `MistralProviderHandler.cs:90-110` |
| R4 | Per-handler effort behaviour under `hasTools: true` — AzureOpenAI `ToOpenAi` **dropped** (`:37-39`); Ollama `ToOpenAi` **dropped** (`:34-36`); Mistral **dropped** (R3); OpenAI `ToOpenAiResponses` **survives** (`:53-55`); OpenRouter never calls the mapping, `OpenRouterReasoningHandler` rewrites the body unconditionally (`:16-33, 48-52`); VLlm `VLlmThinkingHandler` sets `enable_thinking` unconditionally (`:27`); OpenAiCompatible `CreateChatOptions => new()` (`:40`); PiaCloud same (`:49`). | as cited |
| R5 | `AiProviderHandlerResolver` is a `sealed class` taking `IEnumerable<IAiProviderHandler>` and dictionary-keyed on `ProviderType`; `Get` throws `NotSupportedException` for an unregistered type. Registered `AddSingleton<AiProviderHandlerResolver>()`; all 8 handlers `AddSingleton<IAiProviderHandler, …>`. `AiClientService` already injects the concrete resolver. | `AiProviderHandlerResolver.cs`, `Bootstrapper.cs:387-395`, `AiClientService.cs:29,38` |
| R6 | `AgentPlanner` ctor is `(IAiClientService ai, ILogger<AgentPlanner> logger)`; registered `AddTransient<IAgentPlanner, AgentPlanner>()`. | `AgentPlanner.cs:33`, `Bootstrapper.cs:482` |
| R7 | **Exactly one** construction site for the concrete `AgentPlanner` in the whole test project: `AgentPlannerTests.cs:24` (`private AgentPlanner BuildPlanner() => new(_ai, NullLogger<AgentPlanner>.Instance);` — target-typed `new`, which is why a `new AgentPlanner(` grep finds nothing). By contrast there are **four hand-written `IAgentPlanner` fakes**: `AgentRunOrchestratorTests.cs:38`, `HeadlessRunLauncherTests.cs:34`, `HeadlessTurnExecutorTests.cs:24` and `:141`, `LiveTurnExecutorPlannedRunTests.cs:54`. | as cited |
| R8 | `TokenizingAiClientService.TokenizeMessages` rewrites **only `ChatRole.User`** text to PII placeholders (`if (msg.Role != ChatRole.User …) { result.Add(msg); continue; }`). `GetChatResponseAsync` **detokenizes** assistant response text before returning it. | `TokenizingAiClientService.cs:262-279`, `:119-147` |
| R9 | `ChatResponse.Text` and `ChatResponse.Usage` (`UsageDetails?`) are the response accessors already used in the codebase. | `AiClientService.cs:342,598,606`; `ChatTitleService.cs:58` |
| R10 | `LlmTimeoutException : System.TimeoutException` (NOT an `OperationCanceledException`) and its **message embeds the provider name**: `$"Request to provider '{providerName}' timed out after …"`. | `Services/LlmTimeoutException.cs` |
| R11 | `AiProvider.SupportsToolCalling` defaults to **true**; `AiProvider.ReasoningEffort` is `ReasoningEffort?`, default **null**. | `Models/AiProvider.cs:24,56` |
| R12 | Agent settings precedent: `AppSettings.cs:167-172`; VM `:214-227` (declare + `…Display`), `:317-319` (load, clamped), `:464-466` (save); bool-toggle save shape `OnSuggestionsEnabledChanged` at `:116-119`; XAML sliders `AssistantView.xaml:359-408`, Scheduled block ends `:449`, tab closes `:451`. | as cited |
| R13 | Layer rules constrain ViewModels↔Infrastructure and Services↛ViewModels only. `Pia.Services.Providers` is a **sub**-namespace of `Pia.Services`, and `DependencyInjectionTests.ViewModels_MustOnlyInject_InterfacesOrViewModels` is ViewModels-only — so a Service injecting the concrete `AiProviderHandlerResolver` is allowed (and precedented by `AiClientService`). | `Architecture/LayerDependencyTests.cs`, `Architecture/DependencyInjectionTests.cs:79,129` |
| R14 | `src/Pia.Wpf/Pia.Wpf.csproj:69` has `<InternalsVisibleTo Include="Pia.Wpf.Tests" />`, so the test project can call `internal static ReasoningEffortMapping` and `MistralProviderHandler.ShouldEmitReasoning`. | as cited |
| R15 | `ExperimentalApiContainmentTests` polices **MAAI001 only**. An `OPENAI001` pragma in the test project is unconstrained by it. | `Architecture/ExperimentalApiContainmentTests.cs:26,74` |
| R16 | No `AssistantSettingsViewModel` test exists anywhere, and its ctor takes four **concrete** sub-VMs (`ProvidersSettingsViewModel`, `PersonaSettingsViewModel`, `ToolPermissionsSettingsViewModel`, `MeetingSettingsViewModel`) plus seven services — a unit test for it is disproportionate to a checkbox. | `ViewModels/AssistantSettingsViewModel.cs:33-44` |

---

## 2. Decisions

Owner decisions D1–D3 are settled inputs; D4–D10 are decided here.

### D1 (given) — the opt-in is a global `AppSettings` toggle, default OFF
Beside `AgentMaxSteps`/`AgentMaxReplans`/`AgentWallClockMinutes`. Not a per-provider `AiProvider` field.
Default OFF because the cost is real: one extra provider round per plan turn.

### D2 (given) — the "drops effort under tools" knowledge is a flag on `IAiProviderHandler`
Implemented by all eight handlers. Rationale to preserve in the code comment: *the knowledge lives next to
the handler that has it, so a future handler cannot silently be missed.* Not a `ProviderType` switch inside
`AgentPlanner`.

### D3 (given) — PLAN-ONLY this batch
`ReplanAsync` keeps its single constrained turn, with a code comment at `ReplanAsync` so the asymmetry reads
as a decision.

### D4 — Plumbing route: **new ctor dependencies on `AgentPlanner`** (`ISettingsService` + `AiProviderHandlerResolver`)

New ctor: `AgentPlanner(IAiClientService ai, AiProviderHandlerResolver handlers, ISettingsService settings, ILogger<AgentPlanner> logger)`.

Rejected alternatives and why:

- **Carry it on `RunProfile`/`RunContext`.** D1 makes the toggle **global**; `RunProfile` is documented as the
  per-run *budget envelope* and exists in two flavours (interactive `AgentMax*` vs unattended `Scheduled*`).
  Threading one global value through two envelopes means four `FromBudget` call sites
  (`ChatSessionManager.cs:773`, `HeadlessRunLauncher.cs:151` and `:279`, `ScheduledJobBackgroundService.cs:183`)
  each having to remember to pass it — a forgotten one silently disables the feature on that path, with no
  test that would notice. Also perturbs `RunProfileTests`' record-equality assertions for no gain.
- **A `PlanAsync` parameter.** Changes the `IAgentPlanner` interface, which breaks **four hand-written fakes**
  (R7) and forces `AgentRunOrchestrator` to acquire settings it does not otherwise need.
- **A new member on `IAiClientService`.** Breaks the two hand-written `IAiClientService` fakes
  (`Wiki/AiIngestSynthesisServiceTests.cs:241,271`) and puts a provider-capability query on the chat
  interface, where it does not belong.

The ctor route touches **one** test line (R7), reaches every launcher path automatically (interactive,
headless detach, scheduled) because the planner is the single consumer, and needs no interface change at all.
Both new dependencies are registered singletons; `AgentPlanner` is transient, so no captive-dependency issue.

### D5 — The analysis is appended to the **`User(goal)` message**, not to the System prompt
Decisive reason (**privacy**): `TokenizeMessages` tokenizes only `ChatRole.User` text (R8), and the analysis
comes back **detokenized** from the reasoning turn (R8, `:133-144`). Putting it in the System prompt — or in a
new `Assistant` message — would ship restored PII straight past the tokenizer whenever PII tokenization is
enabled. Secondary reason (**reliability**): the request shape stays exactly `[System, User]`, so no provider
sees a shape it has not seen from this code path before (no consecutive-`User` turns, which Mistral has
historically rejected, and no trailing-assistant prefill). Third: `AgentPlannerTests`' `LastPrompt` helper
reads `messages[0]`, so every existing prompt assertion keeps meaning what it meant.

Do **not** rely on placeholder-identity across the detokenize→re-tokenize round trip; nothing here needs it.

### D6 — Gate expression and ordering
```
settings.AgentPlanReasoningTurnEnabled
  && provider.SupportsToolCalling
  && provider.ReasoningEffort is not null and not ReasoningEffort.None
  && handler.DropsReasoningEffortWithTools
```
Cheapest first; the handler lookup last because it can throw (R5). `SupportsToolCalling` is in the gate
because when it is false the constrained turn **already** gets `hasTools: false` (R1) — the effort is already
being sent — *and* `emit_plan` is never attached, so planning is heading for the SingleTurn degrade anyway. A
reasoning turn there burns a round for nothing.

### D7 — Mistral's model-list nuance: **accept the wasted turn**
`ShouldEmitReasoning` also requires `provider.ModelName ∈ ReasoningCapableModels` (R3), which a per-handler
flag cannot know. Accepted: for a non-reasoning-capable Mistral model the reasoning turn still runs, at
default effort. It is not useless — *reason-then-emit is itself the mechanism* (a free-form decomposition the
constrained turn consumes); the boosted effort is an amplifier, not the whole benefit. Cost is one extra
round on a globally opted-in setting.
Rejected: making the member a method `bool DropsReasoningEffortWithTools(AiProvider provider)` so Mistral
could consult `ReasoningCapableModels`. It would turn a transport constant into a model-dependent query,
contradict D2's literal per-handler values, and make the conformance test (§8, T16) non-static.
**Record this in the code comment on the Mistral implementation.**

### D8 — The injected analysis is capped at 4000 chars
`TryCaptureAsync` passes **no** `contextBudget` (correct — two messages, nothing to compact), so an unbounded
analysis block could overflow a small local model's window and turn a working plan turn into a failing one:
exactly the reliability regression this batch is forbidden to cause. Truncate to `MaxAnalysisChars = 4000`
(≈1k tokens) with a visible marker. Log the truncation as metadata (char counts) only.

### D9 — No planner-side timeout on the reasoning turn
It is already bounded by `provider.TimeoutSeconds` (default 300s) inside `GetChatResponseAsync`, which
surfaces as `LlmTimeoutException` → caught by the degrade (§4). Rejected: a second, tighter linked CTS — it
would silently override the user's configured provider timeout, and today's plan turn has no separate cap
either. Accepted risk: a slow reasoning turn can consume run wall-clock, which the orchestrator only checks
between steps.

### D10 — Reason **once**; the R10 firm retry reuses the same analysis
The firm retry exists because the model wrote prose instead of calling `emit_plan` — a second reasoning turn
would not fix that and would pay for a fourth round. Worst case stays 3 provider turns per plan
(reason + emit + firm emit).

---

## 3. Files to touch

| File | Change |
|---|---|
| `src/Pia.Wpf/Services/Providers/IAiProviderHandler.cs` | new `DropsReasoningEffortWithTools` member + doc comment |
| `src/Pia.Wpf/Services/Providers/OpenAiProviderHandler.cs` | `=> false;` |
| `src/Pia.Wpf/Services/Providers/AzureOpenAiProviderHandler.cs` | `=> true;` |
| `src/Pia.Wpf/Services/Providers/OllamaProviderHandler.cs` | `=> true;` |
| `src/Pia.Wpf/Services/Providers/MistralProviderHandler.cs` | `=> true;` + the D7 comment |
| `src/Pia.Wpf/Services/Providers/OpenRouterProviderHandler.cs` | `=> false;` |
| `src/Pia.Wpf/Services/Providers/VLlmProviderHandler.cs` | `=> false;` |
| `src/Pia.Wpf/Services/Providers/OpenAiCompatibleProviderHandler.cs` | `=> false;` |
| `src/Pia.Wpf/Services/Providers/PiaCloudProviderHandler.cs` | `=> false;` |
| `src/Pia.Wpf/Models/AppSettings.cs` | `AgentPlanReasoningTurnEnabled` = `false` |
| `src/Pia.Wpf/Services/AgentPlanner.cs` | ctor + gate + reasoning turn + injection + comments |
| `src/Pia.Wpf/ViewModels/AssistantSettingsViewModel.cs` | observable property, changed-handler, load, save |
| `src/Pia.Wpf/Views/SettingsViews/AssistantView.xaml` | "Planning" section: header + CheckBox + description |
| `src/Pia.Wpf/Resources/Strings/ViewStrings.resx` | 3 keys (en) |
| `src/Pia.Wpf/Resources/Strings/ViewStrings.de.resx` | 3 keys (de) |
| `src/Pia.Wpf/Resources/Strings/ViewStrings.fr.resx` | 3 keys (fr) |
| `tests/Pia.Wpf.Tests/Services/AgentPlannerTests.cs` | ctor fix + new tests T1–T16 |
| `tests/Pia.Wpf.Tests/Unit/Providers/AiProviderHandlerReasoningEffortFlagTests.cs` | **new file** (CRLF) — T17–T19 |

Do **not** hand-edit `ViewStrings.Designer.cs` (it has drifted; `loc:Str` resolves via
`ResourceManager.GetString` at runtime).

---

## 4. `IAiProviderHandler` — the new member

```csharp
    /// <summary>
    /// True when this handler's request shape drops the configured reasoning effort as soon as tools are
    /// attached — i.e. a tool-using turn always reasons at the provider's DEFAULT effort no matter what
    /// <see cref="AiProvider.ReasoningEffort"/> says. <c>AgentPlanner</c> reads this to decide whether a
    /// plan turn is worth splitting into a free-form reasoning turn (tool-free, so the effort survives)
    /// followed by the constrained <c>emit_plan</c> turn.
    /// <para>
    /// The knowledge lives next to the handler that HAS it: whether effort survives tools is decided by the
    /// exact request this handler builds in <see cref="CreateChatOptions"/> (or by the DelegatingHandler it
    /// installs in <see cref="CreateChatClientAsync"/>), so a future handler cannot silently inherit a wrong
    /// answer from a ProviderType switch living somewhere else.
    /// </para>
    /// <para>
    /// MUST be implemented as an expression-bodied constant (<c>=&gt; true;</c> / <c>=&gt; false;</c>) and never
    /// as an initialised auto-property: it is a transport constant, and the conformance test reads it off an
    /// instance created without running any constructor.
    /// </para>
    /// </summary>
    bool DropsReasoningEffortWithTools { get; }
```

Values, each with a one-line reason next to it (place the member directly under `ProviderType`):

| Handler | Value | Comment to write |
|---|---|---|
| `OpenAiProviderHandler` | `=> false;` | Responses API: `ToOpenAiResponses` has no tool gate, so effort already survives tools. |
| `AzureOpenAiProviderHandler` | `=> true;` | `ToOpenAi(effort, hasTools)` omits the param when tools are present. |
| `OllamaProviderHandler` | `=> true;` | same `ToOpenAi` tool gate. |
| `MistralProviderHandler` | `=> true;` | `ShouldEmitReasoning` returns `(false, default)` for a non-None effort once `hasTools` — plus the D7 note: the flag is transport-level and cannot know whether `ModelName` is in `ReasoningCapableModels`, so a non-reasoning model spends one extra turn at default effort; accepted. |
| `OpenRouterProviderHandler` | `=> false;` | `OpenRouterReasoningHandler` rewrites the body to `reasoning:{effort}` unconditionally, tool-independent. |
| `VLlmProviderHandler` | `=> false;` | `VLlmThinkingHandler` sets `chat_template_kwargs.enable_thinking` unconditionally (boolean only, no granularity). |
| `OpenAiCompatibleProviderHandler` | `=> false;` | never sends any reasoning field, with or without tools — nothing for a second turn to recover. |
| `PiaCloudProviderHandler` | `=> false;` | same: `CreateChatOptions` never sets an effort. |

---

## 5. `AppSettings`

Directly under the `AgentWallClockMinutes` line (`AppSettings.cs:172`), as its own commented block:

```csharp
    // Batch 05 — reason-then-emit planning. When true, a plan turn on a provider whose handler DROPS the
    // configured reasoning effort as soon as tools are attached (AzureOpenAI / Ollama / Mistral, see
    // IAiProviderHandler.DropsReasoningEffortWithTools) is split into TWO provider turns: a tool-FREE
    // free-form reasoning turn at the configured effort, then the constrained emit_plan turn seeded with
    // that analysis. Default OFF: it doubles the plan-turn cost, and the plan turn already costs >=2 rounds
    // (§16 R6). Global, not per-provider — the same answer applies to interactive, detached and scheduled runs.
    public bool AgentPlanReasoningTurnEnabled { get; set; } = false;
```

---

## 6. `AgentPlanner` — the shape to write

Add to the using block: `using Pia.Services.Providers;`. `ReasoningEffort` resolves unqualified here
(`using Pia.Models;` is present and there is no OpenAI SDK using to clash with, unlike
`MistralProviderHandler`).

### 6.1 Fields, ctor, constant

```csharp
    private readonly IAiClientService _ai;
    private readonly AiProviderHandlerResolver _handlers;
    private readonly ISettingsService _settings;
    private readonly ILogger<AgentPlanner> _logger;

    /// <summary>
    /// Cap on the analysis text folded into the constrained turn. That turn sends exactly two messages and
    /// passes NO contextBudget (there is nothing to compact), so an unbounded analysis block could overflow
    /// a small local model's window and turn a WORKING plan turn into a failing one — the reliability
    /// regression this optimization is forbidden to cause.
    /// </summary>
    private const int MaxAnalysisChars = 4000;

    public AgentPlanner(
        IAiClientService ai,
        AiProviderHandlerResolver handlers,
        ISettingsService settings,
        ILogger<AgentPlanner> logger)
    { … }
```

Extend the class-level `<summary>` with a sentence on the optional reasoning turn and on I1 covering it.

### 6.2 `PlanAsync`

```csharp
    public async Task<PlanResult> PlanAsync(string goal, RunContext ctx, Persona persona, AiProvider provider, CancellationToken ct)
    {
        // Batch 05: optional free-form reasoning turn BEFORE the constrained one. It sends tools: null, so
        // AiClientService computes hasTools:false (SupportsToolCalling && tools is {Count:>0}) and the
        // handler sends the configured reasoning effort — on the three handlers that drop effort under tools
        // this is the ONLY way a plan turn reasons at anything but the model default. Its tokens are part of
        // the plan's cost, so they are summed in on every path below (I1).
        var (analysis, usage) = await TryReasonAsync(goal, persona, provider, ct).ConfigureAwait(false);

        var (steps, planUsage) = await TryCaptureAsync(BuildPlanMessages(goal, persona, firm: false, analysis), provider, ct).ConfigureAwait(false);
        usage = AgentTurnUsage.Sum(usage, planUsage);

        if (steps is null)
        {
            // The firm retry REUSES the one analysis (D10): the retry exists because the model wrote prose
            // instead of calling emit_plan, which a second reasoning turn would not fix and would pay for.
            var (retried, retryUsage) = await TryCaptureAsync(BuildPlanMessages(goal, persona, firm: true, analysis), provider, ct).ConfigureAwait(false); // R10 retry once
            steps = retried;
            usage = AgentTurnUsage.Sum(usage, retryUsage); // I1: the retry's rounds were paid for too
        }

        if (steps is null || !ValidatePlan(steps, ctx.MaxSteps))
        {
            _logger.LogInformation("Planner degrade → SingleTurn fallback (no valid emit_plan).");
            return PlanResult.Fallback with { Usage = usage }; // still accrue the tokens spent
        }
        return new PlanResult(BuildSteps(steps), false, usage);
    }
```

Everything after the first two statements is today's control flow, unchanged.

### 6.3 `TryReasonAsync`

```csharp
    /// <summary>
    /// The optional first turn (Batch 05): a tool-FREE, free-form "think about how to decompose this goal"
    /// round whose text seeds the constrained turn. Returns (null, usage) — never throws for a provider
    /// problem — so planning degrades to today's single constrained turn. The usage is returned even when
    /// the text is discarded: the round was paid for (I1). Caller cancellation is NOT a degrade.
    /// </summary>
    private async Task<(string? Analysis, UsageDetails? Usage)> TryReasonAsync(
        string goal, Persona persona, AiProvider provider, CancellationToken ct)
    {
        if (!await ShouldReasonFirstAsync(provider, ct).ConfigureAwait(false))
            return (null, null);

        // Cost-aware (guardrail): metadata only — provider TYPE, never the name, never the plan text.
        _logger.LogInformation(
            "Plan reason-then-emit is ON for {ProviderType}: this plan spends TWO provider turns "
            + "(free-form reasoning + constrained emit_plan), so the plan-turn cost is doubled.",
            provider.ProviderType);

        try
        {
            var response = await _ai.GetChatResponseAsync(
                BuildReasoningMessages(goal, persona), provider, tools: null, mode: null, ct).ConfigureAwait(false);

            var usage = response.Usage;          // paid for regardless of what came back
            var text = response.Text?.Trim();
            if (string.IsNullOrEmpty(text))
            {
                _logger.LogInformation("Plan reasoning turn produced no text for {ProviderType}; using the single constrained turn.", provider.ProviderType);
                return (null, usage);
            }

            if (text.Length > MaxAnalysisChars)
            {
                _logger.LogDebug("Plan reasoning analysis truncated: {Chars} → {Cap} chars.", text.Length, MaxAnalysisChars);
                text = text[..MaxAnalysisChars] + "\n… (analysis truncated)";
            }

            _logger.SensitiveDebug("Plan reasoning analysis ({Chars} chars): {Analysis}", text.Length, text);
            return (text, usage);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // cancellation is not a degrade
        }
        catch (Exception ex)
        {
            // The OPTIONAL turn must never be able to hard-fail planning. Warning carries the exception
            // TYPE only: LlmTimeoutException's message embeds the provider NAME, which is user-named and
            // therefore sensitive — the detail goes to SensitiveDebug.
            _logger.LogWarning("Plan reasoning turn failed ({Error}) for {ProviderType}; using the single constrained turn.",
                ex.GetType().Name, provider.ProviderType);
            _logger.SensitiveDebug("Plan reasoning turn failure detail: {Detail}", ex.ToString());
            ct.ThrowIfCancellationRequested(); // a cancel that surfaced as a provider-shaped error is still a cancel
            return (null, null);               // the throw lost the usage; there is nothing to accrue
        }
    }
```

`response` is dereferenced only after the gate has passed, and both production implementations
(`AiClientService.GetChatResponseAsync`, `TokenizingAiClientService.GetChatResponseAsync`) always return a
non-null `ChatResponse`. An unconfigured NSubstitute fake returns a completed task wrapping **null** (the same
auto-value trap as `AppSettings`), but the only tests that leave it unconfigured are the gate-OFF ones, which
assert `DidNotReceive` and never reach the dereference. Either way an NRE here is caught by the existing
`catch (Exception ex)` → degrade, so no null-check is needed.

### 6.4 `ShouldReasonFirstAsync`

```csharp
    /// <summary>
    /// D6 gate, cheapest test first. SupportsToolCalling is in here because when it is false the constrained
    /// turn already gets hasTools:false (so the effort IS being sent) and emit_plan is never attached, so
    /// planning is heading for the SingleTurn degrade regardless — a reasoning turn buys nothing there.
    /// </summary>
    private async Task<bool> ShouldReasonFirstAsync(AiProvider provider, CancellationToken ct)
    {
        try
        {
            var settings = await _settings.GetSettingsAsync().ConfigureAwait(false);
            if (!settings.AgentPlanReasoningTurnEnabled) return false;
            if (!provider.SupportsToolCalling) return false;
            if (provider.ReasoningEffort is null or ReasoningEffort.None) return false;
            return _handlers.Get(provider.ProviderType).DropsReasoningEffortWithTools;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Evaluating the GATE of an optional optimization must never be able to fail planning:
            // GetSettingsAsync does I/O and AiProviderHandlerResolver.Get throws NotSupportedException for
            // an unregistered provider type. Either way the answer is "don't spend the extra turn".
            _logger.LogWarning("Plan reasoning-turn gate could not be evaluated ({Error}); planning single-turn.", ex.GetType().Name);
            return false;
        }
    }
```

### 6.5 `BuildReasoningMessages` — free-form, and it must NOT mention `emit_plan`

```csharp
    /// <summary>
    /// The reasoning turn's prompt. Deliberately says NOTHING about emit_plan or any output format: this
    /// turn exists to think, and no tool schema is even attached to it. The plan contract is imposed by the
    /// SECOND turn, which is the one that stays constrained and validated.
    /// </summary>
    private static List<ChatMessage> BuildReasoningMessages(string goal, Persona persona)
    {
        var sb = new StringBuilder();
        sb.AppendLine(persona.SystemPrompt);
        sb.AppendLine();
        sb.AppendLine("Before this goal is turned into an execution plan, think it through.");
        sb.AppendLine("Work out what accomplishing it actually requires: the sub-problems, the order they must happen in, what depends on what, what is still unknown, and the concrete deliverables that would show it is done.");
        sb.AppendLine("Answer with your analysis only — no tool calls, no JSON, no numbered final plan. Keep it short: a few paragraphs or bullets.");

        return new List<ChatMessage>
        {
            new(ChatRole.System, sb.ToString()),
            new(ChatRole.User, goal),
        };
    }
```

### 6.6 `BuildPlanMessages` — one new optional parameter

Signature becomes `BuildPlanMessages(string goal, Persona persona, bool firm, string? analysis = null)`.
The `StringBuilder` block (the System prompt) is **untouched**. Only the User message changes:

```csharp
        // The analysis rides on the USER message, never on the System prompt (D5):
        // TokenizingAiClientService.TokenizeMessages rewrites ONLY ChatRole.User text to PII placeholders,
        // and this analysis came back DETOKENIZED from the reasoning turn — in the System prompt it would
        // ship restored PII straight past the tokenizer. Folding it into the single user message (rather
        // than appending a second one) also keeps the request shape exactly [System, User], so no provider
        // meets a shape this path has not sent before.
        var user = analysis is null
            ? goal
            : $"{goal}\n\n--- Your analysis of this goal (use it; do not restate it) ---\n{analysis}\n--- end of analysis ---";

        return new List<ChatMessage>
        {
            new(ChatRole.System, sb.ToString()),
            new(ChatRole.User, user),
        };
```

With `analysis: null` the user text is `goal` **verbatim** — byte-identical to today. T3 pins that.

### 6.7 `ReplanAsync` — D3 comment, no behaviour change

Immediately above the first `TryCaptureAsync` in `ReplanAsync`:

```csharp
        // D3 (Batch 05): PLAN-ONLY. ReplanAsync keeps its SINGLE constrained turn even when the
        // reason-then-emit toggle is on. A replan already carries the completed-step summaries and the
        // failure detail, so it has the context a fresh reasoning turn would have to reconstruct; and it can
        // run up to MaxReplans times per run, so doubling ITS cost multiplies over the run instead of being
        // paid once. Deliberate asymmetry, not an oversight — revisit only with evidence that replans
        // specifically plan worse.
```

---

## 7. Settings UI

### 7.1 `AssistantSettingsViewModel` — exactly four touch points

1. Declare, next to the Agent knobs (`:214-222`):
   ```csharp
   // Batch 05 opt-in: split a plan turn into reason-then-emit on providers that drop reasoning effort when
   // tools are attached. Global (not per-provider), default OFF — it doubles the plan-turn cost.
   [ObservableProperty]
   private bool _agentPlanReasoningTurnEnabled;
   ```
2. Changed handler, in the **`OnSuggestionsEnabledChanged` shape** (`:116-119`) — NOT the clamping slider shape:
   ```csharp
   partial void OnAgentPlanReasoningTurnEnabledChanged(bool value)
   {
       if (!_isLoading) SaveSettingsAsync().SafeFireAndForget(_logger);
   }
   ```
3. Load, after `AgentWallClockMinutes` (`:319`): `AgentPlanReasoningTurnEnabled = settings.AgentPlanReasoningTurnEnabled;` (no clamp — it is a bool).
4. Save, after `settings.AgentWallClockMinutes = …` (`:466`): `settings.AgentPlanReasoningTurnEnabled = AgentPlanReasoningTurnEnabled;`

Explicitly **do not** add: a `…Display` property, a `_localizationService.Format` call, or an
`OnPropertyChanged(nameof(…Display))` line in the post-load refresh block. The sliders have those because
they need a numeric readout; a CheckBox's label is the resx string.

### 7.2 `AssistantView.xaml`

Append **after** the Scheduled `MaxReplans` `StackPanel` (currently ends at `:449`) and **before** the
closing `</StackPanel>` of the tab's panel, so the two budget envelopes stay adjacent and the new knob reads
as its own subject. Copy the exact shape of the working `Settings_MeetingBrowser_ShowWindow` CheckBox
(`:348-353`) — `IsChecked` on a `CheckBox` is TwoWay by default, so no `Mode=TwoWay`:

```xml
            <!-- Planning (Batch 05): global, applies to interactive AND unattended runs. -->
            <TextBlock Text="{loc:Str Settings_Agent_Planning_Section_Header}"
                       Style="{StaticResource PiaSettingsSectionLabelStyle}"
                       Margin="0,12,0,0"/>
            <StackPanel Margin="0,0,0,20">
              <CheckBox Content="{loc:Str Settings_Agent_PlanReasoningTurn}"
                        IsChecked="{Binding AgentPlanReasoningTurnEnabled}"
                        Margin="0,8,0,0"/>
              <TextBlock Text="{loc:Str Settings_Agent_PlanReasoningTurn_Description}"
                         Style="{StaticResource PiaSettingsDescriptionStyle}"
                         TextWrapping="Wrap"
                         Margin="22,4,0,0"/>
            </StackPanel>
```

`PiaSettingsSectionLabelStyle` and `PiaSettingsDescriptionStyle` are both already used in this file, so both
resolve.

### 7.3 resx — three keys, all three files

Insert after the `Settings_Agent_MaxReplans_Description` entry in each file
(en `:925`, de `:111`, fr `:111`) so the block stays contiguous.

`ViewStrings.resx` (en):
```xml
  <data name="Settings_Agent_Planning_Section_Header" xml:space="preserve"><value>Planning</value></data>
  <data name="Settings_Agent_PlanReasoningTurn" xml:space="preserve"><value>Think before planning</value></data>
  <data name="Settings_Agent_PlanReasoningTurn_Description" xml:space="preserve"><value>Adds a short tool-free thinking turn before the plan is created, so the model can reason at the effort you configured. Helps on providers that switch reasoning off while tools are attached — and doubles the cost of the planning turn.</value></data>
```

`ViewStrings.de.resx`:
```xml
  <data name="Settings_Agent_Planning_Section_Header" xml:space="preserve"><value>Planung</value></data>
  <data name="Settings_Agent_PlanReasoningTurn" xml:space="preserve"><value>Vor der Planung nachdenken</value></data>
  <data name="Settings_Agent_PlanReasoningTurn_Description" xml:space="preserve"><value>Fügt vor dem Erstellen des Plans einen kurzen werkzeugfreien Denkschritt hinzu, damit das Modell mit der von dir eingestellten Denkstufe arbeiten kann. Hilft bei Anbietern, die das Nachdenken abschalten, solange Werkzeuge aktiv sind – und verdoppelt die Kosten des Planungsschritts.</value></data>
```

`ViewStrings.fr.resx`:
```xml
  <data name="Settings_Agent_Planning_Section_Header" xml:space="preserve"><value>Planification</value></data>
  <data name="Settings_Agent_PlanReasoningTurn" xml:space="preserve"><value>Réfléchir avant de planifier</value></data>
  <data name="Settings_Agent_PlanReasoningTurn_Description" xml:space="preserve"><value>Ajoute un court tour de réflexion sans outils avant la création du plan, afin que le modèle puisse raisonner au niveau d'effort que vous avez configuré. Utile chez les fournisseurs qui désactivent le raisonnement lorsque des outils sont attachés — et double le coût de l'étape de planification.</value></data>
```

No `&`, `<` or `>` in any value, so no XML escaping is needed. `–`, `—` and `'` are fine in UTF-8 resx.

---

## 8. Test plan

### 8.1 `tests/Pia.Wpf.Tests/Services/AgentPlannerTests.cs` (extend — reuses `PlanStream`/`ReturnsPlan`)

Helper changes first:

```csharp
    private readonly ISettingsService _settingsService = Substitute.For<ISettingsService>();

    // A REAL AppSettings: NSubstitute's auto-value for Task<AppSettings> is a completed task wrapping NULL
    // (AppSettings is a plain class, not substitutable), which would NRE inside the gate — the same trap
    // ScheduledJobBackgroundServiceTests:34-37 documents.
    private readonly AppSettings _appSettings = new();

    private AgentPlanner BuildPlanner(
        AiProviderType handlerType = AiProviderType.OpenAI, bool dropsEffortWithTools = false)
    {
        _settingsService.GetSettingsAsync().Returns(_ => Task.FromResult(_appSettings));
        var handler = Substitute.For<IAiProviderHandler>();
        handler.ProviderType.Returns(handlerType);
        handler.DropsReasoningEffortWithTools.Returns(dropsEffortWithTools);
        return new(_ai, new AiProviderHandlerResolver([handler]), _settingsService, NullLogger<AgentPlanner>.Instance);
    }
```

`Provider()` gains defaulted parameters (existing call sites keep working):
`Provider(AiProviderType type = AiProviderType.OpenAI, ReasoningEffort? effort = null, bool supportsTools = true)`.
Note `AiProvider.SupportsToolCalling` already defaults to true (R11).

Two new capture helpers:

```csharp
    private readonly List<string> _userPrompts = new();   // fill inside ReturnsPlan: ci.ArgAt<IList<ChatMessage>>(0)[1].Text
    private string LastUserPrompt => _userPrompts[^1];

    private readonly List<IList<ChatMessage>> _reasoningRequests = new();
    private bool _reasoningSawTools = true;   // must end up false — that is WHY the effort survives
    private bool _reasoningToolsCaptured;

    private void ReturnsReasoning(string? text, UsageDetails? usage = null) { /* stub GetChatResponseAsync */ }
    private void ThrowsFromReasoning(Exception ex) { /* stub GetChatResponseAsync to throw */ }
```

`ReturnsReasoning` must stub with a `Returns(ci => …)` lambda that **captures** its arguments before building
the response — `_reasoningRequests.Add(ci.ArgAt<IList<ChatMessage>>(0))` and
`_reasoningSawTools = ci.ArgAt<IList<AITool>?>(2) is not null; _reasoningToolsCaptured = true;`. T2 then
asserts on the captured values. Do **not** express T2 as
`Received(1).GetChatResponseAsync(Arg.Any<…>(), Arg.Any<…>(), null, …)`: NSubstitute argument matching on a
literal `null` is easy to get subtly wrong, and a capture also proves the call actually happened
(`_reasoningToolsCaptured`) rather than matching vacuously.

`ReturnsReasoning` returns a `ChatResponse` carrying one `ChatRole.Assistant` message plus `Usage` —
**confirm the exact `ChatResponse` constructor overload / settable `Usage` at implementation time** (R9
confirms `.Text` and `.Usage` exist and are used in production code).

All existing tests keep passing unchanged: the toggle defaults to false, so `GetChatResponseAsync` is never
reached on the default path.

| # | Test | File | Asserts |
|---|---|---|---|
| T1 | `PlanAsync_GateOn_AffectedHandler_ReasonsThenEmits` | AgentPlannerTests | toggle on, handler `dropsEffortWithTools: true`, provider `Ollama` + `ReasoningEffort.High`: `_ai.Received(1).GetChatResponseAsync(...)` **and** `Received(1).GetChatCompletionWithToolsAsync(...)`; plan still valid with the same ordered steps; `LastUserPrompt` contains both the goal and the analysis text |
| T2 | `PlanAsync_ReasoningTurn_SendsNoTools_SoTheEffortSurvives` | AgentPlannerTests | the captured `tools` argument of `GetChatResponseAsync` is **null** (this is the premise from R1 that makes the whole batch work), and the reasoning request's System prompt does **not** contain `"emit_plan"` |
| T3 | `PlanAsync_GateOff_RunsOneTurn_AndTheUserMessageIsTheGoalVerbatim` | AgentPlannerTests | toggle off but handler `dropsEffortWithTools: true`: `DidNotReceive().GetChatResponseAsync(...)`; `Assert.Equal("goal", LastUserPrompt)` — the executable form of "no regression when OFF" |
| T4 | `PlanAsync_GateOn_UnaffectedHandler_RunsOneTurn` | AgentPlannerTests | toggle on, `AiProviderType.OpenAI` + `dropsEffortWithTools: false` (the Responses-API case) + effort High: `DidNotReceive().GetChatResponseAsync(...)`; one constrained turn; user message == goal |
| T5 | `PlanAsync_GateOn_EffortNullOrNone_SkipsTheReasoningTurn` (`[Theory]`, `null` / `ReasoningEffort.None`) | AgentPlannerTests | `DidNotReceive().GetChatResponseAsync(...)` — nothing to boost |
| T6 | `PlanAsync_GateOn_ProviderWithoutToolCalling_SkipsTheReasoningTurn` | AgentPlannerTests | `SupportsToolCalling = false`: `DidNotReceive().GetChatResponseAsync(...)` |
| T7 | `PlanAsync_ReasoningTurnThrows_StillProducesAValidPlan` | AgentPlannerTests | reasoning throws `LlmTimeoutException("P", 300)`: plan is valid, `FallBackToSingleTurn` false, exactly one constrained turn, `LastUserPrompt == "goal"` |
| T8 | `PlanAsync_ReasoningTurnEmpty_StillProducesAValidPlan` | AgentPlannerTests | reasoning returns `"   "`: same as T7 |
| T9 | `PlanAsync_ReasoningTurnEmpty_StillAccruesItsUsage` | AgentPlannerTests | reasoning `{In=3,Out=1}` + empty text, plan turn `{In=7,Out=3}` → `Usage` is `{10,4}`. Separate from T8 on purpose: this is the accrual case most easily implemented wrong (returning `(null, null)`) |
| T10 | `PlanAsync_SumsUsageFromBothTurns` | AgentPlannerTests | happy path: reasoning `{3,1}` + plan `{7,3}` → `{10,4}` |
| T11 | `PlanAsync_ReasoningUsage_ReachesTheSingleTurnDegradeResult` | AgentPlannerTests | reasoning `{3,1}` + no `emit_plan` on either constrained attempt (each `{7,3}`) → `FallBackToSingleTurn` true and `Usage` `{17,7}`; `Assert.Null(PlanResult.Fallback.Usage)` (the shared instance is never mutated) |
| T12 | `PlanAsync_FirmRetry_ReusesTheSingleReasoningTurn` | AgentPlannerTests | no `emit_plan` on either attempt: `GetChatResponseAsync` `Received(1)` while `GetChatCompletionWithToolsAsync` `Received(2)` (D10) |
| T13 | `PlanAsync_CancellationDuringTheReasoningTurn_Rethrows` | AgentPlannerTests | pre-cancelled CTS + reasoning throwing `OperationCanceledException`: `await Assert.ThrowsAnyAsync<OperationCanceledException>(...)` and `DidNotReceive().GetChatCompletionWithToolsAsync(...)` — a cancel is not a degrade |
| T14 | `PlanAsync_LongAnalysis_IsTruncatedIntoThePlanTurn` | AgentPlannerTests | reasoning returns 10 000 chars: `LastUserPrompt` contains the truncation marker and its length is well under 10 000 (D8) |
| T15 | `ReplanAsync_GateOn_StillRunsOneConstrainedTurn` | AgentPlannerTests | toggle on + `dropsEffortWithTools: true` + effort High: `DidNotReceive().GetChatResponseAsync(...)` — pins D3 so a later "symmetry" edit has to argue with a test |
| T16 | `AgentPlanReasoningTurn_DefaultsOff` | AgentPlannerTests | `Assert.False(new AppSettings().AgentPlanReasoningTurnEnabled)` — D1's default is a decision, not an accident |

### 8.2 `tests/Pia.Wpf.Tests/Unit/Providers/AiProviderHandlerReasoningEffortFlagTests.cs` — NEW FILE (CRLF)

Namespace `Pia.Wpf.Tests.Unit.Providers` (matches `AiProviderHandlerResolverTests`).

| # | Test | Asserts |
|---|---|---|
| T17 | `EveryHandler_DeclaresDropsReasoningEffortWithTools_WithTheExpectedValue` | reflect every non-abstract class in the Pia assembly assignable to `IAiProviderHandler`; for each, create the instance with `RuntimeHelpers.GetUninitializedObject(type)` and read `DropsReasoningEffortWithTools`; assert the expected map contains the type **and** the value matches; assert `expected.Count == discovered.Count` so a NEW handler fails until it is entered here. **Key the expected map on the CLR `Type`** (`typeof(OllamaProviderHandler)` …), not on `AiProviderType` — a future handler that duplicated an existing `ProviderType` would otherwise satisfy the count assertion while going unchecked. Read `ProviderType` only to build the failure message. Expected: `OpenAiProviderHandler` `false`, `AzureOpenAiProviderHandler` `true`, `OllamaProviderHandler` `true`, `MistralProviderHandler` `true`, `OpenRouterProviderHandler` `false`, `OpenAiCompatibleProviderHandler` `false`, `VLlmProviderHandler` `false`, `PiaCloudProviderHandler` `false` |
| T18 | `ReasoningEffortMapping_ToOpenAi_DropsEffortWithTools_ButSendsItWithout` | `ToOpenAi(ReasoningEffort.High, hasTools: true)` is **null** and `hasTools: false` is **not null**; and `ToOpenAiResponses(ReasoningEffort.High)` is **not null** (no tool gate). Pins the premise the flag values rest on (R2) |
| T19 | `MistralShouldEmitReasoning_SuppressesOnUnderTools_ButHighWithout` | for a `ReasoningCapableModels` model with `ReasoningEffort.High`: `hasTools: true` → `emit == false`; `hasTools: false` → `emit == true`. Pins R3 |

Notes the implementing agent must honour in this file:

- `GetUninitializedObject` runs **no constructor**, which is why §4 requires expression-bodied
  implementations: `public bool X { get; } = true;` compiles its initialiser into the ctor and would read
  back `false` for every handler — a silently vacuous test. It also lets `PiaCloudProviderHandler` (three ctor
  deps) be inspected without a container. If a future handler makes the flag depend on ctor state this test
  throws — **that is the intended signal**, since the flag is a transport constant.
- T18/T19 touch the OpenAI SDK's experimental enums, so they need
  `#pragma warning disable OPENAI001` / `restore` around the region (the repo pattern; the 0-added-warnings
  bar requires it). `ExperimentalApiContainmentTests` polices **MAAI001 only** (R15), so this is not a
  containment violation — but do **not** add `OPENAI001` to any csproj `<NoWarn>`.
- Both `ReasoningEffortMapping` and `MistralProviderHandler.ShouldEmitReasoning` are `internal`, reachable via
  the existing `InternalsVisibleTo` (R14).

---

## 9. Manual-smoke debt (no automated coverage exists)

1. **The two XAML `Binding` paths.** `AgentPlanReasoningTurnEnabled` on the CheckBox is resolved at runtime
   only; a typo fails silently (checkbox renders, never persists). No test parses a View.
   `LocalizationTests` *does* cover the three `loc:Str` keys and their en/de/fr parity (§0.3), so those are
   not on this list. **Check:** open Settings → Assistant → Agent runs, confirm the "Planning" header and the
   checkbox render, toggle it, restart the app, confirm the state persisted.
2. **The VM load/save wiring.** No `AssistantSettingsViewModel` test exists and its four concrete sub-VM
   dependencies make one disproportionate for a checkbox (R16); step 1's restart check is the coverage.
3. **A real two-call plan** against an Ollama or Azure OpenAI provider with `ReasoningEffort` set. Run the
   *same goal twice* — once with the toggle OFF, once ON — and confirm: the `Information` line naming the
   doubled cost appears exactly once in the ON run and not at all in the OFF run; the ON run's plan-phase
   token spend in the run ledger is visibly larger than the OFF run's; and the plan still validates (the run
   goes `Planned`, not SingleTurn-degraded) in both.
4. **DE/FR** strings render without clipping in the settings pane.

---

## 10. Invariants this batch must not break

- **I1 (usage accrual).** Every provider round the planner spends reaches `PlanResult.Usage` — including the
  reasoning turn, including when its text is discarded, including both degrade paths. T9/T10/T11.
- **R6/R10 reliability.** The constrained `emit_plan` turn, `ValidatePlan`, the single firm retry and the
  SingleTurn degrade are behaviourally unchanged with the toggle OFF (T3) and are the fallback whenever the
  reasoning turn fails or returns nothing (T7/T8).
- **Cancellation.** An `OperationCanceledException` while `ct.IsCancellationRequested` propagates; it is never
  swallowed into a degrade. T13.
- **Privacy.** Plan text, replan text, goal text and the analysis go only through `SensitiveDebug`. New log
  lines identify a provider by `provider.ProviderType`, never `provider.Name`; the degrade warning logs
  `ex.GetType().Name` rather than the exception, because `LlmTimeoutException`'s message embeds the provider
  name (R10). Pre-existing `provider.Name` log lines in `AiClientService` are left alone.
- **Zero added warnings**, `--no-incremental` build, and `failed: 0`.

---

## 10a. As built (deviations recorded at implementation time)

1. **`ReasoningEffort` does NOT resolve unqualified in `AgentPlanner`** (§6 claimed it does). `using
   Microsoft.Extensions.AI;` brings its own `ReasoningEffort` into scope, so the gate reads
   `Pia.Models.ReasoningEffort.None` fully qualified — the same collision `MistralProviderHandler` already
   works around. `AgentPlannerTests` hits it too and takes a `using ReasoningEffort = Pia.Models.ReasoningEffort;`
   alias so `[InlineData(ReasoningEffort.None)]` stays readable.
2. **`LlmTimeoutException` lives in `namespace Pia.Services.Exceptions`**, not `Pia.Services` as R10's path
   implied; the test file takes that using.
3. **T16 moved out of `AgentPlannerTests`** into a new `tests/Pia.Wpf.Tests/Models/AppSettingsAgentPlanningTests.cs`
   (namespace `Pia.Tests.Models`, following `AppSettingsMeetingBrowserTests`), so the settings commit ships
   with its own test instead of the assertion landing in the planner commit. It gained a **camelCase JSON
   round-trip test** — the only automated proof that toggling the CheckBox can actually persist, since §9.2
   rules out an `AssistantSettingsViewModel` unit test.
4. **Two gate-robustness tests added** beyond T1–T19: `PlanAsync_GateOn_UnregisteredProviderType_StillPlans`
   and `PlanAsync_GateOn_SettingsUnavailable_StillPlans`. §6.4's catch-all names exactly these two hazards
   (`AiProviderHandlerResolver.Get` throwing, `GetSettingsAsync` doing I/O) and nothing else covered either.
5. **`AgentPlannerTests` couples the handler type to the provider type** in one `PlannerFor(...)` helper rather
   than taking them as independent parameters. A mismatch would make `Get` throw, the gate swallow it, and
   every gate assertion pass for the wrong reason.
6. **`AssertReasoningTurns`/`AssertConstrainedTurns` use `Received(count)` with `count: 0` for
   `DidNotReceive`** and discard the returned task with `_ =` rather than awaiting it, so no assertion can
   depend on NSubstitute's auto-value for `Task<ChatResponse>` in the received-calls route.
7. **Neutralization proof (each restored before committing).** Handler flag: `OllamaProviderHandler` flipped to
   `=> false;` → 1 red; rewritten as `{ get; } = true;` → 1 red (the `GetUninitializedObject` trap §4 warns
   about). Planner: gate forced false → **8** red, and only the reason-then-emit ones; discarded-turn usage
   returned as `(null, null)` → `…_StillAccruesItsUsage` red; **both** cancellation guards removed → the
   cancellation test red (removing only the dedicated `catch` does **not** red it, because
   `ct.ThrowIfCancellationRequested()` in the general catch still propagates — the test pins the invariant,
   not the mechanism); truncation disabled → the truncation test red; analysis moved to the System prompt →
   3 red, which is what pins D5's placement. Localization: the `de` header key deleted →
   `AllTranslations_MustBeComplete` red naming that exact key.
   Two anti-vacuousness checks on the new assertion helpers themselves: `AssertReasoningTurns(1)` inflated to
   `(2)` reds with NSubstitute's *"Expected to receive exactly 2 calls … Actually received 1 matching call"*,
   so the `_ =`-discarded `Received(count)` really does assert and every `AssertReasoningTurns(0)` is
   load-bearing; and the gate's catch-all rewritten to `throw;` reds **both** robustness tests, so the
   settings-unavailable one genuinely reaches the catch rather than passing via an ordinary gate-false path.
   `SensitiveDebug` was confirmed to be the `[Conditional("DEBUG")]` helper in `Pia.Logging/SafeLog.cs`, so
   the analysis text and the failure detail are erased from release IL along with their arguments.
8. **Measured gate.** `dotnet build -p:EnableWindowsTargeting=true --no-incremental` → 0 errors, **194**
   warnings (unchanged). Full suite → **2218** total / **0** failed / 1 skipped (baseline 2194/0/1; +24 = 18
   planner cases, 3 handler-flag, 3 settings).

---

## 11. Open questions (none blocking)

1. **`MaxAnalysisChars = 4000` is a judgement call**, not a measurement. If a local 8k-context model still
   overflows, the knob to turn is this constant (and it is the only one).
2. **Efficacy is unmeasured.** Nothing here proves reason-then-emit produces better plans; it proves the
   boosted-effort round happens, degrades safely and is paid for. Judging quality needs the human smoke of
   §9.3 on a weak/local provider.
3. **`ReplanAsync` (D3)** stays single-turn. Revisit only with evidence that replans specifically plan worse.
4. **Mistral non-reasoning models spend one extra turn** at default effort (D7, accepted). Narrowing needs the
   flag to become a method taking `AiProvider`, which was rejected.
