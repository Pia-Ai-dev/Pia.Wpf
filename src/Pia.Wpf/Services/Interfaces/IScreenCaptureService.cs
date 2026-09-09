using Pia.Services.Screen;

namespace Pia.Services.Interfaces;

/// <summary>Both calls hop off the caller's thread and hand back frozen, immutable results; never block the UI
/// thread on them — enumeration may have to message a Pia window.</summary>
public interface IScreenCaptureService
{
    Task<IReadOnlyList<CaptureTarget>> EnumerateTargetsAsync(CancellationToken cancellationToken = default);

    Task<CaptureResult> CaptureAsync(CaptureTarget target, CancellationToken cancellationToken = default);
}
