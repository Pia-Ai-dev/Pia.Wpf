using Pia.Services.Screen;

namespace Pia.Services.Interfaces;

public interface IScreenCaptureIndicator
{
    /// <summary>Tells the user a capture happened. Only an unattended capture publishes; every other surface is
    /// a no-op, so a caller cannot forget the distinction.</summary>
    void NotifyCapture(ScreenCaptureAuditEvent evt);
}
