using System;
using System.Threading;
using System.Threading.Tasks;
using Pia.Models; // FolderMoveProgress

namespace Pia.Services.Interfaces;

/// <summary>
/// Result of a relocation or a pre-flight validation. The validation-specific values mirror the
/// Infrastructure folder validator but live here so ViewModels map outcomes to messages without
/// depending on Infrastructure.
/// </summary>
public enum RelocationOutcome
{
    Success,
    NoChange,
    OutsideUserProfile,
    BlockedPath,
    NestedInCurrent,
    NotEmpty,
    Invalid,
    CopyFailed,
    VerifyFailed,
}

public record RelocationResult(RelocationOutcome Outcome, string? Error = null);

public interface IAssistantFolderRelocationService
{
    /// <summary>The derived memory-vault path for a given files folder (<c>&lt;folder&gt;\Vault</c>).</summary>
    string GetVaultPath(string filesFolder);

    /// <summary>Pre-flight validation of a candidate folder (Rule 1 + nesting + non-empty). Returns
    /// <see cref="RelocationOutcome.Success"/> when the folder is acceptable.</summary>
    RelocationOutcome Validate(string newFolder);

    /// <summary>
    /// Validate, copy→verify→delete, then hot-swap the vault root + file-tool root to
    /// <paramref name="newFolder"/>. Reports Copying/Verifying/CleaningUp progress.
    /// </summary>
    Task<RelocationResult> MoveAsync(string newFolder,
        IProgress<FolderMoveProgress>? progress, CancellationToken ct);
}
