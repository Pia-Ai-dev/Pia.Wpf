using System.IO;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.ViewModels.Models;
using Xunit;

namespace Pia.Tests.ViewModels;

/// <summary>
/// Batch 06 D4 at the <see cref="ChatSession"/> seam: <c>RunStepTurnAsync</c> turns
/// <c>StepTurnSpec.WorkspaceRoot</c> into the ambient <c>TaskContext</c> the file tools read, and it applies
/// the ONE-NARROWING rule (B6) while doing it. The step turn is driven directly here — no executor, no
/// orchestrator — so this class discriminates <see cref="ChatSession"/>'s own two arguments from
/// <c>LiveTurnExecutor.BuildSpec</c>'s (covered by <see cref="LiveTurnExecutorPlannedRunTests"/>): dropping
/// either one breaks the same end-to-end write, so there is one fact per call site.
/// <para>
/// The write goes through a REAL <see cref="FilesToolHandler"/>, invoked from inside the AI stream — i.e.
/// inside the step's logical async flow, which is the only place the ambient is set. Asserting on where the
/// bytes landed is the point: the ambient itself is also read, because the double-narrow neutralization is
/// otherwise INVISIBLE in the file location (<c>ResolveEffectiveRoot</c> falls back to the base root when a
/// subpath does not exist, so a wrongly-passed subpath would still land the file in the workspace root).
/// </para>
/// </summary>
public sealed class ChatSessionWorkspaceIsolationTests : IDisposable
{
    private readonly IAiClientService _ai = Substitute.For<IAiClientService>();
    private readonly IPluginService _plugins = Substitute.For<IPluginService>();
    private readonly IActionCardBuilder _cards = Substitute.For<IActionCardBuilder>();
    private readonly ILocalizationService _loc = Substitute.For<ILocalizationService>();
    private readonly ITokenMapService _tokenMap = Substitute.For<ITokenMapService>();
    private readonly IToolPermissionService _permissions = Substitute.For<IToolPermissionService>();

    private readonly string _dir;
    private readonly string _filesFolder;
    private readonly string _workingSub;
    private readonly string _workspace;
    private readonly FilesToolHandler _files;

    public ChatSessionWorkspaceIsolationTests()
    {
        _loc[Arg.Any<string>()].Returns(ci => (string)ci[0]);
        _loc.Format(Arg.Any<string>(), Arg.Any<object[]>()).Returns(ci => (string)ci[0]);

        _dir = Path.Combine(Path.GetTempPath(), "pia-step-isolation-" + Guid.NewGuid().ToString("N"));
        _filesFolder = Path.Combine(_dir, "files");
        _workingSub = Path.Combine(_filesFolder, "sub");
        // The workspace stands in for what RunWorkspaceService provisions FROM <filesFolder>\sub: a separate
        // directory whose contents ARE that subfolder's, which is why narrowing it again would be wrong.
        _workspace = Path.Combine(_dir, "workspace");
        Directory.CreateDirectory(_workingSub);
        Directory.CreateDirectory(_workspace);

        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings { AssistantFilesFolder = _filesFolder });
        _files = new FilesToolHandler(settings, new FileStalenessStore(), NullLogger<FilesToolHandler>.Instance);
    }

    public void Dispose()
    {
        TaskAmbient.Current = null;
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private ChatSession CreateSession()
    {
        var session = new ChatSession(
            _tokenMap, _ai, _plugins, _cards, _permissions, _loc, NullLogger.Instance, _ => false);
        session.SetWorkingDirectory("sub");
        session.Messages.Add(new AssistantMessage(ChatRole.User, "goal"));
        return session;
    }

    private static StepTurnSpec Spec(string? workspaceRoot) => new(
        RunId: Guid.NewGuid(),
        Ordinal: 0,
        Intent: "write the file",
        ExpectedArtifact: "a.md",
        SystemPrompt: "system",
        Persona: new PersonaAttribution(Guid.NewGuid(), "Pia", "🤖"),
        Provider: new AiProvider { Name = "Test", Endpoint = "http://localhost", ProviderType = AiProviderType.OpenAI },
        Tools: null,
        SupportsTools: false,
        WebSearchActive: false,
        TokenizationEnabled: false,
        WorkspaceRoot: workspaceRoot);

    /// <summary>Records the ambient the file tools would read, then performs a real write through them.</summary>
    private readonly List<TaskContext?> _seenAmbients = [];

    private void ReturnsAWriteThenText()
    {
        _ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(), cancellationToken: Arg.Any<CancellationToken>())
            .Returns(ci => WriteThenText((CancellationToken)ci[6]));
    }

    private async IAsyncEnumerable<ChatStreamItem> WriteThenText([EnumeratorCancellation] CancellationToken ct)
    {
        _seenAmbients.Add(TaskAmbient.Current);

        var call = new FunctionCallContent("w1", "write_file",
            new Dictionary<string, object?> { ["path"] = "a.md", ["content"] = "hello" });
        var (_, pending) = await _files.HandleToolCallAsync(call, ct);
        if (pending is not null)
            await pending.Execute();

        yield return new TextDelta("wrote it");
        yield return new Finished(null, "m");
    }

    /// <summary>
    /// REGRESSION for <see cref="ChatSession"/>'s two per-step ambient arguments. Drop the
    /// <c>spec.WorkspaceRoot</c> argument and the file lands in the assistant folder instead (red on the path
    /// assertion); pass <c>WorkingDirectory</c> through as the subpath and the ambient carries it (red on the
    /// null assertion). One fact, both call-site mutations discriminated.
    /// </summary>
    [Fact]
    public async Task StepTurn_WithAWorkspaceRoot_WritesIntoTheWorkspace_AndNarrowsOnlyOnce()
    {
        ReturnsAWriteThenText();
        var session = CreateSession();

        var result = await session.RunStepTurnAsync(
            Spec(_workspace), new RunContext("goal", RunProfile.Interactive), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);

        var ambient = Assert.Single(_seenAmbients);
        Assert.NotNull(ambient);
        Assert.Equal(_workspace, ambient!.Value.WorkspaceRoot);
        Assert.Null(ambient.Value.WorkingSubpath); // B6: the workspace root already IS the narrowed root

        Assert.True(File.Exists(Path.Combine(_workspace, "a.md")));
        Assert.False(File.Exists(Path.Combine(_workingSub, "a.md")));
        Assert.False(File.Exists(Path.Combine(_workspace, "sub", "a.md")));

        // The chip the step built points INTO the workspace — which is exactly why opening one has to resolve
        // through RunWorkspaceRedirects once the work is promoted out (plan D8).
        var assistant = session.Messages.Last(m => !m.IsUser);
        Assert.Single(assistant.FileRefs, r => r.AbsolutePath == Path.Combine(_workspace, "a.md"));
    }

    /// <summary>
    /// GUARD: a step with NO workspace root is the pre-Batch-06 shape and must still narrow by the chat's
    /// working directory. It is what keeps the ternary a ternary rather than an unconditional null.
    /// </summary>
    [Fact]
    public async Task StepTurn_WithoutAWorkspaceRoot_StillNarrowsByTheChatWorkingDirectory()
    {
        ReturnsAWriteThenText();
        var session = CreateSession();

        var result = await session.RunStepTurnAsync(
            Spec(workspaceRoot: null), new RunContext("goal", RunProfile.Interactive), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);

        var ambient = Assert.Single(_seenAmbients);
        Assert.NotNull(ambient);
        Assert.Null(ambient!.Value.WorkspaceRoot);
        Assert.Equal("sub", ambient.Value.WorkingSubpath);

        Assert.True(File.Exists(Path.Combine(_workingSub, "a.md")));
        Assert.False(File.Exists(Path.Combine(_workspace, "a.md")));
    }

    /// <summary>
    /// GUARD, and the "no interactive regression" pin (Batch 06 §11): the ORDINARY turn is a separate
    /// <c>TaskContext</c> construction that was deliberately left alone, so a plain chat still writes straight
    /// into the assistant files folder even while isolation ships.
    /// </summary>
    [Fact]
    public async Task AnOrdinaryChatTurn_StillWritesToTheAssistantFolder()
    {
        ReturnsAWriteThenText();
        var session = CreateSession();

        var user = new AssistantMessage(ChatRole.User, "write a.md");
        var assistant = new AssistantMessage(ChatRole.Assistant) { IsStreaming = true };
        session.Messages.Add(user);
        session.Messages.Add(assistant);
        session.BeginTurn();

        await session.RunTurnAsync(new ChatTurnRequest
        {
            UserMessage = user,
            AssistantMessage = assistant,
            Provider = new AiProvider { Name = "Test", Endpoint = "http://localhost", ProviderType = AiProviderType.OpenAI },
            TurnSetup = new AssistantTurnSetup("system", null, SupportsTools: false, WebSearchActive: false),
            AtCommands = [],
            TokenizationEnabled = false,
        }, TestContext.Current.CancellationToken);

        var ambient = Assert.Single(_seenAmbients);
        Assert.NotNull(ambient);
        Assert.Null(ambient!.Value.WorkspaceRoot);
        Assert.Equal("sub", ambient.Value.WorkingSubpath);
        Assert.True(File.Exists(Path.Combine(_workingSub, "a.md")));
    }

    /// <summary>
    /// GUARD (plan R20's precedent): every existing <c>StepTurnSpec</c> construction — the ordinary
    /// interactive path and every test that builds one with named arguments — still means "no isolation".
    /// </summary>
    [Fact]
    public void StepTurnSpec_WorkspaceRoot_DefaultsToNull()
    {
        var spec = new StepTurnSpec(
            RunId: Guid.NewGuid(),
            Ordinal: 0,
            Intent: "i",
            ExpectedArtifact: null,
            SystemPrompt: "s",
            Persona: new PersonaAttribution(Guid.NewGuid(), "Pia", "🤖"),
            Provider: new AiProvider { Name = "Test", Endpoint = "http://localhost", ProviderType = AiProviderType.OpenAI },
            Tools: null,
            SupportsTools: false,
            WebSearchActive: false,
            TokenizationEnabled: false);

        Assert.Null(spec.WorkspaceRoot);
    }
}
