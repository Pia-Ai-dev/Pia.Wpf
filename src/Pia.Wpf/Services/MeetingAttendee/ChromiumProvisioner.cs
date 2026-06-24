using System.IO;
using Microsoft.Extensions.Logging;

namespace Pia.Services.MeetingAttendee;

/// <summary>
/// Provisions the Chromium browser used by the meeting attendee, caching it under
/// <c>%LOCALAPPDATA%\Pia\Browsers</c>. Modelled on the model-download flow in
/// <see cref="Pia.Services.LiveTranscription.LiveTranscriptionModels"/>: everything lands under
/// <c>%LOCALAPPDATA%\Pia</c>, the operation is idempotent, and progress is reported through an
/// optional <see cref="IProgress{T}"/> sink.
///
/// Provisioning is delegated to Playwright's own installer
/// (<see cref="Microsoft.Playwright.Program.Main(string[])"/> with <c>["install","chromium"]</c>),
/// which honours the <c>PLAYWRIGHT_BROWSERS_PATH</c> environment variable for the cache location
/// and an optional <c>PLAYWRIGHT_DOWNLOAD_HOST</c> override for the CDN.
/// </summary>
public sealed class ChromiumProvisioner : IBrowserProvisioner
{
    private readonly ILogger<ChromiumProvisioner> _logger;

    /// <summary>Cache root for downloaded browsers (the <c>PLAYWRIGHT_BROWSERS_PATH</c> value).</summary>
    public static string BrowsersDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Pia", "Browsers");

    /// <summary>
    /// Optional override for the Playwright download host (CDN), wired to the
    /// <c>PLAYWRIGHT_DOWNLOAD_HOST</c> environment variable.
    ///
    /// TODO (OPEN QUESTION #2): the central/self-hosted Chromium download host is not yet decided.
    /// Leaving this <c>null</c> means "use Playwright's own built-in default", which IS the public
    /// Playwright CDN for the pinned package version — so we inherit the correct, version-matched
    /// host automatically and avoid hardcoding a literal URL that rots across versions. When a
    /// central host is chosen, surface it here (or via an AppSettings hook) and it will be applied
    /// before the installer runs.
    /// </summary>
    public static string? DownloadHostOverride { get; set; }

    public ChromiumProvisioner(ILogger<ChromiumProvisioner> logger)
    {
        _logger = logger;
    }

    public async Task<string> EnsureChromiumAsync(
        IProgress<ChromiumDownloadProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Directory.CreateDirectory(BrowsersDirectory);

        // Always point Playwright (this process and any browser launches downstream) at our cache,
        // even on the skip path, so the launcher in Unit 2 resolves the same install.
        Environment.SetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH", BrowsersDirectory);
        if (!string.IsNullOrWhiteSpace(DownloadHostOverride))
        {
            Environment.SetEnvironmentVariable("PLAYWRIGHT_DOWNLOAD_HOST", DownloadHostOverride);
        }

        // Idempotency: if a usable Chromium is already cached, skip the (slow, network-bound)
        // install and return the cached executable. Same resolver is reused after install below.
        var cached = ResolveChromiumExecutable(BrowsersDirectory);
        if (cached is not null)
        {
            progress?.Report(new ChromiumDownloadProgress(ChromiumProvisioningPhase.AlreadyPresent));
            return cached;
        }

        // Playwright's installer is opaque (it shells out and writes its own progress to stdout),
        // so we cannot drive a byte-level percentage. Report a coarse "Downloading" phase, mirroring
        // the indeterminate "Extracting" phase used by the model downloader.
        _logger.LogInformation("Provisioning Chromium browser into cache (this can take a while on first run)");
        progress?.Report(new ChromiumDownloadProgress(ChromiumProvisioningPhase.Downloading));

        var exitCode = await Task.Run(
            () => Microsoft.Playwright.Program.Main(["install", "chromium"]),
            cancellationToken).ConfigureAwait(false);

        if (exitCode != 0)
        {
            throw new InvalidOperationException(
                $"Playwright Chromium install failed with exit code {exitCode}.");
        }

        var resolved = ResolveChromiumExecutable(BrowsersDirectory)
            ?? throw new InvalidOperationException(
                "Chromium install reported success but no Chromium executable was found in the cache.");

        progress?.Report(new ChromiumDownloadProgress(ChromiumProvisioningPhase.Completed));
        _logger.LogInformation("Chromium browser provisioning complete");
        return resolved;
    }

    /// <summary>
    /// Pure resolver: scans <paramref name="browsersRoot"/> for a cached full Chromium build and
    /// returns the path to its <c>chrome.exe</c>, or <c>null</c> if none is present.
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
