# Blank providers and templates after a recovery-key restore

**Status:** Diagnosed, fix in progress
**Owner:** Marco Altmann
**Written:** 2026-08-25
**Origin:** Customer reports against v1.3.206 — after restoring a device with the E2EE recovery
key, every custom provider renders as an unnamed `PiaCloud` row that cannot be deleted, and custom
Optimize templates render as empty cards.

## Symptom

Two reports, both after a Windows reinstall followed by a recovery-key restore onto the same
machine (so the server held the only copy of the data):

1. **Providers** — the list shows one correct `Pia Cloud` row followed by many rows with an empty
   name and the type `PiaCloud`. The delete button is absent; the edit button opens what looks like
   the "add provider" dialog. Some rows carry a *Kein Tool-Calling* badge, some do not.
2. **Templates** — custom Optimize templates render as cards with no title and no description,
   but with the edit and delete buttons of a user template.

## Root cause

`SyncMapper` maps every pull row with the shape

```csharp
if (IsE2EEActive && sync.EncryptedPayload is not null && sync.WrappedDek is not null && userId is not null)
{
    ... decrypt ...
}
return new AiProvider { Name = sync.Name ?? "", ProviderType = (AiProviderType)sync.ProviderType, ... };
```

The `if` is a *silent* condition, not a guard. When the row carries ciphertext but this client cannot
use it, control falls through to the plaintext branch — and the server deliberately blanks the
plaintext columns of an E2EE row. The result is an entity built entirely from defaults:

- `Name` → `""`
- `ProviderType` → `(AiProviderType)0`, which is `AiProviderType.PiaCloud`

Everything the customer sees follows from those two values:

| Observation | Mechanism |
|---|---|
| Rows say "PiaCloud" | `(AiProviderType)0 == PiaCloud` (`Models/AiProvider.cs:5`) |
| Name is empty | `sync.Name` is null on an E2EE row; `?? ""` |
| No delete button | `CanDeleteProvider` returns false for `ProviderType == PiaCloud` (`ProvidersSettingsViewModel.cs:376`) |
| Edit looks like "add" | `ProviderEditModel.FromProvider` on an all-blank row |
| Mixed *Kein Tool-Calling* | Server sets `SupportsToolCalling = false` when an E2EE push **updates** a row (`SyncService.cs:1281`) but leaves the entity default `true` when it **inserts** one (`ServerProvider.cs:13`). The badge therefore tracks server plaintext columns — proof the client rendered them |
| Templates are empty cards | Same fallback in `FromSyncTemplate`; `Name` and `Description` blank, `IsBuiltIn = false` |

The last row is the decisive evidence: nothing in the decrypted payload could produce a *mixed*
badge, but the server's two E2EE write paths produce exactly that split.

Confirmed two ways.

`tests/Pia.Wpf.Tests/Services/SyncMapperCiphertextFallbackTests.cs` pins the client half.

The server half was read off the live local Docker database rather than inferred — every E2EE row
already has the predicted shape:

```
providers:  Name | ProviderType | SupportsToolCalling | has_cipher
            -----+--------------+---------------------+-----------
            NULL |            0 | t                   | t      (x4)

templates:  Name | Prompt | has_cipher
            -----+--------+-----------
            NULL |   NULL | t          (x2)
```

`ProviderType = 0` is `PiaCloud`. A client reading those columns renders the screenshot exactly.

## Trigger

The fallback only fires when a pull carrying ciphertext runs while E2EE is inactive. On a restored
device that window is reachable deterministically:

1. Fresh profile, user logs in. `HandlePostLoginAsync` sees E2EE on the account and no local UMK,
   raises "onboarding required", and correctly does **not** start sync.
2. User goes looking for the recovery code and **restarts the app**. `App.xaml.cs:269` starts
   background sync on `IsLoggedIn` alone — it does not consult E2EE state.
3. Sync cycle 1: `settings.IsE2EEEnabled` is still false locally, so the first guard
   (`SyncClientService.cs:323`) is skipped. The second guard (`:333`) checks the server, sees E2EE
   on, and bails — but it sets `_hasVerifiedServerE2EEStatus = true` first.
4. Sync cycle 2 (~5 min later): guard 1 still skipped, guard 2 now skipped because the flag is set.
   Control reaches the pull. **Every E2EE row is written to the local store blank**, and the pull
   cursor advances past them.

The cursor is what makes it permanent: `PerformFirstSyncMigrationAsync` pulls from
`settings.LastSyncTimestamp`, not from zero, so completing the recovery afterwards never re-fetches
the rows it blanked.

## Why the data is also gone on the server

`PerformFirstSyncMigrationAsync` runs when onboarding finally completes, **pushes before it pulls**,
and pushes every local row with no dirty filter. By then E2EE is active, so it re-encrypts the
blanked rows and overwrites the server originals.

- **Providers survive.** The push filters `p.ProviderType != AiProviderType.PiaCloud`, and the
  blanked rows are all `PiaCloud`. The server ciphertext is intact and recoverable.
- **Templates do not.** No such filter, and the E2EE branch of `SyncPushValidator` does not require
  a name, so blank ciphertext is accepted and written over the original (`SyncService.cs:1107`).
- The same unfiltered push covers personas, sessions, memories, todos, kanban columns and scheduled
  jobs. Every one of those tables stores its content as ciphertext for an E2EE user (verified on the
  local server: personas 3/3, memories 2/2, todos 10/10, kanban columns 3/3, sessions 13/13), so all
  of them take the identical fallback and all of them are re-uploaded blank.

`SyncEvent` records only counts and a details string, so there is no server-side payload history to
recover from. Blanked templates need a database restore.

## Blast radius in the mapper

Nine mappers fall through silently. Two do not — `FromVaultSyncMemory` and `FromSyncAssistantChat`
already throw `InvalidOperationException` with the message "Incoming … is encrypted but E2EE is not
active on this client." The correct pattern already exists in the file; it was simply never applied
to the older entities.

| Mapper | Guarded |
|---|---|
| `FromSyncTemplate`, `FromSyncPersona`, `FromSyncProvider`, `FromSyncSession`, `FromSyncMemory`, `FromSyncTodo`, `FromSyncKanbanColumn`, `ApplySyncSettings`, `FromSyncScheduledJob` | no |
| `FromVaultSyncMemory`, `FromSyncAssistantChat` | yes |

## Separate bug found in the same code

`SyncTemplate.StyleDescription` carries `[JsonPropertyName("ExampleText")]`, but `ToSyncTemplate`
encrypts an anonymous object whose member is named `StyleDescription`. Decrypting back into
`SyncTemplate` looks for `ExampleText`, misses, and yields null — so a template's style description
is lost on **every** correct E2EE round-trip, independent of the fallback above.

## Reproduced in the real UI

A throwaway profile (`PIA_DATA_DIR`) seeded with a `providers.json` in the blanked state — one real
Pia Cloud row plus two rows with an empty name and `providerType: 0` — reproduces all three reported
symptoms, driven through UIA:

| Check | Blanked profile | After repair |
|---|---|---|
| `Provider_Delete_*` buttons | **0** | 2, enabled |
| `Provider_Edit_*` buttons | 3 | 3 |
| `ProviderEdit_Name` on opening edit | `""` | the provider name |
| `ProviderEdit_ProviderType` | `""` | the provider type |

An empty name and type is why the edit dialog is indistinguishable from the add dialog, and the
missing delete button is `CanDeleteProvider` refusing a PiaCloud row. Rewriting the same file with
real types and names — the state a successful repair pull produces — restores both.

## Fix

1. **Guard the mapper.** Ciphertext present + E2EE unusable must throw, never produce a default
   entity. Back-port the vault/chat pattern to the nine mappers above.
2. **Do not advance the cursor** over a pull the client could not decrypt. Abort the page, signal
   onboarding, keep `LastSyncTimestamp` where it was.
3. **Close the window.** Re-check server E2EE status every cycle rather than once per process, only
   latch the flag on a successful check, treat an unreachable server as unknown (do not sync), and
   give `PerformFirstSyncMigrationAsync` the readiness gate `SyncNowAsync` already has.
4. **Repair existing victims.** Detect blanked rows and force a full resync, which restores the
   providers from the intact server ciphertext.
5. Fix the `ExampleText` payload-name mismatch.

## Second hole: the unmigrated shell

Found while answering "so no server code change?". The account is flagged E2EE the moment the
recovery key is stored (`E2EERecoveryService.cs:72`) — before a single row has been encrypted. Until
each row is migrated the server takes its E2EE projection branch and emits **neither** ciphertext nor
plaintext: an empty shell that the client applies over real local data. Settings alone got this
right, pairing `isE2EE` with `EncryptedPayload is not null`; the other nine projections did not.

The guard above does not catch it — there is no ciphertext to detect.

It is durable, not just a startup window: `PerformFirstSyncMigrationAsync` contains no `catch`, so a
batch that fails mid-migration leaves the rest unencrypted on an account already flagged E2EE, and
the delta push only sends rows modified since the cursor, so it never revisits them.

Closed on both sides:

- **Server** — all nine projections now pair `isE2EE` with `EncryptedPayload is not null` and fall
  back to plaintext, matching Settings.
- **Client** — `DropUnmigratedShells` drops a ciphertext-free row while E2EE is active, leaving the
  local copy alone. Deliberately a per-row drop rather than a page refusal: a migration that died
  partway would otherwise wedge sync permanently. This also protects clients talking to an older
  server.

A note on what the server can never do: it cannot decrypt, so "ciphertext of an empty name" is
indistinguishable from "ciphertext of a real name". No server-side validation could have stopped the
blanked rows being pushed back over the originals — only the client can.
