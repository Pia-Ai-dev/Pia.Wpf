using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Pia.Logging;
using Pia.Services.Interfaces;

namespace Pia.Services.Diagnostics;

/// <summary>
/// Writes a redacted, consented zip of the app's own log files. Takes its source directory and its output
/// path as parameters and resolves nothing itself, so every test runs against a temp directory and the gate
/// cannot be talked into writing to the real profile.
/// </summary>
public sealed class DiagnosticsExportService : IDiagnosticsExportService
{
    private const int SchemaVersion = 1;
    private const string LogEntryPrefix = "logs/";
    // Wider than pia-????-??-??.log on purpose: the sink rolls at 10 MB and a rolled name carries a suffix,
    // so a fixed-width pattern would drop a real log file AND leave it out of the manifest. The sink's own
    // base name, pia.log, is matched too — it carries no date, so it is listed as excluded rather than
    // vanishing if FormatLogFileName ever stops stamping one on.
    private const string FileNamePattern = "pia*.log";
    private const string FileNamePrefix = "pia-";
    private const int StampLength = 10;

    // Strings, not ordinals: an ExclusionReason of 0 next to a null one is not a reason anyone can read.
    private static readonly JsonSerializerOptions Json =
        new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    private readonly ILogger<DiagnosticsExportService> _logger;
    private readonly IDiagnosticsEnvironmentCollector _collector;

    public DiagnosticsExportService(
        ILogger<DiagnosticsExportService> logger, IDiagnosticsEnvironmentCollector collector)
    {
        _logger = logger;
        _collector = collector;
    }

    public DiagnosticsExportPlan Plan(string sourceLogDirectory, DiagnosticsExportCaps caps)
    {
        ArgumentNullException.ThrowIfNull(caps);
        if (string.IsNullOrWhiteSpace(sourceLogDirectory) || !Directory.Exists(sourceLogDirectory))
            return DiagnosticsExportPlan.Empty;

        var candidates = new List<(DateOnly? Date, FileInfo File)>();
        foreach (var path in Directory.EnumerateFiles(sourceLogDirectory, FileNamePattern))
        {
            candidates.Add((DateOf(Path.GetFileNameWithoutExtension(path)), new FileInfo(path)));
        }

        var files = new List<DiagnosticsLogFile>();
        long includedBytes = 0;
        var capApplied = false;
        DateOnly? oldest = null, newest = null;

        // Contiguous newest-first run: stop at the first file that would breach, rather than skipping it and
        // hunting for a smaller older one. "You have 08-19 through 08-24" is reasonable about; a set with
        // holes in it is not.
        var stopped = false;
        foreach (var candidate in candidates
                     .Where(c => c.Date is not null)
                     .OrderByDescending(c => c.Date!.Value)
                     .ThenByDescending(c => c.File.Name, StringComparer.Ordinal))
        {
            var bytes = candidate.File.Length;
            if (stopped || files.Count(f => f.Included) >= caps.MaxLogFiles)
            {
                capApplied = true;
                files.Add(new DiagnosticsLogFile(
                    candidate.File.Name, bytes, false, DiagnosticsExclusionReason.OverFileCountCap));
                continue;
            }

            if (includedBytes + bytes > caps.MaxTotalSourceBytes && includedBytes > 0)
            {
                stopped = true;
                capApplied = true;
                files.Add(new DiagnosticsLogFile(
                    candidate.File.Name, bytes, false, DiagnosticsExclusionReason.OverTotalByteCap));
                continue;
            }

            includedBytes += bytes;
            files.Add(new DiagnosticsLogFile(candidate.File.Name, bytes, true, null));
            oldest = candidate.Date;
            newest ??= candidate.Date;
        }

        foreach (var unrecognised in candidates.Where(c => c.Date is null))
        {
            files.Add(new DiagnosticsLogFile(
                unrecognised.File.Name, unrecognised.File.Length, false,
                DiagnosticsExclusionReason.UnrecognisedName));
        }

        var included = files.Count(f => f.Included);
        return new DiagnosticsExportPlan(
            files, included, includedBytes, files.Count - included, oldest, newest, capApplied);
    }

    /// <summary>The date a file name claims, accepting a roll suffix after it. Null means the name is not ours.</summary>
    private static DateOnly? DateOf(string nameWithoutExtension)
    {
        if (nameWithoutExtension.Length < FileNamePrefix.Length + StampLength)
            return null;

        var stamp = nameWithoutExtension.AsSpan(FileNamePrefix.Length);
        if (stamp.Length > StampLength && stamp[StampLength] != '-')
            return null;

        return DateOnly.TryParseExact(
            stamp[..StampLength], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None,
            out var date)
            ? date
            : null;
    }

    public async Task<DiagnosticsExportResult> ExportAsync(
        DiagnosticsExportRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.SourceLogDirectory)
            || !Directory.Exists(request.SourceLogDirectory))
            return Failed(DiagnosticsExportFailure.SourceDirectoryMissing);

        if (IsInsideSourceDirectory(request))
            return Failed(DiagnosticsExportFailure.OutputInsideSourceDirectory);

        var outputDirectory = Path.GetDirectoryName(Path.GetFullPath(request.OutputZipPath));
        if (string.IsNullOrEmpty(outputDirectory) || !Directory.Exists(outputDirectory))
            return Failed(DiagnosticsExportFailure.OutputDirectoryMissing);

        var plan = Plan(request.SourceLogDirectory, request.Caps);
        if (plan.IncludedCount == 0)
            return new DiagnosticsExportResult(
                false, null, plan, null, DiagnosticsExportFailure.NoLogFiles);

        var context = await _collector.CollectAsync(cancellationToken).ConfigureAwait(false);

        // No File.Exists pre-check: CreateNew refusing an existing target is ATOMIC, so it also closes the
        // window a check-then-write leaves open between two exports minted in the same second.
        FileStream zipStream;
        try
        {
            zipStream = new FileStream(
                request.OutputZipPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        }
        catch (IOException) when (File.Exists(request.OutputZipPath))
        {
            // Something is already there, so there is nothing of ours to clean up — and deleting it would
            // destroy a file this export did not write.
            _logger.LogWarning(
                "Diagnostics export refused: an archive from the same second already exists");
            return Failed(DiagnosticsExportFailure.OutputAlreadyExists, plan);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Diagnostics export could not create the archive");
            return Failed(DiagnosticsExportFailure.WriteFailed, plan);
        }

        try
        {
            var summary = Write(zipStream, request, plan, context, cancellationToken);
            _logger.LogInformation(
                "Diagnostics export wrote {Included} log file(s), dropped {Dropped} debug record(s)",
                plan.IncludedCount, summary.RecordsDropped);
            return new DiagnosticsExportResult(true, request.OutputZipPath, plan, summary, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Never leave a half-written archive where the user is about to be shown its folder.
            DeleteQuietly(request.OutputZipPath);
            _logger.LogError(ex, "Diagnostics export failed while writing the archive");
            return Failed(DiagnosticsExportFailure.WriteFailed, plan);
        }
        catch (OperationCanceledException)
        {
            DeleteQuietly(request.OutputZipPath);
            throw;
        }
    }

    private static RedactionSummary Write(
        FileStream zipStream, DiagnosticsExportRequest request, DiagnosticsExportPlan plan,
        DiagnosticsExportContext context, CancellationToken cancellationToken)
    {
        var totals = new Dictionary<string, long>(
            LogRedactor.RuleIds.ToDictionary(id => id, _ => 0L), StringComparer.Ordinal);
        long linesRead = 0, linesWritten = 0, recordsDropped = 0;
        var openFailed = new List<string>();

        using (zipStream)
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create))
        {
            foreach (var file in plan.Files.Where(f => f.Included))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourcePath = Path.Combine(request.SourceLogDirectory, file.FileName);

                FileStream source;
                try
                {
                    // The sink holds today's file open for writing, and FileShare.Read on this side would
                    // be refused for it. This is the one line that makes exporting a live log possible.
                    source = new FileStream(
                        sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    openFailed.Add(file.FileName);
                    continue;
                }

                using (source)
                using (var entry = archive
                           .CreateEntry(LogEntryPrefix + file.FileName, CompressionLevel.Optimal)
                           .Open())
                {
                    var summary = LogRedactor.Redact(source, entry, context.Keys);
                    linesRead += summary.LinesRead;
                    linesWritten += summary.LinesWritten;
                    recordsDropped += summary.RecordsDropped;
                    foreach (var (id, count) in summary.HitsByRuleId)
                        totals[id] += count;
                }
            }

            var written = new RedactionSummary(linesRead, linesWritten, recordsDropped, totals);
            var manifest = ApplyOpenFailures(plan, openFailed);

            WriteText(archive, "README.txt", BuildReadme(manifest, written));
            WriteText(archive, "manifest.json", JsonSerializer.Serialize(manifest, Json));
            WriteText(archive, "environment.json", JsonSerializer.Serialize(
                new ExportEnvironmentDocument(
                    context.Environment,
                    [.. LogRedactor.Descriptors.Select(d => new AppliedRule(
                        d.Id, d.Tier.ToString(), d.Covers, written.HitsByRuleId[d.Id]))]),
                Json));

            return written;
        }
    }

    private static DiagnosticsExportPlan ApplyOpenFailures(
        DiagnosticsExportPlan plan, IReadOnlyCollection<string> openFailed)
    {
        if (openFailed.Count == 0)
            return plan;

        var files = plan.Files
            .Select(f => openFailed.Contains(f.FileName)
                ? f with { Included = false, ExclusionReason = DiagnosticsExclusionReason.OpenFailed }
                : f)
            .ToList();
        var included = files.Count(f => f.Included);
        return plan with
        {
            Files = files,
            IncludedCount = included,
            ExcludedCount = files.Count - included,
        };
    }

    private static void WriteText(ZipArchive archive, string entryName, string content)
    {
        using var stream = archive.CreateEntry(entryName, CompressionLevel.Optimal).Open();
        using var writer = new StreamWriter(
            stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { NewLine = "\r\n" };
        writer.Write(content);
    }

    // Deliberately English and deliberately not localized: its reader is whoever receives the archive.
    private static string BuildReadme(DiagnosticsExportPlan plan, RedactionSummary summary)
    {
        var text = new StringBuilder();
        text.Append("Pia diagnostics export\r\n")
            .Append("======================\r\n\r\n")
            .Append("Contents: this file, manifest.json, environment.json, and logs/ - the app's own log\r\n")
            .Append("files, redacted. Nothing else. No chat transcripts, no vault content, no history\r\n")
            .Append("database, no settings file and no provider credentials are in this archive.\r\n\r\n")
            .Append("manifest.json names every pia*.log found next to the ones exported, so a file left out\r\n")
            .Append("is visible from in here rather than simply absent.\r\n\r\n")
            .Append(CultureInfo.InvariantCulture, $"Log files included: {plan.IncludedCount}\r\n")
            .Append(CultureInfo.InvariantCulture, $"Log files listed but excluded: {plan.ExcludedCount}\r\n")
            .Append(CultureInfo.InvariantCulture, $"Lines read: {summary.LinesRead}\r\n")
            .Append(CultureInfo.InvariantCulture, $"Lines written: {summary.LinesWritten}\r\n")
            .Append(CultureInfo.InvariantCulture,
                $"Debug records whose body was dropped: {summary.RecordsDropped}\r\n\r\n")
            .Append("Redaction\r\n---------\r\n")
            .Append("Two tiers, both listed with their hit counts in environment.json.\r\n\r\n")
            .Append("DETERMINISTIC rules substitute a value read from this machine - the profile paths, the\r\n")
            .Append("machine name, the account name, configured hosts, provider names. They are exact.\r\n\r\n")
            .Append("BEST-EFFORT rules match a shape - URLs, emails, tokens, leftover absolute paths, and\r\n")
            .Append("free-form text quoted into an exception message. They will lose to content built to\r\n")
            .Append("defeat them, and that is accepted: this archive is written to your disk and never sent\r\n")
            .Append("anywhere by Pia, so you are the last check before it goes to anyone.\r\n\r\n")
            .Append("Every DBUG and TRCE message body is replaced with <debug-payload-dropped> wholesale,\r\n")
            .Append("because that is where a debug build writes user content.\r\n\r\n")
            .Append("A <host-NNN> code is stable for the same host, so repeated failures can still be\r\n")
            .Append("correlated without naming it.\r\n\r\n")
            .Append("Not redacted, by decision: run, chat and step identifiers; tool names and approval\r\n")
            .Append("decisions; log category names; and exception text that names nothing the rules key on.\r\n");
        return text.ToString();
    }

    private bool IsInsideSourceDirectory(DiagnosticsExportRequest request)
    {
        var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.SourceLogDirectory));
        var output = Path.GetFullPath(request.OutputZipPath);
        var comparison = StringComparison.OrdinalIgnoreCase;

        if (!output.StartsWith(source, comparison))
            return false;

        var rest = output[source.Length..];
        var inside = rest.Length > 0 && (rest[0] == Path.DirectorySeparatorChar
            || rest[0] == Path.AltDirectorySeparatorChar);
        if (inside)
            _logger.LogWarning("Diagnostics export refused: the target sits inside the log directory");
        return inside;
    }

    private static DiagnosticsExportResult Failed(
        DiagnosticsExportFailure failure, DiagnosticsExportPlan? plan = null) =>
        new(false, null, plan ?? DiagnosticsExportPlan.Empty, null, failure);

    private static void DeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The partial archive is already the failure being reported; nothing useful to add.
        }
    }

    private sealed record AppliedRule(string Id, string Tier, string Covers, long Hits);

    private sealed record ExportEnvironmentDocument(
        DiagnosticsEnvironment Environment, IReadOnlyList<AppliedRule> RedactionRulesApplied)
    {
        public int SchemaVersion => DiagnosticsExportService.SchemaVersion;
    }
}
