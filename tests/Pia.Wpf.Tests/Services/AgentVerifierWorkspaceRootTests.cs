using System.IO;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>Verify runs outside any step's ambient, so the artifact probe must use <c>RunContext.WorkspaceRoot</c> rather than <c>TaskAmbient.Current</c>.</summary>
public sealed class AgentVerifierWorkspaceRootTests : IDisposable
{
    private readonly IAiClientService _ai = Substitute.For<IAiClientService>();
    private readonly ISettingsService _settingsService = Substitute.For<ISettingsService>();
    private readonly AppSettings _settings = new();
    private readonly List<string> _systemPrompts = new();
    private readonly string _settingsDir;
    private readonly string _workspaceDir;

    public AgentVerifierWorkspaceRootTests()
    {
        _settingsDir = Path.Combine(Path.GetTempPath(), "PiaTests_AVWR_settings_" + Guid.NewGuid().ToString("N"));
        _workspaceDir = Path.Combine(Path.GetTempPath(), "PiaTests_AVWR_workspace_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_settingsDir);
        Directory.CreateDirectory(_workspaceDir);
        _settingsService.GetSettingsAsync().Returns(_ => Task.FromResult(_settings));
    }

    public void Dispose()
    {
        try { Directory.Delete(_settingsDir, true); } catch { /* best effort */ }
        try { Directory.Delete(_workspaceDir, true); } catch { /* best effort */ }
    }

    private static AiProvider Provider() => new() { Name = "P", Endpoint = "https://x", ProviderType = AiProviderType.OpenAI };
    private static Persona Persona() => new() { Name = "Pia", SystemPrompt = "sys" };

    private static RunContext CtxDeclaring(string? workspaceRoot, params string?[] artifacts)
    {
        var c = new RunContext("build a thing", RunProfile.Interactive) { WorkspaceRoot = workspaceRoot };
        for (var i = 0; i < artifacts.Length; i++)
        {
            c.RecordStep(
                new AgentStep { Ordinal = i, Title = "S" + i, Intent = "do " + i, ExpectedArtifact = artifacts[i] },
                new StepTurnResult(true, false, null, "did it", null, Guid.NewGuid(), Guid.NewGuid()));
        }
        return c;
    }

    private AgentVerifier BuildVerifier() => new(_ai, _settingsService, NullLogger<AgentVerifier>.Instance);

    private string LastPrompt => _systemPrompts[^1];

    private static async IAsyncEnumerable<ChatStreamItem> VerdictStream(
        ToolCallHandler? handler, Dictionary<string, object?>? emitArgs, UsageDetails? usage)
    {
        if (handler is not null && emitArgs is not null)
            await handler(new FunctionCallContent(Guid.NewGuid().ToString(), "emit_verdict", emitArgs), new ToolDispatchContext(1));
        await Task.Yield();
        yield return new Finished(usage, "test-model");
    }

    private static Dictionary<string, object?> V(bool passed, string reason, params string[] missing) =>
        new() { ["passed"] = passed, ["reason"] = reason, ["missing"] = missing.Cast<object?>().ToArray() };

    private void ReturnsVerdict(Dictionary<string, object?>? emitArgs, UsageDetails? usage = null)
    {
        _ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(), cancellationToken: Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                _systemPrompts.Add(ci.ArgAt<IList<ChatMessage>>(0)[0].Text ?? string.Empty);
                return VerdictStream(ci.ArgAt<ToolCallHandler?>(3), emitArgs, usage);
            });
    }

    [Fact]
    public async Task ArtifactProbe_ResolvesAgainstTheContextWorkspaceRoot_NotTheSettingsFolder()
    {
        File.WriteAllText(Path.Combine(_workspaceDir, "report.md"), "the deliverable");
        _settings.AssistantFilesFolder = _settingsDir; // deliberately NOT where the file lives
        ReturnsVerdict(V(true, "ok"));

        // Matches the production shape at verify time: no step ambient is in scope.
        Assert.Null(TaskAmbient.Current);

        var ctx = CtxDeclaring(_workspaceDir, "report.md");

        await BuildVerifier().VerifyAsync(ctx, Persona(), Provider(), TestContext.Current.CancellationToken);

        Assert.Contains("declared: report.md → found", LastPrompt);
    }

    [Fact]
    public async Task ArtifactProbe_StillUsesTheSettingsFolder_WhenNoWorkspaceRootIsSet()
    {
        File.WriteAllText(Path.Combine(_settingsDir, "report.md"), "the deliverable");
        _settings.AssistantFilesFolder = _settingsDir;
        ReturnsVerdict(V(true, "ok"));

        var ctx = CtxDeclaring(workspaceRoot: null, "report.md");

        await BuildVerifier().VerifyAsync(ctx, Persona(), Provider(), TestContext.Current.CancellationToken);

        Assert.Contains("declared: report.md → found", LastPrompt);
    }
}
