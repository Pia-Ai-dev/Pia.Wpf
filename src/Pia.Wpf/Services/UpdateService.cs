using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pia.Logging;
using Pia.Models;
using Pia.Services.Interfaces;
using Pia.Services.Updates;
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
    public string? CurrentVersion { get; }

    public UpdateService(ILogger<UpdateService> logger, IOptions<AutoUpdateOptions> options)
    {
        _logger = logger;

        _updateManager = new VerifyingUpdateManager(CreateSource(options.Value, logger), options.Value, logger);

        CurrentVersion = _updateManager.IsInstalled
            ? _updateManager.CurrentVersion?.ToString()
            : null;
    }

    /// <summary>A blank <see cref="AutoUpdateOptions.FeedUrl"/> keeps GitHub, so a deployment moves feeds by setting one key.</summary>
    public static IUpdateSource CreateSource(AutoUpdateOptions options, ILogger? logger = null)
    {
        if (!string.IsNullOrWhiteSpace(options.FeedUrl))
        {
            logger?.LogInformation("Checking for updates at {Url}", SafeUrl.Format(options.FeedUrl));
            return new SimpleWebSource(options.FeedUrl);
        }

        logger?.LogInformation("Checking for updates at {Url}", SafeUrl.Format(options.GitHubRepoUrl));
        return new GithubSource(options.GitHubRepoUrl, options.AccessToken, options.Prerelease);
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

    /// <summary>Velopack only compares the feed's own checksum; this adds Authenticode before the package is moved into place.</summary>
    private sealed class VerifyingUpdateManager(IUpdateSource source, AutoUpdateOptions options, ILogger logger)
        : UpdateManager(source)
    {
        private readonly Lazy<bool> _enforce = new(() => RunningBuildIsSigned(logger));

        // Runs before the move, and the move is what lets Velopack install the package's Update.exe.
        protected override async Task VerifyPackageChecksumAsync(VelopackAsset release, string? filePathOverride = null)
        {
            await base.VerifyPackageChecksumAsync(release, filePathOverride).ConfigureAwait(false);
            var packagePath = filePathOverride ?? Path.Combine(Locator.PackagesDir ?? string.Empty, release.FileName);

            await AuditCatalogAsync(release, packagePath).ConfigureAwait(false);
            if (release.Type == VelopackAssetType.Full)
                EnforceSignatures(packagePath);
        }

        // Velopack checksums each delta but never the package it patches together from them.
        protected override async Task DownloadAndApplyDeltaUpdates(
            UpdateInfo updates, string targetFile, Action<int> progress, CancellationToken cancelToken)
        {
            await base.DownloadAndApplyDeltaUpdates(updates, targetFile, progress, cancelToken).ConfigureAwait(false);
            EnforceSignatures(targetFile);
        }

        private void EnforceSignatures(string packagePath)
        {
            if (!_enforce.Value)
                return;

            UpdatePackageVerifier.VerifyBinaries(packagePath, options.EffectiveTrustedPublishers);
            logger.LogInformation("Update package binaries are signed by trusted publishers");
        }

        // Audit only: the catalog is new to the release pipeline, so a miss is logged rather than blocking updates.
        private async Task AuditCatalogAsync(VelopackAsset release, string packagePath)
        {
            var catalogPath = Path.Combine(Path.GetTempPath(), $"pia-release-{Guid.NewGuid():N}.cat");
            try
            {
                try
                {
                    var catalog = release with { FileName = $"{release.PackageId}-{release.Version}.cat" };
                    await Source.DownloadReleaseEntry(Log, catalog, catalogPath, _ => { }, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Release catalog download failed");
                }

                var verdict = UpdatePackageVerifier.CheckCatalog(catalogPath, packagePath, options.EffectiveTrustedPublishers);
                if (verdict == UpdatePackageVerifier.CatalogVerdict.Verified)
                    logger.LogInformation("Release catalog vouches for {Package}", release.FileName);
                else
                    logger.LogWarning("Release catalog check for {Package}: {Verdict} (not enforced yet)", release.FileName, verdict);
            }
            finally
            {
                try { File.Delete(catalogPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        private static bool RunningBuildIsSigned(ILogger logger)
        {
            if (Environment.ProcessPath is { } exe && UpdatePackageVerifier.PublisherOf(exe) is not null)
                return true;

            // A local unsigned build has no signature of its own to hold an update to.
            logger.LogWarning("This build is not Authenticode-signed, so update signatures are not enforced");
            return false;
        }
    }
}
