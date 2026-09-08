using Pia.Services.Screen;

namespace Pia.Services.Interfaces;

/// <summary>The durable record that a capture happened. Metadata only — the frame never reaches it, and the
/// window title reaches it only as a hash.</summary>
public interface IScreenCaptureAuditLog
{
    /// <summary>Fire-and-forget: never throws, never blocks; a full queue drops with a logged warning.</summary>
    void Record(ScreenCaptureAuditEvent evt);
}
