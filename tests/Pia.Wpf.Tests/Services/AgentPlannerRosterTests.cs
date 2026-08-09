using System.IO;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Services.Providers;
using Xunit;

namespace Pia.Tests.Services;

public sealed class AgentPlannerRosterTests
{
    private readonly IAiClientService _ai = Substitute.For<IAiClientService>();
    private readonly ISettingsService _settingsService = Substitute.For<ISettingsService>();
    private readonly IPersonaService _personaService = Substitute.For<IPersonaService>();
    private readonly IProviderService _providerService = Substitute.For<IProviderService>();
    private readonly IAssistantPromptComposer _composer = Substitute.For<IAssistantPromptComposer>();
    private readonly AppSettings _settings = new();
    private readonly CapturingLogger<AgentPlanner> _logger = new();

    private const string Goal = "ship the widget catalogue";

    public AgentPlannerRosterTests() =>
        _settingsService.GetSettingsAsync().Returns(_ => Task.FromResult(_settings));

    private static AiProvider Provider() => new()
    {
        Name = "P", Endpoint = "https://x", ProviderType = AiProviderType.OpenAI, SupportsToolCalling = true,
    };

    private static Persona Persona(string name, string? tagline = null) =>
        new() { Id = Guid.NewGuid(), Name = name, SystemPrompt = $"you are {name}", Tagline = tagline };

    private static RunContext Ctx() => new(Goal, RunProfile.Interactive);

    /// <summary>A resolver over this fixture's settings + persona store, or null for "no roster source at all".</summary>
    private StepPersonaResolver Resolver() => new(
        _personaService, _providerService, _composer, _settingsService,
        NullLogger<StepPersonaResolver>.Instance);

    private AgentPlanner Planner(StepPersonaResolver? personas)
    {
        var handler = Substitute.For<IAiProviderHandler>();
        handler.ProviderType.Returns(AiProviderType.OpenAI);
        handler.DropsReasoningEffortWithTools.Returns(false);
        // The planner takes a FACTORY (one resolver per plan — the planner outlives a plan on the interactive
        // path). Handing back the same instance every call is what lets a fixture keep asserting on it.
        return new AgentPlanner(_ai, new AiProviderHandlerResolver([handler]), _settingsService, _logger,
            personas is null ? null : () => personas);
    }

    /// <summary>Registers <paramref name="personas"/> as the configured roster for the settings' mode.</summary>
    private void Roster(params Persona[] personas)
    {
        _settings.SetAgentPersonaRoster(UserOperatingMode.Personal, personas.Select(p => p.Id).ToList());
        _personaService.GetPersonasAsync().Returns(personas.ToList());
    }

    private static async IAsyncEnumerable<ChatStreamItem> PlanStream(
        ToolCallHandler? handler, Dictionary<string, object?>? emitArgs)
    {
        if (handler is not null && emitArgs is not null)
            await handler(new FunctionCallContent(Guid.NewGuid().ToString(), "emit_plan", emitArgs), new ToolDispatchContext(1));
        await Task.Yield();
        yield return new Finished(null, "test-model");
    }

    /// <summary>The <c>Steps(...)</c> builder of <see cref="AgentPlannerTests"/>, plus the persona and group keys.</summary>
    private static Dictionary<string, object?> Steps(
        params (string Title, string Intent, string? PersonaKey, int? ParallelGroup)[] steps)
    {
        var arr = steps
            .Select(s => (object)new Dictionary<string, object?>
            {
                ["title"] = s.Title,
                ["intent"] = s.Intent,
                ["expectedArtifact"] = null,
                ["personaKey"] = s.PersonaKey,
                ["parallelGroup"] = s.ParallelGroup,
            })
            .ToArray();
        return new Dictionary<string, object?> { ["steps"] = arr };
    }

    private readonly List<string> _systemPrompts = new();
    private readonly List<string> _userPrompts = new();

    private string LastSystemPrompt => _systemPrompts[^1];
    private string LastUserPrompt => _userPrompts[^1];

    private void ReturnsPlan(Dictionary<string, object?>? emitArgs)
    {
        _ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(), cancellationToken: Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var messages = ci.ArgAt<IList<ChatMessage>>(0);
                _systemPrompts.Add(messages[0].Text ?? string.Empty);
                _userPrompts.Add(messages[1].Text ?? string.Empty);
                return PlanStream(ci.ArgAt<ToolCallHandler?>(3), emitArgs);
            });
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task EmptyRoster_ProducesTheExactPrePhase3PlanPrompt()
    {
        // The comparison is what makes this strong: a planner WITH a roster source but no configured roster must
        // build a prompt identical to one built with no roster source at all.
        ReturnsPlan(Steps(("Gather", "collect the inputs", null, null)));

        await Planner(personas: null).PlanAsync(Goal, Ctx(), Persona("Pia"), Provider(), Ct);
        var withoutSource = LastSystemPrompt;

        await Planner(Resolver()).PlanAsync(Goal, Ctx(), Persona("Pia"), Provider(), Ct);
        var withEmptyRoster = LastSystemPrompt;

        Assert.Equal(withoutSource, withEmptyRoster);
        Assert.DoesNotContain("personaKey", withEmptyRoster);
        Assert.DoesNotContain("parallelGroup", withEmptyRoster);
    }

    [Fact]
    public async Task AnEmptyRoster_MeansNoStepIsEverAssigned()
    {
        // Even when the model volunteers both keys — the tool schema is generated unconditionally, so it may have
        // seen them — an unconfigured roster assigns nothing.
        ReturnsPlan(Steps(
            ("Gather", "collect the inputs", "Analyst", 1),
            ("Draft", "write it up", "Writer", 1)));

        var result = await Planner(Resolver()).PlanAsync(Goal, Ctx(), Persona("Pia"), Provider(), Ct);

        Assert.Equal(2, result.Steps.Count);
        Assert.All(result.Steps, s => Assert.Null(s.AssignedPersonaId));
        // parallelGroup is the other member that could make a step row differ, and the roster block is the only
        // thing that mentions it to the model — so with no roster a value in it was never asked for.
        Assert.All(result.Steps, s => Assert.Null(s.ExtraJson));
    }

    [Fact]
    public async Task NonEmptyRoster_ListsEveryPersonaNameOnce_InTheSystemMessage()
    {
        var analyst = Persona("Analyst", "digs through data");
        var writer = Persona("Writer", "turns notes into prose");
        var critic = Persona("Critic", "finds the hole");
        Roster(analyst, writer, critic);
        ReturnsPlan(Steps(("Gather", "collect the inputs", null, null)));

        await Planner(Resolver()).PlanAsync(Goal, Ctx(), Persona("Pia"), Provider(), Ct);

        foreach (var name in new[] { "Analyst", "Writer", "Critic" })
            Assert.Equal(1, CountOccurrences(LastSystemPrompt, name));
        Assert.Contains("personaKey", LastSystemPrompt);
        Assert.Contains("digs through data", LastSystemPrompt);

        // The block is on the SYSTEM message, never the user one: TokenizingAiClientService rewrites only
        // ChatRole.User text to PII placeholders, and a roster is app-owned configuration.
        Assert.Equal(Goal, LastUserPrompt);
        Assert.DoesNotContain("Analyst", LastUserPrompt);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }
        return count;
    }

    [Fact]
    public async Task AMatchedPersonaKey_LandsOnTheStepAsAssignedPersonaId()
    {
        var analyst = Persona("Analyst");
        var writer = Persona("Writer");
        Roster(analyst, writer);
        ReturnsPlan(Steps(
            ("Gather", "collect the inputs", "Analyst", null),
            ("Draft", "write it up", null, null)));

        var result = await Planner(Resolver()).PlanAsync(Goal, Ctx(), Persona("Pia"), Provider(), Ct);

        Assert.Equal(analyst.Id, result.Steps[0].AssignedPersonaId);
        Assert.Null(result.Steps[1].AssignedPersonaId);   // omitting the key means the run persona
    }

    [Fact]
    public async Task MatchingIsCaseAndWhitespaceInsensitive()
    {
        // A model that echoes the name with stray padding or different casing still meant that persona; failing
        // on it would silently downgrade the step to the run persona.
        var analyst = Persona("Analyst");
        Roster(analyst);
        ReturnsPlan(Steps(("Gather", "collect the inputs", "  aNaLySt ", null)));

        var result = await Planner(Resolver()).PlanAsync(Goal, Ctx(), Persona("Pia"), Provider(), Ct);

        Assert.Equal(analyst.Id, result.Steps[0].AssignedPersonaId);
    }

    [Fact]
    public async Task AnUnknownPersonaKey_LeavesTheStepUnassigned_AndIsNeverLogged()
    {
        Roster(Persona("Analyst"));
        ReturnsPlan(Steps(
            ("Gather", "collect the inputs", "Gandalf", null),
            ("Draft", "write it up", "Gandalf", null)));

        var result = await Planner(Resolver()).PlanAsync(Goal, Ctx(), Persona("Pia"), Provider(), Ct);

        Assert.All(result.Steps, s => Assert.Null(s.AssignedPersonaId));

        // The key ECHOES a persona name, i.e. user-named content: the log gets a COUNT and never the key.
        var lines = _logger.Entries.Where(e => e.Level >= LogLevel.Information).Select(e => e.Message).ToList();
        Assert.DoesNotContain(lines, m => m.Contains("Gandalf", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(lines, m => m.Contains("2 step(s) to an unknown persona", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ParallelGroup_RoundTripsThroughStepExtraJson()
    {
        Roster(Persona("Analyst"));
        ReturnsPlan(Steps(
            ("Gather", "collect the inputs", null, 2),
            ("Draft", "write it up", null, null)));

        var result = await Planner(Resolver()).PlanAsync(Goal, Ctx(), Persona("Pia"), Provider(), Ct);

        Assert.Equal("{\"parallelGroup\":2}", result.Steps[0].ExtraJson);
        using var doc = JsonDocument.Parse(result.Steps[0].ExtraJson!);
        Assert.Equal(2, doc.RootElement.GetProperty("parallelGroup").GetInt32());
        Assert.Null(result.Steps[1].ExtraJson);   // absent means sequential, i.e. today
    }

    [Fact]
    public async Task ValidatePlan_IsUnaffectedByPersonaAndGroupFields()
    {
        // An unknown persona key is a cosmetic model slip, not a plan defect; validating it would throw away a
        // perfectly good plan and degrade the whole run to SingleTurn.
        Roster(Persona("Analyst"));
        ReturnsPlan(Steps(
            ("Gather", "collect the inputs", "Gandalf", 99),
            ("Draft", "write it up", "Saruman", 99)));

        var result = await Planner(Resolver()).PlanAsync(Goal, Ctx(), Persona("Pia"), Provider(), Ct);

        Assert.False(result.FallBackToSingleTurn);
        Assert.Equal(2, result.Steps.Count);
    }

    [Fact]
    public async Task ReplanAlsoAssignsPersonas()
    {
        // A replan REPLACES the remaining plan, so a roster threaded into PlanAsync only would make the first
        // failure silently strip every assignment for the rest of the run.
        var analyst = Persona("Analyst");
        Roster(analyst);
        ReturnsPlan(Steps(("Recover", "try another way", "Analyst", null)));

        var result = await Planner(Resolver()).ReplanAsync(Ctx(), "step 1 blew up", Persona("Pia"), Provider(), Ct);

        Assert.False(result.FallBackToSingleTurn);
        Assert.Equal(analyst.Id, result.Steps[0].AssignedPersonaId);
        Assert.Contains("Analyst", LastSystemPrompt);   // and the replan prompt listed the roster
    }

    [Fact]
    public async Task ARosterResolveFault_DegradesToTodaysPrompt()
    {
        Roster(Persona("Analyst"));
        // The roster read gates an optional feature: a fault there must cost the specialists, not the plan. Driven
        // through the SETTINGS read, which StepPersonaResolver swallows itself.
        _settingsService.GetSettingsAsync().Throws(new IOException("settings unavailable"));
        ReturnsPlan(Steps(("Gather", "collect the inputs", "Analyst", null)));

        var result = await Planner(Resolver()).PlanAsync(Goal, Ctx(), Persona("Pia"), Provider(), Ct);

        Assert.False(result.FallBackToSingleTurn);
        Assert.Single(result.Steps);
        Assert.Null(result.Steps[0].AssignedPersonaId);
        Assert.DoesNotContain("personaKey", LastSystemPrompt);
    }
}
