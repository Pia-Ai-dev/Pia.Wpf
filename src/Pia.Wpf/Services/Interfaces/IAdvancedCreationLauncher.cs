using Pia.Models;

namespace Pia.Services.Interfaces;

public interface IAdvancedCreationLauncher
{
    /// <summary>
    /// Runs the interview in the app-wide overlay and returns the draft object the caller should apply
    /// through its own path, or null if the user closed without finishing.
    /// </summary>
    /// <param name="seed">What the editor's description box already holds, so "Advanced…" continues from
    /// what was typed instead of asking for it twice.</param>
    Task<string?> LaunchAsync(AdvancedCreationMode mode, Guid? providerId = null, string? seed = null);
}
