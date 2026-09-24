# Consent retention: 14 days, swept at start and daily

**Status:** Implemented
**Owner:** marco.altmann@neo42.de
**Written:** 2026-09-09
**Origin:** Owner decision reversing D-4 of
`docs/superpowers/2026-08-03-direct-transcription-design.md` ("Consent evidence is write-only in
v1 — no expiry stamp, no cleanup worker and no retention service. The retention question (old spec:
3 years) is deferred to v2.")

## The decision

Speaker-consent data is deleted once it is **older than 14 days**. That is a deliberate move away
from the 3 years the old spec named. It trades the length of the Art. 7 GDPR Nachweispflicht window
for holding far less data: after 14 days Pia can no longer prove a given speaker consented to a
given session. The owner made that call knowing it; it is not an oversight to be "fixed" by raising
the number back up without asking.

The window is a constant, `ConsentRetention.DefaultRetainedDays`. There is no setting, the same way
`LogFileRetention` has none — a user-tunable retention window is itself a compliance surface.

## What gets deleted

Both stores, in one pass:

- `%LOCALAPPDATA%\Pia\ConsentEvidence\{sessionId}\` — one DPAPI-protected file per speaker for the
  grant, plus any `*.revoked.json` beside it.
- `%LOCALAPPDATA%\Pia\ConsentAudit\session_*.jsonl` — the per-session metadata audit trail.

Two constraints shaped the implementation, and both have a test:

1. **The session directory goes too, not just the files in it.** `ConsentEvidence\{sessionId}\`
   left standing as an empty folder still holds the session id, which is a data point of its own.
   The delete is `Directory.Delete(recursive: true)`. This also covers the folder a failed grant
   write leaves behind: `SaveGrantAsync` calls `CreateDirectory` *before* the write, so a DPAPI
   failure leaves an empty directory with no file to date it — it ages out on the directory's own
   stamp.
2. **`assignments.jsonl` must survive.** `JsonlAssignmentConsentStore` writes operator-assignment
   consent receipts into the *same* `ConsentAudit\` folder with a different lifecycle. The audit
   sweep therefore matches `session_*.jsonl` by name, never `*.jsonl`.

A session's age is the **newest** write under its directory, the directory's own stamp included —
not the oldest. A revocation recorded on day 13 holds the whole session for another 14 days, which
is right: the revocation is the more recent processing record of the two.

There is no reader and no expiry stamp inside the files, so the dates on disk are the only age
signal available without decrypting. Nothing here reverses D-4's other half: evidence is still
write-only.

## Where it runs

- **Start** — `Bootstrapper.InitializeAsync`, immediately after `LogFileRetention.Sweep`, before the
  service provider is built. **This is the load-bearing path.** An install that runs for an hour a
  day never reaches a daily timer, and a machine that sits idle for two weeks would otherwise never
  clear anything. The sweep swallows `IOException`/`UnauthorizedAccessException` at every level so it
  can never fail a launch, and logs counts only — a session id and a speaker label are data.
- **Runtime** — `ConsentRetentionBackgroundService`, a 24 h `PeriodicTimer`, started and stopped in
  `App.xaml.cs` beside `AssistantChatRetentionService`. It deliberately does **no** pass before its
  first tick; the start-up sweep has already run seconds earlier. Its only job is to stop a session
  left open for weeks from drifting past the window.

The live session's own audit file is held open by the `JsonlConsentAuditLog` singleton for the
process lifetime. Its stamp is fresh, so it is never a candidate — but if a session ever did run
past the window with no further events, the delete fails and lands in the outcome's `Skipped` count.
The next start collects it.

## A note on UI-test replays

`PiaPaths.ConsentEvidenceDirectory` and `ConsentAuditDirectory` resolve against the **real**
`%LOCALAPPDATA%\Pia` and ignore `PIA_LOCAL_DATA_DIR`, by design — `tests/ui-scripts/README.md`
lists the consent trails among the things a hermetic run shares, because "a consent decision belongs
in one ledger". So a UI-script replay launches the app and its start-up sweep does touch the real
profile's consent data. That is the same thing any ordinary launch does, and it does not trip the
harness's profile-leak check: `Invoke-UiScripts.ps1` hashes exactly two files, `%APPDATA%\Pia\
settings.json` and `%LOCALAPPDATA%\Pia\history.db`, and neither consent folder is among them.

## Tests

`tests/Pia.Wpf.Tests/Consent/ConsentRetentionTests.cs`, temp directories only — the sweep resolves
no path of its own, which is what keeps it off the real profile.
