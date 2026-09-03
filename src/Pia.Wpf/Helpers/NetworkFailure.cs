using System.Net.Http;
using System.Net.Sockets;

namespace Pia.Helpers;

/// <summary>
/// Tells "this machine cannot reach the network" apart from "the service answered with a problem",
/// so the first can be said in the user's language instead of surfacing a socket message.
/// </summary>
public static class NetworkFailure
{
    public static bool IsOffline(Exception? exception)
    {
        for (var ex = exception; ex is not null; ex = ex.InnerException)
        {
            if (ex is HttpRequestException http
                && http.HttpRequestError is HttpRequestError.NameResolutionError
                    or HttpRequestError.ConnectionError
                    or HttpRequestError.SecureConnectionError)
            {
                return true;
            }

            // A raw SocketException also reaches us through handlers that wrap without setting
            // HttpRequestError.
            if (ex is SocketException socket && IsUnreachable(socket.SocketErrorCode))
                return true;
        }

        return false;
    }

    private static bool IsUnreachable(SocketError error) => error
        is SocketError.HostNotFound
        or SocketError.TryAgain
        or SocketError.NoData
        or SocketError.NetworkDown
        or SocketError.NetworkUnreachable
        or SocketError.HostUnreachable
        or SocketError.ConnectionRefused
        or SocketError.TimedOut;
}
