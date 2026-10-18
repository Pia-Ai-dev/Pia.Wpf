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

public sealed class FilesToolHandlerResolveToolRootTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pia-root-" + Guid.NewGuid().ToString("N"));

    public FilesToolHandlerResolveToolRootTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        TaskAmbient.Current = null;
        TempPath.Remove(_root);
    }

    private FilesToolHandler CreateSut(string? folder) => new(
        SettingsStubs.Returning(new AppSettings { AssistantFilesFolder = folder, AssistantFileToolsEnabled = true }),
        Substitute.For<IFileStalenessStore>(),
        NullLogger<FilesToolHandler>.Instance);

    [Fact]
    public void WithTheConfiguredFolder_ReturnsIt()
    {
        Assert.Equal(SafeFolderPath.Canonicalize(_root), SafeFolderPath.Canonicalize(CreateSut(_root).ResolveToolRoot()!));
    }

    [Fact]
    public void WithNoFolder_ReturnsNull()
    {
        Assert.Null(CreateSut(null).ResolveToolRoot());
    }

    [Fact]
    public void AnAmbientWorkspaceRoot_WinsOverTheConfiguredFolder()
    {
        var workspace = Directory.CreateDirectory(Path.Combine(_root, "runs", "r1")).FullName;
        TaskAmbient.Current = new TaskContext(Guid.NewGuid(), null, WorkspaceRoot: workspace);

        Assert.Equal(SafeFolderPath.Canonicalize(workspace), CreateSut(null).ResolveToolRoot());
    }

    [Fact]
    public void AWorkingSubpath_NarrowsTheRoot()
    {
        var sub = Directory.CreateDirectory(Path.Combine(_root, "project")).FullName;
        TaskAmbient.Current = new TaskContext(Guid.NewGuid(), "project");

        Assert.Equal(SafeFolderPath.Canonicalize(sub), SafeFolderPath.Canonicalize(CreateSut(_root).ResolveToolRoot()!));
    }
}
