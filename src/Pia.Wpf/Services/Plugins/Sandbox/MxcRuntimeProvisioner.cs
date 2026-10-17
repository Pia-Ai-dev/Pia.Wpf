using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Pia.Logging;
using Pia.Paths;
using Pia.Services.Assets;
using Pia.Services.Interfaces;
using Pia.Services.Updates;

namespace Pia.Services.Plugins.Sandbox;

/// <summary>Whether this process may call into the MXC native runtime.</summary>
public interface IMxcRuntime
{
    /// <summary>True when the native runtime is installed, passes its signature check in this process and is what the
    /// SDK will load; otherwise <paramref name="reason"/> says why not.</summary>
    bool TryLoad(out string? reason);
}

public sealed record MxcInstallResult(bool Installed, string? Reason);

/// <summary>Downloads the MXC native runtime from Pia's own storage, proves it is the pinned Microsoft-signed file,
/// and points the SDK at it. Nothing here is shipped in the installer.</summary>
public sealed class MxcRuntimeProvisioner : IMxcRuntime
{
    private const string NativeEntry = "runtimes/win-x64/native/mxc_ffi.dll";
    private const string NativeFile = "mxc_ffi.dll";
    private const string TrustedPublisher = "Microsoft Corporation";

    private readonly IAssetDownloader _downloader;
    private readonly ILogger<MxcRuntimeProvisioner> _logger;
    private readonly string _directory;
    private readonly string _archivePath;
    private readonly RuntimeAsset _asset;
    private readonly string _expectedSha256;
    private readonly Func<string, string?> _publisherOf;
    private readonly SemaphoreSlim _install = new(1, 1);
    private readonly object _loadLock = new();
    private bool _loaded;

    public MxcRuntimeProvisioner(IAssetDownloader downloader, ILogger<MxcRuntimeProvisioner> logger)
        : this(downloader, logger, PiaPaths.MxcRuntimeDirectory, PiaPaths.MxcRuntimeArchivePath,
            RuntimeAssetCatalog.MxcRuntime, RuntimeAssetCatalog.MxcRuntimeSha256, UpdatePackageVerifier.PublisherOf)
    {
    }

    internal MxcRuntimeProvisioner(
        IAssetDownloader downloader,
        ILogger<MxcRuntimeProvisioner> logger,
        string directory,
        string archivePath,
        RuntimeAsset asset,
        string expectedSha256,
        Func<string, string?> publisherOf)
    {
        _downloader = downloader;
        _logger = logger;
        _directory = directory;
        _archivePath = archivePath;
        _asset = asset;
        _expectedSha256 = expectedSha256;
        _publisherOf = publisherOf;

        // The SDK probes this variable first, so a value inherited from the user or machine would load unverified code.
        SetRuntimeDirectory(null);
    }

    public bool IsInstalled => File.Exists(NativePath);

    private string NativePath => Path.Combine(_directory, NativeFile);

    public bool TryLoad(out string? reason)
    {
        lock (_loadLock)
        {
            if (_loaded)
            {
                reason = null;
                return true;
            }

            SetRuntimeDirectory(null);
            if (!IsInstalled)
            {
                reason = "The sandbox runtime is not installed.";
                return false;
            }

            if (!IsSignedByMicrosoft(NativePath))
            {
                _logger.LogWarning("The installed MXC runtime failed its signature check and was not loaded");
                reason = "The installed sandbox runtime failed its signature check.";
                return false;
            }

            SetRuntimeDirectory(_directory);
            _loaded = true;
            reason = null;
            return true;
        }
    }

    public async Task<MxcInstallResult> InstallAsync(IProgress<ModelDownloadProgress>? progress = null, CancellationToken ct = default)
    {
        await _install.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsInstalled && IsSignedByMicrosoft(NativePath))
                return new MxcInstallResult(true, null);

            try
            {
                return await InstallCoreAsync(progress, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Installing the MXC runtime failed");
                return new MxcInstallResult(false, $"The sandbox runtime could not be downloaded: {ex.Message}");
            }
            finally
            {
                DiscardTemporaryFiles();
                progress?.Report(new ModelDownloadProgress(100, 0, ModelDownloadPhase.Completed));
            }
        }
        finally
        {
            _install.Release();
        }
    }

    private async Task<MxcInstallResult> InstallCoreAsync(IProgress<ModelDownloadProgress>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_directory)!);
        DiscardTemporaryFiles();

        var package = await AcquirePackageAsync(progress, ct).ConfigureAwait(false);
        if (package is null)
            return new MxcInstallResult(false, "The downloaded sandbox runtime did not match the expected checksum, so it was discarded.");

        progress?.Report(new ModelDownloadProgress(0, 0, ModelDownloadPhase.Extracting));
        var staging = _directory + ".tmp";
        Directory.CreateDirectory(staging);
        var stagedFile = Path.Combine(staging, NativeFile);

        if (!await ExtractAsync(package, stagedFile, ct).ConfigureAwait(false))
            return new MxcInstallResult(false, "The downloaded package does not contain the sandbox runtime.");

        if (!IsSignedByMicrosoft(stagedFile))
        {
            _logger.LogWarning("The downloaded MXC runtime is not signed by Microsoft and was discarded");
            return new MxcInstallResult(false, "The downloaded sandbox runtime is not signed by Microsoft, so it was discarded.");
        }

        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
        Directory.Move(staging, _directory);

        _logger.LogInformation("MXC runtime {Version} installed", PiaPaths.MxcRuntimeVersion);
        return new MxcInstallResult(true, null);
    }

    /// <summary>The path of a package that matches the pin, or null after discarding one that does not.</summary>
    private async Task<string?> AcquirePackageAsync(IProgress<ModelDownloadProgress>? progress, CancellationToken ct)
    {
        if (File.Exists(_archivePath))
        {
            if (await MatchesPinAsync(_archivePath, ct).ConfigureAwait(false))
            {
                _logger.LogInformation("Using the pre-fetched MXC runtime package");
                return _archivePath;
            }

            File.Delete(_archivePath);
        }

        var download = _archivePath + ".tmp";
        await _downloader.DownloadAsync(_asset, download, progress, ct).ConfigureAwait(false);
        if (await MatchesPinAsync(download, ct).ConfigureAwait(false))
            return download;

        File.Delete(download);
        return null;
    }

    private async Task<bool> MatchesPinAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
        return string.Equals(hash, _expectedSha256, StringComparison.OrdinalIgnoreCase);
    }

    // The entry name is fixed, so nothing in the archive decides where the file lands.
    private static async Task<bool> ExtractAsync(string package, string destination, CancellationToken ct)
    {
        await using var stream = File.OpenRead(package);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        var entry = zip.GetEntry(NativeEntry);
        if (entry is null)
            return false;

        await using var source = entry.Open();
        await using var target = File.Create(destination);
        await source.CopyToAsync(target, ct).ConfigureAwait(false);
        return true;
    }

    private bool IsSignedByMicrosoft(string path) =>
        string.Equals(_publisherOf(path), TrustedPublisher, StringComparison.OrdinalIgnoreCase);

    private void DiscardTemporaryFiles()
    {
        TryDelete(() => Directory.Delete(_directory + ".tmp", recursive: true), Directory.Exists(_directory + ".tmp"));
        TryDelete(() => File.Delete(_archivePath + ".tmp"), File.Exists(_archivePath + ".tmp"));
    }

    private void TryDelete(Action delete, bool exists)
    {
        if (!exists)
            return;

        try
        {
            delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.SensitiveDebug("Could not remove a temporary MXC runtime file: {Error}", ex.Message);
        }
    }

    private static void SetRuntimeDirectory(string? directory) =>
        Environment.SetEnvironmentVariable(SandboxLaunchSpecBuilder.RuntimeDirectoryVariable, directory);
}
