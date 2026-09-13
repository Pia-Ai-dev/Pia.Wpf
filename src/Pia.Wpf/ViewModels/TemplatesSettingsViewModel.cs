using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Pia.Helpers;
using Pia.Models;
using Pia.Services.Interfaces;
using Pia.ViewModels.Models;

namespace Pia.ViewModels;

/// <summary>
/// Manages optimization templates (list / add / edit / delete / duplicate-a-built-in) as a master-detail
/// pane with an inline editor, mirroring <see cref="PersonaSettingsViewModel"/>. Built-ins are shown
/// read-only; Duplicate is the escape hatch.
/// </summary>
public partial class TemplatesSettingsViewModel : UiThreadViewModel, IDisposable
{
    private readonly ILogger<SettingsViewModel> _logger;
    private readonly ITemplateService _templateService;
    private readonly ISettingsService _settingsService;
    private readonly ITextOptimizationService _textOptimizationService;
    private readonly Wpf.Ui.ISnackbarService _snackbarService;
    private readonly ILocalizationService _localizationService;
    private readonly IAuthService _authService;
    private bool _isLoading;
    private bool _disposed;

    /// <summary>Bind IsEnabled to Policy[nameof(AppSettings.X)] to grey a control out while policy enforces it.</summary>
    public PolicyLock Policy { get; }

    [ObservableProperty]
    private ObservableCollection<OptimizationTemplate> _templates;

    [ObservableProperty]
    private bool _hasTemplates;

    /// <summary>Owned here rather than by <see cref="OptimizeSettingsViewModel"/>: "set default" is a
    /// template action, and two view-models writing the same setting would clobber each other.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteTemplateCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedCommand))]
    private Guid? _defaultTemplateId;

    // ---- master-detail state ------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EditSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(DuplicateSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(SetDefaultSelectedCommand))]
    private OptimizationTemplate? _selectedTemplate;

    partial void OnSelectedTemplateChanged(OptimizationTemplate? oldValue, OptimizationTemplate? newValue)
    {
        OnPropertyChanged(nameof(ShowsDetail));
        OnPropertyChanged(nameof(ShowsPlaceholder));

        // A refresh rebuilds the roster, so the same row comes back as a NEW instance — only a genuinely
        // different template may close an editor the user is still typing in.
        if (IsEditorOpen && oldValue?.Id != newValue?.Id)
            CancelEdit();
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddTemplateCommand))]
    private bool _isEditorOpen;

    partial void OnIsEditorOpenChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowsDetail));
        OnPropertyChanged(nameof(ShowsPlaceholder));
    }

    /// <summary>The live edit model behind the inline editor pane; null while the editor is closed.</summary>
    [ObservableProperty]
    private TemplateEditModel? _editor;

    /// <summary>Null while creating, the template's id while editing. Also what decides which service call
    /// the save takes, so it must be cleared when the editor closes.</summary>
    [ObservableProperty]
    private Guid? _editingTemplateId;

    /// <summary>Three states, each a full expression: the panes are siblings in one Grid, so a second true
    /// state would still hit-test over the visible one.</summary>
    public bool ShowsDetail => !IsEditorOpen && SelectedTemplate is not null;

    public bool ShowsPlaceholder => !IsEditorOpen && SelectedTemplate is null;

    public TemplatesSettingsViewModel(
        ILogger<SettingsViewModel> logger,
        ITemplateService templateService,
        ISettingsService settingsService,
        ITextOptimizationService textOptimizationService,
        Wpf.Ui.ISnackbarService snackbarService,
        ILocalizationService localizationService,
        IAuthService authService,
        IPolicyService policyService)
    {
        Policy = new PolicyLock(policyService);
        _logger = logger;
        _templateService = templateService;
        _settingsService = settingsService;
        _textOptimizationService = textOptimizationService;
        _snackbarService = snackbarService;
        _localizationService = localizationService;
        _authService = authService;
        Templates = new ObservableCollection<OptimizationTemplate>();

        _templateService.TemplatesChanged += OnTemplatesChanged;
        _authService.LoginStateChanged += OnLoginStateChanged;
        _settingsService.SettingsChanged += OnSettingsChanged;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _templateService.TemplatesChanged -= OnTemplatesChanged;
        _authService.LoginStateChanged -= OnLoginStateChanged;
        _settingsService.SettingsChanged -= OnSettingsChanged;
        Policy.Dispose();
        GC.SuppressFinalize(this);
    }

    private void OnTemplatesChanged(object? sender, EventArgs e) =>
        RefreshTemplatesAsync().SafeFireAndForget(_logger);

    private void OnLoginStateChanged(object? sender, bool isLoggedIn)
    {
        if (isLoggedIn)
            RefreshTemplatesAsync().SafeFireAndForget(_logger);
    }

    // Raised from the policy pull thread, so the mirror has to be marshalled.
    private void OnSettingsChanged(object? sender, AppSettings settings) => Post(() =>
    {
        _isLoading = true;
        DefaultTemplateId = settings.DefaultTemplateId;
        _isLoading = false;
    });

    public async Task InitializeAsync()
    {
        _isLoading = true;
        DefaultTemplateId = (await _settingsService.GetSettingsAsync()).DefaultTemplateId;
        _isLoading = false;
        await RefreshTemplatesAsync();
    }

    /// <summary>Refused, not just greyed, while the editor is open: the alternative is discarding whatever
    /// the user has typed into it.</summary>
    private bool CanAddTemplate() => !IsEditorOpen;

    [RelayCommand(CanExecute = nameof(CanAddTemplate))]
    private void AddTemplate()
    {
        if (!CanAddTemplate())
            return;

        // Minted here, not left to the service: Save re-selects the saved row by this id, and a caller
        // that assigns its own would leave the user on the placeholder.
        var editModel = new TemplateEditModel(_textOptimizationService) { Id = Guid.NewGuid() };
        SelectedTemplate = null;
        OpenEditor(editModel, null);
    }

    [RelayCommand]
    private void EditTemplate(OptimizationTemplate? template)
    {
        if (template is null || template.IsBuiltIn)
            return;

        var editModel = TemplateEditModel.FromTemplate(template, _textOptimizationService);
        SelectedTemplate = Templates.FirstOrDefault(t => t.Id == template.Id) ?? template;
        OpenEditor(editModel, template.Id);
    }

    /// <summary>Creates a new template seeded from an existing one (including a read-only built-in — this
    /// is the escape hatch for those) and opens the editor.</summary>
    [RelayCommand]
    private void DuplicateTemplate(OptimizationTemplate? template)
    {
        if (template is null)
            return;

        var editModel = TemplateEditModel.FromTemplate(template, _textOptimizationService);
        editModel.Id = Guid.NewGuid();
        editModel.Name = $"{template.Name} (copy)";

        // The copy is not in the roster yet, so the selection stays on the original: cancelling lands back
        // on the template the user was reading rather than on the placeholder.
        SelectedTemplate = Templates.FirstOrDefault(t => t.Id == template.Id);
        OpenEditor(editModel, null);
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

        CancelEdit();
        await _templateService.DeleteTemplateAsync(template.Id);
        SelectedTemplate = null;
        await RefreshTemplatesAsync();
        _snackbarService.Show(_localizationService["Msg_Success"], _localizationService["Msg_Settings_TemplateDeleted"], Wpf.Ui.Controls.ControlAppearance.Success, null, TimeSpan.FromSeconds(3));
    }

    [RelayCommand]
    private async Task SetDefaultTemplateAsync(OptimizationTemplate? template)
    {
        if (template is null)
            return;

        DefaultTemplateId = template.Id;
        await SaveDefaultAsync();
    }

    // The detail pane acts on the selection, so its buttons need parameterless commands. They delegate to
    // the parameterised ones, which keep every guard.
    [RelayCommand(CanExecute = nameof(CanEditSelected))]
    private void EditSelected() => EditTemplate(SelectedTemplate);

    [RelayCommand(CanExecute = nameof(CanDuplicateSelected))]
    private void DuplicateSelected() => DuplicateTemplate(SelectedTemplate);

    [RelayCommand(CanExecute = nameof(CanDeleteSelected))]
    private Task DeleteSelectedAsync() => DeleteTemplateAsync(SelectedTemplate);

    [RelayCommand(CanExecute = nameof(CanSetDefaultSelected))]
    private Task SetDefaultSelectedAsync() => SetDefaultTemplateAsync(SelectedTemplate);

    private bool CanEditSelected() => SelectedTemplate is { IsBuiltIn: false };

    private bool CanDuplicateSelected() => SelectedTemplate is not null;

    private bool CanDeleteSelected() => CanDeleteTemplate(SelectedTemplate);

    private bool CanSetDefaultSelected() => SelectedTemplate is not null;

    [RelayCommand]
    private void CancelEdit()
    {
        IsEditorOpen = false;
        Editor = null;
        EditingTemplateId = null;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        // The Save button is bound to Editor.CanSave, which is not a CanExecute — a command reached by any
        // other route still has to find the required fields filled in.
        if (Editor is not { CanSave: true } editModel)
            return;

        var template = editModel.ToTemplate();
        var isUpdate = EditingTemplateId is not null;
        if (EditingTemplateId is { } existingId)
        {
            template.Id = existingId;
            await _templateService.UpdateTemplateAsync(template);
        }
        else
        {
            await _templateService.AddTemplateAsync(template);
        }

        CancelEdit();
        await RefreshTemplatesAsync(template.Id);
        _snackbarService.Show(
            _localizationService["Msg_Success"],
            _localizationService[isUpdate ? "Msg_Settings_TemplateUpdated" : "Msg_Settings_TemplateAdded"],
            Wpf.Ui.Controls.ControlAppearance.Success, null, TimeSpan.FromSeconds(3));
    }

    private void OpenEditor(TemplateEditModel editModel, Guid? editingId)
    {
        Editor = editModel;
        EditingTemplateId = editingId;
        IsEditorOpen = true;
    }

    private bool CanDeleteTemplate(OptimizationTemplate? template) =>
        template is { IsBuiltIn: false } && template.Id != DefaultTemplateId;

    private async Task SaveDefaultAsync()
    {
        if (_isLoading)
            return;

        var settings = await _settingsService.GetSettingsAsync();
        settings.DefaultTemplateId = DefaultTemplateId;
        await _settingsService.SaveSettingsAsync(settings);
    }

    private async Task RefreshTemplatesAsync(Guid? selectId = null)
    {
        // Fetch first (off any thread), then marshal the bound-collection mutation to the captured
        // UI context — RefreshTemplatesAsync is reachable from OnTemplatesChanged, which the sync
        // pull loop can raise on a background thread. Clearing before the await would throw there.
        var templatesList = await _templateService.GetTemplatesAsync();
        await PostAsync(() =>
        {
            // Rows are rebuilt wholesale, so the selection has to be re-resolved by id or every refresh
            // would silently empty the detail pane the user is reading.
            var keepId = selectId ?? SelectedTemplate?.Id;

            Templates.Clear();
            foreach (var template in templatesList)
                Templates.Add(template);
            HasTemplates = Templates.Count > 0;

            SelectedTemplate = keepId is { } id ? Templates.FirstOrDefault(t => t.Id == id) : null;
        });
    }
}
