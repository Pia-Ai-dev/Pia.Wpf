# Chat sync ran before sign-in

**Status:** fixed · **Owner:** Marco Altmann · **Written:** 2026-09-09
**Origin:** a stray log line in the routine working-directory live pass
([../routines/2026-09-08-routine-working-directory-e2e.md](../routines/2026-09-08-routine-working-directory-e2e.md)):
with `syncEnabled: false`, `AssistantChatSyncService` still encrypted a chat and issued
`HTTP PUT /api/v1/chats/<id>`, which answered 401.

`AssistantChatSyncService` gated on one thing — the server advertising the `chats` capability — and
never on being signed in. Every other sync path checks `settings.SyncEnabled` first
(`SyncClientService:318`); this one did not, and `BuildClientAsync` built a client with no
`Authorization` header rather than refusing.

## Why that reached production

- Release writes `settings.ServerUrl = https://cloud.pia-ai.de` on **every** launch
  (`Bootstrapper.cs:146`), unless enterprise policy enforces the key. "No server configured" is not a
  state a shipped install is in.
- `GET /api/capabilities` answers anonymously: `200 {"chats":true,"chatsSchemaVersion":1}` (curled
  2026-09-09). So the capability gate opens for a client that has never signed in.
- `SyncMapper.ToSyncAssistantChat` enciphers only `if (IsE2EEActive && userId is not null)`. Signed
  out there is no `SyncUserId`, so it takes the else-branch **unconditionally** and puts `Title`,
  `ProviderId` and `Messages` on the wire as gzipped JSON — not ciphertext.
- `AssistantChatSyncService` is started unconditionally from `App.xaml.cs:257`.

## The two defects, worst first

**The startup backfill burned its own gate.** `RunStartupPushAsync` is gated only on
`AssistantChatsBackfilledAt is null`. It pushed every local chat, each PUT 401'd, the failures are
swallowed by design ("the local store is authoritative"), and it then stamped
`AssistantChatsBackfilledAt` anyway — only the 403 `e2ee_required` case was special-cased. Login did
not clear the stamp; only logout did. So: install, use Pia for weeks, sign in → none of that history
ever uploads, and nothing retries it. The same trap swallowed the chats written between a logout and
the next login, and it fired for a client that was merely offline at first launch.

**Chat content left the device with sync off.** Full transcripts of users who never signed in were
uploaded to `cloud.pia-ai.de` and rejected at 401 — nothing stored, TLS validated
(`TrustSelfSignedCertificates` is forced false in the same bootstrap block), but the bytes left the
machine, and in plaintext form. Not a breach; a broken promise, in a product sold on making that
promise. Whether the server reads the body before its 401 is **unverified** from the client side.

Minor, same cause: an anonymous capability GET on every launch is an install beacon; one 401 PUT per
chat change all session; an `INFO … returned status 401` per op muddying support logs.

## What changed

- `BuildClientAsync` refuses without `SyncEnabled` and a token. It sits before
  `ToSyncAssistantChat`, so the payload is never even built — one guard covers upsert, delete, pull
  and backfill, and covers the state flipping mid-session.
- `ExecuteAsync` waits for sign-in before probing (`RunStartupCycleAsync` + `WaitForSignInAsync` on
  `IAuthService.LoginStateChanged`) instead of probing and then parking forever. Signing in
  mid-session now starts the pull and the backfill, which it never did before.
- `ProcessOpAsync` / `SendUpsertAsync` / `SendDeleteAsync` report success, and the backfill stamps
  the gate only when every push landed. A delete answered 404 counts as landed — already gone
  server-side is the desired end state.
- `ApplyLoginAsync` clears `AssistantChatsBackfilledAt`, so a stale stamp cannot suppress a fresh
  account's upload.

Each new test has a positive twin in the same file, so neither side can pass by observing a default:
signed-in still sends and still stamps the gate; signed-out sends nothing, and a 401 leaves the gate
open. `AuthServiceChatBackfillMarkerTests` covers the login clear.

## Open: installs already in the bad state

An install that ran a shipped version has the stamp set and is signed in. None of the above un-burns
it — the gate stays closed, and its pre-sign-in chats stay local-only for good. Clearing it needs a
one-time repair keyed on a new settings field, the way `BlankedSyncRowRepairAt` already works
(`SyncClientService:653`). That is a decision, not a detail: it re-pushes every chat of every
existing install once, with the 409-merge path absorbing the conflicts. Not implemented.
