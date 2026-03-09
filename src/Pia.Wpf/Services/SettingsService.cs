using Microsoft.Extensions.Logging;
using Pia.Models;
using Pia.Services.Interfaces;

namespace Pia.Services;

public class SettingsService : JsonPersistenceService<AppSettings>, ISettingsService
{
    private readonly ILogger<SettingsService> _logger;

    public event EventHandler<AppSettings>? SettingsChanged;

    protected override string FileName => "settings.json";

    protected override AppSettings CreateDefault() => new AppSettings();

    public SettingsService(ILogger<SettingsService> logger)
    {
        _logger = logger;
    }

    public async Task<AppSettings> GetSettingsAsync()
    {
        try
        {
            var settings = await LoadAsync(true);
            settings.MigrateFromLegacyDefault();
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
