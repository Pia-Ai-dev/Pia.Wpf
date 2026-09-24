using Microsoft.Extensions.Logging;
using Pia.Models;
using Pia.Services.Interfaces;
using Pia.ViewModels;
using Pia.Views.Controls;
using Pia.Views.Dialogs.Overlay;

namespace Pia.Views.Overlays;

public class AdvancedCreationLauncher : IAdvancedCreationLauncher
{
    private readonly IAdvancedCreationService _service;
    private readonly IDialogOverlayService _overlayService;
    private readonly ILocalizationService _localization;
    private readonly IPersonaService _personas;
    private readonly ISettingsService _settings;
    private readonly ILogger<AdvancedCreationViewModel> _logger;

    public AdvancedCreationLauncher(
        IAdvancedCreationService service,
        IDialogOverlayService overlayService,
        ILocalizationService localization,
        IPersonaService personas,
        ISettingsService settings,
        ILogger<AdvancedCreationViewModel> logger)
    {
        _service = service;
        _overlayService = overlayService;
        _localization = localization;
        _personas = personas;
        _settings = settings;
        _logger = logger;
    }

    public async Task<string?> LaunchAsync(AdvancedCreationMode mode, Guid? providerId = null, string? seed = null)
    {
        var viewModel = new AdvancedCreationViewModel(
            _service, _localization, _logger, _personas, _settings, mode, providerId, seed);
        var panel = new AdvancedCreationOverlayPanel(viewModel);

        var result = await _overlayService.GetOverlayHost().ShowAsync<OverlayDialogResult>(panel);

        return result == OverlayDialogResult.Primary ? viewModel.DraftJson : null;
    }
}
