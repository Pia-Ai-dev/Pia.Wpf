using Pia.Services.Screen;

namespace Pia.Services.Interfaces;

/// <summary>Reads the text a window exposes to accessibility, as an alternative to a picture of it.</summary>
public interface IScreenTextSnapshotService
{
    /// <summary>Never throws. Runs off the caller's thread under a watchdog — a UIA call cannot be cancelled.</summary>
    Task<ScreenTextSnapshot> SnapshotAsync(nint hwnd, CancellationToken cancellationToken = default);
}
