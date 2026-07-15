using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// The git tools are available only when git is installed (from the injected runner) AND the tools are
/// enabled AND a sandbox folder is configured. Git-free: the runner's git-installed flag is substituted,
/// so the suite doesn't depend on git being on the box.
/// </summary>
public sealed class GitToolHandlerAvailabilityTests : IDisposable
{
    private readonly string _root;

    public GitToolHandlerAvailabilityTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "pia-git-avail-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private GitToolHandler Handler(bool gitInstalled, bool enabled, string? folder)
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings
        {
            AssistantFilesFolder = folder,
            AssistantGitToolsEnabled = enabled,
        });
        var runner = new FakeGitProcessRunner { IsGitInstalled = gitInstalled };
        return new GitToolHandler(settings, runner, NullLogger<GitToolHandler>.Instance);
    }

    [Fact]
    public void Available_when_installed_enabled_and_folder_set()
        => Assert.True(Handler(gitInstalled: true, enabled: true, folder: _root).IsAvailable);

    [Fact]
    public void Unavailable_when_git_not_installed()
        => Assert.False(Handler(gitInstalled: false, enabled: true, folder: _root).IsAvailable);

    [Fact]
    public void Unavailable_when_disabled()
        => Assert.False(Handler(gitInstalled: true, enabled: false, folder: _root).IsAvailable);

    [Fact]
    public void Unavailable_when_no_folder()
        => Assert.False(Handler(gitInstalled: true, enabled: true, folder: null).IsAvailable);

    [Fact]
    public void GetTools_empty_when_unavailable()
        => Assert.Empty(Handler(gitInstalled: false, enabled: true, folder: _root).GetTools());

    [Fact]
    public void GetTools_exposes_the_expected_tool_names_when_available()
    {
        var names = Handler(gitInstalled: true, enabled: true, folder: _root).GetTools().Select(t => t.Name).ToList();
        foreach (var expected in new[] { "git_status", "git_log", "git_diff", "git_branch", "git_show", "git_init" })
            Assert.Contains(expected, names);
    }
}
