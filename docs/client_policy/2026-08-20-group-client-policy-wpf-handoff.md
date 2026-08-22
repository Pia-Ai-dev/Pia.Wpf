# Group client policy — WPF client handoff

**Status:** Ready to implement. The server half is complete and verified on `feature/group-client-policy` in
`Pia-Ai-dev/Pia` — **not merged or pushed**, so confirm it has landed before testing against a deployed server.
**Date:** 2026-08-20
**Executed in:** `C:\projects\Pia.Wpf` (the standalone clone, branch `feature/agent-run-spine`). Paths below are
relative to that repo's root unless prefixed.
**Server plan (background only, not required reading):** `C:\Users\maltm\.claude\plans\i-want-to-plan-sharded-ritchie.md`.

This document is self-contained. §3 lists the two `Pia.Shared` files that are **already sitting uncommitted in
your clone** — read that before writing any shared type.

---

## 1. Context in one page

### What exists today

Pia reads a machine-local `policy.json` at startup — first of `<exeDir>\policy.json`,
`<exeDir>\..\policy.json`, `%ProgramData%\Pia.Wpf\policy.json`. It has two sections, both shaped like a full
`AppSettings`:

- **`defaults`** — a starting value, applied only while the user is still sitting on Pia's built-in default.
- **`enforce`** — pinned; `IsEnforced(nameof(...))` drives an inverted `IsEnabled` binding so the control greys
  out.

`PolicyService` is a singleton that loads one file, caches it for the process lifetime, and never reloads.
`SettingsService` applies the policy on load **and again before every save** ("prevent circumvention"), so the
post-policy value is what lands on disk.

### What this feature adds

A second **source** for that same document: an admin publishes it per group in the server's admin console, and
it arrives over the ordinary sync pull. Nothing about the document's meaning changes. The client's job is:

1. Cache the delivered document locally so it survives restarts and offline runs.
2. Merge it with the local file, **server winning** on any key both set.
3. Re-apply a `defaults` value when the admin changes it, rather than only once.

### The four decisions already taken

These were settled with the product owner before the server work; they are not open.

| Decision | Value |
|---|---|
| Scope | The two existing sections only. No per-group Optimize templates. |
| Precedence | **Server wins** over the local `policy.json`, in both sections. |
| Admin editor | Raw JSON, validated server-side. (No client impact — listed so you recognise the shape.) |
| `defaults` | **Re-apply when the admin changes the value**, not one-shot. |

Because the server wins, three keys are structurally excluded from server management rather than resolved by
precedence — see `DeniedKeys` in §3.2. A server that could pin its own URL, or switch off the channel that
delivers policy, would be able to disconnect a whole group from the only place that could fix it.

---

## 2. The server contract as built

Wire JSON is camelCase, and `JsonSerializerOptions.DefaultIgnoreCondition = WhenWritingNull` is applied
app-wide server-side — so **any null property is an ABSENT KEY**, never `"key": null`.

### 2.1 The pull channel

`GET /api/sync/pull?since={ISO-8601}&catalogVersion={long}&limit={int}` is unchanged in shape. The response
gains one top-level property, `clientPolicy`, of type `SyncClientPolicySnapshot`:

| JSON field | Type | Notes |
|---|---|---|
| `document` | `string` | The policy document verbatim. `"{}"` when the group has none. Never null. |
| `updatedAt` | `DateTime?` | When an admin last wrote it; absent when the group has never had one. |

It rides the **same catalog block** as `managedPersonas`, and inherits that block's rules exactly:

- Loaded with **no `SyncedAt` filter** — `since` has no effect on it.
- Skipped wholesale when the client echoes a `catalogVersion` equal to the server's current one. A skipped
  catalog omits `clientPolicy` **and** `managedPersonas` entirely.
- **Absent key ⇒ keep the cached document.** Never read absence as "no policy".
- **Present ⇒ authoritative.** Including `"{}"`, which means *this group has no policy* and must clear the
  cache. This is how "the admin deleted the policy" and "my group changed to one without a policy" both
  arrive — neither carries a tombstone.
- Push has no policy field and never will.

`catalogVersion` remains an **opaque, non-monotonic token**: store it, echo it verbatim, never order two.

### 2.2 Why the document is a string and not typed sections

Deliberate, and it is the one thing not to "improve":

- The client's semantics turn on **which keys are present**. A typed round-trip cannot distinguish "absent"
  from "explicitly set to the built-in default" — that is precisely the bug the uncommitted `PolicyService`
  rewrite in your working tree fixes (§3.3).
- This document spells enum values as **strings** (`"Dark"`, `"DE"`); the rest of the wire uses ints.
- A string round-trips byte for byte, so the server never reinterprets what the admin wrote. The server column
  is `text`, not Postgres `jsonb`, for exactly this reason.

So: feed `document` straight into the same parse path the file uses. Do not deserialize it into DTOs on the way
in.

### 2.3 Real JSON — group has a policy

```json
{
  "serverTimestamp": "2026-08-20T09:14:02.1837744Z",
  "templates": { "upserted": [], "deleted": [] },
  "plugins": { "upserted": [], "deleted": [] },
  "managedPersonas": { "personas": [], "recentlyRemoved": [] },
  "clientPolicy": {
    "document": "{\"defaults\":{\"uiLanguage\":\"DE\"},\"enforce\":{\"assistantFileToolsEnabled\":false}}",
    "updatedAt": "2026-08-19T08:55:10.0000000Z"
  },
  "catalogVersion": 4194235871203344761
}
```

### 2.4 Real JSON — group has no policy (still authoritative)

```json
{
  "serverTimestamp": "2026-08-20T09:20:11.4410920Z",
  "clientPolicy": { "document": "{}" },
  "catalogVersion": 4194235871203344761
}
```

Note `updatedAt` is absent, not null. **Clear the cache.**

### 2.5 Real JSON — catalog skipped

```json
{
  "serverTimestamp": "2026-08-20T09:21:44.9910110Z",
  "templates": { "upserted": [], "deleted": [] },
  "catalogVersion": 4194235871203344761
}
```

No `clientPolicy` key, no `managedPersonas` key. **Keep the cache.**

### 2.6 Admin API (reference only — the client never calls these)

```
GET /api/admin/groups/{id}/client-policy   → { document, updatedAt }
PUT /api/admin/groups/{id}/client-policy     { "document": "…" }
```

`AdminPolicy` on both; the `PUT` additionally requires the `GroupManagement` licence feature. A rejected
document answers `400 { "error": "validation_failed", "message": "…" }`. A blank document, or `{}`, clears the
policy. Admin console page: `/admin/groups/{id}/client-policy`. Useful for driving an integration environment
by hand.

---

## 3. Before you write anything

### 3.1 Two `Pia.Shared` files are already in your working tree, uncommitted

The server needed them to compile, and they were mirrored into your clone so the two copies cannot diverge
while both sessions are open. They were verified byte-identical across clones on 2026-08-20.

| File | State in `C:\projects\Pia.Wpf` |
|---|---|
| `src/Pia.Shared/Sync/SyncPullResponse.cs` | Modified — new `SyncClientPolicySnapshot` type, plus a nullable `ClientPolicy` property after `ManagedPersonas` |
| `src/Pia.Shared/Policy/ClientPolicyContract.cs` | New, untracked |

**Do not re-author these.** Commit them as part of your branch. Anything *further* the client needs in
`Pia.Shared` is yours to add — but note that `CachedClientPolicy` (§4.1) is a client-only type and belongs in
`Pia.Wpf`, not the shared lib: the server never sees it.

`ClientPolicy` is nullable with **no `= new()` initializer**, and must stay that way. The absent-key rule
depends on it deserializing to null.

### 3.2 `ClientPolicyContract` — the shared rule set

```csharp
public static class ClientPolicyContract
{
    public const string DefaultsSection = "defaults";
    public const string EnforceSection  = "enforce";
    public const string EmptyDocument   = "{}";
    public const int    MaxDocumentBytes = 64 * 1024;

    public static readonly IReadOnlySet<string> Sections;     // ordinal: "defaults", "enforce"
    public static readonly IReadOnlySet<string> DeniedKeys;    // OrdinalIgnoreCase

    public static bool    IsDenied(string key);
    public static string? Normalize(string? document);          // blank or "{}" → null
    public static bool    TryValidate(string? document, out string? error);
}
```

`TryValidate` checks shape only — one JSON object, no section beyond those two (matched **case-sensitively**,
because your key reader is), each section an object, the size cap, and no denied key. It deliberately does
**not** validate setting names: the schema lives in `AppSettings` and a second copy would drift. An
unrecognised setting key stays a client-side warning, which `ReadPresentKeys` already logs.

`DeniedKeys`, all 31, camelCase:

```
serverUrl  syncEnabled  trustSelfSignedCertificates
encryptedAccessToken  encryptedRefreshToken  syncUserId  syncUserEmail  syncUserDisplayName
syncProvider  syncDeviceId  lastSyncTimestamp  lastPullETag  lastChatPullETag
lastPushedSettingsHash  lastCatalogVersion  managedPersonaStoreInitialized  clientPolicyInitialized
assistantChatsBackfilledAt  isE2EEEnabled  e2eeEncryptedUmk  e2eeDeviceId  e2eeUmkVersion
e2eeRecoveryConfigured  vaultVersion  ingestSchemaVersion  assistantFolderLayoutVersion
draftText  windowWidth  windowHeight  windowLeft  windowTop
```

The first three are the load-bearing ones and the reason the "server wins" rule is safe. The rest are cursors,
credentials and migration markers the client owns; the server refuses them too, so this is defence in depth.

**Filter these from the local file layer as well.** Nothing today stops a hand-written `policy.json` from
setting `lastPullETag` or `vaultVersion`, and one shared constant now closes both.

### 3.3 Prerequisite: commit the `PolicyService` rewrite already in your tree

`src/Pia.Wpf/Services/PolicyService.cs` is modified in your working tree, and the change is a prerequisite —
not a nice-to-have. The committed version on `feature/agent-run-spine` still decides "did the admin set this?"
by comparing the deserialized value against a fresh `new AppSettings()`. Against a server-delivered document
that is not merely imprecise, it is unusable:

- Every collection-typed property deserializes to a **fresh instance**, so `{"enforce":{}}` reads as "set" for
  `alwaysAllowedTools`, `privacy`, `modeProviderDefaults`, `modePersonaDefaults` and `todoColumnWidths` — and
  overwrites the user's values with empties.
- A key whose policy value equals the built-in default is indistinguishable from absent, so
  `"enforce": { "assistantFileToolsEnabled": true }` is a no-op — you can only ever pin the non-default side.

The working-tree rewrite replaces that with real key-presence tracking (`JsonNode.Parse` → `ReadPresentKeys`,
case-sensitive camelCase match, unknown keys logged) plus a `MatchesBuiltInDefault` that falls back to
JSON-serialized comparison so collection-typed `defaults` work at all. Everything below assumes it.

---

## 4. The client work — ordered slices

Files you will touch, all under `src/Pia.Wpf/`:

```
Models/AppSettings.cs              Models/PolicySettings.cs
Models/CachedClientPolicy.cs (new) Services/PolicyService.cs
Services/Interfaces/IPolicyService.cs
Services/SyncClientService.cs      Services/AuthService.cs
```

No ViewModel or XAML changes are required. Every existing consumer — the ~11 `Is…Enforced` getters across
`GeneralSettingsViewModel`, `OptimizeSettingsViewModel`, `ProvidersSettingsViewModel`,
`AccountSettingsViewModel`, and `IsLoginProviderAllowed` in `AccountView.xaml` / `AccountSetupStep.xaml` —
reads the merged result and keeps working untouched. That is the point of merging inside `PolicyService`.

### C1 — the cache file

**New:** `Models/CachedClientPolicy.cs`.

```csharp
public class CachedClientPolicy
{
    public string Document { get; set; } = ClientPolicyContract.EmptyDocument;
    public DateTime? UpdatedAt { get; set; }

    /// <summary>Per-key JSON of the last `defaults` value this mechanism applied, so a later admin change
    /// re-applies while a user's own change wins.</summary>
    public Dictionary<string, string> AppliedDefaults { get; set; } = new();
}
```

Persist as `policy-cache.json` next to `settings.json` by deriving from `JsonPersistenceService<T>`
(`FileName => "policy-cache.json"`), which already routes through `PiaPaths.RoamingDataDirectory`. **Never call
`Environment.GetFolderPath` directly** — the repo rule exists so UI tests get a throwaway profile.

Roaming, not local: the document is a property of the signed-in user's group, and it lives beside the settings
it modifies.

### C2 — `PolicyService` becomes layer-aware

**Files:** `Services/PolicyService.cs`, `Services/Interfaces/IPolicyService.cs`.

The existing surface does not change:

```csharp
Task<PolicySettings> GetPolicyAsync();
bool IsEnforced(string propertyName);
bool IsLoginProviderAllowed(string provider);
void ApplyPolicy(AppSettings userSettings);
```

Add one member:

```csharp
/// <summary>Stores the pull's authoritative document. Writes the cache ONLY — see the note below.</summary>
Task ReplaceServerPolicyAsync(string document);
```

Internally, extract the current per-file "deserialize + `ReadPresentKeys`" pair into a reusable
`LoadLayer(string json)` returning `(PolicySettings typed, HashSet<string> defaultKeys, HashSet<string>
enforceKeys)`, then:

1. Load the local file layer exactly as today (three candidate paths, first existing wins, no merge across
   them).
2. Load the cached server document as a second layer.
3. Drop every `ClientPolicyContract.DeniedKeys` member from **both** layers' key sets.
4. Flatten **local first, then server**, copying each present key's value into one merged `AppSettings` and
   union-ing the key sets. Later layer wins ⇒ server wins.
5. `ApplyPolicy` / `IsEnforced` / `IsLoginProviderAllowed` then run against the merged result, unchanged.

Effective precedence, end to end:

```
server enforce → local enforce → user value → server defaults → local defaults → built-in default
```

Two traps in the flatten:

- **The merged `Defaults` / `Enforce` must be non-null whenever any layer set a key in that section.**
  `IsLoginProviderAllowed` reads `_cached?.Enforce?.AllowedSyncProviders` through a `?.` chain and treats null
  as "all providers allowed" — a null merged section would fail *open* on the one policy key that gates the
  login UI.
- **`ReplaceServerPolicyAsync` must not touch `_cached` or the merged key sets.** Write the cache file and
  return. If it mutated the in-memory policy, the next `SaveSettingsAsync` — any settings toggle, a draft save,
  a window move — would re-apply the *new* enforce values while every `Is…Enforced` getter still reported the
  old state: values snapping silently while controls stay unlocked. Restart-only is what makes this coherent,
  and it matches what `policy.json` already documents.

### C3 — re-applying `defaults`

In `ApplyPolicy`, a merged `defaults` key lands when the user's current value equals the built-in default
**or** equals the value recorded in `AppliedDefaults` for that key. Record the newly applied value afterwards.
In plain terms: re-apply unless the user has since changed it themselves.

Compare **JSON-serialized strings** using the existing `PolicyService.JsonOptions` — the same fallback
`MatchesBuiltInDefault` already uses. Reference equality would make every collection-typed default re-apply on
every launch.

Track on the **merged** effective default, not per source. That means a changed *file* default also re-applies,
which is a small behaviour change for existing file-only deployments. It is deliberate: one rule, and it is
what an admin expects. It needs a line in the docs (§7).

### C4 — sync client: apply on pull

**File:** `Services/SyncClientService.cs`.

The managed-persona apply block near the end of `PullPageAsync` is the exact precedent — same catalog gating,
same first-page guard, same "before the version persist" placement. Add alongside it:

```csharp
// REPLACE-ALL: a non-null clientPolicy is the authoritative document for this user's group. Null (⇒ key
// absent, the server omits nulls) means the catalog fast-skip fired — keep the cache. A present "{}" is a
// real answer and clears it. Writes the cache only; the merged policy is read once at startup.
if (pullResponse.ClientPolicy is { } policy)
    await _policyService.ReplaceServerPolicyAsync(policy.Document);
```

It must sit **before** the `LastCatalogVersion` / `LastPullETag` persist, for the reason the comment there
already gives: a throw during apply has to leave both conditional tokens unchanged so the next pull re-sends
the catalog instead of fast-skipping a document this page never stored.

Log the document **length**, never its content — it is admin-authored and may name internal paths or hosts.
The server logs it the same way.

### C5 — the first-run rule, and the one thing to get right

**Files:** `Models/AppSettings.cs`, `Services/SyncClientService.cs`.

Add `public bool ClientPolicyInitialized { get; set; }` next to `ManagedPersonaStoreInitialized`.

The mechanism already exists — `forceFullCatalog` in `PullPageAsync` — and both channels ride the same catalog
block, so extend the condition rather than adding a second mechanism:

```csharp
var forceFullCatalog = isFirstPage
    && (!settings.ManagedPersonaStoreInitialized || !settings.ClientPolicyInitialized);
```

Both `?catalogVersion=` and `If-None-Match` are already suppressed when that flag is set. Because every current
profile has `ManagedPersonaStoreInitialized == true` and the new flag defaults to `false`, the first pull of a
policy-aware build is unconditional exactly once. Without it, an upgrading client echoes an already-current
mixed token, the server fast-skips the catalog, and the policy never arrives until some unrelated admin catalog
write — possibly weeks later.

> **Correction to the server-side plan.** That plan said to latch the flag "only after a non-null
> `ClientPolicy` has applied". **Do not do that** — it is wrong against this codebase, and the existing latch
> comment explains why: a pre-upgrade *server* has no such channel, so waiting for a non-null block would keep
> every future pull unconditional and permanently lose the 304 fast path. Latch on the same rule the
> managed-persona flag uses: the forced unconditional pull **reached the persist point**, i.e. it returned 2xx
> and every apply on the page succeeded. Concretely, `latchStoreInitialized` is already `forceFullCatalog`;
> set both flags from it.

### C6 — clear the cache when the account changes

**File:** `Services/AuthService.cs`, in `LogoutAsync`.

`LogoutAsync` already has a block that clears cross-account sync state (`LastPushedSettingsHash`,
`LastPullETag`, `LastChatPullETag`) with a comment explaining that a later login must not inherit it. Delete
`policy-cache.json` and reset `ClientPolicyInitialized` in that same block, for the same reason: otherwise the
next user on this machine keeps enforcing the previous user's group policy.

This one genuinely needs doing rather than relying on self-healing. `LogoutAsync` sets `SyncEnabled = false`, so
no pull happens until the next user logs in and re-enables sync — until then the stale document is still being
applied on every start.

*(Observation, not a directive: `ManagedPersonaStoreInitialized` and the managed-persona store are **not**
cleared there today. That mostly self-heals, because `CatalogVersionMix` folds the caller's group into the token
so a different group's token cannot match. Worth raising separately; out of scope here.)*

---

## 5. Edge cases

**Sync disabled, or never signed in.** No server layer at all; the local file behaves exactly as today. This is
also why `syncEnabled` is a denied key — a server that could switch it off would be cutting the wire that
delivers its own policy.

**Offline start with a stale cache.** Use it as-is. There is deliberately **no TTL** — "the server said so three
days ago" beats no policy, the same reasoning as the managed-persona store.

**A policy change mid-session.** *Reversed by `docs/client_policy/2026-08-20-policy-apply-latency-design.md`, now implemented.*
The re-merge runs on the pull, one coordinator moves the values and only then raises a distinct `LocksChanged`,
and `PolicyLock` plus four per-VM handlers refresh the lock surface — so a change lands within one sync cycle
rather than at the next launch. What this paragraph got right is that the value/lock split matters: it is why
the notification is ordered values-first and why exactly one subscriber owns that order. Only `privacy` still
needs a restart, and only once the tokenization decision has been taken this process.

**First login is unconstrained.** A server-delivered `allowedSyncProviders` cannot gate the login that fetches
it, so it takes effect from the second launch. Pin it in `policy.json` if the very first login must be
constrained. Worth one line in the docs; not a bug.

**An enforced value persists after the policy is withdrawn.** Pre-existing, and out of scope: `ApplyPolicy`
writes into `AppSettings` and `SaveSettingsAsync` persists it, so removing an `enforce` key leaves the last
enforced value as the user's own. It becomes more visible now that changes come from a console rather than an
MDM push. The `AppliedDefaults` map added in C3 is the mechanism that could later fix this too — note it and
move on.

**E2EE accounts.** The document is plaintext by design; a group-shared row cannot be wrapped with one user's
UMK. Do not route it through any decrypt path and do not count it in decrypt-error stats.

---

## 6. Testing

The test project is `tests/Pia.Wpf.Tests/` (root namespace `Pia.Tests`), plain xUnit `Assert.*` with
NSubstitute. **FluentAssertions is not referenced — do not add it.** The gate is unfiltered `dotnet test` with
`failed: 0`, and the branch is not done until `dotnet build -t:Rebuild` reports `0 Warning(s)` in both Debug and
Release.

**`tests/Pia.Wpf.Tests/Services/PolicyServiceTests.cs`** (extend — it is already modified in your tree):

- A server-only document behaves exactly like a file-only one, for both sections.
- Both layers set the same key ⇒ the server value wins, in `defaults` and in `enforce`.
- A key only the local file sets survives a server document that omits it.
- A denied key in either layer is ignored, and `IsEnforced` returns false for it.
- The merged sections are non-null when only one layer set a key — assert through
  `IsLoginProviderAllowed`, since that is the path that fails open.
- Re-apply: a changed server default lands; a default the user has since changed themselves does not.
- Re-apply works for a collection-typed default (this is what pins the JSON-string comparison; reference
  equality passes the scalar cases and fails here).
- `ReplaceServerPolicyAsync("{}")` clears the server layer and leaves the file layer intact.
- `ReplaceServerPolicyAsync` does **not** change `IsEnforced` in the same process — the restart-only pin.

**`tests/Pia.Wpf.Tests/Sync/`** (new file, e.g. `SyncClientPolicyTests.cs`):

- Pin against **raw JSON strings**, not object construction: a body carrying `clientPolicy` deserializes
  non-null; one omitting the key deserializes to null. The absent-key contract is a serialization property, and
  this is the client-side mirror of the server's own wire test.
- A non-null channel calls `ReplaceServerPolicyAsync` exactly once with the document; a null channel never
  calls it.
- A present `{"document":"{}"}` calls it with `"{}"`.
- First run: with `ClientPolicyInitialized == false` the pull URL contains no `catalogVersion=` and the request
  carries no `If-None-Match`; after a successful page the flag is true and the next pull is conditional again.
- The flag latches even when the response carries **no** `clientPolicy` key — the old-server case. If this test
  is missing, the C5 correction can silently regress.

**`ClientPolicyContract`** already has 32 server-side tests. Mirror only what the client relies on rather than
duplicating the table: that `DeniedKeys` contains the three bootstrap keys, and that `Normalize` maps blank and
`"{}"` to null.

Verification:

```bash
cd C:\projects\Pia.Wpf
dotnet build -t:Rebuild -v:n          # and again -c Release: 0 Warning(s), 0 Error(s)
dotnet test                            # the gate
```

End-to-end, once the server branch is deployed:

1. In the admin console, set a group's policy to
   `{"defaults":{"uiLanguage":"DE"},"enforce":{"assistantFileToolsEnabled":false,"allowedSyncProviders":["entraid"]}}`.
2. Run the client as a member of that group, sync, **restart**. Expect German UI, the assistant file-tools
   switch greyed out, and only the Entra ID login button on the account page.
3. Change the default in the console, sync, restart — it re-applies. Now change it in the client first, change
   the console value again, sync, restart — it does not.
4. Clear the policy in the console, sync, restart — the locks are gone.
5. Sign out, sign in as a member of a different group — no trace of the first group's policy.

---

## 7. Docs to update in the server repo

Client-facing pages live in `Pia-Ai-dev/Pia` under `src/Pia.Docs/`, not in this repo. The server side already
documented itself (`server/admin/groups.mdx`, `server/architecture/sync.mdx`, `server/reference/api.mdx`). What
is left is the client reference, and it needs **all three languages** —
`wpf/reference/enterprise-policy.mdx` plus its `de/` and `fr/` copies:

- The second source, and the full precedence chain from §4.
- The `defaults` re-apply change (§C3), including that it now also affects file-only deployments.
- That server-managed policy takes effect within one sync cycle (10 s to 15 min), not at the next start, and
  that only a `privacy` change can still ask the user to restart.
- Which keys the server cannot set, and why.

While in there, fix an existing error: the page says the file lives at `%ProgramData%\Pia\policy.json`, but the
real search order is `<exeDir>\policy.json` → `<exeDir>\..\policy.json` → `%ProgramData%\Pia.Wpf\policy.json`.

---

## 8. Explicitly out of scope

- Any server change. Storage, the admin page, both endpoints, the pull channel and the shared validator are
  complete on `feature/group-client-policy`.
- Authoring or editing the policy from the client. There is no client-side admin surface and no endpoint the
  client is authorized to call.
- ~~Live (no-restart) application, and change notification for the `Is…Enforced` getters (§5).~~
  **No longer out of scope** — designed and implemented per
  `docs/client_policy/2026-08-20-policy-apply-latency-design.md`, Phase 0 and Phase 1. Its Phases 2 and 3 still owe the
  per-key `SettingsChanged` subscribers and the `AssistantFilesFolder` / `AllowedSyncProviders` specials.
- Widening enforce-lock coverage beyond the controls already bound to an `Is…Enforced` property. Most
  addressable settings still enforce invisibly — the value snaps back with no explanation. Real, and a separate
  piece of work.
- A "managed by your organisation" affordance on locked controls. Worth doing (the managed-persona badge plus
  `Msg_Settings_CannotEditManagedPersona` is the in-repo pattern to copy), but it is UI work independent of this
  contract and would need `.resx` entries in all three languages.
- Per-group Optimize templates, or any other admin-authored content channel.
- Fixing the enforced-value-persists-after-withdrawal wart (§5).
- Ordering or comparing `catalogVersion`. Opaque token: store and echo.

---

## 9. Open questions

Neither blocks implementation.

| # | Question | Suggested answer |
|---|---|---|
| Q1 | Should a locked control say *why*, and distinguish device policy from organisation policy? The merge knows which layer won, so the information is available — it is only the UI that is missing. | Ship the contract first; treat the affordance as its own change so it can carry the `.resx` work and a design pass. |
| Q2 | Should `ManagedPersonaStoreInitialized` and the managed-persona store also be cleared on logout (§C6)? | Probably yes, for symmetry — but it is pre-existing behaviour and changing it belongs in its own commit with its own test. |
