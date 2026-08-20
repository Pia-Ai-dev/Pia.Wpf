using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Pia.Models;
using Pia.Services.Interfaces;
using Pia.Shared.Policy;

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

    // Refused from the server but still settable in the device file: they are how the client reaches a server.
    private static readonly HashSet<string> DeviceSettableDeniedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "serverUrl",
        "syncEnabled",
        "trustSelfSignedCertificates"
    };

    private readonly ILogger<PolicyService> _logger;
    private readonly string[] _candidatePaths;
    private readonly ClientPolicyCacheStore _cacheStore;
    private PolicySettings? _cached;
    private CachedClientPolicy? _cacheRecord;

    // The keys the admin actually wrote, per section. Presence in the file is what "set" means —
    // never a comparison against the built-in default, which cannot distinguish "absent" from
    // "deliberately set to the default" and is reference equality for every collection.
    private HashSet<string> _enforcedProperties = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _defaultedProperties = new(StringComparer.OrdinalIgnoreCase);

    public PolicyService(ILogger<PolicyService> logger)
        : this(logger, GetDefaultCandidatePaths(), null)
    {
    }

    public PolicyService(ILogger<PolicyService> logger, string policyFilePath)
        : this(logger, new[] { policyFilePath }, null)
    {
    }

    public PolicyService(ILogger<PolicyService> logger, string policyFilePath, string cacheDirectory)
        : this(logger, new[] { policyFilePath }, cacheDirectory)
    {
    }

    private PolicyService(ILogger<PolicyService> logger, string[] candidatePaths, string? cacheDirectory)
    {
        _logger = logger;
        _candidatePaths = candidatePaths;
        _cacheStore = new ClientPolicyCacheStore(cacheDirectory);
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

        if (_cached.Defaults is { } defaults && _defaultedProperties.Count > 0)
            ApplyDefaults(userSettings, defaults);

        if (_cached.Enforce is { } enforce && _enforcedProperties.Count > 0)
        {
            foreach (var name in _enforcedProperties)
            {
                if (PropertiesByName.TryGetValue(name, out var prop))
                    prop.SetValue(userSettings, prop.GetValue(enforce));
            }
        }
    }

    public async Task ReplaceServerPolicyAsync(string document)
    {
        var normalized = ClientPolicyContract.Normalize(document);
        if (normalized is not null && !ClientPolicyContract.TryValidate(normalized, out var error))
            _logger.LogWarning("Server policy document is malformed, caching it anyway: {Error}", error);

        var record = await _cacheStore.GetAsync();
        record.Document = normalized ?? ClientPolicyContract.EmptyDocument;
        _cacheRecord = record;
        await _cacheStore.SetAsync(record);

        _logger.LogInformation("Server policy document cached: {Length} char(s)", record.Document.Length);
    }

    public Task ClearServerPolicyAsync()
    {
        try
        {
            _cacheStore.Delete();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete the cached server policy");
        }

        _cacheRecord = null;
        _logger.LogInformation("Cached server policy cleared");
        return Task.CompletedTask;
    }

    /// <summary>Re-applies a changed default unless the user has changed the value themselves.</summary>
    private void ApplyDefaults(AppSettings userSettings, AppSettings defaults)
    {
        var builtIn = new AppSettings();
        var applied = _cacheRecord?.AppliedDefaults;
        var recordChanged = false;

        foreach (var name in _defaultedProperties)
        {
            if (!PropertiesByName.TryGetValue(name, out var prop))
                continue;

            var userValue = prop.GetValue(userSettings);
            if (!MatchesBuiltInDefault(prop, userValue, prop.GetValue(builtIn))
                && !MatchesAppliedDefault(prop, userValue, applied, name))
                continue;

            var policyValue = prop.GetValue(defaults);
            prop.SetValue(userSettings, policyValue);

            if (applied is null)
                continue;

            var serialized = SerializeValue(prop, policyValue);
            if (serialized is null || (applied.TryGetValue(name, out var previous) && previous == serialized))
                continue;

            applied[name] = serialized;
            recordChanged = true;
        }

        if (recordChanged)
            PersistCacheRecord();
    }

    /// <summary>Value comparison that also works for the collection- and object-typed settings, whose
    /// built-in default is a fresh instance and therefore never reference-equal.</summary>
    private static bool MatchesBuiltInDefault(PropertyInfo prop, object? userValue, object? builtInValue)
    {
        if (Equals(userValue, builtInValue))
            return true;
        if (userValue is null || builtInValue is null)
            return false;

        var user = SerializeValue(prop, userValue);
        return user is not null && user == SerializeValue(prop, builtInValue);
    }

    private static bool MatchesAppliedDefault(
        PropertyInfo prop, object? userValue, Dictionary<string, string>? applied, string name)
    {
        if (applied is null || !applied.TryGetValue(name, out var recorded))
            return false;

        return recorded == SerializeValue(prop, userValue);
    }

    private static string? SerializeValue(PropertyInfo prop, object? value)
    {
        try
        {
            return JsonSerializer.Serialize(value, prop.PropertyType, JsonOptions);
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    private void PersistCacheRecord()
    {
        if (_cacheRecord is null)
            return;

        try
        {
            _cacheStore.SaveNow(_cacheRecord);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to record the policy defaults that were applied");
        }
    }

    private async Task<PolicySettings> LoadPolicyAsync()
    {
        var file = await LoadFileLayerAsync();
        var server = await LoadServerLayerAsync();

        var defaulted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var enforced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var mergedDefaults = FlattenSection(null, file.Typed.Defaults, file.DefaultKeys, defaulted);
        mergedDefaults = FlattenSection(mergedDefaults, server.Typed.Defaults, server.DefaultKeys, defaulted);

        var mergedEnforce = FlattenSection(null, file.Typed.Enforce, file.EnforceKeys, enforced);
        mergedEnforce = FlattenSection(mergedEnforce, server.Typed.Enforce, server.EnforceKeys, enforced);

        _defaultedProperties = defaulted;
        _enforcedProperties = enforced;

        _logger.LogInformation(
            "Enterprise policy resolved: {DefaultCount} default(s), {EnforcedCount} enforced; {FileCount} key(s) from the policy file, {ServerCount} from the server document",
            defaulted.Count, enforced.Count,
            file.DefaultKeys.Count + file.EnforceKeys.Count,
            server.DefaultKeys.Count + server.EnforceKeys.Count);

        return new PolicySettings { Defaults = mergedDefaults, Enforce = mergedEnforce };
    }

    private static AppSettings? FlattenSection(
        AppSettings? merged, AppSettings? layer, HashSet<string> layerKeys, HashSet<string> mergedKeys)
    {
        if (layer is null || layerKeys.Count == 0)
            return merged;

        // Non-null once any layer set a key: IsLoginProviderAllowed reads it through a ?. chain and fails open.
        merged ??= new AppSettings();

        foreach (var name in layerKeys)
        {
            if (!PropertiesByName.TryGetValue(name, out var prop))
                continue;

            prop.SetValue(merged, prop.GetValue(layer));
            mergedKeys.Add(name);
        }

        return merged;
    }

    private async Task<(PolicySettings Typed, HashSet<string> DefaultKeys, HashSet<string> EnforceKeys)> LoadFileLayerAsync()
    {
        var path = _candidatePaths.FirstOrDefault(File.Exists);
        if (path is null)
        {
            _logger.LogInformation("No enterprise policy file found. Searched: {Paths}", string.Join("; ", _candidatePaths));
            return EmptyLayer();
        }

        try
        {
            var json = await File.ReadAllTextAsync(path);
            var layer = LoadLayer(json, allowDeviceSettableKeys: true);

            _logger.LogInformation(
                "Loaded enterprise policy from {Path}: {DefaultCount} default(s), {EnforcedCount} enforced",
                path, layer.DefaultKeys.Count, layer.EnforceKeys.Count);
            return layer;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load enterprise policy from {Path}, ignoring", path);
            return EmptyLayer();
        }
    }

    private async Task<(PolicySettings Typed, HashSet<string> DefaultKeys, HashSet<string> EnforceKeys)> LoadServerLayerAsync()
    {
        try
        {
            var record = await _cacheStore.GetAsync();
            _cacheRecord = record;

            var document = ClientPolicyContract.Normalize(record.Document);
            if (document is null)
                return EmptyLayer();

            return LoadLayer(document, allowDeviceSettableKeys: false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load the cached server policy, ignoring");
            return EmptyLayer();
        }
    }

    private (PolicySettings Typed, HashSet<string> DefaultKeys, HashSet<string> EnforceKeys) LoadLayer(
        string json, bool allowDeviceSettableKeys)
    {
        var typed = JsonSerializer.Deserialize<PolicySettings>(json, JsonOptions) ?? new PolicySettings();
        var root = JsonNode.Parse(json) as JsonObject;

        return (Typed: typed,
            DefaultKeys: ReadPresentKeys(root, ClientPolicyContract.DefaultsSection, allowDeviceSettableKeys),
            EnforceKeys: ReadPresentKeys(root, ClientPolicyContract.EnforceSection, allowDeviceSettableKeys));
    }

    private static (PolicySettings Typed, HashSet<string> DefaultKeys, HashSet<string> EnforceKeys) EmptyLayer() =>
        (new PolicySettings(),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    private HashSet<string> ReadPresentKeys(JsonObject? root, string sectionName, bool allowDeviceSettableKeys)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (root is null || root[sectionName] is not JsonObject section)
            return set;

        foreach (var (key, _) in section)
        {
            if (ClientPolicyContract.IsDenied(key)
                && !(allowDeviceSettableKeys && DeviceSettableDeniedKeys.Contains(key)))
            {
                _logger.LogWarning("Policy key {Section}.{Key} cannot be set from this source and was ignored", sectionName, key);
                continue;
            }

            if (PropertiesByJsonName.TryGetValue(key, out var prop))
                set.Add(prop.Name);
            else
                _logger.LogWarning("Policy key {Section}.{Key} matches no setting and was ignored", sectionName, key);
        }

        return set;
    }
}
