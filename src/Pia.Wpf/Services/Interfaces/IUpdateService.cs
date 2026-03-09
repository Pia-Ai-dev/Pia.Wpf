namespace Pia.Services.Interfaces;

public interface IUpdateService
{
    bool IsUpdateReady { get; }
    string? AvailableVersion { get; }

    /// <summary>
    /// Silently checks for updates, downloads if available.
    /// Returns true if an update was downloaded and is ready to install.
    /// </summary>
    Task<bool> CheckAndDownloadUpdateAsync();

    /// <summary>
    /// Applies the downloaded update and restarts the application.
    /// </summary>
    void ApplyUpdateAndRestart();
}
