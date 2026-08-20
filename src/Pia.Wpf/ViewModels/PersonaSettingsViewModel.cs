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
/// Manages personas in settings (list / add / edit / delete / duplicate-a-built-in). Built-ins and
/// admin-published managed personas are shown read-only; Duplicate is the escape hatch for both.
/// Mirrors <see cref="OptimizeSettingsViewModel"/>.
/// </summary>
public partial class PersonaSettingsViewModel : UiThreadViewModel, IDisposable
{
    private readonly ILogger<SettingsViewModel> _logger;
    private readonly IPersonaService _personaService;
    private readonly IProviderService _providerService;
    private readonly ITextOptimizationService _textOptimizationService;
    private readonly IDialogService _dialogService;
    private readonly Wpf.Ui.ISnackbarService _snackbarService;
    private readonly ILocalizationService _localizationService;
    private readonly IAuthService _authService;
    private readonly ISettingsService _settingsService;
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
    private bool _canManagePersonas = true;

    public PersonaSettingsViewModel(
        ILogger<SettingsViewModel> logger,
        IPersonaService personaService,
        IProviderService providerService,
        ITextOptimizationService textOptimizationService,
        IDialogService dialogService,
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
        _dialogService = dialogService;
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

        if (await _dialogService.ShowPersonaEditDialogAsync(editModel))
        {
            await _personaService.AddPersonaAsync(editModel.ToPersona());
            await RefreshPersonasAsync();
            _snackbarService.Show(_localizationService["Msg_Success"], _localizationService["Msg_Settings_PersonaAdded"], Wpf.Ui.Controls.ControlAppearance.Success, null, TimeSpan.FromSeconds(3));
        }
    }

    [RelayCommand(CanExecute = nameof(CanManagePersonas))]
    private async Task EditPersonaAsync(Persona? persona)
    {
        if (persona is null)
            return;

        if (!CanManagePersonas)
            return;

        // One gate for both read-only flavours, so a third one would be caught here too. Only the managed
        // case explains itself: the card looks otherwise ordinary and the flag is new, so users will ask.
        // A built-in still returns silently, exactly as before — those have always visibly been fixed.
        if (persona.IsReadOnly)
        {
            if (persona.IsManaged)
                _snackbarService.Show(_localizationService["Msg_Warning"], _localizationService["Msg_Settings_CannotEditManagedPersona"], Wpf.Ui.Controls.ControlAppearance.Caution, null, TimeSpan.FromSeconds(3));
            return;
        }

        var editModel = PersonaEditModel.FromPersona(persona, _textOptimizationService);
        await PopulateProvidersAsync(editModel, persona.PreferredProviderId);

        if (await _dialogService.ShowPersonaEditDialogAsync(editModel))
        {
            await _personaService.UpdatePersonaAsync(editModel.ToPersona());
            await RefreshPersonasAsync();
            _snackbarService.Show(_localizationService["Msg_Success"], _localizationService["Msg_Settings_PersonaUpdated"], Wpf.Ui.Controls.ControlAppearance.Success, null, TimeSpan.FromSeconds(3));
        }
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

        await _personaService.DeletePersonaAsync(persona.Id);
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

        if (await _dialogService.ShowPersonaEditDialogAsync(editModel))
        {
            await _personaService.AddPersonaAsync(editModel.ToPersona());
            await RefreshPersonasAsync();
            _snackbarService.Show(_localizationService["Msg_Success"], _localizationService["Msg_Settings_PersonaAdded"], Wpf.Ui.Controls.ControlAppearance.Success, null, TimeSpan.FromSeconds(3));
        }
    }

    // IsReadOnly covers built-in OR managed, so the command is disabled for both flavours of
    // admin/app-owned persona and the XAML can key off the same one flag.
    private bool CanDeletePersona(Persona? persona) => CanManagePersonas && persona is { IsReadOnly: false };

    private async Task PopulateProvidersAsync(PersonaEditModel editModel, Guid? selectedId)
    {
        var providers = await _providerService.GetProvidersAsync();
        editModel.SetProviders(providers, selectedId);
    }

    private async Task RefreshPersonasAsync()
    {
        // Fetch first (off any thread), then marshal the bound-collection mutation to the captured
        // UI context — RefreshPersonasAsync is reachable from OnPersonasChanged, which the sync pull
        // loop can raise on a background thread. Clearing before the await (as before) would throw.
        var personas = await _personaService.GetPersonasAsync();
        await PostAsync(() =>
        {
            Personas.Clear();
            foreach (var persona in personas)
                Personas.Add(persona);
        });
    }
}
