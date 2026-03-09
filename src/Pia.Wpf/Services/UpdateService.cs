using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pia.Models;
using Pia.Services.Interfaces;
using Velopack;
using Velopack.Sources;

namespace Pia.Services;

public class UpdateService : IUpdateService
{
    private readonly ILogger<UpdateService> _logger;
    private readonly UpdateManager _updateManager;
    private VelopackAsset? _targetAsset;

    public bool IsUpdateReady { get; private set; }
    public string? AvailableVersion { get; private set; }

    public UpdateService(ILogger<UpdateService> logger, IOptions<AutoUpdateOptions> options)
    {
        _logger = logger;

        var opts = options.Value;
        _updateManager = new UpdateManager(
            new GithubSource(opts.GitHubRepoUrl, opts.AccessToken, opts.Prerelease));
    }

    public async Task<bool> CheckAndDownloadUpdateAsync()
    {
        try
        {
            if (!_updateManager.IsInstalled)
            {
                _logger.LogInformation("App is not installed via Velopack; skipping update check");
                return false;
            }

            var updateInfo = await _updateManager.CheckForUpdatesAsync();

            if (updateInfo == null)
            {
                _logger.LogInformation("No updates available");
                return false;
            }

            _targetAsset = updateInfo.TargetFullRelease;
            AvailableVersion = _targetAsset.Version.ToString();
            _logger.LogInformation("Update available: {Version}. Downloading...", AvailableVersion);

            await _updateManager.DownloadUpdatesAsync(updateInfo);
            IsUpdateReady = true;
            _logger.LogInformation("Update downloaded and ready: {Version}", AvailableVersion);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check/download updates");
            return false;
        }
    }

    public void ApplyUpdateAndRestart()
    {
        if (_targetAsset == null || !IsUpdateReady)
            throw new InvalidOperationException("No update downloaded. Call CheckAndDownloadUpdateAsync first.");

        _logger.LogInformation("Applying update and restarting...");
        _updateManager.ApplyUpdatesAndRestart(_targetAsset);
    }
}
