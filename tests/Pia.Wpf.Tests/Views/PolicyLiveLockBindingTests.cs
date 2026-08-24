using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.ViewModels;
using Pia.Views.SettingsViews;
using Xunit;

namespace Pia.Tests.Views;

/// <summary>
/// The live-apply chain against realized markup: assertions target a DependencyProperty located by its
/// declared binding path, not the ViewModel property, which would prove nothing about the XAML.
/// </summary>
[Collection("WpfApplicationStatic")]
public class PolicyLiveLockBindingTests : IDisposable
{
    private const string EnforcedDocument = """
        { "enforce": { "autoCaptureSelectedText": true, "uiLanguage": "DE", "ttsVoiceModelKey": "de_DE-thorsten-medium" } }
        """;

    private readonly string _testDir;
    private readonly string _policyFilePath;
    private readonly string _cacheDir;

    // Held across Run bodies (the RunProgressPanelThemeSwitchTests shape); only ever touched on the host thread.
    private GeneralView? _view;
    private GeneralSettingsViewModel? _viewModel;
    private FrameworkElement? _autoCapture;
    private FrameworkElement? _uiLanguage;
    private FrameworkElement? _selectVoice;
    private int _hostThreadId;
    private int _indexerRaises;
    private int _offThreadRaises;

    public PolicyLiveLockBindingTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"pia-test-{Guid.NewGuid()}");
        _policyFilePath = Path.Combine(_testDir, "policy.json");
        _cacheDir = Path.Combine(_testDir, "cache");
        Directory.CreateDirectory(_cacheDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
            Directory.Delete(_testDir, true);
    }

    /// <summary>Mirrors <see cref="SettingsService"/> exactly: the same instance every time, the policy
    /// applied on read and again on write, and SettingsChanged only from the write.</summary>
    private sealed class PolicyAppliedSettingsService : ISettingsService
    {
        private readonly IPolicyService _policyService;
        private readonly AppSettings _cached = new();

        public event EventHandler<AppSettings>? SettingsChanged;

        public PolicyAppliedSettingsService(IPolicyService policyService) => _policyService = policyService;

        public async Task<AppSettings> GetSettingsAsync()
        {
            await _policyService.GetPolicyAsync();
            _policyService.ApplyPolicy(_cached);
            return _cached;
        }

        public Task SaveSettingsAsync(AppSettings settings)
        {
            _policyService.ApplyPolicy(settings);
            SettingsChanged?.Invoke(this, settings);
            return Task.CompletedTask;
        }

        public Task SaveDraftAsync(string? draftText) => Task.CompletedTask;

        public Task<string?> GetDraftAsync() => Task.FromResult<string?>(null);
    }

    [Fact]
    public async Task AChangedServerPolicy_LocksAndRefreshesTheRealizedBindings()
    {
        var policyService = new PolicyService(
            NullLogger<PolicyService>.Instance, _policyFilePath, _cacheDir);
        var settingsService = new PolicyAppliedSettingsService(policyService);
        var coordinator = new PolicyChangeCoordinator(
            policyService,
            settingsService,
            Substitute.For<IPolicyNotificationSurface>(),
            NullLogger<PolicyChangeCoordinator>.Instance);

        // Without a published snapshot the publish below defers to the first read and raises nothing.
        await policyService.GetPolicyAsync();

        WpfStaHost.Run(() => Realize(policyService, settingsService));
        WpfStaHost.Pump();

        var located = WpfStaHost.Run(Locate);
        Assert.Equal(
            "autoCapture=Policy[AutoCaptureSelectedText]; uiLanguage=IsUiLanguageEnforced/UiLanguage; "
            + "selectVoice=DataContext.Policy[TtsVoiceModelKey]",
            located);

        var chain = WpfStaHost.Run(BindingChain);
        Assert.Equal(
            "autoCapture=Active; autoCaptureIsChecked=Active; uiLanguage=Active; "
            + "uiLanguageSelectedItem=Active; selectVoice=Active",
            chain);

        Assert.Equal(
            "autoCaptureEditable=True; autoCaptureChecked=False; uiLanguageEditable=True; "
            + "uiLanguageSelected=EN; selectVoiceEditable=True; indexerRaises=0; offThreadRaises=0",
            WpfStaHost.Run(Snapshot));

        await policyService.ReplaceServerPolicyAsync(EnforcedDocument);
        // The raise starts the value move fire-and-forget; Pump drains the dispatcher, not that task.
        await coordinator.InFlightApply;
        WpfStaHost.Pump();

        // The lock halves prove PolicyLock and the per-VM raise fired; the checked half proves the
        // coordinator's Save reached the ViewModel, which is the other half of the mechanism. A greyed-out
        // control still showing the old value is the failure the value/lock ordering exists to prevent, so
        // the language combo is asserted on both halves at once.
        Assert.Equal(
            "autoCaptureEditable=False; autoCaptureChecked=True; uiLanguageEditable=False; "
            + "uiLanguageSelected=DE; selectVoiceEditable=False; indexerRaises=1; offThreadRaises=0",
            WpfStaHost.Run(Snapshot));

        // Negative control: the same document again must not re-publish, or the test above could be
        // passing off an incidental re-read rather than a detected change.
        await policyService.ReplaceServerPolicyAsync(EnforcedDocument);
        await coordinator.InFlightApply;
        WpfStaHost.Pump();

        Assert.Equal(
            "autoCaptureEditable=False; autoCaptureChecked=True; uiLanguageEditable=False; "
            + "uiLanguageSelected=DE; selectVoiceEditable=False; indexerRaises=1; offThreadRaises=0",
            WpfStaHost.Run(Snapshot));
    }

    private int Realize(IPolicyService policyService, ISettingsService settingsService)
    {
        var logger = NullLogger<SettingsViewModel>.Instance;
        var localization = Substitute.For<ILocalizationService>();
        localization.CurrentLanguage.Returns(TargetLanguage.EN);
        localization[Arg.Any<string>()].Returns("display");
        localization.Format(Arg.Any<string>(), Arg.Any<object[]>()).Returns("display");

        _viewModel = new GeneralSettingsViewModel(
            logger, settingsService, Substitute.For<ITranscriptionService>(),
            Substitute.For<IDialogService>(), Substitute.For<ITrayIconService>(),
            Substitute.For<ITtsService>(), Substitute.For<global::Wpf.Ui.ISnackbarService>(),
            localization, Substitute.For<IAutostartService>(), policyService,
            new PrivacySettingsViewModel(logger, settingsService, policyService),
            Substitute.For<ISyncClientService>(), Substitute.For<IDiagnosticsExportService>());

        // WPF already marshals a cross-thread source notification onto the target's dispatcher, so the
        // value alone would look right unmarshalled; counting off-host-thread arrivals is what pins the Post.
        _hostThreadId = Environment.CurrentManagedThreadId;
        ((INotifyPropertyChanged)_viewModel.Policy).PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != Binding.IndexerName)
                return;
            _indexerRaises++;
            CountThread();
        };
        _viewModel.PropertyChanged += (_, _) => CountThread();

        // The button lives in the TTS voice ItemTemplate, so the item has to exist before layout.
        _viewModel.TtsVoices.Add(new TtsVoice
        {
            Key = "en_US-lessac-medium",
            DisplayName = "Lessac",
            Language = "en_US",
            Quality = "medium",
            Gender = "F",
            SizeBytes = 1,
            IsDownloaded = true,
        });

        _view = new GeneralView { DataContext = _viewModel };
        Layout();

        // The Speech tab, so the ItemsControl generates the voice row. The Application tab's elements are
        // located from the logical tree, which does not need them realized.
        _viewModel.SelectedInnerTabIndex = 2;
        Layout();

        return 0;
    }

    private void CountThread()
    {
        if (Environment.CurrentManagedThreadId != _hostThreadId)
            _offThreadRaises++;
    }

    private void Layout()
    {
        _view!.Measure(new Size(1200, double.PositiveInfinity));
        _view.Arrange(new Rect(0, 0, 1200, Math.Max(1, _view.DesiredSize.Height)));
        _view.UpdateLayout();
    }

    private string Locate()
    {
        Layout();

        _autoCapture = ByEnabledPath(BindingPathWalker.FindLogical<FrameworkElement>(_view!),
            "Policy[AutoCaptureSelectedText]");
        _uiLanguage = ByEnabledPath(BindingPathWalker.FindLogical<FrameworkElement>(_view!),
            "IsUiLanguageEnforced");
        _selectVoice = ByEnabledPath(VisualDescendants(_view!).OfType<FrameworkElement>(),
            "DataContext.Policy[TtsVoiceModelKey]");

        return $"autoCapture={Describe(_autoCapture)}; "
            + $"uiLanguage={Describe(_uiLanguage)}/{DescribeSelection(_uiLanguage)}; "
            + $"selectVoice={Describe(_selectVoice)}";

        static FrameworkElement? ByEnabledPath(IEnumerable<FrameworkElement> candidates, string path) =>
            candidates.FirstOrDefault(e =>
                BindingPathWalker.PathOf(e, UIElement.IsEnabledProperty) == path);

        static string Describe(FrameworkElement? element) => element is null
            ? "NOT FOUND"
            : BindingPathWalker.PathOf(element, UIElement.IsEnabledProperty)!;

        static string DescribeSelection(FrameworkElement? element) => element is null
            ? "NOT FOUND"
            : BindingPathWalker.PathOf(element, Selector.SelectedItemProperty) ?? "NO EXPRESSION";
    }

    private string BindingChain() =>
        $"autoCapture={Status(_autoCapture, UIElement.IsEnabledProperty)}; "
        + $"autoCaptureIsChecked={Status(_autoCapture, ToggleButton.IsCheckedProperty)}; "
        + $"uiLanguage={Status(_uiLanguage, UIElement.IsEnabledProperty)}; "
        + $"uiLanguageSelectedItem={Status(_uiLanguage, Selector.SelectedItemProperty)}; "
        + $"selectVoice={Status(_selectVoice, UIElement.IsEnabledProperty)}";

    private static string Status(DependencyObject? element, DependencyProperty property) =>
        element is null
            ? "NO ELEMENT"
            : BindingOperations.GetBindingExpression(element, property)?.Status.ToString() ?? "NO EXPRESSION";

    private string Snapshot() =>
        $"autoCaptureEditable={_autoCapture!.IsEnabled}; "
        + $"autoCaptureChecked={((CheckBox)_autoCapture).IsChecked}; "
        + $"uiLanguageEditable={_uiLanguage!.IsEnabled}; "
        + $"uiLanguageSelected={((ComboBox)_uiLanguage).SelectedItem}; "
        + $"selectVoiceEditable={_selectVoice!.IsEnabled}; "
        + $"indexerRaises={_indexerRaises}; offThreadRaises={_offThreadRaises}";

    private static IEnumerable<DependencyObject> VisualDescendants(DependencyObject root)
    {
        yield return root;

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
            foreach (var descendant in VisualDescendants(VisualTreeHelper.GetChild(root, i)))
                yield return descendant;
    }
}
