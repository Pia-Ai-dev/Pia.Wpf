namespace Pia.Services.Exceptions;

/// <summary>
/// Thrown when the meeting browser fails to launch (as opposed to failing later in the join flow, e.g.
/// never being admitted). The orchestrator uses this to distinguish a launch failure — for which a
/// system/branded browser (Chrome/Edge channel) can degrade to bundled Chromium — from a genuine join
/// failure, which it must not retry.
/// </summary>
public sealed class BrowserLaunchException : Exception
{
    public BrowserLaunchException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
