using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services.Interfaces;
using Pia.ViewModels;
using Pia.ViewModels.Models;
using Xunit;

namespace Pia.Tests.ViewModels;

/// <summary>The commands are exercised directly, not through the buttons: <c>PersonasView.xaml</c> only keys their
/// <c>Visibility</c> off <c>IsReadOnly</c>, so a command reached by any other route must still refuse.</summary>
public class PersonaSettingsManagedPersonaTests
{
    private sealed record Harness(
        PersonaSettingsViewModel Sut,
        IPersonaService Personas,
        IDialogService Dialogs,
        global::Wpf.Ui.ISnackbarService Snackbar);

    private static Persona Managed(string name = "Brandvoice") => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        SystemPrompt = "prompt",
        IsManaged = true,
    };

    private static Persona BuiltIn(string name = "Assistant") => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        SystemPrompt = "prompt",
        IsBuiltIn = true,
    };

    private static Persona UserOwned(string name = "Mine") => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        SystemPrompt = "prompt",
    };

    private static Harness Create(params Persona[] roster)
    {
        var personas = Substitute.For<IPersonaService>();
        personas.GetPersonasAsync().Returns(_ => Task.FromResult<IReadOnlyList<Persona>>(roster));

        var providers = Substitute.For<IProviderService>();
        providers.GetProvidersAsync().Returns(_ => Task.FromResult<IReadOnlyList<AiProvider>>([]));

        var dialogs = Substitute.For<IDialogService>();
        // Default to "the user pressed Save", so a test that reaches the dialog reaches the service too —
        // any DidNotReceive below therefore means the guard fired, not that the dialog was cancelled.
        dialogs.ShowPersonaEditDialogAsync(Arg.Any<PersonaEditModel>()).Returns(true);

        var snackbar = Substitute.For<global::Wpf.Ui.ISnackbarService>();

        // Each key resolves to itself, so ShownMessages() can assert on the resource key rather than on
        // English copy that translators are free to reword.
        var localization = Substitute.For<ILocalizationService>();
        localization[Arg.Any<string>()].Returns(call => call.Arg<string>());

        var settingsService = Substitute.For<ISettingsService>();
        settingsService.GetSettingsAsync().Returns(_ => Task.FromResult(new AppSettings()));

        var sut = new PersonaSettingsViewModel(
            NullLogger<SettingsViewModel>.Instance, personas, providers,
            Substitute.For<ITextOptimizationService>(), dialogs, snackbar, localization,
            Substitute.For<IAuthService>(), settingsService, Substitute.For<IPolicyService>());

        return new Harness(sut, personas, dialogs, snackbar);
    }

    /// <summary>Read positionally off <c>ReceivedCalls()</c>, so it survives WPF-UI reshuffling <c>Show</c>'s optional parameters.</summary>
    private static List<string> ShownMessages(global::Wpf.Ui.ISnackbarService snackbar) =>
        snackbar.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == "Show")
            .Select(call => call.GetArguments())
            .Where(args => args.Length > 1)
            .Select(args => args[1])
            .OfType<string>()
            .ToList();

    [Fact]
    public async Task EditPersona_on_a_managed_persona_opens_no_editor_and_updates_nothing()
    {
        var managed = Managed();
        Assert.True(managed.IsReadOnly, "non-vacuity: the fixture must actually be read-only");
        var h = Create(managed);

        await h.Sut.EditPersonaCommand.ExecuteAsync(managed);

        await h.Personas.DidNotReceive().UpdatePersonaAsync(Arg.Any<Persona>());
        await h.Dialogs.DidNotReceive().ShowPersonaEditDialogAsync(Arg.Any<PersonaEditModel>());
        Assert.Contains("Msg_Settings_CannotEditManagedPersona", ShownMessages(h.Snackbar));
    }

    [Fact]
    public async Task EditPersona_on_a_built_in_stays_silent()
    {
        // The regression this guards: routing built-ins through the same IsReadOnly gate must not start
        // telling users their built-in persona is "published by your administrator".
        var builtIn = BuiltIn();
        var h = Create(builtIn);

        await h.Sut.EditPersonaCommand.ExecuteAsync(builtIn);

        await h.Personas.DidNotReceive().UpdatePersonaAsync(Arg.Any<Persona>());
        await h.Dialogs.DidNotReceive().ShowPersonaEditDialogAsync(Arg.Any<PersonaEditModel>());
        Assert.Empty(ShownMessages(h.Snackbar));
    }

    [Fact]
    public async Task DeletePersona_on_a_managed_persona_deletes_nothing()
    {
        var managed = Managed();
        var h = Create(managed);

        await h.Sut.DeletePersonaCommand.ExecuteAsync(managed);

        await h.Personas.DidNotReceive().DeletePersonaAsync(Arg.Any<Guid>());
        Assert.Contains("Msg_Settings_CannotDeleteManagedPersona", ShownMessages(h.Snackbar));
    }

    [Fact]
    public void CanDeletePersona_is_false_for_managed_and_built_in_but_true_for_a_user_persona()
    {
        var h = Create();

        Assert.False(h.Sut.DeletePersonaCommand.CanExecute(Managed()));
        Assert.False(h.Sut.DeletePersonaCommand.CanExecute(BuiltIn()));
        Assert.True(h.Sut.DeletePersonaCommand.CanExecute(UserOwned()));
    }

    [Fact]
    public async Task DuplicatePersona_on_a_managed_persona_adds_one_ordinary_user_persona()
    {
        var managed = Managed();
        var h = Create(managed);

        await h.Sut.DuplicatePersonaCommand.ExecuteAsync(managed);

        // Read the argument off the call log rather than an Arg.Do capture, so nothing depends on when
        // NSubstitute runs a capture callback. Single() is asserted before Received(1) for the same reason.
        var added = (Persona)h.Personas.ReceivedCalls()
            .Single(call => call.GetMethodInfo().Name == nameof(IPersonaService.AddPersonaAsync))
            .GetArguments()[0]!;
        await h.Personas.Received(1).AddPersonaAsync(Arg.Any<Persona>());

        // A new id, because the copy must not shadow the managed row it was seeded from, and IsManaged
        // false so it syncs as the user's own instead of being wiped by the next replace-all pull.
        Assert.NotEqual(managed.Id, added.Id);
        Assert.False(added.IsManaged);
        Assert.False(added.IsBuiltIn);
        Assert.False(added.IsReadOnly);
    }
}
