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
/// Manages personas in settings (list / add / edit / delete / duplicate-a-built-in). Built-ins are
/// shown read-only. Mirrors <see cref="OptimizeSettingsViewModel"/>.
/// </summary>
public partial class PersonaSettingsViewModel : UiThreadViewModel
{
    private readonly ILogger<SettingsViewModel> _logger;
    private readonly IPersonaService _personaService;
    private readonly IProviderService _providerService;
    private readonly ITextOptimizationService _textOptimizationService;
    private readonly IDialogService _dialogService;
    private readonly Wpf.Ui.ISnackbarService _snackbarService;
    private readonly ILocalizationService _localizationService;
    private readonly IAuthService _authService;

    [ObservableProperty]
    private ObservableCollection<Persona> _personas;

    public PersonaSettingsViewModel(
        ILogger<SettingsViewModel> logger,
        IPersonaService personaService,
        IProviderService providerService,
        ITextOptimizationService textOptimizationService,
        IDialogService dialogService,
        Wpf.Ui.ISnackbarService snackbarService,
        ILocalizationService localizationService,
        IAuthService authService)
    {
        _logger = logger;
        _personaService = personaService;
        _providerService = providerService;
        _textOptimizationService = textOptimizationService;
        _dialogService = dialogService;
        _snackbarService = snackbarService;
        _localizationService = localizationService;
        _authService = authService;
        Personas = new ObservableCollection<Persona>();

        _personaService.PersonasChanged += OnPersonasChanged;
        _authService.LoginStateChanged += OnLoginStateChanged;
    }

    private void OnPersonasChanged(object? sender, EventArgs e) =>
        RefreshPersonasAsync().SafeFireAndForget(_logger);

    private void OnLoginStateChanged(object? sender, bool isLoggedIn)
    {
        if (isLoggedIn)
            RefreshPersonasAsync().SafeFireAndForget(_logger);
    }

    public async Task InitializeAsync() => await RefreshPersonasAsync();

    [RelayCommand]
    private async Task AddPersonaAsync()
    {
        var editModel = new PersonaEditModel(_textOptimizationService);
        await PopulateProvidersAsync(editModel, null);

        if (await _dialogService.ShowPersonaEditDialogAsync(editModel))
        {
            await _personaService.AddPersonaAsync(editModel.ToPersona());
            await RefreshPersonasAsync();
            _snackbarService.Show(_localizationService["Msg_Success"], _localizationService["Msg_Settings_PersonaAdded"], Wpf.Ui.Controls.ControlAppearance.Success, null, TimeSpan.FromSeconds(3));
        }
    }

    [RelayCommand]
    private async Task EditPersonaAsync(Persona? persona)
    {
        if (persona is null || persona.IsBuiltIn)
            return;

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

        if (persona.IsBuiltIn)
        {
            _snackbarService.Show(_localizationService["Msg_Warning"], _localizationService["Msg_Settings_CannotDeleteBuiltInPersona"], Wpf.Ui.Controls.ControlAppearance.Caution, null, TimeSpan.FromSeconds(3));
            return;
        }

        await _personaService.DeletePersonaAsync(persona.Id);
        await RefreshPersonasAsync();
        _snackbarService.Show(_localizationService["Msg_Success"], _localizationService["Msg_Settings_PersonaDeleted"], Wpf.Ui.Controls.ControlAppearance.Success, null, TimeSpan.FromSeconds(3));
    }

    /// <summary>Creates a new user persona seeded from an existing one (incl. a built-in) and opens the editor.</summary>
    [RelayCommand]
    private async Task DuplicatePersonaAsync(Persona? persona)
    {
        if (persona is null)
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

    private static bool CanDeletePersona(Persona? persona) => persona is { IsBuiltIn: false };

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
