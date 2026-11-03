# Account switch, local-data reset and E2EE downgrade: review fixes — checklist

**Status:** Done
**Owner:** Marco Altmann
**Written:** 2026-10-28
**Origin:** A review of the client code that Pia `feature/7938` pins (Pia.Wpf `5107d31d`, PRs #80 and
#81). The reviewer reproduced findings 1–6 there. All six also reproduce on `develop` `6298e0bc`, since
#82 and #83 touch none of these paths. Finding 7 (the server's `/auth/login/local` body lacks
`isE2EEEnabled`) is fixed in Pia and needs no client change.

Tick a box in the commit that lands its step.

**Effort:** `XS` under a day, no new types · `S` 1–2 days · `M` 3–5 days, new types or a new
surface · `L` a week or more, a new subsystem.
**Value:** `High` user-visible or a real risk closed · `Med` worthwhile, not headline · `Enabler`
little standalone value, unblocks a High.

## Decision gates

| Gate | Question | Blocks | Decision |
|---|---|---|---|
| G1 | Should an explicit *Clear local data* also delete `settings.json.unreadable-*`? Those copies hold encrypted tokens and the wrapped UMK, and keeping them aside is what the owner chose on 2026-10-27 for an unreadable file at startup. | 4 (that one glob only) | Delete them too; the startup keep-aside is unchanged (owner, 2026-10-28). |

## Steps

- [x] **1. Keep a failed local-data probe from blocking sign-in or sign-out.**
  `LocalDataResetService` catches a failing probe and answers with a cautious default: synced data
  present, vault has memories, API keys may be lost. A locked `history.db` or an unreadable vault no
  longer throws out of the sign-in, sign-out or *Device removed* commands.
  *Deps:* none · *Effort:* XS · *Value:* High
- [x] **2. Hold the scheduler and manual sync while a local-data clear is pending.**
  `ScheduledJobBackgroundService` starts only when no clear is pending. `SyncClientService` refuses
  every sync entry point through an injected reset gate, and *Sync now* / *Full resync* show the
  pending-clear restart prompt instead.
  *Deps:* none · *Effort:* S · *Value:* High
- [x] **3. Resume chats held by the E2EE gate once unencrypted sync is allowed.**
  `AssistantChatSyncService` listens to `E2EEDowngradeChanged`. When the downgrade clears, it requeues
  the held chats on the next loop pass through the same helper `FlushAsync` uses.
  *Deps:* none · *Effort:* XS · *Value:* Med
- [x] **4. Delete moved-aside store copies on *Clear local data*.**
  The wipe also deletes `<name>.unreadable-*` for `templates.json`, `providers.json`,
  `pending-sync-deletes.json`, `policy-cache.json` and, per G1, `settings.json`, each from its own
  store's directory. Device-local asides survive.
  *Deps:* G1 (decided) · *Effort:* XS · *Value:* Med
- [x] **5. Scope the E2EE-required marker to the server.**
  `E2EERequiredAccountIds` entries become `<server key>|<account id>`. The property name and type do
  not change, so `ClientPolicyContract.DeniedKeys` stays valid. The WinWright test-round steps that
  read the raw list are updated.
  *Deps:* none · *Effort:* XS · *Value:* Med
- [x] **6. Stop a stale sync cycle from writing one account's data into another's session.**
  `SyncClientService` has an account epoch that every sign-in and sign-out bumps. Each pull/apply
  holder captures it with `SyncUserId` and re-checks before it applies a page, mutates the shared
  settings, raises a device or E2EE notice, or saves the cursor. A stale cycle applies and saves
  nothing. Steps 8 and 9 replace this check: it ran only after the await it guarded, and the bump came
  after the switch had already landed.
  *Deps:* none · *Effort:* S · *Value:* High

### Second review of this branch

A second review reproduced four gaps in steps 3, 5 and 6.

- [x] **7. Keep the downgrade protection of markers written before step 5.**
  A bare account id in `E2EERequiredAccountIds` still marks that account, on any server. Marking the
  account again replaces it with the server-scoped key, and allowing unencrypted sync removes both.
  *Deps:* 5 · *Effort:* XS · *Value:* High
- [x] **8. Check the account immediately before every send.**
  `PostPushAsync` and the pull request run a synchronous epoch check with no await before the send.
  That covers the delta push and every first-sync batch. A push whose store reads overlapped a switch
  is never sent with the old account's token.
  *Deps:* 6 · *Effort:* S · *Value:* High
- [x] **9. Coordinate account transitions with the local writes of a sync.**
  A shared `AccountTransitionCoordinator` replaces the epoch in `SyncClientService`. Sign-in, sign-out and
  the stored-token load bump the gate's epoch and then wait for it. A pulled page holds the gate from
  its first write to its token save, as do the cursor and settings-hash saves, and each write checks
  the epoch first. A switch therefore waits for the write in flight, and the next check stops the rest
  of the page. Sends never hold the gate. A sign-out raised from inside the holder, such as a failed
  token refresh during a plugin download, does not wait on it.
  *Deps:* 6 · *Effort:* M · *Value:* High
- [x] **10. Resume a held startup backfill once unencrypted sync is allowed.**
  Clearing the downgrade also re-arms the paced backfill, so the chats the missing-key hold kept back
  from the backfill upload as well. Step 3 covered only the live queue.
  *Deps:* 3 · *Effort:* XS · *Value:* Med

## Not yet planned

- **Refuse *Run now* while a clear is pending.** The scheduler no longer starts then, but
  `IScheduledJobRunner.RunNowAsync` (the Routines page and the assistant's job tool) still runs a job
  on the unstarted service. Closing it needs a new `ScheduledJobRunNowResult` and its message.
- **Cancel the stale cycle's HTTP request.** The epoch guard makes a stale cycle harmless, but a
  request already sent still runs to its end. A cancel entry point would have to be separate from
  `StopBackgroundSync`, since the device-revoked and lost-key paths call that from inside a cycle.
- **Server bump.** Once Pia ships the `/auth/login/local` fix, tick *Server bump* in
  [../e2ee_pentest/2026-10-20-e2ee-downgrade-hardening-checklist.md](../e2ee_pentest/2026-10-20-e2ee-downgrade-hardening-checklist.md)
  and re-run WinWright B6.

## Suggested order

1, 2, 3 and 4 first: each is small and closes its own path. Then 5, and 6 last, since it is the
largest and touches the most sync code.
