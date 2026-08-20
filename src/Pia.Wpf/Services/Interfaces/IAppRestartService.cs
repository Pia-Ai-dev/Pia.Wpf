namespace Pia.Services.Interfaces;

/// <summary>
/// Restarts Pia in place: stops background sync, tears the UI down, then exits so
/// <c>App.Main</c> can spawn the replacement.
/// </summary>
public interface IAppRestartService
{
    /// <summary>Latched — a second call does nothing, so a double-click cannot spawn two children.</summary>
    Task RestartAsync();
}
