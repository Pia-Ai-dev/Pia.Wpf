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
