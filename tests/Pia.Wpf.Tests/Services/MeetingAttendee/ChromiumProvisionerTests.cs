using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Services.MeetingAttendee;
using Xunit;

namespace Pia.Tests.Services.MeetingAttendee;

/// <summary>
/// Exercises path resolution and the install decision. The Playwright CLI is replaced through
/// <see cref="ChromiumProvisioner.InstallerInvoker"/>, so nothing here touches the network or the
/// real profile.
/// </summary>
public sealed class ChromiumProvisionerTests : IDisposable
{
    private readonly string _root;
    private readonly string _cache;
    private readonly string _bundled;
    private readonly Func<string[], int> _originalInstaller = ChromiumProvisioner.InstallerInvoker;
    private readonly Func<string, bool> _originalProbe = ChromiumProvisioner.ExecutableProbe;
    private readonly string? _originalBrowsersPath = Environment.GetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH");

    public ChromiumProvisionerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "PiaChromiumProvisionerTests_" + Guid.NewGuid().ToString("N"));
        _cache = Path.Combine(_root, "cache");
        _bundled = Path.Combine(_root, "bundled");
        Directory.CreateDirectory(_root);
        ChromiumProvisioner.FailedInstallVersion = null;
        ChromiumProvisioner.VerifiedBundledExecutable = null;
        // The stub builds are text files, so the real probe would (correctly) refuse to run them.
        ChromiumProvisioner.ExecutableProbe = _ => true;
    }

    public void Dispose()
    {
        ChromiumProvisioner.InstallerInvoker = _originalInstaller;
        ChromiumProvisioner.ExecutableProbe = _originalProbe;
        ChromiumProvisioner.FailedInstallVersion = null;
        ChromiumProvisioner.VerifiedBundledExecutable = null;
        Environment.SetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH", _originalBrowsersPath);

        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch { /* best-effort temp cleanup */ }
    }

    [Fact]
    public void ResolveChromiumExecutable_ReturnsNull_WhenRootMissing()
    {
        var missing = Path.Combine(_root, "does-not-exist");

        Assert.Null(ChromiumProvisioner.ResolveChromiumExecutable(missing));
    }

    [Fact]
    public void ResolveChromiumExecutable_ReturnsNull_WhenRootEmpty()
    {
        Assert.Null(ChromiumProvisioner.ResolveChromiumExecutable(_root));
    }

    [Fact]
    public void ResolveChromiumExecutable_ReturnsNull_ForNullOrWhitespaceRoot()
    {
        Assert.Null(ChromiumProvisioner.ResolveChromiumExecutable(null!));
        Assert.Null(ChromiumProvisioner.ResolveChromiumExecutable("   "));
    }

    [Fact]
    public void ResolveChromiumExecutable_FindsFullHeadedBuild()
    {
        var exe = CreateBuild(_root, "chromium-1187", "chrome-win", "chrome.exe");

        var resolved = ChromiumProvisioner.ResolveChromiumExecutable(_root);

        Assert.Equal(exe, resolved);
    }

    [Fact]
    public void ResolveChromiumExecutable_FindsFullHeadedBuild_WithWin64PlatformFolder()
    {
        // Current Playwright builds lay the headed Chromium out under "chrome-win64" (x64) rather
        // than the older "chrome-win". Regression guard for the install-succeeds-but-not-found bug.
        var exe = CreateBuild(_root, "chromium-1217", "chrome-win64", "chrome.exe");

        var resolved = ChromiumProvisioner.ResolveChromiumExecutable(_root);

        Assert.Equal(exe, resolved);
    }

    [Fact]
    public void ResolveChromiumExecutable_IgnoresHeadlessShell_AndReturnsNull_WhenOnlyShellPresent()
    {
        // The headless-shell folder uses an underscore and ships headless_shell.exe, not chrome.exe.
        CreateBuild(_root, "chromium_headless_shell-1187", "chrome-win", "headless_shell.exe");

        Assert.Null(ChromiumProvisioner.ResolveChromiumExecutable(_root));
    }

    [Fact]
    public void ResolveChromiumExecutable_PrefersFullBuild_WhenBothPresent()
    {
        var fullExe = CreateBuild(_root, "chromium-1187", "chrome-win", "chrome.exe");
        CreateBuild(_root, "chromium_headless_shell-1187", "chrome-win", "headless_shell.exe");

        var resolved = ChromiumProvisioner.ResolveChromiumExecutable(_root);

        Assert.Equal(fullExe, resolved);
    }

    [Fact]
    public void ResolveChromiumExecutable_ReturnsNull_WhenChromiumFolderLacksExecutable()
    {
        // Folder name matches but the chrome.exe is absent (e.g. partial/aborted install).
        Directory.CreateDirectory(Path.Combine(_root, "chromium-1187", "chrome-win"));

        Assert.Null(ChromiumProvisioner.ResolveChromiumExecutable(_root));
    }

    [Fact]
    public void BrowsersDirectory_LivesUnderPiaLocalAppData()
    {
        var dir = ChromiumProvisioner.BrowsersDirectory;

        Assert.Contains("Pia", dir);
        Assert.EndsWith("Browsers", dir);
    }

    [Fact]
    public void BundledAndDownloadedRoots_AreDistinct()
    {
        // If they ever collided, the bundled branch would delete the payload it just resolved.
        Assert.NotEqual(
            Path.GetFullPath(ChromiumProvisioner.BundledBrowsersDirectory),
            Path.GetFullPath(ChromiumProvisioner.BrowsersDirectory));
    }

    [Fact]
    public async Task EnsureChromium_PrefersTheBundledBuild_AndNeverRunsTheInstaller()
    {
        // A bundled build carries a .links entry from the machine that staged it; handing that
        // registry to the installer is what would GC the payload.
        var bundledExe = CreateBuild(_bundled, "chromium-1228", "chrome-win64", "chrome.exe");
        CreateBuild(_cache, "chromium-1217", "chrome-win64", "chrome.exe");
        var calls = new List<string[]>();
        ChromiumProvisioner.InstallerInvoker = args => { calls.Add(args); return 0; };

        var resolved = await NewProvisioner().EnsureChromiumAsync(_bundled, _cache, null, TestContext.Current.CancellationToken);

        Assert.Equal(bundledExe, resolved);
        Assert.Empty(calls);
        // The superseded download is removed rather than left to sit unpatched forever.
        Assert.False(Directory.Exists(_cache));
    }

    [Fact]
    public async Task EnsureChromium_IgnoresABundledBuildThatWillNotStart_AndKeepsTheDownload()
    {
        // Quarantined, half-copied or ACL-blocked payload: deleting the cache here would leave the
        // client with no browser and no way back, because the bundled branch never installs.
        CreateBuild(_bundled, "chromium-1228", "chrome-win64", "chrome.exe");
        var cachedExe = CreateBuild(_cache, "chromium-1217", "chrome-win64", "chrome.exe");
        ChromiumProvisioner.WriteVersionMarker(_cache, ChromiumProvisioner.PinnedPlaywrightVersion);
        ChromiumProvisioner.ExecutableProbe = _ => false;

        var resolved = await NewProvisioner().EnsureChromiumAsync(_bundled, _cache, null, TestContext.Current.CancellationToken);

        Assert.Equal(cachedExe, resolved);
        Assert.True(Directory.Exists(_cache));
    }

    [Fact]
    public async Task EnsureChromium_ProbesTheBundledBuildOnlyOnce()
    {
        CreateBuild(_bundled, "chromium-1228", "chrome-win64", "chrome.exe");
        var probes = 0;
        ChromiumProvisioner.ExecutableProbe = _ => { probes++; return true; };
        var sut = NewProvisioner();

        await sut.EnsureChromiumAsync(_bundled, _cache, null, TestContext.Current.CancellationToken);
        await sut.EnsureChromiumAsync(_bundled, _cache, null, TestContext.Current.CancellationToken);

        Assert.Equal(1, probes);
    }

    [Fact]
    public async Task EnsureChromium_SkipsInstall_WhenTheMarkerMatchesThePinnedVersion()
    {
        var exe = CreateBuild(_cache, "chromium-1228", "chrome-win64", "chrome.exe");
        ChromiumProvisioner.WriteVersionMarker(_cache, ChromiumProvisioner.PinnedPlaywrightVersion);
        var calls = 0;
        ChromiumProvisioner.InstallerInvoker = _ => { calls++; return 0; };

        var resolved = await NewProvisioner().EnsureChromiumAsync(_bundled, _cache, null, TestContext.Current.CancellationToken);

        Assert.Equal(exe, resolved);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(null)]                // what every client installed before the marker existed has
    [InlineData("1.0.0-ancient")]
    public async Task EnsureChromium_Reinstalls_WhenTheCacheDoesNotMatchThePinnedVersion(string? marker)
    {
        CreateBuild(_cache, "chromium-1217", "chrome-win64", "chrome.exe");
        if (marker is not null) ChromiumProvisioner.WriteVersionMarker(_cache, marker);
        string[]? args = null;
        ChromiumProvisioner.InstallerInvoker = a =>
        {
            args = a;
            // What the real installer does: GC the unreferenced revision, land the pinned one.
            Directory.Delete(Path.Combine(_cache, "chromium-1217"), recursive: true);
            CreateBuild(_cache, "chromium-1228", "chrome-win64", "chrome.exe");
            return 0;
        };

        var resolved = await NewProvisioner().EnsureChromiumAsync(_bundled, _cache, null, TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "install", "chromium", "--no-shell" }, args);
        Assert.Contains("chromium-1228", resolved);
        Assert.Equal(ChromiumProvisioner.PinnedPlaywrightVersion, ChromiumProvisioner.ReadVersionMarker(_cache));
    }

    [Fact]
    public async Task EnsureChromium_FallsBackToTheCachedBrowser_WhenTheInstallFails()
    {
        // Offline or CDN-blocked: an outdated browser still joins the meeting, a throw does not.
        var exe = CreateBuild(_cache, "chromium-1217", "chrome-win64", "chrome.exe");
        ChromiumProvisioner.InstallerInvoker = _ => 1;

        var resolved = await NewProvisioner().EnsureChromiumAsync(_bundled, _cache, null, TestContext.Current.CancellationToken);

        Assert.Equal(exe, resolved);
        Assert.Null(ChromiumProvisioner.ReadVersionMarker(_cache));
    }

    [Fact]
    public async Task EnsureChromium_DoesNotRetryAFailedInstall_ForTheSameVersion()
    {
        CreateBuild(_cache, "chromium-1217", "chrome-win64", "chrome.exe");
        var calls = 0;
        ChromiumProvisioner.InstallerInvoker = _ => { calls++; return 1; };
        var sut = NewProvisioner();

        await sut.EnsureChromiumAsync(_bundled, _cache, null, TestContext.Current.CancellationToken);
        await sut.EnsureChromiumAsync(_bundled, _cache, null, TestContext.Current.CancellationToken);

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task EnsureChromium_Throws_WhenTheFailedInstallAlsoPrunedTheCachedBrowser()
    {
        // The installer GCs before it downloads, so the path probed beforehand can be gone.
        CreateBuild(_cache, "chromium-1217", "chrome-win64", "chrome.exe");
        ChromiumProvisioner.InstallerInvoker = _ =>
        {
            Directory.Delete(Path.Combine(_cache, "chromium-1217"), recursive: true);
            return 1;
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewProvisioner().EnsureChromiumAsync(_bundled, _cache, null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task EnsureChromium_Throws_WhenTheInstallThrows_AndNothingIsCached()
    {
        ChromiumProvisioner.InstallerInvoker = _ => throw new InvalidOperationException("driver missing");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewProvisioner().EnsureChromiumAsync(_bundled, _cache, null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void DeleteBrowserCache_RemovesTheWholeTree()
    {
        CreateBuild(_cache, "chromium-1228", "chrome-win64", "chrome.exe");

        ChromiumProvisioner.DeleteBrowserCache(_cache);

        Assert.False(Directory.Exists(_cache));
    }

    [Fact]
    public void VersionMarker_RoundTrips_AndIsNullWhenAbsent()
    {
        Directory.CreateDirectory(_cache);
        Assert.Null(ChromiumProvisioner.ReadVersionMarker(_cache));

        ChromiumProvisioner.WriteVersionMarker(_cache, "1.61.0");

        Assert.Equal("1.61.0", ChromiumProvisioner.ReadVersionMarker(_cache));
    }

    private static ChromiumProvisioner NewProvisioner() => new(NullLogger<ChromiumProvisioner>.Instance);

    private static string CreateBuild(string root, string revisionFolder, string platformFolder, string exeName)
    {
        var dir = Path.Combine(root, revisionFolder, platformFolder);
        Directory.CreateDirectory(dir);
        var exe = Path.Combine(dir, exeName);
        File.WriteAllText(exe, "stub");
        return exe;
    }
}
