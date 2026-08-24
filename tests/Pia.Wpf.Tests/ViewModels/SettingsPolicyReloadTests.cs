using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Windows.Data;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Tests.TestInfrastructure;
using Pia.ViewModels;
using Pia.ViewModels.Models;
using Xunit;

namespace Pia.Tests.ViewModels;

/// <summary>
/// A server policy moves values under an open Settings page; without a reload the stale mirror gets
/// saved back, which permanently clobbers a policy <c>defaults</c> key.
/// </summary>
public class SettingsPolicyReloadTests : IDisposable
{
    private readonly string _testDir;

    public SettingsPolicyReloadTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"pia-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
            Directory.Delete(_testDir, true);
    }

    private sealed class FakeSettingsService : ISettingsService
    {
        public AppSettings Stored { get; } = new();
        public int SaveCount { get; private set; }

        public event EventHandler<AppSettings>? SettingsChanged;

        public Task<AppSettings> GetSettingsAsync() => Task.FromResult(Stored);

        public Task SaveSettingsAsync(AppSettings settings)
        {
            SaveCount++;
            return Task.CompletedTask;
        }

        public Task SaveDraftAsync(string? draftText) => Task.CompletedTask;

        public Task<string?> GetDraftAsync() => Task.FromResult<string?>(null);

        // Deliberately not raised from SaveSettingsAsync: a reload that saved would fan out forever, and
        // SaveCount is what proves it does not.
        public void RaiseSettingsChanged() => SettingsChanged?.Invoke(this, Stored);
    }

    /// <summary>Points the real store at a directory of its own; everything else is production code.</summary>
    private sealed class TempDirSettingsService : SettingsService
    {
        private readonly string _directory;

        public TempDirSettingsService(string directory, IPolicyService policyService)
            : base(NullLogger<SettingsService>.Instance, policyService) => _directory = directory;

        protected override string DirectoryPath => _directory;
    }

    private sealed record Suite(
        FakeSettingsService Settings,
        IPolicyService Policy,
        ILocalizationService Localization,
        AccountSettingsViewModel Account,
        AssistantSettingsViewModel Assistant,
        GeneralSettingsViewModel General,
        MeetingSettingsViewModel Meeting,
        OptimizeSettingsViewModel Optimize,
        PersonaSettingsViewModel Persona,
        PrivacySettingsViewModel Privacy,
        ProvidersSettingsViewModel Providers)
    {
        public IEnumerable<IDisposable> All =>
            [Account, Assistant, General, Meeting, Optimize, Persona, Privacy, Providers];

        public IEnumerable<PolicyLock> AllLocks =>
            [Account.Policy, Assistant.Policy, General.Policy, Meeting.Policy, Optimize.Policy,
             Persona.Policy, Privacy.Policy, Providers.Policy];
    }

    /// <summary>All eight share one settings service and one policy service, exactly as
    /// <see cref="SettingsViewModel"/> wires them, so one raise exercises every handler.</summary>
    private static Suite CreateSuite()
    {
        // AccountSettingsViewModel demands a captured context; inline keeps the assertions synchronous.
        SynchronizationContext.SetSynchronizationContext(new InlineSyncContext());

        var settings = new FakeSettingsService();
        var policy = Substitute.For<IPolicyService>();
        var logger = NullLogger<SettingsViewModel>.Instance;

        var localization = Substitute.For<ILocalizationService>();
        localization.CurrentLanguage.Returns(TargetLanguage.EN);
        localization[Arg.Any<string>()].Returns("display");
        localization.Format(Arg.Any<string>(), Arg.Any<object[]>()).Returns("display");

        var dialogs = Substitute.For<IDialogService>();
        var snackbar = Substitute.For<global::Wpf.Ui.ISnackbarService>();

        var providers = new ProvidersSettingsViewModel(
            null!, logger, Substitute.For<IProviderService>(), settings, dialogs, snackbar,
            Substitute.For<IAuthService>(), localization, policy);

        var optimize = new OptimizeSettingsViewModel(
            providers, logger, Substitute.For<ITemplateService>(), settings,
            Substitute.For<ITextOptimizationService>(), dialogs, snackbar, localization, policy,
            Substitute.For<IAuthService>());

        var persona = new PersonaSettingsViewModel(
            logger, Substitute.For<IPersonaService>(), Substitute.For<IProviderService>(),
            Substitute.For<ITextOptimizationService>(), dialogs, snackbar, localization,
            Substitute.For<IAuthService>(), settings, policy);

        var meeting = new MeetingSettingsViewModel(logger, settings, localization, policy);

        var assistant = new AssistantSettingsViewModel(
            providers, persona,
            new ToolPermissionsSettingsViewModel(
                Substitute.For<IToolPermissionService>(), Substitute.For<IPluginService>(), logger),
            meeting, logger, settings, Substitute.For<IAssistantChatService>(), dialogs, localization,
            Substitute.For<IAssistantFolderRelocationService>(),
            Substitute.For<Pia.Services.IWorkingDirectoryService>(), policy);

        var privacy = new PrivacySettingsViewModel(logger, settings, policy);

        var general = new GeneralSettingsViewModel(
            logger, settings, Substitute.For<ITranscriptionService>(), dialogs,
            Substitute.For<ITrayIconService>(), Substitute.For<ITtsService>(), snackbar, localization,
            Substitute.For<IAutostartService>(), policy, privacy, Substitute.For<ISyncClientService>(),
            Substitute.For<IDiagnosticsExportService>());

        var account = new AccountSettingsViewModel(
            logger, settings, dialogs, snackbar, Substitute.For<IAuthService>(),
            Substitute.For<ISyncClientService>(), localization, Substitute.For<Pia.Services.E2EE.IDeviceManagementService>(),
            Substitute.For<Pia.Services.E2EE.IDeviceKeyService>(), Substitute.For<IMemoryService>(), policy,
            new E2EEOnboardingViewModel(
                Substitute.For<Pia.Services.E2EE.IDeviceManagementService>(),
                Substitute.For<Pia.Services.E2EE.IDeviceKeyService>(),
                Substitute.For<Pia.Services.E2EE.IE2EEService>(),
                Substitute.For<ISyncClientService>(), settings,
                NullLogger<E2EEOnboardingViewModel>.Instance));

        return new Suite(settings, policy, localization, account, assistant, general, meeting, optimize,
            persona, privacy, providers);
    }

    private static void MutateEverySurface(AppSettings stored)
    {
        stored.TrustSelfSignedCertificates = true;
        stored.AgentMaxSteps = 7;
        stored.AutoCaptureSelectedText = true;
        stored.MeetingMaxSpeakers = 5;
        stored.AutoTypeDelayMs = 42;
        stored.AllowPersonaManagement = false;
        stored.Privacy.TokenizationEnabled = true;
        stored.UseSameProviderForAllModes = false;
        stored.AllowProviderManagement = false;
    }

    [Fact]
    public void SettingsChanged_ReloadsEverySettingsViewModel()
    {
        var suite = CreateSuite();

        // Non-vacuity: every assertion below has to be a move, not the value the VM already had.
        Assert.False(suite.Account.TrustSelfSignedCertificates);
        Assert.NotEqual(7, suite.Assistant.AgentMaxSteps);
        Assert.False(suite.General.AutoCaptureSelectedText);
        Assert.NotEqual(5, suite.Meeting.MeetingMaxSpeakers);
        Assert.NotEqual(42, suite.Optimize.AutoTypeDelayMs);
        Assert.True(suite.Persona.CanManagePersonas);
        Assert.False(suite.Privacy.TokenizationEnabled);
        Assert.True(suite.Providers.UseSameProviderForAllModes);
        Assert.True(suite.Providers.CanManageProviders);

        MutateEverySurface(suite.Settings.Stored);
        suite.Settings.RaiseSettingsChanged();

        Assert.True(suite.Account.TrustSelfSignedCertificates);
        Assert.Equal(7, suite.Assistant.AgentMaxSteps);
        Assert.True(suite.General.AutoCaptureSelectedText);
        Assert.Equal(5, suite.Meeting.MeetingMaxSpeakers);
        Assert.Equal(42, suite.Optimize.AutoTypeDelayMs);
        Assert.False(suite.Persona.CanManagePersonas);
        Assert.True(suite.Privacy.TokenizationEnabled);
        Assert.False(suite.Providers.UseSameProviderForAllModes);
        Assert.False(suite.Providers.CanManageProviders);
    }

    [Fact]
    public void SettingsChanged_ReloadDoesNotSave()
    {
        var suite = CreateSuite();

        MutateEverySurface(suite.Settings.Stored);
        suite.Settings.RaiseSettingsChanged();

        Assert.Equal(0, suite.Settings.SaveCount);
    }

    /// <summary>The stored value is what an auto-detected first run never wrote, so the running language is
    /// <see cref="ILocalizationService.CurrentLanguage"/> and the mirror must leave it alone.</summary>
    [Fact]
    public void SettingsChanged_DoesNotMirrorAnUnenforcedUiLanguage()
    {
        var suite = CreateSuite();

        Assert.Equal(TargetLanguage.EN, suite.General.UiLanguage);

        suite.Settings.Stored.UiLanguage = TargetLanguage.DE;
        suite.Settings.Stored.AutoCaptureSelectedText = true;
        suite.Settings.RaiseSettingsChanged();

        // Non-vacuity: the same raise did reach the General mirror.
        Assert.True(suite.General.AutoCaptureSelectedText);
        Assert.Equal(TargetLanguage.EN, suite.General.UiLanguage);
        suite.Localization.DidNotReceive().SetLanguage(Arg.Any<TargetLanguage>());
    }

    /// <summary>Enforced, the combo greys out — so it has to grey out over the enforced language and not
    /// over the one the user can still read.</summary>
    [Fact]
    public void SettingsChanged_MirrorsAnEnforcedUiLanguage()
    {
        var suite = CreateSuite();
        suite.Policy.IsEnforced(nameof(AppSettings.UiLanguage)).Returns(true);

        Assert.Equal(TargetLanguage.EN, suite.General.UiLanguage);

        suite.Settings.Stored.UiLanguage = TargetLanguage.DE;
        suite.Settings.RaiseSettingsChanged();

        Assert.Equal(TargetLanguage.DE, suite.General.UiLanguage);
        Assert.Equal(0, suite.Settings.SaveCount);
        // The running language follows in a later phase; flipping this is that phase's marker.
        suite.Localization.DidNotReceive().SetLanguage(Arg.Any<TargetLanguage>());
    }

    /// <summary>The reload must not re-resolve a mode default against a provider list that has not caught
    /// up: writing the fallback back would strand the policy default on this device for good.</summary>
    [Fact]
    public void SettingsChanged_KeepsAModeDefaultTheProviderListDoesNotHaveYet()
    {
        var suite = CreateSuite();
        var known = new AiProvider { Name = "known", Endpoint = "http://localhost" };
        suite.Providers.Providers.Add(known);

        suite.Settings.Stored.SetProviderForMode(WindowMode.Assistant, Guid.NewGuid());
        suite.Settings.RaiseSettingsChanged();

        Assert.Null(suite.Providers.AssistantProviderId);

        // Non-vacuity: the same reload does move the id once the list actually has that provider.
        suite.Settings.Stored.SetProviderForMode(WindowMode.Assistant, known.Id);
        suite.Settings.RaiseSettingsChanged();

        Assert.Equal(known.Id, suite.Providers.AssistantProviderId);
    }

    [Fact]
    public void Dispose_StopsTheReloadAndTheLockRaise()
    {
        var suite = CreateSuite();
        var indexerRaises = 0;

        foreach (var policyLock in suite.AllLocks)
            ((INotifyPropertyChanged)policyLock).PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == Binding.IndexerName)
                    indexerRaises++;
            };

        // Non-vacuity: the hook is live before the dispose, so the unchanged count below is the unsubscribe.
        suite.Policy.LocksChanged += Raise.EventWith(EventArgs.Empty);
        Assert.Equal(8, indexerRaises);

        foreach (var vm in suite.All)
            vm.Dispose();

        MutateEverySurface(suite.Settings.Stored);
        suite.Settings.RaiseSettingsChanged();
        suite.Policy.LocksChanged += Raise.EventWith(EventArgs.Empty);

        Assert.Equal(8, indexerRaises);
        Assert.False(suite.Account.TrustSelfSignedCertificates);
        Assert.NotEqual(7, suite.Assistant.AgentMaxSteps);
        Assert.False(suite.General.AutoCaptureSelectedText);
        Assert.NotEqual(5, suite.Meeting.MeetingMaxSpeakers);
        Assert.NotEqual(42, suite.Optimize.AutoTypeDelayMs);
        Assert.True(suite.Persona.CanManagePersonas);
        Assert.False(suite.Privacy.TokenizationEnabled);
        Assert.True(suite.Providers.UseSameProviderForAllModes);
    }

    [Fact]
    public void LocksChanged_RaisesTheIndexerAndTheEnforcementGetters()
    {
        var suite = CreateSuite();
        var raised = new List<string>();

        Track(suite.Account, suite.Account.Policy);
        Track(suite.General, suite.General.Policy);
        Track(suite.Optimize, suite.Optimize.Policy);
        Track(suite.Providers, suite.Providers.Policy);
        Track(suite.Assistant, suite.Assistant.Policy);
        Track(suite.Meeting, suite.Meeting.Policy);

        suite.Policy.LocksChanged += Raise.EventWith(EventArgs.Empty);

        Assert.Equal(6, raised.Count(name => name == $"Policy.{Binding.IndexerName}"));
        Assert.Contains(nameof(AccountSettingsViewModel.IsServerUrlEditable), raised);
        Assert.Contains(nameof(GeneralSettingsViewModel.IsUiLanguageEnforced), raised);
        Assert.Contains(nameof(GeneralSettingsViewModel.IsStartMinimizedEnforced), raised);
        Assert.Contains(nameof(GeneralSettingsViewModel.IsLaunchAtStartupEnforced), raised);
        Assert.Contains(nameof(GeneralSettingsViewModel.IsSttBackendEnforced), raised);
        Assert.Contains(nameof(GeneralSettingsViewModel.IsWhisperModelEnforced), raised);
        Assert.Contains(nameof(GeneralSettingsViewModel.IsTargetSpeechLanguageEnforced), raised);
        Assert.Contains(nameof(OptimizeSettingsViewModel.IsOutputActionEnforced), raised);
        Assert.Contains(nameof(OptimizeSettingsViewModel.IsAutoTypeDelayEnforced), raised);
        Assert.Contains(nameof(ProvidersSettingsViewModel.IsUseSameProviderEnforced), raised);
        // Both AND the lock into their own value, so the indexer raise alone would not reach them.
        Assert.Contains(nameof(AssistantSettingsViewModel.GitToolsEditable), raised);
        Assert.Contains(nameof(MeetingSettingsViewModel.SmartSpeakerDetectionEditable), raised);
        // allowedSyncProviders is read through IsLoginProviderAllowed, which the indexer cannot reach.
        Assert.Contains(nameof(AccountSettingsViewModel.IsLocalLoginVisible), raised);
        Assert.Contains(nameof(AccountSettingsViewModel.IsGoogleLoginVisible), raised);
        Assert.Contains(nameof(AccountSettingsViewModel.IsMicrosoftLoginVisible), raised);
        Assert.Contains(nameof(AccountSettingsViewModel.IsEntraIdLoginVisible), raised);
        Assert.Contains(nameof(AccountSettingsViewModel.IsAnyOAuthLoginVisible), raised);

        void Track(INotifyPropertyChanged vm, PolicyLock policyLock)
        {
            vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName!);
            ((INotifyPropertyChanged)policyLock).PropertyChanged += (_, e) => raised.Add($"Policy.{e.PropertyName}");
        }
    }

    /// <summary>Reflected rather than listed: a ninth settings VM inherits the PolicyLock behaviour for
    /// free but not the unsubscribe, and its handler would outlive its window.</summary>
    [Fact]
    public void EverySettingsViewModelHoldingAPolicyLock_IsDisposable()
    {
        var holders = typeof(SettingsViewModel).Assembly.GetTypes()
            .Where(t => t.GetProperty("Policy", BindingFlags.Public | BindingFlags.Instance)
                ?.PropertyType == typeof(PolicyLock))
            .ToList();

        Assert.Equal(8, holders.Count);

        var leaking = holders.Where(t => !typeof(IDisposable).IsAssignableFrom(t))
            .Select(t => t.Name).ToArray();
        Assert.True(leaking.Length == 0,
            "these settings ViewModels subscribe to singleton services but cannot be unsubscribed: "
            + string.Join(", ", leaking));

        Assert.True(typeof(IDisposable).IsAssignableFrom(typeof(SettingsViewModel)),
            "SettingsViewModel owns all eight, so the DI scope has to reach them through its Dispose.");
    }

    /// <summary>Meeting and Privacy are constructed as locals and reachable only through another sub-VM, so
    /// only the real graph proves the window's one Dispose reaches all eight.</summary>
    private sealed record Page(SettingsViewModel Root, FakeSettingsService Settings, IPolicyService Policy)
    {
        public IEnumerable<PolicyLock> AllLocks =>
            [Root.AccountVm.Policy, Root.AssistantVm.Policy, Root.AssistantVm.MeetingVm.Policy,
             Root.GeneralVm.Policy, Root.GeneralVm.PrivacyVm.Policy, Root.OptimizeVm.Policy,
             Root.PersonasVm.Policy, Root.ProvidersVm.Policy];
    }

    private static Page CreatePage()
    {
        SynchronizationContext.SetSynchronizationContext(new InlineSyncContext());

        var settings = new FakeSettingsService();
        var policy = Substitute.For<IPolicyService>();

        var localization = Substitute.For<ILocalizationService>();
        localization.CurrentLanguage.Returns(TargetLanguage.EN);
        localization[Arg.Any<string>()].Returns("display");
        localization.Format(Arg.Any<string>(), Arg.Any<object[]>()).Returns("display");

        var root = new SettingsViewModel(
            NullLogger<SettingsViewModel>.Instance,
            Substitute.For<IProviderService>(),
            Substitute.For<ITemplateService>(),
            settings,
            Substitute.For<IAiClientService>(),
            Substitute.For<ITextOptimizationService>(),
            Substitute.For<ITranscriptionService>(),
            Substitute.For<Pia.Navigation.INavigationService>(),
            Substitute.For<IDialogService>(),
            Substitute.For<ITrayIconService>(),
            Substitute.For<ITtsService>(),
            Substitute.For<global::Wpf.Ui.ISnackbarService>(),
            Substitute.For<IAuthService>(),
            Substitute.For<ISyncClientService>(),
            localization,
            Substitute.For<Pia.Services.E2EE.IDeviceManagementService>(),
            Substitute.For<Pia.Services.E2EE.IDeviceKeyService>(),
            Substitute.For<IMemoryService>(),
            new E2EEOnboardingViewModel(
                Substitute.For<Pia.Services.E2EE.IDeviceManagementService>(),
                Substitute.For<Pia.Services.E2EE.IDeviceKeyService>(),
                Substitute.For<Pia.Services.E2EE.IE2EEService>(),
                Substitute.For<ISyncClientService>(), settings,
                NullLogger<E2EEOnboardingViewModel>.Instance),
            Substitute.For<IAutostartService>(),
            Substitute.For<IPluginService>(),
            Substitute.For<IPluginIconLoader>(),
            policy,
            Substitute.For<IPersonaService>(),
            Substitute.For<IAssistantChatService>(),
            Substitute.For<IToolPermissionService>(),
            Substitute.For<IAssistantFolderRelocationService>(),
            Substitute.For<Pia.Services.IWorkingDirectoryService>(),
            Substitute.For<IDiagnosticsExportService>());

        return new Page(root, settings, policy);
    }

    [Fact]
    public void DisposingTheSettingsPage_StopsEverySubViewModel()
    {
        var page = CreatePage();
        var root = page.Root;
        var indexerRaises = 0;

        foreach (var policyLock in page.AllLocks)
            ((INotifyPropertyChanged)policyLock).PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == Binding.IndexerName)
                    indexerRaises++;
            };

        MutateEverySurface(page.Settings.Stored);
        page.Settings.RaiseSettingsChanged();
        page.Policy.LocksChanged += Raise.EventWith(EventArgs.Empty);

        // Non-vacuity: every handler is live before the dispose, so the unchanged values below are the
        // unsubscribe and not a subscription that was never made.
        Assert.Equal(8, indexerRaises);
        Assert.True(root.AccountVm.TrustSelfSignedCertificates);
        Assert.Equal(7, root.AssistantVm.AgentMaxSteps);
        Assert.Equal(5, root.AssistantVm.MeetingVm.MeetingMaxSpeakers);
        Assert.True(root.GeneralVm.AutoCaptureSelectedText);
        Assert.True(root.GeneralVm.PrivacyVm.TokenizationEnabled);
        Assert.Equal(42, root.OptimizeVm.AutoTypeDelayMs);
        Assert.False(root.PersonasVm.CanManagePersonas);
        Assert.False(root.ProvidersVm.UseSameProviderForAllModes);
        Assert.False(root.ProvidersVm.CanManageProviders);

        root.Dispose();

        // Distinguishable from the first raise, so a handler that survived cannot look like one that did not.
        page.Settings.Stored.TrustSelfSignedCertificates = false;
        page.Settings.Stored.AgentMaxSteps = 9;
        page.Settings.Stored.MeetingMaxSpeakers = 9;
        page.Settings.Stored.AutoCaptureSelectedText = false;
        page.Settings.Stored.Privacy.TokenizationEnabled = false;
        page.Settings.Stored.AutoTypeDelayMs = 99;
        page.Settings.Stored.AllowPersonaManagement = true;
        page.Settings.Stored.UseSameProviderForAllModes = true;
        page.Settings.Stored.AllowProviderManagement = true;

        page.Settings.RaiseSettingsChanged();
        page.Policy.LocksChanged += Raise.EventWith(EventArgs.Empty);

        Assert.Equal(8, indexerRaises);
        Assert.True(root.AccountVm.TrustSelfSignedCertificates);
        Assert.Equal(7, root.AssistantVm.AgentMaxSteps);
        Assert.Equal(5, root.AssistantVm.MeetingVm.MeetingMaxSpeakers);
        Assert.True(root.GeneralVm.AutoCaptureSelectedText);
        Assert.True(root.GeneralVm.PrivacyVm.TokenizationEnabled);
        Assert.Equal(42, root.OptimizeVm.AutoTypeDelayMs);
        Assert.False(root.PersonasVm.CanManagePersonas);
        Assert.False(root.ProvidersVm.UseSameProviderForAllModes);
        Assert.False(root.ProvidersVm.CanManageProviders);
    }

    /// <summary>What the reload's safety rests on: with the read warm, a ViewModel's save writes
    /// <c>settings.X = X</c> before it can suspend, so a reload can never land inside a save.</summary>
    [Fact]
    public async Task AWarmSettingsRead_CompletesWithoutSuspending()
    {
        var policyService = new PolicyService(
            NullLogger<PolicyService>.Instance,
            Path.Combine(_testDir, "policy.json"),
            Path.Combine(_testDir, "cache"));
        var settingsService = new TempDirSettingsService(_testDir, policyService);

        var first = await settingsService.GetSettingsAsync();
        var warm = settingsService.GetSettingsAsync();

        Assert.True(warm.IsCompleted, "GetSettingsAsync suspended on a warm read");
        Assert.Same(first, await warm);
    }
}
