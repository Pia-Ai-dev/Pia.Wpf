using System.IO;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>A deferred write captures the effective root at PREPARE time, so an ambient change after the approval await cannot redirect where the file lands.</summary>
public class FilesToolHandlerWorkingDirectoryTests : IDisposable
{
    private readonly string _root;
    private readonly FilesToolHandler _handler;

    public FilesToolHandlerWorkingDirectoryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "pia-wd-resolve-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings { AssistantFilesFolder = _root });

        _handler = new FilesToolHandler(settings, new FileStalenessStore(), NullLogger<FilesToolHandler>.Instance);
    }

    public void Dispose()
    {
        TaskAmbient.Current = null;
        TempPath.Remove(_root);
    }

    [Fact]
    public async Task ReadFile_WithWorkingSubpath_ResolvesUnderNarrowedRoot()
    {
        var sub = Path.Combine(_root, "sub");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "note.txt"), "hello\n");

        TaskAmbient.Current = new TaskContext(Guid.NewGuid(), "sub");

        var call = new FunctionCallContent("c1", "read_file",
            new Dictionary<string, object?> { ["path"] = "note.txt" });
        var (result, _) = await _handler.HandleToolCallAsync(call, TestContext.Current.CancellationToken);

        Assert.Contains("hello", (string)result!);
    }

    [Fact]
    public async Task DeferredWrite_UsesPrepareTimeEffectiveRoot_NotExecuteTimeAmbient()
    {
        var sub = Path.Combine(_root, "sub");
        Directory.CreateDirectory(sub);

        // PREPARE the write while the ambient working subpath is "sub".
        TaskAmbient.Current = new TaskContext(Guid.NewGuid(), "sub");
        var prepareCall = new FunctionCallContent("c1", "write_file",
            new Dictionary<string, object?> { ["path"] = "deferred.txt", ["content"] = "x" });
        var (prepResult, pending) = await _handler.HandleToolCallAsync(prepareCall, TestContext.Current.CancellationToken);
        Assert.Null(prepResult);
        Assert.NotNull(pending);

        // Change the ambient AFTER prepare (simulating the post-approval window where ambient
        // flow is not guaranteed). The deferred closure must ignore this.
        TaskAmbient.Current = new TaskContext(Guid.NewGuid(), null);

        var execResult = await pending!.Execute();
        Assert.True(Prop<bool>(execResult!, "success"));

        // The file landed under the prepare-time effective root (_root/sub), NOT the base root.
        Assert.True(File.Exists(Path.Combine(sub, "deferred.txt")));
        Assert.False(File.Exists(Path.Combine(_root, "deferred.txt")));
    }

    [Fact]
    public async Task WorkspaceRoot_RedirectsListReadWrite_UnderRunRoot_NotInteractiveFolder()
    {
        // The ambient WorkspaceRoot must win over _currentFolder.
        var runRoot = Path.Combine(Path.GetTempPath(), "pia-runroot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runRoot);
        try
        {
            TaskAmbient.Current = new TaskContext(Guid.NewGuid(), WorkingSubpath: null, OnFileTouched: null, WorkspaceRoot: runRoot);

            // write_file lands under the run root, not the interactive _root.
            var write = new FunctionCallContent("c1", "write_file",
                new Dictionary<string, object?> { ["path"] = "note.txt", ["content"] = "hello" });
            var (_, pending) = await _handler.HandleToolCallAsync(write, TestContext.Current.CancellationToken);
            Assert.NotNull(pending);
            var execResult = await pending!.Execute();
            Assert.True(Prop<bool>(execResult!, "success"));
            Assert.True(File.Exists(Path.Combine(runRoot, "note.txt")));
            Assert.False(File.Exists(Path.Combine(_root, "note.txt")));

            // read_file resolves under the run root.
            var read = new FunctionCallContent("c2", "read_file",
                new Dictionary<string, object?> { ["path"] = "note.txt" });
            var (readResult, _) = await _handler.HandleToolCallAsync(read, TestContext.Current.CancellationToken);
            Assert.Contains("hello", (string)readResult!);

            // list_files enumerates the run root.
            var list = new FunctionCallContent("c3", "list_files", new Dictionary<string, object?>());
            var (listResult, _) = await _handler.HandleToolCallAsync(list, TestContext.Current.CancellationToken);
            Assert.Contains("note.txt", (string)listResult!);
        }
        finally
        {
            TaskAmbient.Current = null;
            TempPath.Remove(runRoot);
        }
    }

    private static T Prop<T>(object obj, string name)
    {
        var p = obj.GetType().GetProperty(name);
        Assert.NotNull(p);
        return (T)p!.GetValue(obj)!;
    }
}
