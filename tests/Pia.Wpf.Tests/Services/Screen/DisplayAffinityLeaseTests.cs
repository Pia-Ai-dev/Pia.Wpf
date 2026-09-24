using Pia.Services.Screen;
using Xunit;

namespace Pia.Tests.Services.Screen;

public class DisplayAffinityLeaseTests
{
    private static readonly nint H1 = 0x11;
    private static readonly nint H2 = 0x22;
    private static readonly nint H3 = 0x33;

    private static readonly nint[] Handles = [H1, H2, H3];

    [Fact]
    public void Run_ExcludesEveryHandle_ThenRestoresThePreviousValue()
    {
        var api = new FakeDisplayAffinityApi();

        var inside = DisplayAffinityLease.Run(api, Handles, DisplayAffinityLease.WDA_EXCLUDEFROMCAPTURE, lease =>
        {
            Assert.True(lease.AllExcluded);
            Assert.Empty(lease.Failed);
            return Handles.Select(h => api.Current[h]).ToArray();
        });

        Assert.All(inside, affinity => Assert.Equal(DisplayAffinityLease.WDA_EXCLUDEFROMCAPTURE, affinity));
        Assert.All(Handles, h => Assert.Equal(DisplayAffinityLease.WDA_NONE, api.Current[h]));
    }

    [Fact]
    public void Run_RestoresEvenWhenTheBodyThrows()
    {
        var api = new FakeDisplayAffinityApi();

        Assert.Throws<InvalidOperationException>(() =>
            DisplayAffinityLease.Run<int>(api, Handles, DisplayAffinityLease.WDA_EXCLUDEFROMCAPTURE, _ =>
                throw new InvalidOperationException("the capture blew up")));

        Assert.All(Handles, h => Assert.Equal(DisplayAffinityLease.WDA_NONE, api.Current[h]));

        var restores = api.SetCalls.TakeLast(3).ToArray();
        Assert.Equal(new[] { H3, H2, H1 }, restores.Select(c => c.Hwnd).ToArray());
        Assert.All(restores, c => Assert.Equal(DisplayAffinityLease.WDA_NONE, c.Affinity));
    }

    [Fact]
    public void Run_FallsBackToMonitor_WhenExcludeFromCaptureIsRefused()
    {
        var api = new FakeDisplayAffinityApi();
        api.RejectedAffinities.Add(DisplayAffinityLease.WDA_EXCLUDEFROMCAPTURE);

        DisplayAffinityLease.Run(api, Handles, DisplayAffinityLease.WDA_EXCLUDEFROMCAPTURE, lease =>
        {
            Assert.True(lease.AllExcluded);
            Assert.Empty(lease.Failed);
            Assert.All(Handles, h => Assert.Equal(DisplayAffinityLease.WDA_MONITOR, api.Current[h]));
            return 0;
        });

        Assert.All(Handles, h => Assert.Equal(DisplayAffinityLease.WDA_NONE, api.Current[h]));
    }

    [Fact]
    public void Run_ReportsAFailedHandle_AndStillRestoresTheOthers()
    {
        var api = new FakeDisplayAffinityApi();
        api.RejectedHandles.Add(H2);

        DisplayAffinityLease.Run(api, Handles, DisplayAffinityLease.WDA_EXCLUDEFROMCAPTURE, lease =>
        {
            Assert.False(lease.AllExcluded);
            Assert.Equal(new[] { H2 }, lease.Failed.ToArray());
            return 0;
        });

        Assert.Equal(DisplayAffinityLease.WDA_NONE, api.Current[H1]);
        Assert.Equal(DisplayAffinityLease.WDA_NONE, api.Current[H3]);
        Assert.False(api.Current.ContainsKey(H2));
    }

    [Fact]
    public void Run_RestoresToThePreviousValue_NotBlindlyToNone()
    {
        var api = new FakeDisplayAffinityApi();
        api.Current[H1] = DisplayAffinityLease.WDA_MONITOR;

        DisplayAffinityLease.Run(api, Handles, DisplayAffinityLease.WDA_EXCLUDEFROMCAPTURE, _ => 0);

        Assert.Equal(DisplayAffinityLease.WDA_MONITOR, api.Current[H1]);
        Assert.Equal(DisplayAffinityLease.WDA_NONE, api.Current[H2]);
    }

    [Fact]
    public void Run_WithPreferredMonitor_NeverAsksForExcludeFromCapture()
    {
        var api = new FakeDisplayAffinityApi();

        DisplayAffinityLease.Run(api, Handles, DisplayAffinityLease.WDA_MONITOR, _ => 0);

        Assert.DoesNotContain(DisplayAffinityLease.WDA_EXCLUDEFROMCAPTURE, api.SetCalls.Select(c => c.Affinity));
    }

    [Fact]
    public void Run_ReportsAHandleItCouldNotPutBack()
    {
        var api = new FakeDisplayAffinityApi();
        DisplayAffinityLease? seen = null;

        DisplayAffinityLease.Run(api, Handles, DisplayAffinityLease.WDA_EXCLUDEFROMCAPTURE, lease =>
        {
            seen = lease;
            api.RejectedHandles.Add(H2);
            return 0;
        });

        Assert.NotNull(seen);
        Assert.Equal(new[] { H2 }, seen.NotRestored.ToArray());
    }

    /// <summary>A read that fails is not a window with no affinity: assuming WDA_NONE would clear an exclusion
    /// somebody else set and put the wrong value back.</summary>
    [Fact]
    public void Run_TreatsAnUnreadableAffinity_AsAFailedAcquire()
    {
        var api = new FakeDisplayAffinityApi();
        api.UnreadableHandles.Add(H2);

        DisplayAffinityLease.Run(api, Handles, DisplayAffinityLease.WDA_EXCLUDEFROMCAPTURE, lease =>
        {
            Assert.False(lease.AllExcluded);
            Assert.Equal(new[] { H2 }, lease.Failed.ToArray());
            return 0;
        });

        Assert.DoesNotContain(H2, api.SetCalls.Select(c => c.Hwnd));
    }

    [Fact]
    public void AnyBlackedOut_IsTrue_OnlyWhenTheOsCanOnlyPaintPiaBlack()
    {
        var excluded = new FakeDisplayAffinityApi();
        DisplayAffinityLease.Run(excluded, Handles, DisplayAffinityLease.WDA_EXCLUDEFROMCAPTURE, lease =>
        {
            Assert.False(lease.AnyBlackedOut);
            return 0;
        });

        // One window falling back says nothing about the whole display, and the ordinary blank-frame
        // message already offers "Pia may be covering it".
        var fallback = new FakeDisplayAffinityApi();
        fallback.RejectedAffinities.Add(DisplayAffinityLease.WDA_EXCLUDEFROMCAPTURE);
        DisplayAffinityLease.Run(fallback, Handles, DisplayAffinityLease.WDA_EXCLUDEFROMCAPTURE, lease =>
        {
            Assert.False(lease.AnyBlackedOut);
            return 0;
        });

        var preferred = new FakeDisplayAffinityApi();
        DisplayAffinityLease.Run(preferred, Handles, DisplayAffinityLease.WDA_MONITOR, lease =>
        {
            Assert.True(lease.AnyBlackedOut);
            return 0;
        });
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var api = new FakeDisplayAffinityApi();

        DisplayAffinityLease.Run(api, Handles, DisplayAffinityLease.WDA_EXCLUDEFROMCAPTURE, lease =>
        {
            lease.Dispose();
            lease.Dispose();
            return 0;
        });

        Assert.Equal(6, api.SetCalls.Count);
    }

    [Fact]
    public void PreferredAffinity_IsOneOfTheTwoLeakSafeValues() =>
        Assert.Contains(
            DisplayAffinityLease.PreferredAffinity,
            new[] { DisplayAffinityLease.WDA_MONITOR, DisplayAffinityLease.WDA_EXCLUDEFROMCAPTURE });

    private sealed class FakeDisplayAffinityApi : IDisplayAffinityApi
    {
        public Dictionary<nint, uint> Current { get; } = [];

        public List<(nint Hwnd, uint Affinity)> SetCalls { get; } = [];

        public HashSet<uint> RejectedAffinities { get; } = [];

        public HashSet<nint> RejectedHandles { get; } = [];

        /// <summary>Handles whose affinity cannot be read at all — GetWindowDisplayAffinity succeeds with
        /// WDA_NONE on an ordinary window, so only a failing call returns false.</summary>
        public HashSet<nint> UnreadableHandles { get; } = [];

        public bool TryGet(nint hwnd, out uint affinity)
        {
            affinity = DisplayAffinityLease.WDA_NONE;
            if (UnreadableHandles.Contains(hwnd)) return false;

            Current.TryGetValue(hwnd, out affinity);
            return true;
        }

        public bool TrySet(nint hwnd, uint affinity)
        {
            SetCalls.Add((hwnd, affinity));
            if (RejectedHandles.Contains(hwnd) || RejectedAffinities.Contains(affinity))
            {
                return false;
            }

            Current[hwnd] = affinity;
            return true;
        }
    }
}
