using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Pia.Logging;
using Pia.Paths;
using Pia.Services.Interfaces;

namespace Pia.Services.Diagnostics;

/// <summary>
/// Builds the two things an export needs from the live app: the allow-listed environment summary, and the
/// values the deterministic redaction tier keys on. Provider NAMES are collected only as redaction keys and
/// never reach the summary — a provider name is user-chosen text.
/// </summary>
public sealed class DiagnosticsEnvironmentCollector : IDiagnosticsEnvironmentCollector
{
    private const int SchemaVersion = 1;

    private readonly ILogger<DiagnosticsEnvironmentCollector> _logger;
    private readonly ISettingsService _settingsService;
    private readonly IProviderService _providerService;
    private readonly IUpdateService _updateService;

    public DiagnosticsEnvironmentCollector(
        ILogger<DiagnosticsEnvironmentCollector> logger,
        ISettingsService settingsService,
        IProviderService providerService,
        IUpdateService updateService)
    {
        _logger = logger;
        _settingsService = settingsService;
        _providerService = providerService;
        _updateService = updateService;
    }

    public async Task<DiagnosticsExportContext> CollectAsync(CancellationToken cancellationToken)
    {
        var settings = await _settingsService.GetSettingsAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var providers = await ReadProvidersAsync().ConfigureAwait(false);

        var hosts = providers
            .Select(p => p.Endpoint)
            .Append(settings.ServerUrl)
            .Select(HostOf)
            .Where(h => h is not null)
            .Select(h => h!)
            .ToList();

        var environment = new DiagnosticsEnvironment(
            SchemaVersion,
            DateTimeOffset.UtcNow,
            _updateService.CurrentVersion
                ?? typeof(DiagnosticsEnvironmentCollector).Assembly.GetName().Version?.ToString()
                ?? "unknown",
            RuntimeInformation.OSDescription,
            RuntimeInformation.OSArchitecture.ToString(),
            RuntimeInformation.ProcessArchitecture.ToString(),
            RuntimeInformation.FrameworkDescription,
            settings.UiLanguage.ToString(),
            SafeLog.SensitiveLoggingCompiledIn,
            PiaPaths.IsOverridden,
            providers
                .GroupBy(p => p.ProviderType.ToString())
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
            providers.Count);

        var keys = new RedactionKeys(
            PiaPaths.RoamingDataDirectory,
            PiaPaths.LocalDataDirectory,
            // Deliberately the REAL profile even under PIA_DATA_DIR: the log to be scrubbed may predate the
            // override, and this value is a redaction key rather than a data path.
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.MachineName,
            Environment.UserName,
            hosts,
            [.. providers.Select(p => p.Name)]);

        return new DiagnosticsExportContext(environment, keys);
    }

    private async Task<IReadOnlyList<Models.AiProvider>> ReadProvidersAsync()
    {
        try
        {
            return await _providerService.GetProvidersAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // An unreadable provider store must not block the export, but it does cost the export its
            // provider-name and host keys, so say so at a level the export itself will carry.
            _logger.LogWarning(ex, "Diagnostics export could not read providers; host keys will be empty");
            return [];
        }
    }

    private static string? HostOf(string? url) =>
        !string.IsNullOrWhiteSpace(url)
        && Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && !string.IsNullOrEmpty(uri.Host)
            ? uri.Host
            : null;
}
