using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// An empty stream used to parse into a draft whose every field was blank, so "Draft with AI" applied
/// nothing and reported nothing — the button read as dead. It has to retry once, then fail out loud.
/// </summary>
public sealed class RoutineDraftFailureTests
{
    [Fact]
    public async Task An_empty_response_is_retried_once_and_then_fails()
    {
        var harness = Build(Empty);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Service.GenerateRoutineDraftAsync("summarise my inbox every morning", []));

        Assert.Equal(2, harness.Calls());
    }

    [Fact]
    public async Task A_draft_that_arrives_on_the_retry_is_kept()
    {
        var attempts = 0;
        var harness = Build(() => ++attempts == 1
            ? Empty()
            : Stream("{\"name\":\"Inbox digest\",\"goal\":\"Summarise the inbox.\",\"recurrence\":\"daily\"}"));

        var draft = await harness.Service.GenerateRoutineDraftAsync("summarise my inbox", []);

        Assert.Equal("Inbox digest", draft.Name);
        Assert.Equal(RecurrenceType.Daily, draft.Recurrence);
    }

    [Fact]
    public async Task Prose_the_model_wrapped_around_no_json_still_becomes_the_goal()
    {
        var harness = Build(() => Stream("Check the inbox every morning and report what needs an answer."));

        var draft = await harness.Service.GenerateRoutineDraftAsync("summarise my inbox", []);

        Assert.Equal("Check the inbox every morning and report what needs an answer.", draft.Goal);
        Assert.Equal(1, harness.Calls());
    }

    private static async IAsyncEnumerable<ChatStreamItem> Empty()
    {
        await Task.CompletedTask;
        yield break;
    }

    private static async IAsyncEnumerable<ChatStreamItem> Stream(string text)
    {
        await Task.CompletedTask;
        yield return new TextDelta(text);
    }

    private sealed record Harness(TextOptimizationService Service, Func<int> Calls);

    private static Harness Build(Func<IAsyncEnumerable<ChatStreamItem>> respond)
    {
        var calls = 0;
        var providers = Substitute.For<IProviderService>();
        providers.GetDefaultProviderForModeAsync(WindowMode.Assistant)
            .Returns(Task.FromResult<AiProvider?>(new AiProvider
            {
                Name = "Test",
                ProviderType = AiProviderType.OpenAI,
                Endpoint = "https://example.invalid/v1",
                ModelName = "test-model",
            }));

        var client = Substitute.For<IAiClientService>();
        client.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<Microsoft.Extensions.AI.ChatMessage>>(), Arg.Any<AiProvider>(),
                Arg.Any<IList<Microsoft.Extensions.AI.AITool>?>(), Arg.Any<ToolCallHandler?>(),
                Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(),
                Arg.Any<AgentContextBudget?>())
            .Returns(_ =>
            {
                calls++;
                return respond();
            });

        var service = new TextOptimizationService(
            Substitute.For<ITemplateService>(), providers,
            Substitute.For<IHistoryService>(), client);

        return new Harness(service, () => calls);
    }
}
