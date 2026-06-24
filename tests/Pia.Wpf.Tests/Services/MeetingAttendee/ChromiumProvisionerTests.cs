using System;
using System.IO;
using Pia.Services.MeetingAttendee;
using Xunit;

namespace Pia.Tests.Services.MeetingAttendee;

/// <summary>
/// Exercises the pure path-resolution logic only. The actual download (Playwright installer +
/// network) is deliberately NOT exercised here.
/// </summary>
public sealed class ChromiumProvisionerTests : IDisposable
{
    private readonly string _root;

    public ChromiumProvisionerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "PiaChromiumProvisionerTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
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
        var exe = CreateBuild("chromium-1187", "chrome-win", "chrome.exe");

        var resolved = ChromiumProvisioner.ResolveChromiumExecutable(_root);

        Assert.Equal(exe, resolved);
    }

    [Fact]
    public void ResolveChromiumExecutable_FindsFullHeadedBuild_WithWin64PlatformFolder()
    {
        // Current Playwright builds lay the headed Chromium out under "chrome-win64" (x64) rather
        // than the older "chrome-win". Regression guard for the install-succeeds-but-not-found bug.
        var exe = CreateBuild("chromium-1217", "chrome-win64", "chrome.exe");

        var resolved = ChromiumProvisioner.ResolveChromiumExecutable(_root);

        Assert.Equal(exe, resolved);
    }

    [Fact]
    public void ResolveChromiumExecutable_IgnoresHeadlessShell_AndReturnsNull_WhenOnlyShellPresent()
    {
        // The headless-shell folder uses an underscore and ships headless_shell.exe, not chrome.exe.
        CreateBuild("chromium_headless_shell-1187", "chrome-win", "headless_shell.exe");

        Assert.Null(ChromiumProvisioner.ResolveChromiumExecutable(_root));
    }

    [Fact]
    public void ResolveChromiumExecutable_PrefersFullBuild_WhenBothPresent()
    {
        var fullExe = CreateBuild("chromium-1187", "chrome-win", "chrome.exe");
        CreateBuild("chromium_headless_shell-1187", "chrome-win", "headless_shell.exe");

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

    private string CreateBuild(string revisionFolder, string platformFolder, string exeName)
    {
        var dir = Path.Combine(_root, revisionFolder, platformFolder);
        Directory.CreateDirectory(dir);
        var exe = Path.Combine(dir, exeName);
        File.WriteAllText(exe, "stub");
        return exe;
    }
}
