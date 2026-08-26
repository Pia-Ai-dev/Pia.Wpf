using System.Windows;
using Microsoft.Extensions.Logging;
using Pia.Services.Interfaces;
using Pia.Services.Operators;
using Pia.ViewModels;
using Wpf.Ui.Controls;

namespace Pia.Views.Dialogs;

/// <summary>Puts a proposed assignment in front of the user. It finds the dialog host off the active window
/// rather than taking <c>IDialogService</c>, because the tool handler that reaches it is a singleton and the
/// dialog services are scoped per window.</summary>
public sealed class AssignmentConsentPrompt : IAssignmentConsentPrompt
{
    private readonly Func<AssignmentConsentViewModel> _factory;
    private readonly IAssignmentSurfaceCache _surface;
    private readonly ILogger<AssignmentConsentPrompt> _logger;

    public AssignmentConsentPrompt(
        Func<AssignmentConsentViewModel> factory,
        IAssignmentSurfaceCache surface,
        ILogger<AssignmentConsentPrompt> logger)
    {
        _factory = factory;
        _surface = surface;
        _logger = logger;
    }

    public Task<AssignmentStartStatus?> PromptAsync(
        string? skillName, string prompt, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<AssignmentStartStatus?>();

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            // Never shown is not the same as declined, so it reports the affirmation that never happened.
            tcs.TrySetResult(AssignmentStartStatus.ConsentMissing);
            return tcs.Task;
        }

        dispatcher.InvokeAsync(async () =>
        {
            try
            {
                var host = FindDialogHost();
                if (host is null)
                {
                    _logger.LogWarning("No ContentDialogHost available for an assignment confirmation.");
                    tcs.TrySetResult(AssignmentStartStatus.ConsentMissing);
                    return;
                }

                var consent = _factory();
                await consent.InitializeAsync(_surface.Surface, prompt, skillName, ct);

                var dialog = new AssignmentConsentContentDialog(host, consent);
                if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                {
                    tcs.TrySetResult(null);
                    return;
                }

                tcs.TrySetResult(await consent.SendAsync(ct));
            }
            catch (Exception ex)
            {
                // A second ShowAsync on the same host throws, and then the user was never asked.
                _logger.LogWarning(ex, "A proposed assignment could not be put to the user.");
                tcs.TrySetResult(AssignmentStartStatus.ConsentMissing);
            }
        });

        return tcs.Task;
    }

    /// <summary>Prefers the window in front: a hidden window's host never completes <c>ShowAsync</c>, which
    /// would hang the tool round the user is waiting on.</summary>
    private static ContentDialogHost? FindDialogHost()
    {
        if (Application.Current is null) return null;

        var windows = Application.Current.Windows.OfType<Window>().Where(w => w.IsVisible).ToList();

        return Host(windows.FirstOrDefault(w => w.IsActive))
            ?? Host(windows.FirstOrDefault(w => ReferenceEquals(w, Application.Current.MainWindow)))
            ?? windows.Select(Host).FirstOrDefault(h => h is not null);
    }

    private static ContentDialogHost? Host(Window? window) =>
        window?.FindName("RootContentDialogPresenter") as ContentDialogHost;
}
