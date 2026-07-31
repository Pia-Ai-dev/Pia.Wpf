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
/// G-1 fuzz: with an unattended run's isolated <see cref="TaskContext.WorkspaceRoot"/> active, every
/// path the model can supply — traversal (<c>..</c>), absolute POSIX/Windows paths, and reparse-point
/// (symlink) escapes — must reject against the PER-RUN root, never the interactive folder. A rejected
/// write/delete never touches the filesystem outside the run workspace; a rejected read returns no
/// outside content. Mirrors the SafeFolderPath containment contract, re-anchored to runs\&lt;runId&gt;.
/// <para>
/// The run root is deliberately at the REAL shape — a Guid-named directory under
/// <see cref="AssistantWorkspace.RunsRoot"/> — because <c>SensitivePathGuard.BuildBlockedRoots</c> reads
/// the real <c>LOCALAPPDATA</c>, so a <c>Path.GetTempPath()</c> fixture sits outside every blocked root
/// and this suite structurally could not see the guard that now applies to a run's every file operation
/// (plan §4 R1). Batch 06 G2 made that the production shape, so the escape matrix now runs where the
/// guard actually applies. All of these are <b>GUARD</b> facts: they cannot go red on a revert of G2 —
/// containment is unchanged by it. <see cref="Write_InsideTheRunRoot_Succeeds"/> can, and is the control
/// that stops the whole matrix passing vacuously against an un-writable root.
/// </para>
/// </summary>
public class FilesToolHandlerWorkspaceEscapeTests : IDisposable
{
    private readonly string _interactiveRoot;
    private readonly string _runRoot;
    private readonly string _outside;
    private readonly FilesToolHandler _handler;

    public FilesToolHandlerWorkspaceEscapeTests()
    {
        _interactiveRoot = NewDir("pia-escape-interactive-");
        // The run root, and ONLY the run root, moves under the real runs dir: _interactiveRoot and _outside
        // stay under GetTempPath() so "outside" really is outside the carve-out. The name is a bare Guid
        // (R11) — RunStartupSweepAsync `continue`s on any directory name that is not a parseable Guid, so a
        // prefixed fixture that leaked would live in the developer's real runs folder forever, while a
        // Guid-named one is swept as `run is null` on the next app start.
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

    /// <summary>
    /// Reads a member off one of FilesToolHandler's private result records (WriteResult and friends are
    /// <c>private sealed record</c>s, so a test cannot name the type). Same helper as
    /// <c>FilesToolHandlerWriteTests.Prop</c>; the positional record parameters are lower-cased, so the
    /// member names here are the wire names (<c>success</c>, <c>error</c>).
    /// </summary>
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
            try { Directory.Delete(d, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>
    /// The positive control for this whole class, and the one fact here that is not a GUARD: every escape
    /// assertion below would pass vacuously against a run root the handler cannot write to at all (a
    /// broken guard carve-out looks exactly like a rejected escape from the outside). Proves the fixture
    /// root is genuinely usable at the real shape before anything claims a rejection means something.
    /// </summary>
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

        // Escape → structured failure, no pending write to approve.
        //
        // NOT a string. write_file's prepare-time hard failures return the private WriteResult record
        // (FilesToolHandler.WriteResult.Failed), so the cast this assertion used to do — (string)result —
        // threw InvalidCastException and took all five theory cases down BEFORE any containment claim was
        // checked, including the "nothing written outside" check below. Read the record's members by
        // reflection, as FilesToolHandlerWriteTests does, and assert the failure rather than merely that
        // the call returned something. (delete_file below still returns a plain string, which is why only
        // the write vector crashed.)
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
        // A reparse point planted inside the run root that targets a sibling outside it must not
        // become a sandbox hole: canonicalization resolves the link and containment rejects it.
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
