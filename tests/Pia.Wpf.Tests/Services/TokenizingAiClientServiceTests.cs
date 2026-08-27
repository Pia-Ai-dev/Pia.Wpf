using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>Guards the write allow-list that gates argument detokenization; recall is read-only and must NOT count as a write.</summary>
// Shares a collection with TokenizationLatchTests: these tests set the process-wide latch it asserts on.
[Collection("TokenizationLatchStatic")]
public class TokenizingAiClientServiceTests
{
    [Theory]
    [InlineData("remember")]
    [InlineData("forget")]
    [InlineData("create_reminder")]
    [InlineData("delete_todo")]
    // Its slot values become a prompt that outlives the token map, so the card and the stored job would
    // otherwise disagree about what the routine actually says.
    [InlineData("create_routine_from_blueprint")]
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
        // The relay once handled only TextDelta/Finished, so reasoning never reached ThinkingContent.
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
        // WrapToolHandler once tokenized only STRING results, so an object result went downstream with real PII.
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
            // The decorator must RELAY the dispatch context; re-creating it would persist Round = 0 for
            // tokenization-enabled installs only, which no tokenization-off test can see.
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
            // 7 is a round the default would not produce — a dropped context reads back 0.
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
        // The decorator is registered AS IAiClientService, so dropping the budget would stop compaction in production.
        AgentContextBudget? seenByInner = null;
        var inner = Substitute.For<IAiClientService>();
        inner.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(), cancellationToken: Arg.Any<CancellationToken>(),
                contextBudget: Arg.Any<AgentContextBudget?>())
            .Returns(ci =>
            {
                seenByInner = ci.ArgAt<AgentContextBudget?>(8);
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
        var observed = new List<AgentContextBudget?>();
        var inner = Substitute.For<IAiClientService>();
        inner.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(), cancellationToken: Arg.Any<CancellationToken>(),
                contextBudget: Arg.Any<AgentContextBudget?>())
            .Returns(ci =>
            {
                observed.Add(ci.ArgAt<AgentContextBudget?>(8));
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

    /// <summary>A carried tool exchange is what the NEXT step is built from, so it must cross the decorator
    /// unchanged — and unflushed text must not overtake it either.</summary>
    [Fact]
    public async Task ToolRoundExchange_CrossesTheDecoratorUntouched()
    {
        var carried = new List<ChatMessage>
        {
            new(ChatRole.Assistant, [new FunctionCallContent("c1", "read_file", new Dictionary<string, object?> { ["path"] = "notes.md" })]),
            new(ChatRole.Tool, [new FunctionResultContent("c1", "the date is [Phone_9]")]),
        };
        var sent = new ToolRoundExchange(1, carried);

        var inner = Substitute.For<IAiClientService>();
        inner.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(), cancellationToken: Arg.Any<CancellationToken>())
            .Returns(_ => Stream(new TextDelta("prea"), sent, new TextDelta("mble"), new Finished(null, "gpt-5")));

        var tokenMap = Substitute.For<ITokenMapService>();
        // Detokenize would restore the placeholder; the point of this test is that nothing calls it here.
        tokenMap.TokenizeStructuredResult(Arg.Any<string>()).Returns(ci => (string)ci[0]);
        tokenMap.Detokenize(Arg.Any<string>()).Returns(ci => ((string)ci[0]).Replace("[Phone_9]", "2026-03-27"));

        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings());
        var serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider.GetService(typeof(IServiceScopeFactory)).Returns(Substitute.For<IServiceScopeFactory>());

        var sut = new TokenizingAiClientService(
            inner, serviceProvider, settings, NullLogger<TokenizingAiClientService>.Instance);

        var items = new List<ChatStreamItem>();
        TokenMapAmbient.Current = tokenMap;
        try
        {
            await foreach (var item in sut.GetChatCompletionWithToolsAsync(
                new List<ChatMessage> { new(ChatRole.User, "hi") },
                new AiProvider { Name = "t", Endpoint = "http://localhost", ProviderType = AiProviderType.OpenAI },
                cancellationToken: TestContext.Current.CancellationToken))
            {
                items.Add(item);
            }
        }
        finally
        {
            TokenMapAmbient.Current = null;
        }

        var relayed = Assert.Single(items.OfType<ToolRoundExchange>());
        Assert.Same(sent, relayed);
        Assert.Equal("the date is [Phone_9]",
            relayed.Messages[1].Contents.OfType<FunctionResultContent>().Single().Result);
    }

    /// <summary>The handler needs the real value to write it to disk; the message the loop keeps must not get it,
    /// or the next round — and every later step, once exchanges are carried — sends real PII back to the provider.</summary>
    [Fact]
    public async Task DetokenizedArguments_ReachTheHandler_ButNotTheCallTheLoopKeeps()
    {
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
        tokenMap.TokenizeStructuredResult(Arg.Any<string>()).Returns(ci => (string)ci[0]);
        tokenMap.Detokenize(Arg.Any<string>()).Returns(ci => ((string)ci[0]).Replace("[Phone_9]", "2026-03-27"));

        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings());
        var serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider.GetService(typeof(IServiceScopeFactory)).Returns(Substitute.For<IServiceScopeFactory>());

        var sut = new TokenizingAiClientService(
            inner, serviceProvider, settings, NullLogger<TokenizingAiClientService>.Instance);

        TokenMapAmbient.Current = tokenMap;
        try
        {
            string? seenByHandler = null;
            ToolCallHandler handler = (call, _) =>
            {
                seenByHandler = call.Arguments!["content"] as string;
                return Task.FromResult<object?>("written");
            };

            await foreach (var _ in sut.GetChatCompletionWithToolsAsync(
                new List<ChatMessage> { new(ChatRole.User, "hi") },
                new AiProvider { Name = "t", Endpoint = "http://localhost", ProviderType = AiProviderType.OpenAI },
                tools: null, toolHandler: handler, cancellationToken: TestContext.Current.CancellationToken))
            {
            }

            var loopsCall = new FunctionCallContent("c1", "remember",
                new Dictionary<string, object?> { ["content"] = "the date is [Phone_9]" });
            await capturedHandler!(loopsCall, new ToolDispatchContext(1));

            Assert.Equal("the date is 2026-03-27", seenByHandler);
            Assert.Equal("the date is [Phone_9]", loopsCall.Arguments!["content"]);
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
