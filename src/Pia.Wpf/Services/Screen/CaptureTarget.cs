namespace Pia.Services.Screen;

public enum CaptureTargetKind
{
    Monitor,
    Window,
}

/// <summary>Physical pixels in virtual-screen coordinates; not a <c>System.Windows.Rect</c> so ViewModels may read it.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public int Right => X + Width;

    public int Bottom => Y + Height;

    public bool Contains(PixelRect other) =>
        other.X >= X && other.Y >= Y && other.Right <= Right && other.Bottom <= Bottom;
}

public sealed record CaptureTarget(
    CaptureTargetKind Kind,
    nint Hwnd,
    string MonitorDeviceId,
    PixelRect Bounds,
    string ProcessName,
    string Title,
    bool IsPrimary,
    bool IsMinimized,
    // Windows recycles window handles, so the owner is re-read before the capture. 0 means unknown.
    uint ProcessId = 0)
{
    /// <summary>Overridden because the synthesized version prints the title into any log line that formats a target.</summary>
    public override string ToString() =>
        Kind == CaptureTargetKind.Monitor
            ? $"Monitor {MonitorDeviceId} {Bounds.Width}x{Bounds.Height}{(IsPrimary ? " primary" : string.Empty)}"
            : $"Window {ProcessName} {Bounds.Width}x{Bounds.Height}{(IsMinimized ? " minimized" : string.Empty)}";
}
