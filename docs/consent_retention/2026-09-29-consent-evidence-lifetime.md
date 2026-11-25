# Consent evidence travels with the transcript

**Status:** Implemented
**Owner:** marco.altmann@neo42.de
**Written:** 2026-09-29
**Origin:** Owner decision of 2026-09-29 (work item #7675), which replaces the fixed 14-day window of
[2026-09-09-consent-retention.md](2026-09-09-consent-retention.md) for sessions recorded from this
release on.

## The rule

The proof that a speaker consented lives as long as the data that relies on it — and it travels
with that data:

- **Saved transcripts carry the proof.** An exported `.md` and a vault note both get a
  `consentRecord` block in their front matter. It has:
  - the session ids;
  - the notice version, purposes and language;
  - one entry per consenting speaker, with the detected label (`label`), the label the text shows
    (`shownAs`, which can be a name), the grant time and any revocation time.
  - Teams notes carry the host's acknowledgement time instead of per-speaker entries.
  - No signature, no audio, no voice features.
- **Pia's own copy** — the DPAPI-protected session folder under `PiaPaths.ConsentEvidenceDirectory`,
  schema v2 with `session.json` — has no grace period:
  - it goes when the session ends if Pia manages no copy of the transcript, even if it was exported;
  - otherwise it goes at the first sweep after the last managed copy is gone.
  - Managed copies are:
    - a vault note whose front matter lists the session id, found by scanning the vault, so a note
      moved inside the vault still counts;
    - topic pages derived from such a note, per IngestState;
    - the summary chat, tracked by id;
    - a summary requested in the last ten minutes.
- **Exports are the owner's responsibility.** Pia logs where and when it exported (`copies.json` in
  the session folder) but never tracks the file afterwards. A note moved out of the vault, or a
  vault folder the user switched away from, counts like an export.
- **The chat never carries the proof.** The summary prompt goes to the AI provider; the record stays
  out of it.

## Teams meetings

A Teams meeting has no per-speaker consent sentence. The host's confirmation is the session's
evidence instead:
- for a scheduled routine, the acknowledgement stored with the job;
- in the live overlay, the tick before joining.

When recording starts, the session folder gets `session.json` (kind `teams` or `teams-live`) and a
DPAPI-protected `host-ack.json`, which holds the time, the notice version, the purposes and the
language. A running recording is registered with `IConsentLiveSessions`, so no sweep touches it. When
it ends, the same rule applies as for direct transcription.

## Revocation

Revoking a speaker lists the session's copies:
- exports with path and time, which the user has to clean up;
- vault notes and chats, which Pia deletes one by one or all at once.

A vault note is re-checked just before deletion: it must lie inside the vault root, be `.md`, and
still name the session. Kept notes get the revocation written into their front matter.

## Notice versions

`ConsentNotice` holds the version and purposes of each notice text:
- direct transcription;
- the Teams routine acknowledgement;
- the live Teams acknowledgement.

A test hashes the en/de/fr wording through `ResourceManager` and fails when the text changes without
a version bump.

## Safety rails

- The lifetime sweep does nothing while a data-folder override (`PIA_DATA_DIR`,
  `PIA_LOCAL_DATA_DIR`) is active. The evidence folder does not follow the override while the vault
  does, so a UI-test run against a throwaway vault would otherwise delete real evidence.
- An unreadable note, an unreadable copies log or a missing vault root makes the scan inconclusive,
  and nothing is deleted in that pass.
  - This is deliberate: evidence is kept longer rather than dropped while its note may still exist.
  - The limit: one note that stays unreadable, for example because of its permissions or because it
    is a cloud placeholder, keeps every v2 folder until it is fixed.
  - The log shows it only as an unreadable-note count.
- Running sessions are never swept, direct or Teams; session end at app shutdown is left to the next
  start.
- Folders without `session.json` (recorded before this rule) still age out after 14 days, as
  described in the older document; the audit trail keeps its 14 days too — it holds detected labels,
  never names.

## Tests

- `tests/Pia.Wpf.Tests/Consent/`: `ConsentLifetimeServiceTests`, `ConsentFrontMatterTests`,
  `ConsentNoticeTests`, `ConsentVaultScanTests`, `ConsentEvidenceStoreTests` and
  `ConsentRetentionTests`.
- `tests/Pia.Wpf.Tests/ViewModels/ConsentCopiesViewModelTests.cs`.
- All of them use temp directories only.
