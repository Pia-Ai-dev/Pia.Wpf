# Relocatable Assistant Folder + Nested Vault — Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let the user relocate the assistant files folder (vault nested inside it) to any folder under `%USERPROFILE%`, with a copy→verify→delete move behind a progress dialog, defaulting to `%USERPROFILE%\Documents\Pia Assistant`.

**Architecture:** The existing `AssistantFilesFolder` setting stays the single anchor; the vault is the derived `<folder>\Vault`. The vault root becomes runtime-mutable (`VaultPathProvider.SetRoot`) so a move hot-swaps in-process: a write gate quiesces memory writes, the watcher is stopped/restarted, the index is rebuilt, and `SettingsChanged` re-points the file tools. A reusable `SafeDirectoryMove` (copy→verify→delete with rollback) powers both the user-initiated move and a one-shot in-place migration that nests existing users' legacy vault under their folder. The vault is a normal part of the sandbox (full file-tool access); no carve-out is added.

**Tech Stack:** C# / .NET 10, WPF + WPF-UI (`ContentDialog`), CommunityToolkit.Mvvm, xUnit.v3 on Microsoft.Testing.Platform (MTP).

**Spec:** `docs/superpowers/specs/2026-06-27-relocatable-assistant-folder-design.md`

---

## Conventions for every task

- **Line endings:** new `.cs` files in this repo are CRLF. After creating a file with the Write tool (which emits LF), convert it to CRLF before committing (e.g. `git add --renormalize` is not enough — use a CRLF rewrite). Existing-file edits preserve the file's endings.
- **Logging:** any path/foldername that reaches a log must use `SensitiveDebug` or be hashed (see `VaultWatcher.HashPath`). Never log full folder paths at Information level. Folder-name is a "user-named item" per CLAUDE.md.
- **Run a single test class (TDD inner loop):**
  `dotnet test tests/Pia.Wpf.Tests/Pia.Wpf.Tests.csproj -- --filter-class "<FullNamespace.ClassName>"`
- **Gate (must pass before declaring a chunk done):**
  `dotnet test tests/Pia.Wpf.Tests/Pia.Wpf.Tests.csproj -- --filter-not-namespace "Pia.Wpf.Tests.Integration.Providers"`
  Per the project baseline there are ~18 known live-network failures inside the `Integration.Providers` namespace (10 OpenRouter + 8 VLlm); gate on **no failures outside** that namespace, not on a raw count. Verify the exact excluded-namespace string against the baseline before relying on it.
- **Build:** `dotnet build` (the WPF project is `net10.0-windows`).
- **Namespaces:** source uses `Pia` (not `Pia.Wpf`); test namespaces are `Pia.Tests.*`.

---

## File Structure

**New files**
- `src/Pia.Wpf/Infrastructure/Vault/IVaultWriteGate.cs` — async quiescence gate interface.
- `src/Pia.Wpf/Infrastructure/Vault/VaultWriteGate.cs` — mutex-backed gate.
- `src/Pia.Wpf/Infrastructure/Vault/AssistantFolderValidator.cs` — secure grounding of a picked folder (Rules 1 & 2).
- `src/Pia.Wpf/Infrastructure/Vault/SafeDirectoryMove.cs` — copy→verify→delete with rollback + progress.
- `src/Pia.Wpf/Services/Interfaces/IAssistantFolderRelocationService.cs` — relocation contract + `FolderMoveProgress`/`FolderMovePhase`/`FolderMoveResult`.
- `src/Pia.Wpf/Services/AssistantFolderRelocationService.cs` — orchestrates the hot-swap.
- `src/Pia.Wpf/Views/Dialogs/FolderMoveContentDialog.xaml` (+ `.xaml.cs`) — determinate progress dialog.
- Tests: `tests/Pia.Wpf.Tests/Vault/VaultWriteGateTests.cs`, `AssistantFolderValidatorTests.cs`, `SafeDirectoryMoveTests.cs`, `AssistantFolderRelocationServiceTests.cs`, `AssistantWorkspaceTests.cs`; extend `VaultPathProviderTests.cs`, `VaultWatcherTests.cs`, `VaultStoreTests.cs`.

**Modified files**
- `src/Pia.Wpf/Models/AppSettings.cs` — `AssistantFileToolsEnabled`, `AssistantFolderLayoutVersion`.
- `src/Pia.Wpf/Infrastructure/AssistantWorkspace.cs` — `DefaultRoot`, `LegacyWorkdir`, `VaultSubfolderName`, `VaultRootFor`.
- `src/Pia.Wpf/Infrastructure/Vault/VaultPathProvider.cs` — mutable root via `SetRoot`.
- `src/Pia.Wpf/Infrastructure/Vault/VaultStore.cs` — dynamic root from provider + write-gate around writes.
- `src/Pia.Wpf/Services/VaultWatcher.cs` — `Stop()` / `Restart(root)`.
- `src/Pia.Wpf/Infrastructure/SensitivePathGuard.cs` — carve-out keyed on `AssistantWorkspace.LegacyWorkdir` (back-compat).
- `src/Pia.Wpf/Bootstrapper.cs` — DI registrations + `InitializeAssistantFoldersAsync` before scaffolding.
- `src/Pia.Wpf/App.xaml.cs` — remove the old `AssistantFilesFolder` seeding block (moved into Bootstrapper).
- `src/Pia.Wpf/Services/FilesToolHandler.cs` — gate availability on `AssistantFileToolsEnabled` (in addition to a configured folder).
- `src/Pia.Wpf/Services/Interfaces/IDialogService.cs` + `DialogService.cs` — `ShowFolderMoveDialogAsync`.
- `src/Pia.Wpf/ViewModels/AssistantSettingsViewModel.cs` — toggle, Change… command, remove Clear, vault path display.
- `src/Pia.Wpf/Views/SettingsViews/AssistantView.xaml` — new controls.
- `src/Pia.Wpf/Resources/Strings/ViewStrings.resx` (+ `.de.resx`, `.fr.resx`) — new/updated strings.

---

## Chunk 1: Settings & path foundations

### Task 1.1: AppSettings fields

**Files:** Modify `src/Pia.Wpf/Models/AppSettings.cs:110-112`

- [ ] **Step 1:** After the `AssistantFilesFolder` property, add:

```csharp
// True when the assistant's file tools (read/write/delete/list/search) are exposed over
// AssistantFilesFolder. The folder is always set (the vault lives under it), so file-tool
// enablement is a distinct flag rather than "clear the folder to disable".
public bool AssistantFileToolsEnabled { get; set; } = true;

// Layout-migration marker, distinct from VaultVersion (SQLite->vault). 0 = pre-nesting
// (legacy vault at %LOCALAPPDATA%\Pia\Vault, sibling of workdir); 1 = vault nested under
// AssistantFilesFolder. Set once the in-place nesting migration completes on this device.
public int AssistantFolderLayoutVersion { get; set; } = 0;
```

- [ ] **Step 2:** Build: `dotnet build`. Expected: success.
- [ ] **Step 3:** Commit: `feat(settings): add AssistantFileToolsEnabled + AssistantFolderLayoutVersion`

### Task 1.2: AssistantWorkspace constants + helper

**Files:** Modify `src/Pia.Wpf/Infrastructure/AssistantWorkspace.cs`; Test `tests/Pia.Wpf.Tests/Vault/AssistantWorkspaceTests.cs`

- [ ] **Step 1: Write the failing test** (`Pia.Tests.Vault.AssistantWorkspaceTests`):

```csharp
using System;
using System.IO;
using Pia.Infrastructure;
using Xunit;

namespace Pia.Tests.Vault;

public class AssistantWorkspaceTests
{
    [Fact]
    public void DefaultRoot_is_PiaAssistant_under_user_profile_Documents()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var expected = Path.Combine(profile, "Documents", "Pia Assistant");
        Assert.Equal(expected, AssistantWorkspace.DefaultRoot);
        Assert.StartsWith(profile, AssistantWorkspace.DefaultRoot);
    }

    [Fact]
    public void LegacyWorkdir_is_workdir_under_local_app_data_Pia()
    {
        var lad = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Assert.Equal(Path.Combine(lad, "Pia", "workdir"), AssistantWorkspace.LegacyWorkdir);
    }

    [Fact]
    public void VaultRootFor_appends_Vault_subfolder()
    {
        Assert.Equal(Path.Combine(@"C:\x\y", "Vault"), AssistantWorkspace.VaultRootFor(@"C:\x\y"));
    }
}
```

- [ ] **Step 2: Run → FAIL** (members don't exist).
- [ ] **Step 3: Implement.** Replace the body of `AssistantWorkspace` with:

```csharp
public static class AssistantWorkspace
{
    /// <summary>Vault subfolder name under the assistant files folder.</summary>
    public const string VaultSubfolderName = "Vault";

    /// <summary>
    /// Default assistant files folder for new installs: <c>%USERPROFILE%\Documents\Pia Assistant</c>.
    /// Built from the literal profile + "Documents" (not SpecialFolder.MyDocuments) so an OneDrive-
    /// redirected Documents cannot push the default outside the profile and break the "under %USERPROFILE%"
    /// rule.
    /// </summary>
    public static string DefaultRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Documents", "Pia Assistant");

    /// <summary>
    /// Legacy default workdir (<c>%LOCALAPPDATA%\Pia\workdir</c>). Retained ONLY so
    /// <see cref="SensitivePathGuard"/> keeps carving it out of the otherwise-blocked
    /// <c>%LOCALAPPDATA%\Pia</c> for migrate-in-place users whose folder stays there.
    /// </summary>
    public static string LegacyWorkdir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Pia", "workdir");

    /// <summary>The vault root derived from an assistant files folder: <c>&lt;folder&gt;\Vault</c>.</summary>
    public static string VaultRootFor(string filesFolder) =>
        Path.Combine(filesFolder, VaultSubfolderName);
}
```

- [ ] **Step 4: Run → PASS.**
- [ ] **Step 5:** Update the comment in `SensitivePathGuard.BuildAllowedExceptions` (Task 5.4 fully wires this) — leave guard code untouched for now; just confirm `dotnet build` passes (the old `DefaultWorkdir` member is removed, so fix the single reference in `App.xaml.cs` / `SensitivePathGuard` if the build breaks — see note). If build breaks on `DefaultWorkdir`, temporarily point those references at `AssistantWorkspace.LegacyWorkdir` to keep the build green; Tasks 5.3/5.4 finalize them.
- [ ] **Step 6:** Commit: `feat(vault): AssistantWorkspace DefaultRoot/LegacyWorkdir/VaultRootFor`

> Note: the old `AssistantWorkspace.DefaultWorkdir` is referenced by `SensitivePathGuard.BuildAllowedExceptions` and `App.xaml.cs`. Replacing it with `LegacyWorkdir` (same value) keeps behavior identical; the App.xaml.cs seeding reference is removed entirely in Task 5.3.

### Task 1.3: VaultPathProvider mutable root

**Files:** Modify `src/Pia.Wpf/Infrastructure/Vault/VaultPathProvider.cs`; extend `tests/Pia.Wpf.Tests/Vault/VaultPathProviderTests.cs`

- [ ] **Step 1: Add failing test:**

```csharp
[Fact]
public void SetRoot_updates_VaultRoot()
{
    var provider = new VaultPathProvider("/initial");
    provider.SetRoot("/changed");
    Assert.Equal("/changed", provider.VaultRoot);
}

[Fact]
public void SetRoot_rejects_blank()
{
    var provider = new VaultPathProvider("/initial");
    Assert.Throws<ArgumentException>(() => provider.SetRoot("  "));
}
```

- [ ] **Step 2: Run → FAIL.**
- [ ] **Step 3: Implement.** Change the `VaultRoot` property + add `SetRoot`:

```csharp
private volatile string _vaultRoot;

public string VaultRoot => _vaultRoot;

public VaultPathProvider() : this(DefaultRoot()) { }

public VaultPathProvider(string root) => _vaultRoot = root;

/// <summary>
/// Re-point the vault root at runtime (folder relocation / startup derivation). Visibility is via
/// the volatile field; readers (VaultStore.Root) observe the new value on their next access.
/// </summary>
public void SetRoot(string root)
{
    if (string.IsNullOrWhiteSpace(root))
        throw new ArgumentException("Vault root must be non-empty.", nameof(root));
    _vaultRoot = root;
}
```

Keep the existing `DefaultRoot()` (`%LOCALAPPDATA%\Pia\Vault`) as the pre-`SetRoot` fallback so the existing default test still passes.

- [ ] **Step 4: Run → PASS** (both old and new tests).
- [ ] **Step 5:** Commit: `feat(vault): make VaultPathProvider root runtime-mutable`

### Task 1.4: VaultStore reads root dynamically

**Files:** Modify `src/Pia.Wpf/Infrastructure/Vault/VaultStore.cs:19-26`; check `tests/Pia.Wpf.Tests/Vault/VaultStoreTests.cs`

- [ ] **Step 1: Add failing test** (`VaultStoreTests`): construct via provider and assert re-point is observed:

```csharp
[Fact]
public void Root_follows_provider_after_SetRoot()
{
    var provider = new VaultPathProvider(@"C:\a");
    var store = new VaultStore(provider, new MarkdownVaultParser());
    Assert.Equal(@"C:\a", store.Root);
    provider.SetRoot(@"C:\b");
    Assert.Equal(@"C:\b", store.Root);
}
```

- [ ] **Step 2: Run → FAIL** (no provider ctor).
- [ ] **Step 3: Implement.** Replace ctor + Root:

```csharp
private readonly VaultPathProvider _paths;
private readonly MarkdownVaultParser _parser;

// Production ctor: shares the DI VaultPathProvider so a runtime re-point is observed live.
public VaultStore(VaultPathProvider paths, MarkdownVaultParser parser)
{
    _paths = paths;
    _parser = parser;
}

// Back-compat / test ctor: a fixed root, wrapped in a private provider.
public VaultStore(string root, MarkdownVaultParser parser)
    : this(new VaultPathProvider(root), parser) { }

/// <inheritdoc />
public string Root => _paths.VaultRoot;
```

- [ ] **Step 4:** Update `Bootstrapper.cs:210-212` registration:

```csharp
services.AddSingleton<IVaultStore>(sp => new VaultStore(
    sp.GetRequiredService<VaultPathProvider>(),
    sp.GetRequiredService<MarkdownVaultParser>()));
```

- [ ] **Step 5: Run → PASS**; `dotnet build`. The string ctor keeps all other `VaultStore` test sites compiling unchanged.
- [ ] **Step 6:** Commit: `feat(vault): VaultStore root tracks VaultPathProvider`

### Chunk 1 gate
- [ ] Run the gate command. Expected: no failures outside `Integration.Providers`.

---

## Chunk 2: Secure folder validation (Rules 1 & 2)

### Task 2.1: AssistantFolderValidator

**Files:** Create `src/Pia.Wpf/Infrastructure/Vault/AssistantFolderValidator.cs`; Test `tests/Pia.Wpf.Tests/Vault/AssistantFolderValidatorTests.cs`

Reuses existing secure primitives only: `SafeFolderPath.Canonicalize` / `WithTrailingSeparator` and `SensitivePathGuard.IsBlocked`. No ad-hoc string checks.

- [ ] **Step 1: Write failing tests:**

```csharp
using System;
using System.IO;
using Pia.Infrastructure.Vault;
using Xunit;

namespace Pia.Tests.Vault;

public class AssistantFolderValidatorTests : IDisposable
{
    private readonly string _profile;
    private readonly string _temp;

    public AssistantFolderValidatorTests()
    {
        _profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _temp = Path.Combine(_profile, "pia-validator-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temp);
    }

    public void Dispose() { try { Directory.Delete(_temp, true); } catch { } }

    [Fact]
    public void Folder_under_profile_is_ok()
    {
        Assert.Equal(FolderValidation.Ok, AssistantFolderValidator.Validate(_temp, currentFolder: null));
    }

    [Fact]
    public void Folder_outside_profile_is_rejected()
    {
        var outside = Path.Combine(Path.GetTempPath(), "pia-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        try
        {
            // Only meaningful when TEMP is not under the profile (CI default). Skip if it is.
            if (outside.StartsWith(_profile, StringComparison.OrdinalIgnoreCase)) return;
            Assert.Equal(FolderValidation.OutsideUserProfile,
                AssistantFolderValidator.Validate(outside, null));
        }
        finally { try { Directory.Delete(outside, true); } catch { } }
    }

    [Fact]
    public void Nesting_target_inside_current_folder_is_rejected()
    {
        var child = Path.Combine(_temp, "child");
        Directory.CreateDirectory(child);
        Assert.Equal(FolderValidation.NestedInCurrent,
            AssistantFolderValidator.Validate(child, currentFolder: _temp));
    }
}
```

- [ ] **Step 2: Run → FAIL.**
- [ ] **Step 3: Implement:**

```csharp
using System.IO;
using System.Linq;
using Pia.Infrastructure; // SafeFolderPath, SensitivePathGuard

namespace Pia.Infrastructure.Vault;

public enum FolderValidation
{
    Ok,
    OutsideUserProfile,   // Rule 1
    BlockedPath,          // system / Pia-data / credential dir
    NestedInCurrent,      // would copy a tree into itself
    NotEmpty,             // existing target already has content (merge ambiguity / rollback risk)
    Invalid,              // unusable path
}

/// <summary>
/// Grounds a user-picked assistant files folder against the structural rules, reusing the same
/// secure path primitives the file tools use (canonicalization + trailing-separator containment +
/// the sensitive-path denylist). Rule 2 (vault under the folder) is structural and not checked here.
/// </summary>
public static class AssistantFolderValidator
{
    public static FolderValidation Validate(string candidate, string? currentFolder)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return FolderValidation.Invalid;

        string canonical, profile;
        try
        {
            canonical = CanonicalizeExistingOrLexical(candidate);
            profile = CanonicalizeExistingOrLexical(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile));
        }
        catch { return FolderValidation.Invalid; }

        // Rule 1: strictly under %USERPROFILE% (trailing-separator-aware, case-insensitive).
        var profileWithSep = SafeFolderPath.WithTrailingSeparator(profile);
        if (!canonical.StartsWith(profileWithSep, System.StringComparison.OrdinalIgnoreCase))
            return FolderValidation.OutsideUserProfile;

        // Never a system / Pia-data / credential dir.
        if (SensitivePathGuard.IsBlocked(canonical, out _))
            return FolderValidation.BlockedPath;

        // No copying a tree into itself / its own vault.
        if (!string.IsNullOrWhiteSpace(currentFolder))
        {
            var curr = CanonicalizeExistingOrLexical(currentFolder!);
            var currWithSep = SafeFolderPath.WithTrailingSeparator(curr);
            if (canonical.Equals(curr, System.StringComparison.OrdinalIgnoreCase))
                return FolderValidation.Ok; // same folder = no-op move, allowed
            if (canonical.StartsWith(currWithSep, System.StringComparison.OrdinalIgnoreCase))
                return FolderValidation.NestedInCurrent;
        }

        // An existing, non-empty target makes the merge ambiguous and the rollback unsafe (a failed
        // verify could delete the user's pre-existing files). Require an empty/new folder — the Win32
        // folder picker has a "New folder" button, so this is easy to satisfy.
        try
        {
            if (Directory.Exists(canonical) &&
                Directory.EnumerateFileSystemEntries(canonical).Any())
                return FolderValidation.NotEmpty;
        }
        catch { return FolderValidation.Invalid; }

        return FolderValidation.Ok;
    }

    private static string CanonicalizeExistingOrLexical(string path)
    {
        var full = Path.GetFullPath(path);
        return Directory.Exists(full) ? SafeFolderPath.Canonicalize(full) : full;
    }
}
```

- [ ] **Step 4: Run → PASS.**
- [ ] **Step 5:** Commit: `feat(vault): secure assistant-folder validator (Rule 1 + nesting + denylist)`

### Chunk 2 gate
- [ ] Run the gate command. Expected: no new failures.

---

## Chunk 3: Write gate & watcher restart

### Task 3.1: VaultWriteGate

**Files:** Create `IVaultWriteGate.cs` + `VaultWriteGate.cs` in `src/Pia.Wpf/Infrastructure/Vault/`; Test `tests/Pia.Wpf.Tests/Vault/VaultWriteGateTests.cs`

A single async mutex: each vault write enters/exits; relocation takes the same lock exclusively for the swap window, so in-flight writes drain before the move and new writes block until it completes. Vault writes are infrequent, so serializing them is acceptable.

- [ ] **Step 1: Write failing tests:**

```csharp
using System.Threading.Tasks;
using Pia.Infrastructure.Vault;
using Xunit;

namespace Pia.Tests.Vault;

public class VaultWriteGateTests
{
    [Fact]
    public async Task Exclusive_blocks_until_writer_releases()
    {
        var gate = new VaultWriteGate();
        var writer = await gate.EnterWriteAsync();
        var exclusive = gate.EnterExclusiveAsync();
        Assert.False(exclusive.IsCompleted);   // blocked while writer holds it
        writer.Dispose();
        var handle = await exclusive;           // now proceeds
        handle.Dispose();
    }

    [Fact]
    public async Task Writer_blocks_while_exclusive_held()
    {
        var gate = new VaultWriteGate();
        var exclusive = await gate.EnterExclusiveAsync();
        var writer = gate.EnterWriteAsync();
        Assert.False(writer.IsCompleted);
        exclusive.Dispose();
        (await writer).Dispose();
    }
}
```

- [ ] **Step 2: Run → FAIL.**
- [ ] **Step 3: Implement** `IVaultWriteGate.cs`:

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Pia.Infrastructure.Vault;

/// <summary>
/// Quiesces vault writes during a folder relocation. All memory writes funnel through
/// VaultStore.WriteAtomicAsync, which holds a write lease; the relocation takes an exclusive lease
/// for the swap window so in-flight writes drain and new writes block until the move completes.
/// </summary>
public interface IVaultWriteGate
{
    Task<IDisposable> EnterWriteAsync(CancellationToken ct = default);
    Task<IDisposable> EnterExclusiveAsync(CancellationToken ct = default);
}
```

`VaultWriteGate.cs`:

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Pia.Infrastructure.Vault;

public sealed class VaultWriteGate : IVaultWriteGate
{
    private readonly SemaphoreSlim _sem = new(1, 1);

    public Task<IDisposable> EnterWriteAsync(CancellationToken ct = default) => AcquireAsync(ct);
    public Task<IDisposable> EnterExclusiveAsync(CancellationToken ct = default) => AcquireAsync(ct);

    private async Task<IDisposable> AcquireAsync(CancellationToken ct)
    {
        await _sem.WaitAsync(ct).ConfigureAwait(false);
        return new Releaser(_sem);
    }

    private sealed class Releaser(SemaphoreSlim sem) : IDisposable
    {
        private SemaphoreSlim? _sem = sem;
        public void Dispose() => Interlocked.Exchange(ref _sem, null)?.Release();
    }
}
```

- [ ] **Step 4: Run → PASS.**
- [ ] **Step 5:** Commit: `feat(vault): VaultWriteGate quiescence primitive`

### Task 3.2: VaultStore acquires the gate around writes

**Files:** Modify `src/Pia.Wpf/Infrastructure/Vault/VaultStore.cs`

- [ ] **Step 1: Add failing test** (writes still succeed with an injected gate; existing splice tests remain green):

```csharp
[Fact]
public async Task WriteAtomic_succeeds_with_injected_gate()
{
    var dir = Path.Combine(Path.GetTempPath(), "vs-" + Guid.NewGuid().ToString("N"));
    var store = new VaultStore(new VaultPathProvider(dir), new MarkdownVaultParser(), new VaultWriteGate());
    await store.WriteAtomicAsync("memory/a.md", "---\nid: x\n---\nhi");
    Assert.True(File.Exists(Path.Combine(dir, "memory", "a.md")));
    Directory.Delete(dir, true);
}
```

- [ ] **Step 2: Run → FAIL** (no 3-arg ctor).
- [ ] **Step 3: Implement.** Add an optional gate to both ctors and wrap every write/splice/delete entry point:

```csharp
private readonly IVaultWriteGate _gate;

public VaultStore(VaultPathProvider paths, MarkdownVaultParser parser, IVaultWriteGate? gate = null)
{
    _paths = paths;
    _parser = parser;
    _gate = gate ?? new VaultWriteGate();
}

public VaultStore(string root, MarkdownVaultParser parser)
    : this(new VaultPathProvider(root), parser) { }
```

In `WriteAtomicAsync` (and any other mutating method — section splice, delete), wrap the body:

```csharp
public async Task WriteAtomicAsync(string relativePath, string content)
{
    using var _ = await _gate.EnterWriteAsync().ConfigureAwait(false);
    // ... existing body unchanged ...
}
```

> Apply the same `using var _ = await _gate.EnterWriteAsync()` to every public method of `VaultStore` that writes or deletes a file. Reads (`ReadAsync`, `EnumerateAsync`) are NOT gated.

- [ ] **Step 4: Run → PASS**; re-run the full `Vault` test folder to confirm splice/atomic tests are unaffected.
- [ ] **Step 5:** Commit: `feat(vault): gate VaultStore writes through VaultWriteGate`

### Task 3.3: VaultWatcher Stop / Restart

**Files:** Modify `src/Pia.Wpf/Services/VaultWatcher.cs`; Test `tests/Pia.Wpf.Tests/Vault/VaultWatcherTests.cs`

- [ ] **Step 1: Add failing test** — after `Stop()`, a `Restart(newRoot)` indexes changes under the new root (follow the existing `VaultWatcherTests` style for timing/debounce):

```csharp
[Fact]
public async Task Restart_rebinds_to_new_root()
{
    // Arrange a watcher started on rootA, then Restart on rootB; a file created under rootB
    // must drive _indexer.IndexFileAsync; one created under rootA must NOT.
    // (Mirror the existing tests' FakeIndexer + debounce-wait helper.)
}
```

- [ ] **Step 2: Run → FAIL.**
- [ ] **Step 3: Implement.** Add to `VaultWatcher` (refactor `Dispose`'s watcher-teardown into `Stop`):

```csharp
/// <summary>Stop watching and release the directory handle, leaving the instance reusable.</summary>
public void Stop()
{
    if (_watcher is not null)
    {
        _watcher.EnableRaisingEvents = false;
        _watcher.Created -= OnChangedOrCreated;
        _watcher.Changed -= OnChangedOrCreated;
        _watcher.Renamed -= OnRenamed;
        _watcher.Deleted -= OnDeleted;
        _watcher.Error -= OnError;
        _watcher.Dispose();
        _watcher = null;
    }
    foreach (var timer in _pending.Values) timer.Dispose();
    _pending.Clear();
}

/// <summary>Stop and re-start on a new root (used by folder relocation).</summary>
public void Restart(string root)
{
    ObjectDisposedException.ThrowIf(_disposed, this);
    Stop();
    Start(root);
}
```

Refactor `Dispose()` to call `Stop()` then set `_disposed = true`. `Start()` already creates a fresh watcher when `_watcher == null`, so it works after `Stop()`.

- [ ] **Step 4: Run → PASS.**
- [ ] **Step 5:** Commit: `feat(vault): VaultWatcher Stop/Restart for re-pointing`

### Chunk 3 gate
- [ ] Run the gate command. Expected: no new failures.

---

## Chunk 4: Safe directory move + relocation service

### Task 4.0: Pre-flight — confirm no consumer caches the vault root

The hot-swap only works if no singleton captures `VaultPathProvider.VaultRoot` or `IVaultStore.Root`
in a field at construction. **Already audited at plan time** (record here so the implementer re-confirms
after the Chunk 1 ctor changes):

- [ ] Run `grep -rn "VaultPathProvider\|\.VaultRoot" src/` — expected hits ONLY: `VaultPathProvider`
  itself, `Bootstrapper` (registration, changed in 1.4/5.1), `VaultWatcher.Start()` (per-call),
  `VaultSchemaService` (per-call in `EnsureScaffolding`). A `SyncBaseStore` comment is fine.
- [ ] Run `grep -rn "_root\s*=\|store\.Root\|_store\.Root" src/` — expected `_root` fields ONLY in
  `SyncBaseStore` (`%LOCALAPPDATA%\Pia\SyncBase`, never moved) and `VaultWatcher` (re-set on `Restart`).
- [ ] Confirm `VaultIndexer`/`VaultIndexService`/`VaultSyncService`/`VaultLogService`/`SectionUpsertService`/
  `IngestService`/`MemoryService` reach the filesystem through `IVaultStore` methods (dynamic `Root`),
  not a cached field. If any caches a path, convert it to read `IVaultStore.Root`/`VaultPathProvider.VaultRoot`
  per-call before proceeding.

### Task 4.1: SafeDirectoryMove (copy → verify → delete, with rollback)

**Files:** Create `src/Pia.Wpf/Infrastructure/Vault/SafeDirectoryMove.cs`; Test `tests/Pia.Wpf.Tests/Vault/SafeDirectoryMoveTests.cs`

Progress types live with the relocation interface (Task 4.2) — to avoid a forward dependency, define them in `IAssistantFolderRelocationService.cs` first OR define the move-level progress here and reuse it. **Decision:** define `FolderMovePhase` + `FolderMoveProgress` in this file's namespace (`Pia.Infrastructure.Vault`) and have the service interface re-export/use them.

- [ ] **Step 1: Write failing tests:**

```csharp
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Pia.Infrastructure.Vault;
using Xunit;

namespace Pia.Tests.Vault;

public class SafeDirectoryMoveTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "sdm-" + Guid.NewGuid().ToString("N"));
    public SafeDirectoryMoveTests() => Directory.CreateDirectory(_base);
    public void Dispose() { try { Directory.Delete(_base, true); } catch { } }

    [Fact]
    public async Task Move_copies_tree_then_deletes_source()
    {
        var src = Path.Combine(_base, "src");
        var dst = Path.Combine(_base, "dst");
        Directory.CreateDirectory(Path.Combine(src, "Vault", "memory"));
        await File.WriteAllTextAsync(Path.Combine(src, "Vault", "memory", "a.md"), "x");
        await File.WriteAllTextAsync(Path.Combine(src, "doc.txt"), "hello");

        var result = await SafeDirectoryMove.MoveAsync(src, dst, progress: null, CancellationToken.None);

        Assert.Equal(DirectoryMoveOutcome.Success, result.Outcome);
        Assert.False(Directory.Exists(src));
        Assert.Equal("x", await File.ReadAllTextAsync(Path.Combine(dst, "Vault", "memory", "a.md")));
        Assert.Equal("hello", await File.ReadAllTextAsync(Path.Combine(dst, "doc.txt")));
    }

    [Fact]
    public async Task Verify_failure_keeps_source_and_removes_partial_dst()
    {
        // Inject a verify failure by pointing dst at a path that cannot be fully written,
        // OR expose an internal seam to force verify=false. Simplest: a test-only overload
        // SafeDirectoryMove.MoveAsync(src, dst, progress, ct, verifyOverride: () => false).
        var src = Path.Combine(_base, "src2");
        var dst = Path.Combine(_base, "dst2");
        Directory.CreateDirectory(src);
        await File.WriteAllTextAsync(Path.Combine(src, "a.txt"), "data");

        var result = await SafeDirectoryMove.MoveAsync(src, dst, null, CancellationToken.None,
            verifyOverride: () => false);

        Assert.Equal(DirectoryMoveOutcome.VerifyFailed, result.Outcome);
        Assert.True(Directory.Exists(src));            // source intact
        Assert.True(File.Exists(Path.Combine(src, "a.txt")));
        Assert.False(Directory.Exists(dst));           // partial copy removed
    }
}
```

- [ ] **Step 2: Run → FAIL.**
- [ ] **Step 3: Implement:**

```csharp
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Pia.Infrastructure.Vault;

public enum FolderMovePhase { Copying, Verifying, CleaningUp }

public record FolderMoveProgress(FolderMovePhase Phase, int PercentComplete, string? CurrentItem = null);

public enum DirectoryMoveOutcome { Success, CopyFailed, VerifyFailed }

public record DirectoryMoveResult(DirectoryMoveOutcome Outcome, string? Error = null);

/// <summary>
/// Copy → verify → delete a directory tree with rollback. The source is the source of truth until
/// verify passes: any failure before the delete step keeps the source intact and removes the partial
/// destination. Used by both the user-initiated folder move and the startup in-place vault migration.
/// </summary>
public static class SafeDirectoryMove
{
    // The copy/verify/delete is synchronous I/O; run it on the thread pool so a UI-thread caller
    // (the settings command awaiting a progress dialog) does not freeze. Progress<T> marshals the
    // report callback back to the captured (UI) context on its own.
    public static Task<DirectoryMoveResult> MoveAsync(
        string source, string destination,
        IProgress<FolderMoveProgress>? progress,
        CancellationToken ct,
        Func<bool>? verifyOverride = null)
        => Task.Run(() => MoveCore(source, destination, progress, ct, verifyOverride), ct);

    private static DirectoryMoveResult MoveCore(
        string source, string destination,
        IProgress<FolderMoveProgress>? progress,
        CancellationToken ct,
        Func<bool>? verifyOverride)
    {
        if (!Directory.Exists(source))
            return new DirectoryMoveResult(DirectoryMoveOutcome.Success); // nothing to move

        // Only a destination WE created may be wiped on rollback — never delete a pre-existing
        // user folder. (Validation also rejects a non-empty target, so this is defense in depth.)
        var destExistedBefore = Directory.Exists(destination);

        try
        {
            // 1) COPY
            var files = Directory.GetFiles(source, "*", SearchOption.AllDirectories);
            var total = Math.Max(files.Length, 1);
            Directory.CreateDirectory(destination);
            for (int i = 0; i < files.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                var rel = Path.GetRelativePath(source, files[i]);
                var target = Path.Combine(destination, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(files[i], target, overwrite: true);
                progress?.Report(new FolderMoveProgress(
                    FolderMovePhase.Copying, (int)((i + 1) * 100L / total), rel));
            }

            // 2) VERIFY
            progress?.Report(new FolderMoveProgress(FolderMovePhase.Verifying, 100));
            var ok = verifyOverride?.Invoke() ?? Verify(source, destination);
            if (!ok)
            {
                if (!destExistedBefore) TryDelete(destination);
                return new DirectoryMoveResult(DirectoryMoveOutcome.VerifyFailed,
                    "Verification of the copied folder failed.");
            }

            // 3) DELETE SOURCE
            progress?.Report(new FolderMoveProgress(FolderMovePhase.CleaningUp, 100));
            TryDelete(source); // delete-source failure is non-fatal: dest is authoritative
            return new DirectoryMoveResult(DirectoryMoveOutcome.Success);
        }
        catch (OperationCanceledException)
        {
            if (!destExistedBefore) TryDelete(destination);
            return new DirectoryMoveResult(DirectoryMoveOutcome.CopyFailed, "Cancelled.");
        }
        catch (Exception ex)
        {
            if (!destExistedBefore) TryDelete(destination);
            return new DirectoryMoveResult(DirectoryMoveOutcome.CopyFailed, ex.Message);
        }
    }

    private static bool Verify(string source, string destination)
    {
        var srcFiles = Directory.GetFiles(source, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(source, f), f => new FileInfo(f).Length,
                          StringComparer.OrdinalIgnoreCase);
        var dstFiles = Directory.GetFiles(destination, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(destination, f), f => f,
                          StringComparer.OrdinalIgnoreCase);

        if (srcFiles.Count != dstFiles.Count) return false;
        foreach (var (rel, size) in srcFiles)
        {
            if (!dstFiles.TryGetValue(rel, out var dstPath)) return false;
            if (new FileInfo(dstPath).Length != size) return false;
            // Hash the Vault subtree (memory integrity); size-check suffices elsewhere.
            if (rel.Replace('\\', '/').StartsWith("Vault/", StringComparison.OrdinalIgnoreCase))
            {
                var srcPath = Path.Combine(source, rel);
                if (!HashEquals(srcPath, dstPath)) return false;
            }
        }
        return true;
    }

    private static bool HashEquals(string a, string b)
    {
        using var sha = SHA256.Create();
        using var fa = File.OpenRead(a);
        using var fb = File.OpenRead(b);
        return sha.ComputeHash(fa).AsSpan().SequenceEqual(SHA256.HashData(fb));
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { /* non-fatal */ }
    }
}
```

> The `verifyOverride` param is a test seam (default `null`). Keep it `internal`-friendly but `public` is acceptable here for test access without `InternalsVisibleTo`.

- [ ] **Step 4: Run → PASS.**
- [ ] **Step 5:** Commit: `feat(vault): SafeDirectoryMove with verify + rollback`

### Task 4.2: AssistantFolderRelocationService

**Files:** Create `IAssistantFolderRelocationService.cs` + `AssistantFolderRelocationService.cs`; Test `tests/Pia.Wpf.Tests/Vault/AssistantFolderRelocationServiceTests.cs`

- [ ] **Step 1: Define the interface** (`src/Pia.Wpf/Services/Interfaces/IAssistantFolderRelocationService.cs`):

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using Pia.Infrastructure.Vault; // FolderMoveProgress/Phase

namespace Pia.Services.Interfaces;

public enum RelocationOutcome { Success, NoChange, ValidationFailed, CopyFailed, VerifyFailed }

public record RelocationResult(RelocationOutcome Outcome, string? Error = null);

public interface IAssistantFolderRelocationService
{
    /// <summary>Validate, copy→verify→delete, then hot-swap the vault root + file-tool root to
    /// <paramref name="newFolder"/>. Reports Copying/Verifying/CleaningUp progress.</summary>
    Task<RelocationResult> MoveAsync(string newFolder,
        IProgress<FolderMoveProgress>? progress, CancellationToken ct);
}
```

- [ ] **Step 2: Write failing tests** (use temp dirs under `%USERPROFILE%` so Rule 1 passes; real `VaultPathProvider`, `VaultWatcher`, `VaultWriteGate`; a fake `ISettingsService` capturing saves; a fake `IVaultIndexer` recording `RebuildAllAsync`):

```csharp
[Fact]
public async Task Move_relocates_repoints_provider_and_saves_setting()
{
    // old folder under profile with a Vault subtree + a doc; new folder under profile.
    // After MoveAsync: outcome Success; old folder gone; provider.VaultRoot == <new>\Vault;
    // settings.AssistantFilesFolder == <new>; indexer.RebuildAllAsync called once.
}

[Fact]
public async Task Move_outside_profile_is_ValidationFailed_and_changes_nothing()
{
    // newFolder = a temp dir NOT under the profile -> ValidationFailed; provider + settings unchanged.
}

[Fact]
public async Task Move_to_same_folder_is_NoChange()
{
    // newFolder canonically equals current -> NoChange, no delete, no reindex.
}
```

- [ ] **Step 3: Run → FAIL.**
- [ ] **Step 4: Implement:**

```csharp
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Pia.Infrastructure;          // SafeFolderPath
using Pia.Infrastructure.Vault;    // VaultPathProvider, VaultWriteGate, SafeDirectoryMove, validator
using Pia.Logging;
using Pia.Services.Interfaces;

namespace Pia.Services;

public sealed class AssistantFolderRelocationService : IAssistantFolderRelocationService
{
    private readonly ISettingsService _settings;
    private readonly VaultPathProvider _paths;
    private readonly VaultWatcher _watcher;
    private readonly IVaultIndexer _indexer;
    private readonly IVaultWriteGate _gate;
    private readonly ILogger<AssistantFolderRelocationService> _logger;

    public AssistantFolderRelocationService(
        ISettingsService settings, VaultPathProvider paths, VaultWatcher watcher,
        IVaultIndexer indexer, IVaultWriteGate gate,
        ILogger<AssistantFolderRelocationService> logger)
    {
        _settings = settings; _paths = paths; _watcher = watcher;
        _indexer = indexer; _gate = gate; _logger = logger;
    }

    public async Task<RelocationResult> MoveAsync(
        string newFolder, IProgress<FolderMoveProgress>? progress, CancellationToken ct)
    {
        var settings = await _settings.GetSettingsAsync().ConfigureAwait(false);
        var oldFolder = settings.AssistantFilesFolder;

        var validation = AssistantFolderValidator.Validate(newFolder, oldFolder);
        if (validation != FolderValidation.Ok)
            return new RelocationResult(RelocationOutcome.ValidationFailed, validation.ToString());

        var newFull = Path.GetFullPath(newFolder);
        if (!string.IsNullOrWhiteSpace(oldFolder) &&
            string.Equals(Path.GetFullPath(oldFolder), newFull, StringComparison.OrdinalIgnoreCase))
            return new RelocationResult(RelocationOutcome.NoChange);

        // Hold the exclusive lease only for the file move + provider/watcher re-point. SaveSettingsAsync
        // (which fires SettingsChanged synchronously) is deliberately performed AFTER the lease is
        // released: no current subscriber writes to the vault, but doing so under the single-permit gate
        // would deadlock a future one. (Verified: SettingsChanged subscribers are file-tools, tool
        // permissions, plugins, and view models — none gate-write.)
        var lease = await _gate.EnterExclusiveAsync(ct).ConfigureAwait(false);
        try
        {
            _watcher.Stop(); // release the old-root directory handle before any delete

            DirectoryMoveResult move = new(DirectoryMoveOutcome.Success);
            if (!string.IsNullOrWhiteSpace(oldFolder) && Directory.Exists(oldFolder))
                move = await SafeDirectoryMove.MoveAsync(oldFolder!, newFull, progress, ct)
                    .ConfigureAwait(false);

            if (move.Outcome == DirectoryMoveOutcome.VerifyFailed)
            {
                _watcher.Restart(_paths.VaultRoot); // stay on old vault
                return new RelocationResult(RelocationOutcome.VerifyFailed, move.Error);
            }
            if (move.Outcome == DirectoryMoveOutcome.CopyFailed)
            {
                _watcher.Restart(_paths.VaultRoot);
                return new RelocationResult(RelocationOutcome.CopyFailed, move.Error);
            }

            // Re-point provider + watcher, rebuild index (copied files raise no Created events).
            var newVault = AssistantWorkspace.VaultRootFor(newFull);
            _paths.SetRoot(newVault);
            _watcher.Restart(newVault);
            try { await _indexer.RebuildAllAsync().ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "Reindex after relocation failed; will rebuild next start"); }
        }
        finally { lease.Dispose(); }

        // Persist OUTSIDE the lease -> SettingsChanged -> FilesToolHandler re-points to the new folder.
        settings.AssistantFilesFolder = newFull;
        await _settings.SaveSettingsAsync(settings).ConfigureAwait(false);

        _logger.LogInformation("Assistant folder relocated (vault re-pointed)");
        _logger.SensitiveDebug("Relocated assistant folder to {Folder}", newFull);
        return new RelocationResult(RelocationOutcome.Success);
    }
}
```

- [ ] **Step 5: Run → PASS.**
- [ ] **Step 6:** Commit: `feat(vault): AssistantFolderRelocationService (validate→move→hot-swap)`

### Chunk 4 gate
- [ ] Run the gate command. Expected: no new failures.

---

## Chunk 5: Startup wiring, in-place migration, DI

### Task 5.1: DI registrations

**Files:** Modify `src/Pia.Wpf/Bootstrapper.cs` (Infrastructure block ~206-213 and vault block ~286-303)

- [ ] **Step 1:** Register the gate as a shared singleton and pass it to `VaultStore`; register the relocation service:

```csharp
services.AddSingleton<Pia.Infrastructure.Vault.IVaultWriteGate, Pia.Infrastructure.Vault.VaultWriteGate>();
services.AddSingleton<IVaultStore>(sp => new VaultStore(
    sp.GetRequiredService<VaultPathProvider>(),
    sp.GetRequiredService<MarkdownVaultParser>(),
    sp.GetRequiredService<Pia.Infrastructure.Vault.IVaultWriteGate>()));
// ... in the vault services block:
services.AddSingleton<IAssistantFolderRelocationService, AssistantFolderRelocationService>();
```

- [ ] **Step 2:** `dotnet build` → success (DEBUG `ValidateOnBuild` confirms the graph resolves).
- [ ] **Step 3:** Commit: `chore(di): register write gate + relocation service`

### Task 5.2: Startup folder init + in-place migration

**Files:** Modify `src/Pia.Wpf/Bootstrapper.cs` — add a step immediately **before** the scaffolding `try` (line ~119, before `VaultSchemaService.EnsureScaffoldingAsync`).

- [ ] **Step 1:** Add a private static method and call it before scaffolding:

```csharp
// (call site, just before the scaffolding try-block ~line 119)
await InitializeAssistantFoldersAsync(_serviceProvider, bootstrapLogger);
```

```csharp
private static async Task InitializeAssistantFoldersAsync(IServiceProvider sp, ILogger logger)
{
    var settingsService = sp.GetRequiredService<ISettingsService>();
    var settings = await settingsService.GetSettingsAsync();
    var paths = sp.GetRequiredService<VaultPathProvider>();

    // Seed the default folder on first run (creating it + the Vault subfolder).
    var folder = settings.AssistantFilesFolder;
    if (string.IsNullOrWhiteSpace(folder))
    {
        folder = AssistantWorkspace.DefaultRoot;
        try
        {
            Directory.CreateDirectory(AssistantWorkspace.VaultRootFor(folder));
            settings.AssistantFilesFolder = folder;
            await settingsService.SaveSettingsAsync(settings);
        }
        catch (Exception ex) { logger.LogWarning(ex, "Failed to seed default assistant folder"); }
    }

    // Point the vault root at <folder>\Vault BEFORE scaffolding/migration/watcher run.
    paths.SetRoot(AssistantWorkspace.VaultRootFor(folder!));

    // One-shot in-place nesting: move legacy %LOCALAPPDATA%\Pia\Vault under the folder.
    if (settings.AssistantFolderLayoutVersion < 1)
    {
        var legacyVault = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Pia", "Vault");
        var derivedVault = AssistantWorkspace.VaultRootFor(folder!);
        try
        {
            if (Directory.Exists(legacyVault) &&
                !string.Equals(Path.GetFullPath(legacyVault), Path.GetFullPath(derivedVault),
                               StringComparison.OrdinalIgnoreCase) &&
                !Directory.Exists(derivedVault))
            {
                var result = await SafeDirectoryMove.MoveAsync(
                    legacyVault, derivedVault, progress: null, CancellationToken.None);
                logger.LogInformation("In-place vault nesting: {Outcome}", result.Outcome);
            }
            settings.AssistantFolderLayoutVersion = 1;
            await settingsService.SaveSettingsAsync(settings);
        }
        catch (Exception ex) { logger.LogWarning(ex, "In-place vault nesting failed; will retry next start"); }
    }
}
```

> This runs before `EnsureScaffoldingAsync`, the migration runner, and `VaultWatcher.Start()` — so they all bind to the nested vault. Guarded so a failure never blocks startup (matches the surrounding pattern). Idempotent: the version gate + the `!Directory.Exists(derivedVault)` guard make re-runs safe.

- [ ] **Step 2:** `dotnet build` → success. Add `using System.Threading;` / `System.IO;` if missing.
- [ ] **Step 3:** Commit: `feat(startup): derive vault root + one-shot in-place vault nesting`

### Task 5.3: Remove the old App.xaml.cs seeding

**Files:** Modify `src/Pia.Wpf/App.xaml.cs:114-131`

- [ ] **Step 1:** Delete the entire "Initialize assistant files folder default on first run" block (lines ~114-131) — seeding now lives in `InitializeAssistantFoldersAsync`, which runs earlier.
- [ ] **Step 2:** `dotnet build` → success (confirms no remaining reference to the removed block / `AssistantWorkspace.DefaultWorkdir`).
- [ ] **Step 3:** Commit: `refactor(startup): drop App-level folder seeding (moved to Bootstrapper)`

### Task 5.4: SensitivePathGuard carve-out → LegacyWorkdir

**Files:** Modify `src/Pia.Wpf/Infrastructure/SensitivePathGuard.cs:100-104`; check `tests/Pia.Wpf.Tests/Infrastructure/SensitivePathGuardTests.cs`

- [ ] **Step 1:** In `BuildAllowedExceptions`, replace `AssistantWorkspace.DefaultWorkdir` with `AssistantWorkspace.LegacyWorkdir` and update the doc comment to explain it's a back-compat carve-out for migrate-in-place users (the new Documents default is outside all blocked roots and needs none; the vault gets no special entry — full file-tool access by design).
- [ ] **Step 2:** Run `SensitivePathGuardTests`. If a test asserts the carve-out path, update it to `AssistantWorkspace.LegacyWorkdir` (same value, so behavior is unchanged). Expected: PASS.
- [ ] **Step 3:** Commit: `refactor(security): key workdir carve-out on LegacyWorkdir`

### Task 5.5: FilesToolHandler honors the enable toggle

**Files:** Modify `src/Pia.Wpf/Services/FilesToolHandler.cs` (`OnSettingsChanged` ~87-94, init ~68-72, `IsAvailable` ~85)

- [ ] **Step 1: Add failing test** in the appropriate `FilesToolHandler*Tests` (settings with a folder but `AssistantFileToolsEnabled = false` → `IsAvailable == false`; and read/list rejected). Mirror the existing test setup that injects `AppSettings`.
- [ ] **Step 2: Run → FAIL.**
- [ ] **Step 3: Implement.** Track the flag and fold it into availability:

```csharp
private volatile bool _toolsEnabled = true;

// in the ctor's settings load and in OnSettingsChanged:
_toolsEnabled = settings.AssistantFileToolsEnabled;
UpdateFolder(settings.AssistantFilesFolder);

public bool IsAvailable => _toolsEnabled && _currentFolder is not null;
```

> `UpdateFolder` already null-guards an empty folder; the new flag adds the explicit disable. Because `IsAvailable` gates tool registration + system-prompt exposure, unchecking the box removes the file tools while the vault keeps working through the memory tools.

- [ ] **Step 4: Run → PASS.**
- [ ] **Step 5:** Commit: `feat(files): gate file tools on AssistantFileToolsEnabled`

### Chunk 5 gate
- [ ] Run the gate command. Then `dotnet build` and launch once (`dotnet run --project src/Pia.Wpf/Pia.Wpf.csproj`) to confirm startup doesn't throw — verify a fresh-profile run creates `%USERPROFILE%\Documents\Pia Assistant\Vault` and an existing-profile run nests the legacy vault. (Per project guidance, do NOT drive the app via winwright; a manual launch/observe is fine.)

---

## Chunk 6: Settings UI

### Task 6.1: Progress dialog

**Files:** Create `src/Pia.Wpf/Views/Dialogs/FolderMoveContentDialog.xaml` (+ `.xaml.cs`); modify `IDialogService.cs` + `DialogService.cs`. **Pattern to copy:** `ModelDownloadContentDialog.xaml(.cs)` + `DialogService.ShowModelDownloadDialogAsync` (`DialogService.cs:104-124`).

- [ ] **Step 1:** Create `FolderMoveContentDialog.xaml` modeled on `ModelDownloadContentDialog.xaml`: a title, a determinate `ProgressBar` (`x:Name="MoveProgressBar"`, `Maximum="100"`), and a status `TextBlock` (`x:Name="PhaseText"`). No primary button while moving; no cancel (the move is short and atomic per-phase) — or a disabled close. Use `{loc:Str ...}` for text.
- [ ] **Step 2:** `FolderMoveContentDialog.xaml.cs` (mirror `ModelDownloadContentDialog.xaml.cs`):

```csharp
public partial class FolderMoveContentDialog : ContentDialog
{
    public FolderMoveContentDialog(ContentDialogHost host, IProgress<FolderMoveProgress> progress) : base(host)
    {
        InitializeComponent();
        if (progress is Progress<FolderMoveProgress> impl)
            impl.ProgressChanged += (_, e) => Dispatcher.Invoke(() => Apply(e));
    }

    private void Apply(FolderMoveProgress e)
    {
        MoveProgressBar.Value = e.PercentComplete;
        PhaseText.Text = e.Phase switch
        {
            FolderMovePhase.Copying    => LocalizationSource.Instance["Dialog_FolderMove_Copying"],
            FolderMovePhase.Verifying  => LocalizationSource.Instance["Dialog_FolderMove_Verifying"],
            FolderMovePhase.CleaningUp => LocalizationSource.Instance["Dialog_FolderMove_CleaningUp"],
            _ => string.Empty,
        };
    }
}
```

- [ ] **Step 3:** Add to `IDialogService`:

```csharp
Task ShowFolderMoveDialogAsync(IProgress<FolderMoveProgress> progress, Func<Task> work);
```

Implement in `DialogService` — open the dialog, run `work()` concurrently, close when done:

```csharp
public async Task ShowFolderMoveDialogAsync(IProgress<FolderMoveProgress> progress, Func<Task> work)
{
    var host = _contentDialogService.GetDialogHostEx()
        ?? throw new InvalidOperationException("No dialog host available");
    var dialog = new FolderMoveContentDialog(host, progress);
    var showTask = dialog.ShowAsync();
    try { await work(); }
    finally { dialog.Hide(); }
    await showTask;
}
```

- [ ] **Step 4:** `dotnet build` → success.
- [ ] **Step 5:** Commit: `feat(ui): folder-move progress dialog`

### Task 6.2: Strings (en/de/fr)

**Files:** `src/Pia.Wpf/Resources/Strings/ViewStrings.resx`, `.de.resx`, `.fr.resx`

- [ ] **Step 1:** Add keys to all three (translate de/fr): `Dialog_FolderMove_Title`, `Dialog_FolderMove_Copying`, `Dialog_FolderMove_Verifying`, `Dialog_FolderMove_CleaningUp`, `Settings_AssistantFilesFolder_Change` ("Change…"), `Settings_AssistantFileToolsEnabled` ("Allow the assistant to read and write files in this folder"), `Settings_AssistantVaultLocation` ("Memory vault: {0}"), a confirm string `Settings_FolderMove_Confirm` ("Move your assistant files and memory to {0}? This copies, verifies, then removes the old location."), and error strings `Settings_FolderMove_OutsideProfile`, `Settings_FolderMove_Blocked`, `Settings_FolderMove_Nested`, `Settings_FolderMove_NotEmpty` ("Choose an empty or new folder."), `Settings_FolderMove_Failed`. `MapValidationMessage` (Task 6.3) maps each `FolderValidation` value to its string. Update `Settings_AssistantFilesFolder_Description` to drop "Leave empty to disable" (now the checkbox).
- [ ] **Step 2:** Build → success (the `.Designer.cs` regenerates).
- [ ] **Step 3:** Commit: `i18n: folder-move + file-tools-toggle strings`

### Task 6.3: AssistantSettingsViewModel

**Files:** Modify `src/Pia.Wpf/ViewModels/AssistantSettingsViewModel.cs`; Test extend `tests/Pia.Wpf.Tests/ViewModels/...` if a VM test fixture exists (else cover the command logic).

- [ ] **Step 1:** Inject `IAssistantFolderRelocationService`. Add observable props: `FileToolsEnabled` (bool), `VaultLocationDisplay` (string, derived = `VaultRootFor(FilesFolder)`). Remove `ClearFilesFolderCommand`.
- [ ] **Step 2:** In `InitializeAsync`, load `FileToolsEnabled = settings.AssistantFileToolsEnabled` and set `VaultLocationDisplay`. In `SaveSettingsAsync`, persist `AssistantFileToolsEnabled = FileToolsEnabled` and stop forcing `AssistantFilesFolder` to null (it is always set now). Add `OnFileToolsEnabledChanged` → save.
- [ ] **Step 3:** Replace `BrowseFilesFolderCommand` with a `ChangeFilesFolderCommand`:

```csharp
[RelayCommand]
private async Task ChangeFilesFolderAsync()
{
    var dialog = new Microsoft.Win32.OpenFolderDialog
    {
        Title = _localizationService["Settings_AssistantFilesFolder"],
        InitialDirectory = Directory.Exists(FilesFolder)
            ? FilesFolder
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
    };
    if (dialog.ShowDialog() != true) return;

    var target = dialog.FolderName;
    var validation = AssistantFolderValidator.Validate(target, FilesFolder);
    if (validation != FolderValidation.Ok)
    {
        await _dialogService.ShowMessageDialogAsync(
            _localizationService["Msg_Error"], MapValidationMessage(validation));
        return;
    }

    var confirmed = await _dialogService.ShowConfirmationDialogAsync(
        _localizationService["Settings_AssistantFilesFolder_Change"],
        _localizationService.Format("Settings_FolderMove_Confirm", target)); // add this string too
    if (!confirmed) return;

    var progress = new Progress<FolderMoveProgress>();
    RelocationResult? result = null;
    await _dialogService.ShowFolderMoveDialogAsync(progress, async () =>
        result = await _relocationService.MoveAsync(target, progress, CancellationToken.None));

    if (result is { Outcome: RelocationOutcome.Success or RelocationOutcome.NoChange })
    {
        FilesFolder = target;
        VaultLocationDisplay = AssistantWorkspace.VaultRootFor(target);
    }
    else
    {
        await _dialogService.ShowMessageDialogAsync(
            _localizationService["Msg_Error"],
            result?.Error ?? _localizationService["Settings_FolderMove_Failed"]);
    }
}
```

> `FilesFolder` is now display-only (no free-text edit that bypasses validation/move). Bind the TextBox `IsReadOnly="True"` (Task 6.4). `OnFilesFolderChanged` should no longer auto-save the raw path (the move owns persistence) — guard it or remove the auto-save.

- [ ] **Step 4:** `dotnet build` → success. If a VM unit test fixture exists, add a test that a validation failure shows an error and performs no move (fake relocation service asserts `MoveAsync` not called).
- [ ] **Step 5:** Commit: `feat(ui): assistant folder Change… flow + file-tools toggle`

### Task 6.4: AssistantView.xaml

**Files:** Modify `src/Pia.Wpf/Views/SettingsViews/AssistantView.xaml:65-91`

- [ ] **Step 1:** Replace the Files-Folder `StackPanel` body: keep the read-only path `TextBox` (`IsReadOnly="True"`), replace Browse/Clear with a single **Change…** button bound to `ChangeFilesFolderCommand`; add a `CheckBox` bound to `FileToolsEnabled` (content `Settings_AssistantFileToolsEnabled`); add a muted `TextBlock` bound to `VaultLocationDisplay` (format via `Settings_AssistantVaultLocation`). Keep the existing description style.
- [ ] **Step 2:** `dotnet build` → success.
- [ ] **Step 3:** Launch the app, open Settings → Assistant; confirm: path shows the folder, Change… opens the picker + (on a real change) the progress dialog, the toggle persists, the vault sub-line shows `<folder>\Vault`.
- [ ] **Step 4:** Commit: `feat(ui): assistant settings folder panel (change + vault location + toggle)`

### Chunk 6 gate
- [ ] Run the gate command. Then a manual smoke test: change the folder to a new path under the profile, watch copy→verify→cleanup, confirm the Memory view still lists memories (vault re-pointed + reindexed) and the file tools target the new folder.

---

## Final verification
- [ ] Full gate: `dotnet test tests/Pia.Wpf.Tests/Pia.Wpf.Tests.csproj -- --filter-not-namespace "Pia.Wpf.Tests.Integration.Providers"` — no failures outside the baseline namespace.
  - **Heads-up:** the `Architecture/` tests (`NamingConventionTests`, `DependencyInjectionTests`) may flag the new `IVaultWriteGate`/`VaultWriteGate` ("Gate" suffix), the static `SafeDirectoryMove`/`AssistantFolderValidator`, or an "every interface is registered" rule. If red, adjust naming or the test's allowlist — it's a convention assertion, not a design fault.
- [ ] `dotnet build -c Release` succeeds.
- [ ] Manual: (a) fresh profile → default folder + vault created; (b) existing profile → legacy vault nested once; (c) relocate → progress dialog runs copy→verify→cleanup, memories survive; (d) **after relocating, create a NEW memory and confirm the `.md` lands under the NEW `<folder>\Vault\memory`** (proves the write path re-pointed, not just reads); (e) **confirm a sync round-trip still reconciles** (the `id`-keyed `SyncBaseStore` stayed in `%LOCALAPPDATA%\Pia` while the vault moved); (f) file tools target the new folder; (g) uncheck toggle → file tools disappear, memory still works; (h) pick a folder outside `%USERPROFILE%` or a non-empty folder → rejected, no move.
- [ ] Use superpowers:requesting-code-review before merge.
