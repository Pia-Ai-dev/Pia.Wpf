using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Shared.Models;
using Xunit;

namespace Pia.Tests.Services;

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

        /// <summary>All-false by default, which is the no-standing-grant surface every other fact here assumes.</summary>
        public IToolPermissionService Permissions = Substitute.For<IToolPermissionService>();

        public IAssistantPromptComposer Composer = Substitute.For<IAssistantPromptComposer>();
        public IPersonaService Personas = Substitute.For<IPersonaService>();
        public IAssistantChatService Chats = Substitute.For<IAssistantChatService>();
        public IChatTitleService Titles = Substitute.For<IChatTitleService>();
        public ISettingsService Settings = Substitute.For<ISettingsService>();
        public IAgentRunService Runs = Substitute.For<IAgentRunService>();

        public List<(string Tool, object? Returned)> HandlerResults = new();
        public SyncAssistantChat? Saved;
        public readonly List<SyncAssistantChat> AllSaved = new();

        public BackgroundAssistantTurnRunner Build(IReadOnlyList<FunctionCallContent> toolCalls, string answer = "ANSWER")
        {
            Settings.GetSettingsAsync().Returns(new AppSettings()); // TokenizationEnabled defaults off
            Personas.ResolveActiveAsync(Arg.Any<WindowMode>(), Arg.Any<UserOperatingMode>())
                .Returns(new Persona { Name = "Pia", SystemPrompt = "sys" });
            Composer.PrepareTurn(Arg.Any<Persona>(), Arg.Any<AiProvider>(), Arg.Any<IReadOnlyList<AtCommand>>(), Arg.Any<bool>())
                .Returns(new AssistantTurnSetup("system", new List<AITool>(), SupportsTools: true, WebSearchActive: false));
            Titles.GenerateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns((string?)null);
            Chats.SaveAsync(Arg.Do<SyncAssistantChat>(c => { Saved = c; AllSaved.Add(c); }), Arg.Any<CancellationToken>())
                .Returns(Task.CompletedTask);

            Runs.CreateAsync(Arg.Any<AgentRunCreateRequest>(), Arg.Any<CancellationToken>())
                .Returns(ci => Task.FromResult(new AgentRun
                {
                    Id = Guid.NewGuid(),
                    ChatId = ci.Arg<AgentRunCreateRequest>().ChatId,
                    RunShape = RunShape.SingleTurn,
                    State = AgentRunState.Running,
                }));

            Ai.GetChatCompletionWithToolsAsync(
                    Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                    Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(), cancellationToken: Arg.Any<CancellationToken>())
                .Returns(ci => Drive(ci.ArgAt<ToolCallHandler?>(3), toolCalls, answer));

            ITokenMapService TokenMapFactory() => Substitute.For<ITokenMapService>();

            return new BackgroundAssistantTurnRunner(
                Ai, Plugins, Permissions, Composer, Personas, Chats, Titles, Settings,
                TokenMapFactory, Runs, new ExecutingRunStore(),
                NullLogger<BackgroundAssistantTurnRunner>.Instance);
        }

        private async IAsyncEnumerable<ChatStreamItem> Drive(
            ToolCallHandler? handler,
            IReadOnlyList<FunctionCallContent> toolCalls,
            string answer)
        {
            if (handler is not null)
            {
                foreach (var call in toolCalls)
                {
                    var returned = await handler(call, new ToolDispatchContext(1));
                    HandlerResults.Add((call.Name, returned));
                }
            }

            yield return new TextDelta(answer);
            yield return new Finished(null, "test-model");
        }
    }

    private static FunctionCallContent Call(string name) =>
        new(Guid.NewGuid().ToString(), name, new Dictionary<string, object?>());

    /// <summary>a pending action whose PLUGIN name is real, so ToolClassifier yields a real class.</summary>
    private static PluginToolCall Pending(string toolName, string pluginName, Action onExecute) =>
        new(toolName, Guid.NewGuid(), pluginName, "desc", null, () =>
        {
            onExecute();
            return Task.FromResult<object?>("write-done");
        });

    private static PluginToolCall Pending(string toolName, Action onExecute) =>
        new(toolName, Guid.NewGuid(), "plugin", "desc", null, () =>
        {
            onExecute();
            return Task.FromResult<object?>("write-done");
        });

    /// <summary>a pending action with a STABLE owner, since a standing grant is keyed on (PluginId, ToolName).</summary>
    private static PluginToolCall Pending(string toolName, string pluginName, Guid pluginId, Action onExecute) =>
        new(toolName, pluginId, pluginName, "desc", null, () =>
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
    public async Task McpTool_Ungranted_IsGrantGated_AndRouted()
    {
        // Phase-2 gate: MCP no longer has a pre-route deny. The MCP handler returns a deferred pending
        // action, so an ungranted MCP call is denied by the grant gate — and IS routed (unlike before).
        var h = new Harness();
        var executed = false;
        h.Plugins.RouteToolCallAsync(Arg.Any<FunctionCallContent>(), Arg.Any<CancellationToken>())
            .Returns(((object?)null, Pending("mcp_search", () => executed = true)));

        var runner = h.Build([Call("mcp_search")]);
        var result = await runner.RunAsync(new BackgroundTurnRequest { Prompt = "go", Provider = Provider() }, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.False(executed);
        var returned = Assert.IsType<string>(h.HandlerResults[0].Returned);
        Assert.Contains("Denied", returned);
        await h.Plugins.Received().RouteToolCallAsync(Arg.Any<FunctionCallContent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task McpTool_Granted_IsExecuted()
    {
        // A scheduled/detached run that explicitly grants an MCP tool name runs it (default-deny otherwise).
        var h = new Harness();
        var executed = false;
        h.Plugins.RouteToolCallAsync(Arg.Any<FunctionCallContent>(), Arg.Any<CancellationToken>())
            .Returns(((object?)null, Pending("mcp_search", () => executed = true)));

        var runner = h.Build([Call("mcp_search")]);
        var result = await runner.RunAsync(new BackgroundTurnRequest
        {
            Prompt = "go",
            Provider = Provider(),
            GrantedWriteTools = ["mcp_search"],
        }, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.True(executed);
    }

    /// <summary>The sharpest consequence of the owner's decision, at the unattended gate: a job whose grant
    /// list names an MCP delete now runs it with nobody looking.</summary>
    [Fact]
    public async Task GrantedDestructiveMcpTool_Executes_BecauseItsGrantListNamesIt()
    {
        var h = new Harness();
        var executed = false;
        h.Plugins.RouteToolCallAsync(Arg.Any<FunctionCallContent>(), Arg.Any<CancellationToken>())
            .Returns(((object?)null, Pending("purge_records", () => executed = true)));
        h.Plugins.IsMcpTool("purge_records").Returns(true);

        var runner = h.Build([Call("purge_records")]);
        var result = await runner.RunAsync(new BackgroundTurnRequest
        {
            Prompt = "go",
            Provider = Provider(),
            GrantedWriteTools = ["purge_records"],
        }, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.True(executed);
    }

    /// <summary>The same across the whole stem family — and ungranted, every one of them is still denied.</summary>
    [Theory]
    [InlineData("remove_page")]
    [InlineData("drop_table")]
    [InlineData("wipe_index")]
    [InlineData("erase_all")]
    [InlineData("destroy_env")]
    [InlineData("truncate_log")]
    [InlineData("delete_issue")]
    public async Task DestructiveMcpTool_RunsWhenNamed_AndIsDeniedWhenNot(string toolName)
    {
        async Task<bool> RunAsync(bool granted)
        {
            var h = new Harness();
            var executed = false;
            h.Plugins.RouteToolCallAsync(Arg.Any<FunctionCallContent>(), Arg.Any<CancellationToken>())
                .Returns(((object?)null, Pending(toolName, () => executed = true)));
            h.Plugins.IsMcpTool(toolName).Returns(true);

            var runner = h.Build([Call(toolName)]);
            await runner.RunAsync(new BackgroundTurnRequest
            {
                Prompt = "go",
                Provider = Provider(),
                GrantedWriteTools = granted ? [toolName] : [],
            }, CancellationToken.None);

            return executed;
        }

        Assert.True(await RunAsync(granted: true));
        Assert.False(await RunAsync(granted: false));
    }

    /// <summary>IsDeleteLike("delete_file") is true, but an explicit grant for it is the user's own auditable
    /// decision and nothing above the named-grant arm withdraws it.</summary>
    [Fact]
    public async Task GrantedBuiltInDeleteFile_StillExecutes()
    {
        var h = new Harness();
        var executed = false;
        h.Plugins.RouteToolCallAsync(Arg.Any<FunctionCallContent>(), Arg.Any<CancellationToken>())
            .Returns(((object?)null, Pending("delete_file", () => executed = true)));
        h.Plugins.IsMcpTool("delete_file").Returns(false);

        var runner = h.Build([Call("delete_file")]);
        var result = await runner.RunAsync(new BackgroundTurnRequest
        {
            Prompt = "go",
            Provider = Provider(),
            GrantedWriteTools = ["delete_file"],
        }, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.True(executed);
        Assert.Equal("write-done", h.HandlerResults[0].Returned);
    }

    [Fact]
    public async Task GrantedNonDestructiveMcpTool_StillExecutes()
    {
        // Being external is not delete-like by itself: a granted MCP tool that isn't a delete keeps
        // running unattended.
        var h = new Harness();
        var executed = false;
        h.Plugins.RouteToolCallAsync(Arg.Any<FunctionCallContent>(), Arg.Any<CancellationToken>())
            .Returns(((object?)null, Pending("create_issue", () => executed = true)));
        h.Plugins.IsMcpTool("create_issue").Returns(true);

        var runner = h.Build([Call("create_issue")]);
        await runner.RunAsync(new BackgroundTurnRequest
        {
            Prompt = "go",
            Provider = Provider(),
            GrantedWriteTools = ["create_issue"],
        }, CancellationToken.None);

        Assert.True(executed);
    }

    /// <summary>Degrade path: MCP-ness is re-derived at the gate, and a fault answers "external". The named
    /// grant still runs the tool — class only narrows the park and the session tier — and the turn survives.
    /// </summary>
    [Fact]
    public async Task DeleteLikeTool_WhenMcpDerivationThrows_TheTurnSurvives()
    {
        var h = new Harness();
        var executed = false;
        h.Plugins.RouteToolCallAsync(Arg.Any<FunctionCallContent>(), Arg.Any<CancellationToken>())
            .Returns(((object?)null, Pending("delete_file", () => executed = true)));
        h.Plugins.IsMcpTool(Arg.Any<string>()).Returns(_ => throw new InvalidOperationException("routes locked"));

        var runner = h.Build([Call("delete_file")]);
        var result = await runner.RunAsync(new BackgroundTurnRequest
        {
            Prompt = "go",
            Provider = Provider(),
            GrantedWriteTools = ["delete_file"],
        }, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.True(executed);
    }

    /// <summary>The fault is recorded as the class it fails closed to: the audited row reads External, not the
    /// plugin's own class, the ungranted call is still denied, and the exchange survives instead of throwing.
    /// </summary>
    [Fact]
    public async Task UngrantedTool_WhenMcpDerivationThrows_IsAuditedExternal_AndStillDenied()
    {
        var timeline = new Pia.Tests.Services.RecordingTimelineService();
        var h = new Harness();
        var executed = false;
        h.Plugins.RouteToolCallAsync(Arg.Any<FunctionCallContent>(), Arg.Any<CancellationToken>())
            .Returns(((object?)null, Pending("write_file", () => executed = true)));
        h.Plugins.IsMcpTool(Arg.Any<string>()).Returns(_ => throw new InvalidOperationException("routes locked"));

        var runner = h.Build([Call("write_file")]);
        await runner.RunExchangeAsync(
            [new ChatMessage(ChatRole.User, "go")], Provider(),
            new AssistantTurnSetup("system", new List<AITool>(), SupportsTools: true, WebSearchActive: false),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            TestContext.Current.CancellationToken,
            timeline: new Pia.Services.Interfaces.AgentTimelineScope(timeline, Guid.NewGuid(), Guid.NewGuid()));

        // Without the fault this plugin name classifies as Unknown, so the row discriminates the fail-closed
        // answer from the honest one.
        var row = Assert.Single(timeline.Rows);
        Assert.Equal(ToolClass.External, row.ToolClass);
        Assert.Equal(ToolGateDecision.DeniedNotGranted, row.Decision);
        Assert.False(executed);
        Assert.Contains("Denied", Assert.IsType<string>(h.HandlerResults[0].Returned));
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

    // ---- the per-run autonomy policy at the unattended gate -------------------------------------
    // Driven through RunExchangeAsync, which is where the policy arrives (HeadlessTurnExecutor relays it from
    // Initialize, and the SingleTurn RunAsync path above passes none — that is why every fact above still
    // holds unchanged).

    private static async Task<object?> RunOneGatedCallAsync(
        Harness h, PluginToolCall pending, string[] grants, RunAutonomyPolicy? policy)
    {
        h.Plugins.RouteToolCallAsync(Arg.Any<FunctionCallContent>(), Arg.Any<CancellationToken>())
            .Returns(((object?)null, pending));

        var runner = h.Build([Call(pending.ToolName)]);
        await runner.RunExchangeAsync(
            [new ChatMessage(ChatRole.User, "go")],
            Provider(),
            new AssistantTurnSetup("system", new List<AITool>(), SupportsTools: true, WebSearchActive: false),
            new HashSet<string>(grants, StringComparer.OrdinalIgnoreCase),
            CancellationToken.None,
            policy: policy);

        return h.HandlerResults[0].Returned;
    }

    [Fact]
    public async Task PolicyCoveredClass_ExecutesWithoutANamedGrant()
    {
        var h = new Harness();
        var executed = false;
        var returned = await RunOneGatedCallAsync(
            h, Pending("create_todo", "todo", () => executed = true),
            grants: [], policy: new RunAutonomyPolicy([ToolClass.Todo]));

        Assert.True(executed);
        Assert.Equal("write-done", returned);
    }

    [Fact]
    public async Task PolicyCoveredClass_DoesNotCoverItsDeleteLikeSibling()
    {
        // ToolClass.Files holds write_file AND delete_file. A policy over Files must not hand an
        // unattended run card-free delete_file — the policy arm's own !isDeleteLike exclusion is what stops it.
        var h = new Harness();
        var executed = false;
        var returned = await RunOneGatedCallAsync(
            h, Pending("delete_file", "files", () => executed = true),
            grants: [], policy: new RunAutonomyPolicy([ToolClass.Files]));

        Assert.False(executed);
        Assert.Equal(
            "Denied: 'delete_file' is a write action not granted to this background job. Do not retry.",
            Assert.IsType<string>(returned));
    }

    [Fact]
    public async Task PolicyCoveredClass_StillExecutesItsNonDeleteSibling()
    {
        var h = new Harness();
        var executed = false;
        await RunOneGatedCallAsync(
            h, Pending("write_file", "files", () => executed = true),
            grants: [], policy: new RunAutonomyPolicy([ToolClass.Files]));

        Assert.True(executed);
    }

    /// <summary>The policy is a CLASS switch, never a delete grant — so an ungranted destructive external
    /// tool is refused even under a policy naming every class.</summary>
    [Fact]
    public async Task PolicyOverEveryClass_StillCannotRunADestructiveExternalTool()
    {
        var h = new Harness();
        var executed = false;
        h.Plugins.IsMcpTool("delete_issue").Returns(true);

        var returned = await RunOneGatedCallAsync(
            h, Pending("delete_issue", "linear", () => executed = true),
            grants: [],
            policy: new RunAutonomyPolicy(Enum.GetValues<ToolClass>()));

        Assert.False(executed);
        Assert.Equal(
            "Denied: 'delete_issue' is a write action not granted to this background job. Do not retry.",
            Assert.IsType<string>(returned));
    }

    [Fact]
    public async Task PolicyDoesNotCoverTheExternalClass_SoAnMcpWriteStillNeedsAName()
    {
        // D9's exclusion, at the gate: a class grant must never cover server-defined tools, or an MCP
        // server's NEXT tool addition would be auto-approved retroactively.
        var h = new Harness();
        var executed = false;
        h.Plugins.IsMcpTool("create_issue").Returns(true);

        var returned = await RunOneGatedCallAsync(
            h, Pending("create_issue", "linear", () => executed = true),
            grants: [], policy: RunAutonomyPolicy.FromSettings(
                new AppSettings { AgentRunAutoApproveBuiltInWrites = true }));

        Assert.False(executed);
        Assert.Contains("not granted", Assert.IsType<string>(returned));
    }

    [Theory]
    [InlineData("write_file", "files", false, false)]
    [InlineData("write_file", "files", true, true)]
    [InlineData("create_object", "memory", false, false)]
    [InlineData("create_object", "memory", true, true)]
    public async Task NullPolicy_LeavesTheGrantGateExactlyAsItWas(
        string toolName, string pluginName, bool granted, bool expectExecuted)
    {
        var h = new Harness();
        var executed = false;
        await RunOneGatedCallAsync(
            h, Pending(toolName, pluginName, () => executed = true),
            grants: granted ? [toolName] : [], policy: null);

        Assert.Equal(expectExecuted, executed);
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
    public async Task EmptyAnswer_ReturnsFailure_ButPersistsStubChat()
    {
        // Even the empty path leaves a stub AssistantChats row up front so the run's FK
        // target (and thus a Failed run's ChatId) resolves. No assistant/user messages are written.
        var h = new Harness();
        var runner = h.Build([], answer: "");

        var result = await runner.RunAsync(new BackgroundTurnRequest { Prompt = "go", Provider = Provider() }, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.NotNull(h.Saved);
        Assert.Equal(result.ChatId, h.Saved!.Id);
        Assert.Empty(h.Saved.Messages);
        // Exactly one save: the stub (the full 2-message chat is never reached on the empty path).
        Assert.Single(h.AllSaved);
        // The empty path marks the run Failed so a resolvable (stub) chat still carries a Failed run.
        await h.Runs.Received().FailAsync(Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    // ---- the unattended gate records its decisions ----

    [Fact]
    public async Task UnattendedDecisionsAreRecorded()
    {
        var timeline = new Pia.Tests.Services.RecordingTimelineService();
        var runId = Guid.NewGuid();
        var stepId = Guid.NewGuid();
        var h = new Harness();

        var pendings = new Dictionary<string, PluginToolCall>(StringComparer.Ordinal)
        {
            // Granted by name → runs
            ["write_file"] = Pending("write_file", "files", () => { }),
            // Not granted → denied
            ["update_todo"] = Pending("update_todo", "todo", () => { }),
            // Destructive, external and UNGRANTED → denied, and the park will not ask about it either
            ["mcp_delete_thing"] = Pending("mcp_delete_thing", "some-mcp-server", () => { }),
        };
        h.Plugins.RouteToolCallAsync(Arg.Any<FunctionCallContent>(), Arg.Any<CancellationToken>())
            .Returns(ci => ((object?)null, (PluginToolCall?)pendings[ci.ArgAt<FunctionCallContent>(0).Name]));
        h.Plugins.IsMcpTool("mcp_delete_thing").Returns(true);

        var runner = h.Build([Call("write_file"), Call("update_todo"), Call("mcp_delete_thing")]);
        await runner.RunExchangeAsync(
            [new ChatMessage(ChatRole.User, "go")], Provider(),
            new AssistantTurnSetup("system", new List<AITool>(), SupportsTools: true, WebSearchActive: false),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "write_file" },
            TestContext.Current.CancellationToken,
            timeline: new Pia.Services.Interfaces.AgentTimelineScope(timeline, runId, stepId));

        var rows = timeline.Rows;
        Assert.Equal(3, rows.Count);
        Assert.All(rows, r =>
        {
            Assert.Equal(ToolGateSurface.Unattended, r.Surface);
            Assert.Equal(runId, r.RunId);
            Assert.Equal(stepId, r.StepId);
        });

        Assert.Equal(ToolGateDecision.GrantedByName, rows[0].Decision);
        Assert.Equal(AgentTimelineOutcome.Ok, rows[0].Outcome);
        Assert.Equal("write-done".Length, rows[0].ResultChars);

        Assert.Equal(ToolGateDecision.DeniedNotGranted, rows[1].Decision);
        Assert.Equal(AgentTimelineOutcome.NotExecuted, rows[1].Outcome);

        Assert.Equal(ToolGateDecision.DeniedNotGranted, rows[2].Decision);
        Assert.Equal(AgentTimelineOutcome.NotExecuted, rows[2].Outcome);
        Assert.Equal(ToolClass.External, rows[2].ToolClass);
    }

    [Fact]
    public async Task NoScope_MeansNoRows_AndTheGateStillRuns()
    {
        // Control for the fact above (which proves the same code path DOES record). A null scope is what the
        // SingleTurn background path passes, and it must make every emit arm a no-op WITHOUT changing the
        // gate's answer — so the granted tool having run is asserted, not assumed. Without it "no rows" would
        // also be true of a turn where nothing happened at all.
        var timeline = new Pia.Tests.Services.RecordingTimelineService();
        var executed = false;
        var h = new Harness();
        h.Plugins.RouteToolCallAsync(Arg.Any<FunctionCallContent>(), Arg.Any<CancellationToken>())
            .Returns(((object?)null, (PluginToolCall?)Pending("write_file", "files", () => executed = true)));

        var runner = h.Build([Call("write_file")]);
        await runner.RunExchangeAsync(
            [new ChatMessage(ChatRole.User, "go")], Provider(),
            new AssistantTurnSetup("system", new List<AITool>(), SupportsTools: true, WebSearchActive: false),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "write_file" },
            TestContext.Current.CancellationToken,
            timeline: null);

        Assert.True(executed, "the granted write must still run with no audit scope");
        Assert.Empty(timeline.Rows);
    }

    /// <summary>
    /// The gate-resolution arm, and the asymmetry against its interactive twin: there is NO allowlist step here.
    /// write_file is auto-approve-eligible interactively, and unattended that buys it nothing — only a named
    /// grant does. A 1 in ToolAutonomyRuleTests' allowlist column for this file would make four tools free on
    /// every scheduled job, which is what this pins from the behavioural side.
    /// </summary>
    [Fact]
    public async Task AnAllowlistEligibleTool_IsStillDeniedUnattended_WithoutANamedGrant()
    {
        var timeline = new Pia.Tests.Services.RecordingTimelineService();
        var executed = false;
        var h = new Harness();
        h.Plugins.RouteToolCallAsync(Arg.Any<FunctionCallContent>(), Arg.Any<CancellationToken>())
            .Returns(((object?)null, (PluginToolCall?)Pending("write_file", "files", () => executed = true)));

        var runner = h.Build([Call("write_file")]);
        await runner.RunExchangeAsync(
            [new ChatMessage(ChatRole.User, "go")], Provider(),
            new AssistantTurnSetup("system", new List<AITool>(), SupportsTools: true, WebSearchActive: false),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase), // no named grant
            TestContext.Current.CancellationToken,
            timeline: new Pia.Services.Interfaces.AgentTimelineScope(timeline, Guid.NewGuid(), Guid.NewGuid()));

        Assert.False(executed);
        var row = Assert.Single(timeline.Rows);
        Assert.Equal(ToolGateDecision.DeniedNotGranted, row.Decision);
        // The class still came from the classifier, so the refusal is recorded against a real class.
        Assert.Equal(ToolClass.Files, row.ToolClass);
    }

    /// <summary>
    /// The dispatch arm carries the gate's OWN pair of instants onto the row. Every answered arm gets both —
    /// only the approval park leaves DecidedAt null, and there is no park in this turn.
    /// </summary>
    [Fact]
    public async Task TheDispatchArmStampsBothInstantsFromTheGate()
    {
        var timeline = new Pia.Tests.Services.RecordingTimelineService();
        var h = new Harness();
        h.Plugins.RouteToolCallAsync(Arg.Any<FunctionCallContent>(), Arg.Any<CancellationToken>())
            .Returns(((object?)null, (PluginToolCall?)Pending("write_file", "files", () => { })));

        var before = DateTime.UtcNow;
        var runner = h.Build([Call("write_file")]);
        await runner.RunExchangeAsync(
            [new ChatMessage(ChatRole.User, "go")], Provider(),
            new AssistantTurnSetup("system", new List<AITool>(), SupportsTools: true, WebSearchActive: false),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "write_file" },
            TestContext.Current.CancellationToken,
            timeline: new Pia.Services.Interfaces.AgentTimelineScope(timeline, Guid.NewGuid(), Guid.NewGuid()));

        var row = Assert.Single(timeline.Rows);
        Assert.Equal(ToolGateDecision.GrantedByName, row.Decision);
        Assert.NotNull(row.RequestedAt);
        Assert.NotNull(row.DecidedAt);
        // Both were taken around Resolve, so neither may predate the turn.
        Assert.True(row.RequestedAt >= before);
        Assert.True(row.DecidedAt >= row.RequestedAt);
    }

    // ---- the standing "Always" tier reaches this surface too ----

    /// <summary>
    /// The scheduled-job shape: a SingleTurn request with no grant list, no policy and no approval store, so the
    /// standing tier is the only thing that can authorize the call. delete_file makes the fact load-bearing —
    /// the autonomy policy excludes it and the park will not even offer to ask about it, so before this the
    /// "Always" the user ticked in Settings bought a job nothing at all.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StandingGrant_AuthorizesAnUngrantedDelete_OnTheSingleTurnPath(bool granted)
    {
        var h = new Harness();
        var pluginId = Guid.NewGuid();
        var executed = false;
        h.Plugins.RouteToolCallAsync(Arg.Any<FunctionCallContent>(), Arg.Any<CancellationToken>())
            .Returns(((object?)null, Pending("delete_file", "files", pluginId, () => executed = true)));
        h.Permissions.IsGranted(pluginId, "delete_file").Returns(granted);

        var runner = h.Build([Call("delete_file")]);
        var result = await runner.RunAsync(
            new BackgroundTurnRequest { Prompt = "go", Provider = Provider() }, CancellationToken.None);

        Assert.True(result.Succeeded);
        // The false case is the guard against an input that is accidentally always true: same call, same turn.
        Assert.Equal(granted, executed);
    }

    /// <summary>Keyed on the OWNER as well as the name, so a grant on another plugin's same-named tool is not this
    /// tool's grant.</summary>
    [Fact]
    public async Task StandingGrant_ForADifferentOwner_DoesNotAuthorizeTheCall()
    {
        var h = new Harness();
        var executed = false;
        var callOwner = Guid.NewGuid();
        var someOtherPlugin = Guid.NewGuid();
        h.Plugins.RouteToolCallAsync(Arg.Any<FunctionCallContent>(), Arg.Any<CancellationToken>())
            .Returns(((object?)null, Pending("write_file", "files", callOwner, () => executed = true)));
        h.Permissions.IsGranted(someOtherPlugin, "write_file").Returns(true);

        var runner = h.Build([Call("write_file")]);
        await runner.RunAsync(
            new BackgroundTurnRequest { Prompt = "go", Provider = Provider() }, CancellationToken.None);

        Assert.False(executed);
    }

    /// <summary>Audited as the standing tier, not folded into the run's grant list: the panel's reason line is how a
    /// user finds out which authority ran a tool nobody was asked about.</summary>
    [Fact]
    public async Task StandingGrant_IsRecordedAsItsOwnDecision()
    {
        var timeline = new Pia.Tests.Services.RecordingTimelineService();
        var h = new Harness();
        var pluginId = Guid.NewGuid();
        h.Plugins.RouteToolCallAsync(Arg.Any<FunctionCallContent>(), Arg.Any<CancellationToken>())
            .Returns(((object?)null, Pending("write_file", "files", pluginId, () => { })));
        h.Permissions.IsGranted(pluginId, "write_file").Returns(true);

        var runner = h.Build([Call("write_file")]);
        await runner.RunExchangeAsync(
            [new ChatMessage(ChatRole.User, "go")], Provider(),
            new AssistantTurnSetup("system", new List<AITool>(), SupportsTools: true, WebSearchActive: false),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase), // no named grant
            TestContext.Current.CancellationToken,
            timeline: new Pia.Services.Interfaces.AgentTimelineScope(timeline, Guid.NewGuid(), Guid.NewGuid()));

        var row = Assert.Single(timeline.Rows);
        Assert.Equal(ToolGateDecision.AutoApprovedStandingGrant, row.Decision);
        Assert.Equal(AgentTimelineOutcome.Ok, row.Outcome);
    }
}
