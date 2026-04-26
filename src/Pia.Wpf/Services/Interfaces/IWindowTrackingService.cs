namespace Pia.Services.Interfaces;

public interface IWindowTrackingService
{
    void TrackForegroundWindow();
    void TrackWindowAtCursor();
    string? GetTrackedWindowTitle();
    string? GetTrackedWindowProcessName();
    void ClearTracking();
    bool RestorePreviousWindow();
    bool HasTrackedWindow { get; }
}
