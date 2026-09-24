namespace Pia.Services.Screen;

/// <summary>Display affinity applies to every capture on the machine — a Teams share, the Snipping Tool — so it is
/// held for one capture and put back to whatever it was, including when the capture throws.</summary>
internal sealed class DisplayAffinityLease : IDisposable
{
    public const uint WDA_NONE = 0x0;
    public const uint WDA_MONITOR = 0x1;
    public const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

    private readonly IDisplayAffinityApi _api;
    private readonly List<(nint Hwnd, uint Previous)> _held = [];
    private readonly List<nint> _failed = [];
    private readonly List<nint> _notRestored = [];
    private bool _restored;
    private bool _blackedOut;

    private DisplayAffinityLease(IDisplayAffinityApi api) => _api = api;

    /// <summary>WDA_EXCLUDEFROMCAPTURE arrived in Windows 10 2004; below that WDA_MONITOR blacks the window out,
    /// which is still not a leak.</summary>
    public static uint PreferredAffinity =>
        OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041) ? WDA_EXCLUDEFROMCAPTURE : WDA_MONITOR;

    public bool AllExcluded => _failed.Count == 0;

    /// <summary>True when this Windows build can only hide Pia by painting it black, which is what a capture
    /// of the display Pia covers comes back as.</summary>
    public bool AnyBlackedOut => _blackedOut;

    public IReadOnlyList<nint> Failed => _failed;

    public IReadOnlyList<nint> NotRestored => _notRestored;

    /// <summary>The only way in: acquire, run, restore in a finally.</summary>
    public static T Run<T>(
        IDisplayAffinityApi api,
        IReadOnlyList<nint> hwnds,
        uint preferred,
        Func<DisplayAffinityLease, T> body)
    {
        var lease = new DisplayAffinityLease(api);
        try
        {
            lease.Acquire(hwnds, preferred);
            return body(lease);
        }
        finally
        {
            lease.Dispose();
        }
    }

    public void Dispose()
    {
        if (_restored)
        {
            return;
        }

        _restored = true;

        for (var i = _held.Count - 1; i >= 0; i--)
        {
            var (hwnd, previous) = _held[i];
            try
            {
                if (!_api.TrySet(hwnd, previous))
                {
                    _notRestored.Add(hwnd);
                }
            }
            catch (Exception)
            {
                _notRestored.Add(hwnd);
            }
        }
    }

    private void Acquire(IReadOnlyList<nint> hwnds, uint preferred)
    {
        foreach (var hwnd in hwnds)
        {
            // Windows reports WDA_NONE for a window that has none, so a failed read is a failed acquire —
            // guessing WDA_NONE here would clear an exclusion somebody else set and never put it back.
            if (!_api.TryGet(hwnd, out var previous))
            {
                _failed.Add(hwnd);
                continue;
            }

            var applied = _api.TrySet(hwnd, preferred);
            if (!applied && preferred == WDA_EXCLUDEFROMCAPTURE)
            {
                applied = _api.TrySet(hwnd, WDA_MONITOR);
            }

            if (applied)
            {
                _held.Add((hwnd, previous));
                if (preferred == WDA_MONITOR) _blackedOut = true;
            }
            else
            {
                _failed.Add(hwnd);
            }
        }
    }
}
