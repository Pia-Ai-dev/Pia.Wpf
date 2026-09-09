namespace Pia.Services.Screen;

/// <summary>One window a run nobody is watching may capture. <c>TitleContains</c> is empty for "any single
/// window of this program".</summary>
public sealed record ScreenCaptureAllowlistEntry(
    Guid Id,
    string ProcessName,
    string TitleContains,
    DateTimeOffset AddedAt);

/// <summary>The on-disk shape of <see cref="ScreenCaptureAllowlistStore"/>.</summary>
public sealed record ScreenCaptureAllowlistState
{
    public List<ScreenCaptureAllowlistEntry> Entries { get; set; } = [];
}
