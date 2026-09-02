using Microsoft.Extensions.Logging;
using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Input;
using Xunit;

namespace Pia.Tests.Services;

public class PolicyServiceTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _policyFilePath;
    private readonly string _cacheDir;
    private readonly ILogger<PolicyService> _logger;

    public PolicyServiceTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"pia-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDir);
        _policyFilePath = Path.Combine(_testDir, "policy.json");
        _cacheDir = Path.Combine(_testDir, "cache");
        Directory.CreateDirectory(_cacheDir);
        _logger = Substitute.For<ILogger<PolicyService>>();
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
            Directory.Delete(_testDir, true);
    }

    /// <summary>The cache directory is per-test: the real one is process-global and shared.</summary>
    private PolicyService CreateService()
    {
        return new PolicyService(_logger, _policyFilePath, _cacheDir);
    }

    /// <summary>Hand-authored JSON, never a serialized <c>AppSettings</c>: the engine keys off which
    /// keys the admin wrote, and a round-tripped object writes every one of them.</summary>
    private void WritePolicy(string json) => File.WriteAllText(_policyFilePath, json);

    private async Task<PolicyService> LoadedService(string json)
    {
        WritePolicy(json);
        var service = CreateService();
        await service.GetPolicyAsync();
        return service;
    }

    [Fact]
    public async Task GetPolicyAsync_NoPolicyFile_ReturnsEmptyPolicy()
    {
        var service = CreateService();

        var policy = await service.GetPolicyAsync();

        Assert.NotNull(policy);
        Assert.Null(policy.Defaults);
        Assert.Null(policy.Enforce);
    }

    [Fact]
    public void ApplyPolicy_NoPolicyLoaded_IsNoOp()
    {
        var service = CreateService();
        var settings = new AppSettings { Theme = AppTheme.Light };

        service.ApplyPolicy(settings);

        Assert.Equal(AppTheme.Light, settings.Theme);
    }

    [Fact]
    public void IsEnforced_NoPolicyLoaded_ReturnsFalse()
    {
        var service = CreateService();

        Assert.False(service.IsEnforced(nameof(AppSettings.Theme)));
    }

    [Fact]
    public async Task ApplyPolicy_DefaultsOnly_SetsUnsetValues()
    {
        var service = await LoadedService("""{ "defaults": { "theme": "Dark" } }""");

        var settings = new AppSettings(); // Theme = System (built-in default)
        service.ApplyPolicy(settings);

        Assert.Equal(AppTheme.Dark, settings.Theme);
    }

    [Fact]
    public async Task ApplyPolicy_DefaultsOnly_PreservesUserOverrides()
    {
        var service = await LoadedService("""{ "defaults": { "theme": "Dark" } }""");

        var settings = new AppSettings { Theme = AppTheme.Light };
        service.ApplyPolicy(settings);

        Assert.Equal(AppTheme.Light, settings.Theme);
    }

    [Fact]
    public async Task ApplyPolicy_EnforcedRetentionDays_IsWrittenAndLocked()
    {
        var service = await LoadedService("""{ "enforce": { "chatHistoryRetentionDays": 45 } }""");

        var settings = new AppSettings { ChatHistoryRetentionDays = 90 };
        service.ApplyPolicy(settings);

        Assert.Equal(45, settings.ChatHistoryRetentionDays);
        Assert.True(service.IsEnforced(nameof(AppSettings.ChatHistoryRetentionDays)));
    }

    /// <summary>The retention default moved from 30 to 180. Without the superseded-default allowance an
    /// install still sitting on 30 reads as a value the user picked and never sees the admin's.</summary>
    [Fact]
    public async Task ApplyPolicy_DefaultedRetentionDays_ReachesAnInstallOnTheSupersededDefault()
    {
        var service = await LoadedService("""{ "defaults": { "chatHistoryRetentionDays": 14 } }""");

        var settings = new AppSettings
        {
            ChatHistoryRetentionDays = AppSettings.LegacyChatHistoryRetentionDays,
        };
        service.ApplyPolicy(settings);

        Assert.Equal(14, settings.ChatHistoryRetentionDays);
    }

    [Fact]
    public async Task ApplyPolicy_DefaultedRetentionDays_PreservesAValueTheUserPicked()
    {
        var service = await LoadedService("""{ "defaults": { "chatHistoryRetentionDays": 14 } }""");

        var settings = new AppSettings { ChatHistoryRetentionDays = 90 };
        service.ApplyPolicy(settings);

        Assert.Equal(90, settings.ChatHistoryRetentionDays);
    }

    [Fact]
    public async Task ApplyPolicy_EnforcedOnly_OverwritesUserValues()
    {
        var service = await LoadedService("""{ "enforce": { "theme": "Dark" } }""");

        var settings = new AppSettings { Theme = AppTheme.Light };
        service.ApplyPolicy(settings);

        Assert.Equal(AppTheme.Dark, settings.Theme);
    }

    [Fact]
    public async Task IsEnforced_PropertyInEnforce_ReturnsTrue()
    {
        var service = await LoadedService("""{ "enforce": { "theme": "Dark" } }""");

        Assert.True(service.IsEnforced(nameof(AppSettings.Theme)));
    }

    [Fact]
    public async Task IsEnforced_PropertyNotInEnforce_ReturnsFalse()
    {
        var service = await LoadedService("""{ "enforce": { "theme": "Dark" } }""");

        Assert.False(service.IsEnforced(nameof(AppSettings.StartMinimized)));
    }

    [Fact]
    public async Task ApplyPolicy_DefaultsAndEnforce_CorrectMergeOrder()
    {
        var service = await LoadedService(
            """{ "defaults": { "theme": "Light", "startMinimized": true }, "enforce": { "theme": "Dark" } }""");

        var settings = new AppSettings();
        service.ApplyPolicy(settings);

        Assert.Equal(AppTheme.Dark, settings.Theme);
        Assert.True(settings.StartMinimized);
    }

    [Fact]
    public async Task GetPolicyAsync_HandAuthoredJsonWithStringEnums_DeserializesCorrectly()
    {
        // Admins author policy.json by hand using string enum values, e.g. "DE" rather than 1.
        const string json = """
        {
          "defaults": {
            "uiLanguage": "DE",
            "targetLanguage": "DE",
            "targetSpeechLanguage": "DE",
            "startMinimized": true
          },
          "enforce": {
            "serverUrl": "https://pia-cloud.example.com",
            "syncEnabled": true,
            "autoUpdateEnabled": false,
            "trustSelfSignedCertificates": false
          }
        }
        """;
        File.WriteAllText(_policyFilePath, json);
        var service = CreateService();

        var policy = await service.GetPolicyAsync();

        Assert.NotNull(policy.Defaults);
        Assert.Equal(TargetLanguage.DE, policy.Defaults!.UiLanguage);
        Assert.Equal(TargetLanguage.DE, policy.Defaults.TargetLanguage);
        Assert.Equal(TargetSpeechLanguage.DE, policy.Defaults.TargetSpeechLanguage);
        Assert.True(policy.Defaults.StartMinimized);

        Assert.NotNull(policy.Enforce);
        Assert.Equal("https://pia-cloud.example.com", policy.Enforce!.ServerUrl);
        Assert.True(policy.Enforce.SyncEnabled);
        Assert.False(policy.Enforce.AutoUpdateEnabled);
    }

    [Fact]
    public async Task GetPolicyAsync_InvalidJson_ReturnsEmptyPolicy()
    {
        File.WriteAllText(_policyFilePath, "{ invalid json }}}");
        var service = CreateService();

        var policy = await service.GetPolicyAsync();

        Assert.NotNull(policy);
        Assert.Null(policy.Defaults);
        Assert.Null(policy.Enforce);
    }

    [Fact]
    public async Task ApplyPolicy_EnforcedBool_CannotBeCircumvented()
    {
        var service = await LoadedService("""{ "enforce": { "autoUpdateEnabled": false } }""");

        var settings = new AppSettings { AutoUpdateEnabled = true };
        service.ApplyPolicy(settings);

        Assert.False(settings.AutoUpdateEnabled);
    }

    // ---- presence-based detection ----------------------------------------------------------------
    // The engine used to infer "the admin set this" from `value != new AppSettings().Prop`. That is
    // reference equality for every collection- and object-typed setting, so an enforce block reset
    // them all; and it could not express "enforce the value that happens to be the built-in default".

    [Fact]
    public async Task ApplyPolicy_EnforceBlockOmittingCollections_LeavesThemIntact()
    {
        var service = await LoadedService("""{ "enforce": { "theme": "Dark" } }""");

        var settings = new AppSettings();
        settings.AlwaysAllowedTools.Add(new ToolGrant(Guid.NewGuid(), "write_file", DateTimeOffset.UtcNow));
        settings.Privacy.PiiKeywords.Add(new PiiKeywordEntry { Keyword = "Acme", Category = "Custom" });
        settings.Privacy.TokenizationEnabled = false;
        settings.ModeProviderDefaults[WindowMode.Assistant] = Guid.NewGuid();
        settings.ModePersonaDefaults[WindowMode.Assistant] = Guid.NewGuid();
        settings.AgentPersonaRoster[UserOperatingMode.Business] = [Guid.NewGuid()];
        settings.TodoColumnWidths[Guid.NewGuid()] = 123;

        service.ApplyPolicy(settings);

        Assert.Single(settings.AlwaysAllowedTools);
        Assert.Single(settings.Privacy.PiiKeywords);
        Assert.False(settings.Privacy.TokenizationEnabled);
        Assert.Single(settings.ModeProviderDefaults);
        Assert.Single(settings.ModePersonaDefaults);
        Assert.Single(settings.AgentPersonaRoster);
        Assert.Single(settings.TodoColumnWidths);
        Assert.Equal(AppTheme.Dark, settings.Theme);
    }

    [Fact]
    public async Task ApplyPolicy_EmptyEnforceBlock_ChangesNothing()
    {
        var service = await LoadedService("""{ "enforce": { } }""");

        var settings = new AppSettings { Theme = AppTheme.Light };
        settings.AlwaysAllowedTools.Add(new ToolGrant(Guid.NewGuid(), "write_file", DateTimeOffset.UtcNow));

        service.ApplyPolicy(settings);

        Assert.Equal(AppTheme.Light, settings.Theme);
        Assert.Single(settings.AlwaysAllowedTools);
    }

    [Fact]
    public async Task ApplyPolicy_EnforcedValueEqualToBuiltInDefault_StillApplies()
    {
        // autoUpdateEnabled defaults to true — the old value-diff engine read this as "not set".
        var service = await LoadedService("""{ "enforce": { "autoUpdateEnabled": true } }""");

        var settings = new AppSettings { AutoUpdateEnabled = false };
        service.ApplyPolicy(settings);

        Assert.True(settings.AutoUpdateEnabled);
        Assert.True(service.IsEnforced(nameof(AppSettings.AutoUpdateEnabled)));
    }

    [Fact]
    public async Task ApplyPolicy_EnforcedCollection_ReplacesUserValue()
    {
        var service = await LoadedService(
            """{ "enforce": { "alwaysAllowedTools": [] } }""");

        var settings = new AppSettings();
        settings.AlwaysAllowedTools.Add(new ToolGrant(Guid.NewGuid(), "write_file", DateTimeOffset.UtcNow));

        service.ApplyPolicy(settings);

        Assert.Empty(settings.AlwaysAllowedTools);
    }

    [Fact]
    public async Task ApplyPolicy_DefaultedCollection_IsAppliedWhenUserIsStillAtBuiltIn()
    {
        var service = await LoadedService("""
        {
          "defaults": {
            "alwaysAllowedTools": [
              { "pluginId": "11111111-1111-1111-1111-111111111111", "toolName": "read_file", "grantedAt": "2026-01-01T00:00:00+00:00" }
            ]
          }
        }
        """);

        var settings = new AppSettings();
        service.ApplyPolicy(settings);

        Assert.Single(settings.AlwaysAllowedTools);
        Assert.Equal("read_file", settings.AlwaysAllowedTools[0].ToolName);
    }

    [Fact]
    public async Task ApplyPolicy_DefaultedCollection_PreservesUserValue()
    {
        var service = await LoadedService("""
        {
          "defaults": {
            "alwaysAllowedTools": [
              { "pluginId": "11111111-1111-1111-1111-111111111111", "toolName": "read_file", "grantedAt": "2026-01-01T00:00:00+00:00" }
            ]
          }
        }
        """);

        var settings = new AppSettings();
        settings.AlwaysAllowedTools.Add(new ToolGrant(Guid.NewGuid(), "write_file", DateTimeOffset.UtcNow));

        service.ApplyPolicy(settings);

        Assert.Equal("write_file", Assert.Single(settings.AlwaysAllowedTools).ToolName);
    }

    [Fact]
    public async Task ApplyPolicy_UnknownKey_IsIgnored()
    {
        var service = await LoadedService("""{ "enforce": { "notASetting": 42, "theme": "Dark" } }""");

        var settings = new AppSettings();
        service.ApplyPolicy(settings);

        Assert.Equal(AppTheme.Dark, settings.Theme);
        Assert.False(service.IsEnforced("NotASetting"));
    }

    [Fact]
    public async Task IsEnforced_PascalCaseKey_IsNotMatched()
    {
        // The deserializer is camelCase and case-sensitive, so a PascalCase key never populates the
        // typed value. Treating it as present would enforce a built-in default over the user's value.
        var service = await LoadedService("""{ "enforce": { "Theme": "Dark" } }""");

        var settings = new AppSettings { Theme = AppTheme.Light };
        service.ApplyPolicy(settings);

        Assert.False(service.IsEnforced(nameof(AppSettings.Theme)));
        Assert.Equal(AppTheme.Light, settings.Theme);
    }

    // ---- login provider allow-list ---------------------------------------------------------------

    [Fact]
    public async Task IsLoginProviderAllowed_NoPolicy_AllowsAll()
    {
        var service = CreateService();
        await service.GetPolicyAsync();

        Assert.True(service.IsLoginProviderAllowed("local"));
        Assert.True(service.IsLoginProviderAllowed("google"));
        Assert.True(service.IsLoginProviderAllowed("microsoft"));
    }

    [Fact]
    public async Task IsLoginProviderAllowed_EnforcedAllowList_RestrictsToList()
    {
        var service = await LoadedService("""{ "enforce": { "allowedSyncProviders": ["microsoft"] } }""");

        Assert.False(service.IsLoginProviderAllowed("local"));
        Assert.False(service.IsLoginProviderAllowed("google"));
        Assert.True(service.IsLoginProviderAllowed("microsoft"));
    }

    [Fact]
    public async Task IsLoginProviderAllowed_AllowListIsCaseInsensitive()
    {
        var service = await LoadedService("""{ "enforce": { "allowedSyncProviders": ["Microsoft"] } }""");

        Assert.True(service.IsLoginProviderAllowed("microsoft"));
        Assert.True(service.IsLoginProviderAllowed("MICROSOFT"));
    }

    [Fact]
    public async Task IsLoginProviderAllowed_EmptyList_AllowsAll()
    {
        var service = await LoadedService("""{ "enforce": { "allowedSyncProviders": [] } }""");

        Assert.True(service.IsLoginProviderAllowed("local"));
        Assert.True(service.IsLoginProviderAllowed("google"));
    }

    [Fact]
    public async Task IsLoginProviderAllowed_DefaultsAllowList_AlsoApplies()
    {
        var service = await LoadedService(
            """{ "defaults": { "allowedSyncProviders": ["local", "microsoft"] } }""");

        Assert.True(service.IsLoginProviderAllowed("local"));
        Assert.False(service.IsLoginProviderAllowed("google"));
        Assert.True(service.IsLoginProviderAllowed("microsoft"));
    }

    [Fact]
    public void IsLoginProviderAllowed_PolicyNotLoaded_AllowsAll()
    {
        var service = CreateService();
        // Note: GetPolicyAsync NOT called

        Assert.True(service.IsLoginProviderAllowed("local"));
        Assert.True(service.IsLoginProviderAllowed("google"));
    }

    // ---- file resolution -------------------------------------------------------------------------

    [Fact]
    public void ResolvePolicyFilePath_PrefersPrimaryWhenFileExists()
    {
        var primaryDir = Path.Combine(_testDir, "primary");
        var fallbackDir = Path.Combine(_testDir, "fallback");
        Directory.CreateDirectory(primaryDir);
        Directory.CreateDirectory(fallbackDir);
        File.WriteAllText(Path.Combine(primaryDir, "policy.json"), "{}");
        File.WriteAllText(Path.Combine(fallbackDir, "policy.json"), "{}");

        var resolved = PolicyService.ResolvePolicyFilePath(primaryDir, fallbackDir);

        Assert.Equal(Path.Combine(primaryDir, "policy.json"), resolved);
    }

    [Fact]
    public void ResolvePolicyFilePath_FallsBackWhenPrimaryMissing()
    {
        var primaryDir = Path.Combine(_testDir, "primary");
        var fallbackDir = Path.Combine(_testDir, "fallback");
        Directory.CreateDirectory(primaryDir);
        Directory.CreateDirectory(fallbackDir);
        File.WriteAllText(Path.Combine(fallbackDir, "policy.json"), "{}");

        var resolved = PolicyService.ResolvePolicyFilePath(primaryDir, fallbackDir);

        Assert.Equal(Path.Combine(fallbackDir, "policy.json"), resolved);
    }

    [Fact]
    public void ResolvePolicyFilePath_NeitherExists_ReturnsFallbackPath()
    {
        var primaryDir = Path.Combine(_testDir, "primary");
        var fallbackDir = Path.Combine(_testDir, "fallback");

        var resolved = PolicyService.ResolvePolicyFilePath(primaryDir, fallbackDir);

        Assert.Equal(Path.Combine(fallbackDir, "policy.json"), resolved);
    }

    [Fact]
    public void ResolvePolicyFilePath_ChecksParentWhenExeDirEmpty()
    {
        // Mirrors Velopack layout: exe runs from <install>\current\, policy.json sits at <install>\.
        var installRoot = Path.Combine(_testDir, "install");
        var exeDir = Path.Combine(installRoot, "current");
        var fallbackDir = Path.Combine(_testDir, "fallback");
        Directory.CreateDirectory(exeDir);
        Directory.CreateDirectory(installRoot);
        Directory.CreateDirectory(fallbackDir);
        File.WriteAllText(Path.Combine(installRoot, "policy.json"), "{}");

        var resolved = PolicyService.ResolvePolicyFilePath(exeDir, installRoot, fallbackDir);

        Assert.Equal(Path.Combine(installRoot, "policy.json"), resolved);
    }

    [Fact]
    public async Task LoadsPolicyFromInstallRoot_WhenExeDirHasNone()
    {
        var installRoot = Path.Combine(_testDir, "install");
        var exeDir = Path.Combine(installRoot, "current");
        Directory.CreateDirectory(exeDir);

        var rootPolicyPath = Path.Combine(installRoot, "policy.json");
        File.WriteAllText(rootPolicyPath, """{ "enforce": { "theme": "Dark" } }""");

        var service = new PolicyService(_logger, rootPolicyPath, _cacheDir);
        var policy = await service.GetPolicyAsync();

        Assert.NotNull(policy.Enforce);
        Assert.Equal(AppTheme.Dark, policy.Enforce!.Theme);
    }

    [Fact]
    public async Task GetPolicyAsync_CachesResult()
    {
        WritePolicy("""{ "enforce": { "theme": "Dark" } }""");
        var service = CreateService();

        var first = await service.GetPolicyAsync();
        var second = await service.GetPolicyAsync();

        Assert.Same(second, first);
    }

    // ---- server layer ----------------------------------------------------------------------------

    private string CacheFilePath => Path.Combine(_cacheDir, "policy-cache.json");

    /// <summary>Seeds the server layer through the pull's own entry point, so a service created afterwards
    /// reads it the way a restart would.</summary>
    private Task SeedServerPolicy(string document) => CreateService().ReplaceServerPolicyAsync(document);

    private async Task<PolicyService> LoadedService(string? fileJson, string? serverDocument)
    {
        if (fileJson is not null)
            WritePolicy(fileJson);
        if (serverDocument is not null)
            await SeedServerPolicy(serverDocument);

        var service = CreateService();
        await service.GetPolicyAsync();
        return service;
    }

    private void WriteRawCache(CachedClientPolicy record) => File.WriteAllText(
        CacheFilePath,
        JsonSerializer.Serialize(record, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));

    [Fact]
    public async Task ApplyPolicy_ServerDefaultsOnly_SetsUnsetValues()
    {
        var service = await LoadedService(null, """{ "defaults": { "theme": "Dark" } }""");

        var settings = new AppSettings();
        service.ApplyPolicy(settings);

        Assert.Equal(AppTheme.Dark, settings.Theme);
    }

    [Fact]
    public async Task ApplyPolicy_ServerDefaultsOnly_PreservesUserOverrides()
    {
        var service = await LoadedService(null, """{ "defaults": { "theme": "Dark" } }""");

        var settings = new AppSettings { Theme = AppTheme.Light };
        service.ApplyPolicy(settings);

        Assert.Equal(AppTheme.Light, settings.Theme);
    }

    [Fact]
    public async Task ApplyPolicy_ServerEnforceOnly_OverwritesUserValues()
    {
        var service = await LoadedService(null, """{ "enforce": { "theme": "Dark" } }""");

        var settings = new AppSettings { Theme = AppTheme.Light };
        service.ApplyPolicy(settings);

        Assert.Equal(AppTheme.Dark, settings.Theme);
    }

    [Fact]
    public async Task IsEnforced_ServerEnforceKey_ReturnsTrue()
    {
        var service = await LoadedService(null, """{ "enforce": { "theme": "Dark" } }""");

        Assert.True(service.IsEnforced(nameof(AppSettings.Theme)));
        Assert.False(service.IsEnforced(nameof(AppSettings.StartMinimized)));
    }

    [Fact]
    public async Task ApplyPolicy_BothLayersDefaultSameKey_ServerWins()
    {
        var service = await LoadedService(
            """{ "defaults": { "theme": "Light" } }""",
            """{ "defaults": { "theme": "Dark" } }""");

        var settings = new AppSettings();
        service.ApplyPolicy(settings);

        Assert.Equal(AppTheme.Dark, settings.Theme);
    }

    [Fact]
    public async Task ApplyPolicy_BothLayersEnforceSameKey_ServerWins()
    {
        var service = await LoadedService(
            """{ "enforce": { "theme": "Light" } }""",
            """{ "enforce": { "theme": "Dark" } }""");

        var settings = new AppSettings();
        service.ApplyPolicy(settings);

        Assert.Equal(AppTheme.Dark, settings.Theme);
        Assert.True(service.IsEnforced(nameof(AppSettings.Theme)));
    }

    [Fact]
    public async Task ApplyPolicy_LocalEnforceBeatsServerDefault()
    {
        var service = await LoadedService(
            """{ "enforce": { "theme": "Light" } }""",
            """{ "defaults": { "theme": "Dark" } }""");

        var settings = new AppSettings();
        service.ApplyPolicy(settings);

        Assert.Equal(AppTheme.Light, settings.Theme);
    }

    [Fact]
    public async Task ApplyPolicy_KeyOnlyInFileLayer_SurvivesServerDocumentOmittingIt()
    {
        var service = await LoadedService(
            """{ "defaults": { "theme": "Light" }, "enforce": { "startMinimized": true } }""",
            """{ "enforce": { "autoUpdateEnabled": false } }""");

        var settings = new AppSettings();
        service.ApplyPolicy(settings);

        Assert.Equal(AppTheme.Light, settings.Theme);
        Assert.True(settings.StartMinimized);
        Assert.False(settings.AutoUpdateEnabled);
        Assert.True(service.IsEnforced(nameof(AppSettings.StartMinimized)));
        Assert.True(service.IsEnforced(nameof(AppSettings.AutoUpdateEnabled)));
    }

    // A merged section left null would allow every provider, so both of these pin the non-null merge.

    [Fact]
    public async Task IsLoginProviderAllowed_ServerEnforceListOnly_Restricts()
    {
        var service = await LoadedService(
            null, """{ "enforce": { "allowedSyncProviders": ["microsoft"] } }""");

        Assert.False(service.IsLoginProviderAllowed("local"));
        Assert.True(service.IsLoginProviderAllowed("microsoft"));
    }

    [Fact]
    public async Task IsLoginProviderAllowed_FileDefaultsListOnly_Restricts()
    {
        var service = await LoadedService(
            """{ "defaults": { "allowedSyncProviders": ["local"] } }""", "{}");

        Assert.True(service.IsLoginProviderAllowed("local"));
        Assert.False(service.IsLoginProviderAllowed("microsoft"));
    }

    // ---- keys the server may not set -------------------------------------------------------------

    [Fact]
    public async Task ApplyPolicy_BootstrapKeysInServerLayer_AreIgnored()
    {
        var service = await LoadedService(null, """
        {
          "enforce": {
            "serverUrl": "https://rogue.example.com",
            "syncEnabled": false,
            "trustSelfSignedCertificates": true,
            "isE2EEEnabled": true,
            "theme": "Dark"
          }
        }
        """);

        var settings = new AppSettings { ServerUrl = "https://own.example.com", SyncEnabled = true };
        service.ApplyPolicy(settings);

        Assert.Equal("https://own.example.com", settings.ServerUrl);
        Assert.True(settings.SyncEnabled);
        Assert.False(settings.TrustSelfSignedCertificates);
        Assert.False(settings.IsE2EEEnabled);
        Assert.False(service.IsEnforced(nameof(AppSettings.ServerUrl)));
        Assert.False(service.IsEnforced(nameof(AppSettings.SyncEnabled)));
        Assert.False(service.IsEnforced(nameof(AppSettings.TrustSelfSignedCertificates)));
        Assert.False(service.IsEnforced(nameof(AppSettings.IsE2EEEnabled)));
        Assert.Equal(AppTheme.Dark, settings.Theme);
    }

    [Fact]
    public async Task IsEnforced_BootstrapKeysInFileLayer_StillEnforced()
    {
        // Pinning the three keys that reach a server is shipped device-management behaviour; only the
        // server layer refuses them, where a bad value could strand a whole group.
        var service = await LoadedService("""
        {
          "enforce": {
            "serverUrl": "https://managed.example.com",
            "syncEnabled": true,
            "trustSelfSignedCertificates": true
          }
        }
        """, null);

        var settings = new AppSettings { ServerUrl = "https://own.example.com", SyncEnabled = false };
        service.ApplyPolicy(settings);

        Assert.Equal("https://managed.example.com", settings.ServerUrl);
        Assert.True(settings.SyncEnabled);
        Assert.True(settings.TrustSelfSignedCertificates);
        Assert.True(service.IsEnforced(nameof(AppSettings.ServerUrl)));
        Assert.True(service.IsEnforced(nameof(AppSettings.SyncEnabled)));
        Assert.True(service.IsEnforced(nameof(AppSettings.TrustSelfSignedCertificates)));
    }

    [Fact]
    public async Task ApplyPolicy_CursorKeysInServerLayer_AreIgnored()
    {
        var service = await LoadedService(
            null, """{ "enforce": { "lastPullETag": "policy", "vaultVersion": 99, "theme": "Dark" } }""");

        var settings = new AppSettings { LastPullETag = "own", VaultVersion = 3 };
        service.ApplyPolicy(settings);

        Assert.Equal("own", settings.LastPullETag);
        Assert.Equal(3, settings.VaultVersion);
        Assert.False(service.IsEnforced(nameof(AppSettings.LastPullETag)));
        Assert.False(service.IsEnforced(nameof(AppSettings.VaultVersion)));
        Assert.Equal(AppTheme.Dark, settings.Theme);
    }

    [Fact]
    public async Task ApplyPolicy_CursorKeysInFileLayer_AreIgnored()
    {
        var service = await LoadedService(
            """{ "enforce": { "lastPullETag": "policy", "vaultVersion": 99, "theme": "Dark" } }""", null);

        var settings = new AppSettings { LastPullETag = "own", VaultVersion = 3 };
        service.ApplyPolicy(settings);

        Assert.Equal("own", settings.LastPullETag);
        Assert.Equal(3, settings.VaultVersion);
        Assert.False(service.IsEnforced(nameof(AppSettings.LastPullETag)));
        Assert.False(service.IsEnforced(nameof(AppSettings.VaultVersion)));
        Assert.Equal(AppTheme.Dark, settings.Theme);
    }

    // ---- re-applying a changed default -----------------------------------------------------------

    [Fact]
    public async Task ApplyPolicy_ChangedServerDefault_ReAppliesOnNextLoad()
    {
        var first = await LoadedService(null, """{ "defaults": { "targetSpeechLanguage": "DE" } }""");
        var applied = new AppSettings();
        first.ApplyPolicy(applied);
        Assert.Equal(TargetSpeechLanguage.DE, applied.TargetSpeechLanguage);

        await SeedServerPolicy("""{ "defaults": { "targetSpeechLanguage": "FR" } }""");
        var restarted = CreateService();
        await restarted.GetPolicyAsync();

        var settings = new AppSettings { TargetSpeechLanguage = TargetSpeechLanguage.DE };
        restarted.ApplyPolicy(settings);

        Assert.Equal(TargetSpeechLanguage.FR, settings.TargetSpeechLanguage);
    }

    [Fact]
    public async Task ApplyPolicy_DefaultTheUserChanged_IsNotReApplied()
    {
        var first = await LoadedService(null, """{ "defaults": { "targetSpeechLanguage": "DE" } }""");
        first.ApplyPolicy(new AppSettings());

        await SeedServerPolicy("""{ "defaults": { "targetSpeechLanguage": "FR" } }""");
        var restarted = CreateService();
        await restarted.GetPolicyAsync();

        var settings = new AppSettings { TargetSpeechLanguage = TargetSpeechLanguage.EN };
        restarted.ApplyPolicy(settings);

        Assert.Equal(TargetSpeechLanguage.EN, settings.TargetSpeechLanguage);
    }

    [Fact]
    public async Task ApplyPolicy_ChangedServerCollectionDefault_ReAppliesOnNextLoad()
    {
        var first = await LoadedService(null, """{ "defaults": { "allowedSyncProviders": ["local"] } }""");
        var applied = new AppSettings();
        first.ApplyPolicy(applied);
        Assert.Equal("local", Assert.Single(applied.AllowedSyncProviders!));

        await SeedServerPolicy("""{ "defaults": { "allowedSyncProviders": ["microsoft"] } }""");
        var restarted = CreateService();
        await restarted.GetPolicyAsync();

        // A fresh list, as a restart would deserialize it: reference equality would pin nothing here.
        var settings = new AppSettings { AllowedSyncProviders = new List<string> { "local" } };
        restarted.ApplyPolicy(settings);

        Assert.Equal("microsoft", Assert.Single(settings.AllowedSyncProviders!));
    }

    // ---- cache writes ----------------------------------------------------------------------------

    [Fact]
    public async Task ReplaceServerPolicyAsync_EmptyDocument_ClearsServerLayerAndKeepsFileLayer()
    {
        var service = await LoadedService(
            """{ "enforce": { "startMinimized": true } }""",
            """{ "enforce": { "theme": "Dark" } }""");
        Assert.True(service.IsEnforced(nameof(AppSettings.Theme)));

        await service.ReplaceServerPolicyAsync("{}");

        Assert.False(service.IsEnforced(nameof(AppSettings.Theme)));
        Assert.True(service.IsEnforced(nameof(AppSettings.StartMinimized)));

        var restarted = CreateService();
        await restarted.GetPolicyAsync();

        Assert.False(restarted.IsEnforced(nameof(AppSettings.Theme)));
        Assert.True(restarted.IsEnforced(nameof(AppSettings.StartMinimized)));
    }

    [Fact]
    public async Task ApplyPolicy_ServerDefaultWithdrawnThenRepublished_ReAppliesTheNewValue()
    {
        // Withdrawal keeps the applied-defaults record, unlike a logout, so the republished value still
        // counts the user as sitting on a policy value rather than one they chose.
        var service = await LoadedService(null, """{ "defaults": { "targetSpeechLanguage": "DE" } }""");
        var settings = new AppSettings();
        service.ApplyPolicy(settings);
        Assert.Equal(TargetSpeechLanguage.DE, settings.TargetSpeechLanguage);

        await service.ReplaceServerPolicyAsync("{}");
        service.ApplyPolicy(settings);
        Assert.Equal(TargetSpeechLanguage.DE, settings.TargetSpeechLanguage);

        await service.ReplaceServerPolicyAsync("""{ "defaults": { "targetSpeechLanguage": "FR" } }""");
        service.ApplyPolicy(settings);
        Assert.Equal(TargetSpeechLanguage.FR, settings.TargetSpeechLanguage);

        var restarted = CreateService();
        await restarted.GetPolicyAsync();
        var afterRestart = new AppSettings();
        restarted.ApplyPolicy(afterRestart);

        Assert.Equal(TargetSpeechLanguage.FR, afterRestart.TargetSpeechLanguage);
    }

    [Fact]
    public async Task ReplaceServerPolicyAsync_MovesAChangedPinInTheSameProcess()
    {
        var service = await LoadedService(null, """{ "enforce": { "uiLanguage": "DE" } }""");

        await service.ReplaceServerPolicyAsync("""{ "enforce": { "uiLanguage": "FR" } }""");

        var settings = new AppSettings();
        service.ApplyPolicy(settings);
        Assert.Equal(TargetLanguage.FR, settings.UiLanguage);
        Assert.True(service.IsEnforced(nameof(AppSettings.UiLanguage)));

        var restarted = CreateService();
        await restarted.GetPolicyAsync();
        var afterRestart = new AppSettings();
        restarted.ApplyPolicy(afterRestart);

        Assert.Equal(TargetLanguage.FR, afterRestart.UiLanguage);
    }

    [Fact]
    public async Task ClearServerPolicyAsync_DropsDocumentAndAppliedDefaults()
    {
        var service = await LoadedService(null, """{ "defaults": { "targetSpeechLanguage": "DE" } }""");
        service.ApplyPolicy(new AppSettings());
        Assert.True(File.Exists(CacheFilePath));

        await service.ClearServerPolicyAsync();

        Assert.False(File.Exists(CacheFilePath));

        // DE belongs to the next user now, not to this mechanism, so a file default leaves it alone.
        WritePolicy("""{ "defaults": { "targetSpeechLanguage": "FR" } }""");
        var restarted = CreateService();
        await restarted.GetPolicyAsync();
        var settings = new AppSettings { TargetSpeechLanguage = TargetSpeechLanguage.DE };
        restarted.ApplyPolicy(settings);

        Assert.Equal(TargetSpeechLanguage.DE, settings.TargetSpeechLanguage);
    }

    [Fact]
    public async Task ApplyPolicy_NothingNewToApply_DoesNotRewriteCacheFile()
    {
        var service = await LoadedService(null, """{ "defaults": { "theme": "Dark" } }""");
        var settings = new AppSettings();
        service.ApplyPolicy(settings);
        Assert.True(File.Exists(CacheFilePath));

        // Deleting the record makes any further write visible; repeated passes must not produce one.
        File.Delete(CacheFilePath);
        service.ApplyPolicy(settings);
        service.ApplyPolicy(new AppSettings { Theme = AppTheme.Light });

        // The literal unrelated-save case: a draft or a window move must not rewrite the record.
        service.ApplyPolicy(new AppSettings { Theme = AppTheme.Dark, DraftText = "typing", WindowLeft = 42 });

        Assert.False(File.Exists(CacheFilePath));
    }

    [Fact]
    public async Task GetPolicyAsync_UnparseableServerDocument_KeepsFileLayer()
    {
        WriteRawCache(new CachedClientPolicy { Document = "{ not json" });

        var service = await LoadedService("""{ "enforce": { "startMinimized": true } }""", null);
        var settings = new AppSettings();
        service.ApplyPolicy(settings);

        Assert.True(settings.StartMinimized);
        Assert.False(service.IsEnforced(nameof(AppSettings.Theme)));
    }

    [Fact]
    public async Task GetPolicyAsync_CorruptCacheFile_KeepsFileLayer()
    {
        File.WriteAllText(CacheFilePath, "{ invalid json }}}");

        var service = await LoadedService("""{ "enforce": { "theme": "Dark" } }""", null);
        var settings = new AppSettings();
        service.ApplyPolicy(settings);

        Assert.Equal(AppTheme.Dark, settings.Theme);
    }

    // ---- reference-typed enforce values are copied, not aliased ----------------------------------

    private static readonly string[] ReferenceTypedEnforceKeys =
    [
        nameof(AppSettings.Privacy),
        nameof(AppSettings.ModeProviderDefaults),
        nameof(AppSettings.ModePersonaDefaults),
        nameof(AppSettings.AgentPersonaRoster),
        nameof(AppSettings.AllowedSyncProviders),
        nameof(AppSettings.AlwaysAllowedTools),
        nameof(AppSettings.BlockedBuiltInPersonas),
        nameof(AppSettings.TodoColumnWidths)
    ];

    // Also cloned, but KeyboardShortcut is an immutable record, so aliasing one was never exploitable.
    private static readonly string[] ImmutableReferenceTypedEnforceKeys =
    [
        nameof(AppSettings.AssistantHotkey),
        nameof(AppSettings.FastPathHotkey),
        nameof(AppSettings.OptimizeHotkey)
    ];

    private const string ReferenceTypedEnforceDocument = """
    {
      "enforce": {
        "privacy": { "tokenizationEnabled": true, "piiKeywords": [ { "keyword": "Contoso", "category": "Company" } ] },
        "modeProviderDefaults": { "Assistant": "11111111-1111-1111-1111-111111111111" },
        "modePersonaDefaults": { "Optimize": "22222222-2222-2222-2222-222222222222" },
        "agentPersonaRoster": { "Business": [ "33333333-3333-3333-3333-333333333333" ] },
        "allowedSyncProviders": [ "microsoft" ],
        "alwaysAllowedTools": [ { "pluginId": "44444444-4444-4444-4444-444444444444", "toolName": "read_file", "grantedAt": "2026-01-01T00:00:00+00:00" } ],
        "blockedBuiltInPersonas": [ "coach" ],
        "todoColumnWidths": { "55555555-5555-5555-5555-555555555555": 240.0 },
        "assistantHotkey": { "modifiers": "Control, Alt", "key": "J", "virtualKeyCode": 74 },
        "fastPathHotkey": { "modifiers": "Control, Shift", "key": "K", "virtualKeyCode": 75 },
        "optimizeHotkey": { "modifiers": "Alt", "key": "L", "virtualKeyCode": 76 }
      }
    }
    """;

    [Fact]
    public async Task ApplyPolicy_EnforcedPrivacy_SurvivesAnInPlaceEditOfTheAppliedValue()
    {
        var service = await LoadedService("""
        {
          "enforce": {
            "privacy": {
              "tokenizationEnabled": true,
              "piiKeywords": [ { "keyword": "Contoso", "category": "Company" } ]
            }
          }
        }
        """);

        var applied = new AppSettings();
        service.ApplyPolicy(applied);

        applied.Privacy.TokenizationEnabled = false;
        applied.Privacy.PiiKeywords.Clear();

        var fresh = new AppSettings { Privacy = new PrivacySettings { TokenizationEnabled = false } };
        service.ApplyPolicy(fresh);

        Assert.True(fresh.Privacy.TokenizationEnabled);
        Assert.Equal("Contoso", Assert.Single(fresh.Privacy.PiiKeywords).Keyword);
    }

    [Fact]
    public async Task ApplyPolicy_EnforcedCollection_SurvivesAnInPlaceEditOfTheAppliedValue()
    {
        var service = await LoadedService("""{ "enforce": { "allowedSyncProviders": ["microsoft"] } }""");

        var applied = new AppSettings();
        service.ApplyPolicy(applied);

        applied.AllowedSyncProviders!.Add("local");

        var fresh = new AppSettings();
        service.ApplyPolicy(fresh);

        Assert.Equal("microsoft", Assert.Single(fresh.AllowedSyncProviders!));
        Assert.True(service.IsLoginProviderAllowed("microsoft"));
        Assert.False(service.IsLoginProviderAllowed("local"));
    }

    [Fact]
    public async Task ApplyPolicy_ReferenceTypedEnforceValues_GiveEveryTargetItsOwnCopy()
    {
        var service = await LoadedService(ReferenceTypedEnforceDocument);
        var policy = await service.GetPolicyAsync();

        var first = new AppSettings();
        var second = new AppSettings();
        service.ApplyPolicy(first);
        service.ApplyPolicy(second);

        foreach (var name in ReferenceTypedEnforceKeys.Concat(ImmutableReferenceTypedEnforceKeys))
        {
            Assert.True(service.IsEnforced(name));

            var prop = typeof(AppSettings).GetProperty(name);
            Assert.NotNull(prop);

            var enforced = prop.GetValue(policy.Enforce);
            var applied = prop.GetValue(first);
            Assert.NotNull(enforced);
            Assert.NotNull(applied);

            Assert.NotSame(enforced, applied);
            Assert.NotSame(applied, prop.GetValue(second));
            Assert.Equal(Serialize(enforced), Serialize(applied));
        }
    }

    [Fact]
    public async Task ApplyPolicy_EnforcedHotkey_KeepsItsValueThroughTheClone()
    {
        var service = await LoadedService(ReferenceTypedEnforceDocument);

        var settings = new AppSettings();
        service.ApplyPolicy(settings);

        Assert.Equal(
            new KeyboardShortcut(KeyModifiers.Control | KeyModifiers.Alt, Key.J, 74),
            settings.AssistantHotkey);
    }

    [Fact]
    public void ReferenceTypedEnforceKeys_CoverEveryReferenceTypedSetting()
    {
        var reachable = typeof(AppSettings)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite)
            .Where(p => !p.PropertyType.IsValueType && p.PropertyType != typeof(string))
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        var classified = ReferenceTypedEnforceKeys
            .Concat(ImmutableReferenceTypedEnforceKeys)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(reachable, classified);
        Assert.Empty(ReferenceTypedEnforceKeys.Intersect(ImmutableReferenceTypedEnforceKeys, StringComparer.Ordinal));
    }

    // ---- an enforce pin is whole-object ----------------------------------------------------------
    // Key presence is read at the section's top level, so pinning one leaf writes the type's own
    // defaults over every sibling the admin left out.

    [Fact]
    public async Task ApplyPolicy_EnforcedPrivacyWithoutKeywords_ClearsTheUsersKeywords()
    {
        var service = await LoadedService("""{ "enforce": { "privacy": { "tokenizationEnabled": true } } }""");

        var settings = new AppSettings { Privacy = new PrivacySettings { TokenizationEnabled = false } };
        settings.Privacy.PiiKeywords.Add(new PiiKeywordEntry { Keyword = "Acme", Category = "Custom" });
        service.ApplyPolicy(settings);

        Assert.True(settings.Privacy.TokenizationEnabled);
        Assert.Empty(settings.Privacy.PiiKeywords);

        // Every save re-applies the policy first, so the user cannot add one back either.
        settings.Privacy.PiiKeywords.Add(new PiiKeywordEntry { Keyword = "Acme", Category = "Custom" });
        service.ApplyPolicy(settings);

        Assert.Empty(settings.Privacy.PiiKeywords);
    }

    [Fact]
    public async Task ApplyPolicy_EnforcedHotkeyWithoutAVirtualKeyCode_LeavesItUnregisterable()
    {
        var service = await LoadedService("""{ "enforce": { "assistantHotkey": { "key": "J" } } }""");

        var settings = new AppSettings();
        service.ApplyPolicy(settings);

        Assert.Equal(new KeyboardShortcut(KeyModifiers.None, Key.J, 0), settings.AssistantHotkey);
    }

    // ---- live re-apply ---------------------------------------------------------------------------

    private static List<PolicyChangedEventArgs> Observe(PolicyService service)
    {
        var raised = new List<PolicyChangedEventArgs>();
        service.PolicyChanged += (_, e) => raised.Add(e);
        return raised;
    }

    [Fact]
    public async Task ReplaceServerPolicyAsync_ChangedPin_RaisesTheValueOnly()
    {
        var service = await LoadedService(null, """{ "enforce": { "uiLanguage": "DE" } }""");
        var raised = Observe(service);

        await service.ReplaceServerPolicyAsync("""{ "enforce": { "uiLanguage": "FR" } }""");

        var change = Assert.Single(raised);
        Assert.Equal(nameof(AppSettings.UiLanguage), Assert.Single(change.ValuesChanged));
        Assert.Empty(change.EnforcementChanged);
    }

    [Fact]
    public async Task ReplaceServerPolicyAsync_NewPin_RaisesTheValueAndTheLock()
    {
        var service = await LoadedService(null, """{ "enforce": { "uiLanguage": "DE" } }""");
        var raised = Observe(service);

        await service.ReplaceServerPolicyAsync("""{ "enforce": { "uiLanguage": "DE", "theme": "Dark" } }""");

        var change = Assert.Single(raised);
        Assert.Equal(nameof(AppSettings.Theme), Assert.Single(change.ValuesChanged));
        Assert.Equal(nameof(AppSettings.Theme), Assert.Single(change.EnforcementChanged));
        Assert.True(service.IsEnforced(nameof(AppSettings.Theme)));
    }

    [Fact]
    public async Task ReplaceServerPolicyAsync_Withdrawal_RaisesTheLockWithoutAValue()
    {
        var service = await LoadedService(null, """{ "enforce": { "uiLanguage": "DE" } }""");
        var raised = Observe(service);

        await service.ReplaceServerPolicyAsync("{}");

        var change = Assert.Single(raised);
        Assert.Equal(nameof(AppSettings.UiLanguage), Assert.Single(change.EnforcementChanged));
        Assert.Empty(change.ValuesChanged);
        Assert.False(service.IsEnforced(nameof(AppSettings.UiLanguage)));
    }

    [Fact]
    public async Task ReplaceServerPolicyAsync_NewDefault_RaisesTheValueWithoutALock()
    {
        var service = await LoadedService(null, """{ "defaults": { "theme": "Dark" } }""");
        var raised = Observe(service);

        await service.ReplaceServerPolicyAsync(
            """{ "defaults": { "theme": "Dark", "targetSpeechLanguage": "FR" } }""");

        var change = Assert.Single(raised);
        Assert.Equal(nameof(AppSettings.TargetSpeechLanguage), Assert.Single(change.ValuesChanged));
        Assert.Empty(change.EnforcementChanged);
    }

    [Fact]
    public async Task ReplaceServerPolicyAsync_ByteIdenticalDocument_SkipsTheRebuild()
    {
        const string document = """{ "enforce": { "uiLanguage": "DE" } }""";
        var service = await LoadedService(null, document);
        var raised = Observe(service);

        // A key that only a rebuild could pick up: the policy file was empty when the service loaded.
        WritePolicy("""{ "enforce": { "startMinimized": true } }""");
        await service.ReplaceServerPolicyAsync(document);
        await service.ReplaceServerPolicyAsync(document);

        Assert.Empty(raised);
        Assert.False(service.IsEnforced(nameof(AppSettings.StartMinimized)));
    }

    [Fact]
    public async Task ReplaceServerPolicyAsync_BeforeTheFirstLoad_PublishesNothingAndRaisesNothing()
    {
        var service = CreateService();
        var raised = Observe(service);

        await service.ReplaceServerPolicyAsync("""{ "enforce": { "uiLanguage": "FR" } }""");

        Assert.Empty(raised);
        Assert.False(service.IsEnforced(nameof(AppSettings.UiLanguage)));
        var untouched = new AppSettings();
        service.ApplyPolicy(untouched);
        Assert.Equal(TargetLanguage.EN, untouched.UiLanguage);

        await service.GetPolicyAsync();

        Assert.True(service.IsEnforced(nameof(AppSettings.UiLanguage)));
        var settings = new AppSettings();
        service.ApplyPolicy(settings);
        Assert.Equal(TargetLanguage.FR, settings.UiLanguage);
        Assert.Empty(raised);
    }

    [Fact]
    public async Task ReplaceServerPolicyAsync_AThrowingSubscriber_NeitherPropagatesNorStrandsTheChange()
    {
        var service = await LoadedService(null, """{ "enforce": { "uiLanguage": "DE" } }""");
        var seen = new List<PolicyChangedEventArgs>();
        service.PolicyChanged += (_, e) =>
        {
            seen.Add(e);
            throw new InvalidOperationException("subscriber");
        };

        await service.ReplaceServerPolicyAsync("""{ "enforce": { "uiLanguage": "FR" } }""");

        Assert.Single(seen);
        var applied = new AppSettings();
        service.ApplyPolicy(applied);
        Assert.Equal(TargetLanguage.FR, applied.UiLanguage);

        await service.ReplaceServerPolicyAsync("""{ "enforce": { "uiLanguage": "FR" } }""");
        Assert.Single(seen);

        await service.ReplaceServerPolicyAsync("""{ "enforce": { "uiLanguage": "EN" } }""");
        Assert.Equal(2, seen.Count);
    }

    [Fact]
    public async Task ReplaceServerPolicyAsync_AfterAFailedCacheWrite_StillAppliesTheSameDocument()
    {
        const string document = """{ "enforce": { "uiLanguage": "FR" } }""";
        var service = await LoadedService(null, null);
        var raised = Observe(service);

        // A directory in the cache file's place fails the write while the store keeps the record it
        // already mutated, which is why the change baseline cannot be read back off that record.
        Directory.CreateDirectory(CacheFilePath);
        await Assert.ThrowsAnyAsync<SystemException>(() => service.ReplaceServerPolicyAsync(document));
        Directory.Delete(CacheFilePath);

        await service.ReplaceServerPolicyAsync(document);

        Assert.Single(raised);
        Assert.True(service.IsEnforced(nameof(AppSettings.UiLanguage)));
    }

    [Fact]
    public async Task ClearServerPolicyAsync_DropsEnforcementOnTheSameInstance()
    {
        var service = await LoadedService(
            """{ "enforce": { "startMinimized": true } }""",
            """{ "enforce": { "theme": "Dark" } }""");
        Assert.True(service.IsEnforced(nameof(AppSettings.Theme)));
        var raised = Observe(service);

        await service.ClearServerPolicyAsync();

        Assert.False(service.IsEnforced(nameof(AppSettings.Theme)));
        Assert.True(service.IsEnforced(nameof(AppSettings.StartMinimized)));

        var change = Assert.Single(raised);
        Assert.Equal(nameof(AppSettings.Theme), Assert.Single(change.EnforcementChanged));
        Assert.Empty(change.ValuesChanged);
        Assert.False(File.Exists(CacheFilePath));
    }

    [Fact]
    public async Task ReplaceServerPolicyAsync_NewlyEnforcedKey_AppliesTheAdminValueNotABuiltInDefault()
    {
        var service = await LoadedService(null, """{ "enforce": { "theme": "Dark" } }""");
        var settings = new AppSettings();
        service.ApplyPolicy(settings);

        await service.ReplaceServerPolicyAsync("""{ "enforce": { "theme": "Dark", "autoTypeDelayMs": 250 } }""");
        service.ApplyPolicy(settings);

        Assert.Equal(250, settings.AutoTypeDelayMs);
        Assert.NotEqual(new AppSettings().AutoTypeDelayMs, settings.AutoTypeDelayMs);
        Assert.Equal(AppTheme.Dark, settings.Theme);
    }

    [Fact]
    public async Task ReplaceServerPolicyAsync_ASubscriberThatCallsBackAndWaits_IsNotBlocked()
    {
        var service = await LoadedService(null, """{ "enforce": { "uiLanguage": "DE" } }""");
        var reentered = false;
        var completed = false;
        service.PolicyChanged += (_, _) =>
        {
            if (reentered)
                return;

            // Blocking is the point: raising while the write gate is still held would deadlock here,
            // and the semaphore is not reentrant.
            reentered = true;
            completed = service.ClearServerPolicyAsync().Wait(TimeSpan.FromSeconds(10));
        };

        await service.ReplaceServerPolicyAsync("""{ "enforce": { "uiLanguage": "FR" } }""");

        Assert.True(reentered);
        Assert.True(completed);
        Assert.False(service.IsEnforced(nameof(AppSettings.UiLanguage)));
    }

    [Fact]
    public async Task ReplaceServerPolicyAsync_WhenThePolicyFileFailsToLoad_KeepsTheLastFileLayer()
    {
        var service = await LoadedService(
            """{ "enforce": { "startMinimized": true } }""",
            """{ "enforce": { "theme": "Dark" } }""");
        Assert.True(service.IsEnforced(nameof(AppSettings.StartMinimized)));

        WritePolicy("{ invalid json }}}");
        await service.ReplaceServerPolicyAsync("""{ "enforce": { "theme": "Light" } }""");

        Assert.True(service.IsEnforced(nameof(AppSettings.StartMinimized)));
        var settings = new AppSettings();
        service.ApplyPolicy(settings);
        Assert.True(settings.StartMinimized);
        Assert.Equal(AppTheme.Light, settings.Theme);
    }

    [Fact]
    public async Task ReplaceServerPolicyAsync_AfterTheUserEditedADefaultedObject_ReportsOnlyTheAdminsChange()
    {
        var service = await LoadedService(null, """
            { "defaults": { "privacy": { "tokenizationEnabled": true }, "modeProviderDefaults": {} } }
            """);
        var settings = new AppSettings();
        service.ApplyPolicy(settings);

        // ApplyDefaults aliases the merged object into AppSettings on purpose, and the Settings pages and
        // the sync pull then mutate it in place.
        settings.Privacy.TokenizationEnabled = false;
        settings.ModeProviderDefaults[WindowMode.Assistant] = Guid.NewGuid();
        var raised = Observe(service);

        await service.ReplaceServerPolicyAsync("""
            { "defaults": { "privacy": { "tokenizationEnabled": true }, "modeProviderDefaults": {}, "theme": "Dark" } }
            """);

        var change = Assert.Single(raised);
        Assert.Equal(nameof(AppSettings.Theme), Assert.Single(change.ValuesChanged));
    }

    [Fact]
    public async Task ReplaceServerPolicyAsync_ADocumentWithAnUnknownEnumValue_KeepsTheLastServerLayer()
    {
        var service = await LoadedService(null, """{ "enforce": { "uiLanguage": "DE" } }""");
        var raised = Observe(service);

        await service.ReplaceServerPolicyAsync("""{ "enforce": { "uiLanguage": "DE", "theme": "Purple" } }""");

        Assert.True(service.IsEnforced(nameof(AppSettings.UiLanguage)));
        Assert.False(service.IsEnforced(nameof(AppSettings.Theme)));
        Assert.Empty(raised);

        var settings = new AppSettings();
        service.ApplyPolicy(settings);
        Assert.Equal(TargetLanguage.DE, settings.UiLanguage);
    }

    [Fact]
    public async Task ReplaceServerPolicyAsync_AnUnparseableDocumentTwice_RebuildsOnlyOnce()
    {
        const string malformed = """{ "enforce": { "uiLanguage": "DE", "theme": "Purple" } }""";
        var service = await LoadedService(null, """{ "enforce": { "uiLanguage": "DE" } }""");
        await service.ReplaceServerPolicyAsync(malformed);

        // A key that only a rebuild could pick up: the policy file was empty when the service loaded.
        WritePolicy("""{ "enforce": { "startMinimized": true } }""");
        await service.ReplaceServerPolicyAsync(malformed);

        Assert.False(service.IsEnforced(nameof(AppSettings.StartMinimized)));
    }

    private static string Serialize(object value) => JsonSerializer.Serialize(
        value, new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } });
}
