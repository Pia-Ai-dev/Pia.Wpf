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
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(), cancellationToken: Arg.Any<CancellationToken>())
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
                new AiProvider { Name = "t", Endpoint = "http://localhost", ProviderType = AiProviderType.OpenAI }, cancellationToken: TestContext.Current.CancellationToken))
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

    [Fact]
    public async Task GetChatCompletionWithTools_ObjectToolResult_IsSerializedAndTokenized()
    {
        // Regression: WrapToolHandler used to tokenize only STRING tool results, so an object result
        // (recall's RecallResult, a read_topic body, a raw read_source transcript) was JSON-serialized
        // downstream with REAL PII. It must now serialize the object to its wire JSON and tokenize it.
        ToolCallHandler? capturedHandler = null;
        var inner = Substitute.For<IAiClientService>();
        inner.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(), cancellationToken: Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                capturedHandler = ci.ArgAt<ToolCallHandler?>(3);
                return Stream(new Finished(null, "gpt-5"));
            });

        var tokenMap = Substitute.For<ITokenMapService>();
        tokenMap.TokenizeStructuredResult(Arg.Any<string>())
            .Returns(ci => ((string)ci[0]).Replace("John Smith", "[Person_1]"));
        tokenMap.Detokenize(Arg.Any<string>()).Returns(ci => (string)ci[0]);

        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings());
        var serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider.GetService(typeof(IServiceScopeFactory)).Returns(Substitute.For<IServiceScopeFactory>());

        var sut = new TokenizingAiClientService(
            inner, serviceProvider, settings, NullLogger<TokenizingAiClientService>.Instance);

        TokenMapAmbient.Current = tokenMap;
        try
        {
            // T2-14: the decorator must RELAY the dispatch context, not re-create it. It sits between the tool
            // loop and the real gate for every tokenization-enabled user, so a `handler(toolCall, default)`
            // would persist Round = 0 on exactly those installs — and would be invisible to any test that
            // leaves tokenization off, which is why this fact lives in the ENABLED test rather than its own.
            ToolDispatchContext? seenByInnerHandler = null;
            ToolCallHandler objectResultHandler = (_, ctx) =>
            {
                seenByInnerHandler = ctx;
                return Task.FromResult<object?>(new { Name = "John Smith", Note = "topic hit" });
            };

            await foreach (var _ in sut.GetChatCompletionWithToolsAsync(
                new List<ChatMessage> { new(ChatRole.User, "hi") },
                new AiProvider { Name = "t", Endpoint = "http://localhost", ProviderType = AiProviderType.OpenAI },
                tools: null,
                toolHandler: objectResultHandler, cancellationToken: TestContext.Current.CancellationToken))
            {
            }

            Assert.NotNull(capturedHandler);
            // A round the DEFAULT would not produce: `default(ToolDispatchContext).Round` is 0, so a decorator
            // that dropped the context reads back 0 here rather than 7.
            var toolResult = await capturedHandler!(
                new FunctionCallContent("id", "recall", new Dictionary<string, object?>()), new ToolDispatchContext(7));

            // The object was serialized to JSON and tokenized — a string, PII masked.
            var str = Assert.IsType<string>(toolResult);
            Assert.Contains("[Person_1]", str);
            Assert.DoesNotContain("John Smith", str);

            Assert.NotNull(seenByInnerHandler);
            Assert.Equal(7, seenByInnerHandler!.Value.Round);
        }
        finally
        {
            TokenMapAmbient.Current = null;
        }
    }

    [Theory]
    [InlineData(true)]   // tokenization ON  — the wrapping branch
    [InlineData(false)]  // tokenization OFF — the pass-through branch
    public async Task RelaysTheContextBudgetToTheInnerClient(bool tokenizationEnabled)
    {
        // The decorator is registered AS IAiClientService, so if it dropped the budget the in-step tool
        // loop would never compact in production even though every unit test of the adapter passed.
        // Both branches must relay it.
        AgentContextBudget? seenByInner = null;
        var inner = Substitute.For<IAiClientService>();
        inner.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(), cancellationToken: Arg.Any<CancellationToken>(),
                contextBudget: Arg.Any<AgentContextBudget?>())
            .Returns(ci =>
            {
                seenByInner = ci.ArgAt<AgentContextBudget?>(7);
                return Stream(new Finished(null, "gpt-5"));
            });

        var tokenMap = Substitute.For<ITokenMapService>();
        tokenMap.TokenizeStructuredResult(Arg.Any<string>()).Returns(ci => (string)ci[0]);
        tokenMap.Detokenize(Arg.Any<string>()).Returns(ci => (string)ci[0]);

        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings
        {
            Privacy = new PrivacySettings { TokenizationEnabled = tokenizationEnabled },
        });
        var serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider.GetService(typeof(IServiceScopeFactory)).Returns(Substitute.For<IServiceScopeFactory>());

        var sut = new TokenizingAiClientService(
            inner, serviceProvider, settings, NullLogger<TokenizingAiClientService>.Instance);

        TokenMapAmbient.Current = tokenMap;
        try
        {
            await foreach (var _ in sut.GetChatCompletionWithToolsAsync(
                new List<ChatMessage> { new(ChatRole.User, "hi") },
                new AiProvider { Name = "t", Endpoint = "http://localhost", ProviderType = AiProviderType.OpenAI },
                cancellationToken: TestContext.Current.CancellationToken,
                contextBudget: new AgentContextBudget(128_000, 4_096)))
            {
            }

            Assert.Equal(new AgentContextBudget(128_000, 4_096), seenByInner);
        }
        finally
        {
            TokenMapAmbient.Current = null;
        }
    }

    [Fact]
    public async Task WithoutAContextBudget_TheInnerClientSeesNull()
    {
        // An unconfigured provider (every provider after upgrade) and every interactive/background
        // caller must reach the inner client with a null budget — i.e. today's behaviour exactly.
        var observed = new List<AgentContextBudget?>();
        var inner = Substitute.For<IAiClientService>();
        inner.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(), cancellationToken: Arg.Any<CancellationToken>(),
                contextBudget: Arg.Any<AgentContextBudget?>())
            .Returns(ci =>
            {
                observed.Add(ci.ArgAt<AgentContextBudget?>(7));
                return Stream(new Finished(null, "gpt-5"));
            });

        var tokenMap = Substitute.For<ITokenMapService>();
        tokenMap.TokenizeStructuredResult(Arg.Any<string>()).Returns(ci => (string)ci[0]);
        tokenMap.Detokenize(Arg.Any<string>()).Returns(ci => (string)ci[0]);
        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings());
        var serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider.GetService(typeof(IServiceScopeFactory)).Returns(Substitute.For<IServiceScopeFactory>());

        var sut = new TokenizingAiClientService(
            inner, serviceProvider, settings, NullLogger<TokenizingAiClientService>.Instance);

        TokenMapAmbient.Current = tokenMap;
        try
        {
            await foreach (var _ in sut.GetChatCompletionWithToolsAsync(
                new List<ChatMessage> { new(ChatRole.User, "hi") },
                new AiProvider { Name = "t", Endpoint = "http://localhost", ProviderType = AiProviderType.OpenAI },
                cancellationToken: TestContext.Current.CancellationToken))
            {
            }

            Assert.Single(observed);
            Assert.Null(observed[0]);
        }
        finally
        {
            TokenMapAmbient.Current = null;
        }
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
