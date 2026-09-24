namespace Pia.Services.Screen;

/// <summary>What a capture site reports once a frame has actually been produced and handed on. The title is raw
/// here and is hashed before anything is written.</summary>
public sealed record ScreenCaptureAuditEvent(
    string Surface,
    Guid? TaskId,
    string? Granter,
    string TargetKind,
    string ProcessName,
    string? WindowTitle,
    int Width,
    int Height)
{
    /// <summary>A synthesised record ToString would print the window title, so one "{Evt}" in a log line would
    /// leak it into a release log.</summary>
    public override string ToString() =>
        $"{nameof(ScreenCaptureAuditEvent)} {{ Surface = {Surface}, TaskId = {TaskId}, Granter = {Granter}, "
        + $"TargetKind = {TargetKind}, ProcessName = {ProcessName}, Width = {Width}, Height = {Height} }}";
}

/// <summary>Who asked for the capture. Strings, not an enum: the trail is read by a human, and the serializer
/// would write an enum as an integer.</summary>
public static class ScreenCaptureSurfaces
{
    public const string Picker = "picker";
    public const string Interactive = "interactive";
    public const string Unattended = "unattended";
    public const string Voice = "voice";
    public const string Unknown = "unknown";
}

public static class ScreenCaptureTargetKinds
{
    public const string Monitor = "monitor";
    public const string Window = "window";
}

/// <summary>The on-disk line. Metadata only — no title, no bytes.</summary>
public sealed record ScreenCaptureAuditEntry(
    DateTimeOffset Timestamp,
    string Surface,
    Guid? TaskId,
    string? Granter,
    string TargetKind,
    string ProcessName,
    int Width,
    int Height,
    string TitleHash);
