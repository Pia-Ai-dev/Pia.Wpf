# UMK rotation: removed broken stub + spec for a correct implementation

**Date:** 2026-06-07
**Status:** **Resolved by removal.** The broken `ReKeyAsync` stub was deleted; no data-preserving UMK rotation exists in the client today.
**Component:** `Pia.Wpf` — `DeviceManagementService` / `IDeviceManagementService` (`src/Pia.Wpf/Services/E2EE/`)
**Related:** [`docs/plans/2026-06-07-post-quantum-e2ee-migration.md`](plans/2026-06-07-post-quantum-e2ee-migration.md) — the PQ migration's one-time UMK rotation must implement the correct routine specified below.

---

## TL;DR

A method named `ReKeyAsync` used to exist on `DeviceManagementService`. It generated a **new UMK** and overwrote the old one locally, on the server, and in the recovery blob — but it **never re-wrapped the existing per-record DEKs** (encrypted under the *old* UMK) and **never re-distributed the new UMK to the account's other devices**. Calling it would have caused silent, permanent data loss (single-device account) or a UMK split-brain (multi-device account).

It had **no callers** — it was a latent footgun, not an active bug. Because it looked finished (right name, returned a fresh recovery code) it was an attractive nuisance: wiring up any "rotate key / reset encryption" UI action, or implementing the PQ migration by reaching for it, would have triggered the loss.

**It has been removed** (method + interface declaration) rather than marked `[Obsolete]`, because `[Obsolete]` is only a warning and would have left the trap callable. This document is retained as the **specification** for the correct, data-preserving rotation the PQ migration must build.

---

## Background: the key hierarchy (why a naive UMK swap is destructive)

From `E2EEService` (`src/Pia.Wpf/Services/E2EE/E2EEService.cs`):

```
record payload : AES-GCM(DEK, json)                 // EncryptRecord:99
wrapped DEK    : AES-GCM(UMK, DEK)   ← per record   // EncryptRecord:103
UMK            : 32 random bytes, DPAPI-stored      // GenerateAndStoreUmkAsync:35, StoreUmkAsync:62
```

Every record stores its own `WrappedDek = AES-GCM(UMK, DEK)`. To decrypt a record you must first unwrap its DEK **with the UMK** (`DecryptRecord:121`). The UMK is the single root: change it, and **every** existing `WrappedDek` becomes un-unwrappable unless it is re-wrapped under the new UMK.

---

## What the removed `ReKeyAsync` did (for the record)

```csharp
var umk = await _e2ee.GenerateAndStoreUmkAsync();        // (1) NEW UMK; overwrites local DPAPI + cache → UMK_old gone locally
var (selfWrapped, hkdfSalt) = _e2ee.WrapUmkForSelf();    // (2) self-wrap UMK_new
await UploadWrappedUmkAsync(deviceId, ...);              //     → server SetWrappedUmk OVERWRITES this device's blob (was UMK_old-wrap)
var recoveryCode = _recovery.GenerateRecoveryCode();     // (3) new recovery code
var recoveryBlob = _recovery.WrapUmkForRecovery(umk, ..);//     wraps UMK_new
await UploadRecoveryWrappedUmkAsync(recoveryBlob);       //     → OVERWRITES recovery blob (was UMK_old-wrap)
settings.E2EEUmkVersion++;                               // (4) bump version
return recoveryCode;
```

**What it never did:**

1. Re-wrap the existing records' `WrappedDek` from `UMK_old` to `UMK_new`. No such re-wrap routine exists anywhere in the client.
2. Re-distribute `UMK_new` to the account's **other** active devices. Only the calling device's `ServerWrappedUmk` was updated (step 2). Every other device's server blob still wrapped `UMK_old`.
3. Preserve `UMK_old` anywhere it could be recovered from on a single-device account.

---

## Blast radius (had it ever been wired up)

### Case A — single-device account (common): permanent total loss
After step (1) the local DPAPI copy of `UMK_old` is overwritten; step (2) overwrites the server's only wrapped copy; step (3) overwrites the recovery blob. **`UMK_old` exists nowhere.** Every record's `WrappedDek` is `AES-GCM(UMK_old, DEK)` and can never be unwrapped again → **all encrypted data permanently unrecoverable.** Ciphertext is intact but cryptographically orphaned.

### Case B — multi-device account: split-brain + partial loss
`UMK_old` survives only on the *other* devices (their local DPAPI and server wrapped-UMK blobs are untouched). Result:

- The **re-keying device** holds `UMK_new` → can no longer decrypt any pre-rekey record (those DEKs are `UMK_old`-wrapped).
- **Other devices** still hold `UMK_old` → can't decrypt anything the re-keying device writes under `UMK_new`.
- The **recovery blob** now yields `UMK_new`, so recovery cannot restore access to `UMK_old`-encrypted records.
- New-device onboarding wraps "the UMK" the approver happens to hold → outcome depends on which device approves. **No convergence mechanism exists** (nothing compares local vs. server `UmkVersion` to trigger a re-fetch).

---

## Root cause

A UMK rotation is a **fan-out re-keying operation**, but `ReKeyAsync` treated it as a **local key swap**. It changed the root of the key hierarchy without (a) migrating the data that depends on the old root, or (b) propagating the new root to the other holders of the old root. The bumped `E2EEUmkVersion` implied an awareness that consumers should react, but no consumer does.

> **Note — the sync layer is *not* a rotation mechanism.** Every `WrappedDek` write goes through `SyncMapper`, which calls `EncryptRecord` to wrap a *fresh* DEK under the *current* UMK at push time. This only re-wraps records this device still holds as **plaintext locally** (the decrypt path writes plaintext back and nulls `WrappedDek`). Records that exist only on the server — other devices' data — can never be re-wrapped this way, because reading them requires `UMK_old`, which a rotation destroys.

---

## Specification for a correct rotation (build this when the PQ migration needs it)

Replace the local key-swap with a **data-preserving, fleet-consistent rotation**. Do **not** overwrite `UMK_old` until the migration has succeeded.

1. **Hold `UMK_old`**; generate `UMK_new` in memory (don't `StoreUmkAsync` yet).
2. **Re-wrap every DEK:** for each record, `dek = Decrypt(UMK_old, wrappedDek)`, `wrappedDek' = Encrypt(UMK_new, dek)`; push the updated `WrappedDek`. **Payloads are not touched** (same DEK, same `AES-GCM(DEK, json)`), so this is a small per-record field update, not a re-encrypt.
3. **Re-distribute `UMK_new` to every active device** (wrap for each device's public key via `WrapUmkForDevice`) and regenerate the recovery blob, **atomically** with the version bump — ideally a server-side transactional "publish UMK vN+1" so devices flip together. Devices detect the `UmkVersion` change and re-fetch.
4. Only after all of the above commit: `StoreUmkAsync(UMK_new)` locally and discard `UMK_old`.
5. **Guard rails:** make the rotation idempotent/resumable (a crash mid-rotation must not strand records under a half-applied key); refuse to rotate while any active device is on an old app version that can't re-fetch; keep `UMK_old` retrievable until the new version is confirmed everywhere.

The reusable building blocks already exist (`EncryptRecord`/`DecryptRecord` for re-wrapping a DEK, `WrapUmkForDevice` for fan-out); what was missing — and remains missing — is the routine that orchestrates them plus the server-side transactional publish.

---

## Relationship to the post-quantum migration

The PQ migration plan calls for a **one-time UMK rotation** (to close the "harvest-now-decrypt-later" window on the old ECDH-wrapped UMK). That rotation **must** implement the specification above — **not** resurrect the deleted `ReKeyAsync`. Concretely, the PQ one-time rotation = the spec in this doc + wrapping `UMK_new` with the hybrid `ECDH P-256 ∥ ML-KEM-768` KEM. Implementing a correct, data-preserving rotation is therefore a **prerequisite** for Phase 5 of the migration.
