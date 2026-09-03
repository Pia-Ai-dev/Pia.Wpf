using System.Net.Http;
using System.Net.Sockets;
using Pia.Helpers;
using Xunit;

namespace Pia.Tests.Helpers;

/// <summary>
/// Only a genuine "cannot reach the network" may take the offline arm — a service that answered, or
/// a turn the user cancelled, has to keep its own message.
/// </summary>
public sealed class NetworkFailureTests
{
    [Theory]
    [InlineData(HttpRequestError.NameResolutionError)]
    [InlineData(HttpRequestError.ConnectionError)]
    [InlineData(HttpRequestError.SecureConnectionError)]
    public void AnUnreachableHostIsOffline(HttpRequestError error) =>
        Assert.True(NetworkFailure.IsOffline(new HttpRequestException(error)));

    [Fact]
    public void AWrappedSocketFailureIsOffline() =>
        Assert.True(NetworkFailure.IsOffline(
            new InvalidOperationException("chat failed", new SocketException((int)SocketError.HostNotFound))));

    /// <summary>A local provider that is simply not running — the sentence has to fit this too.</summary>
    [Fact]
    public void ALocalProviderThatIsNotListeningIsUnreachable() =>
        Assert.True(NetworkFailure.IsOffline(
            new HttpRequestException(HttpRequestError.ConnectionError, "localhost:11434")));

    /// <summary>A slow-but-alive server is reachable, and keeps its own message.</summary>
    [Fact]
    public void ATimeoutIsNotUnreachable() =>
        Assert.False(NetworkFailure.IsOffline(
            new InvalidOperationException("slow", new SocketException((int)SocketError.TimedOut))));

    [Fact]
    public void AServerThatAnsweredIsNotOffline() =>
        Assert.False(NetworkFailure.IsOffline(
            new HttpRequestException(HttpRequestError.Unknown, "PiaCloud optimization failed (400): …")));

    [Theory]
    [InlineData(null)]
    public void NothingIsNotOffline(Exception? none) => Assert.False(NetworkFailure.IsOffline(none));

    [Fact]
    public void ACancelledTurnIsNotOffline() =>
        Assert.False(NetworkFailure.IsOffline(new OperationCanceledException()));
}
