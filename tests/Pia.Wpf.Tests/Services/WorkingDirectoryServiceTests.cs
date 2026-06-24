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
}
