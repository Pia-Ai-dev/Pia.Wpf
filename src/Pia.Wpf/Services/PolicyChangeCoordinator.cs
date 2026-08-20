using Microsoft.Extensions.Logging;
using Pia.Helpers;
using Pia.Models;
using Pia.Services.Interfaces;

namespace Pia.Services;

/// <summary>The sole <see cref="IPolicyService.PolicyChanged"/> subscriber: it moves the changed values
/// into the shared settings and only then notifies, so the order cannot depend on subscription order.</summary>
public sealed class PolicyChangeCoordinator
{
    // Privacy alone: every other key a server policy can set either applies live or is only read at the next start.
    internal static readonly IReadOnlySet<string> RestartRequiredKeys =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { nameof(AppSettings.Privacy) };

    private readonly IPolicyService _policyService;
    private readonly ISettingsService _settingsService;
    private readonly ILogger<PolicyChangeCoordinator> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public PolicyChangeCoordinator(
        IPolicyService policyService,
        ISettingsService settingsService,
        ILogger<PolicyChangeCoordinator> logger)
    {
        _policyService = policyService;
        _settingsService = settingsService;
        _logger = logger;
        _policyService.PolicyChanged += OnPolicyChanged;
    }

    internal Task InFlightApply { get; private set; } = Task.CompletedTask;

    private void OnPolicyChanged(object? sender, PolicyChangedEventArgs e)
    {
        // The logout path can raise this from the UI thread, where waiting would deadlock the dispatcher.
        var apply = ApplyAsync(e);
        InFlightApply = apply;
        apply.SafeFireAndForget(_logger);
    }

    private async Task ApplyAsync(PolicyChangedEventArgs change)
    {
        // Two changes in flight would interleave their Get/Save pairs.
        await _gate.WaitAsync();
        try
        {
            AppSettings? applied = null;

            // Own catch: a failed write must not also swallow the lock refresh and the restart flag.
            try
            {
                // The Get already applies the policy to the shared instance; only the Save raises SettingsChanged.
                applied = await _settingsService.GetSettingsAsync();
                await _settingsService.SaveSettingsAsync(applied);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to apply a changed policy to the settings");
            }

            // After the values: the other order greys out a value the user can still see.
            try
            {
                _policyService.NotifyLocksChanged();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "A policy-lock subscriber threw");
            }

            if (applied is { } settings && change.ValuesChanged.Any(key => RequiresRestart(key, settings)))
                _policyService.SetRestartRequired();
        }
        finally
        {
            _gate.Release();
        }
    }

    // Decided on the applied value, not on enforcement: a pin the user's value already matches moves
    // nothing, and a changed default that does move one is never enforced.
    private static bool RequiresRestart(string key, AppSettings settings) =>
        RestartRequiredKeys.Contains(key) && TokenizationLatch.IsStale(settings.Privacy);
}
