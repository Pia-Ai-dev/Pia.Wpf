namespace Pia.Models;

/// <summary>
/// Progress contract for the assistant-folder relocation, in <c>Pia.Models</c> so it is shared by the
/// Infrastructure mover (<c>SafeDirectoryMove</c>), the Services relocation/dialog interfaces, and the
/// ViewModels — none of which may depend on each other across the layer boundary.
/// </summary>
public enum FolderMovePhase { Copying, Verifying, CleaningUp }

public record FolderMoveProgress(FolderMovePhase Phase, int PercentComplete, string? CurrentItem = null);
