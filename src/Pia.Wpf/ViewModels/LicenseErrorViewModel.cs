using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Pia.Services.Interfaces;
using Pia.Shared.Licensing;

namespace Pia.ViewModels;

/// <summary>
/// Singleton listener that turns <see cref="ILicenseErrorBus"/> events into
/// user-visible toasts and triggers graceful degradation (e.g. stopping sync
/// when the server's license lacks the feature). Constructed at startup and
/// held for the lifetime of the app.
/// </summary>
public partial class LicenseErrorViewModel : ObservableObject
{
    private static readonly TimeSpan DedupeWindow = TimeSpan.FromSeconds(5);

    private readonly INotificationService _notifications;
    private readonly ISyncClientService _syncClient;
    private readonly ILogger<LicenseErrorViewModel> _logger;
    private readonly SynchronizationContext? _uiContext;

    private readonly object _dedupeGate = new();
    private string? _lastKey;
    private DateTime _lastAt = DateTime.MinValue;

    public LicenseErrorViewModel(
        ILicenseErrorBus bus,
        INotificationService notifications,
        ISyncClientService syncClient,
        ILogger<LicenseErrorViewModel> logger)
    {
        _notifications = notifications;
        _syncClient = syncClient;
        _logger = logger;
        _uiContext = SynchronizationContext.Current;
        bus.OnLicenseError += OnLicenseError;
    }

    private void OnLicenseError(object? sender, LicenseErrorResponse error)
    {
        if (!ShouldNotify(DedupeKey(error))) return;

        var message = BuildMessage(error);
        _logger.LogWarning("License error received from server: {Error} {Feature} {Limit}",
            error.Error, error.Feature, error.Limit);

        if (error is { Error: LicenseErrorKeys.FeatureNotLicensed, Feature: "Sync" })
        {
            _syncClient.DisableByLicense();
        }

        void Show() => _notifications.ShowError(message, durationMs: 8000);

        if (_uiContext is null) Show();
        else _uiContext.Post(_ => Show(), null);
    }

    private static string DedupeKey(LicenseErrorResponse error) =>
        error.Error == LicenseErrorKeys.FeatureNotLicensed
            ? $"{error.Error}:{error.Feature}"
            : error.Error;

    private bool ShouldNotify(string key)
    {
        var now = DateTime.UtcNow;
        lock (_dedupeGate)
        {
            if (_lastKey == key && now - _lastAt < DedupeWindow) return false;
            _lastKey = key;
            _lastAt = now;
            return true;
        }
    }

    private static string BuildMessage(LicenseErrorResponse error) => error.Error switch
    {
        LicenseErrorKeys.NoLicense =>
            string.IsNullOrWhiteSpace(error.SetupUrl)
                ? "This server hasn't been activated. Ask your admin to activate it."
                : $"This server hasn't been activated. Ask your admin to visit {error.SetupUrl}.",
        LicenseErrorKeys.FeatureNotLicensed when error.Feature == "Sync" =>
            "Sync is disabled on this server's license.",
        LicenseErrorKeys.FeatureNotLicensed =>
            $"The '{error.Feature}' feature is not enabled on this server's license.",
        LicenseErrorKeys.UserLimitReached when error.Limit.HasValue =>
            $"This server is full ({error.Limit} users maximum). Contact your admin.",
        LicenseErrorKeys.UserLimitReached =>
            "This server is full. Contact your admin.",
        _ => error.Message ?? "The server rejected the request due to its license."
    };
}
