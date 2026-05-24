# Plan — Stats summary (token count + model)

**Goal:** the `PiaAnswerToolbar` right-side caption — currently empty — shows
something like `1.234 Tokens · Pia.Cloud` per finished assistant turn.

**Surface (already built):**
- `Pia.Models.AnswerStats(int Tokens, string Model)` → exposes `Summary`.
- `AssistantMessage.Stats` (`ObservableProperty`).
- `PiaAnswerToolbar.Stats` DP → renders `Stats.Summary` (auto-hides when null).

**Why:** transparency. Power users see token spend; the llm name gives transparency.

---

## Steps

### 1. Capture usage during streaming

`AiClientService.GetChatCompletionWithToolsAsync` already collects every
`ChatResponseUpdate` into `updates` (see ~line 287). The Microsoft.Extensions.AI
`ChatResponseUpdate` carries `Usage` (a `UsageDetails`) on the final update.

- Make the streaming method **also** return total usage. Cleanest path: add a
  new overload returning `IAsyncEnumerable<ChatStreamItem>` where
  `ChatStreamItem` is a discriminated record (`TextDelta(string)` |
  `Finished(UsageDetails?, string Model)`), and adapt callers.
- Less invasive alternative: add an `out`-style `Action<AnswerStats>` parameter
  the caller can supply, invoked once at stream end.

Pick the discriminated record — it composes better with the existing tool-loop.

### 2. Surface model id

The chat client knows its model via `provider.ModelName` (`AiProvider`).
Pass it into the stream end-event alongside the usage so the VM doesn't
have to re-resolve the active provider.

### 3. Plumb to `AssistantMessage.Stats`

In `AssistantViewModel`, the streaming consumer is the loop that appends
text to the current `AssistantMessage.Content`. Wherever that loop ends
(when the enumerator finishes), set:

```csharp
currentMessage.Stats = new AnswerStats(
    (int)(usage?.InputTokenCount + usage?.OutputTokenCount ?? 0),
    providerModel);
```


### 4. Bind the toolbar in the view

`PiaAssistantMessage.xaml` already binds `Stats="{Binding Stats}"`, so once
the model property changes the UI updates. No XAML edits needed.

---

## Acceptance

- Send any message. After the turn finishes, the toolbar shows the stats
  line right-aligned. While streaming the line is empty.
- Switch provider (e.g. cloud → local Ollama). The model name in the
  summary updates on the next turn.
- Cancel mid-stream (Esc): no stats line appears (the turn never finished).

## Risks / notes

- Not every provider populates `UsageDetails` reliably during streaming.
  Default to 0 + log at `SensitiveDebug` when usage is missing, don't
  show a broken `0 Tokens` line — collapse the toolbar caption when
  Tokens == 0.
- The summary string is currently hardcoded in German ("lokal"). If the
  app is meant to localize, move that to `ViewStrings.resx`.
