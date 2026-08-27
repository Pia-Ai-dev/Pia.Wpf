# Timed Teams meeting attendance

**Status.** Built on `feature/scheduled_teams`; the Windows end-to-end round in §7 is still owed by a human.
**Owner.** Marco Altmann.
**Written.** 2026-08-27.
**Origin.** "Set up a Teams link with a time when Pia should join the meeting in the background."

## The problem

Pia could already attend a Teams meeting, but only with someone sitting there to start it: open the
overlay, paste a link, tick consent, click Join. Everything downstream of "here is a URL" was shipped —
a Playwright/Chromium browser joins anonymously, taps the page audio silently, runs local VAD + STT +
adaptive diarization, snapshots the roster, and notices the meeting ending on its own.

Separately, Pia had a mature scheduler: persisted `NextFireAt`, recurrence, owner-device pinning, a 30 s
poll, dispatch-not-await semantics, failure strikes.

The two had never been connected, and the missing piece was not the join — it was that **Save is a
button**. On an unattended run nobody clicks it.

## What was built

A third `ScheduledJobKind`, a dispatch leg for it, and a headless recorder that does what the overlay's
Save button does.

### 1. Model and storage

`ScheduledJobKind.MeetingAttendance` (2) — appended, because the enum is persisted and crosses the wire
as an int.

Two device-local columns on `ScheduledJobs`, following the `BlueprintKey` precedent (model property →
`CREATE TABLE` → `PRAGMA`-guarded `ALTER TABLE` → `CreateAsync`/`UpdateAsync` → `MapJob`):

- `MeetingUrl` — the join link.
- `MeetingConsentAckAt` — when recording was acknowledged; null means the job refuses to join.

**Meeting jobs never sync.** Both columns are absent from `SyncScheduledJob`, and
`SyncClientService` filters `Kind == MeetingAttendance` out of *both* push sites. Two reasons, and either
alone is sufficient:

- A Teams join link is a bearer token for the meeting, and `Query` is plaintext on the wire unless E2EE
  happens to be on. `16-event-trigger-design-note.md:54` had already ruled the meeting URL unstorable raw.
- `Kind` crosses as an int, so a peer on an older build would cast the unknown `2` and fall through
  `ExecuteJobAsync`'s ternary into the **Research** leg, running the meeting's title as a research query.

`SyncMapperNewEntitiesTests.ScheduledJob_MeetingLink_NeverReachesTheWire` serializes the DTO and asserts
the link is not in it, so a field added later cannot leak it silently.

### 2. Dispatch

`ExecuteJobAsync`'s ternary became a switch. Before it, in `RunJobAsync`, a meeting more than
**5 minutes** late is skipped outright: the generic 15-minute grace ends in a dialog, which is wrong
twice over here — joining most of the way through captures little, and the person who would answer is in
the meeting. The occurrence is spent (`AdvanceMissedRunAsync`), so tomorrow's standup still fires.

`ExecuteMeetingAttendanceAsync` refuses, each with its own reason, when the attendee is policy-disabled,
consent was never acknowledged, or the link is missing or not a Teams URL. Every refusal is checked
**before** a slot is taken and before the schedule moves on, so a misconfigured job neither starves the
pool nor burns its occurrence.

Then it takes a session (§3) and dispatches, like the other two legs — a meeting runs for an hour and a
tick must not wait for it.

### 3. Concurrency, and why it is safe

Scheduled meetings do **not** run on the overlay's shared attendee. That singleton holds one session, one
`SingleReader` utterance channel and one end-watch loop, and refuses a second start. `IBackgroundMeetingSessions`
hands out a fresh `MeetingAttendeeService` per meeting, bounded by
`AppSettings.MaxConcurrentBackgroundMeetings` (JSON-only, default 2, values below 1 read as 1).

Every session it hands out is `SilentCaptureOnly`, which is the load-bearing part. It forces three things:

- the browser window stays hidden regardless of `MeetingAttendeeShowBrowserWindow`,
- capture goes through the in-browser Web Audio tap, which is **per page** and leaves the meeting muted,
- and a silent-capture failure **fails the join** instead of degrading to endpoint loopback.

That last one is the whole reason concurrency works. Endpoint loopback records the default render device's
whole mix, so two meetings degrading to it would each transcribe both meetings — and a "silent" session that
degraded would start playing out loud. The degrade is still there for the overlay's own meeting, where it is
the right trade (hidden but audible beats silent and untranscribed).

Because sessions are silent and per-page, there is no contention with the overlay's meeting or with direct
transcription's microphone and loopback, so the dispatcher no longer refuses on either. What is bounded is
CPU and memory: each session runs its own VAD, STT engine and diarizer.

The slot is taken by `TryAcquireAsync` **before** the schedule moves on, and the acquire itself is the
reservation — a capacity check followed by a later acquire would let two meetings coming due on the same
tick both pass it. A meeting that finds no free slot is not failed and not skipped: nothing is written, so
the occurrence stays due and the next tick retries. If nothing frees up inside the join window the lateness
gate skips it, which beats retiring a standup because another meeting overran.

The lease owns the session's lifetime. Disposing it disposes the attendee (browser, models, loops) and only
then returns the slot, so a fresh acquire can never race a teardown.

### 4. The recorder

`ScheduledMeetingRecorder` collects before joining (an utterance produced between the join completing and
a later subscribe would be lost), applies retroactive `SpeakersReassigned` corrections, and on meeting end
renders through the same helpers the overlay uses and writes to the vault, then triggers ingest.

Two waits, both hardcoded constants, both settable only so tests need not sit them out:

- **60 s lobby retry, once.** The usual reason nobody admitted the attendee is that the organiser had not
  started the meeting. A second timeout is a different problem. It is precise, not string-matched:
  `TeamsMeetingSession` now throws a typed `MeetingAdmissionTimeoutException` instead of a bare
  `TimeoutException`, so an unrelated join failure is not retried. Safe as written — the failed start
  already tore its browser down and left the service in `Error`, which `StartAsync` accepts.
- **5 s drain grace**, then whatever is still buffered is taken. The channel completes only on the
  service's own disposal, so the collector loop would otherwise never return.

`NothingCaptured` (attended, nobody spoke) completes the job rather than failing it — booking it as a
failure would spend a strike on a meeting that worked.

### 5. Vault transcripts moved — this changed existing behaviour

`MeetingVaultMarkdown.BuildReference` now returns `sources/transcripts/meeting-<ts>-<slug>.md`. This
applies to the **manual** Save-to-vault flow too; they share the one helper, which is why it was cheap.

Nothing else needed changing, and this was verified rather than assumed:
`MemoryService.TryResolveSourceScope` only requires a `sources/` prefix; `CreateSourceAsync` already
calls `Directory.CreateDirectory`; `AutoIngestService` watches with `IncludeSubdirectories = true` and
reconciles with `AllDirectories`; and the `meeting-followup` blueprint hardcodes no path — it routes
`recall` → `read_topic` → `read_source`.

Meetings already saved under `sources/` stay where they are. They remain readable and ingested, so there
is no migration.

### 6. A SingleReader hazard, closed

`MeetingAttendeeViewModel.StartAsync` attaches its utterance consumer **before** calling the service, on a
channel documented `SingleReader`. If a scheduled meeting was attending and the user clicked Join, a
second reader attached and silently stole utterances from the recorder before the service refused.

`CanStart` now also requires the service to be `Idle`/`Error`, and the ctor seeds its state from
`_service.State` rather than assuming idle — a window opened mid-meeting gets no `StateChanged` to correct
it.

### 7. UI and notification

Routines gained the third kind. A meeting shows a link field and a consent tick; the goal, provider,
persona, effort and tool-grant fields are hidden, because none of them mean anything when the "run" is a
browser sitting in a call. Saving a meeting routine pre-provisions Chromium — the first-ever meeting would
otherwise spend its join window downloading a browser.

`IScheduledJobNotificationSurface.NotifyMeetingSaved` is separate from `NotifySuccess` because a meeting
produces a vault source, not a chat: an "Open chat" button would be dead. It honours quiet mode.

**Trap worth remembering:** `RecurrenceCalculator` treats `Once` *without* a `SpecificDate` as a daily
clamp — it repeats forever. A one-off meeting must always write `SpecificDate`.

## 8. What a human still owes

`dotnet test` cannot execute on the author's Mac (net10.0-windows), so the suite has been compiled but not
run; both projects build with **0 warnings** in Debug and Release.

1. `dotnet test` with no filter — the gate is `failed: 0`.
2. Create a meeting routine on a real Teams link one minute out, tick consent, leave the app idle. Confirm
   the browser joins headlessly, and that on hangup a `sources/transcripts/meeting-*.md` appears with
   attendees in its front matter and a Flow card fires. Then confirm `recall` finds it — that is what
   proves ingest followed the transcripts into the subfolder.
3. Lobby retry: schedule against a meeting that has not started, leave it past the 120 s timeout, admit it
   during the second attempt. Expect one retry, ~60 s apart.
4. Lateness: schedule one 10 minutes in the past, restart, confirm it is skipped with a notification and
   that **no** missed-run dialog appears.
5. Concurrency: schedule two meetings for the same minute. Expect two hidden browsers, two transcripts,
   and neither transcript containing the other meeting's speech. Then set
   `MaxConcurrentBackgroundMeetings` to 1 and confirm the second waits and is skipped at the join window.
6. No contention: start a manual meeting or direct transcription, let a scheduled one come due. Expect it
   to join anyway, silently, without disturbing either.
7. Manual Save-to-vault still works and also lands in `sources/transcripts/`.
8. Sync: pair a second device and confirm the meeting job does not appear there.

## Limitations accepted up front

- **Lobby.** The retry covers the common case. If nobody ever admits the attendee, the job fails after the
  second timeout and there is no one to intervene. Unavoidable without an authenticated identity.
- **Anonymous join.** The attendee joins as a guest. Meetings locked to authenticated org members reject
  it. Already true of the manual flow; scheduling makes it more visible.
- **Machine state.** A sleeping or powered-off machine does not join. Wake timers are out of scope.
- **No live view.** A scheduled meeting runs on its own session, which the overlay is not attached to, so
  it shows nothing until the transcript lands. Worth revisiting.
- **The concurrency ceiling is a guess, not a measurement.** The default of 2 was chosen for caution; two
  real-time STT streams plus diarization on one machine has not been benchmarked here.
- **Shutdown mid-meeting drops the transcript.** The save runs after the meeting ends; quitting Pia while
  a scheduled meeting is being recorded abandons what was collected. Writing to the vault under a
  cancelled token is its own hazard, so this was left rather than half-solved.
- **No calendar.** The link and the time are typed in. There is no Graph/MSAL/calendar layer anywhere in
  the repo, and building one is its own workstream.
