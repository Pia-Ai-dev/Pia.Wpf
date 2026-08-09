using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;
// Microsoft.Extensions.AI also defines a ReasoningEffort; the persona carries the Pia one.
using ReasoningEffort = Pia.Models.ReasoningEffort;

namespace Pia.Tests.Services;

// An assigned persona brings its own system prompt, tool list and provider; every way that can go wrong degrades to
// the run default and none of them throws, because a per-step persona must never be able to fail a run.
public sealed class StepPersonaResolverTests
{
    private readonly IPersonaService _personas = Substitute.For<IPersonaService>();
    private readonly IProviderService _providers = Substitute.For<IProviderService>();
    private readonly IAssistantPromptComposer _composer = Substitute.For<IAssistantPromptComposer>();
    private readonly ISettingsService _settingsService = Substitute.For<ISettingsService>();

    // A REAL AppSettings: NSubstitute's auto-value for Task<AppSettings> wraps NULL, which would NRE in the
    // roster read (AppSettings is a plain class, not substitutable).
    private readonly AppSettings _settings = new();

    public StepPersonaResolverTests() =>
        _settingsService.GetSettingsAsync().Returns(_ => Task.FromResult(_settings));

    private StepPersonaResolver Build() => new(
        _personas, _providers, _composer, _settingsService, NullLogger<StepPersonaResolver>.Instance);

    private static Persona Persona(string name, Guid? preferredProviderId = null, ReasoningEffort? effort = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            SystemPrompt = $"you are {name}",
            PreferredProviderId = preferredProviderId,
            ReasoningEffort = effort,
        };

    private static AiProvider Provider(string name = "P", ReasoningEffort? effort = null) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Endpoint = "https://x",
        ProviderType = AiProviderType.OpenAI,
        ReasoningEffort = effort,
    };

    private static StepPersonaSetup RunDefault() =>
        new(Persona("Pia"), Provider("run-provider"), new AssistantTurnSetup("run system", null, false, false));

    private void Roster(params Persona[] personas)
    {
        _settings.SetAgentPersonaRoster(UserOperatingMode.Personal, personas.Select(p => p.Id).ToList());
        _personas.GetPersonasAsync().Returns(personas.ToList());
        foreach (var p in personas)
            _personas.GetPersonaAsync(p.Id).Returns(p);
    }

    // The composed prompt names the persona, so a test can tell whose prompt came back.
    private void ComposerEchoesThePersona() =>
        _composer.PrepareTurn(Arg.Any<Persona>(), Arg.Any<AiProvider>(), Arg.Any<IReadOnlyList<AtCommand>>(),
                Arg.Any<bool>(), Arg.Any<bool>())
            .Returns(ci => new AssistantTurnSetup($"system for {ci.ArgAt<Persona>(0).Name}", null, true, false));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task NullAssignedId_ReturnsTheRunDefault_Unchanged()
    {
        var runDefault = RunDefault();

        var resolved = await Build().ResolveAsync(null, runDefault, tokenizationEnabled: false, Ct);

        // The very same objects, not equal copies: the null arm is "today's behaviour" and must not rebuild
        // anything, because rebuilding is where a difference could creep in.
        Assert.Same(runDefault, resolved);
        Assert.Same(runDefault.Persona, resolved.Persona);
        Assert.Same(runDefault.Provider, resolved.Provider);
        Assert.Same(runDefault.TurnSetup, resolved.TurnSetup);
        await _personas.DidNotReceive().GetPersonaAsync(Arg.Any<Guid>());
        await _personas.DidNotReceive().GetPersonasAsync();
    }

    [Fact]
    public async Task AssignedPersona_GetsItsOwnSystemPromptAndTools()
    {
        // A per-step persona that does not re-compose the turn setup is INERT: it changes the label and nothing
        // the model reads.
        var analyst = Persona("Analyst");
        Roster(analyst);
        ComposerEchoesThePersona();
        _providers.GetDefaultProviderForModeAsync(Arg.Any<WindowMode>()).Returns(Provider());
        var runDefault = RunDefault();

        var resolved = await Build().ResolveAsync(analyst.Id, runDefault, tokenizationEnabled: false, Ct);

        Assert.Same(analyst, resolved.Persona);
        Assert.Equal("system for Analyst", resolved.TurnSetup.SystemPrompt);
        Assert.NotEqual(runDefault.TurnSetup.SystemPrompt, resolved.TurnSetup.SystemPrompt);
        Assert.True(resolved.TurnSetup.SupportsTools);  // the tool list comes from the persona's setup too
        _composer.Received(1).PrepareTurn(analyst, Arg.Any<AiProvider>(), Arg.Any<IReadOnlyList<AtCommand>>(),
            Arg.Any<bool>(), Arg.Any<bool>());

        // The persona comes from the roster list already in hand, not a second per-id store round-trip: that
        // lookup does raw SQLite I/O outside any executor try/catch, so a busy connection failed the whole run.
        await _personas.DidNotReceive().GetPersonaAsync(Arg.Any<Guid>());
    }

    [Fact]
    public async Task AssignedPersona_UsesItsPreferredProvider_NotTheRunsOne()
    {
        // A roster persona was chosen BECAUSE of its provider, so the run-level provider does not win here.
        var preferred = Provider("preferred");
        var analyst = Persona("Analyst", preferredProviderId: preferred.Id);
        Roster(analyst);
        ComposerEchoesThePersona();
        _providers.GetProviderAsync(preferred.Id).Returns(preferred);
        var runDefault = RunDefault();

        var resolved = await Build().ResolveAsync(analyst.Id, runDefault, tokenizationEnabled: false, Ct);

        Assert.Equal(preferred.Id, resolved.Provider.Id);
        Assert.NotEqual(runDefault.Provider.Id, resolved.Provider.Id);
    }

    [Fact]
    public async Task ReasoningEffort_IsAppliedToACLONE_NeverToTheSharedProvider()
    {
        var shared = Provider("shared", effort: ReasoningEffort.Low);
        var analyst = Persona("Analyst", preferredProviderId: shared.Id, effort: ReasoningEffort.High);
        Roster(analyst);
        ComposerEchoesThePersona();
        _providers.GetProviderAsync(shared.Id).Returns(shared);

        var resolved = await Build().ResolveAsync(analyst.Id, RunDefault(), tokenizationEnabled: false, Ct);

        Assert.Equal(ReasoningEffort.High, resolved.Provider.ReasoningEffort);
        // The instance the store handed out is untouched — mutating it would leak this persona's effort into
        // every other run in the process.
        Assert.Equal(ReasoningEffort.Low, shared.ReasoningEffort);
        Assert.NotSame(shared, resolved.Provider);
    }

    [Fact]
    public async Task UnresolvablePersona_FallsBackToTheRunDefault()
    {
        // The persona was deleted between plan and execute: its id is still in the configured roster but the store
        // no longer returns it, so the roster read drops it and the assignment has nothing to resolve against.
        var ghost = Persona("Ghost");
        var survivor = Persona("Survivor");
        _settings.SetAgentPersonaRoster(UserOperatingMode.Personal, [ghost.Id, survivor.Id]);
        _personas.GetPersonasAsync().Returns([survivor]);
        var runDefault = RunDefault();

        var resolved = await Build().ResolveAsync(ghost.Id, runDefault, tokenizationEnabled: false, Ct);

        Assert.Same(runDefault, resolved);
        _composer.DidNotReceive().PrepareTurn(Arg.Any<Persona>(), Arg.Any<AiProvider>(),
            Arg.Any<IReadOnlyList<AtCommand>>(), Arg.Any<bool>(), Arg.Any<bool>());
    }

    [Fact]
    public async Task APersonaOffTheCurrentRoster_FallsBackToTheRunDefault_EvenThoughItResolves()
    {
        // A plan outlives the setting that produced it: a replan, a resume, or a roster the user has since
        // edited. The persona resolves perfectly well — it is simply not one the user offered up.
        var onRoster = Persona("Analyst");
        var offRoster = Persona("Gandalf");
        Roster(onRoster);
        // Both personas exist in the STORE — that is what makes this a containment check rather than a
        // resolution failure. Only one of them is on the configured roster.
        _personas.GetPersonasAsync().Returns([onRoster, offRoster]);
        ComposerEchoesThePersona();
        var runDefault = RunDefault();

        var resolved = await Build().ResolveAsync(offRoster.Id, runDefault, tokenizationEnabled: false, Ct);

        Assert.Same(runDefault, resolved);
        // Positive control: the SAME resolver honours an on-roster id, so this is a containment check and not
        // a resolver that never resolves anything.
        var honoured = await Build().ResolveAsync(onRoster.Id, runDefault, tokenizationEnabled: false, Ct);
        Assert.Same(onRoster, honoured.Persona);
    }

    [Fact]
    public async Task WithAnEmptyRoster_NoAssignedIdIsEverHonoured()
    {
        // The opt-in at the EXECUTOR seam: even a persisted plan that already carries assignments goes back to the
        // run persona once the roster is empty.
        var analyst = Persona("Analyst");
        _personas.GetPersonaAsync(analyst.Id).Returns(analyst);
        _personas.GetPersonasAsync().Returns([analyst]);
        var runDefault = RunDefault();

        var resolved = await Build().ResolveAsync(analyst.Id, runDefault, tokenizationEnabled: false, Ct);

        Assert.Same(runDefault, resolved);
    }

    [Fact]
    public async Task UnresolvableProvider_BorrowsTheRunProvider_ButKeepsTheAssignedPersonaAndItsPrompt()
    {
        // The one PARTIAL arm: dropping the persona here would throw away its SYSTEM PROMPT, which is the whole
        // substance of assigning the step.
        var analyst = Persona("Analyst", preferredProviderId: Guid.NewGuid());
        Roster(analyst);
        ComposerEchoesThePersona();
        _providers.GetProviderAsync(Arg.Any<Guid>()).Returns((AiProvider?)null);
        _providers.GetDefaultProviderForModeAsync(Arg.Any<WindowMode>()).Returns((AiProvider?)null);
        var runDefault = RunDefault();

        var resolved = await Build().ResolveAsync(analyst.Id, runDefault, tokenizationEnabled: false, Ct);

        Assert.Same(runDefault.Provider, resolved.Provider);      // borrowed
        Assert.Same(analyst, resolved.Persona);                    // kept
        Assert.Equal("system for Analyst", resolved.TurnSetup.SystemPrompt);  // kept, and it is the point
    }

    [Fact]
    public async Task PrepareTurnThrows_FallsBackToTheRunDefault()
    {
        var analyst = Persona("Analyst");
        Roster(analyst);
        _providers.GetDefaultProviderForModeAsync(Arg.Any<WindowMode>()).Returns(Provider());
        _composer.PrepareTurn(Arg.Any<Persona>(), Arg.Any<AiProvider>(), Arg.Any<IReadOnlyList<AtCommand>>(),
            Arg.Any<bool>(), Arg.Any<bool>()).Throws(new InvalidOperationException("bad persona prompt"));
        var runDefault = RunDefault();

        // Defence in depth: one bad persona prompt must not fail every step of a run.
        var resolved = await Build().ResolveAsync(analyst.Id, runDefault, tokenizationEnabled: false, Ct);

        Assert.Same(runDefault, resolved);
    }

    [Fact]
    public async Task ResolvesEachPersonaOnce_AcrossManySteps()
    {
        // Recomposing the prompt on every step is waste, and the memo is also what keeps a persona edited mid-run
        // from changing the prompt at step 13.
        var analyst = Persona("Analyst");
        Roster(analyst);
        ComposerEchoesThePersona();
        _providers.GetDefaultProviderForModeAsync(Arg.Any<WindowMode>()).Returns(Provider());
        var resolver = Build();
        var runDefault = RunDefault();

        for (var i = 0; i < 5; i++)
            await resolver.ResolveAsync(analyst.Id, runDefault, tokenizationEnabled: false, Ct);

        Assert.Single(_composer.ReceivedCalls());
    }

    [Fact]
    public async Task SuggestAgentModeIsNeverOfferedInsideARun()
    {
        // suggest_agent_mode offers to switch the USER into Agent mode; inside a run there is nobody to offer it
        // to, and the tool has no surface.
        var analyst = Persona("Analyst");
        Roster(analyst);
        ComposerEchoesThePersona();
        _providers.GetDefaultProviderForModeAsync(Arg.Any<WindowMode>()).Returns(Provider());

        await Build().ResolveAsync(analyst.Id, RunDefault(), tokenizationEnabled: true, Ct);

        _composer.DidNotReceive().PrepareTurn(Arg.Any<Persona>(), Arg.Any<AiProvider>(),
            Arg.Any<IReadOnlyList<AtCommand>>(), Arg.Any<bool>(), suggestAgentModeEligible: true);
        _composer.Received(1).PrepareTurn(analyst, Arg.Any<AiProvider>(), Arg.Any<IReadOnlyList<AtCommand>>(),
            tokenizationEnabled: true, suggestAgentModeEligible: false);
    }

    [Fact]
    public async Task GetRoster_ClampsToSix_DedupesAndDropsUnknownIds()
    {
        var personas = new List<Persona>();
        for (var i = 0; i < 8; i++)
            personas.Add(Persona($"P{i}"));
        var unknown = Guid.NewGuid();

        // Written STRAIGHT into the dictionary, bypassing the setter: a hand-edited settings file never went
        // through SetAgentPersonaRoster, so the READ side is what has to hold.
        _settings.AgentPersonaRoster[UserOperatingMode.Personal] =
        [
            personas[0].Id, personas[0].Id, personas[1].Id, unknown,
            personas[2].Id, personas[3].Id, personas[3].Id, personas[4].Id, personas[5].Id,
        ];
        _personas.GetPersonasAsync().Returns(personas);

        var roster = await Build().GetRosterAsync(Ct);

        // Deduped to [P0, P1, unknown, P2, P3, P4], clamped at 6, then the unknown dropped: five personas in
        // configured order. P5 fell off the cap, and the unknown left no blank line in the plan prompt.
        Assert.Equal(new[] { personas[0], personas[1], personas[2], personas[3], personas[4] }, roster);
    }

    [Fact]
    public async Task GetRoster_IsEmptyWhenNothingIsConfigured()
    {
        // Nothing configured ⇒ nothing listed ⇒ nothing assigned.
        Assert.Empty(await Build().GetRosterAsync(Ct));
        await _personas.DidNotReceive().GetPersonasAsync();
    }

    [Fact]
    public async Task ASettingsFault_YieldsAnEmptyRoster_RatherThanThrowing()
    {
        _settingsService.GetSettingsAsync().Throws(new IOException("settings unavailable"));

        // Reading the roster gates an optional feature, so the answer on any fault is "no specialists".
        Assert.Empty(await Build().GetRosterAsync(Ct));
    }
}
