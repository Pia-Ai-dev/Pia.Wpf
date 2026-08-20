using Pia.Models;

namespace Pia.Services.Interfaces;

public interface IPolicyService
{
    /// <summary>
    /// Loads and returns the enterprise policy settings.
    /// Returns an empty policy if no policy file exists.
    /// </summary>
    Task<PolicySettings> GetPolicyAsync();

    /// <summary>
    /// Returns true if the given AppSettings property is enforced by enterprise policy.
    /// </summary>
    bool IsEnforced(string propertyName);

    /// <summary>
    /// Applies the enterprise policy to user settings:
    /// - Sets default values for properties that still have their built-in default
    /// - Overwrites enforced properties unconditionally
    /// </summary>
    void ApplyPolicy(AppSettings userSettings);

    /// <summary>
    /// Returns true if the given login provider name is permitted by the
    /// <see cref="AppSettings.AllowedSyncProviders"/> allow-list (defaults +
    /// enforce). When no allow-list is configured by policy, all providers are allowed.
    /// </summary>
    bool IsLoginProviderAllowed(string provider);

    /// <summary>
    /// Stores the pull's authoritative document in the cache file only. A live re-apply would move values
    /// while every enforcement getter still reported the old lock state, so a changed pin waits for restart.
    /// </summary>
    Task ReplaceServerPolicyAsync(string document);

    /// <summary>
    /// Drops the cached document and the applied-defaults record, so a later user's values count as their own.
    /// </summary>
    Task ClearServerPolicyAsync();
}
