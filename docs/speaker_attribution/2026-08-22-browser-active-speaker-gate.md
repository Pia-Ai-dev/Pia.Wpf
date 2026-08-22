# Plan: gate the "read Teams' own speaker indicator" idea with a DEBUG probe

**Status.** Planned, not started. The probe is buildable now; the finding needs one real meeting.
**Owner.** Marco Altmann.
**Written.** 2026-08-22.
**Origin.** The "Teams DOM active-speaker signal" lever in
`../reviews/2026-08-21-speaker-attribution-assessment.md`, raised again 2026-08-22 as "we put so much
effort into audio-based speaker recognition — can we capture the speaker indicators in the browser?".
Scope was cut to the gate in that conversation: consent-phase enrollment stays the next accuracy lever,
so this work banks the browser finding cheaply instead of building on it.

## The question, and the short answer

Attribution today is audio-only: VAD segments → embeddings → online clustering → `Speaker N`. The
measurements in [2026-08-21-speaker-attribution-measurements.md](2026-08-21-speaker-attribution-measurements.md)
say the mechanics are sound but the labels are not trustworthy enough to put a name on, and the errors
land at turn boundaries. Meanwhile the meeting attendee *is* a real Chromium page Pia drives through
Playwright, and Teams itself renders who is speaking — the same notion the fixture answer key is
extracted from offline, at about an hour of hand-measured layout per recording (see
[speaker-attribution-fixture-playbook.md](speaker-attribution-fixture-playbook.md)).

So: yes, reading it live is a real option, and every piece of plumbing it needs already exists. What
does not exist is evidence that the signal is there *in the configuration Pia runs*, or that it arrives
early enough to be worth anything. This plan buys that evidence and nothing else. No production
behaviour changes, no labels move.

## What already exists (reuse, do not rebuild)

| Piece | Where | Why it matters |
|---|---|---|
| Playwright page, serialized access | `_pageGate`, `TeamsMeetingSession.cs:327` | one page op at a time; a 300 ms poll would fight the 2 s hangup poll |
| page → host push channel | `AudioBindingName` / `ExposeFunctionAsync`, `TeamsMeetingSession.cs:579` | the exact pattern for an event stream that needs no polling |
| in-page init hook | `AudioHookInitScript`, `TeamsMeetingSession.cs:145` | already runs before Teams' first `RTCPeerConnection`; a second init script is free |
| unverified-selector discovery | `RosterDomScript`, `TeamsMeetingSession.cs:126` | "dump the DOM once at DEBUG, refine the selectors from it" is established here |
| roster names + cleanup | `RosterNamesScript` `:97`, `CleanAttendeeName` in `MeetingAttendeeService.cs:611` | the name vocabulary an indicator would be matched against |
| retroactive relabelling | `Rename` / `SpeakersReassigned`, `ISpeakerIdentificationService.cs:43,71` | a cluster can be renamed after the fact, live bubbles included |
| roster → diarizer ceiling | `SetExpectedSpeakers`, `MeetingAttendeeService.cs:601` | precedent for "a browser observation feeds the diarizer" |
| anti-throttle launch flags | `TeamsMeetingSession.cs:741-744` | `--disable-renderer-backgrounding` and friends are already set, so a hidden window still animates |

## The three gate questions

**1. Does a per-participant speaking indicator exist where Pia can see it?**
The only hint in the code is the `voice-level` tid filtered out as noise at `TeamsMeetingSession.cs:110`
— and it appears in the *on-stage-tile* fallback branch, not the People-panel path. Pia's roster loop
deliberately opens the People panel (`EnsureRosterOpenAsync`). So the probe must report: is there an
indicator on the People rows, or only on stage tiles; do tiles still render and animate with the panel
open, camera-off, in a hidden window; and are tiles virtualized, so a camera-off speaker has no tile at
all. If it turns out to be tiles-only *and* the panel displaces them, that is a design fork for Phase 1
(alternate the panel, or take names from tiles and stop opening it) — not a detail to discover later.

**2. What is the DOM→audio offset?** A few hundred ms of lag puts its errors exactly at turn
boundaries, which is where attribution is already weakest. Measure it with a sign and a spread; do not
assume zero.

**3. Was the clock trustworthy for that measurement?** `BrowserAudioCaptureService` writes hops into a
bounded channel with `DropOldest` (`:57`, `:141`), *upstream* of the VAD's sample counter. A dropped hop
therefore shifts every later `VadSegment.StartSample` against page time, permanently and cumulatively,
and nothing downstream flags it. An offset measured across a window with drops is fiction, so the drop
count has to be printed next to the number.

## What to build

### 1. In-page probe — DEBUG only, off unless the env var is set

In `TeamsMeetingSession.cs`, following the existing patterns:

- **Env var** `PIA_DEBUG_MEETING_ATTENDEE_SPEAKER_PROBE`, declared beside its siblings in
  `Bootstrapper.cs:33-39`, same comment style.
- **Init script** added alongside `AudioHookInitScript` so it runs before Teams' first render: a
  `MutationObserver` tracking a candidate list — `[data-tid*="voice-level" i]`, `[class*="speaking" i]`,
  `[aria-label*="speaking" i]`, plus each hit's owning `[role="menuitem"]` tile or
  `[data-tid^="attendeesInMeeting-"]` row — emitting one event per on/off transition.
- **The observer must be cheap, or it invalidates its own measurement.** An unfiltered attribute
  observer over a heavy SPA's whole tree burns CPU in a renderer already running WebRTC decode, the
  `ScriptProcessorNode` tap, and the mute sweep's own subtree observer plus 1 s interval
  (`beginMuting`). CPU contention is exactly what manufactures dropped hops — trap 6 in the playbook
  records it happening. So: an `attributeFilter` of `class` / `data-tid` / `aria-label`, and an observer
  root scoped to the roster panel or tile container once located, falling back to `documentElement` only
  until then. **Pass condition: zero dropped hops across a probe run**, not merely a reported field.
- **Push, never poll.** Events leave through a `__piaSpeakerProbeSink` binding cloned from
  `AudioBindingName`. After arming, the probe touches `_pageGate` never again, so the hangup poll and
  the roster snapshot are undisturbed. Debounce in-page (Teams paints the indicator at animation rate)
  and cap the event rate.
- **Stamp events on the audio clock:** the same `AudioContext.currentTime` the PCM tap uses
  (`window.__piaCtx`), plus that context's time reported once at the first PCM chunk. Fall back to
  `performance.now()` when the silent-capture path is not in use, and say which clock was used in the
  log line. A run that degraded to endpoint loopback (`MeetingAttendeeService.cs:315`) has no
  `window.__piaCtx` *and* took a different pipeline with its own buffering and an extra D/A→A/D pass:
  its offset is not comparable and must be discarded, not averaged in.
- **One-shot markup dump**, modelled on `RosterDomScript`: the surrounding subtree of every candidate
  hit, whether the People panel was open, and how many stage tiles existed at that moment. This is what
  answers question 1. Truncated, and `SensitiveDebug` — participant names are user-named items, as the
  roster loop already models at `MeetingAttendeeService.cs:562`.
- **Never fails the meeting.** No candidate anywhere is one "no indicator found" line, not an error —
  same contract as `GetAttendeeNamesAsync`.
- **Summary line at stop:** events seen, distinct names, whether those names matched the roster union,
  and the audio source's dropped-hop count.

### 2. Measure the offset onset-to-onset

The gate number is **DOM on-transition → speech onset**, nothing else. An overlap-maximising sweep over
intervals would fold three lags into one figure and could wrongly close the door on per-segment
attribution later:

- a segment's `start=` is **preroll-anchored, not onset-anchored** — `SileroVadDetector` backdates it by
  up to 16 windows (~512 ms) of preroll, and by *less* than that when the preroll buffer has not
  refilled, i.e. precisely on back-to-back turns (`SileroVadDetector.cs:170`);
- the trailing edge carries the 512 ms silence hysteresis (`SilenceWindowsToEnd`).

So add the stream position and the actual preroll count to the VAD's existing OPEN/CLOSE debug lines
(`SileroVadDetector.cs:176`) — plain `LogDebug`, no user content — and measure against the OPEN
position. Then `scripts/Measure-DomSpeakerOffset.ps1`, in the style of
`scripts/Measure-SpeakerAttribution.ps1`:

- pair each DOM on-transition with the nearest VAD OPEN; print the median offset and its spread as the
  gate number, discarding pairs further apart than a stated bound rather than letting them skew it;
- keep an interval-overlap figure only as a secondary sanity check, labelled as containing preroll and
  hysteresis;
- print the dropped-hop count beside both, and refuse to print a gate number at all when it is non-zero.

### 3. Verification

- `dotnet test` at `failed: 0`; `dotnet build -t:Rebuild -v:n` at `0 Warning(s)` in Debug **and**
  Release — the probe is `#if DEBUG`, so the Release pass is what catches an unused-symbol slip.
- Unit: the PS1's parser and offset pairing against a synthetic log fixture, including the no-indicator
  case and a log with drops.
- Off by default: `TeamsMeetingSession` has no unit seam (it launches real Playwright), so make the
  arming decision a pure `internal static` — env var to bool — and assert *that*. Otherwise this item
  quietly degrades from a test into an intention.
- Live, one real meeting with the env var set: read the dump for question 1, run the PS1 for questions 2
  and 3, then write the findings into this doc. "No indicator exists in the configuration Pia runs" is a
  complete and valuable answer that stops the follow-on.

## Findings

Not yet run. Record here: which surface carried the indicator (People row / stage tile / neither),
whether tiles survive the panel being open, the onset offset with its spread, the dropped-hop count, and
the clock used. Then set this doc's Status to the decision the numbers force.

## The follow-on this gates

Do not start any of it before the findings above exist.

**Phase 1 — promote the probe.** One new member,
`IMeetingSession.StartActiveSpeakerCaptureAsync(Action<ActiveSpeakerEvent>)`, with the probe's observer
as its implementation. Touches `TeamsMeetingSession`, `DebugNoOpMeetingSession`, the `IMeetingSession`
fake in `tests/Pia.Wpf.Tests/Services/MeetingAttendee/MeetingAttendeeServiceStateTests.cs`, and
`DiRegistrationTests` if the registration shape changes.

**Phase 2 — name clusters, not segments.** Accumulate a name → intervals timeline in stream time beside
the roster union `MeetingAttendeeService` already keeps. At each adaptive re-cluster pass and once at
meeting end: for every cluster, keep only the segment spans the timeline calls unambiguous (exactly one
name active, with an edge margin absorbing the measured offset), majority-vote a name subject to a
minimum share and a minimum number of voting spans, and apply it through the existing `Rename` +
`SpeakersReassigned` path — live bubbles, the saved transcript and the summary prompt then all follow
for free. A cluster with no confident winner keeps `Speaker N`. Two clusters winning the same name means
the diarizer over-split, and is fine.

Per-segment override stays out of scope. The assessment's "wrong labels are worse than no labels"
section applies directly: DOM lag deposits its errors at turn boundaries, which is where attribution is
already weakest, so a per-segment override would amplify the failure mode already measured. Revisit only
if the measured offset is small enough to justify it.

**Prerequisites Phase 2 inherits.** The `DropOldest` drift needs a real fix before any of this can be
trusted: keep a `(deliveredSamples, cumulativeDroppedSamples)` splice map in
`BrowserAudioCaptureService`, map segment starts back through it, and refuse to name at all once drops
pass a threshold. And the consent interaction has to be decided explicitly, not in passing: a
DOM-derived name must not promote a cluster's consent state, and `SpeakerConsentEntry` is keyed by
label, so a rename must re-key rather than duplicate — the existing rename UX is the precedent.

**The byproduct, stated honestly.** A live timeline yields a free answer key for *future live* meetings
in the shape of `scripts/speaker-reference/*.reference.json`, which is the hour the playbook spends per
recording. It does not retro-fix existing recordings — there is no live page during
`Invoke-MeetingReplay.ps1` — and it comes from Teams' own active-speaker notion, the same source as the
burned-in pill, so it is a cheaper route to the same ground truth rather than an independent check on it.

**Selectors are perishable.** The roster extractor's own comment records one round of `data-tid` death
already ("the older roster-list-item / roster-list-title selectors no longer exist"). Carry several
candidates, refine from the dump, and make total absence a no-op rather than an error.
