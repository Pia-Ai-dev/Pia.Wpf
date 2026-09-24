namespace Pia.Services.Screen;

/// <summary>Which of Pia's own windows a display capture has to hide from itself.</summary>
internal static class OwnWindowExclusion
{
    /// <summary>Visible windows only — an invisible one paints nothing into a capture, so excluding it
    /// buys no privacy and only risks disturbing a window nobody asked us to touch.</summary>
    public static List<nint> Needed(IEnumerable<(nint Hwnd, bool IsVisible)> own)
    {
        ArgumentNullException.ThrowIfNull(own);

        var needed = new List<nint>();
        foreach (var (hwnd, isVisible) in own)
        {
            if (hwnd != 0 && isVisible)
            {
                needed.Add(hwnd);
            }
        }

        return needed;
    }
}
