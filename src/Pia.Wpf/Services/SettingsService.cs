using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
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

            // Before LoadAsync, which populates the cache: the retired flag has to be read off the raw
            // file, and only the first load is guaranteed to precede every save this process makes.
            var retiredHistoryFlag = GetCached() is null ? await ReadRetiredHistoryFlagAsync() : null;

            var settings = await LoadAsync(true);
            settings.MigrateFromLegacyDefault();
            if (retiredHistoryFlag is { } historyWasEnabled)
                await MigrateRetiredHistoryFlagAsync(settings, historyWasEnabled);

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

    /// <summary>The retired chat-history flag, or null when the document no longer carries it. Read raw
    /// because <see cref="AppSettings"/> has no such property any more, so the deserializer drops it.</summary>
    private async Task<bool?> ReadRetiredHistoryFlagAsync()
    {
        try
        {
            if (!File.Exists(FilePath))
                return null;

            var document = JsonNode.Parse(await File.ReadAllTextAsync(FilePath));
            return document?["chatHistoryEnabled"]?.GetValue<bool>();
        }
        catch (Exception ex) when (ex is IOException or JsonException or FormatException
            or InvalidOperationException or NotSupportedException)
        {
            _logger.LogWarning(ex, "Could not read the retired chat-history flag; retention left as stored");
            return null;
        }
    }

    private async Task MigrateRetiredHistoryFlagAsync(AppSettings settings, bool historyWasEnabled)
    {
        // History off used to mean nothing was ever evicted, so these installs carry a backlog that the
        // now-unconditional sweep would cut at whatever stale window they stored. Raise, never lower: one
        // that chose a long window before switching history off must not lose more than it asked for.
        if (!historyWasEnabled)
            settings.ChatHistoryRetentionDays = Math.Max(
                settings.ChatHistoryRetentionDays, AppSettings.DefaultChatHistoryRetentionDays);

        // Rewrites the whole document without the retired key, so this runs once and never again.
        await SaveAsync(settings);

        _logger.LogInformation(
            "Retired the chat-history flag (was {Enabled}); retention window is {Days} days",
            historyWasEnabled, settings.ChatHistoryRetentionDays);
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
