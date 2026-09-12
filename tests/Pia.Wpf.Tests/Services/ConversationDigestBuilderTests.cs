using System.Net.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Shared.Models;
using Xunit;

namespace Pia.Tests.Services;

public sealed class ConversationDigestBuilderTests
{
    private const string Goal = "the file is not in the working folder";

    private readonly IAiClientService _ai = Substitute.For<IAiClientService>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AiProvider Provider(int? window = 200_000) => new()
    {
        Name = "P",
        Endpoint = "https://x",
        ProviderType = AiProviderType.OpenAI,
        MaxContextWindowTokens = window,
        MaxOutputTokens = window is null ? null : 4096,
    };

    private static SyncAssistantChat Chat(params (string Role, string Content)[] rows) => new()
    {
        Id = Guid.NewGuid(),
        Messages = [.. rows.Select(r => new SyncAssistantChatMessage { Id = Guid.NewGuid(), Role = r.Role, Content = r.Content })],
    };

    private static SyncAssistantChat ConversationThenGoal() => Chat(
        ("user", "rename quarterly.md to q3.md"),
        ("assistant", "Done — it is now q3.md in Reports."),
        ("user", Goal),
        ("assistant", "I could not ground that goal. Which file do you mean?"));

    private Task<ConversationDigestResult> BuildAsync(
        SyncAssistantChat? chat, AgentContextMode mode, AiProvider? provider = null) =>
        ConversationDigestBuilder.BuildAsync(
            chat, Goal, mode, provider ?? Provider(), _ai, NullLogger.Instance, Ct);

    private void SummaryReturns(string? text, UsageDetails? usage = null) =>
        _ai.GetChatResponseAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)) { Usage = usage }));

    [Fact]
    public async Task Off_ProducesNothing()
    {
        var result = await BuildAsync(ConversationThenGoal(), AgentContextMode.Off);

        Assert.Null(result.Text);
        Assert.Null(result.Usage);
    }

    [Fact]
    public async Task AMissingChatRow_ProducesNothingAndNoError()
    {
        var result = await BuildAsync(chat: null, AgentContextMode.Verbatim);

        Assert.Null(result.Text);
    }

    [Fact]
    public async Task Verbatim_CarriesTheTurnsBeforeTheGoal()
    {
        var result = await BuildAsync(ConversationThenGoal(), AgentContextMode.Verbatim);

        Assert.NotNull(result.Text);
        Assert.Contains("rename quarterly.md to q3.md", result.Text);
        Assert.Contains("it is now q3.md in Reports", result.Text);
    }

    /// <summary>The run's own goal row and the clarification it then asked are the run restating itself.</summary>
    [Fact]
    public async Task Verbatim_DropsTheRunsOwnGoalAndItsClarification()
    {
        var result = await BuildAsync(ConversationThenGoal(), AgentContextMode.Verbatim);

        Assert.DoesNotContain(Goal, result.Text);
        Assert.DoesNotContain("Which file do you mean?", result.Text);
    }

    /// <summary>Cut at the LAST match, so a goal the user had typed once before still keeps its own context.</summary>
    [Fact]
    public async Task Verbatim_CutsAtTheRunsOwnCopyOfTheGoal()
    {
        var chat = Chat(
            ("user", Goal),
            ("assistant", "I looked and found nothing."),
            ("user", "try the Reports folder"),
            ("user", Goal));

        var result = await BuildAsync(chat, AgentContextMode.Verbatim);

        Assert.Contains("try the Reports folder", result.Text);
        Assert.Contains("I looked and found nothing.", result.Text);
    }

    [Fact]
    public async Task AChatThatIsOnlyTheGoal_ProducesNothing()
    {
        var result = await BuildAsync(Chat(("user", Goal)), AgentContextMode.Verbatim);

        Assert.Null(result.Text);
    }

    [Fact]
    public async Task Verbatim_NeverSpendsAProviderTurn()
    {
        await BuildAsync(ConversationThenGoal(), AgentContextMode.Verbatim);

        await _ai.DidNotReceive().GetChatResponseAsync(
            Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
            Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AProviderWithNoConfiguredWindow_StillProducesADigest()
    {
        var result = await BuildAsync(ConversationThenGoal(), AgentContextMode.Verbatim, Provider(window: null));

        Assert.Contains("rename quarterly.md to q3.md", result.Text);
    }

    [Fact]
    public async Task ALongChat_IsCappedInCharacters()
    {
        var rows = Enumerable.Range(0, 400)
            .Select(i => (i % 2 == 0 ? "user" : "assistant", new string('x', 200) + i))
            .ToArray();
        var chat = Chat([.. rows, ("user", Goal)]);

        var result = await BuildAsync(chat, AgentContextMode.Verbatim);

        Assert.NotNull(result.Text);
        Assert.True(result.Text.Length < ConversationDigestBuilder.MaxDigestChars + 500,
            $"digest was {result.Text.Length} chars");
        // Truncated from the front: the recent turns are the ones a follow-up refers to.
        Assert.Contains("399", result.Text);
    }

    [Fact]
    public async Task Summary_ReplacesTheExcerptAndReportsItsUsage()
    {
        SummaryReturns("They renamed quarterly.md to q3.md under Reports.",
            new UsageDetails { InputTokenCount = 900, OutputTokenCount = 40 });

        var result = await BuildAsync(ConversationThenGoal(), AgentContextMode.Summary);

        Assert.Contains("They renamed quarterly.md to q3.md under Reports.", result.Text);
        Assert.DoesNotContain("Assistant: Done —", result.Text);
        Assert.Equal(900, result.Usage?.InputTokenCount);
    }

    [Fact]
    public async Task ASummaryThatThrows_FallsBackToVerbatim()
    {
        _ai.GetChatResponseAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("upstream is down"));

        var result = await BuildAsync(ConversationThenGoal(), AgentContextMode.Summary);

        Assert.Contains("rename quarterly.md to q3.md", result.Text);
    }

    /// <summary>The round was still paid for, so its usage must reach the ledger even though the text was useless.</summary>
    [Fact]
    public async Task ASummaryThatComesBackEmpty_FallsBackToVerbatimAndStillReportsTheUsage()
    {
        SummaryReturns(string.Empty, new UsageDetails { InputTokenCount = 900, OutputTokenCount = 0 });

        var result = await BuildAsync(ConversationThenGoal(), AgentContextMode.Summary);

        Assert.Contains("rename quarterly.md to q3.md", result.Text);
        Assert.Equal(900, result.Usage?.InputTokenCount);
    }

    [Fact]
    public async Task Summary_WithNoAiClient_FallsBackToVerbatim()
    {
        var result = await ConversationDigestBuilder.BuildAsync(
            ConversationThenGoal(), Goal, AgentContextMode.Summary, Provider(), ai: null,
            NullLogger.Instance, Ct);

        Assert.Contains("rename quarterly.md to q3.md", result.Text);
        Assert.Null(result.Usage);
    }

    /// <summary>The fast model never sees the raw chat — only the already-capped excerpt.</summary>
    [Fact]
    public async Task Summary_IsGivenTheRenderedExcerpt()
    {
        SummaryReturns("a summary");
        IList<ChatMessage>? sent = null;
        _ai.GetChatResponseAsync(
                Arg.Do<IList<ChatMessage>>(m => sent = m), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "a summary"))));

        await BuildAsync(ConversationThenGoal(), AgentContextMode.Summary);

        Assert.NotNull(sent);
        Assert.Equal(2, sent.Count);
        Assert.Equal(ChatRole.System, sent[0].Role);
        Assert.Contains("rename quarterly.md to q3.md", sent[1].Text);
    }
}
