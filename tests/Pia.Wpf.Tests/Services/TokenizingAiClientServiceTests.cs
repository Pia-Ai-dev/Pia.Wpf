using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>Guards the two directions of the PII skew: what a tool is HANDED, and what goes back to the provider.</summary>
// Shares a collection with TokenizationLatchTests: these tests set the process-wide latch it asserts on.
[Collection("TokenizationLatchStatic")]
public class TokenizingAiClientServiceTests
{
    /// <summary>
    /// A named allowlist of write verbs used to gate this, and no file tool was ever on it — so a placeholder
    /// the model copied out of a read_file result and into write_file content landed on disk verbatim, in a
    /// user-visible deliverable. Every tool's string arguments are detokenized now; the two here are the read
    /// tool and the retired verb that were previously excluded.
    /// </summary>
    [Theory]
    [InlineData("write_file")]
    [InlineData("delete_file")]
    [InlineData("recall")]
    [InlineData("totally_unknown")]
    public async Task EveryToolsStringArguments_ReachTheHandlerDetokenized(string toolName)
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
        tokenMap.Detokenize(Arg.Any<string>()).Returns(ci => ((string)ci[0]).Replace("[Phone_9]", "2026/03/27"));

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
                return Task.FromResult<object?>("ok");
            };

            await foreach (var _ in sut.GetChatCompletionWithToolsAsync(
                new List<ChatMessage> { new(ChatRole.User, "hi") },
                new AiProvider { Name = "t", Endpoint = "http://localhost", ProviderType = AiProviderType.OpenAI },
                tools: null, toolHandler: handler, cancellationToken: TestContext.Current.CancellationToken))
            {
            }

            await capturedHandler!(
                new FunctionCallContent("c1", toolName, new Dictionary<string, object?> { ["content"] = "due [Phone_9]" }),
                new ToolDispatchContext(1));

            Assert.Equal("due 2026/03/27", seenByHandler);
        }
        finally
        {
            TokenMapAmbient.Current = null;
        }
    }

    /// <summary>
    /// The mirror image, and the reason the run's placeholders were not uniform: an assistant reply is
    /// DETOKENIZED on the way out, so a User-only tokenize pass sent the restored values straight back to the
    /// provider on the next step.
    /// </summary>
    [Fact]
    public async Task AnAssistantReplyGoesBackOut_AsItsToken_NotAsTheRealValue()
    {
        IList<ChatMessage>? seenByInner = null;
        var inner = Substitute.For<IAiClientService>();
        inner.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(), cancellationToken: Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                seenByInner = ci.ArgAt<IList<ChatMessage>>(0);
                return Stream(new Finished(null, "gpt-5"));
            });

        var tokenMap = Substitute.For<ITokenMapService>();
        tokenMap.TokenizeStructuredResult(Arg.Any<string>())
            .Returns(ci => ((string)ci[0]).Replace("2026/03/27", "[Phone_9]"));
        tokenMap.Detokenize(Arg.Any<string>()).Returns(ci => (string)ci[0]);

        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings());
        var serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider.GetService(typeof(IServiceScopeFactory)).Returns(Substitute.For<IServiceScopeFactory>());

        var sut = new TokenizingAiClientService(
            inner, serviceProvider, settings, NullLogger<TokenizingAiClientService>.Instance);

        var carriedResult = new FunctionResultContent("c1", "raw 2026/03/27 from the file");
        var request = new List<ChatMessage>
        {
            new(ChatRole.System, "sys"),
            new(ChatRole.User, "the date is 2026/03/27"),
            new(ChatRole.Assistant, "I read 2026/03/27 out of the file"),
            new(ChatRole.Tool, [carriedResult]),
        };

        TokenMapAmbient.Current = tokenMap;
        try
        {
            await foreach (var _ in sut.GetChatCompletionWithToolsAsync(
                request,
                new AiProvider { Name = "t", Endpoint = "http://localhost", ProviderType = AiProviderType.OpenAI },
                cancellationToken: TestContext.Current.CancellationToken))
            {
            }
        }
        finally
        {
            TokenMapAmbient.Current = null;
        }

        Assert.NotNull(seenByInner);
        Assert.Equal("sys", seenByInner![0].Text);
        Assert.Equal("the date is [Phone_9]", seenByInner[1].Text);
        Assert.Equal(ChatRole.Assistant, seenByInner[2].Role);
        Assert.Equal("I read [Phone_9] out of the file", seenByInner[2].Text);

        // A carried tool result was tokenized where it was produced; a second pass here would be double work
        // and, on a lossy map, a corruption.
        Assert.Same(carriedResult, seenByInner[3].Contents[0]);
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

    /// <summary>A dropped <c>Stop</c> would leave the tool loop running past a park for tokenization-enabled
    /// installs only, and no tokenization-off test could see it.</summary>
    [Fact]
    public async Task RelaysTheStopSignalToTheInnerHandler()
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
            ToolDispatchContext? seen = null;
            ToolCallHandler handler = (_, ctx) =>
            {
                seen = ctx;
                return Task.FromResult<object?>("done");
            };

            await foreach (var _ in sut.GetChatCompletionWithToolsAsync(
                new List<ChatMessage> { new(ChatRole.User, "hi") },
                new AiProvider { Name = "t", Endpoint = "http://localhost", ProviderType = AiProviderType.OpenAI },
                tools: null, toolHandler: handler, cancellationToken: TestContext.Current.CancellationToken))
            {
            }

            var signal = new ToolLoopStopSignal();
            Assert.NotNull(capturedHandler);
            await capturedHandler!(
                new FunctionCallContent("id", "write_file", new Dictionary<string, object?>()),
                new ToolDispatchContext(7, signal));

            Assert.NotNull(seen);
            Assert.Equal(7, seen!.Value.Round);
            // The SAME instance, so a stop raised inside the gate is what the loop reads back.
            Assert.Same(signal, seen.Value.Stop);
            Assert.False(signal.IsStopRequested);
            seen.Value.Stop!.RequestStop();
            Assert.True(signal.IsStopRequested);
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
