using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Pia.Infrastructure;          // AssistantWorkspace
using Pia.Infrastructure.Vault;    // VaultPathProvider, IVaultWriteGate, SafeDirectoryMove, validator
using Pia.Logging;
using Pia.Services.Interfaces;

namespace Pia.Services;

/// <summary>
/// Relocates the assistant files folder (and its nested vault) under <c>%USERPROFILE%</c>, copying
/// then verifying then deleting the old tree, and hot-swapping the vault root, watcher, index, and
/// file-tool root in-process. See docs/superpowers/specs/2026-06-27-relocatable-assistant-folder-design.md.
/// </summary>
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
        _settings = settings;
        _paths = paths;
        _watcher = watcher;
        _indexer = indexer;
        _gate = gate;
        _logger = logger;
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
        // would deadlock a future one.
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
