using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services;

// Shares a collection with TokenizationLatchTests: the restart decision reads the process-wide latch.
[Collection("TokenizationLatchStatic")]
public class PolicyChangeCoordinatorTests : IDisposable
{
    private const string PrivacyOff = """{ "enforce": { "privacy": { "tokenizationEnabled": false } } }""";
    private const string PrivacyOn = """{ "enforce": { "privacy": { "tokenizationEnabled": true } } }""";
    private const string PrivacyDefaultOff = """{ "defaults": { "privacy": { "tokenizationEnabled": false } } }""";
    private const string PrivacyDefaultOn = """{ "defaults": { "privacy": { "tokenizationEnabled": true } } }""";

    private readonly string _testDir;
    private readonly string _policyFilePath;
    private readonly string _cacheDir;
    private readonly ILogger<PolicyService> _policyLogger = Substitute.For<ILogger<PolicyService>>();
    private readonly ISettingsService _settingsService = Substitute.For<ISettingsService>();
    private readonly List<string> _steps = [];
    private readonly AppSettings _shared = new();
    private IPolicyService? _appliedBy;
    private TaskCompletionSource _saveCompleted = CompletedSource();

    public PolicyChangeCoordinatorTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"pia-test-{Guid.NewGuid()}");
        _policyFilePath = Path.Combine(_testDir, "policy.json");
        _cacheDir = Path.Combine(_testDir, "cache");
        Directory.CreateDirectory(_cacheDir);
        TokenizationLatch.Reset();

        _settingsService.GetSettingsAsync().Returns(_ => Task.FromResult(ApplyToTheSharedInstance()));
        _settingsService.SaveSettingsAsync(Arg.Any<AppSettings>()).Returns(_ => RecordSaveOnCompletionAsync());
    }

    public void Dispose()
    {
        TokenizationLatch.Reset();
        if (Directory.Exists(_testDir))
            Directory.Delete(_testDir, true);
    }

    /// <summary>What SettingsService.GetSettingsAsync does: the policy lands on the one shared instance.</summary>
    private AppSettings ApplyToTheSharedInstance()
    {
        Record("get");
        _appliedBy?.ApplyPolicy(_shared);
        return _shared;
    }

    private void Record(string step)
    {
        lock (_steps)
            _steps.Add(step);
    }

    /// <summary>Recorded when the returned task completes, not when the call is made: a fire-and-forget
    /// save would otherwise record in order too.</summary>
    private async Task RecordSaveOnCompletionAsync()
    {
        await _saveCompleted.Task;
        Record("save");
    }

    private static TaskCompletionSource CompletedSource()
    {
        var source = new TaskCompletionSource();
        source.SetResult();
        return source;
    }

    private void FailTheSave() => _settingsService.SaveSettingsAsync(Arg.Any<AppSettings>())
        .Returns(_ => Task.FromException(new InvalidOperationException("a settings-changed subscriber threw")));

    private static PolicyChangedEventArgs PrivacyValueChanged() => new()
    {
        ValuesChanged = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { nameof(AppSettings.Privacy) },
        EnforcementChanged = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    };

    /// <summary>Takes the decision the way the first AI turn does: from the settings as they stand now.</summary>
    private void LatchNow() => TokenizationLatch.Latch(_shared.Privacy);

    private static PiiKeywordEntry Keyword(string value) => new() { Keyword = value, Category = "Custom" };

    private PolicyService CreateService() => new(_policyLogger, _policyFilePath, _cacheDir);

    /// <summary>Seeds the server layer the way the pull does, so the service under test starts from it.</summary>
    private async Task<PolicyService> LoadedService(string? serverDocument = null)
    {
        if (serverDocument is not null)
            await CreateService().ReplaceServerPolicyAsync(serverDocument);

        var service = CreateService();
        await service.GetPolicyAsync();
        _appliedBy = service;
        return service;
    }

    private PolicyChangeCoordinator Coordinate(IPolicyService service) =>
        new(service, _settingsService, NullLogger<PolicyChangeCoordinator>.Instance);

    [Fact]
    public async Task AChangedPolicy_IsBothAppliedAndSaved()
    {
        var service = await LoadedService("""{ "enforce": { "uiLanguage": "DE" } }""");
        var coordinator = Coordinate(service);

        await service.ReplaceServerPolicyAsync("""{ "enforce": { "uiLanguage": "FR" } }""");
        await coordinator.InFlightApply;

        // Get alone mutates the shared instance; only Save raises SettingsChanged — and saving any other
        // instance would replace the one every component holds.
        Assert.Equal(TargetLanguage.FR, _shared.UiLanguage);
        await _settingsService.Received(1).GetSettingsAsync();
        await _settingsService.Received(1).SaveSettingsAsync(_shared);
    }

    [Fact]
    public async Task TheLocksAreRaisedAfterTheValueMove()
    {
        var service = await LoadedService("""{ "enforce": { "uiLanguage": "DE" } }""");
        service.LocksChanged += (_, _) => Record("locks");
        // Held pending so the save is still in flight when an unawaited one would raise the locks.
        _saveCompleted = new TaskCompletionSource();
        var coordinator = Coordinate(service);

        await service.ReplaceServerPolicyAsync("""{ "enforce": { "uiLanguage": "FR" } }""");
        _saveCompleted.SetResult();
        await coordinator.InFlightApply;

        Assert.Equal(new[] { "get", "save", "locks" }, _steps);
    }

    [Fact]
    public async Task AChangedPrivacyValue_RequiresARestartOnceTokenizationHasLatched()
    {
        var service = await LoadedService();
        LatchNow();
        var coordinator = Coordinate(service);
        var raised = 0;
        service.RestartRequiredChanged += (_, _) => raised++;

        await service.ReplaceServerPolicyAsync(PrivacyOff);
        await coordinator.InFlightApply;

        Assert.False(_shared.Privacy.TokenizationEnabled);
        Assert.True(service.IsRestartRequired);
        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task AChangedPrivacyValue_AppliesLiveBeforeTokenizationHasLatched()
    {
        var service = await LoadedService();
        var coordinator = Coordinate(service);
        var raised = 0;
        service.RestartRequiredChanged += (_, _) => raised++;

        await service.ReplaceServerPolicyAsync(PrivacyOff);
        await coordinator.InFlightApply;

        Assert.False(_shared.Privacy.TokenizationEnabled);
        Assert.False(service.IsRestartRequired);
        Assert.Equal(0, raised);
    }

    /// <summary>Pinning the recommended default is the likeliest admin action and moves no value at all.</summary>
    [Fact]
    public async Task APinnedPrivacyValueTheUserAlreadyHas_RequiresNoRestart()
    {
        var service = await LoadedService();
        LatchNow();
        var coordinator = Coordinate(service);

        await service.ReplaceServerPolicyAsync(PrivacyOn);
        await coordinator.InFlightApply;

        Assert.True(service.IsEnforced(nameof(AppSettings.Privacy)));
        Assert.True(_shared.Privacy.TokenizationEnabled);
        Assert.False(service.IsRestartRequired);
    }

    /// <summary>A default is applied over a value the user has not changed themselves, so it moves the
    /// value while the key is never enforced.</summary>
    [Fact]
    public async Task AChangedPrivacyDefault_RequiresARestartWhenItMovesTheValue()
    {
        var service = await LoadedService(PrivacyDefaultOff);
        service.ApplyPolicy(_shared);
        Assert.False(_shared.Privacy.TokenizationEnabled);
        LatchNow();
        var coordinator = Coordinate(service);

        await service.ReplaceServerPolicyAsync(PrivacyDefaultOn);
        await coordinator.InFlightApply;

        Assert.False(service.IsEnforced(nameof(AppSettings.Privacy)));
        Assert.True(_shared.Privacy.TokenizationEnabled);
        Assert.True(service.IsRestartRequired);
    }

    [Fact]
    public async Task AChangedPrivacyDefault_RequiresNoRestartWhenTheUserCustomisedTheValue()
    {
        var service = await LoadedService();
        _shared.Privacy = new PrivacySettings { PiiKeywords = [Keyword("acme")] };
        LatchNow();
        var coordinator = Coordinate(service);

        await service.ReplaceServerPolicyAsync(PrivacyDefaultOff);
        await coordinator.InFlightApply;

        Assert.Equal("acme", Assert.Single(_shared.Privacy.PiiKeywords).Keyword);
        Assert.True(_shared.Privacy.TokenizationEnabled);
        Assert.False(service.IsRestartRequired);
    }

    [Fact]
    public async Task AnUnpinnedPrivacyKey_NeverRequiresARestart()
    {
        var service = await LoadedService(PrivacyOff);
        LatchNow();
        service.ApplyPolicy(_shared);
        var coordinator = Coordinate(service);

        await service.ReplaceServerPolicyAsync("{}");
        await coordinator.InFlightApply;

        // Nothing restores what the pin displaced, so the applied value stays stale on purpose: only a key
        // the new document still sets may flag a restart.
        Assert.False(service.IsEnforced(nameof(AppSettings.Privacy)));
        Assert.False(_shared.Privacy.TokenizationEnabled);
        Assert.True(TokenizationLatch.IsStale(_shared.Privacy));
        Assert.False(service.IsRestartRequired);
    }

    /// <summary>Both documents wipe the user's keywords, so the second change does reach the setter.</summary>
    [Fact]
    public async Task ASecondRestartRequiringChange_RaisesNothingFurther()
    {
        var service = await LoadedService();
        _shared.Privacy = new PrivacySettings { PiiKeywords = [Keyword("acme")] };
        LatchNow();
        var coordinator = Coordinate(service);
        var raised = 0;
        service.RestartRequiredChanged += (_, _) => raised++;

        await service.ReplaceServerPolicyAsync(PrivacyOff);
        await coordinator.InFlightApply;
        await service.ReplaceServerPolicyAsync(PrivacyOn);
        await coordinator.InFlightApply;

        Assert.True(TokenizationLatch.IsStale(_shared.Privacy));
        Assert.True(service.IsRestartRequired);
        Assert.Equal(1, raised);
    }

    /// <summary>Multicast invocation follows subscription order, so a second subscriber would make the
    /// lock-after-value ordering depend on construction order without failing a single test.</summary>
    [Fact]
    public void NothingElseSubscribesToPolicyChanged()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Pia.Wpf")))
            dir = dir.Parent;
        Assert.NotNull(dir);

        var subscribing = Directory
            .EnumerateFiles(Path.Combine(dir!.FullName, "src", "Pia.Wpf"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(f => Regex.IsMatch(File.ReadAllText(f), @"PolicyChanged\s*\+="))
            .Select(f => Path.GetFileName(f))
            .ToArray();

        Assert.Equal(new[] { nameof(PolicyChangeCoordinator) + ".cs" }, subscribing);
    }

    [Fact]
    public async Task AFailingSave_IsSwallowed()
    {
        var service = await LoadedService("""{ "enforce": { "uiLanguage": "DE" } }""");
        FailTheSave();
        var coordinator = Coordinate(service);

        await service.ReplaceServerPolicyAsync("""{ "enforce": { "uiLanguage": "FR" } }""");
        await coordinator.InFlightApply;

        Assert.True(service.IsEnforced(nameof(AppSettings.UiLanguage)));
    }

    /// <summary>The settings instance has already moved by the time a save can fail, so the locks and the
    /// restart flag still have to catch up with it.</summary>
    [Fact]
    public async Task AFailingSave_StillRefreshesTheLocksAndFlagsTheRestart()
    {
        var service = await LoadedService();
        var locks = 0;
        service.LocksChanged += (_, _) => locks++;
        FailTheSave();
        LatchNow();
        var coordinator = Coordinate(service);

        await service.ReplaceServerPolicyAsync(PrivacyOff);
        await coordinator.InFlightApply;

        Assert.Equal(1, locks);
        Assert.False(_shared.Privacy.TokenizationEnabled);
        Assert.True(service.IsRestartRequired);
    }

    [Fact]
    public async Task AThrowingLockSubscriber_StillFlagsTheRestart()
    {
        var service = await LoadedService();
        service.LocksChanged += (_, _) => throw new InvalidOperationException("a lock subscriber threw");
        LatchNow();
        var coordinator = Coordinate(service);

        await service.ReplaceServerPolicyAsync(PrivacyOff);
        await coordinator.InFlightApply;

        Assert.True(service.IsRestartRequired);
    }

    /// <summary>The raise happens after the write gate is released, so a diff can reach the coordinator once
    /// a later change has already withdrawn the key and the apply moves nothing.</summary>
    [Fact]
    public async Task ADiffWhoseValueDidNotMove_RefreshesTheLocksWithoutFlaggingARestart()
    {
        var policy = Substitute.For<IPolicyService>();
        LatchNow();
        var coordinator = Coordinate(policy);

        policy.PolicyChanged += Raise.EventWith(PrivacyValueChanged());
        await coordinator.InFlightApply;

        policy.Received(1).NotifyLocksChanged();
        policy.DidNotReceive().SetRestartRequired();
    }
}
