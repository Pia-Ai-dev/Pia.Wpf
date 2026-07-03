using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// Guards the WriteOperations allow-list that gates argument detokenization before a
/// write reaches the vault. The memory tool rename (Phase 3) replaced the retired
/// create_object/update_object/append_to_list/delete_object verbs with remember/forget;
/// recall is read-only and must NOT be treated as a write.
/// </summary>
public class TokenizingAiClientServiceTests
{
    [Theory]
    [InlineData("remember")]
    [InlineData("forget")]
    [InlineData("create_reminder")]
    [InlineData("delete_todo")]
    public void IsWriteOperation_WriteVerbs_ReturnTrue(string toolName)
    {
        Assert.True(TokenizingAiClientService.IsWriteOperation(toolName));
    }

    [Theory]
    [InlineData("recall")]            // read-only search, must not detokenize
    [InlineData("create_object")]     // retired
    [InlineData("update_object")]     // retired
    [InlineData("append_to_list")]    // retired
    [InlineData("delete_object")]     // retired
    [InlineData("totally_unknown")]
    public void IsWriteOperation_ReadOnlyOrRetiredVerbs_ReturnFalse(string toolName)
    {
        Assert.False(TokenizingAiClientService.IsWriteOperation(toolName));
    }

    [Fact]
    public async Task GetChatCompletionWithTools_WithTokenizationEnabled_ForwardsReasoningDelta()
    {
        // Regression: the tokenization-enabled relay used to handle only TextDelta/Finished,
        // silently dropping ReasoningDelta — so reasoning never reached ThinkingContent for
        // users with PII tokenization on. It must now pass reasoning through (detokenized).
        var inner = Substitute.For<IAiClientService>();
        inner.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<Func<FunctionCallContent, Task<object?>>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(_ => Stream(
                new ReasoningDelta("thinking out loud"),
                new TextDelta("the answer"),
                new Finished(null, "gpt-5")));

        var tokenMap = Substitute.For<ITokenMapService>();
        tokenMap.TokenizeStructuredResult(Arg.Any<string>()).Returns(ci => (string)ci[0]);
        tokenMap.Detokenize(Arg.Any<string>()).Returns(ci => (string)ci[0]);

        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings()); // Privacy.TokenizationEnabled defaults true

        var serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider.GetService(typeof(IServiceScopeFactory)).Returns(Substitute.For<IServiceScopeFactory>());

        var sut = new TokenizingAiClientService(
            inner, serviceProvider, settings, NullLogger<TokenizingAiClientService>.Instance);

        var items = new List<ChatStreamItem>();
        TokenMapAmbient.Current = tokenMap; // bypass the DI scope; decorator uses the ambient map
        try
        {
            await foreach (var item in sut.GetChatCompletionWithToolsAsync(
                new List<ChatMessage> { new(ChatRole.User, "hi") },
                new AiProvider { Name = "t", Endpoint = "http://localhost", ProviderType = AiProviderType.OpenAI }))
            {
                items.Add(item);
            }
        }
        finally
        {
            TokenMapAmbient.Current = null;
        }

        Assert.Contains("thinking out loud", items.OfType<ReasoningDelta>().Select(r => r.Text));
        Assert.Contains(items.OfType<TextDelta>(), t => t.Text.Contains("the answer"));
    }

    private static async IAsyncEnumerable<ChatStreamItem> Stream(params ChatStreamItem[] items)
    {
        foreach (var item in items)
        {
            yield return item;
            await Task.Yield();
        }
    }
}
