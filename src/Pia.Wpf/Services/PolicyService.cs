using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Pia.Models;
using Pia.Services.Interfaces;

namespace Pia.Services;

public class PolicyService : IPolicyService
{
    private const string PolicyFileName = "policy.json";

    private static readonly string FallbackPolicyDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Pia.Wpf");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly ILogger<PolicyService> _logger;
    private readonly string _policyFilePath;
    private PolicySettings? _cached;
    private HashSet<string>? _enforcedProperties;

    public PolicyService(ILogger<PolicyService> logger)
        : this(logger, ResolvePolicyFilePath(AppContext.BaseDirectory, FallbackPolicyDirectory))
    {
    }

    public PolicyService(ILogger<PolicyService> logger, string policyFilePath)
    {
        _logger = logger;
        _policyFilePath = policyFilePath;
    }

    // Primary: policy.json next to the running exe (in production: %ProgramFiles%\Pia.Wpf).
    // Fallback: %ProgramData%\Pia.Wpf\policy.json (legacy machine-wide location).
    public static string ResolvePolicyFilePath(string primaryDirectory, string fallbackDirectory)
    {
        var primary = Path.Combine(primaryDirectory, PolicyFileName);
        if (File.Exists(primary))
            return primary;
        return Path.Combine(fallbackDirectory, PolicyFileName);
    }

    public async Task<PolicySettings> GetPolicyAsync()
    {
        if (_cached is not null)
            return _cached;

        _cached = await LoadPolicyAsync();
        _enforcedProperties = BuildEnforcedSet(_cached.Enforce);
        return _cached;
    }

    public bool IsEnforced(string propertyName)
    {
        if (_enforcedProperties is null)
        {
            // Policy not loaded yet — load synchronously from cache or return false
            if (_cached is null)
                return false;

            _enforcedProperties = BuildEnforcedSet(_cached.Enforce);
        }

        return _enforcedProperties.Contains(propertyName);
    }

    public bool IsLoginProviderAllowed(string provider)
    {
        if (string.IsNullOrEmpty(provider))
            return false;

        var allowList = _cached?.Enforce?.AllowedSyncProviders
            ?? _cached?.Defaults?.AllowedSyncProviders;

        if (allowList is null || allowList.Count == 0)
            return true;

        return allowList.Any(p => string.Equals(p, provider, StringComparison.OrdinalIgnoreCase));
    }

    public void ApplyPolicy(AppSettings userSettings)
    {
        if (_cached is null)
            return;

        // Apply defaults: set values only where the user setting matches the built-in default
        if (_cached.Defaults is not null)
        {
            var builtInDefaults = new AppSettings();
            ApplyDefaults(userSettings, _cached.Defaults, builtInDefaults);
        }

        // Apply enforced values: always overwrite
        if (_cached.Enforce is not null)
        {
            ApplyEnforced(userSettings, _cached.Enforce);
        }
    }

    private async Task<PolicySettings> LoadPolicyAsync()
    {
        if (!File.Exists(_policyFilePath))
        {
            _logger.LogDebug("No enterprise policy file found at {Path}", _policyFilePath);
            return new PolicySettings();
        }

        try
        {
            var json = await File.ReadAllTextAsync(_policyFilePath);
            var policy = JsonSerializer.Deserialize<PolicySettings>(json, JsonOptions);
            _logger.LogInformation("Loaded enterprise policy from {Path}", _policyFilePath);
            return policy ?? new PolicySettings();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load enterprise policy from {Path}, ignoring", _policyFilePath);
            return new PolicySettings();
        }
    }

    private static HashSet<string> BuildEnforcedSet(AppSettings? enforce)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (enforce is null)
            return set;

        var builtInDefaults = new AppSettings();

        foreach (var prop in typeof(AppSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!prop.CanRead)
                continue;

            var enforcedValue = prop.GetValue(enforce);
            var defaultValue = prop.GetValue(builtInDefaults);

            if (!Equals(enforcedValue, defaultValue))
            {
                set.Add(prop.Name);
            }
        }

        return set;
    }

    private static void ApplyDefaults(AppSettings userSettings, AppSettings policyDefaults, AppSettings builtInDefaults)
    {
        foreach (var prop in typeof(AppSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!prop.CanRead || !prop.CanWrite)
                continue;

            var policyDefaultValue = prop.GetValue(policyDefaults);
            var builtInDefaultValue = prop.GetValue(builtInDefaults);

            // Only apply the policy default if it differs from built-in default
            // AND the user's current value still matches the built-in default
            if (!Equals(policyDefaultValue, builtInDefaultValue))
            {
                var userValue = prop.GetValue(userSettings);
                if (Equals(userValue, builtInDefaultValue))
                {
                    prop.SetValue(userSettings, policyDefaultValue);
                }
            }
        }
    }

    private static void ApplyEnforced(AppSettings userSettings, AppSettings policyEnforce)
    {
        var builtInDefaults = new AppSettings();

        foreach (var prop in typeof(AppSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!prop.CanRead || !prop.CanWrite)
                continue;

            var enforcedValue = prop.GetValue(policyEnforce);
            var defaultValue = prop.GetValue(builtInDefaults);

            // Only enforce properties that are explicitly set in the policy
            // (i.e., differ from the built-in default)
            if (!Equals(enforcedValue, defaultValue))
            {
                prop.SetValue(userSettings, enforcedValue);
            }
        }
    }
}
