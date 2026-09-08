namespace Pia.Services.Screen;

internal enum WindowRejection
{
    None,
    OwnProcess,
    NotVisible,
    ToolWindow,
    Cloaked,
    Untitled,
    EmptyBounds,
}

internal static class WindowTargetFilter
{
    public const uint WS_EX_TOOLWINDOW = 0x00000080;

    /// <summary>Pure over a descriptor so it tests against a fake window list; Pia's own windows are found by pid,
    /// never by asking the running application.</summary>
    public static WindowRejection Classify(in WindowDescriptor window, uint ownProcessId)
    {
        if (window.ProcessId == ownProcessId)
        {
            return WindowRejection.OwnProcess;
        }

        if (!window.IsVisible)
        {
            return WindowRejection.NotVisible;
        }

        if ((window.ExStyle & WS_EX_TOOLWINDOW) != 0)
        {
            return WindowRejection.ToolWindow;
        }

        if (window.IsCloaked)
        {
            return WindowRejection.Cloaked;
        }

        // A row with no label is one the picker cannot show and a tool call cannot name.
        if (string.IsNullOrWhiteSpace(window.Title))
        {
            return WindowRejection.Untitled;
        }

        return window.Bounds.IsEmpty ? WindowRejection.EmptyBounds : WindowRejection.None;
    }

    public static bool IsEligible(in WindowDescriptor window, uint ownProcessId) =>
        Classify(window, ownProcessId) == WindowRejection.None;
}
