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
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    private PolicySnapshot? _snapshot;

    // Assigned only where a snapshot is published: a baseline that advanced on a write the snapshot
    // never saw would strand the change for as long as the document stays the same.
    private string? _publishedDocument;

    private int _restartRequired;

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

    public event EventHandler<PolicyChangedEventArgs>? PolicyChanged;

    public event EventHandler? LocksChanged;

    public event EventHandler? RestartRequiredChanged;

    public bool IsRestartRequired => Volatile.Read(ref _restartRequired) != 0;

    public void NotifyLocksChanged() => LocksChanged?.Invoke(this, EventArgs.Empty);

    public void SetRestartRequired()
    {
        if (Interlocked.Exchange(ref _restartRequired, 1) == 0)
            RestartRequiredChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task<PolicySettings> GetPolicyAsync()
    {
        // Gate-free on purpose: a subscriber re-entering through GetSettingsAsync must not need the
        // semaphore, which is not reentrant.
        if (Volatile.Read(ref _snapshot) is { } cached)
            return cached.Merged;

        await _writeGate.WaitAsync();
        try
        {
            if (Volatile.Read(ref _snapshot) is { } loaded)
                return loaded.Merged;

            return (await LoadAndPublishAsync()).Merged;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public bool IsEnforced(string propertyName) =>
        Volatile.Read(ref _snapshot)?.Enforced.Contains(propertyName) == true;

    public bool IsLoginProviderAllowed(string provider)
    {
        if (string.IsNullOrEmpty(provider))
            return false;

        var snapshot = Volatile.Read(ref _snapshot);
        var allowList = snapshot is null
            ? null
            : snapshot.Enforced.Contains(nameof(AppSettings.AllowedSyncProviders))
                ? snapshot.Merged.Enforce?.AllowedSyncProviders
                : snapshot.Defaulted.Contains(nameof(AppSettings.AllowedSyncProviders))
                    ? snapshot.Merged.Defaults?.AllowedSyncProviders
                    : null;

        if (allowList is null || allowList.Count == 0)
            return true;

        return allowList.Any(p => string.Equals(p, provider, StringComparison.OrdinalIgnoreCase));
    }

    public void ApplyPolicy(AppSettings userSettings)
    {
        if (Volatile.Read(ref _snapshot) is not { } snapshot)
            return;

        if (snapshot.Merged.Defaults is { } defaults && snapshot.Defaulted.Count > 0)
            ApplyDefaults(snapshot, userSettings, defaults);

        if (snapshot.Merged.Enforce is { } enforce && snapshot.Enforced.Count > 0)
        {
            foreach (var name in snapshot.Enforced)
            {
                if (PropertiesByName.TryGetValue(name, out var prop))
                    prop.SetValue(userSettings, CloneEnforcedValue(prop, prop.GetValue(enforce)));
            }
        }
    }

    public async Task ReplaceServerPolicyAsync(string document)
    {
        var normalized = ClientPolicyContract.Normalize(document);
        if (normalized is not null && !ClientPolicyContract.TryValidate(normalized, out var error))
            _logger.LogWarning("Server policy document is malformed, caching it anyway: {Error}", error);

        var stored = normalized ?? ClientPolicyContract.EmptyDocument;
        await MutateAndPublishAsync(
            mutate: async () =>
            {
                var record = await _cacheStore.GetAsync();
                record.Document = stored;
                await _cacheStore.SetAsync(record);
                return !string.Equals(_publishedDocument, normalized, StringComparison.Ordinal);
            },
            logResult: outcome => _logger.LogInformation(
                outcome switch
                {
                    PublishOutcome.Republished => "Server policy document cached and applied: {Length} char(s)",
                    PublishOutcome.DeferredToFirstRead =>
                        "Server policy document cached, applies at the first policy read: {Length} char(s)",
                    _ => "Server policy document cached, unchanged: {Length} char(s)"
                },
                stored.Length));
    }

    public async Task ClearServerPolicyAsync()
    {
        await MutateAndPublishAsync(
            mutate: () =>
            {
                try
                {
                    _cacheStore.Delete();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete the cached server policy");
                }

                return Task.FromResult(true);
            },
            logResult: _ => _logger.LogInformation("Cached server policy cleared"));
    }

    private enum PublishOutcome
    {
        Unchanged,
        Republished,
        DeferredToFirstRead
    }

    private async Task MutateAndPublishAsync(Func<Task<bool>> mutate, Action<PublishOutcome> logResult)
    {
        PolicyChangedEventArgs? change = null;
        var outcome = PublishOutcome.Unchanged;
        await _writeGate.WaitAsync();
        try
        {
            if (await mutate())
            {
                if (Volatile.Read(ref _snapshot) is { } previous)
                {
                    change = Diff(previous, await LoadAndPublishAsync());
                    outcome = PublishOutcome.Republished;
                }
                else
                {
                    outcome = PublishOutcome.DeferredToFirstRead;
                }
            }

            logResult(outcome);
        }
        finally
        {
            _writeGate.Release();
        }

        RaisePolicyChanged(change);
    }

    private async Task<PolicySnapshot> LoadAndPublishAsync()
    {
        var snapshot = await LoadPolicyAsync();
        Volatile.Write(ref _snapshot, snapshot);
        _publishedDocument = snapshot.ServerDocument;
        return snapshot;
    }

    private void RaisePolicyChanged(PolicyChangedEventArgs? change)
    {
        if (change is null)
            return;

        try
        {
            PolicyChanged?.Invoke(this, change);
        }
        catch (Exception ex)
        {
            // A throw here would abort the sync pull before it persists its catalog version and ETag.
            _logger.LogWarning(ex, "A policy-changed subscriber threw");
        }
    }

    private static PolicyChangedEventArgs? Diff(PolicySnapshot previous, PolicySnapshot current)
    {
        var enforcementChanged = new HashSet<string>(previous.Enforced, StringComparer.OrdinalIgnoreCase);
        enforcementChanged.SymmetricExceptWith(current.Enforced);

        // Only keys the new document still sets: nothing records the value an unpin displaced, so a
        // withdrawal moves nothing.
        var valuesChanged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in current.EffectiveValues)
        {
            previous.EffectiveValues.TryGetValue(name, out var before);
            if (value != before)
                valuesChanged.Add(name);
        }

        if (enforcementChanged.Count == 0 && valuesChanged.Count == 0)
            return null;

        return new PolicyChangedEventArgs
        {
            ValuesChanged = valuesChanged,
            EnforcementChanged = enforcementChanged
        };
    }

    /// <summary>Re-applies a changed default unless the user has changed the value themselves.</summary>
    private void ApplyDefaults(PolicySnapshot snapshot, AppSettings userSettings, AppSettings defaults)
    {
        var builtIn = new AppSettings();
        var record = snapshot.Record;
        var applied = record?.AppliedDefaults;
        var recordChanged = false;

        foreach (var name in snapshot.Defaulted)
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

        // Not snapshot identity: a republish reuses the store's record and only a delete replaces it,
        // and saving a replaced one would put a cleared policy back on disk.
        if (recordChanged && record is not null
            && ReferenceEquals(Volatile.Read(ref _snapshot)?.Record, record))
            PersistCacheRecord(record);
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

    /// <summary>Copies a reference-typed enforce value so a settings page cannot mutate the policy in place.</summary>
    private static object? CloneEnforcedValue(PropertyInfo prop, object? value)
    {
        if (value is null || prop.PropertyType.IsValueType || prop.PropertyType == typeof(string))
            return value;

        try
        {
            var json = SerializeValue(prop, value);
            return json is null
                ? value
                : JsonSerializer.Deserialize(json, prop.PropertyType, JsonOptions) ?? value;
        }
        catch (Exception ex) when (ex is NotSupportedException or JsonException)
        {
            return value;
        }
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

    private void PersistCacheRecord(CachedClientPolicy record)
    {
        try
        {
            _cacheStore.SaveNow(record);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to record the policy defaults that were applied");
        }
    }

    private async Task<PolicySnapshot> LoadPolicyAsync()
    {
        // A load failure is not a removed key, so neither layer may unlock for a cycle.
        var file = await LoadFileLayerAsync() ?? Volatile.Read(ref _snapshot)?.FileLayer ?? EmptyLayer();
        var server = await LoadServerLayerAsync();
        var serverLayer = server.Layer ?? Volatile.Read(ref _snapshot)?.ServerLayer ?? EmptyLayer();

        var defaulted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var enforced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var mergedDefaults = FlattenSection(null, file.Typed.Defaults, file.DefaultKeys, defaulted);
        mergedDefaults = FlattenSection(mergedDefaults, serverLayer.Typed.Defaults, serverLayer.DefaultKeys, defaulted);

        var mergedEnforce = FlattenSection(null, file.Typed.Enforce, file.EnforceKeys, enforced);
        mergedEnforce = FlattenSection(mergedEnforce, serverLayer.Typed.Enforce, serverLayer.EnforceKeys, enforced);

        _logger.LogInformation(
            "Enterprise policy resolved: {DefaultCount} default(s), {EnforcedCount} enforced; {FileCount} key(s) from the policy file, {ServerCount} from the server document",
            defaulted.Count, enforced.Count,
            file.DefaultKeys.Count + file.EnforceKeys.Count,
            serverLayer.DefaultKeys.Count + serverLayer.EnforceKeys.Count);

        var merged = new PolicySettings { Defaults = mergedDefaults, Enforce = mergedEnforce };
        return new PolicySnapshot(
            merged,
            enforced,
            defaulted,
            server.Record,
            server.Document,
            file,
            serverLayer,
            BuildEffectiveValues(merged, enforced, defaulted));
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

    // Taken while the merged objects are still pristine: ApplyDefaults aliases them into AppSettings and
    // the app mutates them in place, so reading them at diff time compares a value against itself.
    private static Dictionary<string, string?> BuildEffectiveValues(
        PolicySettings merged, HashSet<string> enforced, HashSet<string> defaulted)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in enforced)
        {
            if (PropertiesByName.TryGetValue(name, out var prop))
                values[name] = merged.Enforce is { } enforce ? SerializeValue(prop, prop.GetValue(enforce)) : null;
        }

        foreach (var name in defaulted)
        {
            if (!values.ContainsKey(name) && PropertiesByName.TryGetValue(name, out var prop))
                values[name] = merged.Defaults is { } defaults ? SerializeValue(prop, prop.GetValue(defaults)) : null;
        }

        return values;
    }

    private async Task<PolicyLayer?> LoadFileLayerAsync()
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
            _logger.LogWarning(ex, "Failed to load enterprise policy from {Path}, keeping the last load", path);
            return null;
        }
    }

    private async Task<(PolicyLayer? Layer, CachedClientPolicy? Record, string? Document)> LoadServerLayerAsync()
    {
        CachedClientPolicy record;
        try
        {
            record = await _cacheStore.GetAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read the cached server policy, keeping the last load");
            return (null, null, Volatile.Read(ref _snapshot)?.ServerDocument);
        }

        var document = ClientPolicyContract.Normalize(record.Document);
        if (document is null)
            return (EmptyLayer(), record, null);

        try
        {
            return (LoadLayer(document, allowDeviceSettableKeys: false), record, document);
        }
        catch (Exception ex)
        {
            // Reported as published although it did not parse, so the same bad document stays unchanged.
            _logger.LogWarning(ex, "The cached server policy is malformed, keeping the last load");
            return (null, record, document);
        }
    }

    private PolicyLayer LoadLayer(string json, bool allowDeviceSettableKeys)
    {
        var typed = JsonSerializer.Deserialize<PolicySettings>(json, JsonOptions) ?? new PolicySettings();
        var root = JsonNode.Parse(json) as JsonObject;

        return new PolicyLayer(
            typed,
            ReadPresentKeys(root, ClientPolicyContract.DefaultsSection, allowDeviceSettableKeys),
            ReadPresentKeys(root, ClientPolicyContract.EnforceSection, allowDeviceSettableKeys));
    }

    private static PolicyLayer EmptyLayer() =>
        new(new PolicySettings(),
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

    /// <summary>The key sets record which keys the admin actually wrote, never a comparison against the
    /// built-in default, which cannot tell "absent" from "deliberately set to the default".</summary>
    private sealed record PolicySnapshot(
        PolicySettings Merged,
        HashSet<string> Enforced,
        HashSet<string> Defaulted,
        CachedClientPolicy? Record,
        string? ServerDocument,
        PolicyLayer FileLayer,
        PolicyLayer ServerLayer,
        IReadOnlyDictionary<string, string?> EffectiveValues);

    private sealed record PolicyLayer(PolicySettings Typed, HashSet<string> DefaultKeys, HashSet<string> EnforceKeys);
}
