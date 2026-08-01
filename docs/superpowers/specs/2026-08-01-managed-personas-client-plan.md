# Managed personas — WPF client implementation plan

**Status:** Ready for a build session. Grounded against `feature/agent-run-spine` on 2026-08-01.
**Source of truth for the contract:** `Pia/docs/plans/2026-08-01-managed-personas-wpf-handoff.md`
(server repo, branch `feature/managed-personas`). That document is self-contained and authoritative on
*what* the server sends. This document is *how* the client session should execute it, plus what grounding
found that the handoff does not say.

Read the handoff's §2 (contract), §4 (slices), §5 (edge cases), §6 (tests) before starting. §8 is settled
history — nothing there blocks this work. §7 is the do-not-do list and is binding.

---

## 1. What grounding confirmed

The handoff's file inventory is accurate. Every path it names exists on this branch, and the code shapes it
relies on are the shapes that are actually there:

| Claim in handoff | Verified |
|---|---|
| `Persona` has `IsBuiltIn`, `Archetype`, `Expertise`, `ToolScope`, `PreferredProviderId`, `ReasoningEffort` | ✅ `src/Pia.Wpf/Models/Persona.cs:24-45` |
| `PersonaService` has a reusable `MapPersona` reader + `AddPersonaParameters` writer | ✅ `PersonaService.cs:189,209` — the C1 "identical column list" trick works |
| `DeletePersonaAsync` calls `_deleteTracker.TrackDeletion("personas", id)` | ✅ `PersonaService.cs:159` — this is the push-safety line to guard |
| Two push builders filter `.Where(p => !p.IsBuiltIn)` | ✅ `SyncClientService.cs:483` (first-sync) and `:714` (incremental) |
| "Apply plugins" block precedes the `LastCatalogVersion` persist | ✅ `SyncClientService.cs:1524` vs `:1539` — insert the managed apply between them |
| `catalogVersion` is only ever compared for equality client-side | ✅ `SyncClientService.cs:995` uses `!=`. No ordering exists today; keep it that way |
| `PiaCloudChatClient` sets `X-Pia-Mode` in `SendWithAuthRetryAsync` | ✅ `PiaCloudChatClient.cs:30,51-52` — mirror position exactly |
| `PersonasView.xaml` keys Edit/Delete off `IsBuiltIn` + inverse converter | ✅ lines `99` and `111`; the `Personas_BuiltIn` badge is at `120` |
| `PersonaSettingsViewModel` guards are `IsBuiltIn`-shaped | ✅ `:82`, `:102`, `:133` — all three become `IsReadOnly` |
| `SqliteContext` has `CREATE TABLE IF NOT EXISTS Personas` + a `PRAGMA table_info` migration idiom | ✅ `:225` (EnsureSchema) and `:595-633` (defensive re-check) — both need the `ManagedPersonas` twin |
| Test project is xUnit + NSubstitute, no FluentAssertions | ✅ confirmed; `tests/Pia.Wpf.Tests/Sync/` exists and holds `SyncPullResponseSerializationTests.cs`, the natural sibling for the new wire test |

### Two corrections to the handoff

1. **Handler count.** §4/C5 says `IAiProviderHandler` has "7 handler implementations" and "six of the seven
   ignore it". There are **8**: `AzureOpenAi`, `Mistral`, `Ollama`, `OpenAi`, `OpenAiCompatible`,
   `OpenRouter`, `PiaCloud`, `VLlm`. So **seven** ignore the new parameter and only `PiaCloudProviderHandler`
   forwards it. Cosmetic, but a session that trusts the count will think it missed a file.
2. **`IAiClientService` has more `mode` sites than named.** The handoff names two methods; grep finds
   `string? mode = null` at five positions in the interface. Thread the new parameter through whichever of
   those actually reach `CreateChatClientAsync` with a real mode, and pass `null` at the
   title-generation / optimize / prompt-generation sites, per the handoff's own rule.

---

## 2. Prerequisite — the `Pia.Shared` DTOs (resolved locally, still unpushed)

§3 of the handoff says the promotion is "done — the DTOs are already in the submodule". That was true of the
submodule *checkout* inside the server repo, not of this working clone. State as of 2026-08-01:

- **Server side is pushed.** `Pia` origin has `feature/managed-personas` at `b67793f`, matching local head.
- **Client DTO commit was not.** `Pia/lib/Pia.Wpf` is a clone of the same origin (`Pia-Ai-dev/Pia.Wpf`) on
  branch `feature/managed-personas` at **`f6b6bbf` — "feat(sync): add the managedPersonas pull channel to
  Pia.Shared"**, with no upstream tracking ref. `Pia.Wpf` origin has no such branch.
- **Worked around locally:** `f6b6bbf` has been fetched over the filesystem into this clone as branch
  **`managed-personas-dtos`**. `SyncManagedPersona.cs` and `SyncPullResponse.ManagedPersonas` are now
  reachable here, so the build session is unblocked without the network.

`f6b6bbf` touches exactly two files (`src/Pia.Shared/Models/SyncManagedPersona.cs` +38,
`src/Pia.Shared/Sync/SyncPullResponse.cs` +57/-1) and sits on a `main` that is 4 commits behind current
`origin/main`, none of them related. It cherry-picks cleanly.

**Still outstanding (release step, not build work):** push `f6b6bbf` to `Pia-Ai-dev/Pia.Wpf` so the server
branch stops referencing a submodule commit nobody else can fetch, then commit the new submodule SHA in the
server repo. Needs credentials this environment does not have.

Verify before writing client code:
```bash
grep -n "ManagedPersonas" src/Pia.Shared/Sync/SyncPullResponse.cs   # must find the nullable property
ls src/Pia.Shared/Models/SyncManagedPersona.cs                      # must exist
```

Do not hand-retype the DTOs. The server's wire test is pinned against that exact shape; a re-typed copy that
drifts is precisely the failure the promotion was meant to prevent.

---

## 3. Branch — base on `feature/agent-run-spine`

`feature/agent-run-spine` is **242 commits ahead of `origin/main` and 0 behind**, and is in sync with its
remote. It is the current state of the client; `main` is stale. Base the work there:

```bash
git checkout -b feature/managed-personas-client feature/agent-run-spine
git cherry-pick f6b6bbf
```

The cherry-pick is **verified conflict-free** (`git merge-tree` clean): `agent-run-spine` touches neither
`src/Pia.Shared/Models/SyncManagedPersona.cs` nor `src/Pia.Shared/Sync/SyncPullResponse.cs`.

`f6b6bbf` is published as `origin/managed-personas-dtos`. That branch exists only to carry the commit to the
server for the submodule pointer — **do not merge it into anything**, it forks off a stale `main`.

### What agent-run-spine changed under the managed-persona files

All grounding in §1 was taken **on this branch**, so the line numbers are already correct for this base. But
agent-run-spine has recent commits in the files C1–C5 touch, and a build session should read them before
editing:

| Commit | Relevance |
|---|---|
| `7a41a68` Providers: let each handler say whether tools cost it reasoning effort | Touches `IAiProviderHandler` — the same interface C5 extends with `Guid? managedPersonaId`. Read the current signature before adding a parameter. |
| `1c49b08` Sync: preserve the device-local provider fields across a pull | Touches `SyncClientService`/`SyncMapper` — the C3 apply site. |
| `e4ad6bf` Db: give the shared connection WAL and a busy timeout | Touches `SqliteContext` — the C1 DDL site. |
| `8add90c` Sync: assert the E2EE vault envelope carries an empty Data | Sync test conventions; useful precedent for the C6 wire tests. |
| `b2f46a2`, `50d2054`, `1ceb9a4` agent-run substrate | Adjacent, not overlapping. Awareness only. |

None of these conflict with the managed-persona work — they are the reason to branch from here rather than
from `main`.

---

## 4. Slice order and dependency shape

The handoff's C1…C6 order is correct and should not be resequenced. The dependency graph is what determines
what a workflow can parallelize:

```
Step 0 (Pia.Shared DTOs reachable)
   │
   ├── C1  model flag + ManagedPersonas table      ─┐
   │        Persona.IsManaged / IsReadOnly          │
   │        SqliteContext DDL + PRAGMA migration    │
   │                                                │
   ├── C2  PersonaService / IPersonaService  ◄──────┤ (needs C1's table + flag)
   │        GetManagedPersonasAsync                 │
   │        ReplaceManagedPersonasAsync             │
   │        merged ordering + id-collision guards   │
   │        write-path rejection of managed ids     │
   │                                                │
   ├── C3  SyncClientService + SyncMapper    ◄──────┘ (needs C2's replace method)
   │        FromSyncManagedPersona
   │        replace-all apply before catalog persist
   │        ManagedPersonaStoreInitialized first-run
   │        push filters + log line
   │
   ├── C4  UI  ◄── needs C1 (IsReadOnly) only — can run parallel with C3
   │        PersonasView badge + IsReadOnly rebind
   │        3 VM guards + snackbars
   │        3 × .resx keys (en/de/fr)
   │
   ├── C5  chat header  ◄── independent of C1–C4 entirely — can run first or parallel
   │        X-Pia-Persona through 8 handlers + IAiClientService + 3 callers
   │
   └── C6  tests  ◄── per-slice, written with each slice, not batched at the end
```

**Parallelizable:** C5 is fully independent (it touches no persona storage) and C4 needs only C1. A workflow
can fan out `C1 → {C2 → C3, C4}` and `C5` concurrently, then join for C6 and verification. C2 and C3 are
strictly sequential.

**Not parallelizable:** anything touching `SyncClientService.PullPageAsync`, `PersonaService.cs`, or the
three `.resx` files — single-writer each.

---

## 5. The five things most likely to be got wrong

Flag these explicitly to whatever agents do the building; they are the load-bearing subtleties.

1. **Absent key ≠ empty.** `ManagedPersonas is { } managed` — apply only when non-null. Null means the
   catalog fast-skip fired; keep the store. Present-and-empty means clear it. Getting these two backwards
   silently wipes every user's managed personas on the first conditional pull.
2. **Replace-all, never merge.** Unassignment carries no tombstone. `SyncManagedPersonaSnapshot` is a
   distinct type precisely so the merge helper cannot be reached for — do not "normalize" it into a
   `SyncEntityChanges<T>`.
3. **`catalogVersion` is opaque.** Store and echo, equality-compare only. Grep for `>`/`<`/`Math.Max` on
   `LastCatalogVersion` at the end of the session; the existing code at `SyncClientService.cs:995` is already
   correct and must stay that way.
4. **Never write a managed id to the delete tracker.** `PersonaService.cs:159` is the exact line. A managed
   id reaching `TrackDeletion("personas", …)` enqueues a push tombstone — the server quarantines it, but the
   client contract is to never emit it. Pin this with a test that asserts the tracker's persisted state.
5. **Managed rows never touch the E2EE path.** They have no `encryptedPayload`/`wrappedDek` by design. Route
   through `FromSyncManagedPersona`, never the decrypt branch of `FromSyncPersona`, and do not increment
   `decryptionErrors` for them.

---

## 6. Open decisions for the build session

The handoff closed all sixteen of its own questions. These are the ones *this* plan raises, and none block
starting:

| # | Question | Suggested default |
|---|---|---|
| P1 | ✅ Resolved for building — `f6b6bbf` fetched locally as `managed-personas-dtos`. Pushing it to `Pia-Ai-dev/Pia.Wpf` is still owed. | Push before the server branch merges; §3 of the handoff names it as a release step. Not a build blocker. |
| P2 | ✅ Resolved — branch `origin/main`, cherry-pick `f6b6bbf`. | See §3. |
| P3 | §5.1 asks for a "one-shot informational snackbar" when a selected managed persona vanishes. Where does the one-shot state live? | Not specified by the handoff. Simplest: raise `PersonasChanged` from the C3 apply and let `PersonaSettingsViewModel` show it on next refresh; no new persisted flag. Worth confirming before building. |
| P4 | German/French strings for the four new resource keys. | The handoff supplies `Personas_Managed` in all three languages. `Msg_Settings_CannotEditManagedPersona`, `Msg_Settings_CannotDeleteManagedPersona` and `Personas_ManagedTooltip` need de/fr translations written. |

---

## 7. Verification

Per `CLAUDE.md`'s Zero-Warning Policy, this is not commit-ready until a **rebuild** reports `0 Warning(s)`
and `0 Error(s)` in Debug *and* Release:

```bash
dotnet build -t:Rebuild -v:n
dotnet build -t:Rebuild -v:n -c Release
dotnet test tests/Pia.Wpf.Tests/Pia.Wpf.Tests.csproj
```

Use a rebuild — an incremental build does not re-emit warnings from projects it skips. Read the count off
MSBuild's `N Warning(s)` summary line rather than grepping, since `-v:n` prints each warning twice. The WPF
markup-compile pass re-reports `src/` warnings under a generated `Pia.Wpf_<hash>_wpftmp.csproj`; fixing the
source clears both. Suppress narrowly (`#pragma warning disable <ID>` + why-comment) if a warning is
genuinely wrong; never project-wide `<NoWarn>`.

**Environment caveat:** the test project targets `net10.0-windows10.0.17763.0` and only *runs* on Windows.
On this Mac, `dotnet build -p:EnableWindowsTargeting=true` compiles but `dotnet test` cannot execute —
defer the run to Windows or CI. Record the pre-change pass/fail baseline there before starting and treat any
new failure as this session's.

**Privacy-first logging** applies to everything logged here. Persona names, taglines, system prompts,
guardrails and output formats are all user-named/payload content: log ids and counts at Information, and put
any name or prompt behind `SensitiveDebug`. The C3 log line the handoff asks for (`ManagedPersonas: {N}u/{M}d`)
is counts-only and is fine as written.

**Manual smoke** (needs a running server on `feature/managed-personas`): publish a managed persona to a
group, confirm it appears with a Managed badge and no Edit/Delete, duplicate it and confirm the copy is an
ordinary syncing user persona, then unassign the group and confirm the row disappears on the next pull and
the selection falls back to the operating-mode built-in without an error.
