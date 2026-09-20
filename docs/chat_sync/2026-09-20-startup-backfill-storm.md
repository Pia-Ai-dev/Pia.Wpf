# The startup chat backfill cannot finish, so it runs in full on every launch

**Status:** Planned, not started
**Owner:** Marco Altmann
**Written:** 2026-09-20
**Origin:** Review of `%LOCALAPPDATA%\Pia\Logs\pia-2026-09-20.log`. Two-thirds of that day's warnings
came from this one loop. It is not new — 09-15, 09-17, 09-18 and 09-19 all show it.

## What the log shows

Each launch pushes the whole chat catalogue and is rate-limited away:

```
10:10:33  Pushed chat 0001a78c… to cloud (status 200)      ← 49 of these
10:10:36  HTTP PUT …/api/v1/chats/… -> 429                  ← 619 of these
          Rate limited (429) by host-209/api/v1: Retry-After=60
          Retry-After exceeds 30s, not retrying host-209/api/v1
10:11:42  Startup backfill incomplete; leaving the gate unset so the next launch retries all 668 chat(s)
```

Same shape at 11:43 (673 chats, again exactly 49 successes, starting with the identical ids). 1248
rate-limited PUTs across the day's two launches.

## Cause

The client half, in `AssistantChatSyncService.RunStartupPushAsync`:

1. **The backfill is all-or-nothing and banks no per-chat progress.** It walks every id from
   `GetAllIdsAsync`, ANDs the results into `allPushed`, and sets the
   `Settings.AssistantChatsBackfilledAt` gate only if every push succeeded. Nothing records *which*
   chats went up — the local `AssistantChats` table has no sync marker column — so a partial run
   banks nothing.
2. **It fires the whole catalogue in a tight sequential loop.** `RateLimitRetryHandler` self-throttles
   to ~99 ms per request, i.e. ~10–16 req/s sustained.
3. **A rate-limited push is abandoned, not retried.** The server answers `Retry-After: 60`;
   `RateLimitRetryHandler.MaxRetryAfter` is `TimeSpan.FromSeconds(30)`, so the handler logs
   "Retry-After exceeds 30s, not retrying" and gives up. Every 429 is a dropped push.
4. **So `allPushed` is false, the gate stays unset, and the next launch repeats all of it** — which
   trips the limiter again at the same point. Self-perpetuating; it cannot converge.

The server half, in the `Pia` repo: `/api/v1/chats` carries the `sync` rate policy
(`AssistantChatsController`), and `RateLimitOptions.Sync` defaults to **`PermitLimit = 30`,
`WindowSeconds = 60`, `SegmentsPerWindow = 3`** — 30 requests per rolling minute. The ~49 successes
are that budget plus one segment rollover.

The two halves are simply incompatible: at 30 requests/minute a 668-chat backfill needs **~22
minutes** of paced pushing, and the client tries to do it in 40 seconds with no back-off.

The comment above the gate is right that marking an unaccepted backfill "done" would strand chats
local-only, because only a logout reopens the gate. The defect is the missing middle — there is no
way to be partly done.

## Impact

Beyond ~1900 log lines and ~619 wasted requests per launch: only ~49 chats reach the cloud per
launch, and no other path pushes a pre-existing chat (the normal path pushes on change). A chat the
user does not touch again stays local-only. Measured against the local Docker server with the default
`RateLimitOptions`; the deployed limit is admin-configurable (`LimitSettingsViewModel`), so confirm
the production `sync` policy before sizing any pacing.

## Fix

1. **Bank per-chat progress.** Add a nullable backfill/pushed marker to the local `AssistantChats`
   table and push only the unmarked rows. A partial pass then becomes permanent progress and the
   gate closes on its own once the remainder empties. Necessary, but on its own it converges in
   ~22 launches, so it ships together with (2).
2. **Stop the pass at the first 429 instead of firing the rest.** Once the limiter trips, every
   remaining push in that pass will fail. Abandon the pass, resume on a later sync signal — that
   turns a 41-second storm into one rejected request.
3. **Let a background bulk op wait longer than 30 s.** A backfill is not latency-sensitive; a 60 s
   `Retry-After` is affordable where an interactive push's is not. Either carry a per-request retry
   ceiling into `RateLimitRetryHandler`, or rely on (2) and skip this — they overlap, so pick one
   before implementing.
4. **Owner decision 2026-09-20: client-only.** A bulk upsert endpoint and a raised `sync` limit were
   both considered and declined — the first changes the server API contract, the second loosens a
   limit that exists for a reason. The backfill converges over several sessions instead, which is
   acceptable because nothing downstream needs it complete on the first launch.
5. **Log a summary, not a line per chat.** One "backfill pass: pushed N, rate-limited R, remaining M"
   at Information; per-chat at Debug.

**Test seam:** a fake HTTP handler that returns 200 for the first N pushes and 429 with
`Retry-After: 60` thereafter. Assert that the pass stops on the first 429, that the chats already
pushed are marked, and that a second pass starts from the remainder rather than from the top.

*Effort:* S · *Value:* High.

## Reproducing

Any profile with more than ~50 chats and `AssistantChatsBackfilledAt` unset. Watch for the
"Startup backfill incomplete" warning; count distinct chat ids in the 429s, not lines.

## What shipped, and what is left

Shipped 2026-09-20: the per-chat `BackfilledAt` marker, the query that asks only for the remainder,
and the stop-on-first-429. That ends the storm — a launch now costs one rejected request instead of
619 — and every pass is permanent progress, so the gate closes on its own.

**Not done: in-session pacing.** A pass still banks only what one rate-limit window allows (~49
chats), so a 670-chat profile converges over roughly a dozen launches rather than inside one
session. Finishing it means resuming the pass after the `Retry-After` elapses, and the constraint
that makes it more than a `Task.Delay` is that `RunStartupPushAsync` is awaited *before* the signal
loop starts reading: sleeping there would hold up ordinary chat syncing for the ~22 minutes the
server's budget needs. It wants a resumption that runs alongside the signal loop — which also breaks
the "no other upsert is in flight" invariant the `_rateLimited` field currently documents. Design
that before writing it.
