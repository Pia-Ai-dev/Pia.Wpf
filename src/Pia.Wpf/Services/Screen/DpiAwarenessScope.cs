using Pia.Native;

namespace Pia.Services.Screen;

/// <summary>Flips the calling thread to per-monitor-v2 so rects and blits come back in physical pixels. Never the UI
/// thread — WPF caches DPI per render target — and never the process, which a manifest would re-layout.</summary>
internal readonly struct DpiAwarenessScope : IDisposable
{
    private readonly nint _previous;

    private DpiAwarenessScope(nint previous) => _previous = previous;

    public bool Applied => _previous != 0;

    public int PreviousAwareness => ScreenCaptureInterop.GetAwarenessFromDpiAwarenessContext(_previous);

    public static int CurrentAwareness =>
        ScreenCaptureInterop.GetAwarenessFromDpiAwarenessContext(ScreenCaptureInterop.GetThreadDpiAwarenessContext());

    public static DpiAwarenessScope PerMonitorV2() =>
        new(ScreenCaptureInterop.SetThreadDpiAwarenessContext(ScreenCaptureInterop.DpiAwarenessContextPerMonitorAwareV2));

    public void Dispose()
    {
        if (_previous != 0)
        {
            ScreenCaptureInterop.SetThreadDpiAwarenessContext(_previous);
        }
    }
}
