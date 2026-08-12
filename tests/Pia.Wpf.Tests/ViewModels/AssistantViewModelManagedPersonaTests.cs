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
/// The two events are raised in the service's order because the notice names the fallback, which exists only
/// once the reload resolved it; <c>InlineUiDispatcher</c> runs the posted lambda inline so nothing polls.
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

        // Echo the key and arguments instead of English copy, so the assertions survive a reworded template.
        _localization.Format(Arg.Any<string>(), Arg.Any<object[]>()).Returns(call =>
            $"{call.ArgAt<string>(0)}|{string.Join("|", call.ArgAt<object[]>(1).Select(a => a?.ToString()))}");
        _localization[Arg.Any<string>()].Returns(call => call.Arg<string>());

        var meeting = new MeetingAttendeeViewModel(
            Substitute.For<IMeetingAttendeeService>(),
            _settings,
            Substitute.For<ILocalizationService>(),
            Substitute.For<IFileDialogService>(),
            Substitute.For<IDialogService>(),
            Substitute.For<IMemoryService>(),
            Substitute.For<IIngestScheduler>(),
            Substitute.For<Wpf.Ui.ISnackbarService>(),
            NullLogger<MeetingAttendeeViewModel>.Instance,
            new InlineUiDispatcher());

        var directTranscription = new DirectTranscriptionViewModel(
            Substitute.For<IDirectTranscriptionService>(),
            _settings,
            Substitute.For<ILocalizationService>(),
            Substitute.For<IFileDialogService>(),
            Substitute.For<IDialogService>(),
            Substitute.For<IMemoryService>(),
            Substitute.For<IIngestScheduler>(),
            Substitute.For<Wpf.Ui.ISnackbarService>(),
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

    /// <summary>Read by method name and position, not an <c>Arg.Is</c> matcher, so it survives WPF-UI reshuffling <c>Show</c>'s optional parameters.</summary>
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
        // The latch is the cleared per-mode selection in PersonaService, so nothing is left here to re-announce.
        CreateSut();

        RaiseWithdrawn(Guid.NewGuid(), "Brandvoice");
        RaisePersonasChanged();
        RaisePersonasChanged();

        Assert.Single(ShownMessages());
    }

    [Fact]
    public void A_reload_that_still_resolves_the_withdrawn_persona_defers_the_notice()
    {
        // A reload can START before the managed replace and FINISH after it, so consuming the stash there would
        // announce the withdrawn persona as its own fallback and burn the one shot.
        var withdrawn = new Persona
        {
            Id = Guid.NewGuid(),
            Name = "Brandvoice",
            SystemPrompt = "prompt",
            IsManaged = true,
        };
        var vm = CreateSut();

        // Configured after CreateSut so these win: the first reload sees the store before the replace.
        var beforeReplace = true;
        _personas.GetPersonasAsync().Returns(_ =>
            Task.FromResult<IReadOnlyList<Persona>>(beforeReplace ? [withdrawn, _fallback] : [_fallback]));
        _personas.ResolveActiveAsync(Arg.Any<WindowMode>(), Arg.Any<UserOperatingMode>())
            .Returns(_ => Task.FromResult(beforeReplace ? withdrawn : _fallback));

        RaiseWithdrawn(withdrawn.Id, withdrawn.Name);
        RaisePersonasChanged();

        Assert.Empty(ShownMessages());
        Assert.Equal(withdrawn.Id, vm.ActivePersona?.Id);

        // PersonaService always raises PersonasChanged after the withdrawal event, so this reload is guaranteed.
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
        // Every sync pull that touches a persona raises PersonasChanged, so an unconditional notice would nag.
        CreateSut();

        RaisePersonasChanged();

        Assert.Empty(ShownMessages());
    }
}
