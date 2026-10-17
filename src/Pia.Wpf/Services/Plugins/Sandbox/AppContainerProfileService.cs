using Microsoft.Extensions.Logging;
using Pia.Native;

namespace Pia.Services.Plugins.Sandbox;

/// <summary>Every container name leaves an AppContainer profile behind and MXC never deletes it, so a removed
/// server has to take its profiles with it.</summary>
public interface IAppContainerProfileService
{
    /// <summary>Best effort; false when a profile that exists could not be deleted.</summary>
    bool DeleteServerProfiles(string toolPrefix);
}

public sealed class AppContainerProfileService : IAppContainerProfileService
{
    private const int Ok = 0;
    private const int NotFound = unchecked((int)0x80070490);

    private readonly ILogger<AppContainerProfileService> _logger;
    private readonly Func<string, int> _deleteProfile;

    public AppContainerProfileService(ILogger<AppContainerProfileService> logger)
        : this(logger, AppContainerNative.DeleteProfile)
    {
    }

    internal AppContainerProfileService(ILogger<AppContainerProfileService> logger, Func<string, int> deleteProfile)
    {
        _logger = logger;
        _deleteProfile = deleteProfile;
    }

    public bool DeleteServerProfiles(string toolPrefix)
    {
        var allGone = true;
        foreach (var name in new[]
        {
            SandboxLaunchSpecBuilder.ContainerName(toolPrefix, isProbe: false),
            SandboxLaunchSpecBuilder.ContainerName(toolPrefix, isProbe: true)
        })
        {
            allGone &= Delete(name);
        }

        return allGone;
    }

    private bool Delete(string name)
    {
        try
        {
            var hresult = _deleteProfile(name);
            if (hresult is Ok or NotFound)
            {
                _logger.LogInformation("AppContainer profile {Container}: {Result}", name, hresult == Ok ? "deleted" : "already gone");
                return true;
            }

            _logger.LogWarning("AppContainer profile {Container} could not be deleted: HRESULT 0x{HResult:X8}", name, hresult);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AppContainer profile {Container} could not be deleted", name);
            return false;
        }
    }
}
