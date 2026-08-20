using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
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

    // Ordinal on purpose: this must match the deserializer, which is camelCase and case-SENSITIVE
    // (PropertyNameCaseInsensitive is off). A looser match here would mark a key present whose typed
    // value never got populated, and the enforce pass would then write a built-in default over the
    // user's setting.
    private static readonly Dictionary<string, PropertyInfo> PropertiesByJsonName =
        SettableProperties().ToDictionary(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name), StringComparer.Ordinal);

    private static readonly Dictionary<string, PropertyInfo> PropertiesByName =
        SettableProperties().ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);

    private readonly ILogger<PolicyService> _logger;
    private readonly string[] _candidatePaths;
    private PolicySettings? _cached;

    // The keys the admin actually wrote, per section. Presence in the file is what "set" means —
    // never a comparison against the built-in default, which cannot distinguish "absent" from
    // "deliberately set to the default" and is reference equality for every collection.
    private HashSet<string> _enforcedProperties = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _defaultedProperties = new(StringComparer.OrdinalIgnoreCase);

    public PolicyService(ILogger<PolicyService> logger)
        : this(logger, GetDefaultCandidatePaths())
    {
    }

    public PolicyService(ILogger<PolicyService> logger, string policyFilePath)
        : this(logger, new[] { policyFilePath })
    {
    }

    private PolicyService(ILogger<PolicyService> logger, string[] candidatePaths)
    {
        _logger = logger;
        _candidatePaths = candidatePaths;
    }

    private static IEnumerable<PropertyInfo> SettableProperties() =>
        typeof(AppSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite);

    // Search order:
    //   1. Next to the running exe (AppContext.BaseDirectory) — dev runs and simple deployments.
    //   2. Parent of (1) — Velopack installs the running exe under <install>\current\, but
    //      admins drop policy.json next to the visible launcher stub at the install root.
    //   3. %ProgramData%\Pia.Wpf — legacy machine-wide fallback.
    private static string[] GetDefaultCandidatePaths()
    {
        var exeDir = AppContext.BaseDirectory;
        var parentDir = new DirectoryInfo(exeDir).Parent?.FullName;

        var paths = new List<string> { Path.Combine(exeDir, PolicyFileName) };
        if (parentDir is not null && !string.Equals(parentDir, exeDir, StringComparison.OrdinalIgnoreCase))
            paths.Add(Path.Combine(parentDir, PolicyFileName));
        paths.Add(Path.Combine(FallbackPolicyDirectory, PolicyFileName));
        return paths.ToArray();
    }

    public static string ResolvePolicyFilePath(params string[] candidateDirectories)
    {
        if (candidateDirectories.Length == 0)
            throw new ArgumentException("At least one candidate directory is required", nameof(candidateDirectories));

        return candidateDirectories.Select(d => Path.Combine(d, PolicyFileName)).FirstOrDefault(File.Exists)
            ?? Path.Combine(candidateDirectories[^1], PolicyFileName);
    }

    public async Task<PolicySettings> GetPolicyAsync()
    {
        if (_cached is not null)
            return _cached;

        _cached = await LoadPolicyAsync();
        return _cached;
    }

    public bool IsEnforced(string propertyName) => _enforcedProperties.Contains(propertyName);

    public bool IsLoginProviderAllowed(string provider)
    {
        if (string.IsNullOrEmpty(provider))
            return false;

        var allowList = _enforcedProperties.Contains(nameof(AppSettings.AllowedSyncProviders))
            ? _cached?.Enforce?.AllowedSyncProviders
            : _defaultedProperties.Contains(nameof(AppSettings.AllowedSyncProviders))
                ? _cached?.Defaults?.AllowedSyncProviders
                : null;

        if (allowList is null || allowList.Count == 0)
            return true;

        return allowList.Any(p => string.Equals(p, provider, StringComparison.OrdinalIgnoreCase));
    }

    public void ApplyPolicy(AppSettings userSettings)
    {
        if (_cached is null)
            return;

        // Defaults are a recommendation: they land only while the user is still sitting on the
        // built-in value, so an explicit user choice always survives.
        if (_cached.Defaults is { } defaults && _defaultedProperties.Count > 0)
        {
            var builtIn = new AppSettings();
            foreach (var name in _defaultedProperties)
            {
                if (!PropertiesByName.TryGetValue(name, out var prop))
                    continue;

                if (MatchesBuiltInDefault(prop, prop.GetValue(userSettings), prop.GetValue(builtIn)))
                    prop.SetValue(userSettings, prop.GetValue(defaults));
            }
        }

        if (_cached.Enforce is { } enforce && _enforcedProperties.Count > 0)
        {
            foreach (var name in _enforcedProperties)
            {
                if (PropertiesByName.TryGetValue(name, out var prop))
                    prop.SetValue(userSettings, prop.GetValue(enforce));
            }
        }
    }

    /// <summary>Value comparison that also works for the collection- and object-typed settings, whose
    /// built-in default is a fresh instance and therefore never reference-equal.</summary>
    private static bool MatchesBuiltInDefault(PropertyInfo prop, object? userValue, object? builtInValue)
    {
        if (Equals(userValue, builtInValue))
            return true;
        if (userValue is null || builtInValue is null)
            return false;

        try
        {
            return JsonSerializer.Serialize(userValue, prop.PropertyType, JsonOptions)
                == JsonSerializer.Serialize(builtInValue, prop.PropertyType, JsonOptions);
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    private async Task<PolicySettings> LoadPolicyAsync()
    {
        var path = _candidatePaths.FirstOrDefault(File.Exists);
        if (path is null)
        {
            _logger.LogInformation("No enterprise policy file found. Searched: {Paths}", string.Join("; ", _candidatePaths));
            return new PolicySettings();
        }

        try
        {
            var json = await File.ReadAllTextAsync(path);
            var policy = JsonSerializer.Deserialize<PolicySettings>(json, JsonOptions) ?? new PolicySettings();

            var root = JsonNode.Parse(json) as JsonObject;
            _defaultedProperties = ReadPresentKeys(root, "defaults");
            _enforcedProperties = ReadPresentKeys(root, "enforce");

            _logger.LogInformation(
                "Loaded enterprise policy from {Path}: {DefaultCount} default(s), {EnforcedCount} enforced",
                path, _defaultedProperties.Count, _enforcedProperties.Count);
            return policy;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load enterprise policy from {Path}, ignoring", path);
            return new PolicySettings();
        }
    }

    private HashSet<string> ReadPresentKeys(JsonObject? root, string sectionName)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (root is null || root[sectionName] is not JsonObject section)
            return set;

        foreach (var (key, _) in section)
        {
            if (PropertiesByJsonName.TryGetValue(key, out var prop))
                set.Add(prop.Name);
            else
                _logger.LogWarning("Policy key {Section}.{Key} matches no setting and was ignored", sectionName, key);
        }

        return set;
    }
}
