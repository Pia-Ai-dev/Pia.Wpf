namespace Pia.Services.MeetingAttendee;

/// <summary>
/// How the meeting attendee should launch its browser, and how to recognize its process.
///
/// <para>Exactly one of <see cref="ExecutablePath"/> (bundled / arbitrary Chromium) XOR
/// <see cref="Channel"/> ("chrome"/"msedge") is set — Playwright treats them as mutually exclusive.
/// This record decouples the browser-choice / default-browser decisions from the launch + PID code in
/// <c>TeamsMeetingSession</c>.</para>
/// </summary>
/// <param name="ExecutablePath">
/// Path to a Chromium executable to launch (bundled build, or an arbitrary Chromium). Null when a
/// <paramref name="Channel"/> is used instead.
/// </param>
/// <param name="Channel">
/// Playwright browser channel ("chrome" or "msedge") for a system/branded install. Null when an
/// <paramref name="ExecutablePath"/> is used instead.
/// </param>
/// <param name="ProcessName">
/// Process name to scan when attributing the launched browser PID ("chrome" or "msedge").
/// </param>
/// <param name="MatchExecutablePath">
/// Resolved on-disk binary path used to disambiguate the PID from the user's own browser of the same
/// name. Bundled =&gt; the provisioned chrome.exe. Channel =&gt; resolved from App Paths; null only if
/// resolution failed, in which case PID matching falls back to process-name + new-since-launch only.
/// </param>
/// <param name="ShowWindow">
/// True = window visible on-screen; false = parked off-screen + taskbar button suppressed.
/// </param>
public sealed record BrowserLaunchSpec(
    string? ExecutablePath,
    string? Channel,
    string ProcessName,
    string? MatchExecutablePath,
    bool ShowWindow);
