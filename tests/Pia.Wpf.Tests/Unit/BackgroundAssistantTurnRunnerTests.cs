using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Shared.Models;
using Xunit;

namespace Pia.Wpf.Tests.Unit;

/// <summary>
/// Unit coverage for the headless background-turn tool policy: reads (immediate result) are
/// always allowed, writes (pending action) run only when explicitly granted, and the produced
/// turn is persisted as a user+assistant assistant chat.
/// </summary>
public class BackgroundAssistantTurnRunnerTests
{
    private static AiProvider Provider() => new()
    {
        Id = Guid.NewGuid(),
        Name = "P",
        Endpoint = "https://example",
        TimeoutSeconds = 60,
    };

    private sealed class Harness
    {
        public IAiClientService Ai = Substitute.For<IAiClientService>();
        public IPluginService Plugins = Substitute.For<IPluginService>();
        public IAssistantPromptComposer Composer = Substitute.For<IAssistantPromptComposer>();
        public IPersonaService Personas = Substitute.For<IPersonaService>();
        public IAssistantChatService Chats = Substitute.For<IAssistantChatService>();
        public IChatTitleService Titles = Substitute.For<IChatTitleService>();
        public ISettingsService Settings = Substitute.For<ISettingsService>();

        public List<(string Tool, object? Returned)> HandlerResults = new();
        public SyncAssistantChat? Saved;

        public BackgroundAssistantTurnRunner Build(IReadOnlyList<FunctionCallContent> toolCalls, string answer = "ANSWER")
        {
            Settings.GetSettingsAsync().Returns(new AppSettings()); // TokenizationEnabled defaults off
            Personas.ResolveActiveAsync(Arg.Any<WindowMode>(), Arg.Any<UserOperatingMode>())
                .Returns(new Persona { Name = "Pia", SystemPrompt = "sys" });
            Composer.PrepareTurn(Arg.Any<Persona>(), Arg.Any<AiProvider>(), Arg.Any<IReadOnlyList<AtCommand>>(), Arg.Any<bool>())
                .Returns(new AssistantTurnSetup("system", new List<AITool>(), SupportsTools: true, WebSearchActive: false));
            Titles.GenerateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns((string?)null);
            Chats.SaveAsync(Arg.Do<SyncAssistantChat>(c => Saved = c), Arg.Any<CancellationToken>())
                .Returns(Task.CompletedTask);

            Ai.GetChatCompletionWithToolsAsync(
                    Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                    Arg.Any<Func<FunctionCallContent, Task<object?>>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(ci => Drive(ci.ArgAt<Func<FunctionCallContent, Task<object?>>?>(3), toolCalls, answer));

            ITokenMapService TokenMapFactory() => Substitute.For<ITokenMapService>();

            return new BackgroundAssistantTurnRunner(
                Ai, Plugins, Composer, Personas, Chats, Titles, Settings,
                TokenMapFactory, NullLogger<BackgroundAssistantTurnRunner>.Instance);
        }

        private async IAsyncEnumerable<ChatStreamItem> Drive(
            Func<FunctionCallContent, Task<object?>>? handler,
            IReadOnlyList<FunctionCallContent> toolCalls,
            string answer)
        {
            if (handler is not null)
            {
                foreach (var call in toolCalls)
                {
                    var returned = await handler(call);
                    HandlerResults.Add((call.Name, returned));
                }
            }

            yield return new TextDelta(answer);
            yield return new Finished(null, "test-model");
        }
    }

    private static FunctionCallContent Call(string name) =>
        new(Guid.NewGuid().ToString(), name, new Dictionary<string, object?>());

    private static PluginToolCall Pending(string toolName, Action onExecute) =>
        new(toolName, Guid.NewGuid(), "plugin", "desc", null, () =>
        {
            onExecute();
            return Task.FromResult<object?>("write-done");
        });

    [Fact]
    public async Task ReadTool_IsAllowed_AndResultReturned()
    {
        var h = new Harness();
        // A read tool routes to an immediate result (no pending action).
        h.Plugins.RouteToolCallAsync(Arg.Any<FunctionCallContent>(), Arg.Any<CancellationToken>())
            .Returns(((object?)"read-result", (PluginToolCall?)null));

        var runner = h.Build([Call("search_files")]);
        var result = await runner.RunAsync(new BackgroundTurnRequest { Prompt = "go", Provider = Provider() }, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Single(h.HandlerResults);
        Assert.Equal("read-result", h.HandlerResults[0].Returned);
    }

    [Fact]
    public async Task UngrantedWriteTool_IsDenied_AndNotExecuted()
    {
        var h = new Harness();
        var executed = false;
        h.Plugins.RouteToolCallAsync(Arg.Any<FunctionCallContent>(), Arg.Any<CancellationToken>())
            .Returns(((object?)null, Pending("write_file", () => executed = true)));

        var runner = h.Build([Call("write_file")]);
        // No grants → write denied.
        var result = await runner.RunAsync(new BackgroundTurnRequest { Prompt = "go", Provider = Provider() }, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.False(executed);
        var returned = Assert.IsType<string>(h.HandlerResults[0].Returned);
        Assert.Contains("Denied", returned);
    }

    [Fact]
    public async Task GrantedWriteTool_IsExecuted()
    {
        var h = new Harness();
        var executed = false;
        h.Plugins.RouteToolCallAsync(Arg.Any<FunctionCallContent>(), Arg.Any<CancellationToken>())
            .Returns(((object?)null, Pending("create_object", () => executed = true)));

        var runner = h.Build([Call("create_object")]);
        var result = await runner.RunAsync(new BackgroundTurnRequest
        {
            Prompt = "go",
            Provider = Provider(),
            GrantedWriteTools = ["create_object"],
        }, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.True(executed);
        Assert.Equal("write-done", h.HandlerResults[0].Returned);
    }

    [Fact]
    public async Task GrantCheck_IsCaseInsensitive()
    {
        var h = new Harness();
        var executed = false;
        h.Plugins.RouteToolCallAsync(Arg.Any<FunctionCallContent>(), Arg.Any<CancellationToken>())
            .Returns(((object?)null, Pending("create_object", () => executed = true)));

        var runner = h.Build([Call("create_object")]);
        var result = await runner.RunAsync(new BackgroundTurnRequest
        {
            Prompt = "go",
            Provider = Provider(),
            GrantedWriteTools = ["Create_Object"],
        }, CancellationToken.None);

        Assert.True(executed);
    }

    [Fact]
    public async Task PersistsUserAndAssistantMessages()
    {
        var h = new Harness();
        var provider = Provider();
        var runner = h.Build([], answer: "the answer");

        var result = await runner.RunAsync(new BackgroundTurnRequest
        {
            Prompt = "the prompt",
            Provider = provider,
            Title = "My Job",
        }, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.NotNull(h.Saved);
        Assert.Equal(result.ChatId, h.Saved!.Id);
        Assert.Equal("My Job", h.Saved.Title);
        Assert.Equal(WindowMode.Assistant.ToString(), h.Saved.WindowMode);
        Assert.Equal(provider.Id, h.Saved.ProviderId);
        Assert.Equal(2, h.Saved.Messages.Count);

        var user = h.Saved.Messages[0];
        Assert.Equal("user", user.Role);
        Assert.Equal("the prompt", user.Content);

        var assistant = h.Saved.Messages[1];
        Assert.Equal("assistant", assistant.Role);
        Assert.Equal("the answer", assistant.Content);
    }

    [Fact]
    public async Task EmptyAnswer_ReturnsFailure_AndDoesNotPersist()
    {
        var h = new Harness();
        var runner = h.Build([], answer: "");

        var result = await runner.RunAsync(new BackgroundTurnRequest { Prompt = "go", Provider = Provider() }, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Null(h.Saved);
    }
}
