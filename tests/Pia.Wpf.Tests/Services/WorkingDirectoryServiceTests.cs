using System.IO;
using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// Covers <see cref="WorkingDirectoryService"/> enumeration: immediate children only,
/// sandbox-contained, ordinal-ignore-case sorted; graceful empty results on escape/missing.
/// (Sensitive-folder filtering is delegated to <c>SensitivePathGuard</c>, covered by its own
/// tests; the blocklist is computed once at type load and cannot be re-pointed at a temp dir.)
/// </summary>
public class WorkingDirectoryServiceTests : IDisposable
{
    private readonly string _root;
    private readonly WorkingDirectoryService _service;

    public WorkingDirectoryServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "pia-wd-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings { AssistantFilesFolder = _root });

        _service = new WorkingDirectoryService(settings);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void ListSubfolders_Root_ReturnsImmediateChildrenSorted()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Beta"));
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        Directory.CreateDirectory(Path.Combine(_root, "Gamma"));
        // A grandchild must NOT appear at the root level.
        Directory.CreateDirectory(Path.Combine(_root, "alpha", "nested"));

        var result = _service.ListSubfolders("");

        Assert.Equal(new[] { "alpha", "Beta", "Gamma" }, result);
    }

    [Fact]
    public void ListSubfolders_NestedParent_ReturnsOnlyThatLevel()
    {
        Directory.CreateDirectory(Path.Combine(_root, "projects", "app"));
        Directory.CreateDirectory(Path.Combine(_root, "projects", "lib"));
        Directory.CreateDirectory(Path.Combine(_root, "projects", "app", "deep"));

        var result = _service.ListSubfolders("projects");

        Assert.Equal(new[] { "app", "lib" }, result);
    }

    [Fact]
    public void ListSubfolders_NoChildren_ReturnsEmpty()
    {
        Directory.CreateDirectory(Path.Combine(_root, "empty"));
        Assert.Empty(_service.ListSubfolders("empty"));
    }

    [Fact]
    public void ListSubfolders_MissingParent_ReturnsEmpty()
    {
        Assert.Empty(_service.ListSubfolders("nope/missing"));
    }

    [Fact]
    public void ListSubfolders_EscapingParent_ReturnsEmpty()
    {
        Assert.Empty(_service.ListSubfolders("../../"));
    }

    [Fact]
    public void EnsureSubfolder_CreatesFolderAndReturnsNormalizedRelative()
    {
        var result = _service.EnsureSubfolder("Playground");

        Assert.Equal("Playground", result);
        Assert.True(Directory.Exists(Path.Combine(_root, "Playground")));
    }

    [Fact]
    public void EnsureSubfolder_NestedPath_NormalizesToForwardSlashes()
    {
        var result = _service.EnsureSubfolder(@"Playground\Notes");

        Assert.Equal("Playground/Notes", result);
        Assert.True(Directory.Exists(Path.Combine(_root, "Playground", "Notes")));
    }

    [Fact]
    public void EnsureSubfolder_ExistingFolder_IsIdempotent()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Existing"));

        Assert.Equal("Existing", _service.EnsureSubfolder("Existing"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EnsureSubfolder_EmptyInput_ReturnsRoot(string? input)
    {
        Assert.Equal(string.Empty, _service.EnsureSubfolder(input));
    }

    [Fact]
    public void EnsureSubfolder_EscapingPath_ReturnsNull()
    {
        Assert.Null(_service.EnsureSubfolder(@"..\..\escape"));
    }

    [Fact]
    public void EnsureSubfolder_RootedPath_ReturnsNull()
    {
        Assert.Null(_service.EnsureSubfolder(@"C:\Windows"));
    }

    [Fact]
    public void EnsureSubfolder_VaultFolder_ReturnsNull()
    {
        Assert.Null(_service.EnsureSubfolder("Vault"));
        Assert.Null(_service.EnsureSubfolder("Vault/topics"));
        // The vault must not have been created as a side effect of the rejected call.
        Assert.False(Directory.Exists(Path.Combine(_root, "Vault", "topics")));
    }

    [Fact]
    public void ResolveAbsolutePath_ExistingSubfolder_ReturnsAbsolutePath()
    {
        Directory.CreateDirectory(Path.Combine(_root, "projects", "app"));

        var result = _service.ResolveAbsolutePath("projects/app");

        Assert.Equal(Path.Combine(_root, "projects", "app"), result, ignoreCase: true);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveAbsolutePath_EmptyInput_ReturnsSandboxRoot(string? input)
    {
        Assert.Equal(_root, _service.ResolveAbsolutePath(input), ignoreCase: true);
    }

    [Fact]
    public void ResolveAbsolutePath_MissingFolder_ReturnsNull_AndCreatesNothing()
    {
        Assert.Null(_service.ResolveAbsolutePath("nope"));
        Assert.False(Directory.Exists(Path.Combine(_root, "nope")));
    }

    [Fact]
    public void ResolveAbsolutePath_EscapingOrRootedPath_ReturnsNull()
    {
        Assert.Null(_service.ResolveAbsolutePath(@"....escape"));
        Assert.Null(_service.ResolveAbsolutePath(@"C:Windows"));
    }

    [Fact]
    public void ResolveAbsolutePath_NoSandboxConfigured_ReturnsNull()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings { AssistantFilesFolder = null });
        var service = new WorkingDirectoryService(settings);

        Assert.Null(service.ResolveAbsolutePath(""));
    }

    [Fact]
    public void EnsureSubfolder_NoSandboxConfigured_ReturnsNull()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings { AssistantFilesFolder = null });
        var service = new WorkingDirectoryService(settings);

        Assert.Null(service.EnsureSubfolder("Playground"));
    }
}
