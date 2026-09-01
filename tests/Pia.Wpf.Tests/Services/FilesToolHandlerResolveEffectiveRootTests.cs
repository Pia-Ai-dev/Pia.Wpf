using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// Covers <see cref="FilesToolHandler.ResolveEffectiveRoot"/>: the per-chat working-subpath
/// narrowing that runs at the dispatch entry point. Null/empty → base; valid existing subpath →
/// narrowed; escape or missing → base (fail safe). <see cref="FilesToolHandler.DescribeEffectiveRoot"/>
/// is that same narrowing over the configured folder.
/// </summary>
public class FilesToolHandlerResolveEffectiveRootTests : IDisposable
{
    private readonly string _root;
    private readonly FilesToolHandler _handler;

    public FilesToolHandlerResolveEffectiveRootTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "pia-effroot-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings { AssistantFilesFolder = _root });

        _handler = new FilesToolHandler(settings, new FileStalenessStore(), NullLogger<FilesToolHandler>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void NullSubpath_ReturnsBase()
    {
        Assert.Equal(_root, _handler.ResolveEffectiveRoot(_root, null));
    }

    [Fact]
    public void WhitespaceSubpath_ReturnsBase()
    {
        Assert.Equal(_root, _handler.ResolveEffectiveRoot(_root, "   "));
    }

    [Fact]
    public void ValidExistingSubpath_ReturnsNarrowedRoot()
    {
        var sub = Path.Combine(_root, "projects", "app");
        Directory.CreateDirectory(sub);

        var eff = _handler.ResolveEffectiveRoot(_root, "projects/app");

        Assert.NotEqual(_root, eff);
        // The effective root is the narrowed subfolder (canonicalized).
        Assert.EndsWith("app", eff.TrimEnd(Path.DirectorySeparatorChar));
        Assert.True(Directory.Exists(eff));
    }

    [Fact]
    public void MissingSubpath_FallsBackToBase()
    {
        var eff = _handler.ResolveEffectiveRoot(_root, "does/not/exist");
        Assert.Equal(_root, eff);
    }

    [Fact]
    public void EscapingSubpath_FallsBackToBase()
    {
        // "../.." escapes the sandbox → containment rejects → base.
        var eff = _handler.ResolveEffectiveRoot(_root, "../../elsewhere");
        Assert.Equal(_root, eff);
    }
    [Fact]
    public void DescribeEffectiveRoot_NoFolderConfigured_ReturnsNull()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings { AssistantFilesFolder = null });
        var handler = new FilesToolHandler(settings, new FileStalenessStore(), NullLogger<FilesToolHandler>.Instance);

        Assert.Null(handler.DescribeEffectiveRoot(null));
    }

    [Fact]
    public void DescribeEffectiveRoot_ToolsDisabled_ReturnsNull()
    {
        // The folder stays configured when the toggle goes off (the vault lives under it), so the
        // switch is the only thing standing between a disabled tool and a prompt naming its sandbox.
        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings
        {
            AssistantFilesFolder = _root,
            AssistantFileToolsEnabled = false,
        });
        var handler = new FilesToolHandler(settings, new FileStalenessStore(), NullLogger<FilesToolHandler>.Instance);

        Assert.False(handler.IsAvailable);
        Assert.Null(handler.DescribeEffectiveRoot(null));
    }

    [Fact]
    public void DescribeEffectiveRoot_NullSubpath_ReturnsTheConfiguredRoot()
    {
        Assert.Equal(SafeFolderPath.Canonicalize(_root), _handler.DescribeEffectiveRoot(null));
    }

    [Fact]
    public void DescribeEffectiveRoot_ValidSubpath_ReturnsTheNarrowedFolder()
    {
        Directory.CreateDirectory(Path.Combine(_root, "projects", "app"));

        var described = _handler.DescribeEffectiveRoot("projects/app");

        Assert.NotNull(described);
        Assert.NotEqual(_handler.DescribeEffectiveRoot(null), described);
        Assert.EndsWith("app", described.TrimEnd(Path.DirectorySeparatorChar), StringComparison.Ordinal);
        Assert.True(Directory.Exists(described));
    }

    [Fact]
    public void DescribeEffectiveRoot_EscapingSubpath_FallsBackToTheConfiguredRoot()
    {
        Assert.Equal(_handler.DescribeEffectiveRoot(null), _handler.DescribeEffectiveRoot("../../elsewhere"));
    }

    [Fact]
    public void DescribeEffectiveRoot_MissingSubpath_FallsBackToTheConfiguredRoot()
    {
        Assert.Equal(_handler.DescribeEffectiveRoot(null), _handler.DescribeEffectiveRoot("does/not/exist"));
    }
}
