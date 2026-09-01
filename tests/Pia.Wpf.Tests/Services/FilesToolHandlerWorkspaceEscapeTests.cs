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

/// <summary>
/// The run root sits at its real shape under <see cref="AssistantWorkspace.RunsRoot"/>: a
/// <c>GetTempPath()</c> fixture falls outside every <c>SensitivePathGuard</c> blocked root. That shape is taken
/// inside a REDIRECTED profile, not the developer's own — the guard rebuilds its roots when they move.
/// </summary>
[Collection("PiaPathsStatic")]
public class FilesToolHandlerWorkspaceEscapeTests : IClassFixture<RedirectedProfileFixture>, IDisposable
{
    private readonly string _interactiveRoot;
    private readonly string _runRoot;
    private readonly string _outside;
    private readonly FilesToolHandler _handler;

    public FilesToolHandlerWorkspaceEscapeTests(RedirectedProfileFixture profile)
    {
        _ = profile;
        _interactiveRoot = NewDir("pia-escape-interactive-");
        // Bare Guid name: RunStartupSweepAsync skips any directory name that is not a parseable Guid, so a
        // prefixed fixture that leaked would live in the runs folder forever.
        _runRoot = Path.Combine(AssistantWorkspace.RunsRoot, Guid.NewGuid().ToString());
        Directory.CreateDirectory(_runRoot);
        _outside = NewDir("pia-escape-outside-");
        File.WriteAllText(Path.Combine(_outside, "secret.txt"), "TOP SECRET");

        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings { AssistantFilesFolder = _interactiveRoot });
        _handler = new FilesToolHandler(settings, new FileStalenessStore(), NullLogger<FilesToolHandler>.Instance);

        // Activate the per-run workspace isolation for every case below.
        TaskAmbient.Current = new TaskContext(Guid.NewGuid(), WorkingSubpath: null, OnFileTouched: null, WorkspaceRoot: _runRoot);
    }

    /// <summary>FilesToolHandler's result records are private, so members are read by their wire names (<c>success</c>, <c>error</c>).</summary>
    private static T Prop<T>(object obj, string name)
    {
        var p = obj.GetType().GetProperty(name);
        Assert.NotNull(p);
        return (T)p!.GetValue(obj)!;
    }

    public void Dispose()
    {
        TaskAmbient.Current = null;
        foreach (var d in new[] { _interactiveRoot, _runRoot, _outside })
            TempPath.Remove(d);
    }

    /// <summary>Non-vacuity control: every escape assertion below would pass against a run root the handler cannot write to at all.</summary>
    [Fact]
    public async Task Write_InsideTheRunRoot_Succeeds()
    {
        var call = new FunctionCallContent("ok", "write_file",
            new Dictionary<string, object?> { ["path"] = "deliverable.md", ["content"] = "real work" });
        var (result, pending) = await _handler.HandleToolCallAsync(call, TestContext.Current.CancellationToken);

        Assert.Null(result);
        Assert.NotNull(pending);
        var executed = await pending!.Execute();
        Assert.True(Prop<bool>(executed!, "success"));

        var full = Path.Combine(_runRoot, "deliverable.md");
        Assert.True(File.Exists(full));
        Assert.Contains("real work", File.ReadAllText(full));

        // …and it landed in the RUN root, not the interactive folder.
        Assert.False(File.Exists(Path.Combine(_interactiveRoot, "deliverable.md")));
    }

    [Theory]
    [InlineData("../secret.txt")]
    [InlineData("../../secret.txt")]
    [InlineData("..\\..\\secret.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\Windows\\System32\\drivers\\etc\\hosts")]
    public async Task Write_EscapeVector_IsRejected_AndNothingWrittenOutside(string vector)
    {
        var call = new FunctionCallContent("w", "write_file",
            new Dictionary<string, object?> { ["path"] = vector, ["content"] = "pwned" });
        var (result, pending) = await _handler.HandleToolCallAsync(call, TestContext.Current.CancellationToken);

        // write_file's prepare-time hard failures return the private WriteResult record, not a string, so
        // the members are read by reflection (delete_file below still returns a plain string).
        Assert.Null(pending);
        Assert.NotNull(result);
        Assert.False(Prop<bool>(result!, "success"));
        Assert.Contains("Error", Prop<string?>(result!, "error")!, StringComparison.OrdinalIgnoreCase);

        // The out-of-run "secret.txt" is untouched; no stray file appeared outside the run root.
        Assert.Equal("TOP SECRET", File.ReadAllText(Path.Combine(_outside, "secret.txt")));
    }

    [Theory]
    [InlineData("../secret.txt")]
    [InlineData("../../secret.txt")]
    [InlineData("/etc/passwd")]
    public async Task Delete_EscapeVector_IsRejected(string vector)
    {
        var call = new FunctionCallContent("d", "delete_file",
            new Dictionary<string, object?> { ["path"] = vector });
        var (result, pending) = await _handler.HandleToolCallAsync(call, TestContext.Current.CancellationToken);

        Assert.Null(pending);
        Assert.NotNull(result);
        Assert.Equal("TOP SECRET", File.ReadAllText(Path.Combine(_outside, "secret.txt")));
    }

    [Theory]
    [InlineData("../secret.txt")]
    [InlineData("/etc/passwd")]
    public async Task Read_EscapeVector_DoesNotReturnOutsideContent(string vector)
    {
        var call = new FunctionCallContent("r", "read_file",
            new Dictionary<string, object?> { ["path"] = vector });
        var (result, _) = await _handler.HandleToolCallAsync(call, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.DoesNotContain("TOP SECRET", (string)result!);
    }

    [Fact]
    public async Task Symlink_InsideRunRoot_PointingOutside_IsRejectedOnWrite()
    {
        // Canonicalization must resolve a reparse point planted inside the run root that targets a sibling outside.
        var linkPath = Path.Combine(_runRoot, "escape-link");
        try { Directory.CreateSymbolicLink(linkPath, _outside); }
        catch { return; /* privilege/platform unavailable — skip (cannot create the reparse point) */ }

        var call = new FunctionCallContent("s", "write_file",
            new Dictionary<string, object?> { ["path"] = "escape-link/secret.txt", ["content"] = "pwned" });
        var (result, pending) = await _handler.HandleToolCallAsync(call, TestContext.Current.CancellationToken);

        Assert.Null(pending);
        Assert.NotNull(result);
        Assert.Equal("TOP SECRET", File.ReadAllText(Path.Combine(_outside, "secret.txt")));
    }

    private static string NewDir(string prefix)
    {
        var d = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }
}
