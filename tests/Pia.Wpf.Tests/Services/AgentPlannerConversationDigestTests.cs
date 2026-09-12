using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Services.Providers;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// The digest reaches the plan and re-plan turns on the USER message. The system prompt is the failure this
/// locks out: TokenizeMessages rewrites ChatRole.User text only, so a transcript in the system prompt ships
/// past the tokenizer verbatim.
/// </summary>
public sealed class AgentPlannerConversationDigestTests
{
    private readonly IAiClientService _ai = Substitute.For<IAiClientService>();
    private readonly ISettingsService _settingsService = Substitute.For<ISettingsService>();
    private readonly AppSettings _settings = new();

    private const string Goal = "the file is not in the working folder";
    private const string Digest = "--- Earlier in this conversation ---\nUser: rename quarterly.md\n--- end ---";

    public AgentPlannerConversationDigestTests() =>
        _settingsService.GetSettingsAsync().Returns(_ => Task.FromResult(_settings));

    private static AiProvider Provider() => new()
    {
        Name = "P", Endpoint = "https://x", ProviderType = AiProviderType.OpenAI, SupportsToolCalling = true,
    };

    private static Persona Persona() => new() { Id = Guid.NewGuid(), Name = "Pia", SystemPrompt = "you are Pia" };

    private static RunContext Ctx(string? digest) =>
        new(Goal, RunProfile.Interactive) { ConversationDigest = digest };

    private AgentPlanner Planner()
    {
        var handler = Substitute.For<IAiProviderHandler>();
        handler.ProviderType.Returns(AiProviderType.OpenAI);
        handler.DropsReasoningEffortWithTools.Returns(false);
        return new AgentPlanner(_ai, new AiProviderHandlerResolver([handler]), _settingsService,
            NullLogger<AgentPlanner>.Instance);
    }

    private readonly List<string> _systemPrompts = [];
    private readonly List<string> _userPrompts = [];

    private string LastSystemPrompt => _systemPrompts[^1];
    private string LastUserPrompt => _userPrompts[^1];

    private static async IAsyncEnumerable<ChatStreamItem> PlanStream(
        ToolCallHandler? handler, Dictionary<string, object?> emitArgs)
    {
        if (handler is not null)
            await handler(new FunctionCallContent(Guid.NewGuid().ToString(), "emit_plan", emitArgs),
                new ToolDispatchContext(1));
        await Task.Yield();
        yield return new Finished(null, "test-model");
    }

    private static Dictionary<string, object?> OneStep() => new()
    {
        ["steps"] = new object[]
        {
            new Dictionary<string, object?>
            {
                ["title"] = "do it", ["intent"] = "get it done", ["expectedArtifact"] = null,
                ["personaKey"] = null, ["parallelGroup"] = null,
            },
        },
    };

    private void ReturnsPlan()
    {
        _ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<string?>(),
                cancellationToken: Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var messages = ci.ArgAt<IList<ChatMessage>>(0);
                _systemPrompts.Add(messages[0].Text ?? string.Empty);
                _userPrompts.Add(messages[1].Text ?? string.Empty);
                return PlanStream(ci.ArgAt<ToolCallHandler?>(3), OneStep());
            });
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheDigestRidesTheUserMessage_NeverTheSystemPrompt()
    {
        ReturnsPlan();

        await Planner().PlanAsync(Goal, Ctx(Digest), Persona(), Provider(), Ct);

        Assert.Contains(Digest, LastUserPrompt);
        Assert.DoesNotContain("rename quarterly.md", LastSystemPrompt);
        Assert.Single(_userPrompts);
    }

    [Fact]
    public async Task TheGoalStillComesFirst()
    {
        ReturnsPlan();

        await Planner().PlanAsync(Goal, Ctx(Digest), Persona(), Provider(), Ct);

        Assert.True(LastUserPrompt.IndexOf(Goal, StringComparison.Ordinal)
            < LastUserPrompt.IndexOf(Digest, StringComparison.Ordinal));
    }

    [Fact]
    public async Task NoRecordedMode_LeavesThePromptExactlyAsItWas()
    {
        ReturnsPlan();

        await Planner().PlanAsync(Goal, Ctx(digest: null), Persona(), Provider(), Ct);

        Assert.DoesNotContain("Earlier in this conversation", LastUserPrompt);
        Assert.DoesNotContain("Earlier in this conversation", LastSystemPrompt);
    }

    [Fact]
    public async Task ARePlanCarriesItToo()
    {
        ReturnsPlan();

        await Planner().ReplanAsync(Ctx(Digest), "the step failed", Persona(), Provider(), Ct);

        Assert.Contains(Digest, LastUserPrompt);
        Assert.DoesNotContain("rename quarterly.md", LastSystemPrompt);
    }

    [Fact]
    public async Task ARePlanWithoutOne_LeavesThePromptExactlyAsItWas()
    {
        ReturnsPlan();

        await Planner().ReplanAsync(Ctx(digest: null), "the step failed", Persona(), Provider(), Ct);

        Assert.DoesNotContain("Earlier in this conversation", LastUserPrompt);
    }
}
