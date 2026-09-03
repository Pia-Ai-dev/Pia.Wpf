using System.Globalization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Resources.Strings;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;
using ReasoningEffort = Pia.Models.ReasoningEffort;

namespace Pia.Tests.Services;

/// <summary>
/// The assistant's route into the blueprint catalog. The interesting half is what the model CANNOT do here:
/// the blueprint owns the prompt, the kind, the grants and the effort, and a slot name it invents is refused
/// rather than defaulted.
/// </summary>
public class ScheduledJobBlueprintToolTests
{
    private static ScheduledJobToolHandler CreateHandler(RecordingJobService jobs)
    {
        var providers = Substitute.For<IProviderService>();
        providers.GetProvidersAsync().Returns(Array.Empty<AiProvider>());

        // Template, slot defaults and guard resolve for real — an echoed key carries no {topic} to render.
        var loc = Substitute.For<ILocalizationService>();
        loc[Arg.Any<string>()].Returns(ci =>
        {
            var key = (string)ci[0]!;
            var isBlueprintText = key.EndsWith("_Query", StringComparison.Ordinal)
                || (key.Contains("_Slot_", StringComparison.Ordinal) && key.EndsWith("_Default", StringComparison.Ordinal))
                || (key.StartsWith("Routines_Catalog_", StringComparison.Ordinal) && key.EndsWith("Guard", StringComparison.Ordinal));
            return isBlueprintText
                ? ViewStrings.ResourceManager.GetString(key, CultureInfo.InvariantCulture) ?? key
                : key;
        });
        loc.Format(Arg.Any<string>(), Arg.Any<object[]>()).Returns(ci => (string)ci[0]!);

        return new ScheduledJobToolHandler(jobs.Service, providers, loc,
            NullLogger<ScheduledJobToolHandler>.Instance);
    }

    private static FunctionCallContent MakeCall(string toolName, IDictionary<string, object?> args)
        => new("call-1", toolName, args);

    private static async Task<(object? Result, ScheduledJobToolCall? Pending)> CallAsync(
        RecordingJobService jobs, string toolName, IDictionary<string, object?> args) =>
        await CreateHandler(jobs).HandleToolCallAsync(MakeCall(toolName, args), TestContext.Current.CancellationToken);

    [Fact]
    public void BothBlueprintToolsAreDeclared()
    {
        var names = CreateHandler(new RecordingJobService()).GetTools()
            .OfType<AIFunction>().Select(f => f.Name).ToList();

        Assert.Contains("list_routine_blueprints", names);
        Assert.Contains("create_routine_from_blueprint", names);
    }

    /// <summary>The model cannot widen the grants because there is nowhere to put them — the absence of the
    /// parameter is the mechanism, so pin it.</summary>
    [Fact]
    public void CreateFromBlueprint_TakesNoGrantsNoQueryAndNoKind()
    {
        var tool = CreateHandler(new RecordingJobService()).GetTools()
            .OfType<AIFunction>().Single(f => f.Name == "create_routine_from_blueprint");

        var schema = tool.JsonSchema.ToString();

        Assert.Contains("blueprintKey", schema);
        Assert.DoesNotContain("grantedTools", schema);
        Assert.DoesNotContain("\"query\"", schema);
        Assert.DoesNotContain("\"kind\"", schema);
    }

    [Fact]
    public async Task ListBlueprints_NamesEveryKeyAndEverySlot()
    {
        var (result, pending) = await CallAsync(new RecordingJobService(), "list_routine_blueprints",
            new Dictionary<string, object?>());

        Assert.Null(pending);
        var text = Assert.IsType<string>(result);
        foreach (var bp in RoutineBlueprintCatalog.All)
        {
            Assert.Contains($"key: {bp.Key}", text);
            foreach (var slot in bp.Slots)
                Assert.Contains($"Slot '{slot.Name}'", text);
        }
    }

    /// <summary>A read tool must not produce an approval card.</summary>
    [Fact]
    public async Task ListBlueprints_IsARead()
    {
        var jobs = new RecordingJobService();

        var (_, pending) = await CallAsync(jobs, "list_routine_blueprints", new Dictionary<string, object?>());

        Assert.Null(pending);
        Assert.Empty(jobs.Created);
    }

    [Fact]
    public async Task CreateFromBlueprint_RendersTheSlotAndCarriesEveryPinTheCardPathHonours()
    {
        var jobs = new RecordingJobService();
        var blueprint = RoutineBlueprintCatalog.Find(RoutineBlueprintCatalog.TopicDigest)!;

        var (result, pending) = await CallAsync(jobs, "create_routine_from_blueprint", new Dictionary<string, object?>
        {
            ["blueprintKey"] = RoutineBlueprintCatalog.TopicDigest,
            ["slots"] = """{"topic":"quantum computing"}"""
        });

        Assert.Null(result);
        Assert.NotNull(pending);
        Assert.Equal("create_routine_from_blueprint", pending!.ToolName);
        Assert.Empty(jobs.Created);

        await CreateHandler(jobs).ExecutePendingActionAsync(pending);

        var created = Assert.Single(jobs.Created);
        Assert.Contains("quantum computing", created.Query);
        Assert.DoesNotContain("artificial intelligence", created.Query);
        Assert.DoesNotContain("{", created.Query);
        Assert.Equal(blueprint.Kind, created.Kind);
        Assert.Equal(blueprint.Recurrence, created.Recurrence);
        Assert.Equal(blueprint.DefaultTime, created.TimeOfDay);
        Assert.Equal(blueprint.DefaultEffort, created.ReasoningEffort);
        Assert.Equal(blueprint.Key, created.BlueprintKey);
        Assert.Equal(blueprint.GrantedTools, created.GrantedTools);
    }

    /// <summary>The card is where the user reads what they are approving, so it must show the RENDERED prompt
    /// rather than the template that still says <c>{topic}</c>.</summary>
    [Fact]
    public async Task CreateFromBlueprint_ShowsTheRenderedQueryOnTheCard()
    {
        var (_, pending) = await CallAsync(new RecordingJobService(), "create_routine_from_blueprint",
            new Dictionary<string, object?>
            {
                ["blueprintKey"] = RoutineBlueprintCatalog.TopicDigest,
                ["slots"] = """{"topic":"quantum computing"}"""
            });

        var details = pending!.Details ?? string.Empty;
        Assert.Contains("quantum computing", details);
        Assert.DoesNotContain("{topic}", details);
    }

    [Fact]
    public async Task CreateFromBlueprint_WithNoSlots_TakesTheBlueprintsOwnDefault()
    {
        var jobs = new RecordingJobService();

        var (_, pending) = await CallAsync(jobs, "create_routine_from_blueprint", new Dictionary<string, object?>
        {
            ["blueprintKey"] = RoutineBlueprintCatalog.TopicDigest
        });

        await CreateHandler(jobs).ExecutePendingActionAsync(pending!);

        Assert.Contains("artificial intelligence", Assert.Single(jobs.Created).Query);
    }

    /// <summary>The one blueprint that grants a write: the job must get exactly what its card advertises.</summary>
    [Fact]
    public async Task CreateFromBlueprint_GivesTheBlueprintsOwnGrants()
    {
        var jobs = new RecordingJobService();

        var (_, pending) = await CallAsync(jobs, "create_routine_from_blueprint", new Dictionary<string, object?>
        {
            ["blueprintKey"] = RoutineBlueprintCatalog.MeetingFollowup
        });

        Assert.Contains("create_todo", pending!.Details ?? string.Empty);
        await CreateHandler(jobs).ExecutePendingActionAsync(pending);

        Assert.Equal("create_todo", Assert.Single(Assert.Single(jobs.Created).GrantedTools));
    }

    /// <summary>Rule 1 through the tool: the refusal must reach the model as a result, not as a card offering
    /// to create the wrong routine.</summary>
    [Fact]
    public async Task CreateFromBlueprint_RefusesAnUnknownSlotName()
    {
        var jobs = new RecordingJobService();

        var (result, pending) = await CallAsync(jobs, "create_routine_from_blueprint", new Dictionary<string, object?>
        {
            ["blueprintKey"] = RoutineBlueprintCatalog.TopicDigest,
            ["slots"] = """{"subject":"quantum computing"}"""
        });

        Assert.Null(pending);
        Assert.Contains("subject", Assert.IsType<string>(result));
        Assert.Empty(jobs.Created);
    }

    [Fact]
    public async Task CreateFromBlueprint_RefusesAnUnknownBlueprintKey()
    {
        var jobs = new RecordingJobService();

        var (result, pending) = await CallAsync(jobs, "create_routine_from_blueprint", new Dictionary<string, object?>
        {
            ["blueprintKey"] = "no-such-blueprint"
        });

        Assert.Null(pending);
        Assert.Contains("list_routine_blueprints", Assert.IsType<string>(result));
        Assert.Empty(jobs.Created);
    }

    /// <summary>A model saying "no value" with a JSON null must not write the word <c>null</c> into a prompt
    /// that then fires every morning.</summary>
    [Fact]
    public async Task CreateFromBlueprint_TreatsAJsonNullSlotValueAsUnsupplied()
    {
        var jobs = new RecordingJobService();

        var (_, pending) = await CallAsync(jobs, "create_routine_from_blueprint", new Dictionary<string, object?>
        {
            ["blueprintKey"] = RoutineBlueprintCatalog.TopicDigest,
            ["slots"] = """{"topic":null}"""
        });

        await CreateHandler(jobs).ExecutePendingActionAsync(pending!);

        var query = Assert.Single(jobs.Created).Query;
        Assert.Contains("artificial intelligence", query);
        Assert.DoesNotContain("topic of null", query);
    }

    /// <summary>The unknown-name refusal is about the NAME, so a null value must not smuggle a typo past it.</summary>
    [Fact]
    public async Task CreateFromBlueprint_RefusesAnUnknownSlotNameEvenWhenItsValueIsNull()
    {
        var jobs = new RecordingJobService();

        var (result, pending) = await CallAsync(jobs, "create_routine_from_blueprint", new Dictionary<string, object?>
        {
            ["blueprintKey"] = RoutineBlueprintCatalog.TopicDigest,
            ["slots"] = """{"subject":null}"""
        });

        Assert.Null(pending);
        Assert.Contains("subject", Assert.IsType<string>(result));
        Assert.Empty(jobs.Created);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[\"topic\"]")]
    public async Task CreateFromBlueprint_RefusesSlotsThatAreNotAJsonObject(string slots)
    {
        var jobs = new RecordingJobService();

        var (result, pending) = await CallAsync(jobs, "create_routine_from_blueprint", new Dictionary<string, object?>
        {
            ["blueprintKey"] = RoutineBlueprintCatalog.TopicDigest,
            ["slots"] = slots
        });

        Assert.Null(pending);
        Assert.Contains("JSON object", Assert.IsType<string>(result));
        Assert.Empty(jobs.Created);
    }

    [Fact]
    public async Task CreateFromBlueprint_HonoursTheNameTimeAndDayOverrides()
    {
        var jobs = new RecordingJobService();

        var (_, pending) = await CallAsync(jobs, "create_routine_from_blueprint", new Dictionary<string, object?>
        {
            ["blueprintKey"] = RoutineBlueprintCatalog.WeeklyReview,
            ["name"] = "My Friday look-back",
            ["timeOfDay"] = "16:30",
            ["dayOfWeek"] = "Thursday"
        });

        await CreateHandler(jobs).ExecutePendingActionAsync(pending!);

        var created = Assert.Single(jobs.Created);
        Assert.Equal("My Friday look-back", created.Name);
        Assert.Equal(new TimeOnly(16, 30), created.TimeOfDay);
        Assert.Equal(DayOfWeek.Thursday, created.DayOfWeek);
    }

    /// <summary>The catalog's own title is the fallback, so a create with no name is still a readable row.</summary>
    [Fact]
    public async Task CreateFromBlueprint_WithNoName_UsesTheBlueprintsTitle()
    {
        var jobs = new RecordingJobService();
        var blueprint = RoutineBlueprintCatalog.Find(RoutineBlueprintCatalog.MorningBrief)!;

        var (_, pending) = await CallAsync(jobs, "create_routine_from_blueprint", new Dictionary<string, object?>
        {
            ["blueprintKey"] = RoutineBlueprintCatalog.MorningBrief
        });

        await CreateHandler(jobs).ExecutePendingActionAsync(pending!);

        Assert.Equal(blueprint.TitleKey, Assert.Single(jobs.Created).Name);
    }

    /// <summary>Approving this tool once lets Pia stand up a routine that writes unattended, which is the
    /// caution the tool catalog exists to show.</summary>
    [Fact]
    public void CreateFromBlueprint_CountsAsAuthorityAuthoring()
    {
        Assert.True(ToolPermissionService.IsAuthorityAuthoring("create_routine_from_blueprint"));
    }

    /// <summary>Records what reached <c>CreateAsync</c>, since the blueprint's authority over the grants, the
    /// kind and the effort is a claim about the arguments rather than about the card.</summary>
    private sealed class RecordingJobService
    {
        public IScheduledJobService Service { get; } = Substitute.For<IScheduledJobService>();

        public List<ScheduledJob> Created { get; } = [];

        public RecordingJobService()
        {
            Service.CreateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<RecurrenceType>(), Arg.Any<TimeOnly>(),
                    Arg.Any<DayOfWeek?>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<DateTime?>(), Arg.Any<Guid?>(),
                    Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<ScheduledJobKind>(), Arg.Any<bool>(),
                    Arg.Any<Guid?>(), Arg.Any<ReasoningEffort?>(), Arg.Any<string?>())
                .Returns(ci =>
                {
                    var job = new ScheduledJob
                    {
                        Name = (string)ci[0]!,
                        Query = (string)ci[1]!,
                        Recurrence = (RecurrenceType)ci[2]!,
                        TimeOfDay = (TimeOnly)ci[3]!,
                        DayOfWeek = (DayOfWeek?)ci[4],
                        GrantedTools = ((IReadOnlyCollection<string>?)ci[9])?.ToList() ?? [],
                        Kind = (ScheduledJobKind)ci[10]!,
                        QuietOnSuccess = (bool)ci[11]!,
                        PersonaId = (Guid?)ci[12],
                        ReasoningEffort = (ReasoningEffort?)ci[13],
                        BlueprintKey = (string?)ci[14],
                        NextFireAt = DateTime.Now.AddHours(1),
                    };
                    Created.Add(job);
                    return job;
                });
        }
    }
}
