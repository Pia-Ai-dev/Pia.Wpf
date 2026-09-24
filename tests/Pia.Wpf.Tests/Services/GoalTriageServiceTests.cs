using System.Net.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>Answering a real multi-step goal in one chat turn is the expensive direction to be wrong in, so
/// every reply that is not exactly the downgrade token has to land on NeedsPlan.</summary>
public sealed class GoalTriageServiceTests
{
    private readonly IAiClientService _ai = Substitute.For<IAiClientService>();

    private static AiProvider Provider() => new() { Name = "P", Endpoint = "https://x", ProviderType = AiProviderType.OpenAI };
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private GoalTriageService Triage() => new(_ai, NullLogger<GoalTriageService>.Instance);

    private void Replies(string? text) =>
        _ai.GetChatResponseAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text ?? string.Empty))));

    [Theory]
    [InlineData("ANSWER")]
    [InlineData("answer")]
    [InlineData("  ANSWER  ")]
    public async Task TheDowngradeToken_DowngradesTheTurn(string reply)
    {
        Replies(reply);

        Assert.Equal(GoalTriageVerdict.AnswerDirectly,
            await Triage().ClassifyAsync("what is this about?", Provider(), Ct));
    }

    [Theory]
    [InlineData("PLAN")]
    [InlineData("ANSWER — but first read the file")]
    [InlineData("I think this can be answered directly.")]
    [InlineData("")]
    [InlineData(null)]
    public async Task AnythingElse_Plans(string? reply)
    {
        Replies(reply);

        Assert.Equal(GoalTriageVerdict.NeedsPlan,
            await Triage().ClassifyAsync("build me a comparison table", Provider(), Ct));
    }

    [Fact]
    public async Task AProviderFault_Plans()
    {
        _ai.GetChatResponseAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns<Task<ChatResponse>>(_ => throw new HttpRequestException("down"));

        Assert.Equal(GoalTriageVerdict.NeedsPlan, await Triage().ClassifyAsync("do the thing", Provider(), Ct));
    }

    [Fact]
    public async Task AnEmptyGoal_PlansWithoutSpendingATurn()
    {
        Assert.Equal(GoalTriageVerdict.NeedsPlan, await Triage().ClassifyAsync("   ", Provider(), Ct));

        await _ai.DidNotReceiveWithAnyArgs().GetChatResponseAsync(
            default!, default!, default, default, default, default, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TheUsersOwnCancel_Propagates()
    {
#pragma warning disable xUnit1051 // the cancellation being asserted is the caller's own, not the test host's
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync(); // the CT under test IS this one, so Ct would defeat the assertion
        _ai.GetChatResponseAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns<Task<ChatResponse>>(ci => throw new OperationCanceledException(ci.ArgAt<CancellationToken>(6)));

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => Triage().ClassifyAsync("do the thing", Provider(), cts.Token));
#pragma warning restore xUnit1051
    }

    [Fact]
    public async Task TheGoalRidesTheUserRole_NeverTheSystemPrompt()
    {
        List<ChatMessage>? sent = null;
        _ai.GetChatResponseAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                sent = ci.ArgAt<IList<ChatMessage>>(0).ToList();
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "PLAN")));
            });

        await Triage().ClassifyAsync("my secret goal", Provider(), Ct);

        Assert.NotNull(sent);
        Assert.DoesNotContain("my secret goal", sent![0].Text);
        Assert.Equal(ChatRole.User, sent[^1].Role);
        Assert.Contains("my secret goal", sent[^1].Text);
    }

    [Fact]
    public async Task ItRoutesAsSpineTraffic_NotAsThePersonasOwnTurn()
    {
        string? mode = null, modelType = null;
        IList<AITool>? tools = null;
        _ai.GetChatResponseAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                tools = ci.ArgAt<IList<AITool>?>(2);
                mode = ci.ArgAt<string?>(3);
                modelType = ci.ArgAt<string?>(5);
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "PLAN")));
            });

        await Triage().ClassifyAsync("do the thing", Provider(), Ct);

        Assert.Null(tools); // a classification has nothing to call
        Assert.Equal("Assistant", mode);
        Assert.Equal("fast", modelType);
    }
}
