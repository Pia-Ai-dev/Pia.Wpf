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
/// Manages personas in settings (list / add / edit / delete / duplicate-a-built-in) as a master-detail
/// pane with an inline editor, mirroring <see cref="RoutinesViewModel"/>. Built-ins and admin-published
/// managed personas are shown read-only; Duplicate is the escape hatch for both.
/// </summary>
public partial class PersonaSettingsViewModel : UiThreadViewModel, IDisposable
{
    private readonly ILogger<SettingsViewModel> _logger;
    private readonly IPersonaService _personaService;
    private readonly IProviderService _providerService;
    private readonly ITextOptimizationService _textOptimizationService;
    private readonly Wpf.Ui.ISnackbarService _snackbarService;
    private readonly ILocalizationService _localizationService;
    private readonly IAuthService _authService;
    private readonly ISettingsService _settingsService;
    // Concrete and readonly, refilled in place: the architecture rule bans a mutable interface-typed field.
    private readonly List<AiProvider> _providers = [];
    private bool _disposed;

    [ObservableProperty]
    private ObservableCollection<Persona> _personas;

    /// <summary>Bind IsEnabled to Policy[nameof(AppSettings.X)] to grey a control out while policy enforces it.</summary>
    public PolicyLock Policy { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddPersonaCommand))]
    [NotifyCanExecuteChangedFor(nameof(EditPersonaCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeletePersonaCommand))]
    [NotifyCanExecuteChangedFor(nameof(DuplicatePersonaCommand))]
    [NotifyCanExecuteChangedFor(nameof(EditSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(DuplicateSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private bool _canManagePersonas = true;

    [ObservableProperty]
    private bool _hasPersonas;

    // ---- master-detail state ------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EditSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(DuplicateSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedCommand))]
    private Persona? _selectedPersona;

    partial void OnSelectedPersonaChanged(Persona? oldValue, Persona? newValue)
    {
        OnPropertyChanged(nameof(ShowsDetail));
        OnPropertyChanged(nameof(ShowsPlaceholder));
        RaiseSelectionProjections();

        // A refresh rebuilds the roster, so the same row comes back as a NEW instance — only a genuinely
        // different persona may close an editor the user is still typing in.
        if (IsEditorOpen && oldValue?.Id != newValue?.Id)
            CancelEdit();
    }

    [ObservableProperty]
    private bool _isEditorOpen;

    partial void OnIsEditorOpenChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowsDetail));
        OnPropertyChanged(nameof(ShowsPlaceholder));
    }

    /// <summary>The live edit model behind the inline editor pane; null while the editor is closed.</summary>
    [ObservableProperty]
    private PersonaEditModel? _editor;

    /// <summary>Null while creating, the persona's id while editing. Also what decides which service call
    /// the save takes, so it must be cleared when the editor closes.</summary>
    [ObservableProperty]
    private Guid? _editingPersonaId;

    /// <summary>Three states, each a full expression: the panes are siblings in one Grid, so a second true
    /// state would still hit-test over the visible one.</summary>
    public bool ShowsDetail => !IsEditorOpen && SelectedPersona is not null;

    public bool ShowsPlaceholder => !IsEditorOpen && SelectedPersona is null;

    public string? SelectedExpertise =>
        SelectedPersona is { Expertise.Count: > 0 } persona ? string.Join(", ", persona.Expertise) : null;

    /// <summary>Null when the persona pins no provider, so the row collapses instead of showing a raw id.</summary>
    public string? SelectedProviderName =>
        SelectedPersona?.PreferredProviderId is { } id ? _providers.FirstOrDefault(p => p.Id == id)?.Name : null;

    public string? SelectedEffortLabel =>
        SelectedPersona?.ReasoningEffort is { } effort
            ? PersonaEditModel.EffortChoices.FirstOrDefault(c => c.Value == effort)?.Display
            : null;

    public PersonaSettingsViewModel(
        ILogger<SettingsViewModel> logger,
        IPersonaService personaService,
        IProviderService providerService,
        ITextOptimizationService textOptimizationService,
        Wpf.Ui.ISnackbarService snackbarService,
        ILocalizationService localizationService,
        IAuthService authService,
        ISettingsService settingsService,
        IPolicyService policyService)
    {
        Policy = new PolicyLock(policyService);
        _logger = logger;
        _personaService = personaService;
        _providerService = providerService;
        _textOptimizationService = textOptimizationService;
        _snackbarService = snackbarService;
        _localizationService = localizationService;
        _authService = authService;
        _settingsService = settingsService;
        Personas = new ObservableCollection<Persona>();

        _personaService.PersonasChanged += OnPersonasChanged;
        _authService.LoginStateChanged += OnLoginStateChanged;
        _settingsService.SettingsChanged += OnSettingsChanged;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _settingsService.SettingsChanged -= OnSettingsChanged;
        Policy.Dispose();
        GC.SuppressFinalize(this);
    }

    // Raised from the policy pull thread, so the mirror has to be marshalled. No _isLoading guard is
    // needed: CanManagePersonas has no change handler and is never written back.
    private void OnSettingsChanged(object? sender, AppSettings settings) =>
        Post(() => CanManagePersonas = settings.AllowPersonaManagement);

    private void OnPersonasChanged(object? sender, EventArgs e) =>
        RefreshPersonasAsync().SafeFireAndForget(_logger);

    private void OnLoginStateChanged(object? sender, bool isLoggedIn)
    {
        if (isLoggedIn)
            RefreshPersonasAsync().SafeFireAndForget(_logger);
    }

    public async Task InitializeAsync()
    {
        CanManagePersonas = (await _settingsService.GetSettingsAsync()).AllowPersonaManagement;
        await RefreshPersonasAsync();
    }

    [RelayCommand(CanExecute = nameof(CanManagePersonas))]
    private async Task AddPersonaAsync()
    {
        // CanExecute only greys the button out; ExecuteAsync does not consult it, so every command the
        // lock covers re-checks here.
        if (!CanManagePersonas)
            return;

        var editModel = new PersonaEditModel(_textOptimizationService);
        await PopulateProvidersAsync(editModel, null);

        SelectedPersona = null;
        OpenEditor(editModel, null);
    }

    [RelayCommand(CanExecute = nameof(CanManagePersonas))]
    private async Task EditPersonaAsync(Persona? persona)
    {
        if (persona is null)
            return;

        if (!CanManagePersonas)
            return;

        // One gate for both read-only flavours, so a third one would be caught here too. Only the managed
        // case explains itself: the row looks otherwise ordinary and the flag is new, so users will ask.
        // A built-in still returns silently, exactly as before — those have always visibly been fixed.
        if (persona.IsReadOnly)
        {
            if (persona.IsManaged)
                _snackbarService.Show(_localizationService["Msg_Warning"], _localizationService["Msg_Settings_CannotEditManagedPersona"], Wpf.Ui.Controls.ControlAppearance.Caution, null, TimeSpan.FromSeconds(3));
            return;
        }

        var editModel = PersonaEditModel.FromPersona(persona, _textOptimizationService);
        await PopulateProvidersAsync(editModel, persona.PreferredProviderId);

        SelectedPersona = Personas.FirstOrDefault(p => p.Id == persona.Id) ?? persona;
        OpenEditor(editModel, persona.Id);
    }

    [RelayCommand(CanExecute = nameof(CanDeletePersona))]
    private async Task DeletePersonaAsync(Persona? persona)
    {
        if (persona is null)
            return;

        if (!CanManagePersonas)
            return;

        if (persona.IsBuiltIn)
        {
            _snackbarService.Show(_localizationService["Msg_Warning"], _localizationService["Msg_Settings_CannotDeleteBuiltInPersona"], Wpf.Ui.Controls.ControlAppearance.Caution, null, TimeSpan.FromSeconds(3));
            return;
        }

        // Deleting a managed persona locally would only invite the next sync pull to put it straight back —
        // the admin owns the catalog. Refuse before reaching the service (which refuses again, in C2).
        if (persona.IsManaged)
        {
            _snackbarService.Show(_localizationService["Msg_Warning"], _localizationService["Msg_Settings_CannotDeleteManagedPersona"], Wpf.Ui.Controls.ControlAppearance.Caution, null, TimeSpan.FromSeconds(3));
            return;
        }

        CancelEdit();
        await _personaService.DeletePersonaAsync(persona.Id);
        SelectedPersona = null;
        await RefreshPersonasAsync();
        _snackbarService.Show(_localizationService["Msg_Success"], _localizationService["Msg_Settings_PersonaDeleted"], Wpf.Ui.Controls.ControlAppearance.Success, null, TimeSpan.FromSeconds(3));
    }

    /// <summary>Creates a new user persona seeded from an existing one (incl. a read-only built-in or
    /// managed one — this is the escape hatch for those) and opens the editor.</summary>
    [RelayCommand(CanExecute = nameof(CanManagePersonas))]
    private async Task DuplicatePersonaAsync(Persona? persona)
    {
        if (persona is null)
            return;

        if (!CanManagePersonas)
            return;

        var editModel = PersonaEditModel.FromPersona(persona, _textOptimizationService);
        editModel.Id = Guid.NewGuid();
        editModel.Name = $"{persona.Name} (copy)";
        await PopulateProvidersAsync(editModel, persona.PreferredProviderId);

        // The copy is not in the roster yet, so the selection stays on the original: cancelling lands back
        // on the persona the user was reading rather than on the placeholder.
        SelectedPersona = Personas.FirstOrDefault(p => p.Id == persona.Id);
        OpenEditor(editModel, null);
    }

    // The detail pane acts on the selection, so its buttons need parameterless commands. They delegate to
    // the parameterised ones, which keep every guard.
    [RelayCommand(CanExecute = nameof(CanEditSelected))]
    private Task EditSelectedAsync() => EditPersonaAsync(SelectedPersona);

    [RelayCommand(CanExecute = nameof(CanDuplicateSelected))]
    private Task DuplicateSelectedAsync() => DuplicatePersonaAsync(SelectedPersona);

    [RelayCommand(CanExecute = nameof(CanDeleteSelected))]
    private Task DeleteSelectedAsync() => DeletePersonaAsync(SelectedPersona);

    private bool CanEditSelected() => CanManagePersonas && SelectedPersona is { IsReadOnly: false };

    private bool CanDuplicateSelected() => CanManagePersonas && SelectedPersona is not null;

    private bool CanDeleteSelected() => CanDeletePersona(SelectedPersona);

    [RelayCommand]
    private void CancelEdit()
    {
        IsEditorOpen = false;
        Editor = null;
        EditingPersonaId = null;
    }

    [RelayCommand(CanExecute = nameof(CanManagePersonas))]
    private async Task SaveAsync()
    {
        if (!CanManagePersonas)
            return;

        // The Save button is bound to Editor.CanSave, which is not a CanExecute — a command reached by any
        // other route still has to find the required fields filled in.
        if (Editor is not { CanSave: true } editModel)
            return;

        var persona = editModel.ToPersona();
        var isUpdate = EditingPersonaId is not null;
        if (EditingPersonaId is { } existingId)
        {
            persona.Id = existingId;
            await _personaService.UpdatePersonaAsync(persona);
        }
        else
        {
            await _personaService.AddPersonaAsync(persona);
        }

        CancelEdit();
        await RefreshPersonasAsync(persona.Id);
        _snackbarService.Show(
            _localizationService["Msg_Success"],
            _localizationService[isUpdate ? "Msg_Settings_PersonaUpdated" : "Msg_Settings_PersonaAdded"],
            Wpf.Ui.Controls.ControlAppearance.Success, null, TimeSpan.FromSeconds(3));
    }

    private void OpenEditor(PersonaEditModel editModel, Guid? editingId)
    {
        Editor = editModel;
        EditingPersonaId = editingId;
        IsEditorOpen = true;
    }

    // IsReadOnly covers built-in OR managed, so the command is disabled for both flavours of
    // admin/app-owned persona and the XAML can key off the same one flag.
    private bool CanDeletePersona(Persona? persona) => CanManagePersonas && persona is { IsReadOnly: false };

    private void SetProviderCache(IReadOnlyList<AiProvider> providers)
    {
        _providers.Clear();
        _providers.AddRange(providers);
    }

    private void RaiseSelectionProjections()
    {
        OnPropertyChanged(nameof(SelectedExpertise));
        OnPropertyChanged(nameof(SelectedProviderName));
        OnPropertyChanged(nameof(SelectedEffortLabel));
    }

    private async Task PopulateProvidersAsync(PersonaEditModel editModel, Guid? selectedId)
    {
        SetProviderCache(await _providerService.GetProvidersAsync());
        editModel.SetProviders(_providers, selectedId);
    }

    private async Task RefreshPersonasAsync(Guid? selectId = null)
    {
        // Fetch first (off any thread), then marshal the bound-collection mutation to the captured
        // UI context — RefreshPersonasAsync is reachable from OnPersonasChanged, which the sync pull
        // loop can raise on a background thread. Clearing before the await (as before) would throw.
        var personas = await _personaService.GetPersonasAsync();
        var providers = await _providerService.GetProvidersAsync();
        await PostAsync(() =>
        {
            SetProviderCache(providers);
            // Rows are rebuilt wholesale, so the selection has to be re-resolved by id or every refresh
            // would silently empty the detail pane the user is reading.
            var keepId = selectId ?? SelectedPersona?.Id;

            Personas.Clear();
            foreach (var persona in personas)
                Personas.Add(persona);
            HasPersonas = Personas.Count > 0;

            SelectedPersona = keepId is { } id ? Personas.FirstOrDefault(p => p.Id == id) : null;
            RaiseSelectionProjections();
        });
    }
}
