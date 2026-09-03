namespace Pia.Services.Exceptions;

/// <summary>An upstream failure Pia.Server relayed inside an open 200 stream, with the server's own message.
/// Deliberately not an HttpRequestException: a status-shaped one would trip the tool-less retry.</summary>
public sealed class PiaCloudStreamException : InvalidOperationException
{
    /// <summary>The server's error title (<c>"Bad Gateway"</c>, <c>"Upstream Error"</c>, …).</summary>
    public string Title { get; }

    public PiaCloudStreamException(string title, string? message)
        : base(string.IsNullOrWhiteSpace(message) ? title : message)
    {
        Title = title;
    }
}
