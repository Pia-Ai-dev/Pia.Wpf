using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Pia.Paths;

namespace Pia.Services.MeetingAttendee;

/// <summary>
/// Provisions the Chromium browser used by the meeting attendee. A build bundled beside the app wins;
/// otherwise Chromium is downloaded into <c>%LOCALAPPDATA%\Pia\Browsers</c>, refreshed whenever the
/// pinned Playwright version changes, and deleted on uninstall.
///
/// Provisioning is delegated to Playwright's own installer
/// (<see cref="Microsoft.Playwright.Program.Main(string[])"/> with <c>["install","chromium","--no-shell"]</c>),
/// which honours the <c>PLAYWRIGHT_BROWSERS_PATH</c> environment variable for the cache location
/// and an optional <c>PLAYWRIGHT_DOWNLOAD_HOST</c> override for the CDN. Running it is also what
/// prunes older revisions: the installer garbage-collects every browser directory the linked driver
/// no longer references, so skipping it leaves the client on a stale browser forever.
/// </summary>
public sealed class ChromiumProvisioner : IBrowserProvisioner
{
    /// <summary>Records the Playwright version the cache was installed for; a mismatch triggers a refresh.</summary>
    internal const string VersionMarkerFileName = ".playwright-version";

    /// <summary>Set once an install for this version failed, so a blocked CDN is not retried per join.</summary>
    internal static string? FailedInstallVersion { get; set; }

    private readonly ILogger<ChromiumProvisioner> _logger;

    /// <summary>Cache root for downloaded browsers (the <c>PLAYWRIGHT_BROWSERS_PATH</c> value).</summary>
    public static string BrowsersDirectory => PiaPaths.BrowsersDirectory;

    /// <summary>
    /// Chromium staged into the release payload beside the app. Empty unless the packaging step ran,
    /// in which case it supersedes the download: it is replaced by an update and removed by the
    /// uninstaller along with the rest of the app directory.
    /// </summary>
    public static string BundledBrowsersDirectory => Path.Combine(AppContext.BaseDirectory, "Browsers");

    /// <summary>
    /// Optional override for the Playwright download host (CDN), wired to the
    /// <c>PLAYWRIGHT_DOWNLOAD_HOST</c> environment variable. Null means Playwright's own
    /// version-matched default, so no literal URL rots across version bumps.
    /// </summary>
    public static string? DownloadHostOverride { get; set; }

    /// <summary>Seam for the Playwright CLI, so the install decision is testable without a network.</summary>
    internal static Func<string[], int> InstallerInvoker { get; set; } = Microsoft.Playwright.Program.Main;

    /// <summary>Seam for the bundled-build probe, so a test never spawns a process.</summary>
    internal static Func<string, bool> ExecutableProbe { get; set; } = CanStart;

    /// <summary>Bundled executable already proven to start, so the probe runs once per process.</summary>
    internal static string? VerifiedBundledExecutable { get; set; }

    /// <summary>The pinned <c>Microsoft.Playwright</c> version — the browser revision is tied to it.</summary>
    internal static string PinnedPlaywrightVersion =>
        typeof(Microsoft.Playwright.Program).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(Microsoft.Playwright.Program).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    public ChromiumProvisioner(ILogger<ChromiumProvisioner> logger)
    {
        _logger = logger;
    }

    public Task<string> EnsureChromiumAsync(
        IProgress<ChromiumDownloadProgress>? progress,
        CancellationToken cancellationToken = default) =>
        EnsureChromiumAsync(BundledBrowsersDirectory, BrowsersDirectory, progress, cancellationToken);

    /// <summary>Roots as parameters so a test can drive the whole decision without the real profile.</summary>
    internal async Task<string> EnsureChromiumAsync(
        string bundledRoot,
        string cacheRoot,
        IProgress<ChromiumDownloadProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // A bundled build must never be handed to the installer: its .links entry points at the machine
        // that staged it, and a registry whose links are all broken makes Playwright's GC delete every
        // browser directory it finds — the payload included.
        var bundled = ResolveChromiumExecutable(bundledRoot);
        if (bundled is not null && IsBundledUsable(bundled))
        {
            Environment.SetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH", bundledRoot);

            // Otherwise a client that updates into a bundled build keeps its download forever, unread
            // and no longer refreshed by anything.
            if (Directory.Exists(cacheRoot))
            {
                _logger.LogInformation("Using the bundled Chromium; removing the superseded browser download");
                await Task.Run(() => DeleteBrowserCache(cacheRoot), cancellationToken).ConfigureAwait(false);
            }

            progress?.Report(new ChromiumDownloadProgress(ChromiumProvisioningPhase.AlreadyPresent));
            return bundled;
        }

        Directory.CreateDirectory(cacheRoot);
        Environment.SetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH", cacheRoot);

        // Cleared as well as set: the override is live-applied from policy, and a stale host would
        // otherwise survive in the environment for the rest of the session.
        Environment.SetEnvironmentVariable(
            "PLAYWRIGHT_DOWNLOAD_HOST",
            string.IsNullOrWhiteSpace(DownloadHostOverride) ? null : DownloadHostOverride);

        var version = PinnedPlaywrightVersion;
        var cached = ResolveChromiumExecutable(cacheRoot);

        // Keyed on the Playwright version rather than "any chrome.exe will do": the folder name alone
        // cannot say whether the cached build still matches the pinned driver. The failure latch keeps
        // a blocked CDN from putting a node spawn and a connect timeout in front of every later join.
        if (cached is not null && (ReadVersionMarker(cacheRoot) == version || FailedInstallVersion == version))
        {
            progress?.Report(new ChromiumDownloadProgress(ChromiumProvisioningPhase.AlreadyPresent));
            return cached;
        }

        if (cached is null)
        {
            _logger.LogInformation("Provisioning Chromium browser into cache (this can take a while on first run)");
        }
        else
        {
            _logger.LogInformation("Cached Chromium predates Playwright {Version}; refreshing", version);
        }

        // Playwright's installer is opaque (it shells out and writes its own progress to stdout),
        // so we cannot drive a byte-level percentage. Report a coarse "Downloading" phase, mirroring
        // the indeterminate "Extracting" phase used by the model downloader.
        progress?.Report(new ChromiumDownloadProgress(ChromiumProvisioningPhase.Downloading));

        int exitCode;
        try
        {
            // --no-shell: the attendee needs the full headed build for a real audio render session, so
            // the headless shell would be 260 MB of binary nothing ever launches.
            exitCode = await Task.Run(
                () => InstallerInvoker(["install", "chromium", "--no-shell"]),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Chromium install threw");
            exitCode = -1;
        }

        if (exitCode == 0 && ResolveChromiumExecutable(cacheRoot) is { } installed)
        {
            WriteVersionMarker(cacheRoot, version);
            progress?.Report(new ChromiumDownloadProgress(ChromiumProvisioningPhase.Completed));
            _logger.LogInformation("Chromium browser provisioning complete");
            return installed;
        }

        FailedInstallVersion = version;
        var reason = exitCode == 0
            ? "Chromium install reported success but no Chromium executable was found in the cache."
            : $"Playwright Chromium install failed with exit code {exitCode}.";

        // Re-resolve rather than trust the pre-install probe: the installer prunes the old revision
        // before it downloads, so a half-finished run can leave that path deleted.
        var fallback = ResolveChromiumExecutable(cacheRoot);
        if (fallback is not null)
        {
            // Offline or CDN-blocked: an outdated browser still joins the meeting, a throw does not.
            _logger.LogWarning("{Reason} Continuing with the cached browser", reason);
            progress?.Report(new ChromiumDownloadProgress(ChromiumProvisioningPhase.AlreadyPresent));
            return fallback;
        }

        throw new InvalidOperationException(reason);
    }

    /// <summary>
    /// A payload can be present and still unusable — EDR quarantine, an interrupted copy, ACLs on a
    /// per-machine install. Probing it first keeps the branch from being a one-way door: it deletes
    /// the download cache, so an unusable bundle has to fall through to the download instead.
    /// </summary>
    private bool IsBundledUsable(string bundledExe)
    {
        if (VerifiedBundledExecutable == bundledExe)
        {
            return true;
        }

        if (!ExecutableProbe(bundledExe))
        {
            _logger.LogWarning("Bundled Chromium will not start; falling back to the downloaded browser");
            return false;
        }

        VerifiedBundledExecutable = bundledExe;
        return true;
    }

    /// <summary>Starts the browser with <c>--version</c>: proof it is present, allowed and runnable.</summary>
    private static bool CanStart(string exePath)
    {
        try
        {
            using var probe = Process.Start(new ProcessStartInfo(exePath, "--version")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });

            if (probe is null)
            {
                return false;
            }

            // Starting is the whole answer; a build that lingers instead of printing is still a build.
            if (!probe.WaitForExit(TimeSpan.FromSeconds(10)))
            {
                try { probe.Kill(entireProcessTree: true); } catch (Exception) { }
            }

            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Removes the downloaded browser cache. The uninstaller tears down the app directory and knows
    /// nothing about <c>%LOCALAPPDATA%\Pia\Browsers</c>, which would otherwise keep an unpatched
    /// Chromium on the machine forever. Best-effort: a locked file leaves a partial cache the next
    /// provisioning run repairs, while a throw would fail the uninstall.
    /// </summary>
    public static void DeleteDownloadCache() => DeleteBrowserCache(BrowsersDirectory);

    internal static void DeleteBrowserCache(string browsersRoot)
    {
        try
        {
            if (Directory.Exists(browsersRoot))
            {
                Directory.Delete(browsersRoot, recursive: true);
            }
        }
        catch (Exception)
        {
            // Nothing to fall back to, and no logger on an uninstall callback.
        }
    }

    /// <summary>Playwright version the cache at <paramref name="browsersRoot"/> was installed for.</summary>
    internal static string? ReadVersionMarker(string browsersRoot)
    {
        try
        {
            var path = Path.Combine(browsersRoot, VersionMarkerFileName);
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Stamps the cache. An unwritable marker only costs one redundant install next time.</summary>
    internal static void WriteVersionMarker(string browsersRoot, string version)
    {
        try
        {
            File.WriteAllText(Path.Combine(browsersRoot, VersionMarkerFileName), version);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Pure resolver: scans <paramref name="browsersRoot"/> for a cached full Chromium build and
    /// returns the path to its <c>chrome.exe</c>, or <c>null</c> if none is present. Any revision
    /// matches — whether that build is current is decided by the version marker, not the folder name.
    ///
    /// Playwright lays browsers out as <c>&lt;root&gt;\chromium-&lt;revision&gt;\&lt;platform&gt;\chrome.exe</c>,
    /// where the revision varies by Playwright version and the platform folder is <c>chrome-win64</c>
    /// on current builds (older builds used <c>chrome-win</c>) — so the check must scan rather than
    /// assume a fixed path. It deliberately matches only the full headed build:
    /// <list type="bullet">
    /// <item>the <c>chromium-*</c> folder (hyphen) that ships <c>chrome.exe</c>, NOT</item>
    /// <item>the <c>chromium_headless_shell-*</c> folder (underscore) that ships <c>headless_shell.exe</c>.</item>
    /// </list>
    /// The meeting attendee launches Chromium headed-but-off-screen so a real audio render session
    /// exists, which requires the full build. This method is side-effect free and used both for the
    /// idempotency skip and to return the path after install, so the two never disagree.
    /// </summary>
    public static string? ResolveChromiumExecutable(string browsersRoot)
    {
        if (string.IsNullOrWhiteSpace(browsersRoot) || !Directory.Exists(browsersRoot))
        {
            return null;
        }

        foreach (var dir in Directory.EnumerateDirectories(browsersRoot, "chromium-*"))
        {
            // Exclude the headless-shell sibling, whose folder uses an underscore
            // ("chromium_headless_shell-*") and which would not match "chromium-*" anyway; this
            // guard is belt-and-suspenders in case of unexpected naming.
            var name = Path.GetFileName(dir);
            if (name.StartsWith("chromium_headless_shell", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // The platform subfolder is "chrome-win64" on current Playwright builds and "chrome-win"
            // on older ones; probe both rather than hardcoding a single name that breaks across
            // Playwright version bumps.
            foreach (var platformDir in new[] { "chrome-win64", "chrome-win" })
            {
                var exe = Path.Combine(dir, platformDir, "chrome.exe");
                if (File.Exists(exe))
                {
                    return exe;
                }
            }
        }

        return null;
    }
}
