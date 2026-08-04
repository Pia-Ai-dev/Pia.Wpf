using System.IO;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Services.Providers;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// T2-17a — the grounding digest folded into the plan turn. Same harness shape as
/// <see cref="AgentPlannerRosterTests"/> (substituted <see cref="IAiClientService"/>, real
/// <see cref="AppSettings"/>, captured prompts), pointed at a real temp folder because the digest's whole
/// subject is what is on disk.
/// <para>
/// Three facts carry the design: the listing lands on the USER message and not the System prompt (the tokenizer
/// rewrites only user text, and these are file names out of the user's own folder); no usable folder means a
/// byte-identical pre-T2-17a prompt; and the REPLAN does not carry it.
/// </para>
/// </summary>
public sealed class AgentPlannerGroundingTests : IDisposable
{
    private readonly IAiClientService _ai = Substitute.For<IAiClientService>();
    private readonly ISettingsService _settingsService = Substitute.For<ISettingsService>();
    private readonly AppSettings _settings = new();
    private readonly string _tmpDir;

    private const string Goal = "ship the widget catalogue";
    private const string FenceOpen = "--- Already in the working folder";
    private const string FenceClose = "--- end of working folder ---";

    public AgentPlannerGroundingTests()
    {
        _settingsService.GetSettingsAsync().Returns(_ => Task.FromResult(_settings));
        _tmpDir = Path.Combine(Path.GetTempPath(), "PiaGrounding_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
        _settings.AssistantFilesFolder = _tmpDir;
    }

    private static AiProvider Provider() => new()
    {
        Name = "P", Endpoint = "https://x", ProviderType = AiProviderType.OpenAI, SupportsToolCalling = true,
    };

    private static Persona Persona() => new() { Id = Guid.NewGuid(), Name = "Pia", SystemPrompt = "you are Pia" };

    private static RunContext Ctx(string? workspaceRoot = null, string? workingSubpath = null) =>
        new(Goal, RunProfile.Interactive) { WorkspaceRoot = workspaceRoot, WorkingSubpath = workingSubpath };

    private AgentPlanner Planner()
    {
        var handler = Substitute.For<IAiProviderHandler>();
        handler.ProviderType.Returns(AiProviderType.OpenAI);
        handler.DropsReasoningEffortWithTools.Returns(false);
        return new AgentPlanner(_ai, new AiProviderHandlerResolver([handler]), _settingsService,
            NullLogger<AgentPlanner>.Instance);
    }

    private readonly List<string> _systemPrompts = [];
    private readonly List<string> _userPrompts = [];

    private string LastSystemPrompt => _systemPrompts[^1];
    private string LastUserPrompt => _userPrompts[^1];

    private static async IAsyncEnumerable<ChatStreamItem> PlanStream(
        ToolCallHandler? handler, Dictionary<string, object?>? emitArgs)
    {
        if (handler is not null && emitArgs is not null)
            await handler(new FunctionCallContent(Guid.NewGuid().ToString(), "emit_plan", emitArgs),
                new ToolDispatchContext(1));
        await Task.Yield();
        yield return new Finished(null, "test-model");
    }

    private static Dictionary<string, object?> OneStep() => new()
    {
        ["steps"] = new object[]
        {
            new Dictionary<string, object?>
            {
                ["title"] = "do it", ["intent"] = "get it done", ["expectedArtifact"] = null,
                ["personaKey"] = null, ["parallelGroup"] = null,
            },
        },
    };

    private void ReturnsPlan()
    {
        _ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(),
                cancellationToken: Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var messages = ci.ArgAt<IList<ChatMessage>>(0);
                _systemPrompts.Add(messages[0].Text ?? string.Empty);
                _userPrompts.Add(messages[1].Text ?? string.Empty);
                return PlanStream(ci.ArgAt<ToolCallHandler?>(3), OneStep());
            });
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void Touch(string relative)
    {
        var full = Path.Combine(_tmpDir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, "x");
    }

    // ---- what reaches the model, and where -----------------------------------------------------------

    [Fact]
    public async Task TheListingRidesTheUserMessage_NeverTheSystemPrompt()
    {
        Touch("report.md");
        Touch("notes.txt");
        Directory.CreateDirectory(Path.Combine(_tmpDir, "drafts"));
        ReturnsPlan();

        await Planner().PlanAsync(Goal, Ctx(), Persona(), Provider(), Ct);

        Assert.Contains(FenceOpen, LastUserPrompt);
        Assert.Contains("report.md", LastUserPrompt);
        Assert.Contains("notes.txt", LastUserPrompt);
        Assert.Contains("drafts/", LastUserPrompt);
        Assert.Contains(FenceClose, LastUserPrompt);

        // THE placement fact: TokenizeMessages rewrites ChatRole.User text only, so a file name in the System
        // prompt would ship past the tokenizer with tokenization ON.
        Assert.DoesNotContain(FenceOpen, LastSystemPrompt);
        Assert.DoesNotContain("report.md", LastSystemPrompt);

        // The request shape is still exactly [System, User] — no provider meets a shape this path has not sent.
        Assert.Single(_systemPrompts);
        Assert.Single(_userPrompts);
    }

    [Fact]
    public async Task AnEmptyFolder_IsStillWorthSaying()
    {
        ReturnsPlan();

        await Planner().PlanAsync(Goal, Ctx(), Persona(), Provider(), Ct);

        // "There is nothing here yet" is what stops a first step of "update the existing report".
        Assert.Contains("the folder is empty", LastUserPrompt);
    }

    /// <summary>
    /// No usable folder ⇒ the prompt is the pre-T2-17a one, byte for byte. This is the opt-out that costs
    /// nothing: a device with no assistant folder configured cannot get a different plan because of this item.
    /// </summary>
    [Fact]
    public async Task NoUsableFolder_LeavesThePromptExactlyAsItWas()
    {
        _settings.AssistantFilesFolder = null;
        ReturnsPlan();

        await Planner().PlanAsync(Goal, Ctx(), Persona(), Provider(), Ct);

        Assert.Equal(Goal, LastUserPrompt);
    }

    [Fact]
    public async Task AFolderThatDoesNotExist_IsNotDescribed()
    {
        _settings.AssistantFilesFolder = Path.Combine(_tmpDir, "does-not-exist");
        ReturnsPlan();

        await Planner().PlanAsync(Goal, Ctx(), Persona(), Provider(), Ct);

        Assert.Equal(Goal, LastUserPrompt);
    }

    // ---- which folder ---------------------------------------------------------------------------------

    /// <summary>
    /// Batch 06 B3's ladder: an isolated run's steps write into its WORKSPACE, so describing the settings folder
    /// would name a directory the run never touches. ctx.WorkspaceRoot is set by the executor's BeginRunAsync,
    /// which the orchestrator calls BEFORE PlanAsync.
    /// </summary>
    [Fact]
    public async Task AnIsolatedRun_DescribesItsWorkspace_NotTheSettingsFolder()
    {
        Touch("in-the-settings-folder.md");
        var workspace = Path.Combine(_tmpDir, "runs", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        File.WriteAllText(Path.Combine(workspace, "in-the-workspace.md"), "x");
        ReturnsPlan();

        await Planner().PlanAsync(Goal, Ctx(workspaceRoot: workspace), Persona(), Provider(), Ct);

        Assert.Contains("in-the-workspace.md", LastUserPrompt);
        Assert.DoesNotContain("in-the-settings-folder.md", LastUserPrompt);
    }

    [Fact]
    public async Task AWorkingSubpath_NarrowsTheListing()
    {
        Touch("at-the-root.md");
        Touch("Playground/inside.md");
        ReturnsPlan();

        await Planner().PlanAsync(Goal, Ctx(workingSubpath: "Playground"), Persona(), Provider(), Ct);

        Assert.Contains("inside.md", LastUserPrompt);
        Assert.DoesNotContain("at-the-root.md", LastUserPrompt);
    }

    /// <summary>
    /// A subpath that does not exist falls back to the base root and never widens past it — the same fail-safe
    /// direction as <c>FilesToolHandler.ResolveEffectiveRoot</c>.
    /// </summary>
    [Fact]
    public async Task AMissingWorkingSubpath_FallsBackToTheBaseRoot()
    {
        Touch("at-the-root.md");
        ReturnsPlan();

        await Planner().PlanAsync(Goal, Ctx(workingSubpath: "nope"), Persona(), Provider(), Ct);

        Assert.Contains("at-the-root.md", LastUserPrompt);
    }

    // ---- bounds and filtering -------------------------------------------------------------------------

    /// <summary>
    /// The cap is a reliability bound, not tidiness: this turn passes no context budget, so nothing downstream
    /// would compact a listing that buried the goal.
    /// </summary>
    [Fact]
    public async Task TheListingIsCapped_AndSaysHowMuchItLeftOut()
    {
        for (var i = 0; i < 45; i++)
            Touch($"file-{i:000}.md");
        ReturnsPlan();

        await Planner().PlanAsync(Goal, Ctx(), Persona(), Provider(), Ct);

        Assert.Contains("and 5 more", LastUserPrompt);
        Assert.Contains("file-000.md", LastUserPrompt);   // sorted, so the first is listed
        Assert.DoesNotContain("file-044.md", LastUserPrompt); // and the tail is not
    }

    /// <summary>
    /// The count is a CLAIM, so it may only be printed when the walk actually saw everything. Past the scan cap
    /// the block says "and more" with no number — the earlier shape reported (entries looked at − the cap), which
    /// on a folder of ten thousand files was a specific, wrong number in a model-facing prompt.
    /// </summary>
    [Fact]
    public async Task PastTheScanCap_TheBlockSaysMore_WithoutAWrongNumber()
    {
        // Above the scan cap (5,000) — the only way to reach the truncated arm.
        for (var i = 0; i < 5_050; i++)
            File.WriteAllText(Path.Combine(_tmpDir, $"f-{i:0000}.md"), "x");
        ReturnsPlan();

        await Planner().PlanAsync(Goal, Ctx(), Persona(), Provider(), Ct);

        Assert.Contains("… and more (use list_files", LastUserPrompt);
        Assert.DoesNotContain("… and 5", LastUserPrompt);   // no count at all on this path
        Assert.Contains(FenceClose, LastUserPrompt);         // and the block is still well-formed
    }

    /// <summary>
    /// The digest must not advertise what the file tools would refuse to show: it applies the SAME
    /// <c>SandboxIgnore</c> matcher <c>list_files</c> does.
    /// </summary>
    [Fact]
    public async Task IgnoredEntries_AreNotAdvertised()
    {
        Touch("kept.md");
        Directory.CreateDirectory(Path.Combine(_tmpDir, "node_modules"));
        Directory.CreateDirectory(Path.Combine(_tmpDir, ".git"));
        ReturnsPlan();

        await Planner().PlanAsync(Goal, Ctx(), Persona(), Provider(), Ct);

        Assert.Contains("kept.md", LastUserPrompt);
        Assert.DoesNotContain("node_modules", LastUserPrompt);
        Assert.DoesNotContain(".git", LastUserPrompt);
    }

    // ---- scope ----------------------------------------------------------------------------------------

    /// <summary>
    /// PLAN ONLY. A replan already carries the completed-step summaries and their declared artifacts — better
    /// evidence about the folder than a fresh listing — and it can run MaxReplans times per run.
    /// </summary>
    [Fact]
    public async Task TheReplanCarriesNoListing()
    {
        Touch("report.md");
        ReturnsPlan();

        await Planner().ReplanAsync(Ctx(), failure: "step 1 failed", Persona(), Provider(), Ct);

        Assert.DoesNotContain(FenceOpen, LastUserPrompt);
        Assert.DoesNotContain("report.md", LastUserPrompt);
    }

    /// <summary>
    /// The firm retry (R10) re-sends the digest it already has rather than walking the folder again — and the
    /// retry's prompt must still carry it, or the retry would be planning with less grounding than the attempt
    /// it is replacing.
    /// </summary>
    [Fact]
    public async Task TheFirmRetry_StillCarriesTheListing()
    {
        Touch("report.md");
        // No emit_plan call at all ⇒ the planner retries firmly, then degrades.
        _ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(),
                cancellationToken: Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var messages = ci.ArgAt<IList<ChatMessage>>(0);
                _systemPrompts.Add(messages[0].Text ?? string.Empty);
                _userPrompts.Add(messages[1].Text ?? string.Empty);
                return PlanStream(null, null);
            });

        var result = await Planner().PlanAsync(Goal, Ctx(), Persona(), Provider(), Ct);

        Assert.True(result.FallBackToSingleTurn); // non-vacuity: the retry really happened
        Assert.Equal(2, _userPrompts.Count);
        Assert.All(_userPrompts, p => Assert.Contains("report.md", p));
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, recursive: true); } catch { /* temp dir */ }
        GC.SuppressFinalize(this);
    }
}
