using System.Globalization;
using Pia.Logging;

namespace Pia.Services.Diagnostics;

/// <summary>
/// A size control, not a privacy control. The sink writes up to 7 rolls a day and retention keeps a month
/// of them, so without a cap the export would be a multi-gigabyte read.
/// </summary>
public sealed record DiagnosticsExportCaps(int MaxLogFiles = 7, long MaxTotalSourceBytes = 10L * 1024 * 1024)
{
    public static DiagnosticsExportCaps Default { get; } = new();
}

/// <summary>Closed set on purpose: an exception message must never reach the manifest.</summary>
public enum DiagnosticsExclusionReason
{
    OverFileCountCap,
    OverTotalByteCap,
    UnrecognisedName,
    OpenFailed,
}

public sealed record DiagnosticsLogFile(
    string FileName, long Bytes, bool Included, DiagnosticsExclusionReason? ExclusionReason);

/// <summary>What an export would contain. Produced without reading a single log byte, so the consent
/// dialog can state the count and the range before anything is written.</summary>
public sealed record DiagnosticsExportPlan(
    IReadOnlyList<DiagnosticsLogFile> Files,
    int IncludedCount,
    long IncludedBytes,
    int ExcludedCount,
    DateOnly? OldestIncluded,
    DateOnly? NewestIncluded,
    bool CapApplied)
{
    public static DiagnosticsExportPlan Empty { get; } = new([], 0, 0, 0, null, null, false);
}

public sealed record DiagnosticsExportRequest(
    string SourceLogDirectory, string OutputZipPath, DiagnosticsExportCaps Caps)
{
    /// <summary>A name a user can be told out loud, unique to the second.</summary>
    public static string BuildFileName(DateTimeOffset at) =>
        string.Create(CultureInfo.InvariantCulture, $"pia-diagnostics-{at:yyyy-MM-dd-HHmmss}.zip");
}

public sealed record DiagnosticsExportResult(
    bool Succeeded,
    string? OutputZipPath,
    DiagnosticsExportPlan Plan,
    RedactionSummary? Redaction,
    DiagnosticsExportFailure? Failure);

/// <summary>A cause the caller can branch on. Never carries a path or an exception message.</summary>
public enum DiagnosticsExportFailure
{
    SourceDirectoryMissing,
    NoLogFiles,
    OutputInsideSourceDirectory,
    OutputDirectoryMissing,
    OutputAlreadyExists,
    WriteFailed,
}

/// <summary>
/// The allow-listed facts about this install. Built field by field from an explicit list — never by walking
/// a settings file — so a new setting cannot appear in an export by default.
/// </summary>
public sealed record DiagnosticsEnvironment(
    int SchemaVersion,
    DateTimeOffset ExportedAtUtc,
    string AppVersion,
    string OsDescription,
    string OsArchitecture,
    string ProcessArchitecture,
    string FrameworkDescription,
    string UiLanguage,
    bool SensitiveLoggingCompiledIn,
    bool DataDirectoryOverridden,
    IReadOnlyDictionary<string, int> ProviderTypeCounts,
    int ProviderCount);

/// <summary>The two things the exporter needs from the live app: what to say, and what to scrub.</summary>
public sealed record DiagnosticsExportContext(DiagnosticsEnvironment Environment, RedactionKeys Keys);
