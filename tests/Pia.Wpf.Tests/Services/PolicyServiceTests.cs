using Microsoft.Extensions.Logging;
using NSubstitute;
using Pia.Models;
using Pia.Services;
using System.IO;
using System.Text.Json;
using Xunit;

namespace Pia.Tests.Services;

public class PolicyServiceTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _policyFilePath;
    private readonly ILogger<PolicyService> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

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

    private void WritePolicyFile(PolicySettings policy)
    {
        var json = JsonSerializer.Serialize(policy, JsonOptions);
        File.WriteAllText(_policyFilePath, json);
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
        WritePolicyFile(new PolicySettings
        {
            Defaults = new AppSettings { Theme = AppTheme.Dark }
        });
        var service = CreateService();
        await service.GetPolicyAsync();

        var settings = new AppSettings(); // Theme = System (built-in default)
        service.ApplyPolicy(settings);

        Assert.Equal(AppTheme.Dark, settings.Theme);
    }

    [Fact]
    public async Task ApplyPolicy_DefaultsOnly_PreservesUserOverrides()
    {
        WritePolicyFile(new PolicySettings
        {
            Defaults = new AppSettings { Theme = AppTheme.Dark }
        });
        var service = CreateService();
        await service.GetPolicyAsync();

        var settings = new AppSettings { Theme = AppTheme.Light };
        service.ApplyPolicy(settings);

        Assert.Equal(AppTheme.Light, settings.Theme);
    }

    [Fact]
    public async Task ApplyPolicy_EnforcedOnly_OverwritesUserValues()
    {
        WritePolicyFile(new PolicySettings
        {
            Enforce = new AppSettings { Theme = AppTheme.Dark }
        });
        var service = CreateService();
        await service.GetPolicyAsync();

        var settings = new AppSettings { Theme = AppTheme.Light };
        service.ApplyPolicy(settings);

        Assert.Equal(AppTheme.Dark, settings.Theme);
    }

    [Fact]
    public async Task IsEnforced_PropertyInEnforce_ReturnsTrue()
    {
        WritePolicyFile(new PolicySettings
        {
            Enforce = new AppSettings { Theme = AppTheme.Dark }
        });
        var service = CreateService();
        await service.GetPolicyAsync();

        Assert.True(service.IsEnforced(nameof(AppSettings.Theme)));
    }

    [Fact]
    public async Task IsEnforced_PropertyNotInEnforce_ReturnsFalse()
    {
        WritePolicyFile(new PolicySettings
        {
            Enforce = new AppSettings { Theme = AppTheme.Dark }
        });
        var service = CreateService();
        await service.GetPolicyAsync();

        Assert.False(service.IsEnforced(nameof(AppSettings.StartMinimized)));
    }

    [Fact]
    public async Task ApplyPolicy_DefaultsAndEnforce_CorrectMergeOrder()
    {
        WritePolicyFile(new PolicySettings
        {
            Defaults = new AppSettings { Theme = AppTheme.Light, StartMinimized = true },
            Enforce = new AppSettings { Theme = AppTheme.Dark }
        });
        var service = CreateService();
        await service.GetPolicyAsync();

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
            "startMinimized": true,
            "showTodoPanelButton": false
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
        Assert.False(policy.Defaults.ShowTodoPanelButton);

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
        WritePolicyFile(new PolicySettings
        {
            Enforce = new AppSettings { AutoUpdateEnabled = false }
        });
        var service = CreateService();
        await service.GetPolicyAsync();

        var settings = new AppSettings { AutoUpdateEnabled = true };
        service.ApplyPolicy(settings);

        Assert.False(settings.AutoUpdateEnabled);
    }

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
        WritePolicyFile(new PolicySettings
        {
            Enforce = new AppSettings { AllowedSyncProviders = new List<string> { "microsoft" } }
        });
        var service = CreateService();
        await service.GetPolicyAsync();

        Assert.False(service.IsLoginProviderAllowed("local"));
        Assert.False(service.IsLoginProviderAllowed("google"));
        Assert.True(service.IsLoginProviderAllowed("microsoft"));
    }

    [Fact]
    public async Task IsLoginProviderAllowed_AllowListIsCaseInsensitive()
    {
        WritePolicyFile(new PolicySettings
        {
            Enforce = new AppSettings { AllowedSyncProviders = new List<string> { "Microsoft" } }
        });
        var service = CreateService();
        await service.GetPolicyAsync();

        Assert.True(service.IsLoginProviderAllowed("microsoft"));
        Assert.True(service.IsLoginProviderAllowed("MICROSOFT"));
    }

    [Fact]
    public async Task IsLoginProviderAllowed_EmptyList_AllowsAll()
    {
        WritePolicyFile(new PolicySettings
        {
            Enforce = new AppSettings { AllowedSyncProviders = new List<string>() }
        });
        var service = CreateService();
        await service.GetPolicyAsync();

        Assert.True(service.IsLoginProviderAllowed("local"));
        Assert.True(service.IsLoginProviderAllowed("google"));
    }

    [Fact]
    public async Task IsLoginProviderAllowed_DefaultsAllowList_AlsoApplies()
    {
        WritePolicyFile(new PolicySettings
        {
            Defaults = new AppSettings { AllowedSyncProviders = new List<string> { "local", "microsoft" } }
        });
        var service = CreateService();
        await service.GetPolicyAsync();

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

    [Fact]
    public async Task GetPolicyAsync_CachesResult()
    {
        WritePolicyFile(new PolicySettings
        {
            Enforce = new AppSettings { Theme = AppTheme.Dark }
        });
        var service = CreateService();

        var first = await service.GetPolicyAsync();
        var second = await service.GetPolicyAsync();

        Assert.Same(second, first);
    }
}
