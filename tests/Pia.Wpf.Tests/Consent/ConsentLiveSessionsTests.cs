using Microsoft.Extensions.Logging.Abstractions;
using Pia.Services.Consent;
using Xunit;

namespace Pia.Tests.Consent;

public sealed class ConsentLiveSessionsTests
{
    private const string SessionA = "3f2a9c1e7b4d4e0f8a6b5c4d3e2f1a0b";
    private const string SessionB = "9b1c2d3e4f5a6b7c8d9e0f1a2b3c4d5e";

    private readonly ConsentLiveSessions _sut = new(NullLogger<ConsentLiveSessions>.Instance);

    [Fact]
    public void Snapshot_HoldsTheRegisteredSessions_UntilTheyAreUnregistered()
    {
        _sut.Register(SessionA);
        _sut.Register(SessionB);
        _sut.Unregister(SessionA);

        Assert.Equal([SessionB], _sut.Snapshot());
    }

    [Fact]
    public void Unregister_AnnouncesTheEndOnce()
    {
        var ended = new List<string>();
        _sut.SessionEnded += (_, id) => ended.Add(id);
        _sut.Register(SessionA);

        _sut.Unregister(SessionA);
        _sut.Unregister(SessionA);
        _sut.Unregister(SessionB);

        Assert.Equal([SessionA], ended);
    }

    [Fact]
    public void ASubscriberThatThrows_DoesNotKeepTheNextFromHearingTheEnd()
    {
        var heard = false;
        _sut.SessionEnded += (_, _) => throw new InvalidOperationException("boom");
        _sut.SessionEnded += (_, _) => heard = true;
        _sut.Register(SessionA);

        _sut.Unregister(SessionA);

        Assert.True(heard);
        Assert.Empty(_sut.Snapshot());
    }
}
