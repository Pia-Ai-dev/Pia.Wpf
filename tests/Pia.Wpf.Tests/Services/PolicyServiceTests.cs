using Microsoft.Extensions.Logging;
using NSubstitute;
using Pia.Models;
using Pia.Services;
using System.IO;
using Xunit;

namespace Pia.Tests.Services;

public class PolicyServiceTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _policyFilePath;
    private readonly ILogger<PolicyService> _logger;

    public PolicyServiceTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"pia-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDir);
        _policyFilePath = Path.Combine(_testDir, "policy.json");
        _logger = Substitute.For<ILogger<PolicyService>>();
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
            Directory.Delete(_testDir, true);
    }

    private PolicyService CreateService()
    {
        return new PolicyService(_logger, _policyFilePath);
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

        var service = new PolicyService(_logger, rootPolicyPath);
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
}
