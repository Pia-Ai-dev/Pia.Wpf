using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Navigation;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Services.MeetingAttendee;
using Pia.Tests.Services;
using Pia.ViewModels;
using Pia.ViewModels.Models;
using Xunit;

namespace Pia.Tests.ViewModels;

/// <summary>
/// Slice C7 / §5.1: when the org withdraws a managed persona the user had selected, the assistant tells them
/// once, in a snackbar, and keeps working. The one-shot property is the interesting half — the latch is the
/// cleared per-mode selection in <c>PersonaService.ReplaceManagedPersonasAsync</c>, not a persisted flag, so
/// nothing here may re-announce a withdrawal on a later, unrelated persona reload.
/// <para>
/// The two events are raised in the order the service raises them (<c>ManagedPersonaWithdrawn</c>, then
/// <c>PersonasChanged</c>) because that ordering is the contract the handler depends on: the notice names the
/// fallback persona, which only exists once the reload has resolved it. An <c>InlineUiDispatcher</c> makes the
/// posted lambda run synchronously, so the fire-and-forget reload has finished by the time <c>Raise</c>
/// returns and the assertions need no polling.
/// </para>
/// </summary>
public sealed class AssistantViewModelManagedPersonaTests
{
    private const string WithdrawnKey = "Msg_Settings_ManagedPersonaWithdrawn";

    private readonly IPersonaService _personas = Substitute.For<IPersonaService>();
    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();
    private readonly global::Wpf.Ui.ISnackbarService _snackbar = Substitute.For<global::Wpf.Ui.ISnackbarService>();
    private readonly ILocalizationService _localization = Substitute.For<ILocalizationService>();

    private static Persona BuiltIn(string name) =>
        new() { Id = Guid.NewGuid(), Name = name, SystemPrompt = "prompt", IsBuiltIn = true };

    /// <summary>The operating-mode built-in the withdrawn selection falls back to.</summary>
    private readonly Persona _fallback = BuiltIn("Pia Personal");

    private AssistantViewModel CreateSut()
    {
        // ChatTitleChipViewModel (built in the ctor) requires a captured SynchronizationContext.
        if (SynchronizationContext.Current is null)
            SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());

        _settings.GetSettingsAsync().Returns(new AppSettings());
        _personas.GetPersonasAsync().Returns(_ => Task.FromResult<IReadOnlyList<Persona>>([_fallback]));
        _personas.ResolveActiveAsync(Arg.Any<WindowMode>(), Arg.Any<UserOperatingMode>())
            .Returns(_ => Task.FromResult(_fallback));

        // Echo the key and the substituted arguments instead of formatting English copy, so the assertions
        // pin the resource key and both names without breaking when translators reword the template.
        _localization.Format(Arg.Any<string>(), Arg.Any<object[]>()).Returns(call =>
            $"{call.ArgAt<string>(0)}|{string.Join("|", call.ArgAt<object[]>(1).Select(a => a?.ToString()))}");
        _localization[Arg.Any<string>()].Returns(call => call.Arg<string>());

        var meeting = new MeetingAttendeeViewModel(
            Substitute.For<IMeetingAttendeeService>(),
            _settings,
            Substitute.For<ILocalizationService>(),
            Substitute.For<IFileDialogService>(),
            Substitute.For<IDialogService>(),
            NullLogger<MeetingAttendeeViewModel>.Instance,
            new InlineUiDispatcher());

        var directTranscription = new DirectTranscriptionViewModel(
            Substitute.For<IDirectTranscriptionService>(),
            _settings,
            Substitute.For<ILocalizationService>(),
            Substitute.For<IFileDialogService>(),
            Substitute.For<IDialogService>(),
            NullLogger<DirectTranscriptionViewModel>.Instance,
            new InlineUiDispatcher());

        return new AssistantViewModel(
            NullLogger<AssistantViewModel>.Instance,
            Substitute.For<IAiClientService>(),
            Substitute.For<IProviderService>(),
            _personas,
            _settings,
            Substitute.For<IOutputService>(),
            Substitute.For<IPluginService>(),
            Substitute.For<IVoiceInputService>(),
            Substitute.For<ITtsService>(),
            Substitute.For<IAudioRecordingService>(),
            Substitute.For<ITranscriptionService>(),
            NullLoggerFactory.Instance,
            _snackbar,
            _localization,
            Substitute.For<ITokenMapService>(),
            Substitute.For<IAutocompleteService>(),
            Substitute.For<INavigationService>(),
            Substitute.For<ISuggestionService>(),
            Substitute.For<IAssistantChatService>(),
            meeting,
            directTranscription,
            Substitute.For<IAssistantPromptComposer>(),
            Substitute.For<IProviderCapabilityService>(),
            Substitute.For<IAgentRunService>(),
            Substitute.For<IAgentRunResumeService>(),
            Substitute.For<IChatSessionManager>(),
            Substitute.For<IWorkingDirectoryService>(),
            Substitute.For<IFilesToolHandler>(),
            Substitute.For<IMarkdownExportService>(),
            Substitute.For<IDialogService>(),
            new InlineUiDispatcher(),
            Substitute.For<IToolPermissionService>());
    }

    /// <summary>
    /// The message argument of every <c>ISnackbarService.Show</c> call, read off <c>ReceivedCalls()</c> by
    /// method name and position rather than with an <c>Arg.Is</c> matcher, so this survives WPF-UI
    /// reshuffling <c>Show</c>'s optional parameters.
    /// </summary>
    private List<string> ShownMessages() =>
        _snackbar.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == "Show")
            .Select(call => call.GetArguments())
            .Where(args => args.Length > 1)
            .Select(args => args[1])
            .OfType<string>()
            .ToList();

    private void RaiseWithdrawn(Guid id, string name) =>
        _personas.ManagedPersonaWithdrawn += Raise.EventWith(
            new ManagedPersonaWithdrawnEventArgs { PersonaId = id, PersonaName = name });

    private void RaisePersonasChanged() =>
        _personas.PersonasChanged += Raise.Event<EventHandler>(_personas, EventArgs.Empty);

    [Fact]
    public void Withdrawal_then_reload_shows_one_notice_naming_the_persona_and_the_fallback()
    {
        var vm = CreateSut();

        RaiseWithdrawn(Guid.NewGuid(), "Brandvoice");
        RaisePersonasChanged();

        var shown = Assert.Single(ShownMessages());
        Assert.Contains(WithdrawnKey, shown);
        Assert.Contains("Brandvoice", shown);
        Assert.Contains(_fallback.Name, shown);
        // Non-vacuity: the reload really did land on the fallback, which is what the notice claims.
        Assert.Equal(_fallback.Id, vm.ActivePersona?.Id);
    }

    [Fact]
    public void A_second_reload_does_not_repeat_the_notice()
    {
        // The one-shot. A later pull that changes any persona raises PersonasChanged again, and the stash
        // was consumed by the first reload — the latch that stops a re-detection is the cleared per-mode
        // selection in PersonaService, so there is nothing left here to re-announce.
        CreateSut();

        RaiseWithdrawn(Guid.NewGuid(), "Brandvoice");
        RaisePersonasChanged();
        RaisePersonasChanged();

        Assert.Single(ShownMessages());
    }

    [Fact]
    public void A_reload_that_still_resolves_the_withdrawn_persona_defers_the_notice()
    {
        // A reload can START before the managed replace and FINISH after it: the pull raises PersonasChanged
        // once per applied user persona, well before it applies the managed snapshot, and LoadPersonasAsync is
        // fire-and-forget. Such a reload still holds pre-replace data, so consuming the stash there would show
        // "Brandvoice is no longer available; switched to Brandvoice" and burn the one shot on a nonsense
        // message. It must be left pending for the reload that sees the real fallback.
        var withdrawn = new Persona
        {
            Id = Guid.NewGuid(),
            Name = "Brandvoice",
            SystemPrompt = "prompt",
            IsManaged = true,
        };
        var vm = CreateSut();

        // Configured after CreateSut so these win: the first reload sees the store as it was BEFORE the
        // replace, the second sees it after.
        var beforeReplace = true;
        _personas.GetPersonasAsync().Returns(_ =>
            Task.FromResult<IReadOnlyList<Persona>>(beforeReplace ? [withdrawn, _fallback] : [_fallback]));
        _personas.ResolveActiveAsync(Arg.Any<WindowMode>(), Arg.Any<UserOperatingMode>())
            .Returns(_ => Task.FromResult(beforeReplace ? withdrawn : _fallback));

        RaiseWithdrawn(withdrawn.Id, withdrawn.Name);
        RaisePersonasChanged();

        Assert.Empty(ShownMessages());
        Assert.Equal(withdrawn.Id, vm.ActivePersona?.Id);

        // The replace has now landed, and PersonaService always raises PersonasChanged after the withdrawal
        // event — so a reload that resolves the real fallback is guaranteed to follow, and it announces once.
        beforeReplace = false;
        RaisePersonasChanged();

        var shown = Assert.Single(ShownMessages());
        Assert.Contains(WithdrawnKey, shown);
        Assert.Contains("Brandvoice", shown);
        Assert.Contains(_fallback.Name, shown);
        Assert.Equal(_fallback.Id, vm.ActivePersona?.Id);
    }

    [Fact]
    public void An_ordinary_reload_with_no_withdrawal_shows_nothing()
    {
        // Guards the other direction: every sync pull that touches a persona raises PersonasChanged, so an
        // unconditional notice here would nag on routine syncs.
        CreateSut();

        RaisePersonasChanged();

        Assert.Empty(ShownMessages());
    }
}
