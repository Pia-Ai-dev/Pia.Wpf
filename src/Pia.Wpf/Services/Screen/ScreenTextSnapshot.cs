namespace Pia.Services.Screen;

public enum ScreenTextSnapshotOutcome
{
    Ok,
    WindowGone,
    NoAutomationTree,
    NoText,
    Timeout,
    Error,
}

/// <summary>One window's on-screen text. <see cref="Text"/> is the user's content — never log it above
/// SensitiveDebug.</summary>
public sealed record ScreenTextSnapshot(
    nint Hwnd,
    ScreenTextSnapshotOutcome Outcome,
    string Text,
    int ElementsVisited,
    int TextNodes,
    bool Truncated,
    TimeSpan Elapsed,
    string ContentHash)
{
    public bool IsUsable => Outcome == ScreenTextSnapshotOutcome.Ok;

    public static ScreenTextSnapshot Failed(
        nint hwnd, ScreenTextSnapshotOutcome outcome, TimeSpan elapsed, int elementsVisited = 0) =>
        new(hwnd, outcome, string.Empty, elementsVisited, 0, false, elapsed, string.Empty);

    /// <summary>A synthesised record ToString would print the window's whole text into any log line.</summary>
    public override string ToString() =>
        $"{nameof(ScreenTextSnapshot)} {{ Outcome = {Outcome}, ElementsVisited = {ElementsVisited}, "
        + $"TextNodes = {TextNodes}, Chars = {Text.Length}, Truncated = {Truncated}, "
        + $"Elapsed = {Elapsed.TotalMilliseconds:F0} ms, ContentHash = {ContentHash} }}";
}
