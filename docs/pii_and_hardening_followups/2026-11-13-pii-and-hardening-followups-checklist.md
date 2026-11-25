# PII and hardening follow-ups — checklist

**Status:** Steps 1–8 done. Step 2b waits on gate G1; gate G2 needs the owner's confirmation before release.
**Owner:** Marco Altmann
**Written:** 2026-11-13
**Origin:** [2026-11-13-pii-and-hardening-followups-plan.md](2026-11-13-pii-and-hardening-followups-plan.md),
from GitHub issues #88, #92 and #93, the E2EE pen-test brief and two *Not yet planned* lists.

Tick a box in the commit that lands its step.

**Effort:** `XS` under a day, no new types · `S` 1–2 days · `M` 3–5 days, new types or a new
surface · `L` a week or more, a new subsystem.
**Value:** `High` user-visible or a real risk closed · `Med` worthwhile, not headline · `Enabler`
little standalone value, unblocks a High.

## Decision gates

| Gate | Question | Blocks | Decision |
|---|---|---|---|
| G1 | Did Pia.Mac ever store a recovery copy with a salt other than 32 bytes, or with Argon2id costs outside 19456–262144 KiB / 1–10 passes / 1–16 lanes? | 2b | Open. Pia.Wpf wrote 32 bytes and 65536 / 3 / 4 in every release. The ceilings already shipped in step 2, so a "yes" above the ceiling means step 2 needs revisiting. |
| G2 | May a known name in another case, with a typo, or glued to letters reach the provider unmasked in tool results and replayed replies, in exchange for byte-exact file round trips? | 6 | Yes, provisionally, as #93 proposes and Pia.Mac does. The owner confirms before release; step 6 is its own commit so it can be reverted alone. |

## Steps

- [x] **1. Mask `(+49) …` and `Tel.+49 …`.**
  `StructuredPiiDetector.PhoneRegex` starts a match at `(+` and lets a `+` follow a dotted word, with
  every existing positive and negative case unchanged.
  *Deps:* none · *Effort:* XS · *Value:* High
- [x] **2. Bound the recovery copy's Argon2id parameters.**
  `RecoveryCodeService` refuses memory above 262144 KiB, time above 10, parallelism above 16, values
  below 1, memory below 8 KiB per lane and an undecodable salt before the KDF runs, and onboarding
  shows its own message for it.
  *Deps:* none · *Effort:* S · *Value:* High
- [ ] **2b. Hold the recovery copy to the server's full write rule.**
  The same check also refuses a salt that is not 32 bytes and costs below 19456 / 1 / 1.
  *Deps:* 2, G1 · *Effort:* XS · *Value:* Med
- [x] **3. Refuse *Run now* while a local-data clear is pending.**
  `RunNowAsync` returns the new `ScheduledJobRunNowResult.ResetPending` before it reads the job, and the
  Routines page and the `run_routine` tool each say so.
  *Deps:* none · *Effort:* XS · *Value:* Med
- [x] **4. Send a User-Agent.**
  Every factory-created `HttpClient` carries `Pia/<version> (Windows NT x.y)`, which the server reads as
  "Pia desktop x.y"; a request that brings its own header keeps it.
  *Deps:* none · *Effort:* XS · *Value:* Med
- [x] **5. Record the server bump in the docs.**
  The downgrade-hardening checklist and plan say done, and the account-switch checklist keeps only the
  WinWright B6 re-run.
  *Deps:* none · *Effort:* XS · *Value:* Enabler
- [x] **6. Tokenize tool results and replayed assistant text verbatim.**
  `TokenMapService.TokenizeVerbatim` matches stored values in exact case as whole words with no fuzzy
  pass, object results are tokenized leaf by leaf, and the user's own text keeps today's matching.
  *Deps:* G2 · *Effort:* M · *Value:* High
- [x] **7. Stop *Regenerate embeddings* at the first unavailable error.**
  The loop in `VaultViewModel` leaves on `EmbeddingUnavailableException` with one log line.
  *Deps:* none · *Effort:* XS · *Value:* Med
- [x] **8. Verify the branch.**
  Zero-warning rebuilds in Debug and Release and the test exe with `Failed: 0`.
  *Deps:* 1–7 · *Effort:* XS · *Value:* High

## Suggested order

1, 2 and 3 first: each closes its own path and touches no shared code. Then 4 and 5. Decide G2 before 6,
which is the largest step and the only one that changes what PII protection masks. 7 can land any time
before 8. 2b follows whenever G1 is answered.

## Not yet planned

- A guard on the scheduler's tick for a clear that becomes pending while Pia runs. The startup hold
  covers the next launch; a session that writes the marker and fails to restart keeps ticking.
- Tokenizing a headless run's replayed object results leaf by leaf, as step 6 does for live tool
  results. The replay path holds only the serialized text.
- Verbatim matching for user-role messages the app writes itself, such as agent step instructions.
- The recovery KDF limits as constants in `Pia.Shared`, so client and server read one table.
- A `meeting-redirect` client registration without `HttpLoggingHandler`, which a comment in
  `TeamsMeetingSession` describes and `Bootstrapper` does not contain.
- Masking `Tel.(+49) …`, a dot directly before `(+`.
