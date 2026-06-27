using System;
using System.Threading;
using System.Threading.Tasks;
using Pia.Infrastructure.Vault; // FolderMoveProgress

namespace Pia.Services.Interfaces;

public enum RelocationOutcome { Success, NoChange, ValidationFailed, CopyFailed, VerifyFailed }

public record RelocationResult(RelocationOutcome Outcome, string? Error = null);

public interface IAssistantFolderRelocationService
{
    /// <summary>
    /// Validate, copy→verify→delete, then hot-swap the vault root + file-tool root to
    /// <paramref name="newFolder"/>. Reports Copying/Verifying/CleaningUp progress.
    /// </summary>
    Task<RelocationResult> MoveAsync(string newFolder,
        IProgress<FolderMoveProgress>? progress, CancellationToken ct);
}
