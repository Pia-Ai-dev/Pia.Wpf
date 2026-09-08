using Pia.Services.Screen;

namespace Pia.Services.Interfaces;

/// <summary>The windows a routine or a background run may capture. Monitors are never listable — an unattended
/// capture is one named window or nothing.</summary>
public interface IScreenCaptureAllowlistStore
{
    Task<IReadOnlyList<ScreenCaptureAllowlistEntry>> ListAsync();

    /// <summary>Null when the program name is blank after normalisation, or when the pair is already listed.</summary>
    Task<ScreenCaptureAllowlistEntry?> AddAsync(string? processName, string? titleContains);

    Task<bool> RemoveAsync(Guid id);

    /// <summary>Raised after an add or remove that changed the list; may fire off the UI thread.</summary>
    event EventHandler? Changed;
}
