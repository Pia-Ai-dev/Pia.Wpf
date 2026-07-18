using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Services.Plugins;
using Pia.Shared.Models;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// Locks the wiring that makes the git tool pack REACHABLE by the model: a preloaded, default-enabled
/// built-in whose adapter exposes the git_* tools plus a system prompt that names the exact tools and
/// states the no-network scope. Combined with PluginService's "git" switch arm and its defaultEnabled
/// gate, this ensures GetAllTools() surfaces the git tools.
/// </summary>
public sealed class GitPluginRegistrationTests : IDisposable
{
    private readonly string _root;

    public GitPluginRegistrationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "pia-git-reg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static SyncPlugin GitConfig() =>
        BuiltInPluginDefaults.Defaults[BuiltInPluginDefaults.GitPluginId];

    [Fact]
    public void GitPlugin_IsPreloadedAndDefaultEnabled()
    {
        Assert.Contains(BuiltInPluginDefaults.GitPluginId, BuiltInPluginDefaults.PreloadedPluginIds);

        var config = GitConfig();
        Assert.True(config.IsPreloaded);
        Assert.True(config.IsActive);
        Assert.Equal("git", config.Name);
        Assert.Contains("\"handlerId\":\"git\"", config.ConfigJson);
        Assert.Contains("\"defaultEnabled\":true", config.ConfigJson);
    }

    [Fact]
    public void GitPlugin_SystemPrompt_NamesExactToolsAndNoNetworkScope()
    {
        var config = GitConfig().ConfigJson;

        // The model must learn the exact registered tool names (so it doesn't hallucinate variants)...
        foreach (var tool in new[]
        {
            "git_status", "git_log", "git_diff", "git_branch", "git_show",
            "git_init", "git_add", "git_commit", "git_switch", "git_restore", "git_stash",
        })
        {
            Assert.Contains(tool, config);
        }

        // ...and the no-network scope (a distinctive phrase, not a bare "push" that could read affirmatively)
        // + the fresh-folder recipe.
        Assert.Contains("NO network operations", config);
        Assert.Contains("push, pull, fetch, or clone", config);
        Assert.Contains("git_init", config);
    }

    [Fact]
    public void FromGitHandler_ExposesGitToolsAndSystemPrompt_WhenAvailable()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings
        {
            AssistantFilesFolder = _root,
            AssistantGitToolsEnabled = true,
        });
        var handler = new GitToolHandler(
            settings, new FakeGitProcessRunner { IsGitInstalled = true }, NullLogger<GitToolHandler>.Instance);

        var adapter = BuiltInPluginHandler.FromGitHandler(handler, GitConfig());

        Assert.Contains(adapter.GetTools(), t => t.Name == "git_status");
        Assert.Contains(adapter.GetTools(), t => t.Name == "git_init");
        Assert.False(string.IsNullOrWhiteSpace(adapter.GetSystemPromptAddition()));
    }

    [Fact]
    public void FromGitHandler_SuppressesToolsAndPrompt_WhenUnavailable()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings
        {
            AssistantFilesFolder = _root,
            AssistantGitToolsEnabled = true,
        });
        // Git not installed ⇒ handler.IsAvailable false ⇒ adapter suppresses both tools and prompt.
        var handler = new GitToolHandler(
            settings, new FakeGitProcessRunner { IsGitInstalled = false }, NullLogger<GitToolHandler>.Instance);

        var adapter = BuiltInPluginHandler.FromGitHandler(handler, GitConfig());

        Assert.Empty(adapter.GetTools());
        Assert.Null(adapter.GetSystemPromptAddition());
    }
}
