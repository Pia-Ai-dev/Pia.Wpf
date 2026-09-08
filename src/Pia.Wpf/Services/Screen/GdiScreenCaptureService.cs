using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using Pia.Logging;
using Pia.Native;
using Pia.Services.Interfaces;

namespace Pia.Services.Screen;

public sealed class GdiScreenCaptureService : IScreenCaptureService
{
    private static readonly TimeSpan WindowWatchdog = TimeSpan.FromSeconds(5);

    private readonly ILogger<GdiScreenCaptureService> _logger;
    private readonly IDisplayAffinityApi _affinity = new NativeDisplayAffinityApi();
    private readonly SemaphoreSlim _monitorGate = new(1, 1);

    public GdiScreenCaptureService(ILogger<GdiScreenCaptureService> logger) => _logger = logger;

    public Task<IReadOnlyList<CaptureTarget>> EnumerateTargetsAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => Enumerate(cancellationToken), cancellationToken);

    public async Task<CaptureResult> CaptureAsync(CaptureTarget target, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (target.Kind == CaptureTargetKind.Monitor)
        {
            // Two overlapping monitor captures would each read the other's exclusion as the value to restore,
            // leaving Pia hidden from every capture on the machine.
            await _monitorGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await Task.Run(() => CaptureMonitor(target), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _monitorGate.Release();
            }
        }

        // Only the window path gets a watchdog: PrintWindow messages the target, while abandoning the monitor path
        // early would return while Pia is still excluded from every capture on the machine.
        var work = Task.Run(() => CaptureWindow(target), cancellationToken);
        try
        {
            return await work.WaitAsync(WindowWatchdog, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning(
                "Capture of window {Process} timed out after {Seconds}s", target.ProcessName, WindowWatchdog.TotalSeconds);
            return CaptureResult.Failed(target, CaptureFailureReason.Timeout);
        }
    }

    private IReadOnlyList<CaptureTarget> Enumerate(CancellationToken cancellationToken)
    {
        var targets = new List<CaptureTarget>();
        var monitors = 0;
        var rejected = 0;

        try
        {
            using var dpi = DpiAwarenessScope.PerMonitorV2();
            LogDpi(dpi);

            foreach (var monitor in ReadMonitors())
            {
                targets.Add(monitor);
                monitors++;
            }

            cancellationToken.ThrowIfCancellationRequested();

            var ownProcessId = (uint)Environment.ProcessId;
            var processNames = new Dictionary<uint, string>();

            foreach (var hwnd in ScreenCaptureInterop.EnumerateTopLevelWindows())
            {
                // Own windows are dropped before anything else is asked of them: GetWindowText on a window of the
                // calling process sends WM_GETTEXT to that window's thread instead of reading the cached title.
                if (ScreenCaptureInterop.GetWindowThreadProcessId(hwnd, out var processId) == 0
                    || processId == ownProcessId)
                {
                    continue;
                }

                var window = Describe(hwnd, processId);
                if (window is not { } descriptor)
                {
                    continue;
                }

                if (!WindowTargetFilter.IsEligible(descriptor, ownProcessId))
                {
                    rejected++;
                    continue;
                }

                targets.Add(new CaptureTarget(
                    CaptureTargetKind.Window,
                    hwnd,
                    string.Empty,
                    descriptor.Bounds,
                    ProcessNameFor(processId, processNames),
                    descriptor.Title,
                    IsPrimary: false,
                    descriptor.IsMinimized,
                    processId));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Screen target enumeration failed ({Type})", ex.GetType().Name);
            _logger.SensitiveDebug("Screen target enumeration failure: {Error}", ex);
        }

        _logger.LogDebug(
            "Enumerated {Monitors} monitors, {Eligible} eligible windows, {Rejected} rejected",
            monitors, targets.Count - monitors, rejected);

        return targets;
    }

    private CaptureResult CaptureWindow(CaptureTarget target)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var dpi = DpiAwarenessScope.PerMonitorV2();
            LogDpi(dpi);

            var hwnd = target.Hwnd;
            if (hwnd == 0 || !ScreenCaptureInterop.IsWindow(hwnd))
            {
                return Refuse(target, CaptureFailureReason.TargetGone);
            }

            if (ScreenCaptureInterop.GetWindowThreadProcessId(hwnd, out var processId) == 0)
            {
                return Refuse(target, CaptureFailureReason.TargetGone);
            }

            if (processId == (uint)Environment.ProcessId)
            {
                return Refuse(target, CaptureFailureReason.SelfTarget);
            }

            // A handle that now belongs to another process is another window: the one the user approved, or
            // the one the allowlist cleared, is gone.
            if (target.ProcessId != 0 && processId != target.ProcessId)
            {
                return Refuse(target, CaptureFailureReason.TargetGone);
            }

            if (ScreenCaptureInterop.IsIconic(hwnd))
            {
                return Refuse(target, CaptureFailureReason.Minimized);
            }

            if (IsCloaked(hwnd))
            {
                return Refuse(target, CaptureFailureReason.Cloaked);
            }

            var full = ReadWindowRect(hwnd);
            if (full is not { } outer || outer.IsEmpty)
            {
                return Refuse(target, CaptureFailureReason.EmptyBounds);
            }

            var frame = Render(
                outer.Width,
                outer.Height,
                (memDc, _) =>
                {
                    if (ScreenCaptureInterop.PrintWindow(hwnd, memDc, ScreenCaptureInterop.PW_RENDERFULLCONTENT))
                    {
                        return true;
                    }

                    _logger.LogWarning("PrintWindow refused {Process} (error {Error})",
                        target.ProcessName, Marshal.GetLastPInvokeError());
                    return false;
                });

            if (frame is null)
            {
                return Refuse(target, CaptureFailureReason.NativeError);
            }

            // The invisible resize border renders as black, so the shot is cut back to the frame DWM draws.
            var cropped = CropToFrame(frame, outer, ReadExtendedFrameBounds(hwnd) ?? outer);
            if (FrameUniformityDetector.IsNearUniform(cropped))
            {
                return Refuse(target, CaptureFailureReason.UniformFrame);
            }

            var result = CaptureResult.Success(target, cropped);
            LogCaptured(target, result, stopwatch);
            return result;
        }
        catch (Exception ex)
        {
            return Failed(target, ex);
        }
    }

    private CaptureResult CaptureMonitor(CaptureTarget target)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var dpi = DpiAwarenessScope.PerMonitorV2();
            LogDpi(dpi);

            var current = ReadMonitors().FirstOrDefault(m =>
                string.Equals(m.MonitorDeviceId, target.MonitorDeviceId, StringComparison.OrdinalIgnoreCase));

            if (current is null)
            {
                return Refuse(target, CaptureFailureReason.TargetGone);
            }

            if (current.Bounds.IsEmpty)
            {
                return Refuse(target, CaptureFailureReason.EmptyBounds);
            }

            var own = ReadOwnWindows();
            DisplayAffinityLease? lease = null;

            try
            {
                return DisplayAffinityLease.Run(
                    _affinity,
                    own,
                    DisplayAffinityLease.PreferredAffinity,
                    held =>
                    {
                        lease = held;

                        if (!held.AllExcluded)
                        {
                            _logger.LogWarning(
                                "Refusing a display capture: {Failed} of {Total} Pia windows would not accept a capture exclusion ({Handles})",
                                held.Failed.Count, own.Count, string.Join(",", held.Failed.Select(h => h.ToString("X"))));
                            return Refuse(target, CaptureFailureReason.SelfExclusionFailed);
                        }

                        ScreenCaptureInterop.DwmFlush();

                        var bounds = current.Bounds;
                        var frame = Render(
                            bounds.Width,
                            bounds.Height,
                            (memDc, screenDc) =>
                            {
                                if (ScreenCaptureInterop.BitBlt(
                                        memDc, 0, 0, bounds.Width, bounds.Height,
                                        screenDc, bounds.X, bounds.Y, ScreenCaptureInterop.SRCCOPY))
                                {
                                    return true;
                                }

                                _logger.LogWarning("Display blit failed (error {Error})", Marshal.GetLastPInvokeError());
                                return false;
                            });

                        if (frame is null)
                        {
                            return Refuse(target, CaptureFailureReason.NativeError);
                        }

                        if (FrameUniformityDetector.IsNearUniform(frame))
                        {
                            // Blaming the captured app for a blank frame Pia itself painted would leave the
                            // user with nothing to act on.
                            return Refuse(
                                target,
                                held.AnyBlackedOut
                                    ? CaptureFailureReason.SelfBlackout
                                    : CaptureFailureReason.UniformFrame);
                        }

                        var success = CaptureResult.Success(target, frame);
                        LogCaptured(target, success, stopwatch);
                        return success;
                    });
            }
            finally
            {
                // Reported from a finally because a throwing capture is exactly when a restore is most likely to
                // have been missed.
                if (lease is { NotRestored.Count: > 0 } finished)
                {
                    _logger.LogError(
                        "{Count} Pia windows are still excluded from screen capture; they will stay hidden in a screen share",
                        finished.NotRestored.Count);
                }
            }
        }
        catch (Exception ex)
        {
            return Failed(target, ex);
        }
    }

    private unsafe BitmapSource? Render(int width, int height, Func<nint, nint, bool> draw)
    {
        nint screenDc = 0;
        nint memDc = 0;
        nint bitmap = 0;
        nint previous = 0;

        try
        {
            screenDc = ScreenCaptureInterop.GetDC(0);
            if (screenDc == 0)
            {
                _logger.LogWarning("No screen device context available for a capture");
                return null;
            }

            memDc = ScreenCaptureInterop.CreateCompatibleDC(screenDc);
            if (memDc == 0)
            {
                _logger.LogWarning("No memory device context available for a capture");
                return null;
            }

            bitmap = ScreenCaptureInterop.CreateCompatibleBitmap(screenDc, width, height);
            if (bitmap == 0)
            {
                _logger.LogWarning("No compatible bitmap available for a {Width}x{Height} capture", width, height);
                return null;
            }

            previous = ScreenCaptureInterop.SelectObject(memDc, bitmap);
            if (!draw(memDc, screenDc))
            {
                return null;
            }

            // GetDIBits copies nothing while the bitmap is still selected into a device context.
            ScreenCaptureInterop.SelectObject(memDc, previous);
            previous = 0;

            return ReadPixels(memDc, bitmap, width, height);
        }
        finally
        {
            if (previous != 0)
            {
                ScreenCaptureInterop.SelectObject(memDc, previous);
            }

            if (bitmap != 0)
            {
                ScreenCaptureInterop.DeleteObject(bitmap);
            }

            if (memDc != 0)
            {
                ScreenCaptureInterop.DeleteDC(memDc);
            }

            if (screenDc != 0)
            {
                ScreenCaptureInterop.ReleaseDC(0, screenDc);
            }
        }
    }

    private unsafe BitmapSource? ReadPixels(nint dc, nint bitmap, int width, int height)
    {
        var stride = width * 4;
        var pixels = new byte[(long)stride * height];
        var info = new ScreenCaptureInterop.BITMAPINFO
        {
            bmiHeader = new ScreenCaptureInterop.BITMAPINFOHEADER
            {
                biSize = (uint)sizeof(ScreenCaptureInterop.BITMAPINFOHEADER),
                biWidth = width,
                biHeight = -height,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = ScreenCaptureInterop.BI_RGB,
            },
        };

        int copied;
        fixed (byte* buffer = pixels)
        {
            copied = ScreenCaptureInterop.GetDIBits(
                dc, bitmap, 0, (uint)height, buffer, ref info, ScreenCaptureInterop.DIB_RGB_COLORS);
        }

        if (copied <= 0)
        {
            _logger.LogWarning("Reading capture pixels failed (error {Error})", Marshal.GetLastPInvokeError());
            return null;
        }

        // Bgr32, not Bgra32: GDI leaves alpha at zero and a JPEG encoder renders that as black.
        var frame = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, pixels, stride);
        frame.Freeze();
        return frame;
    }

    private static BitmapSource CropToFrame(BitmapSource frame, PixelRect outer, PixelRect visible)
    {
        if (visible == outer || visible.IsEmpty || !outer.Contains(visible))
        {
            return frame;
        }

        var cropped = new CroppedBitmap(
            frame, new Int32Rect(visible.X - outer.X, visible.Y - outer.Y, visible.Width, visible.Height));
        cropped.Freeze();
        return cropped;
    }

    private static List<CaptureTarget> ReadMonitors()
    {
        var monitors = new List<CaptureTarget>();

        foreach (var handle in ScreenCaptureInterop.EnumerateMonitors())
        {
            var info = default(ScreenCaptureInterop.MONITORINFOEXW);
            info.cbSize = MonitorInfoSize();
            if (!ScreenCaptureInterop.GetMonitorInfo(handle, ref info))
            {
                continue;
            }

            monitors.Add(new CaptureTarget(
                CaptureTargetKind.Monitor,
                Hwnd: 0,
                ScreenCaptureInterop.ReadDeviceName(info),
                ToPixelRect(info.rcMonitor),
                ProcessName: string.Empty,
                Title: string.Empty,
                (info.dwFlags & ScreenCaptureInterop.MONITORINFOF_PRIMARY) != 0,
                IsMinimized: false));
        }

        return [.. monitors.OrderByDescending(m => m.IsPrimary).ThenBy(m => m.MonitorDeviceId, StringComparer.OrdinalIgnoreCase)];
    }

    private static List<nint> ReadOwnWindows()
    {
        var ownProcessId = (uint)Environment.ProcessId;
        var own = new List<nint>();

        // Hidden HWNDs are kept: popups and tray flyouts never appear in the application's window list.
        foreach (var hwnd in ScreenCaptureInterop.EnumerateTopLevelWindows())
        {
            if (ScreenCaptureInterop.GetWindowThreadProcessId(hwnd, out var processId) != 0
                && processId == ownProcessId)
            {
                own.Add(hwnd);
            }
        }

        return own;
    }

    private static WindowDescriptor? Describe(nint hwnd, uint processId)
    {
        if (!ScreenCaptureInterop.IsWindow(hwnd))
        {
            return null;
        }

        var minimized = ScreenCaptureInterop.IsIconic(hwnd);
        var outer = ReadWindowRect(hwnd);
        if (outer is not { } bounds)
        {
            return null;
        }

        // A minimized window has no frame for DWM to report, so its restored rect is the only size available.
        if (!minimized && ReadExtendedFrameBounds(hwnd) is { } visible)
        {
            bounds = visible;
        }

        return new WindowDescriptor(
            hwnd,
            processId,
            ScreenCaptureInterop.IsWindowVisible(hwnd),
            minimized,
            IsCloaked(hwnd),
            (uint)ScreenCaptureInterop.GetWindowLong(hwnd, ScreenCaptureInterop.GWL_EXSTYLE),
            ReadTitle(hwnd),
            bounds);
    }

    private static unsafe string ReadTitle(nint hwnd)
    {
        var length = ScreenCaptureInterop.GetWindowTextLength(hwnd);
        if (length <= 0)
        {
            return string.Empty;
        }

        var buffer = new char[length + 1];
        fixed (char* text = buffer)
        {
            var copied = ScreenCaptureInterop.GetWindowText(hwnd, text, buffer.Length);
            return copied <= 0 ? string.Empty : new string(text, 0, copied);
        }
    }

    private static PixelRect? ReadWindowRect(nint hwnd) =>
        ScreenCaptureInterop.GetWindowRect(hwnd, out var rect) ? ToPixelRect(rect) : null;

    private static unsafe PixelRect? ReadExtendedFrameBounds(nint hwnd)
    {
        var rect = default(ScreenCaptureInterop.RECT);
        var hr = ScreenCaptureInterop.DwmGetWindowAttribute(
            hwnd, ScreenCaptureInterop.DWMWA_EXTENDED_FRAME_BOUNDS, &rect, (uint)sizeof(ScreenCaptureInterop.RECT));

        if (hr != 0)
        {
            return null;
        }

        var bounds = ToPixelRect(rect);
        return bounds.IsEmpty ? null : bounds;
    }

    private static unsafe bool IsCloaked(nint hwnd)
    {
        var cloaked = 0;
        var hr = ScreenCaptureInterop.DwmGetWindowAttribute(
            hwnd, ScreenCaptureInterop.DWMWA_CLOAKED, &cloaked, sizeof(int));

        return hr == 0 && cloaked != 0;
    }

    private static unsafe uint MonitorInfoSize() => (uint)sizeof(ScreenCaptureInterop.MONITORINFOEXW);

    private static PixelRect ToPixelRect(ScreenCaptureInterop.RECT rect) =>
        new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);

    private static string ProcessNameFor(uint processId, Dictionary<uint, string> cache)
    {
        if (cache.TryGetValue(processId, out var cached))
        {
            return cached;
        }

        var name = string.Empty;
        try
        {
            using var process = Process.GetProcessById((int)processId);
            name = process.ProcessName;
        }
        catch (ArgumentException)
        {
        }
        catch (InvalidOperationException)
        {
        }

        cache[processId] = name;
        return name;
    }

    private void LogDpi(in DpiAwarenessScope scope)
    {
        if (!scope.Applied)
        {
            _logger.LogWarning("Per-monitor DPI context refused; captures on a mixed-DPI desktop will come back scaled");
            return;
        }

        _logger.LogDebug(
            "Capture thread DPI awareness {Before} -> {After}", scope.PreviousAwareness, DpiAwarenessScope.CurrentAwareness);
    }

    private void LogCaptured(CaptureTarget target, CaptureResult result, Stopwatch stopwatch)
    {
        _logger.LogInformation(
            "Captured {Kind} {Process} {Width}x{Height} in {Ms} ms",
            target.Kind, target.ProcessName, result.Width, result.Height, stopwatch.ElapsedMilliseconds);
        _logger.SensitiveDebug("Capture target title: {Title}", target.Title);
    }

    private CaptureResult Refuse(CaptureTarget target, CaptureFailureReason reason)
    {
        _logger.LogWarning("Capture of {Kind} {Process} refused: {Reason}", target.Kind, target.ProcessName, reason);
        _logger.SensitiveDebug("Refused capture target title: {Title}", target.Title);
        return CaptureResult.Failed(target, reason);
    }

    private CaptureResult Failed(CaptureTarget target, Exception error)
    {
        _logger.LogWarning("Capture of {Kind} {Process} failed ({Type})", target.Kind, target.ProcessName, error.GetType().Name);
        _logger.SensitiveDebug("Capture failure: {Error}", error);
        return CaptureResult.Failed(target, CaptureFailureReason.NativeError);
    }

    private sealed class NativeDisplayAffinityApi : IDisplayAffinityApi
    {
        public bool TryGet(nint hwnd, out uint affinity) =>
            ScreenCaptureInterop.GetWindowDisplayAffinity(hwnd, out affinity);

        public bool TrySet(nint hwnd, uint affinity) =>
            ScreenCaptureInterop.SetWindowDisplayAffinity(hwnd, affinity);
    }
}
