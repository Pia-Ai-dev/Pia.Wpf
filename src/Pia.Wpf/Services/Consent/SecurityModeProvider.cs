using Microsoft.Extensions.Logging;
using Pia.Models;
using Pia.Services.Interfaces;

namespace Pia.Services.Consent;

public sealed class SecurityModeProvider : ISecurityModeProvider, IDisposable
{
    private readonly ISettingsService _settingsService;
    private readonly ILogger<SecurityModeProvider> _logger;
    private readonly object _lock = new();
    private SecurityProfile _current = SecurityProfile.Standard;

    public SecurityProfile Current
    {
        get { lock (_lock) return _current; }
    }

    public event EventHandler<SecurityProfileChangedEventArgs>? ProfileChanged;

    public SecurityModeProvider(ISettingsService settingsService, ILogger<SecurityModeProvider> logger)
    {
        _settingsService = settingsService;
        _logger = logger;
        _settingsService.SettingsChanged += OnSettingsChanged;
        _ = InitialiseAsync();
    }

    private async Task InitialiseAsync()
    {
        try
        {
            var settings = await _settingsService.GetSettingsAsync().ConfigureAwait(false);
            ApplyMode(settings.SecurityMode);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load initial security mode; staying on Standard");
        }
    }

    public async Task SetModeAsync(SecurityMode mode, CancellationToken cancellationToken = default)
    {
        var settings = await _settingsService.GetSettingsAsync().ConfigureAwait(false);
        if (settings.SecurityMode == mode)
        {
            ApplyMode(mode);
            return;
        }
        settings.SecurityMode = mode;
        await _settingsService.SaveSettingsAsync(settings).ConfigureAwait(false);
        // SettingsChanged handler will ApplyMode and raise ProfileChanged.
    }

    private void OnSettingsChanged(object? sender, Pia.Models.AppSettings settings)
    {
        ApplyMode(settings.SecurityMode);
    }

    private void ApplyMode(SecurityMode mode)
    {
        var newProfile = SecurityProfile.ForMode(mode);
        SecurityProfile? oldProfile = null;
        lock (_lock)
        {
            if (_current.Mode == newProfile.Mode) return;
            oldProfile = _current;
            _current = newProfile;
        }
        _logger.LogInformation("Security profile changed: {Old} -> {New}", oldProfile.Mode, newProfile.Mode);
        try { ProfileChanged?.Invoke(this, new SecurityProfileChangedEventArgs(oldProfile, newProfile)); }
        catch (Exception ex) { _logger.LogError(ex, "ProfileChanged subscriber threw"); }
    }

    public void Dispose()
    {
        _settingsService.SettingsChanged -= OnSettingsChanged;
    }
}
