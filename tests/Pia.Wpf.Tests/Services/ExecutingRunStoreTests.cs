using Pia.Services;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// A2's launch-bracket index. NEW SURFACE, so none of this can fail on the pre-patch tree — it pins the
/// contract the rest of the fix leans on: release is idempotent and keyed on the RUN id alone (the
/// RunChanged handler has no chat id), and a chat stays gated while any run on it is still bracketed.
/// </summary>
public class ExecutingRunStoreTests
{
    [Fact]
    public void Release_IsIdempotent_SoTheRunChangedHandlerAndTheLauncherFinallyCanBothCallIt()
    {
        // Both sides release, and RunChanged fires BEFORE the launcher's finally, so the second call must be
        // a harmless no-op rather than throwing into a finally block or resurrecting anything.
        var chatId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var sut = new ExecutingRunStore();

        sut.Register(chatId, runId);
        Assert.True(sut.IsExecuting(chatId));

        sut.Release(runId);
        sut.Release(runId);
        sut.Release(Guid.NewGuid()); // an unknown run id is a no-op too

        Assert.False(sut.IsExecuting(chatId));
        Assert.Null(sut.GetChatId(runId));
    }

    [Fact]
    public void GetChatId_ReverseLooksUpTheChat_BecauseRunChangedCarriesNone()
    {
        var chatId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var sut = new ExecutingRunStore();

        Assert.Null(sut.GetChatId(runId));

        sut.Register(chatId, runId);
        Assert.Equal<Guid?>(chatId, sut.GetChatId(runId));

        sut.Release(runId);
        Assert.Null(sut.GetChatId(runId));
    }

    [Fact]
    public void IsExecuting_StaysTrue_UntilTheLastRunOnTheChatIsReleased()
    {
        // Two writers on one chat (a resume dispatch overlapping the previous one's unwind, or a scheduled
        // single turn plus a resumed plan). Releasing one must not un-gate the composer for the other.
        var chatId = Guid.NewGuid();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var sut = new ExecutingRunStore();

        sut.Register(chatId, first);
        sut.Register(chatId, second);

        sut.Release(first);
        Assert.True(sut.IsExecuting(chatId));

        sut.Release(second);
        Assert.False(sut.IsExecuting(chatId));
    }

    [Fact]
    public void IsExecuting_IsScopedToItsOwnChat()
    {
        var sut = new ExecutingRunStore();
        var mine = Guid.NewGuid();
        var other = Guid.NewGuid();

        sut.Register(mine, Guid.NewGuid());

        Assert.True(sut.IsExecuting(mine));
        Assert.False(sut.IsExecuting(other));
    }
}
