# AI stream idle timeout — design

**Status:** Approved in brainstorming; ready for an implementation plan.
**Owner:** man
**Written:** 2026-09-22
**Origin:** Investigation of `artifacts/pia-2026-09-22.log`, in which three consecutive chat turns died
on the provider timeout while the provider was answering. The per-round timeout fix that came out of the
same session (`AiClientService.GetChatCompletionWithToolsAsync`, round-scoped `timeoutCts`) shipped
first and is assumed present here.

---

## 1. The problem

The provider timeout cancels work that is progressing, because at the layer the timer runs it cannot
tell progress from a hang.

Two independent mechanisms produce that:

**A round that emits only tool calls is silent to its consumer.** `PiaCloudChatClient` accumulates
tool-call argument fragments into a builder and yields nothing until `finish_reason == "tool_calls"`:

```csharp
// PiaCloudChatClient.cs:225-226
var args = funcNode?["arguments"]?.GetValue<string>();
if (args is not null) builder.Args.Append(args);
```

So for the whole of such a round `AiClientService` receives exactly one update — the content-free
guardrail marker the server emits first — and then nothing until the end. In the log, turn 3 round 3
wrote eight `edit_file` patches over 245.6 s; from the consumer's side that was 245.6 s of total
silence, indistinguishable from a dead socket.

**The timer measures elapsed time, not silence.** Turn 1 round 3 was streaming reasoning deltas when it
was cancelled at exactly 300.0 s — the timed-out message in the UI carries the reasoning chevron, which
`PiaReasoningView.xaml:64` gates on `HasThinkingContent`.

Measured evidence that this is not simply a slow model: in one conversation, round 3 produced 8
`edit_file` calls in 245.6 s while round 5 produced 7 in 18.9 s, on a strictly larger context.

## 2. The contract

`provider.TimeoutSeconds` is reinterpreted as **maximum silence on the wire**. A stream that keeps
delivering bytes runs as long as it needs; a stream that stops delivering for that long is dead and is
reported as such.

A second bound, not user-facing, exists only to stop a pathological dribble.

| bound | value | lives in | catches |
|---|---|---|---|
| idle | `provider.TimeoutSeconds` (default 300) | `IdleTimeoutStream` | a hung or dead stream |
| ceiling | 10 minutes, fixed constant | the per-round `timeoutCts` in `AiClientService` | an endless dribble |

Ten minutes is far above any round observed (worst: 283 s) and keeps a 24-round step bounded at roughly
four hours in the pathological case, which the user can cancel out of at any time.

The `Timeout (seconds)` field keeps its 600 maximum. As an idle window 600 is generous rather than
useful, but it costs nothing and the cap was raised in the same release.

## 3. Components

Two new types, plus a factory.

```
IdleTimeoutHandler : DelegatingHandler
  SendAsync → await base.SendAsync(...), then replace response.Content with a
              StreamContent over IdleTimeoutStream(original, idleWindow)

IdleTimeoutStream : Stream
  ReadAsync → races the inner read against a CancellationTokenSource(idleWindow)
              linked to the caller's token; any read returning >0 bytes re-arms the
              clock; the window elapsing throws StreamIdleTimeoutException

AiHttpPipeline.CreateTail(TimeSpan idle) → IdleTimeoutHandler over an HttpClientHandler
```

The clock resets on **bytes**, not on parsed chunks. That is what makes it see the tool-call fragments
of §1's silent round, and an SSE keep-alive comment if the server ever sends one.

Swapping `response.Content` inside the handler works for both completion options: `HttpClient` buffers a
`ResponseContentRead` body after the handler chain returns, so the wrapper is in place either way.

## 4. Installation — nine call sites

There is no single insertion point. Five handlers ignore the injected `HttpClient` and build their own
chain; four use the injected one.

| handler | today | change |
|---|---|---|
| Anthropic, Mistral, OpenAI, OpenRouter, vLLM | `new HttpClientHandler()` as `tail` | `AiHttpPipeline.CreateTail(idle)` as `tail`, own handlers still layered on top |
| Azure OpenAI, Ollama, OpenAiCompatible, Pia Cloud | injected client | `CreateAiHttpClient()` builds on the same factory |

Provider-specific handlers (`OpenAiWebSearchHandler`, `OpenAiReasoningSummaryFallbackHandler`) keep their
position and ordering; the idle handler sits innermost, closest to the socket.

An architecture guard asserts `new HttpClientHandler()` appears nowhere outside `AiHttpPipeline`. That
guard is what makes this placement as safe as changing `IAiProviderHandler` to hand over the chain — the
heavier alternative that was considered and rejected for blast radius.

## 5. Error handling

`LlmTimeoutException` is `sealed`, so the stream layer throws `StreamIdleTimeoutException :
TimeoutException` and `AiClientService` translates it to `LlmTimeoutException(provider.Name,
idleSeconds, …)` at the points it already translates cancellation. The exception type reaching callers
is unchanged: the comment on `AcquireProviderPermitAsync` records that every downstream catch, retry and
UI path depends on it.

Catch ordering matters — `StreamIdleTimeoutException` must be caught before any broader
`TimeoutException`, which `LlmTimeoutException` also derives from.

`Msg_Assistant_ResponseTimedOut` ("Anbieter X hat nicht innerhalb von N Sekunden geantwortet") becomes
accurate for the idle case. The ceiling case needs its own string: "did not answer" is wrong when the
answer was still arriving.

**Principal risk.** The exception must survive the vendor SDK layer. `OpenAiProviderHandler` hands its
pipeline to `ResponsesClient`; Anthropic and Mistral do the same. An SDK that catches stream exceptions
and rethrows its own type forces classification to walk the `InnerException` chain. This is the step
most likely to surprise the implementation and is called out in §6 with its own test.

## 6. Testing

- `IdleTimeoutStream` unit tests: a slow-but-steady stream survives; a stalled one throws after the
  window; cancellation during a read still cancels rather than reporting a timeout.
- `AiClientService` integration with a fake handler serving a stalling body — asserts
  `LlmTimeoutException` carrying the idle seconds, not the ceiling.
- A round that streams continuously past the old 300 s bound completes. This is the regression the
  whole change exists for, and it must fail without the wrapper.
- Architecture guard on `new HttpClientHandler()` (§4).
- One SDK-passthrough test per self-building handler, proving `StreamIdleTimeoutException` arrives
  recognisably through the vendor transport (§5).

## 7. Out of scope

- **Yielding tool-call progress.** `PiaCloudChatClient.cs:217` already logs a `SensitiveDebug` line per
  call-opening delta and could yield a lightweight update there. Under this design it buys the timer
  nothing — the idle clock sits below that layer — so its value is purely that the UI stops looking
  frozen for four minutes. Worth doing; a separate change, and one that must first check
  `AiClientServiceToolLoopArmTests` and `AiClientServiceProtectedRouteTests` for assertions on update
  counts.
- **Resuming a cut round.** When a round is cancelled the applied edits remain on disk while the
  model's closing text and the cut round's tool results are discarded. A resume path is a different
  feature.
- **Capping tool calls per round.** Rejected: it does not shorten a round that thinks before its first
  call, and it costs extra round-trips.
- **A server-side heartbeat.** Not required — the wire carries argument fragments during the silent
  phase. It would only matter if measurement showed the wire genuinely idle while the upstream model
  thinks, in which case revisit §2.

## 8. Open measurement

The design holds regardless, but one cheap reproduce sharpens it: at Debug level,
`PiaCloudChatClient.cs:217` logs `tool_call delta id=… name=…` per call-opening. Where those timestamps
fall across a long round says whether the wire is continuously active (idle timer alone suffices) or
bursts at the end after silent upstream thinking (the §7 heartbeat question reopens).
