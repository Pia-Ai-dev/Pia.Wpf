# Saved meeting transcripts lose everything past the last ~200 bubbles

**Status:** Fixed, uncommitted
**Owner:** Marco Altmann
**Written:** 2026-09-18
**Origin:** User report — a 2 h 44 min Teams meeting on 2026-09-18 saved a transcript
containing only roughly the last hour; the beginning was missing. Evidence is the
support log `artifacts/pia-2026-09-18.log`.

## Symptom

The user attended a Teams meeting with the meeting attendee running, then saved the
transcript. The saved file starts partway into the meeting — the first hour of content
is absent. Nothing in the UI reports a loss.

## What the log shows

The session ran once, uninterrupted, and nothing failed:

| Time (local) | Event |
|---|---|
| 12:54:18 | `MeetingAttendee ViewModel: StartAsync invoked` |
| 12:54:52 | `Meeting attendee admitted to the call`, capture running 48 kHz → 16 kHz |
| 12:59 – 15:37 | `VAD: 20 s segment cap hit, flushing` throughout, no gaps |
| 15:38:37 | `Meeting attendee stopped` / `Transcript consumer stopped` |
| 15:39:20 | `Saved transcript to …\Playground\2026-09-18_meeting.md` |
| 15:39:32 | `Saved a meeting into the vault (54766 chars)` |

There is no restart, no reconnect, and no `WARN`/`ERROR` from any transcription
component in the whole file — the only four warnings all come from `PolicyService` at
startup. So nothing was dropped on the capture or recognition side: the audio was
transcribed, and the loss happens at retention/export time.

54 766 characters is about what one hour of continuous German speech produces, which
matches the user's "only one hour is there".

## Cause

`TranscriptOverlayViewModel` (`src/Pia.Wpf/ViewModels/TranscriptOverlayViewModel.cs`)
keeps the transcript in `Bubbles`, an `ObservableCollection<TranscriptBubble>` that is
deliberately a *rolling display window*:

```csharp
private const int MaxBubbles = 200;   // :36
private const int TrimBatch  = 20;    // :37

private void TrimIfNeeded()           // :313
{
    if (Bubbles.Count <= MaxBubbles) return;
    for (int i = 0; i < TrimBatch && Bubbles.Count > MaxBubbles - TrimBatch; i++)
        Bubbles.RemoveAt(0);
}
```

`TrimIfNeeded` runs on every utterance, so the collection oscillates between 181 and 201
entries and the oldest bubbles are destroyed as the meeting goes on.

Both export paths then render from that same trimmed collection:

- `BuildMarkdown()` (:627) — `foreach (var bubble in Bubbles)` — the file save.
- `SaveToVaultAsync()` (:568) — `DirectTranscriptMarkdown.RenderBody(…, [.. Bubbles], …)`
  — the vault save.

A display cap is therefore silently acting as an export cap. Bubble spans are bounded by
`TranscriptGrouping.BubbleWindowSeconds = 25` measured from the bubble's *start*, plus
the trailing segment, so a bubble covers at most ~45 s; 200 of them is an upper bound of
~150 min, and at a realistic ~18 s average it is ~60 min. The user's observation sits
squarely in that range.

## Blast radius

- **Both** saves from this session are truncated — the OneDrive `.md` *and* the vault
  source. The vault copy was handed to `_ingestScheduler`, so the knowledge-base page
  built for this meeting also only covers the last hour.
- `DirectTranscriptionViewModel` derives from the same base, so direct transcription has
  the identical defect.
- `MeetingAttendeeViewModel.BuildSummaryPrompt` appends `BuildMarkdown()`, so "summarize
  with assistant" on a long meeting silently summarizes only its tail.
- The two exports disagree about the session start, which is a visible tell:
  `BuildMarkdown` puts `_sessionStart` (12:54) in the `#` heading while the first entry
  below it is ~14:3x; `RenderBody` instead derives its heading from
  `bubbles[0].StartTimestamp`, so the vault copy's heading is the *truncated* start while
  its YAML front matter still carries the real one.

`ScheduledMeetingRecorder` is the exception and confirms the intent: it collects the raw
utterance stream into its own `MeetingJournal`, whose `_entries` list has **no cap**, and
renders from that. Unattended scheduled recordings keep the whole meeting; only the
interactive overlays truncate.

## The lost hour is not recoverable

The only other retention is `_journal`, which is in-memory, capped at
`JournalCap = 1000` and front-trimmed the same way; `RebuildBubblesFromJournal` re-trims
to `MaxBubbles` regardless. Nothing is written to disk during the session. The first hour
of that meeting is gone.

## Verification on the user's file

Without reading any content, from `…\Playground\2026-09-18_meeting.md`:

- Timestamp on the first `**Speaker** _HH:MM:SS…_` line — predicted ≈ 14:30–14:45.
- Count of those lines — predicted 181–200.

A count far below 181 would mean something else is also in play.

## Fix shape

Keep `Bubbles` capped — the cap exists so the overlay's `ItemsControl` stays responsive
over a long meeting, and that reason is still good. Decouple *export* from it:

1. Retain every utterance for the session, not the last 1000. `JournalCap` has to grow or
   go: 2 h 44 min of VAD segments is already on the order of 1000, so a journal-backed
   export that kept the cap would still truncate long meetings.
2. Render exports by replaying that record through the existing
   `GetOrCreateBubble` / `Append` path into a throwaway list, so grouping stays identical
   and the rebuild-vs-incremental equivalence the class already relies on still holds.
   `ScheduledMeetingRecorder.MeetingJournal` already does exactly this and is the model to
   follow — possibly by hoisting it so both paths share one implementation.
3. `SaveToVaultAsync`'s `sessionEnd` and `RenderBody`'s heading should come from the full
   record, not `Bubbles[^1]` / `bubbles[0]`.

Regression test: feed more than `MaxBubbles` worth of utterances, assert `BuildMarkdown()`
still contains the first one. Nothing locks this behaviour today — no test references
`MaxBubbles`.

Memory footprint is the open question: an unbounded journal for a very long session is
the reason the cap was set at all, so decide whether "whole session in memory" is
acceptable or whether the record needs to spill to disk.
