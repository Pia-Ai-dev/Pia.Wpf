# Background Assignments — Client Surface

> Server-side implementation: `docs/pia-mesh/2026-08-12-mesh-d2-decrypt-in-gate.md` and the release
> checklist `docs/pia-mesh/CHECKLIST.md` (item D2.4) in the **Pia repo** (`C:\projects\Pia`), shipped on
> `feature/connector-abstraction-phase1`. This document is the client half and is self-contained: everything
> needed to build it, including the consent requirements, is restated here.

The service half is **done** — `src/Pia.Wpf/Services/Operators/`, commits `59607e07`, `faddf160`, `bc24f510`
on `feature/agent-run-spine`. What remains is the UI.

## What the feature is

Pia hands a piece of work to the user's Pia server to run in the background — a research answer or a written
brief — grounded in records the user picks from their own data. It survives the app closing, and the answer
comes back as an ordinary assistant chat.

It matters because everything else in Pia is end-to-end encrypted and the server cannot read it. This one path
cannot work that way: whatever runs the work has to read what it is working on. So it is a declared, consented
crossing, and the UI is where the "consented" part actually happens.

## What already exists, and the seams to build against

| Seam | What it gives you |
|---|---|
| `IAssignmentApiClient.GetSurfaceAsync()` | `AssignmentSurface.Hidden` for no server, no token, 401/403/404, or an empty skill list. Otherwise the skills this user may run, each with `Mode` and `DeclaredInputTypes` |
| `IAssignmentScopeResolver.ListAsync(declaredTypes)` | The offerable records as `AssignmentScopeItem` — entity type, id, title, character count, last-modified, `ExceedsItemCap`. **Metadata only** |
| `IAssignmentConsentStore.RecordAsync(skill, mode, items)` | Writes the consent record, awaits the disk, and returns the receipt. **The only source of a receipt** |
| `IAssignmentRunOrchestrator.StartAsync(request, receipt, ct)` | Reads the content, sends it, remembers the run. Refuses without a matching receipt |
| `IAssignmentApiClient.ListAsync(skip, limit)` | This user's runs: status, step count, spend, timestamps. No artifact, by design |
| `IAssignmentRunOrchestrator.CancelAsync(id)` | Stops a live run. `false` means there was nothing to stop |
| `IAssignmentPendingStore.GetJournalAsync()` | This device's runs including collected ones — the prompt and the chat id |
| `AssignmentRunOrchestrator.Completed` | Fires once a run is stored locally AND acknowledged. Ids only |

The pull, the local write, the acknowledgement and the recovery of a run that finished while the app was
closed are already handled by `AssignmentDrainService`. The UI never touches them.

## Decisions

**1. Two surfaces.** An entry point that is *absent* unless the surface is available, and a navigable view
listing this user's runs. The dialog is the security boundary; the view is why the feature does not feel like
work vanishing into a hole.

**2. Progress is `status` + `stepCount`, never a percentage.** A `brief` run really does walk 0 → 3 passes, so
the count is honest progress. A `research` run is one step and is honestly just "running". A bar that invents a
fraction from a step count becomes a lie the moment a skill's pass count changes.

**3. The list is the server's rows joined to the local journal.** The server answers "what state is my run in";
only this device knows what was asked and which chat holds the answer — the prompt travels inside the input the
server drops at 72 hours, and the list projection never carried it. A server row with no journal entry (another
device's run) still renders, just without *Open chat*.

**4. Polling belongs to the view while it is visible.** `AssignmentDrainService` already polls what is
outstanding and costs nothing when idle. The view refreshes while shown and stops when hidden — a list nobody
is looking at must not keep a poll alive.

**5. Cancel is offered.** A cancelled run still lands terminal, so its result is still stored and still
acknowledged: cancelling stops the work, it does not abandon the plaintext.

## The consent dialog — the requirements, in full

All of it on **one screen**, at the moment of affirmation. Not a wizard: a manifest on a page the user already
left is not consent.

- [ ] **Every record by name** — entity type, title, character count, last-modified. Not a count, not a
      category. The named list is the whole point.
- [ ] **The skill and its mode**, and the destination in plain words: this leaves end-to-end encryption and is
      stored unencrypted on your Pia server.
- [ ] **The honest retention numbers**, which are *not* the 30-day row retention: the plaintext is gone at most
      **72 hours** after the run finishes, whether or not this device ever collects it. What survives longer is
      the run's metadata — status, step count, token spend — with no plaintext in it.
- [ ] **The residual, stated rather than implied:** someone with server access can read the plaintext while it
      is there. That is the trade this plane makes.
- [ ] **An affirmative act** — a checkbox plus a labelled action ("Send unencrypted"), never a default-focused
      OK.
- [ ] **No "remember this choice."** The record set differs per assignment; a remembered blanket consent is
      exactly the data-minimisation defeat this gate exists to prevent.
- [ ] **An over-cap record is shown as unsendable, not truncated.** A user who affirms sending a record and
      sends a fifth of it was not asked the question they answered. Same for the running total against
      `AssignmentInput.MaxItems` (20) and `MaxTotalItemChars` (32 000) — a long conversation or session blows
      the 8 000-character per-item cap routinely.

The local consent record is already written by `JsonlAssignmentConsentStore` (metadata only — entity type, id
and size, never a title or content).

## Work shape

1. `AssignmentsViewModel` + `AssignmentsView` — rows joined from `ListAsync` and `GetJournalAsync`, a status
   pill, step count, elapsed, *Open chat* on a collected row, *Cancel* on a live one, an empty state that
   invites the first run, and a refresh that runs only while the view is shown.
2. Navigation entry, hidden unless `GetSurfaceAsync()` says available.
3. `AssignmentConsentContentDialog` + ViewModel + one `IDialogService` method, per the checklist above.
   Reachable from the list view's primary action and from the assistant input's action row.
4. Strings in `Resources/Strings/*.resx` for **en, de and fr**. `LocalizationTests` fails on a missing
   translation *and* on an orphaned one.
5. Completion toast off `AssignmentRunOrchestrator.Completed`, mirroring `ScheduledJobNotificationSurface`,
   with *Open chat* routing on the chat id.

## Testability

| Step | What proves it | What would make it vacuous |
|---|---|---|
| Row mapping | `AssignmentsViewModel` tests | Asserting a row exists. Assert a collected row offers *Open chat* and a queued one does not, and that a server row with no journal entry still renders |
| Polling lifecycle | ViewModel test | Asserting a refresh happens. Assert it STOPS when the view is hidden |
| Consent gating | ViewModel test | Asserting the dialog appeared. Assert the primary action is disabled until the checkbox, that an over-cap item cannot be selected, and that the receipt is minted before `StartAsync` |
| No blanket consent | Reflection or grep test | Nothing. Pin that no setting persists a consent decision — this is the one requirement a later "convenience" change will quietly add |

Gate: unfiltered `dotnet test` (`failed: 0`) plus zero warnings on `-t:Rebuild` in Debug **and** Release.
`NamingConventionTests`, `MvvmPatternTests` and `LocalizationTests` all bite here — the first one already
renamed this feature's service classes once.

## Out of scope

- A progress percentage or determinate bar (decision 2).
- Another device's run in detail: its artifact went to that device's chat store and its prompt was never here.
- Re-running or editing a finished assignment — a new run is a new consent decision, by design.
- Anything that remembers a consent choice.
