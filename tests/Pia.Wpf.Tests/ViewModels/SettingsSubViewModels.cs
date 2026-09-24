using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Services.Interfaces;
using Pia.ViewModels;

namespace Pia.Tests.ViewModels;

/// <summary>Stand-ins for the sub-view-models a settings VM needs but a given test never exercises.</summary>
internal static class SettingsSubViewModels
{
    public static McpServersSettingsViewModel McpVm() => new(
        Substitute.For<IPluginService>(),
        Substitute.For<IDialogService>(),
        Substitute.For<ILocalizationService>(),
        Substitute.For<global::Wpf.Ui.ISnackbarService>(),
        NullLogger<SettingsViewModel>.Instance);
}
