# Plan — Source chips (which data drove the answer)

**Goal:** show a row of numbered pills under the assistant body listing
the data sources the answer used: memory hits, pantry/Vorrat items, web
search results, plugin tool results.

**Surface (already built):**
- `Pia.Models.SourceRef(int Number, string Source, string Meta)`.
- `AssistantMessage.Sources` (`ObservableCollection<SourceRef>`) + `HasSources`.
- `PiaSourceChip` (Number / Source / Meta DPs).
- `PiaAssistantMessage` renders chips in a `WrapPanel` when `HasSources`.

**Why:** trust + verifiability. Users see exactly which memory entries
or plugin results shaped the reply. Big differentiator for an assistant
that mixes private memory with a cloud LLM.

---

## The hard part: tool result plumbing

Tool handlers today return plain `object?` results that get serialized
into a `FunctionResultContent`. There is no side-channel for source
metadata. We need one.

### Option A — extend tool return shape (recommended)

Define `Pia.Services.ToolInvocationResult`:

```csharp
public sealed record ToolInvocationResult(
    object? Result,
    IReadOnlyList<SourceRef> Sources);
```

Change each `I*ToolHandler` interface so the executor receives that
record instead of `object?`. Existing handlers wrap their current
result like `new ToolInvocationResult(existing, Array.Empty<SourceRef>())`
in a first cleanup pass, then progressively start returning real
sources.

Affected interfaces (`src/Pia.Wpf/Services/Interfaces/`):
- `IMemoryToolHandler` — returns matched memory entries → emit a
  `SourceRef` per hit with `Source="Profil"`, `Meta=summary`.
- `IReminderToolHandler`, `IScheduledJobToolHandler`,
  `ITodoToolHandler` — mostly *write* paths; usually no sources.
  Skip on first pass.
- `IPluginToolHandler` — depends on the plugin. Emit one chip per
  plugin invocation with `Source=pluginName`, `Meta="tool: …"`.
- `IResearchHistoryToolHandler` — emit `Source="Recherche"`,
  `Meta=session.Query`.

### Option B — ambient collector

Add an `AsyncLocal<List<SourceRef>>` that the tool executor sets before
each invocation. Tool handlers call a helper `SourceCollector.Add(...)`.
Cheaper to retrofit but worse for testing (hidden coupling).

Go with Option A.

---

## Step 1 — Introduce the record + interface changes

Add `ToolInvocationResult` in `Pia.Services`. Update all
`I*ToolHandler` interfaces to return it from their execute methods.
Wrap each handler's existing return as the no-source shape so the
build stays green.

## Step 2 — Plumb to the message

Where `AiClientService.GetChatCompletionWithToolsAsync` invokes
`toolHandler` (see ~line 199), `toolHandler` currently returns `object?`.
Change the delegate to `Func<FunctionCallContent, Task<ToolInvocationResult>>`.
Collect sources during the tool loop into a local list. At stream end,
flush them onto the message:

```csharp
foreach (var s in sourcesThisTurn)
    currentMessage.Sources.Add(s with { Number = currentMessage.Sources.Count + 1 });
```

## Step 3 — Populate real sources for memory + research

Memory handler is the highest-value first source. After matching memory
entries, build one `SourceRef` per top-N hit with the entry's display
title (NOT raw memory text — sensitive). Cap at 5 chips.

Research history is the second highest value — research sessions are
already keyed.

Other tool handlers can stay empty until needed.

## Step 4 — Optional: click-to-detail

Add `OpenSourceCommand` on the VM that takes a `SourceRef` and opens
a contextual flyout (memory entry detail, research session, etc).
Bind on `PiaSourceChip` via a wrapping Button template. Skip on first
pass; non-trivial.

---

## Acceptance

- Ask Pia something that hits memory: "Was weißt du über mich?". After
  the turn ends, 1–3 chips appear with `Profil` labels.
- Ask Pia to recall a past research: chips show `Recherche` with the
  session title.
- Ask Pia an arbitrary general question with no memory match: no chips.
- Chips wrap below the markdown body, never overlap the toolbar.

## Risks / notes

- `SourceRef.Source` / `Meta` are user-adjacent content. Never log at
  Information level — use `SensitiveDebug`. The `Number` is safe.
- Cap chips per turn (e.g. 5) so a heavy memory hit doesn't push the
  toolbar offscreen.
- Persistence: if you persist chat history, persist `Sources`. Otherwise
  fine to leave ephemeral.
- This plan is the biggest of the four because the producer side touches
  every tool handler. Land Step 1 + Step 2 in one PR (skeleton, no real
  sources yet), then Step 3 per-handler.
