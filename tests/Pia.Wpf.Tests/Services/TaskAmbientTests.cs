using Pia.Services;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// Gates the ambient task-id flow that lets tool handlers (FilesToolHandler) key
/// per-task state for the in-flight turn with no parameter plumbing. The payload is
/// <c>Guid?</c> because <c>ChatSession.Id</c> is nullable — direct test callers that
/// bypass the manager's SetIdentity run a turn with a null Id, which must not throw.
/// </summary>
public class TaskAmbientTests
{
    [Fact]
    public void NullId_SetAndRestore_DoesNotThrow_AndRoundTrips()
    {
        // Mirror RunTurnAsync's set/restore with a null Id (the direct-test-caller case).
        var previous = TaskAmbient.Current;
        TaskAmbient.Current = null;
        Assert.Null(TaskAmbient.Current);
        TaskAmbient.Current = previous;
        Assert.Equal(previous, TaskAmbient.Current);
    }

    [Fact]
    public void Current_RoundTripsAGuid_AndRestoresPrevious()
    {
        var previous = TaskAmbient.Current;
        var id = Guid.NewGuid();

        TaskAmbient.Current = id;
        Assert.Equal(id, TaskAmbient.Current);

        TaskAmbient.Current = previous;
        Assert.Equal(previous, TaskAmbient.Current);
    }

    [Fact]
    public async Task ConcurrentAsyncFlows_DoNotBleed()
    {
        // Each logical async flow carries its own ExecutionContext, so an ambient set
        // inside one Task.Run is invisible to the other — the property that keeps
        // interleaved background turns from sharing a task id.
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        var observedA = Guid.Empty;
        var observedB = Guid.Empty;

        var taskA = Task.Run(async () =>
        {
            TaskAmbient.Current = a;
            await Task.Yield();
            await Task.Delay(10);
            observedA = TaskAmbient.Current ?? Guid.Empty;
        });

        var taskB = Task.Run(async () =>
        {
            TaskAmbient.Current = b;
            await Task.Yield();
            await Task.Delay(10);
            observedB = TaskAmbient.Current ?? Guid.Empty;
        });

        await Task.WhenAll(taskA, taskB);

        Assert.Equal(a, observedA);
        Assert.Equal(b, observedB);
    }
}
