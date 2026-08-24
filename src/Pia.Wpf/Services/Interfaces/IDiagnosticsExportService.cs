using Pia.Services.Diagnostics;

namespace Pia.Services.Interfaces;

public interface IDiagnosticsExportService
{
    /// <summary>
    /// Metadata only — no reads, no redaction, and never throws for a missing directory. The consent
    /// dialog needs it before anything is written.
    /// </summary>
    DiagnosticsExportPlan Plan(string sourceLogDirectory, DiagnosticsExportCaps caps);

    Task<DiagnosticsExportResult> ExportAsync(
        DiagnosticsExportRequest request, CancellationToken cancellationToken);
}

public interface IDiagnosticsEnvironmentCollector
{
    Task<DiagnosticsExportContext> CollectAsync(CancellationToken cancellationToken);
}
