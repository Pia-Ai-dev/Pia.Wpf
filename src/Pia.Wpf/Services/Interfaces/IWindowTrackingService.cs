namespace Pia.Services.Interfaces;

public interface IWindowTrackingService
{
    void TrackForegroundWindow();
    void TrackWindowAtCursor();
    string? GetTrackedWindowTitle();
    void ClearTracking();
    bool RestorePreviousWindow();
    bool HasTrackedWindow { get; }
}
