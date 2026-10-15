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

public sealed class FilesToolHandlerReadFilePathTests : IDisposable
{
    private readonly string _root;
    private readonly FilesToolHandler _handler;

    public FilesToolHandlerReadFilePathTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "pia-readpath-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "proj", "src"));
        Directory.CreateDirectory(Path.Combine(_root, "other"));
        File.WriteAllText(Path.Combine(_root, "notes.txt"), "x");
        File.WriteAllText(Path.Combine(_root, "proj", "src", "a.cs"), "class A {}");
        File.WriteAllText(Path.Combine(_root, "other", "b.txt"), "x");

        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings { AssistantFilesFolder = _root });
        _handler = new FilesToolHandler(settings, new FileStalenessStore(), NullLogger<FilesToolHandler>.Instance);
    }

    public void Dispose() => TempPath.Remove(_root);

    [Fact]
    public void ToReadFilePath_AFileAtTheRoot_IsItsName() =>
        Assert.Equal("notes.txt", _handler.ToReadFilePath(Path.Combine(_root, "notes.txt"), workingSubpath: null));

    [Fact]
    public void ToReadFilePath_ASandboxRelativePath_ResolvesAgainstTheWorkingSubpath() =>
        Assert.Equal("src/a.cs", _handler.ToReadFilePath("proj/src/a.cs", workingSubpath: "proj"));

    [Fact]
    public void ToReadFilePath_AnAbsolutePathInsideTheWorkingSubpath_IsRelative() =>
        Assert.Equal("src/a.cs", _handler.ToReadFilePath(Path.Combine(_root, "proj", "src", "a.cs"), "proj"));

    [Fact]
    public void ToReadFilePath_OutsideTheWorkingSubpath_IsNull() =>
        Assert.Null(_handler.ToReadFilePath(Path.Combine(_root, "other", "b.txt"), "proj"));

    [Fact]
    public void ToReadFilePath_OutsideTheFilesFolder_IsNull()
    {
        var outside = Path.Combine(Path.GetTempPath(), "pia-outside-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(outside, "x");
        try
        {
            Assert.Null(_handler.ToReadFilePath(outside, workingSubpath: null));
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public void ToReadFilePath_AMissingFile_IsNull() =>
        Assert.Null(_handler.ToReadFilePath(Path.Combine(_root, "gone.txt"), workingSubpath: null));
}
