using Pia.Services;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// Gates the ambient turn-context flow that lets tool handlers (FilesToolHandler) key
/// per-task state and resolve the effective working root for the in-flight turn with no
/// parameter plumbing. <see cref="TaskContext.TaskId"/> is <c>Guid?</c> because
/// <c>ChatSession.Id</c> is nullable — direct test callers that bypass the manager's
/// SetIdentity run a turn with a null Id, which must not throw.
/// </summary>
public class TaskAmbientTests
{
    [Fact]
    public void NullContext_SetAndRestore_DoesNotThrow_AndRoundTrips()
    {
        // Mirror RunTurnAsync's set/restore with a null context (the direct-test-caller case).
        var previous = TaskAmbient.Current;
        TaskAmbient.Current = null;
        Assert.Null(TaskAmbient.Current);
        TaskAmbient.Current = previous;
        Assert.Equal(previous, TaskAmbient.Current);
    }

    [Fact]
    public void Current_RoundTripsContext_AndRestoresPrevious()
    {
        var previous = TaskAmbient.Current;
        var id = Guid.NewGuid();

        TaskAmbient.Current = new TaskContext(id, "projects/app");
        Assert.Equal(id, TaskAmbient.Current?.TaskId);
        Assert.Equal("projects/app", TaskAmbient.Current?.WorkingSubpath);

        TaskAmbient.Current = previous;
        Assert.Equal(previous, TaskAmbient.Current);
    }

    [Fact]
    public void NullTaskId_WithWorkingSubpath_IsCarried()
    {
        var previous = TaskAmbient.Current;

        TaskAmbient.Current = new TaskContext(null, "sub");
        Assert.Null(TaskAmbient.Current?.TaskId);
        Assert.Equal("sub", TaskAmbient.Current?.WorkingSubpath);

        TaskAmbient.Current = previous;
    }

    [Fact]
    public async Task ConcurrentAsyncFlows_DoNotBleed()
    {
        // Each logical async flow carries its own ExecutionContext, so an ambient set
        // inside one Task.Run is invisible to the other — the property that keeps
        // interleaved background turns from sharing a context.
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        var observedA = Guid.Empty;
        var observedB = Guid.Empty;
        string? subA = null;
        string? subB = null;

        var taskA = Task.Run(async () =>
        {
            TaskAmbient.Current = new TaskContext(a, "dir-a");
            await Task.Yield();
            await Task.Delay(10);
            observedA = TaskAmbient.Current?.TaskId ?? Guid.Empty;
            subA = TaskAmbient.Current?.WorkingSubpath;
        });

        var taskB = Task.Run(async () =>
        {
            TaskAmbient.Current = new TaskContext(b, "dir-b");
            await Task.Yield();
            await Task.Delay(10);
            observedB = TaskAmbient.Current?.TaskId ?? Guid.Empty;
            subB = TaskAmbient.Current?.WorkingSubpath;
        });

        await Task.WhenAll(taskA, taskB);

        Assert.Equal(a, observedA);
        Assert.Equal(b, observedB);
        Assert.Equal("dir-a", subA);
        Assert.Equal("dir-b", subB);
    }
}
