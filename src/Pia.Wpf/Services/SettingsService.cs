using Microsoft.Extensions.Logging;
using Pia.Models;
using Pia.Services.Interfaces;

namespace Pia.Services;

public class SettingsService : JsonPersistenceService<AppSettings>, ISettingsService
{
    private readonly ILogger<SettingsService> _logger;
    private readonly IPolicyService _policyService;

    public event EventHandler<AppSettings>? SettingsChanged;

    protected override string FileName => "settings.json";

    protected override AppSettings CreateDefault() => new AppSettings();

    public SettingsService(ILogger<SettingsService> logger, IPolicyService policyService)
    {
        _logger = logger;
        _policyService = policyService;
    }

    public async Task<AppSettings> GetSettingsAsync()
    {
        try
        {
            // Ensure the policy is loaded before applying it
            await _policyService.GetPolicyAsync();

            var settings = await LoadAsync(true);
            settings.MigrateFromLegacyDefault();
            _policyService.ApplyPolicy(settings);
            return settings;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load settings, using default settings");
            return CreateDefault();
        }
    }

    public async Task SaveSettingsAsync(AppSettings settings)
    {
        // Re-apply enforced values before saving to prevent circumvention
        _policyService.ApplyPolicy(settings);
        await SaveAsync(settings);
        SettingsChanged?.Invoke(this, settings);
    }

    public async Task SaveDraftAsync(string? draftText)
    {
        var settings = await GetSettingsAsync();
        settings.DraftText = draftText;
        await SaveSettingsAsync(settings);
    }

    public async Task<string?> GetDraftAsync()
    {
        var settings = await GetSettingsAsync();
        return settings.DraftText;
    }
}
