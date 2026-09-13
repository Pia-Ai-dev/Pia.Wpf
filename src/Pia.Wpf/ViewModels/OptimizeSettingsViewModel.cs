using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Pia.Helpers;
using Pia.Models;
using Pia.Services.Interfaces;
using Pia.ViewModels.Models;

namespace Pia.ViewModels;

public partial class OptimizeSettingsViewModel : UiThreadViewModel, IDisposable
{
    private readonly ILogger<SettingsViewModel> _logger;
    private readonly ISettingsService _settingsService;
    private readonly IPolicyService _policyService;
    private readonly ProvidersSettingsViewModel _providersVm;
    private readonly TemplatesSettingsViewModel _templatesVm;
    private bool _isLoading;
    private bool _disposed;

    /// <summary>Bind IsEnabled to Policy[nameof(AppSettings.X)] to grey a control out while policy enforces it.</summary>
    public PolicyLock Policy { get; }

    public OptimizeSettingsViewModel(
        ProvidersSettingsViewModel providersVm,
        TemplatesSettingsViewModel templatesVm,
        ILogger<SettingsViewModel> logger,
        ISettingsService settingsService,
        IPolicyService policyService)
    {
        _providersVm = providersVm;
        _templatesVm = templatesVm;
        _logger = logger;
        _settingsService = settingsService;
        _policyService = policyService;
        Policy = new PolicyLock(policyService);

        _policyService.LocksChanged += OnLocksChanged;
        _settingsService.SettingsChanged += OnSettingsChanged;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _policyService.LocksChanged -= OnLocksChanged;
        _settingsService.SettingsChanged -= OnSettingsChanged;
        Policy.Dispose();
        GC.SuppressFinalize(this);
    }

    // Enterprise policy enforcement
    public bool IsOutputActionEnforced => _policyService.IsEnforced(nameof(AppSettings.DefaultOutputAction));
    public bool IsAutoTypeDelayEnforced => _policyService.IsEnforced(nameof(AppSettings.AutoTypeDelayMs));

    // The indexer bindings are covered by PolicyLock; these getters are separate binding targets.
    private void OnLocksChanged(object? sender, EventArgs e) => Post(() =>
    {
        OnPropertyChanged(nameof(IsOutputActionEnforced));
        OnPropertyChanged(nameof(IsAutoTypeDelayEnforced));
    });

    public ProvidersSettingsViewModel ProvidersVm => _providersVm;

    public TemplatesSettingsViewModel TemplatesVm => _templatesVm;

    [ObservableProperty]
    private OutputAction _outputAction;

    [ObservableProperty]
    private int _autoTypeDelayMs;

    public IEnumerable<OutputAction> OutputActions => Enum.GetValues<OutputAction>();

    partial void OnOutputActionChanged(OutputAction value)
    {
        if (!_isLoading) SaveSettingsAsync().SafeFireAndForget(_logger);
    }

    partial void OnAutoTypeDelayMsChanged(int value)
    {
        if (!_isLoading) SaveSettingsAsync().SafeFireAndForget(_logger);
    }

    public async Task InitializeAsync()
    {
        _isLoading = true;
        ApplySettings(await _settingsService.GetSettingsAsync());
        _isLoading = false;

        await _templatesVm.InitializeAsync();
    }

    // Raised from the policy pull thread, so the mirror has to be marshalled.
    private void OnSettingsChanged(object? sender, AppSettings settings) => Post(() =>
    {
        _isLoading = true;
        ApplySettings(settings);
        _isLoading = false;
    });

    private void ApplySettings(AppSettings settings)
    {
        OutputAction = settings.DefaultOutputAction;
        AutoTypeDelayMs = settings.AutoTypeDelayMs;
    }

    private async Task SaveSettingsAsync()
    {
        var settings = await _settingsService.GetSettingsAsync();
        settings.DefaultOutputAction = OutputAction;
        settings.AutoTypeDelayMs = AutoTypeDelayMs;
        await _settingsService.SaveSettingsAsync(settings);
    }
}
