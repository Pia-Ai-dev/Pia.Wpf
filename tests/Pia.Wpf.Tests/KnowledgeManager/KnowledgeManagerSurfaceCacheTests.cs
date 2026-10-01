using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Pia.Services.KnowledgeManager;
using Pia.Shared.Knowledge;
using Xunit;

namespace Pia.Tests.KnowledgeManager;

public sealed class KnowledgeManagerSurfaceCacheTests
{
    private readonly IKnowledgeManagerApiClient _api = Substitute.For<IKnowledgeManagerApiClient>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private KnowledgeManagerSurfaceCache CreateSut() => new(_api, NullLogger<KnowledgeManagerSurfaceCache>.Instance);

    private void Answer(KbManagerCallStatus status, IReadOnlyList<KbManagerKnowledgeBase>? rows = null) =>
        _api.ListKnowledgeBasesAsync(Arg.Any<CancellationToken>())
            .Returns(new KbManagerResult<IReadOnlyList<KbManagerKnowledgeBase>>(status, rows));

    [Fact]
    public void BeforeTheFirstProbe_TheSurfaceIsHidden()
    {
        Assert.False(CreateSut().IsAvailable);
    }

    [Fact]
    public async Task A200_MakesItAvailable_EvenWithNoKnowledgeBases()
    {
        Answer(KbManagerCallStatus.Ok, []);
        var sut = CreateSut();

        Assert.True(await sut.RefreshAsync(Ct));
        Assert.True(sut.IsAvailable);
    }

    [Theory]
    [InlineData(KbManagerCallStatus.Forbidden)]
    [InlineData(KbManagerCallStatus.NotFound)]
    [InlineData(KbManagerCallStatus.Unavailable)]
    [InlineData(KbManagerCallStatus.NotConnected)]
    public async Task EveryOtherAnswer_HidesIt(KbManagerCallStatus status)
    {
        Answer(status);

        Assert.False(await CreateSut().RefreshAsync(Ct));
    }

    [Fact]
    public async Task AThrowingProbe_HidesIt()
    {
        _api.ListKnowledgeBasesAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("boom"));

        Assert.False(await CreateSut().RefreshAsync(Ct));
    }

    [Fact]
    public async Task Changed_FiresOnAFlipOnly()
    {
        Answer(KbManagerCallStatus.Ok, []);
        var sut = CreateSut();
        var fired = 0;
        sut.Changed += (_, _) => fired++;

        await sut.RefreshAsync(Ct);
        await sut.RefreshAsync(Ct);

        Assert.Equal(1, fired);
    }

    [Fact]
    public async Task Hide_TurnsAnAvailableSurfaceOffAndSaysSo()
    {
        Answer(KbManagerCallStatus.Ok, []);
        var sut = CreateSut();
        await sut.RefreshAsync(Ct);
        var fired = 0;
        sut.Changed += (_, _) => fired++;

        sut.Hide();

        Assert.False(sut.IsAvailable);
        Assert.Equal(1, fired);
    }

    [Fact]
    public async Task AProbeInFlightDuringHide_DoesNotShowTheSurfaceAgain()
    {
        Answer(KbManagerCallStatus.Ok, []);
        var sut = CreateSut();
        await sut.RefreshAsync(Ct);
        var pending = PendingProbe();

        var refresh = sut.RefreshAsync(Ct);
        sut.Hide();
        pending.SetResult(new(KbManagerCallStatus.Ok, []));

        Assert.False(await refresh);
        Assert.False(sut.IsAvailable);
    }

    [Fact]
    public async Task AnOlderProbeLandingLast_DoesNotOverrideANewerOne()
    {
        var older = PendingProbe();
        var sut = CreateSut();
        var first = sut.RefreshAsync(Ct);
        Answer(KbManagerCallStatus.Forbidden);

        Assert.False(await sut.RefreshAsync(Ct));
        older.SetResult(new(KbManagerCallStatus.Ok, []));

        Assert.False(await first);
        Assert.False(sut.IsAvailable);
    }

    private TaskCompletionSource<KbManagerResult<IReadOnlyList<KbManagerKnowledgeBase>>> PendingProbe()
    {
        var pending = new TaskCompletionSource<KbManagerResult<IReadOnlyList<KbManagerKnowledgeBase>>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _api.ListKnowledgeBasesAsync(Arg.Any<CancellationToken>()).Returns(pending.Task);
        return pending;
    }
}
