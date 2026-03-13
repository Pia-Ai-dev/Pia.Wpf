using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Pia.Models;
using Pia.Services.Interfaces;
using Pia.ViewModels.Models;
using System.Collections.ObjectModel;

namespace Pia.ViewModels;

public partial class OptimizeSettingsViewModel : ObservableObject
{
    private readonly ILogger<SettingsViewModel> _logger;
    private readonly ITemplateService _templateService;
    private readonly ISettingsService _settingsService;
    private readonly ITextOptimizationService _textOptimizationService;
    private readonly IDialogService _dialogService;
    private readonly Wpf.Ui.ISnackbarService _snackbarService;
    private readonly ILocalizationService _localizationService;
    private readonly ProvidersSettingsViewModel _providersVm;
    private bool _isLoading;

    public OptimizeSettingsViewModel(
        ProvidersSettingsViewModel providersVm,
        ILogger<SettingsViewModel> logger,
        ITemplateService templateService,
        ISettingsService settingsService,
        ITextOptimizationService textOptimizationService,
        IDialogService dialogService,
        Wpf.Ui.ISnackbarService snackbarService,
        ILocalizationService localizationService)
    {
        _providersVm = providersVm;
        _logger = logger;
        _templateService = templateService;
        _settingsService = settingsService;
        _textOptimizationService = textOptimizationService;
        _dialogService = dialogService;
        _snackbarService = snackbarService;
        _localizationService = localizationService;
        Templates = new ObservableCollection<OptimizationTemplate>();
    }

    // Expose provider VM for bindings
    public ProvidersSettingsViewModel ProvidersVm => _providersVm;

    [ObservableProperty]
    private ObservableCollection<OptimizationTemplate> _templates;

    [ObservableProperty]
    private Guid? _defaultTemplateId;

    [ObservableProperty]
    private OutputAction _outputAction;

    [ObservableProperty]
    private int _autoTypeDelayMs;

    public IEnumerable<OutputAction> OutputActions => Enum.GetValues<OutputAction>();

    partial void OnDefaultTemplateIdChanged(Guid? value)
    {
        if (!_isLoading) SafeFireAndForget(SaveSettingsAsync());
    }

    partial void OnOutputActionChanged(OutputAction value)
    {
        if (!_isLoading) SafeFireAndForget(SaveSettingsAsync());
    }

    partial void OnAutoTypeDelayMsChanged(int value)
    {
        if (!_isLoading) SafeFireAndForget(SaveSettingsAsync());
    }

    public async Task InitializeAsync()
    {
        _isLoading = true;

        var templatesList = await _templateService.GetTemplatesAsync();
        foreach (var template in templatesList)
            Templates.Add(template);

        var settings = await _settingsService.GetSettingsAsync();
        DefaultTemplateId = settings.DefaultTemplateId;
        OutputAction = settings.DefaultOutputAction;
        AutoTypeDelayMs = settings.AutoTypeDelayMs;

        _isLoading = false;
    }

    [RelayCommand]
    private async Task AddTemplateAsync()
    {
        var editModel = new TemplateEditModel(_textOptimizationService);

        if (await _dialogService.ShowTemplateEditDialogAsync(editModel))
        {
            await _templateService.AddTemplateAsync(editModel.ToTemplate());
            await RefreshTemplatesAsync();
            _snackbarService.Show(_localizationService["Msg_Success"], _localizationService["Msg_Settings_TemplateAdded"], Wpf.Ui.Controls.ControlAppearance.Success, null, TimeSpan.FromSeconds(3));
        }
    }

    [RelayCommand]
    private async Task ViewTemplatePromptAsync(OptimizationTemplate? template)
    {
        if (template is null)
            return;

        var prompt = template.IsBuiltIn
            ? template.Prompt
            : _localizationService["Msg_Settings_CustomTemplatePromptInfo"];

        await _dialogService.ShowMessageDialogAsync(template.Name, prompt);
    }

    [RelayCommand]
    private async Task EditTemplateAsync(OptimizationTemplate? template)
    {
        if (template is null || template.IsBuiltIn)
            return;

        var editModel = TemplateEditModel.FromTemplate(template, _textOptimizationService);

        if (await _dialogService.ShowTemplateEditDialogAsync(editModel))
        {
            await _templateService.UpdateTemplateAsync(editModel.ToTemplate());
            await RefreshTemplatesAsync();
            _snackbarService.Show(_localizationService["Msg_Success"], _localizationService["Msg_Settings_TemplateUpdated"], Wpf.Ui.Controls.ControlAppearance.Success, null, TimeSpan.FromSeconds(3));
        }
    }

    [RelayCommand(CanExecute = nameof(CanDeleteTemplate))]
    private async Task DeleteTemplateAsync(OptimizationTemplate? template)
    {
        if (template is null)
            return;

        if (template.IsBuiltIn)
        {
            _snackbarService.Show(_localizationService["Msg_Warning"], _localizationService["Msg_Settings_CannotDeleteBuiltInTemplate"], Wpf.Ui.Controls.ControlAppearance.Caution, null, TimeSpan.FromSeconds(3));
            return;
        }

        if (template.Id == DefaultTemplateId)
        {
            _snackbarService.Show(_localizationService["Msg_Warning"], _localizationService["Msg_Settings_CannotDeleteDefaultTemplate"], Wpf.Ui.Controls.ControlAppearance.Caution, null, TimeSpan.FromSeconds(3));
            return;
        }

        await _templateService.DeleteTemplateAsync(template.Id);
        await RefreshTemplatesAsync();
        _snackbarService.Show(_localizationService["Msg_Success"], _localizationService["Msg_Settings_TemplateDeleted"], Wpf.Ui.Controls.ControlAppearance.Success, null, TimeSpan.FromSeconds(3));
    }

    [RelayCommand]
    private async Task SetDefaultTemplateAsync(OptimizationTemplate? template)
    {
        if (template is null)
            return;

        _isLoading = true;
        try
        {
            DefaultTemplateId = template.Id;
            await SaveSettingsAsync();
        }
        finally
        {
            _isLoading = false;
        }
    }

    private bool CanDeleteTemplate(OptimizationTemplate? template)
    {
        return template != null && !template.IsBuiltIn && template.Id != DefaultTemplateId;
    }

    private async Task RefreshTemplatesAsync()
    {
        Templates.Clear();
        var templatesList = await _templateService.GetTemplatesAsync();
        foreach (var template in templatesList)
            Templates.Add(template);
    }

    private async Task SaveSettingsAsync()
    {
        var settings = await _settingsService.GetSettingsAsync();
        settings.DefaultTemplateId = DefaultTemplateId;
        settings.DefaultOutputAction = OutputAction;
        settings.AutoTypeDelayMs = AutoTypeDelayMs;
        await _settingsService.SaveSettingsAsync(settings);
    }

    private async void SafeFireAndForget(Task task)
    {
        try { await task; }
        catch (Exception ex) { _logger.LogError(ex, "Background operation failed"); }
    }
}
