using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// Covers <see cref="AttachedFileStore"/>: a composer attachment lands in the chat's working directory,
/// never overwrites, never duplicates a file that is already in the sandbox, and never escapes it.
/// </summary>
public class AttachedFileStoreTests : IDisposable
{
    private readonly string _root;
    private readonly string _outside;
    private readonly AttachedFileStore _store;

    public AttachedFileStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "pia-afs-tests-" + Guid.NewGuid().ToString("N"));
        _outside = Path.Combine(Path.GetTempPath(), "pia-afs-src-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_outside);

        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings { AssistantFilesFolder = _root });

        _store = new AttachedFileStore(
            settings, new WorkingDirectoryService(settings), NullLogger<AttachedFileStore>.Instance);
    }

    public void Dispose()
    {
        TempPath.Remove(_root);
        TempPath.Remove(_outside);
    }

    private string SourceFile(string name = "notes.txt", string content = "hello")
    {
        var path = Path.Combine(_outside, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void SaveIntoWorkingDirectory_CopiesIntoTheWorkingDirectory()
    {
        var relative = _store.SaveIntoWorkingDirectory(SourceFile(), "Playground");

        Assert.Equal("Playground/notes.txt", relative);
        Assert.Equal("hello", File.ReadAllText(Path.Combine(_root, "Playground", "notes.txt")));
    }

    [Fact]
    public void SaveIntoWorkingDirectory_NullWorkingDirectory_LandsAtTheSandboxRoot()
    {
        var relative = _store.SaveIntoWorkingDirectory(SourceFile(), null);

        Assert.Equal("notes.txt", relative);
        Assert.True(File.Exists(Path.Combine(_root, "notes.txt")));
    }

    [Fact]
    public void SaveIntoWorkingDirectory_NameCollision_SuffixesInsteadOfOverwriting()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Playground"));
        File.WriteAllText(Path.Combine(_root, "Playground", "notes.txt"), "the one already there");

        var relative = _store.SaveIntoWorkingDirectory(SourceFile(), "Playground");

        Assert.Equal("Playground/notes (2).txt", relative);
        Assert.Equal("the one already there",
            File.ReadAllText(Path.Combine(_root, "Playground", "notes.txt")));
    }

    [Fact]
    public void SaveIntoWorkingDirectory_SourceAlreadyInsideTheSandbox_DoesNotDuplicate()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Playground"));
        var inside = Path.Combine(_root, "Playground", "already.txt");
        File.WriteAllText(inside, "hello");

        var relative = _store.SaveIntoWorkingDirectory(inside, "Other");

        Assert.Equal("Playground/already.txt", relative);
        Assert.False(Directory.Exists(Path.Combine(_root, "Other")));
    }

    [Fact]
    public void SaveIntoWorkingDirectory_RelativeSource_IsNotMistakenForOneAlreadyInside()
    {
        // "notes.txt" resolves against the sandbox and would otherwise be reported as already saved
        // while the real file sits next to the working directory of the process.
        Assert.Null(_store.SaveIntoWorkingDirectory("notes.txt", "Playground"));
    }

    [Fact]
    public void SaveIntoWorkingDirectory_VaultTarget_IsRefused()
    {
        var relative = _store.SaveIntoWorkingDirectory(SourceFile(), AssistantWorkspace.VaultSubfolderName);

        Assert.Null(relative);
    }

    [Fact]
    public void SaveIntoWorkingDirectory_EscapingWorkingDirectory_IsRefused()
    {
        Assert.Null(_store.SaveIntoWorkingDirectory(SourceFile(), "../escaped"));
    }

    [Fact]
    public void SaveIntoWorkingDirectory_MissingSource_ReturnsNull()
    {
        Assert.Null(_store.SaveIntoWorkingDirectory(Path.Combine(_outside, "gone.txt"), "Playground"));
    }

    [Fact]
    public void SaveIntoWorkingDirectory_UnconfiguredSandbox_ReturnsNull()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings { AssistantFilesFolder = "" });
        var store = new AttachedFileStore(
            settings, new WorkingDirectoryService(settings), NullLogger<AttachedFileStore>.Instance);

        Assert.Null(store.SaveIntoWorkingDirectory(SourceFile(), "Playground"));
    }

    [Fact]
    public void ResolveAbsolute_ComposesWithoutProbingDisk()
    {
        // A since-deleted file still yields a path: the open is best-effort, and probing every history
        // row would cost a disk hit per chip.
        var resolved = _store.ResolveAbsolute("Playground/gone.txt");

        Assert.Equal(Path.Combine(_root, "Playground", "gone.txt"), resolved);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("../escaped.txt")]
    [InlineData("C:/Windows/system.ini")]
    public void ResolveAbsolute_RejectsAnythingOutsideTheSandbox(string? relative)
    {
        Assert.Null(_store.ResolveAbsolute(relative));
    }
}
