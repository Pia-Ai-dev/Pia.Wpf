namespace Pia.Services.Screen;

internal readonly record struct WindowDescriptor(
    nint Hwnd,
    uint ProcessId,
    bool IsVisible,
    bool IsMinimized,
    bool IsCloaked,
    uint ExStyle,
    string Title,
    PixelRect Bounds);
