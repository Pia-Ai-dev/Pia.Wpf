using System.IO;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>Roots the fixture inside a guard-checked <c>Pia</c> data tree, so a successful write actually
/// exercises the runs carve-out. That tree is a REDIRECTED profile rather than the developer's own — the guard
/// rebuilds its roots now, so nothing here has to reach into the real one to be inside it.</summary>
[Collection("PiaPathsStatic")]
public sealed class FilesToolHandlerRunsDirGuardTests : IClassFixture<RedirectedProfileFixture>, IDisposable
{
    private readonly string _runDir;
    private readonly FilesToolHandler _handler;

    public FilesToolHandlerRunsDirGuardTests(RedirectedProfileFixture profile)
    {
        _ = profile;
        // Guid-shaped so the startup sweep reclaims a leaked fixture instead of leaving it in the runs folder.
        _runDir = Path.Combine(AssistantWorkspace.RunsRoot, Guid.NewGuid().ToString());
        Directory.CreateDirectory(_runDir);

        // The settings folder is deliberately unrelated: the ambient TaskContext.WorkspaceRoot below wins, so
        // the run root — not a settings-side carve-out — is what makes the write succeed.
        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings { AssistantFilesFolder = Path.GetTempPath() });
        _handler = new FilesToolHandler(settings, new FileStalenessStore(), NullLogger<FilesToolHandler>.Instance);

        TaskAmbient.Current = new TaskContext(Guid.NewGuid(), WorkingSubpath: null, OnFileTouched: null, WorkspaceRoot: _runDir);
    }

    public void Dispose()
    {
        TaskAmbient.Current = null;
        TempPath.Remove(_runDir);
    }

    private static T Prop<T>(object obj, string name)
    {
        var p = obj.GetType().GetProperty(name);
        Assert.NotNull(p);
        return (T)p!.GetValue(obj)!;
    }

    /// <summary>Without the <c>runs</c> carve-out in <c>SensitivePathGuard</c> this fails: <c>%LOCALAPPDATA%\Pia</c> is blocked wholesale.</summary>
    [Fact]
    public async Task AWriteInsideARealRunsWorkspace_Succeeds()
    {
        var call = new FunctionCallContent("w1", "write_file",
            new Dictionary<string, object?> { ["path"] = "out.md", ["content"] = "hi" });
        var (result, pending) = await _handler.HandleToolCallAsync(call, TestContext.Current.CancellationToken);

        Assert.Null(result);
        Assert.NotNull(pending);
        var executed = await pending!.Execute();

        Assert.True(Prop<bool>(executed!, "success"));
        var full = Path.Combine(_runDir, "out.md");
        Assert.True(File.Exists(full));
        Assert.Contains("hi", File.ReadAllText(full));
    }

    /// <summary>Companion to the write above, which is its control: an escape assertion also passes against a root nothing can write to at all.</summary>
    [Fact]
    public async Task EscapeVector_IsStillRejected_InsideARealRunsWorkspace()
    {
        var call = new FunctionCallContent("w2", "write_file",
            new Dictionary<string, object?> { ["path"] = "../secret.txt", ["content"] = "pwned" });
        var (result, pending) = await _handler.HandleToolCallAsync(call, TestContext.Current.CancellationToken);

        Assert.Null(pending);
        Assert.NotNull(result);
        Assert.False(Prop<bool>(result!, "success"));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_runDir)!, "secret.txt")));
    }
}
