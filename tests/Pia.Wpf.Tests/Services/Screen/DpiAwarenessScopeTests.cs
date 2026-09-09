using System.Runtime.ExceptionServices;
using Pia.Native;
using Pia.Services.Screen;
using Xunit;

namespace Pia.Tests.Services.Screen;

/// <summary>The standing runtime assertion behind every physical-pixel coordinate the capture seam returns.</summary>
public class DpiAwarenessScopeTests
{
    [Fact]
    public void PerMonitorV2_FlipsTheCallingThread_AndDisposeRestoresIt() =>
        OnADedicatedThread(() =>
        {
            var before = ScreenCaptureInterop.GetThreadDpiAwarenessContext();

            using (var scope = DpiAwarenessScope.PerMonitorV2())
            {
                Assert.True(scope.Applied);
                Assert.True(ScreenCaptureInterop.AreDpiAwarenessContextsEqual(
                    ScreenCaptureInterop.GetThreadDpiAwarenessContext(),
                    ScreenCaptureInterop.DpiAwarenessContextPerMonitorAwareV2));
            }

            Assert.True(ScreenCaptureInterop.AreDpiAwarenessContextsEqual(
                ScreenCaptureInterop.GetThreadDpiAwarenessContext(), before));
        });

    [Fact]
    public void Dispose_IsIdempotent() =>
        OnADedicatedThread(() =>
        {
            var before = ScreenCaptureInterop.GetThreadDpiAwarenessContext();

            var scope = DpiAwarenessScope.PerMonitorV2();
            scope.Dispose();
            var afterFirst = ScreenCaptureInterop.GetThreadDpiAwarenessContext();
            scope.Dispose();

            Assert.True(ScreenCaptureInterop.AreDpiAwarenessContextsEqual(afterFirst, before));
            Assert.True(ScreenCaptureInterop.AreDpiAwarenessContextsEqual(
                ScreenCaptureInterop.GetThreadDpiAwarenessContext(), before));
        });

    /// <summary>A failing assertion must not leave a pooled thread flipped for the rest of the suite.</summary>
    private static void OnADedicatedThread(Action body)
    {
        ExceptionDispatchInfo? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                body();
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
        });

        thread.Start();
        thread.Join();
        failure?.Throw();
    }
}
