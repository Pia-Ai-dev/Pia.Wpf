namespace Pia.Models;

/// <summary><c>TitleDisplay</c> is the pattern, or the localised "any window title" when it is blank —
/// <c>TargetNullValue</c> does not fire on an empty string.</summary>
public sealed record ScreenCaptureAllowlistRow(Guid Id, string ProcessName, string TitleDisplay);
