# Meeting Summarization — Design

**Date:** 2026-04-27
**Status:** Approved (pending implementation plan)
**Branch:** `feature/meeting_transscription`

## Problem

The live transcription overlay can save a transcript as Markdown, but everything after that is manual: the user has to open the file, read it, and decide what to do. Meetings produce three recurring needs the assistant should handle directly — cleaning up STT noise, extracting actionable bullets (todos, decisions, open questions), and writing a prose summary. Recurring meetings also need a place to live so the user can later ask *"what did we discuss with Alice last month?"*.

## User-visible flow

1. After stopping recording in the live transcription overlay, a new **"Save and summarize"** button appears next to **Save** and **Resume** (visible only when `IsRunning=false`).
2. Click → overlay closes; transcript is silently written as Markdown (with YAML front-matter) to the configured meeting folder using the existing auto-name pattern `transcript-YYYYMMDD-HHmmss.md`. A snackbar reports the saved path with an "Open folder" affordance.
3. The button injects a synthetic user message into the assistant chat: *"Please summarize the meeting transcript saved at `%APPDATA%\Pia\assistant\meetings\transcript-….md`"*. The path is shortened against the closest matching environment variable (`%APPDATA%`, `%LOCALAPPDATA%`, `%USERPROFILE%`) for a clean display.
4. The LLM picks `summarize_meeting_transcript`. The handler returns a **multi-choice action card** in the chat: *"How should I summarize this meeting?"* with three buttons — **Clean text**, **Bulleted by topic**, **Text summary**. (Escape / dismiss = decline.)
5. On choice, the tool reads the file (env vars expanded), prompts the AI provider with the appropriate summarization template, and the summary streams back as a normal assistant message.
6. The assistant then conversationally asks *"Want me to save this as a memory?"*. If the user agrees, it calls the existing `create_object` memory tool with `type=meeting_summary` — the standard memory action card appears for confirmation.
7. Later, the user can ask *"What meetings did I have with Alice last month?"* and the LLM picks `query_meeting_summaries` for date+speaker filtering.

## Architecture & components

| Unit | Purpose | Depends on |
|---|---|---|
| `Services/LiveTranscription/MeetingTranscriptWriter.cs` | Writes transcript markdown + YAML front-matter (speakers, start/end, original filename). Pure formatting; no UI. Replaces the inline `BuildMarkdown` in `LiveTranscriptionViewModel`. | Bubbles, settings |
| `Services/PathShortener.cs` | Shortens a path against `%APPDATA%`, `%LOCALAPPDATA%`, `%USERPROFILE%` (longest match wins); expands back. Pure utility. | none |
| `Services/MeetingToolHandler.cs` (+ `IMeetingToolHandler`) | New plugin handler exposing `summarize_meeting_transcript` and `query_meeting_summaries`. | `IAiClientService`, `IProviderService`, `IMemoryService`, `ILocalizationService` |
| `Services/Plugins/BuiltInPluginHandler.FromMeetingHandler(...)` | Adapter factory mirroring `FromMemoryHandler` etc. | existing |
| `Services/Plugins/BuiltInPluginDefaults.cs` (edit) | Adds the meeting plugin to defaults. | existing |
| `Models/ActionCardChoice.cs` + `ActionCardInfo` extension | Optional `Choices: List<ActionCardChoice>` (key + label). 2-choice case (no `Choices` set) keeps the existing Accept/Decline buttons unchanged. `WaitForUserDecisionAsync()` is generalized: a binary card returns `"accept"`/`"decline"` for back-compat; multi-choice returns the chosen key. | existing |
| `Controls/ActionCardControl.xaml` (edit) | Renders N buttons when `Choices` is non-empty, otherwise the existing Accept/Decline buttons. | existing |
| `Models/MemoryObjectTypes.cs` (edit) | Adds `MeetingSummary = "meeting_summary"`. `GetDisplayName` updated. `MemoryToolHandler.CreateObjectSchema` description updated to mention the new type. | existing |
| `ViewModels/LiveTranscriptionViewModel.cs` (edit) | Adds `SaveAndSummarizeCommand`. Raises `SummarizeRequested(MeetingSummarizationRequest)` event. The VM does not depend on the assistant. | existing |
| `ViewModels/AssistantViewModel.cs` (edit) | Subscribes to `LiveTranscription.SummarizeRequested`, closes the overlay, shows the snackbar, and injects the synthetic chat message via the existing `ExecuteSendMessage` pathway. | existing |

### Boundaries

- **`MeetingTranscriptWriter`** is the only thing that knows the markdown/YAML format. Both the button handler and tests use it.
- **`PathShortener`** is a tiny pure utility — testable in isolation, reusable elsewhere.
- **`MeetingToolHandler`** is the *only* component that knows the summarization prompts and the meeting-memory schema (label format, JSON shape).
- The **multi-choice extension** to `ActionCardInfo` is small (one new property + view template change). Existing call sites stay binary — no churn elsewhere.
- The **VM-to-VM coupling** is via an event, not a direct call: `LiveTranscriptionViewModel` doesn't reference `AssistantViewModel`, matching the existing `CloseRequested` pattern.

## Data shapes

### Markdown front-matter (written by `MeetingTranscriptWriter`)

```yaml
---
schema: pia-meeting-transcript/v1
start: 2026-04-27T10:30:00+02:00
end:   2026-04-27T11:15:42+02:00
speakers:
  - You
  - Alice
  - Bob
originalFilename: transcript-20260427-103000.md
---
# Live Transcription — 2026-04-27 10:30
**Alice** _10:30:01–10:30:08_

Hi, thanks for joining…
```

The `schema` line lets future versions evolve the format. Speakers are de-duplicated and resolved to display names (`You` / labelled / `Speaker N`) at write time.

### `meeting_summary` memory object data (JSON in `MemoryObject.Data`)

```json
{
  "topic": "Q2 roadmap planning",
  "date": "2026-04-27",
  "speakers": ["You", "Alice", "Bob"],
  "originalFilename": "transcript-20260427-103000.md",
  "summaryKind": "bulleted",
  "content": "<the generated summary text>"
}
```

`MemoryObject.Label` is set to the LLM-generated `topic`. The `content` field holds the rendered summary so it is queryable via `query_memory` semantic search and visible in the Memory view.

### Multi-choice card payload (from `MeetingToolHandler`)

- **Description:** localized *"How should I summarize this meeting?"*
- **Choices:** `[("clean", "Clean text"), ("bulleted", "Bulleted by topic"), ("text", "Text summary")]`
- A fourth implicit "Cancel" choice (Escape / dismiss) declines the tool call.

### Tool schemas

```csharp
[Description("Summarize a saved meeting transcript file. Reads the file, prompts the user " +
             "to choose a summarization style, and returns the summary text.")]
private static string SummarizeMeetingTranscriptSchema(
    [Description("Path to the transcript markdown file. Environment variables like " +
                 "%APPDATA% are expanded.")] string filePath) => "";

[Description("Search saved meeting summaries by date range and/or speaker name. " +
             "Returns matching meeting_summary memory objects.")]
private static string QueryMeetingSummariesSchema(
    [Description("Optional ISO date (yyyy-MM-dd); inclusive lower bound")] string? from = null,
    [Description("Optional ISO date (yyyy-MM-dd); inclusive upper bound")] string? to = null,
    [Description("Optional speaker name (case-insensitive substring match)")] string? speaker = null) => "";
```

### Synthetic user message template

New localization key `Assistant_Meeting_SummarizeRequest`:

- **EN:** *"Please summarize the meeting transcript saved at `{0}`."*
- **DE:** *"Bitte fasse das Meeting-Transkript unter `{0}` zusammen."*
- **FR:** *"Merci de résumer la transcription de la réunion enregistrée dans `{0}`."*

## Summarization prompts

Three system prompts live in `MeetingToolHandler` (localized):

**Clean text:** *"You are a transcript editor. Take the meeting transcript below and produce a cleaned version: fix obvious speech-to-text errors, remove filler ('um', 'uh', repeated words), and normalize punctuation. Preserve speaker labels, timestamps, the original ordering, and the meaning. Do not summarize, condense, paraphrase beyond cleanup, or invent content. Output Markdown in the same `**Speaker** _time_` … format as the input."*

**Bulleted by topic:** *"Summarize the meeting transcript below in concise bullet points, grouped under these headings (omit any that are empty): **Decisions**, **Todos / action items** (with owner if mentioned), **Information**, **Open questions**. Use one bullet per fact; cite the speaker in parentheses where it adds clarity. Output Markdown."*

**Text summary:** *"Write a coherent prose summary of the meeting transcript below: 2–4 paragraphs covering what was discussed, decisions made, and any follow-ups. Refer to participants by name. Output plain Markdown."*

The transcript is sent as the user message; the front-matter is stripped before sending so the model isn't distracted. The provider used is whatever `IProviderService.GetDefaultProviderForModeAsync(WindowMode.Assistant)` resolves to.

### Plugin `systemPromptAddition`

> *"After producing a meeting summary, ask the user once whether they'd like to save it as a memory. If yes, call `create_object` with `type=meeting_summary`, `label=<topic distilled from the summary>`, and `data` as a JSON object with `topic`, `date` (from the front-matter), `speakers` (from the front-matter), `originalFilename` (from the front-matter), `summaryKind`, and `content` (the summary you produced). Do not save without explicit user confirmation."*

## Error handling

Only at boundaries — no over-defensive code elsewhere.

| Failure | Behavior |
|---|---|
| File doesn't exist / unreadable when tool is called | Tool returns *"Error: meeting transcript not found at `<path>`"* — LLM relays to user. |
| Front-matter missing / malformed | Tool falls back to body-only parse: speakers extracted from `**Name**` headers, date from filename. Logs a warning. |
| AI provider fails / cancels during summarization | Tool returns *"Error: summarization failed: `<reason>`"*. The multi-choice card is already resolved by the time generation starts, so no card is left dangling. |
| User dismisses the multi-choice card (Escape) | Treated as decline: tool returns *"User declined the summarize_meeting_transcript operation. Do not retry."* (matches existing decline pattern). |
| `query_meeting_summaries` returns nothing | *"No meetings found matching those criteria."* |
| File-write failure on Save and summarize | Snackbar shows error; the synthetic message is **not** sent. |

## Out of scope (YAGNI)

- Editing the summary before saving as memory — user can re-prompt.
- Re-summarizing with a different kind from the same card — re-invoke the tool.
- Batch summarization of multiple transcripts.
- Automatic save-as-memory; always requires explicit user accept on the `create_object` card.
- Background regeneration of front-matter for transcripts saved before this feature ships — the body-only fallback covers them.

## Testing

- **Unit:** `MeetingTranscriptWriter` (front-matter format, speaker dedup, missing end-time), `PathShortener` (round-trip, closest-match selection, case-insensitive Windows paths), `MeetingToolHandler` (tool dispatch, malformed-front-matter fallback, query-by-speaker/date filtering — fake `IMemoryService`, fake `IAiClientService`).
- **Manual:** UI flow (button → snackbar → synthetic message → multi-choice card → streamed summary → save-as-memory card) since it spans MVVM, the WPF dispatcher, and a real provider.

## Decisions log (for future reference)

- **Tool entry point:** synthetic user message in chat (not direct programmatic invocation). Keeps a single tool pipeline. Path is env-var-shortened for clean display.
- **Summary-kind picker:** multi-choice action card extension to `ActionCardInfo`. Reused for any future N-way decisions.
- **File location:** silent save to default folder; snackbar feedback. No file dialog.
- **Memory storage:** new `meeting_summary` type, one object per meeting (not a single appended list). Per-object embeddings make `query_memory` work; a dedicated `query_meeting_summaries` adds structured date/speaker filtering.
- **Speaker metadata path:** YAML front-matter in the saved markdown — durable, format-agnostic, robust to manual file edits.
