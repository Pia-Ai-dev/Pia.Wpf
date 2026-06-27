# Relocatable assistant folder + nested vault — design

**Date:** 2026-06-27
**Status:** Approved (brainstorming), pending implementation plan
**Branch:** feature/memory_update

## Goal

Let the user choose where Pia keeps the assistant's files and memory, with two
hard structural rules and a safe relocation flow:

1. The **assistant files folder** must always be **below the user profile** (`%USERPROFILE%`).
2. The **vault** must always be **below the assistant files folder**.
3. Changing the folder **copies → verifies → deletes** the old data behind a progress dialog.
4. The default location is **`%USERPROFILE%\Documents\Pia Assistant`**.

## Decisions (from brainstorming)

| # | Decision | Choice |
|---|----------|--------|
| Folder model | How the two folders relate | **The files folder IS the assistant folder.** The vault is a fixed `Vault` subfolder of it. |
| Disable file tools | Replacement for "clear path = disable" | **Separate on/off toggle** (`AssistantFileToolsEnabled`). The folder always has a value. |
| Apply move | Re-point after the copy | **Live hot-swap** — re-point the vault store, restart the watcher, refresh the file-tool root in-process. No restart. |
| Existing users | Upgrade behavior | **Migrate in place** — keep the current files folder, move the legacy vault to `<folder>\Vault`. New installs default to Documents. |
| Vault access | File-tool access to vault files | **Full read + write.** The vault is a normal part of the sandbox; the watcher keeps the index consistent. Memory tools remain the *preferred* path for structured edits. |

## Current architecture (as-is)

- **Files folder:** `AppSettings.AssistantFilesFolder` (nullable; null/empty disables file tools).
  Default `%LOCALAPPDATA%\Pia\workdir` (`AssistantWorkspace.DefaultWorkdir`), seeded in `App.OnStartup`.
  Consumed by `FilesToolHandler` (sandbox root, hot-repoints via `ISettingsService.SettingsChanged`)
  and `WorkingDirectoryService` (reads per call).
- **Vault:** `VaultPathProvider.VaultRoot` → `%LOCALAPPDATA%\Pia\Vault`. The get-only root is captured
  by `VaultStore` (ctor), read dynamically by `VaultSchemaService`, and read once by `VaultWatcher.Start()`.
  `VaultStore`/`VaultWatcher`/`VaultIndexer`/`VaultIndexService`/`VaultSyncService`/`VaultMigrationRunner`
  are singletons.
- **Path safety:** `SafeFolderPath` (containment + `Canonicalize` via `GetFinalPathNameByHandle`),
  `SensitivePathGuard.IsBlocked` (denylist of `%LOCALAPPDATA%\Pia`, system/credential dirs; carve-out
  for the default workdir). **`IsBlocked` is enforced on every file op** — read, write, delete, list, search.
- **Sync:** `SyncBaseStore` lives at `%LOCALAPPDATA%\Pia\SyncBase` (internal 3-way-merge base state,
  keyed by frontmatter `id` GUID). **Not user data — stays put, never moved.**
- **Progress UI precedent:** `IDialogService.ShowModelDownloadDialogAsync(name, IProgress<…>, ct)` →
  `ModelDownloadResult(Completed, Cancelled)`, a determinate progress `ContentDialog` with phases.

## Target architecture

### 1. Settings (`AppSettings`)

- `AssistantFilesFolder` (string) — the anchor; non-empty after first run.
- **New** `AssistantFileToolsEnabled` (bool, default `true`) — gates the file tools, replacing
  the "clear to disable" behavior.
- **New** `AssistantFolderLayoutVersion` (int, default `0`) — idempotency marker for the in-place
  vault migration. Distinct from `VaultVersion` (which tracks the SQLite→vault migration).

The vault path is **derived, never stored**: `vaultRoot = Path.Combine(AssistantFilesFolder, "Vault")`.

### 2. Defaults & constants (`AssistantWorkspace`)

- `DefaultRoot` → `Path.Combine(GetFolderPath(UserProfile), "Documents", "Pia Assistant")`.
  Literal `%USERPROFILE%\Documents` join (not `SpecialFolder.MyDocuments`) so an OneDrive-redirected
  Documents cannot push the default outside the profile and violate Rule 1.
- `LegacyWorkdir` → the existing `%LOCALAPPDATA%\Pia\workdir`. Retained solely so
  `SensitivePathGuard`'s carve-out keeps file tools working for migrate-in-place users whose folder
  stays inside the blocked `%LOCALAPPDATA%\Pia`. New installs (Documents) sit outside all blocked
  roots and need no carve-out.
- `VaultSubfolderName` → `"Vault"`. `VaultRootFor(filesFolder)` helper returns the derived vault path.

### 3. Runtime-mutable vault root (single source of truth)

- `VaultPathProvider.VaultRoot` becomes settable (`SetRoot(string)`); stays in Infrastructure (no
  settings dependency). A Services-layer coordinator computes `VaultRootFor(AssistantFilesFolder)`
  and calls `SetRoot` at startup and after a move.
- `VaultStore` reads `Root` from the provider dynamically (`Root => _paths.VaultRoot`) instead of
  capturing it in its ctor — so existing memory writes pick up a re-point with no reconstruction.
- `VaultSchemaService` already reads `_paths.VaultRoot` dynamically — no change.
- `SyncBaseStore` unchanged (stays in `%LOCALAPPDATA%\Pia\SyncBase`).

### 4. Relocation engine (`IAssistantFolderRelocationService`)

One core, used by both the user-initiated move and the startup in-place migration. Reports
`IProgress<FolderMoveProgress>` with phases **Copying → Verifying → Cleaning up** (mirroring
`ModelDownloadPhase`).

**Data-safety / rollback — the old tree is the source of truth until verify passes:**

1. Validate target (§7).
2. Copy old → new (entire files folder, including its `Vault` subtree).
3. Verify: file-count + per-file size across the tree; content hash over the `Vault` subtree.
4. **On any failure before the delete step:** keep the old tree intact, delete the partial new copy,
   surface the error, stay pointed at the old location. No data loss is possible at any point.
5. Only after verify passes: re-point (§5), then delete the old tree.

### 5. Hot-swap sequencing (quiescence)

A lightweight async **vault write gate** is the one chokepoint — all memory writes funnel through
`VaultStore.WriteAtomicAsync`. The move:

1. Acquire the write gate (block new memory writes; drain in-flight).
2. `Dispose()` the `VaultWatcher` **first** (releases the directory handle so Windows allows the
   old root to be deleted).
3. Copy → verify (§4).
4. Re-point:
   - `VaultPathProvider.SetRoot(newVaultRoot)`.
   - Save `AssistantFilesFolder` → raises `SettingsChanged` → `FilesToolHandler` re-points (already
     wired); `WorkingDirectoryService` reads per call (auto).
5. Delete the old tree.
6. `VaultWatcher.Start(newRoot)` + **full reindex by enumeration** — copied files raise no watcher
   `Created` events, so the index must be rebuilt explicitly, not awaited from the watcher.
7. Release the write gate.

The move is a user-initiated settings action performed while no assistant turn is active, so a
concurrent file-tool write into the vault during the swap window is not a practical concern; the
write gate covers the realistic concurrent writer (background memory operations).

### 6. Vault & file tools (no carve-out)

The vault is a normal part of the sandbox. `read/write/delete/list/search` all operate on vault
files like any other file. The `VaultWatcher` re-indexes external edits idempotently (its designed
behavior — the same path a human editor takes), and the MemoryView reads from disk. The dedicated
memory tools remain the **preferred** path for structured edits because they are frontmatter/section
-aware (byte-range splices preserve the `id` GUID and section structure that sync's 3-way merge
depends on); this preference is reinforced via tool/prompt guidance, **not** a filesystem block.

`SensitivePathGuard` keeps its existing behavior unchanged: `%LOCALAPPDATA%\Pia` (DB/config) and
system/credential dirs stay blocked, with the `LegacyWorkdir` carve-out retained for migrate-in-place
users. No vault-specific guard entry is added.

### 7. Folder validation (Rules 1 & 2) — using existing secure primitives

On folder pick, the target is **grounded through the same secure methods the file tools use**, not
ad-hoc string checks:

- `Path.GetFullPath` then `SafeFolderPath.Canonicalize` (resolves junctions/symlinks via
  `GetFinalPathNameByHandle`) on the picked path and on `%USERPROFILE%`.
- **Under-profile check (Rule 1):** trailing-separator-aware containment — canonicalized target must
  start with `SafeFolderPath.WithTrailingSeparator(canonical %USERPROFILE%)` (the same primitive
  `SafeFolderPath`/`SensitivePathGuard` use), case-insensitive.
- **Not-blocked check:** `SensitivePathGuard.IsBlocked(canonicalTarget)` must be false.
- **No self-nesting:** reject if the target is inside the current files folder or current vault
  (canonicalized containment), to prevent copying a tree into itself.

Rule 2 is structural and needs no separate validation — the vault is always `<folder>\Vault`.

Any failed check rejects the selection with a localized message and performs **no move**.

### 8. Existing-user migration (in-place, startup, one-shot)

If `AssistantFolderLayoutVersion < 1` at startup:

- If the legacy vault exists at `%LOCALAPPDATA%\Pia\Vault` and `AssistantFilesFolder` is set,
  move it to `<AssistantFilesFolder>\Vault` via the relocation engine (copy → verify → delete).
- Set `AssistantFolderLayoutVersion = 1`.

Runs **before** `VaultSchemaService.EnsureScaffolding` and `VaultWatcher.Start` (so they bind to the
nested location). The existing files folder is otherwise untouched; `%LOCALAPPDATA%\Pia\workdir`
remains valid under Rule 1 (LocalAppData is under the profile), so there is **no forced relocation**.
New installs seed `AssistantFilesFolder = DefaultRoot`, create the folder + `Vault` subfolder, and
set the layout version to `1`.

### 9. Settings UI (`AssistantView.xaml` + `AssistantSettingsViewModel`)

- Replace TextBox + Browse + Clear with: a read-only path display + a **Change…** button (validate →
  confirm → move-with-progress) + an **"Allow assistant to read/write files"** checkbox bound to
  `AssistantFileToolsEnabled`.
- Remove the `ClearFilesFolder` command.
- Add a sub-line showing the derived vault path (`<folder>\Vault`).
- New progress dialog: a determinate `ContentDialog` mirroring `ModelDownloadContentDialog`
  (`ShowFolderMoveDialogAsync(IProgress<FolderMoveProgress>, ct)` on `IDialogService`).
- Update `Settings_AssistantFilesFolder_*` strings + new keys in en/de/fr resx.

## Components & boundaries

| Unit | Responsibility | Depends on |
|------|----------------|------------|
| `AssistantWorkspace` | Default/legacy/vault path constants + `VaultRootFor` | — |
| `VaultPathProvider` (mutable) | Single source of the current vault root | — |
| `IAssistantFolderRelocationService` | Validate → copy → verify → delete → re-point; reports progress | `VaultPathProvider`, `VaultWatcher`, indexer, settings, write gate |
| vault write gate | Quiesce memory writes during the swap | — (used by `VaultStore` + relocation) |
| `IDialogService.ShowFolderMoveDialogAsync` | Determinate progress dialog | `ModelDownload*` precedent |
| `AssistantSettingsViewModel` | Folder display, toggle, Change… command | relocation service, dialog service, settings |

## Error handling

- **Verify failure / copy failure:** old intact, partial new deleted, error surfaced, no re-point.
- **Delete-old failure (after successful re-point):** non-fatal — new location is authoritative;
  log + surface a "couldn't remove old folder at X" warning; leftover old tree is harmless.
- **Watcher restart / reindex failure:** log; index can be rebuilt on next startup.
- **Validation failure:** localized rejection message, no move.

## Testing

- `VaultPathProvider` mutability + `VaultRootFor` derivation.
- Relocation engine: success (copy/verify/delete), verify-failure rollback (old intact + partial new
  cleaned), target validation matrix (outside profile, blocked path, self-nesting), delete-old-fails
  is non-fatal.
- Path grounding reuses `SafeFolderPath`/`SensitivePathGuard` (junction/symlink + containment cases).
- In-place migration: legacy vault → nested, runs once, idempotent across restarts.
- Settings VM: `AssistantFileToolsEnabled` toggle gates `FilesToolHandler.IsAvailable`; Change…
  validation paths.
- Watcher full-reindex-by-enumeration after a move (no reliance on `Created` events).

## Out of scope

- Moving the SQLite DB or `SyncBase` (internal app state, stays in `%LOCALAPPDATA%\Pia`).
- A hard filesystem block on vault edits (explicitly rejected — full access chosen).
- Per-folder sync reconfiguration (sync keys on frontmatter `id`, path-independent).
