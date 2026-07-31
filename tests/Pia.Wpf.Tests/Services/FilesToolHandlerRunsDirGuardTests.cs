using System.IO;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// The R1 fact (plan §4 R1 / this batch's §0.2a), proved at the REAL shape rather than under
/// <c>Path.GetTempPath()</c>: <see cref="FilesToolHandlerWorkspaceEscapeTests"/> roots its fixture outside
/// every blocked root, so it structurally cannot see the guard collision Batch 06 B1 exists to fix. This
/// class roots at <c>AssistantWorkspace.RunsRoot\&lt;guid&gt;</c> — inside the real, guard-checked
/// <c>%LOCALAPPDATA%\Pia</c> tree — and asserts a SUCCESSFUL write, not only that an escape is rejected.
/// </summary>
public sealed class FilesToolHandlerRunsDirGuardTests : IDisposable
{
    private readonly string _runDir;
    private readonly FilesToolHandler _handler;

    public FilesToolHandlerRunsDirGuardTests()
    {
        // Guid-shaped name (R11): RunStartupSweepAsync `continue`s on any directory name that is not a
        // parseable Guid, so a leaked fixture with this shape is swept as `run is null` on next app
        // start rather than living in the developer's real runs folder forever.
        _runDir = Path.Combine(AssistantWorkspace.RunsRoot, Guid.NewGuid().ToString());
        Directory.CreateDirectory(_runDir);

        // The base root the settings folder would resolve to is irrelevant here on purpose: the ambient
        // TaskContext.WorkspaceRoot below is what FilesToolHandler's dispatch point (R1) prefers, so an
        // unrelated settings folder proves the run root — not some carve-out on the settings side — is
        // what makes the write succeed.
        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings { AssistantFilesFolder = Path.GetTempPath() });
        _handler = new FilesToolHandler(settings, new FileStalenessStore(), NullLogger<FilesToolHandler>.Instance);

        TaskAmbient.Current = new TaskContext(Guid.NewGuid(), WorkingSubpath: null, OnFileTouched: null, WorkspaceRoot: _runDir);
    }

    public void Dispose()
    {
        TaskAmbient.Current = null;
        try { Directory.Delete(_runDir, recursive: true); } catch { /* best effort */ }
    }

    private static T Prop<T>(object obj, string name)
    {
        var p = obj.GetType().GetProperty(name);
        Assert.NotNull(p);
        return (T)p!.GetValue(obj)!;
    }

    /// <summary>
    /// <b>REGRESSION</b>, and the fact carrying R1's whole point — labelled here rather than only in the spec's
    /// table, as 06 §9's preamble requires. Neutralization: revert the <c>runs</c> entry in
    /// <c>SensitivePathGuard.BuildAllowedExceptions</c> → the write comes back as
    /// <c>WriteResult.Failed</c> naming a "protected system or application data directory", because
    /// <c>%LOCALAPPDATA%\Pia</c> is blocked wholesale and containment passes before the denylist runs.
    /// </summary>
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

    /// <summary>
    /// <b>GUARD</b>, not a regression, and the distinction matters when this pair is next debugged: containment
    /// is unchanged by G1/G2, so reverting the carve-out leaves this fact GREEN. It is the companion that stops
    /// the carve-out from being read as "the runs tree is unguarded" — but it cannot itself demonstrate the
    /// carve-out, because an escape assertion also passes against a root nothing can write to at all. The fact
    /// above is the control.
    /// </summary>
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
