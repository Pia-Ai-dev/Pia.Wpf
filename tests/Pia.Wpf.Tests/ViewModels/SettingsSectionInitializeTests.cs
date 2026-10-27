using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Pia.Models;
using Pia.Services;
using Pia.Services.Credits;
using Pia.Services.Interfaces;
using Pia.Services.Screen;
using Pia.Tests.TestInfrastructure;
using Pia.ViewModels;
using Xunit;

namespace Pia.Tests.ViewModels;

/// <summary>A pending clear that failed leaves a store file locked, and that section's load must not
/// keep the sections after it from loading.</summary>
public class SettingsSectionInitializeTests
{
    private readonly AppSettings _stored = new()
    {
        ServerUrl = "https://localhost:18081",
        TrustSelfSignedCertificates = true,
    };

    private readonly IProviderService _providers = Substitute.For<IProviderService>();

    private SettingsViewModel CreateRoot()
    {
        SynchronizationContext.SetSynchronizationContext(new InlineSyncContext());

        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(_stored);
        var policy = Substitute.For<IPolicyService>();

        var localization = Substitute.For<ILocalizationService>();
        localization.CurrentLanguage.Returns(TargetLanguage.EN);
        localization[Arg.Any<string>()].Returns("display");
        localization.Format(Arg.Any<string>(), Arg.Any<object[]>()).Returns("display");

        var allowlist = Substitute.For<IScreenCaptureAllowlistStore>();
        allowlist.ListAsync().Returns(Task.FromResult<IReadOnlyList<ScreenCaptureAllowlistEntry>>([]));

        return new SettingsViewModel(
            NullLogger<SettingsViewModel>.Instance,
            _providers,
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
            Substitute.For<IDiagnosticsExportService>(),
            allowlist,
            Substitute.For<IAdvancedCreationLauncher>(),
            Substitute.For<IAccountDataService>(),
            Substitute.For<IFileDialogService>(),
            Substitute.For<ICreditStatusService>(),
            Substitute.For<ILocalDataResetService>());
    }

    [Fact]
    public async Task ALockedProvidersFile_StillLoadsTheServerUrlAndTrustSetting()
    {
        _providers.RepairModeDefaultsAsync()
            .ThrowsAsync(new IOException("The process cannot access the file 'providers.json'."));
        using var root = CreateRoot();

        await root.OnNavigatedToAsync(null);

        Assert.Equal("https://localhost:18081", root.AccountVm.ServerUrl);
        Assert.True(root.AccountVm.TrustSelfSignedCertificates);
    }
}
