# Resumable interruption for agent runs — implementation plan

**Status:** executable — all four decision gates closed 2026-11-17, not started ·
**Owner:** Marco Altmann ·
**Written:** 2026-11-17
**Origin:** GitHub issue [Pia-Ai-dev/Pia.Wpf#95](https://github.com/Pia-Ai-dev/Pia.Wpf/issues/95), "Agent run
stopped by a credit limit or network failure ends as Failed, and continuing starts over without its finished
work", reviewed against `develop` at `bd454448`.
**Tracking:** [2026-11-17-resumable-interruption-checklist.md](2026-11-17-resumable-interruption-checklist.md).
Tick its boxes in the commit that lands each task. This plan adds no second tracking surface, so its steps
are numbered, not checkboxes.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or
> superpowers:executing-plans to implement this plan task-by-task.

**Goal:** An agent run cut off by the credit limit, a lost connection, a provider timeout or a provider
fault stops resumable instead of ending Failed, and Continue or a typed Agent-mode message picks it up at
the interrupted step in the same workspace.

**Architecture:** Both executors tag the step result with an interruption kind from one shared classifier.
The orchestrator parks on that tag the way it already parks for a tool approval or a user question: the step
goes back to Pending, the run goes to `WaitingForInput` with an app-owned reason token, and nothing is
recorded, replanned or published. Everything after the park (Continue, the workspace, the seeded Done steps)
is the existing resume path.

**Tech stack:** C# on .NET 10 (WPF), xunit v3 run from the built test exe, NSubstitute, SQLite-backed
`AgentRunService`.

**Spec:** the issue above. Section 1 restates its decisions so this file reads cold. Section 2 lists where
this plan departs from it and why.

---

## 1. What the issue decides

Today (all confirmed, see 2.1): a provider call that throws inside a step becomes an ordinary failed step
result. The orchestrator records the step Failed and tries a replan, which is another provider call and
throws too, so the run settles **Failed**. A failed run never publishes its workspace, offers no Continue,
and the next Agent-mode send creates a new run with a new workspace.

The issue's fix, which this plan implements:

1. `StepTurnResult` gains a trailing, defaulted interruption member, set by both executors from one shared
   classifier. Kinds: credit, offline, timeout, provider.
2. The drain loop parks on it before `SafeRecordStep`: step back to Pending, pause with one of four tokens
   (`interrupted-credit`, `interrupted-offline`, `interrupted-timeout`, `interrupted-provider`), release the
   executor. No replan is spent and no grace turn is attempted.
3. A replan call that is interrupted parks the same way. After a step failure the already-recorded Failed
   step goes back to Pending first.
4. The run card and the Flow item say why, in English, German and French.
5. An Agent-mode send while the run is parked on one of the four tokens resumes it with the typed text as
   the note. A Chat-mode send stays an ordinary chat turn.
6. On the live path the interrupted step's error bubble is dropped, so the resumed step does not read it as
   its own earlier answer.

Unchanged and not interruptions: `LlmTruncatedException`, an empty response, the vision-disabled rejection,
a step that declared `succeeded:false`, tool errors, cancellation. A pending tool approval or user question
still wins over an interruption.

Out of scope in the issue, and here: runs that are already Failed, Pia closed mid-run, a failure of the
first plan call, the wording a parent shows when one of its sub-agent child runs was interrupted (F14),
showing the credit reset time on the card.

## 2. Review of the issue

### 2.1 Confirmed against the code

Every "what happens today" claim holds at `bd454448`:

- `HeadlessTurnExecutor.RunExchangeStepAsync` returns a plain failed result from its catch-all
  (`HeadlessTurnExecutor.cs:876`), after the approval and user-question arms (`:850`, `:865`).
- `ChatSession.RunStepTurnAsync` catches timeout, truncation, cancel, vision, offline and everything else
  (`ChatSession.cs:866-910`), writes the localized error into the step's assistant message and returns
  `Succeeded: false`.
- The credit limit is a plain `InvalidOperationException` at `PiaCloudChatClient.cs:698` and
  `AiClientService.cs:180` / `:999`, reached only after `RateLimitRetryHandler` gave up.
- `AgentPlanner.ReplanAsync` does not catch provider faults (`TryCaptureAsync` has no catch around the
  provider call), so an outage during a replan reaches the orchestrator's catch-all
  (`AgentRunOrchestrator.cs:603-608`) and the run is Failed.
- `SettleTerminalAsync` promotes only on the success path (`:862-866`).
- `TryClaimForResumeAsync` claims `WaitingForInput` and `Paused` only (`HeadlessRunLauncher.cs:834-839`).
- `TryAnswerParkedRunAsync` handles `needs-goal` and `needs-input` only (`ChatSessionManager.cs:1101`).
- A resume does not re-plan (`AgentRunOrchestrator.cs:218-229`) and seeds the Done steps (`:947-972`).
- `RecordStepResultAsync` is an `UPDATE` by step id and adds to the per-step ledger entry
  (`AgentRunService.cs:1305-1342`), so a step that was recorded Failed, put back to Pending and run again
  is recorded a second time safely, with both attempts' tokens counted.

### 2.2 Findings

| # | Finding | What this plan does |
|---|---|---|
| F1 | "Any other `HttpRequestException`" is too wide. `PiaCloudChatClient.HandleErrorResponse` throws an `HttpRequestException` carrying the status for every non-2xx except 429 and 401 (`:706`). A deterministic 400, for example a request that is too large, would park on every Continue and never fail or replan. | **Q1, closed:** only status null, 408, 429 or 500 and above parks. |
| F2 | The most likely lost-connection shape is not matched by the issue's table. Measured with a loopback probe on the installed .NET SDK (11.0 RC; the app targets net10, and Task 2's test repeats it there): a streamed response that is reset mid-body surfaces as `IOException` → `SocketException(ConnectionReset)`, and one that ends early as `HttpIOException(ResponseEnded)`. Neither is an `HttpRequestException`, and `NetworkFailure.IsOffline` answers false for both. An agent step spends most of its time reading a stream. | The classifier treats `HttpIOException` and any `SocketException` in the chain as offline. Task 2 pins it with a loopback test. |
| F3 | The issue's table covers Pia Cloud only. The other providers run on two SDKs, and neither raises an `HttpRequestException` for an HTTP error. The OpenAI SDK (OpenAI, Azure OpenAI, Ollama, OpenRouter, OpenAI-compatible, Mistral, vLLM) raises `System.ClientModel.ClientResultException` with a `Status`. The Anthropic SDK (`Anthropic` 12.48.0) raises `AnthropicRateLimitException` for 429, `Anthropic5xxException` for 5xx, other `AnthropicApiException` subclasses by `StatusCode`, `AnthropicSseException` for an error after the stream opened, and `AnthropicIOException` for a network error (SDK documentation; the type names are present in the installed assembly). | **Q2, closed:** every provider gets the coverage Pia Cloud gets. Task 2 adds the rows and proves them against real clients. A 429 from these providers parks as provider, not credit: there it is a rate limit, and "credit limit" is Pia Cloud's wording. |
| F4 | The classifier has to walk the inner chain. `FailureMapper` documents that a refused connection arrives as `AggregateException` → `ClientResultException` → `HttpRequestException` → `SocketException`. The chain order also matters: an `HttpClient` timeout is `TaskCanceledException` → `TimeoutException` → … → `SocketException(OperationAborted)` (measured), so timeout must be tested before offline. | Task 2 reuses `FailureMapper.Unwrap` and fixes the rule order. |
| F5 | The fallback turn and the grace turn share both executor seams (`RunExchangeStepAsync`, `RunStepTurnAsync`). The degrade path has no step row to put back to Pending and no park arm, so a tagged fallback turn would still fail the run, and on live its error text would have been dropped. | Only a real plan step is classified. Headless: `offerStepResultTool`. Live: `!spec.UseGoalVerbatim`. |
| F6 | Neither catch-all checks the run token. A Stop that surfaces as a transport fault would park as "Connection lost" instead of settling Cancelled. | Classify only while the token is not cancelled, in both executors and both replan sites. |
| F7 | `TryReplanAfterFailureAsync` returns `bool` and has two callers. A park is a third outcome (return without the terminal settle), and the fan-out caller (`:412`) has no single step to put back. | A three-valued result. The fan-out caller passes no step and keeps today's behaviour. Sub-agents are out of scope in the issue anyway. |
| F8 | `HeadlessRunLauncher.InterruptedReasonFor` (`:162-168`) re-parks a resume that failed before dispatch as `resume-interrupted`. That would overwrite an interruption token, and the next typed message would start a new run again: the original defect by the back door. | Task 8 preserves the four tokens there. |
| F9 | `TryAnswerParkedRunAsync` must gate only the new tokens on the Agent lever. The existing `needs-goal` / `needs-input` tests send with `planned: false` and have to keep resuming. | Task 9. |
| F10 | Two of the three 429 sites (`AiClientService.cs:180`, `:999`) are the prompt-generation and optimize routes. No agent run reaches them. | Still converted, as the issue asks, so one exception type means "credit limit" everywhere. |
| F11 | Routines. A parked run does not count as executing (`AgentRunStates.IsExecuting`, `AgentEnums.cs:98`), so the next firing of the same job dispatches a new run. During a multi-day outage every firing parks one more run with a workspace and a Flow card, and the five-strike valve never trips. | **Q3, closed:** routines park exactly as interactive runs do. The pile-up is listed as a follow-up. |
| F12 | `SafeVerify` degrades to accept on any fault (`AgentRunOrchestrator.cs:918-933`). An outage that hits only the verify call therefore completes the run unverified. | Unchanged. Noted so nobody expects a park there. |
| F13 | The issue keeps tool errors out, but the tool loop does not catch a throwing handler (`AiClientService.cs:753`), so a tool's own `HttpRequestException` or `SocketException` reaches the same catch as a provider fault. MCP tools return their errors as text (`McpPluginToolHandler.cs:205-210`); a built-in handler that throws would park the run as "Connection lost" on every Continue. | Task 2 marks an exception that leaves a tool handler, and the classifier ignores a marked chain. |
| F14 | Child runs park too. The headless executor tags every real step, so an interrupted delegated child parks, and its parent goes to `children-parked` instead of replanning around a Failed child. The launcher's automatic parent resume (`HeadlessRunLauncher.cs:268-309`) holds while any child is parked, so nothing loops. The issue expects this and leaves only the budget wording open. | Task 3 states it and pins it with one fan-out test. |
| F15 | The comment ratchet has no stated reach. `RunAsync`, `RunExchangeStepAsync`, `RunStepTurnAsync` and `StepTurnResult` carry hundreds of lines of multi-paragraph, history-laden comments. Cleaning each touched member in full would be several times the size of the fix: the ten touched members hold about 540 comment lines, 450 of them in those four. | **Q4, closed:** only the lines edited, plus the summary of a member whose behaviour changes. |

### 2.3 Decision gates

The checklist's gate table holds the four questions and their answers. The owner answered all four on
2026-11-17 and the tasks below are written for those answers:

- **Q1:** an `HttpRequestException` parks only with status null, 408, 429 or 500 and above.
- **Q2:** providers other than Pia Cloud get the same coverage (F3).
- **Q3:** scheduled routines park the same way as interactive runs.
- **Q4:** comment cleanup covers only the lines edited, plus the summary of a member whose behaviour
  changes.

## Global Constraints

- **Branch:** `feature/agent-run-resumable-interruption` from `develop`, created through the `release-flow`
  skill. PR into `develop`, merged with a merge commit. This plan and the checklist are the branch's first
  commit. No release-notes edit in this PR.
- **Test gate:** the built exe, never `dotnet test`, and a redirect, never a pipe:
  `tests/Pia.Wpf.Tests/bin/Debug/net10.0-windows10.0.17763.0/Pia.Wpf.Tests.exe -class "<FullClassName>" > test.log 2>&1`,
  then read `TEST EXECUTION SUMMARY`. One filter kind per run: `-class` plus `-namespace` runs zero tests.
  "Run class X" below means this command after `dotnet build`.
- **Zero warnings:** `dotnet build -t:Rebuild -v:n` and again with `-c Release` must both report
  `0 Warning(s)` and `0 Error(s)`.
- **Reason tokens**, exact: `interrupted-credit`, `interrupted-offline`, `interrupted-timeout`,
  `interrupted-provider`.
- **Contract compatibility:** `StepTurnResult.Interruption` is trailing and defaulted. No existing
  construction of the record changes.
- **Logging:** a new log line carries the run id, the step ordinal where one is in scope, and the reason
  token. Never an exception message: it can contain a URL or a response body. The ordinal is in scope in the
  orchestrator, not in `RunExchangeStepAsync`.
- **Comments:** default to none. One short line when the reason is not visible in the code. No issue
  number, no history. For existing comments (Q4): bring into compliance the comments on the lines you edit
  and the summary of any member whose behaviour you change. Leave the rest of a long member alone, and say
  in the PR description that this was the owner's decision for this change.
- **Server calls:** none is added or changed. The PR description says so.
- **Resource files:** change `.resx` files with the Edit tool only (`sed -i` strips their CRLF), and type
  non-ASCII characters literally.
- **Chat mode is unchanged:** `CreditLimitException` keeps the message and stays an
  `InvalidOperationException`, so every existing catch behaves as before.

## Review Focus

Failure modes the issue implies but does not test. Each has a test in the task that owns the code.

1. **An interruption on the fallback turn** (planner degraded, no step row): the run fails as today and the
   live error bubble stays. Tasks 3 and 6.
2. **Stop pressed while the transport faults:** the run settles Cancelled, it does not park. Tasks 5 and 6.
3. **The replan park:** a step put back to Pending after being recorded Failed is recorded again on the
   resume and ends Done. A fan-out group failure whose replan is interrupted still fails. Task 4.
4. **A resume that fails before dispatch** keeps the interruption token, so a typed message still resumes.
   Task 8.
5. **A deterministic 4xx** (400) fails or replans as today and never parks, on every provider. Tasks 2
   and 5.

## Names (binding)

| Thing | Name | Where |
|---|---|---|
| Kind enum | `public enum StepInterruption { Credit, Offline, Timeout, Provider }` | `src/Pia.Wpf/Services/Interfaces/IAgentTurnExecutor.cs`, namespace `Pia.Services.Interfaces` |
| Result member | `StepInterruption? Interruption = null`, last parameter of `StepTurnResult` | same file |
| Exception | `public sealed class CreditLimitException(string message) : InvalidOperationException(message)` | `src/Pia.Wpf/Services/Exceptions/CreditLimitException.cs`, namespace `Pia.Services.Exceptions` |
| Classifier | `internal static class StepInterruptionClassifier` with `internal static StepInterruption? Classify(Exception ex)` | `src/Pia.Wpf/Services/StepInterruptionClassifier.cs`, namespace `Pia.Services` |
| Tool-fault mark | `internal static bool MarkToolFault(Exception ex)` on the classifier: records the mark in `ex.Data` and always returns false, for use in an exception filter | same file |
| Tokens | `InterruptedCreditReason`, `InterruptedOfflineReason`, `InterruptedTimeoutReason`, `InterruptedProviderReason` (`internal const string`) | `AgentRunOrchestrator`, beside `PlanApprovalReason` |
| Token helpers | `internal static string InterruptionReason(StepInterruption kind)` and `internal static bool IsInterruptionReason(string? reason)` | `AgentRunOrchestrator` |
| Park arm | `private async Task ParkForInterruptionAsync(IAgentTurnExecutor executor, AgentRun run, RunContext ctx, AgentStep step, UsageDetails? usage, StepInterruption kind, Guid? runFirst, Guid runLast, CancellationToken ct)` | `AgentRunOrchestrator`, beside `ParkForUserInputAsync` |
| Replan result | `private enum ReplanOutcome { Revised, Failed, Parked }` | `AgentRunOrchestrator` |
| Card keys | `Run_Activity_InterruptedCredit`, `…Offline`, `…Timeout`, `…Provider` | `ViewStrings.resx`, `.de.resx`, `.fr.resx` |
| Flow keys | `Flow_Run_InterruptedCredit`, `…Offline`, `…Timeout`, `…Provider` | same three files |

The classifier is a static class, so the service-suffix naming rule does not apply to it. After adding the
enum, run class `Pia.Tests.Architecture.NamingConventionTests`.

## File map

| File | Change | Task |
|---|---|---|
| `src/Pia.Wpf/Services/Exceptions/CreditLimitException.cs` | new | 1 |
| `src/Pia.Wpf/Services/PiaCloudChatClient.cs`, `AiClientService.cs` | throw the new type at the three 429 sites | 1 |
| `src/Pia.Wpf/Services/Interfaces/IAgentTurnExecutor.cs` | enum, result member | 2 |
| `src/Pia.Wpf/Services/StepInterruptionClassifier.cs` | new | 2 |
| `src/Pia.Wpf/Services/FailureMapper.cs` | `Unwrap` becomes `internal` | 2 |
| `src/Pia.Wpf/Services/AiClientService.cs` | mark an exception that leaves a tool handler (`:753`) | 2 |
| `src/Pia.Wpf/Services/AgentRunOrchestrator.cs` | tokens, park arm, replan parks | 3, 4 |
| `src/Pia.Wpf/Services/HeadlessTurnExecutor.cs` | set the member | 5 |
| `src/Pia.Wpf/ViewModels/Models/ChatSession.cs` | set the member, drop the bubble | 6 |
| `src/Pia.Wpf/ViewModels/RunProgressViewModel.cs`, `Services/AgentRunNotificationSurface.cs`, three `ViewStrings*.resx` | wording | 7 |
| `src/Pia.Wpf/Services/HeadlessRunLauncher.cs` | keep the token on a failed resume | 8 |
| `src/Pia.Wpf/ViewModels/Models/ChatSessionManager.cs` | typed message resumes | 9 |

---

## Task 1: `CreditLimitException` at the three 429 sites

**Files:**
- Create: `src/Pia.Wpf/Services/Exceptions/CreditLimitException.cs`
- Modify: `src/Pia.Wpf/Services/PiaCloudChatClient.cs:698`, `src/Pia.Wpf/Services/AiClientService.cs:180`, `:999`
- Test: `tests/Pia.Wpf.Tests/Services/PiaCloudChatClientCreditLimitTests.cs` (new, fixture modelled on
  `PiaCloudChatClientStreamErrorTests`), `tests/Pia.Wpf.Tests/Services/AiClientServiceCreditLimitTests.cs`
  (new, construction modelled on `AiClientServiceHttpClientTimeoutTests`)

**Interfaces:**
- Produces: `CreditLimitException` as in Names.

1. Write the failing tests.
   - `A429_ThrowsCreditLimitException_WithTheLocalizedMessage`: the fake handler answers 429 with body `{}`.
     `var ex = await Assert.ThrowsAsync<CreditLimitException>(…)`,
     `Assert.Equal(LocalizationSource.Instance["Msg_CreditLimit_Reached"], ex.Message)`,
     `Assert.IsAssignableFrom<InvalidOperationException>(ex)`.
   - `A429WithAResetTime_KeepsTheResetWording`: body `{"resetsAt":"<UtcNow + 3 h, ISO 8601>"}`. Same type,
     and `Assert.NotEqual(LocalizationSource.Instance["Msg_CreditLimit_Reached"], ex.Message)`.
   - `Optimize_A429_ThrowsCreditLimitException` and `GeneratePrompt_A429_ThrowsCreditLimitException`: the
     `AiClientService` methods that post to `/api/ai/optimize` and `/api/ai/generate-prompt`, same two
     assertions as the first test.
2. Run both classes. Expected: the type does not exist, the build fails.
3. Add the exception type and replace `throw new InvalidOperationException(friendlyMessage)` with
   `throw new CreditLimitException(friendlyMessage)` at the three sites. Nothing else in those blocks moves.
4. Run both classes. Expected: `Failed: 0`.
5. Commit: `fix: raise a typed exception for the Pia Cloud credit limit`.

## Task 2: The interruption contract and the classifier

**Files:**
- Modify: `src/Pia.Wpf/Services/Interfaces/IAgentTurnExecutor.cs` (enum; `StepTurnResult`, `:127-180`)
- Modify: `src/Pia.Wpf/Services/FailureMapper.cs:58` (`private static` → `internal static` on `Unwrap`)
- Modify: `src/Pia.Wpf/Services/AiClientService.cs:753` (the `await toolHandler(toolCall, dispatch)` call in
  `DispatchToolCallsAsync`)
- Create: `src/Pia.Wpf/Services/StepInterruptionClassifier.cs`
- Test: `tests/Pia.Wpf.Tests/Services/StepInterruptionClassifierTests.cs` (new),
  `tests/Pia.Wpf.Tests/Services/ProviderInterruptionShapeTests.cs` (new),
  `tests/Pia.Wpf.Tests/Services/AiClientServiceToolLoopArmTests.cs`

**Interfaces:**
- Consumes: `CreditLimitException` (Task 1), `NetworkFailure.IsOffline`, `FailureMapper.Unwrap(ex, depth: 0)`.
- Produces: `StepInterruption`, `StepTurnResult.Interruption`, `StepInterruptionClassifier.Classify`,
  `StepInterruptionClassifier.MarkToolFault`.

The rules. A chain in which any exception carries the tool-fault mark is never an interruption. Otherwise
the whole chain (`FailureMapper.Unwrap`) is tested against rule 1, then against rule 2, and so on. The first
rule that matches any exception in the chain wins.

The tool loop wraps its handler call in `try { … } catch (Exception ex) when (StepInterruptionClassifier.MarkToolFault(ex)) { throw; }`.
The filter never matches, so the exception leaves exactly as it does today, only marked.

| Order | Kind | Matches |
|---|---|---|
| 1 | Credit | `CreditLimitException` |
| 2 | Timeout | `LlmTimeoutException`; an `OperationCanceledException` whose `InnerException` is a `TimeoutException` |
| 3 | Offline | `NetworkFailure.IsOffline(ex)` on the outermost exception; `HttpIOException`; `SocketException`; `AnthropicIOException` |
| 4 | Provider | An error after the stream opened: `PiaCloudStreamException`; `AnthropicSseException` |
| 4 | Provider | An HTTP status of 408, 429 or ≥ 500: `HttpRequestException.StatusCode` (also when it is null); `ClientResultException.Status`; `AnthropicRateLimitException`; `Anthropic5xxException`; any other `AnthropicApiException` whose `StatusCode` is 408 |
| — | none | everything else, including a 400, 401, 403, 404 or 422 from any provider, `LlmTruncatedException` and a bare `InvalidOperationException` |

The Anthropic types live in `Anthropic.Exceptions`. Their constructors are not documented, so no test builds
one by hand: `ProviderInterruptionShapeTests` gets them from a real client.

`Classify` does not look at cancellation. Its callers do (F6).

1. Write the failing tests in `StepInterruptionClassifierTests`.
   - `Theory` `AnExceptionMapsToItsKind(string shape, StepInterruption? expected)`, with a private factory
     that builds the exception for each `shape`:

     | shape | expected |
     |---|---|
     | `credit` — `new CreditLimitException("x")` | Credit |
     | `credit-wrapped` — `new AggregateException(new CreditLimitException("x"))` | Credit |
     | `llm-timeout` — `new LlmTimeoutException("P", 30)` | Timeout |
     | `httpclient-timeout` — `new TaskCanceledException("t", new TimeoutException("t", new IOException("io", new SocketException((int)SocketError.OperationAborted))))` | Timeout |
     | `refused` — `new HttpRequestException(HttpRequestError.ConnectionError)` | Offline |
     | `stream-reset` — `new IOException("io", new SocketException((int)SocketError.ConnectionReset))` | Offline |
     | `stream-ended` — `new HttpIOException(HttpRequestError.ResponseEnded)` | Offline |
     | `cloud-stream` — `new PiaCloudStreamException("Bad Gateway", null)` | Provider |
     | `http-503` — `new HttpRequestException("m", null, HttpStatusCode.ServiceUnavailable)` | Provider |
     | `http-no-status` — `new HttpRequestException("m")` | Provider |
     | `http-429` — `new HttpRequestException("m", null, HttpStatusCode.TooManyRequests)` | Provider |
     | `http-400` — `new HttpRequestException("m", null, HttpStatusCode.BadRequest)` | null |
     | `http-401` — `new HttpRequestException("m", null, HttpStatusCode.Unauthorized)` | null |
     | `truncated` — `new LlmTruncatedException("P", 10)` | null |
     | `plain` — `new InvalidOperationException("boom")` | null |
     | `cancelled` — `new OperationCanceledException()` | null |

   - `ADroppedStreamingResponse_IsOffline(bool reset)`, `Theory` over `true` / `false`: the real exception
     .NET raises, not a hand-built one. Start the loopback server below, read the response with
     `HttpCompletionOption.ResponseHeadersRead` and drain the body stream until it throws, then
     `Assert.Equal(StepInterruption.Offline, StepInterruptionClassifier.Classify(caught))`.

     ```csharp
     private static async Task<int> ServeTruncatedAsync(bool reset, CancellationToken ct)
     {
         var listener = new TcpListener(IPAddress.Loopback, 0);
         listener.Start();
         var port = ((IPEndPoint)listener.LocalEndpoint).Port;
         _ = Task.Run(async () =>
         {
             using var client = await listener.AcceptTcpClientAsync(ct);
             var stream = client.GetStream();
             await stream.ReadAtLeastAsync(new byte[4096], 1, throwOnEndOfStream: false, ct);
             await stream.WriteAsync(Encoding.ASCII.GetBytes(
                 "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nTransfer-Encoding: chunked\r\n\r\n5\r\nhello\r\n"), ct);
             await Task.Delay(200, ct);
             if (reset) client.Client.LingerState = new LingerOption(true, 0);
             client.Client.Close();
             listener.Stop();
         }, ct);
         return port;
     }
     ```

   - In `ProviderInterruptionShapeTests`, `Theory`
     `ARealProviderFailure_ParksTheSameOnEveryProvider(string family, string failure, StepInterruption? expected)`.
     It proves Q2 with the exceptions the three client families really raise. A loopback server like the
     one above takes a mode: `status-503`, `status-429` and `status-400` answer every connection with that
     status, `Retry-After: 0` and the body `{}`; `dropped-stream` is `ServeTruncatedAsync(reset: true)`.
     Build the `IChatClient` against the loopback URL, call `GetStreamingResponseAsync`, drain it until it
     throws, and classify what was thrown.
     - `pia-cloud`: a `PiaCloudChatClient` constructed as `PiaCloudChatClientStreamErrorTests` constructs it.
     - `openai-sdk`: `new OpenAiCompatibleProviderHandler().CreateChatClientAsync(…)` for an `AiProvider`
       whose `Endpoint` is the loopback URL (the construction `HttpClientDefaultsTests` uses, `:63`).
     - `anthropic-sdk`: `new AnthropicProviderHandler().CreateChatClientAsync(…)`, same way.

     | failure | `pia-cloud` | `openai-sdk` | `anthropic-sdk` |
     |---|---|---|---|
     | `status-503` | Provider | Provider | Provider |
     | `status-429` | Credit | Provider | Provider |
     | `status-400` | null | null | null |
     | `dropped-stream` | Offline | Offline | Offline |

     Both SDKs retry a 429 and a 5xx before they throw, so these cases can take a few seconds each. If a
     family raises a shape the rule table does not list, add the row to the table and to `Classify`: this
     test, not the table, is the definition of "same coverage".
   - `AToolFault_IsNeverAnInterruption`: build the `refused` shape, call `MarkToolFault` on it (assert it
     returns false), then `Assert.Null(Classify(ex))` and `Assert.Null(Classify(new AggregateException(ex)))`.
   - In `AiClientServiceToolLoopArmTests`, `AThrowingToolHandler_LeavesTheLoopMarkedAsAToolFault`: the tool
     handler throws `new HttpRequestException(HttpRequestError.ConnectionError)`. The loop throws that same
     instance, and `StepInterruptionClassifier.Classify` answers null for it.

2. Run class `Pia.Tests.Services.StepInterruptionClassifierTests`. Expected: the build fails, the types do
   not exist.
3. Add the enum and the `Interruption` member, make `Unwrap` internal, implement `Classify` and
   `MarkToolFault` from the rules above, and add the filter around the handler call.
4. Run the class, then `Pia.Tests.Services.ProviderInterruptionShapeTests`,
   `Pia.Tests.Services.AiClientServiceToolLoopArmTests`,
   `Pia.Tests.Architecture.NamingConventionTests` and `Pia.Tests.Services.FailureMapperTests`. Expected:
   `Failed: 0` in each, and the test project builds with `0 Warning(s)` (a plain `Stream.ReadAsync` in the
   loopback helper raises CA2022).
5. Commit: `feat: classify provider interruptions of an agent step`.

## Task 3: The orchestrator parks on an interrupted step

**Files:**
- Modify: `src/Pia.Wpf/Services/AgentRunOrchestrator.cs` — tokens and helpers after `:81`, the new arm
  between the user-question arm (`:507-513`) and `SafeRecordStep` (`:515`), `ParkForInterruptionAsync`
  after `ParkForUserInputAsync` (`:855`)
- Test: `tests/Pia.Wpf.Tests/Services/AgentRunOrchestratorArmTests.cs`

**Interfaces:**
- Consumes: `StepTurnResult.Interruption` (Task 2).
- Produces: the four tokens, `InterruptionReason`, `IsInterruptionReason`, `ParkForInterruptionAsync`.

`ParkForInterruptionAsync` makes four moves, in this order: `ReturnStepToPendingAsync`, one
`LogInformation` with run id, step ordinal and token, `SafePause(run.Id, CancellationToken.None, token)`,
`SafeOnPaused`. No grace turn, no chat post, no `SafeEndRun`.

The arm has no condition on the run's trigger: a scheduled routine parks exactly as an interactive run does
(Q3). `ScheduledJobBackgroundService.BookkeepAgentRunAsync` already books a `WaitingForInput` run as parked,
not failed (`ScheduledJobBackgroundService.cs:709-724`), so the job takes no strike and no code changes there.

Child runs take this arm as well (F14). An interrupted child parks, its parent parks `children-parked`, and
the launcher does not resume the parent while a child is parked.

Test doubles: `StubExecutor` gains `StepInterruption? Interruption`, `int? InterruptAtStepTurn` (null means
every turn) and `StepInterruption? FallbackInterruption`. An interrupted turn returns
`Succeeded: false, Error: "transport"`, empty message ids and the kind. `StubPlanner` gains `PlanCalls` and
`ReplanCalls` counters.

1. Write the failing tests.
   - `Theory` `AnInterruptedStep_ParksWithItsToken_AndKeepsTheStepPending(StepInterruption kind, string token)`
     over the four pairs, plan `A`: state `WaitingForInput`; `RunPauseEnvelope.ReadReason` equals `token`;
     `NextPendingStepAsync` returns step `A`; no step has `Status == Failed`; `planner.ReplanCalls == 0`;
     `executor.GraceTurns == 0`; `executor.EndedRun` is false; `CompletedAt` is null.
   - `AnInterruptionAtTheThirdStep_KeepsTheFirstTwoDone`: plan `A`, `B`, `C`, `InterruptAtStepTurn = 3`.
     `A` and `B` are Done, `C` is Pending.
   - `AResumeAfterAnInterruption_RunsOnlyThePendingStep_WithoutRePlanning`: continue the previous run with a
     fresh `StubExecutor`, `resume: true`, `parkReason: AgentRunOrchestrator.InterruptedOfflineReason`.
     State `Completed`; the fresh executor's `StepTurns == 1`; `planner.PlanCalls == 1`;
     `planner.ReplanCalls == 0`; every step Done.
   - `AUserPauseRequest_OutranksAnInterruption`: steering answers `true`, the executor interrupts. State
     `Paused`.
   - `AnInterruptedFallbackTurn_StillFailsTheRun`: `StubPlanner(PlanResult.Fallback)`,
     `FallbackInterruption = StepInterruption.Offline`. State `Failed`.
   - `Theory` `IsInterruptionReason_KnowsExactlyTheFourTokens`: true for the four, false for `step-cap`,
     `needs-input`, `resume-interrupted` and null.
   - In `AgentRunOrchestratorFanOutTests`, `AnInterruptedChild_ParksTheParentBehindIt_AndDoesNotFailIt`: a
     group whose child's step is interrupted. The child is `WaitingForInput` with the interruption token;
     the parent is `WaitingForInput` with `children-parked`; the parent's planner saw no replan call.
2. Run classes `Pia.Tests.Services.AgentRunOrchestratorArmTests` and
   `Pia.Tests.Services.AgentRunOrchestratorFanOutTests`. Expected: the park, resume and child tests fail,
   because the interrupted step is recorded Failed. The fallback-turn test and the pause test are guards
   and pass before and after.
3. Implement the tokens, the helpers, the arm and `ParkForInterruptionAsync`.
4. Run the class. Expected: `Failed: 0`.
5. Commit: `fix: park an agent run when a step is interrupted instead of failing it`.

## Task 4: An interrupted replan parks

**Files:**
- Modify: `src/Pia.Wpf/Services/AgentRunOrchestrator.cs` — `TryReplanAfterFailureAsync` (`:284-314`), its
  callers (`:412`, `:533`), the verify-fail replan (`:553-566`)
- Test: `tests/Pia.Wpf.Tests/Services/AgentRunOrchestratorArmTests.cs`,
  `tests/Pia.Wpf.Tests/Services/AgentRunOrchestratorFanOutTests.cs`

**Interfaces:**
- Consumes: Task 3's tokens and `StepInterruptionClassifier.Classify`.
- Produces: `async Task<ReplanOutcome> TryReplanAfterFailureAsync(string? error, AgentStep? failedStep)`.
  `Revised` means continue the drain loop, `Failed` means break as today, `Parked` means return from
  `RunAsync`.

Behaviour:
- Step-failure site: wrap only the `_planner.ReplanAsync` call. On an exception with
  `failedStep is not null`, the run token not cancelled and `Classify(ex)` non-null: set `failedStep` back
  to Pending with `SafeSetStepStatus(…, CancellationToken.None)`, `PinRange`, `SafePause` with the token,
  `SafeOnPaused`, return `Parked`. Do not call `ReturnStepToPendingAsync`: the step's tokens were already
  billed when it was recorded.
- The fan-out caller passes `failedStep: null`, so the exception reaches the catch-all as it does today.
- Verify-fail site: same wrap around its `_planner.ReplanAsync` call. On a classified exception with the
  token not cancelled: `PinRange`, `SafePause`, `SafeOnPaused`, `return`. Every step is already Done and
  stays Done.
- Each park logs one `LogInformation` line with the run id and the token.

Test doubles: `StubPlanner` gains `Exception? ReplanThrows` and an optional
`CancellationTokenSource? CancelBeforeReplanThrows`.

1. Write the failing tests.
   - `AnInterruptedReplanAfterAStepFailure_Parks_AndPutsTheFailedStepBackToPending`: plan `A`,
     `FailFirstStep = true`, `ReplanThrows = new CreditLimitException("x")`. State `WaitingForInput`; reason
     `interrupted-credit`; `NextPendingStepAsync` returns `A`; `executor.EndedRun` is false.
   - `TheStepPutBack_RunsAgainOnResume_AndEndsDone`: resume that run with an executor that succeeds. State
     `Completed`; `A` is Done; the resumed executor's `StepTurns == 1`.
   - `AnInterruptedReplanAfterAFailedVerify_Parks_WithEveryStepDone`: plan `A`, a verifier stub whose
     verdict has `Passed == false` (construction as in
     `AgentRunResumeNoRePlanPremiseTests.ZeroStepResume_WhoseVerifyFails_ReachesReplanAsync`),
     `ReplanThrows = new HttpRequestException(HttpRequestError.ConnectionError)`. State `WaitingForInput`;
     reason `interrupted-offline`; `A` is Done; no Pending step.
   - `AnOrdinaryReplanFault_StillFailsTheRun`: `ReplanThrows = new InvalidOperationException("boom")`.
     State `Failed`.
   - `AnInterruptedReplanDuringACancel_DoesNotPark`: the stub cancels the token passed to `RunAsync`, then
     throws `new CreditLimitException("x")`. State is not `WaitingForInput`.
   - In `AgentRunOrchestratorFanOutTests`, `AnInterruptedReplanAfterAFailedGroup_FailsTheParentAsBefore`: a
     group with one failing child, the parent's replan throws `new CreditLimitException("x")`. Parent state
     `Failed`.
2. Run both classes. Expected: the three park tests fail, because the run ends Failed. The ordinary-fault,
   cancel and fan-out tests are guards and pass before and after.
3. Implement the outcome enum, the two wraps and the caller changes.
4. Run both classes, then `Pia.Tests.Services.AgentRunOrchestratorTests`. Expected: `Failed: 0` in each.
5. Commit: `fix: park an agent run when a replan call is interrupted`.

## Task 5: The headless executor names the interruption

**Files:**
- Modify: `src/Pia.Wpf/Services/HeadlessTurnExecutor.cs:876`
- Test: `tests/Pia.Wpf.Tests/Services/InterruptionParkTests.cs` (new, launcher fixture and probe stream
  modelled on `MidPlanAskTests.Build` / `Drive`), `tests/Pia.Wpf.Tests/Services/UnattendedApprovalParkTests.cs`,
  `tests/Pia.Wpf.Tests/Services/HeadlessTurnExecutorTests.cs`,
  `tests/Pia.Wpf.Tests/Services/ScheduledJobBackgroundServiceTests.cs`

**Interfaces:**
- Consumes: `StepInterruptionClassifier.Classify`, the orchestrator arm (Task 3).
- Produces: `StepTurnResult.Interruption` on the headless path.

The final `return` of the catch-all carries
`Interruption: offerStepResultTool && !ct.IsCancellationRequested ? StepInterruptionClassifier.Classify(ex) : null`.
The two park arms above it are untouched and stay first. When the kind is non-null, add one
`LogInformation` with `_runId` and the token.

The probe throws a caller-supplied `Exception? Fault` out of the stream enumeration, as
`MidPlanAskTests.Drive` throws its `FaultMessage`. That fixture plans through a stub (`OneStepPlanner`), not
through the faulting stream, so the fault reaches step turns only. Keep it that way: a fault on the plan
call fails the run at planning, which is out of scope.

1. Write the failing tests in `InterruptionParkTests`.
   - `Theory` `AStepWhoseProviderCallIsInterrupted_ParksWithTheToken(string shape, string token)`: `credit`,
     `llm-timeout`, `refused`, `stream-reset`, `cloud-stream`, `http-503` (the shapes of Task 2). The run is
     `WaitingForInput` with `token` in the envelope, `CompletedAt` is null, the envelope has no `error`
     member, and the run's chat holds no assistant message.
   - `AnOrdinaryFault_StillFailsTheRun`: `new InvalidOperationException("boom")`. State `Failed`.
   - `ADeterministic400_StillFails_AndNeverParks`: `new HttpRequestException("m", null, HttpStatusCode.BadRequest)`.
     State `Failed`.
   - In `ScheduledJobBackgroundServiceTests`, `ExecuteOnceAsync_AgentTaskJob_ParkedOnAnInterruption_TakesNoStrike`,
     set up as `ExecuteOnceAsync_AgentTaskJob_ParkedAtBudget_MovesTheScheduleOnceAtDispatch_AndDoesNotRelaunch`
     (`:623`) with the envelope reason `interrupted-credit`. `MarkRunFailedAsync` and `NotifyFailure` are
     not received. A guard: it passes before and after.
   - `APendingQuestion_OutranksTheInterruption`: the turn asks, then the stream throws
     `new HttpRequestException(HttpRequestError.ConnectionError)`. Reason `needs-input`.
   - In `HeadlessTurnExecutorTests`, with that file's existing executor construction,
     `AFaultAfterTheRunTokenIsCancelled_IsNotAnInterruption`: the fake stream cancels the token passed to
     `ExecuteStepAsync`, then throws the `stream-reset` shape. `Assert.Null(result.Interruption)`.
   - `AnInterruptedRun_ParksAgainWhileTheOutageLasts_ThenFinishesWhereItStopped`: launch with the `credit`
     fault → parked. `ResumeAsync(runId, "continue")` with the fault still set → parked again with the same
     token and the same step Pending. Clear the fault, resume → `Completed`, every step Done.
   - In `UnattendedApprovalParkTests`, a twin of
     `AParkIsNotDiscardedByAProviderFaultLaterInTheSameExchange` whose later fault is
     `new HttpRequestException(HttpRequestError.ConnectionError)`: the reason is still `tool-approval`.
   - In `HeadlessTurnExecutorTests`, add `Assert.Null(result.Interruption)` to one existing fact for an
     empty response and one for a step that declared `succeeded:false`.
2. Run class `Pia.Tests.Services.InterruptionParkTests`. Expected: the park tests fail with state `Failed`.
3. Implement the one-line change and the log line.
4. Run `InterruptionParkTests`, `MidPlanAskTests`, `UnattendedApprovalParkTests`, `HeadlessTurnExecutorTests`.
   Expected: `Failed: 0` in each.
5. Commit: `fix: stop a background agent run resumably on a provider interruption`.

## Task 6: The live step names the interruption and drops its error bubble

**Files:**
- Modify: `src/Pia.Wpf/ViewModels/Models/ChatSession.cs` — `RunStepTurnAsync` (`:786-1013`)
- Test: `tests/Pia.Wpf.Tests/ViewModels/ChatSessionStepTurnTests.cs`,
  `tests/Pia.Wpf.Tests/Services/AgentRunOrchestratorUserPauseLiveTests.cs`

**Interfaces:**
- Consumes: `StepInterruptionClassifier.Classify`, the orchestrator arm (Task 3).
- Produces: `StepTurnResult.Interruption` on the live path.

Behaviour:
- The timeout arm, the offline arm and the final catch-all each record
  `!spec.UseGoalVerbatim && !ct.IsCancellationRequested ? StepInterruptionClassifier.Classify(ex) : null`
  into a local. The truncation, cancel and vision arms never set it.
- After the `finally`, a pending user question clears the local: the question wins and its bubble stays.
- When the local is still set: remove `assistantMessage` from `Messages`, remove its id from
  `_stepToolExchanges` and `_answeredSteps`, and return `VisibleText: string.Empty`,
  `FirstMessageId` and `LastMessageId` `Guid.Empty`, `Interruption` set.
- `LiveTurnExecutor.ExecuteStepAsync` already calls `RequestPersist` after the step, so the stored chat
  never holds the dropped bubble. No change there.

`Spec(…)` in the test class gains an optional `bool useGoalVerbatim = false`.

1. Write the failing tests in `ChatSessionStepTurnTests`.
   - `Theory` `RunStepTurn_Interrupted_ReturnsTheKind_AndRemovesTheStepBubble(string shape, StepInterruption kind)`
     for `credit`, `llm-timeout`, `refused`, `cloud-stream`: `result.Interruption == kind`;
     `result.Succeeded` is false; both message ids are `Guid.Empty`; `result.VisibleText` is empty;
     `session.Messages` holds only the goal; the state is not `Error`.
   - Extend `RunStepTurn_Exception_ReturnsFailedResult_NoErrorState`: `Assert.Null(result.Interruption)` and
     `Assert.Equal(2, session.Messages.Count)`.
   - `RunStepTurn_Truncated_IsNotAnInterruption`: `new LlmTruncatedException("P", 10)`. Interruption null,
     two messages.
   - Add `Assert.Null(result.Interruption)` to one existing fact for an empty response and one for a step
     that declared `succeeded:false`.
   - `RunStepTurn_InterruptedFallbackTurn_KeepsItsErrorBubble`: `useGoalVerbatim: true`, the `refused`
     shape. Interruption null, two messages.
   - `RunStepTurn_FaultAfterStop_IsNotAnInterruption`: `ThrowingStream(<stream-reset>, beforeThrow: cts.Cancel)`
     with `cts.Token` as the step token. Interruption null.
   - `RunStepTurn_InterruptedAfterAsking_KeepsTheQuestion`: the stream calls `request_user_input` through
     the handler, then throws the `refused` shape. `result.UserInputQuestion` is set, `result.Interruption`
     is null, two messages.
2. Add to `AgentRunOrchestratorUserPauseLiveTests`: `ALiveStepInterrupted_ParksTheRun_AndLeavesNoErrorBubble`.
   A real session and `LiveTurnExecutor`, the AI stub throws `new CreditLimitException("limit text")`. The
   run is `WaitingForInput` with `interrupted-credit`; the step is Pending; `session.State` is `Idle`; no
   message in `session.Messages` contains `"limit text"`.
3. Run both classes. Expected: the new tests fail; the bubble is present and `Interruption` is null.
4. Implement the behaviour above.
5. Run both classes, then `Pia.Tests.ViewModels.ChatSessionManagerTests`. Expected: `Failed: 0` in each.
6. Commit: `fix: stop a live agent run resumably on a provider interruption`.

## Task 7: The card and the Flow item say why

**Files:**
- Modify: `src/Pia.Wpf/ViewModels/RunProgressViewModel.cs` (`DescribePause`, `:1429-1452`)
- Modify: `src/Pia.Wpf/Services/AgentRunNotificationSurface.cs` (`PausedBodyKey`, `:94-115`)
- Modify: `src/Pia.Wpf/Resources/Strings/ViewStrings.resx`, `ViewStrings.de.resx`, `ViewStrings.fr.resx`
  (the `Run_Activity_*` keys after `Run_Activity_ResumeInterrupted`, the `Flow_Run_*` keys after
  `Flow_Run_ResumeInterrupted`)
- Test: `tests/Pia.Wpf.Tests/ViewModels/RunProgressViewModelTests.cs`,
  `tests/Pia.Wpf.Tests/Services/AgentRunNotificationSurfaceTests.cs`,
  `tests/Pia.Wpf.Tests/Architecture/LocalizationTests.cs`

**Interfaces:**
- Consumes: the four token constants (Task 3).

Copy. The card key and the Flow key of a kind carry the same text; they are two keys because the two
surfaces are edited separately. English is the issue's wording. The German and French credit lines follow
the existing `Msg_CreditLimit_*` strings, where the limit is what gets reset.

| Kind | English | German | French |
|---|---|---|---|
| Credit | Credit limit reached. Continue once your credit has reset. | Credit-Limit erreicht. Setzen Sie fort, sobald es zurückgesetzt wurde. | Limite de crédits atteinte. Reprenez une fois qu'elle aura été réinitialisée. |
| Offline | Connection lost. Continue when you are back online. | Verbindung unterbrochen. Setzen Sie fort, sobald Sie wieder online sind. | Connexion perdue. Reprenez lorsque vous serez de nouveau en ligne. |
| Timeout | The model did not answer in time. Continue? | Das Modell hat nicht rechtzeitig geantwortet. Fortsetzen? | Le modèle n'a pas répondu à temps. Reprendre ? |
| Provider | The AI service had a problem. Continue? | Beim KI-Dienst ist ein Problem aufgetreten. Fortsetzen? | Le service d'IA a rencontré un problème. Reprendre ? |

1. Write the failing tests.
   - `RunProgressViewModelTests.AParkedRunsActivityLineNamesWhyItParked`: four `InlineData` rows,
     `("interrupted-credit", "Run_Activity_InterruptedCredit")` and the three siblings.
   - `RunProgressViewModelTests.AnInterruptionPark_OffersContinue`: pause with `interrupted-offline`,
     refresh, `Assert.True(vm.CanContinue)`.
   - `AgentRunNotificationSurfaceTests.AParkedRunsFlowBodyNamesWhyItParked`: four `InlineData` rows,
     `("interrupted-credit", "Flow_Run_InterruptedCredit")` and the three siblings.
   - `LocalizationTests.EveryInterruptionPauseKeyResolvesInAllThreeLocales`: for each of the four tokens,
     `AgentRunNotificationSurface.PausedBodyKey(token)` and the matching `Run_Activity_Interrupted*` key
     exist in the invariant, `de` and `fr` key sets (shape of `EveryRunStateLabelKeyResolvesInAllThreeLocales`).
     Non-vacuity: eight distinct keys.
2. Run the three classes. Expected: the new rows fall through to the budget keys and fail.
3. Add the four arms to each switch and the eight keys to each of the three files.
4. Run the three classes. Expected: `Failed: 0`, including `AllTranslations_MustBeComplete`.
5. Commit: `fix: say why an interrupted agent run stopped`.

## Task 8: A failed resume keeps the interruption token

**Files:**
- Modify: `src/Pia.Wpf/Services/HeadlessRunLauncher.cs` (`InterruptedReasonFor`, `:162-168`)
- Test: `tests/Pia.Wpf.Tests/Services/HeadlessRunLauncherTests.cs`

**Interfaces:**
- Consumes: `AgentRunOrchestrator.IsInterruptionReason` (Task 3).

`InterruptedReasonFor` returns the incoming token unchanged when `IsInterruptionReason` is true, next to the
three tokens it already preserves.

1. Write the failing tests.
   - Four `InlineData` rows on the existing theory at `:1611-1615`, the one that already pins
     `("step-cap", "resume-interrupted")` and the three preserved tokens: each interruption token maps to
     itself, for example `("interrupted-credit", "interrupted-credit")`.
   - `Resume_AfterAnInterruption_InitializesTheExecutorWithTheSameRunWorkspaceRoot`: the twin of
     `Resume_InitializesTheExecutorWithTheSameRunWorkspaceRoot` (`:606`) with the run parked on
     `interrupted-offline`.
2. Run class `Pia.Tests.Services.HeadlessRunLauncherTests`. Expected: the four new rows fail with
   `resume-interrupted`. The workspace twin is a guard and passes before and after.
3. Implement the arm.
4. Run the class. Expected: `Failed: 0`.
5. Commit: `fix: keep the interruption reason when a continuation fails to start`.

## Task 9: A typed Agent-mode message continues the run

**Files:**
- Modify: `src/Pia.Wpf/ViewModels/Models/ChatSessionManager.cs` — the call in `StartTurnAsync` (`:737-739`)
  and `TryAnswerParkedRunAsync` (`:1089-1102`)
- Test: `tests/Pia.Wpf.Tests/ViewModels/ChatSessionManagerTests.cs` (beside the facts at `:2055`)

**Interfaces:**
- Consumes: `AgentRunOrchestrator.IsInterruptionReason` (Task 3).
- Produces: `TryAnswerParkedRunAsync(…, string? parkReason, bool planned)`.

The method proceeds when the reason is `needs-goal` or `needs-input` (any mode, as today) or when `planned`
is true and `IsInterruptionReason(parkReason)`. Everything after that guard is unchanged: the durable
append, then `ResumeAsync(runId, userText)`.

1. Write the failing tests.
   - `Theory` `StartTurnAsync_AgentSendDuringAnInterruptionPark_ResumesWithTheText_AndCreatesNoRun(string token)`
     over the four tokens: `AttachParkedRun(session, token)`, then
     `sut.StartTurnAsync(session, "continue", null, planned: true)`.
     `_resumeService.Received(1).ResumeAsync(runId, "continue", Arg.Any<CancellationToken>())`;
     `_runService.DidNotReceiveWithAnyArgs().CreateAsync(default!)`; `session.Messages` holds one user
     message; `SaveMergedAsync` is received before `ResumeAsync` (`Received.InOrder`).
   - `StartTurnAsync_ChatSendDuringAnInterruptionPark_StartsAnOrdinaryTurn_AndNeverResumes`: the same park,
     `planned: false`, set up and asserted as
     `StartTurnAsync_RunParkedAtBudget_StartsAnOrdinaryTurn_AndNeverResumes`.
2. Run class `Pia.Tests.ViewModels.ChatSessionManagerTests`. Expected: the theory fails; no resume is
   received.
3. Implement the parameter and the guard.
4. Run the class. Expected: `Failed: 0`, including the untouched `needs-goal` / `needs-input` facts.
5. Commit: `fix: continue an interrupted agent run from a typed message`.

## Task 10: Gate

1. `dotnet build -t:Rebuild -v:n` and `dotnet build -t:Rebuild -v:n -c Release`. Expected in both:
   `0 Warning(s)`, `0 Error(s)`.
2. The full exe with no filter, redirected to `test.log`. Expected: `Failed: 0`.
3. Manual check on the throwaway stack (port 18081, never 8081), following
   `docs/ui_automation/ui-automation-playbook.md`: start an Agent run of at least three steps, cut the
   connection during step 2, and confirm the card reads "Connection lost. Continue when you are back
   online." with step 1 ticked. Restore the connection, type "continue" with the lever on Agent, and confirm
   the run resumes at step 2, step 1 does not run again, and the files are published at the end.
4. PR description: link issue 95; state that no server call was added or changed; state how Q1–Q4 closed;
   state the routine behaviour change (Q3), that every provider is covered (Q2) and that delegated child
   runs now park (F14).

## Not in this plan

Listed in the checklist's "not yet planned" section so they are not lost: a Retry for runs that are already
Failed, the parent's wording for an interrupted sub-agent, the reset time on the card, an interruption of the first plan call
or of the fallback turn, the routine pile-up during a long outage, a send with an attachment during an
interruption park, a sign-out (401) mid-run, the existing headless `LogError` that carries the exception
message, and `FailureMapper` filing a dropped stream under Workspace.
