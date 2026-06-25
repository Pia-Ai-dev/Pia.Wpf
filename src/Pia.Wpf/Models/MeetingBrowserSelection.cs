namespace Pia.Models;

/// <summary>
/// Which browser the meeting attendee drives. Bundled Chromium is the only Playwright-guaranteed build
/// (the reliable default); System Chrome/Edge are opt-in convenience (may be affected by browser
/// updates or enterprise policy); SystemDefault detects the OS default browser and falls back to
/// bundled when it is not a Chromium-family browser.
/// </summary>
public enum MeetingBrowserSelection
{
    BundledChromium,
    SystemChrome,
    SystemEdge,
    SystemDefault,
}
