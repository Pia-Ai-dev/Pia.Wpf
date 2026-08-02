using System.IO;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// Batch 06 B3: the verifier's artifact probe must prefer <c>RunContext.WorkspaceRoot</c> over the
/// (null-at-verify-time) ambient and the settings folder, because verify runs on the orchestrator thread
/// — outside any step's ambient — after the per-step <c>TaskAmbient.Current</c> has already been restored
/// in the step's <c>finally</c>. Mirrors <c>AgentVerifierTests</c>'s probe harness.
/// </summary>
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
        Func<FunctionCallContent, Task<object?>>? handler, Dictionary<string, object?>? emitArgs, UsageDetails? usage)
    {
        if (handler is not null && emitArgs is not null)
            await handler(new FunctionCallContent(Guid.NewGuid().ToString(), "emit_verdict", emitArgs));
        await Task.Yield();
        yield return new Finished(usage, "test-model");
    }

    private static Dictionary<string, object?> V(bool passed, string reason, params string[] missing) =>
        new() { ["passed"] = passed, ["reason"] = reason, ["missing"] = missing.Cast<object?>().ToArray() };

    private void ReturnsVerdict(Dictionary<string, object?>? emitArgs, UsageDetails? usage = null)
    {
        _ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<Func<FunctionCallContent, Task<object?>>?>(), Arg.Any<string?>(), Arg.Any<Guid?>(), cancellationToken: Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                _systemPrompts.Add(ci.ArgAt<IList<ChatMessage>>(0)[0].Text ?? string.Empty);
                return VerdictStream(ci.ArgAt<Func<FunctionCallContent, Task<object?>>?>(3), emitArgs, usage);
            });
    }

    // REGRESSION (T-G1-4): drop `ctx.WorkspaceRoot ??` from AgentVerifier's probe and this goes red —
    // the artifact would report NOT FOUND because the probe would fall through to the settings folder,
    // where nothing was ever written.
    [Fact]
    public async Task ArtifactProbe_ResolvesAgainstTheContextWorkspaceRoot_NotTheSettingsFolder()
    {
        File.WriteAllText(Path.Combine(_workspaceDir, "report.md"), "the deliverable");
        _settings.AssistantFilesFolder = _settingsDir; // deliberately NOT where the file lives
        ReturnsVerdict(V(true, "ok"));

        // TaskAmbient.Current is null here, matching the production shape at verify time (the
        // orchestrator thread, outside any step's ambient — the per-step ambient was already restored
        // in the step's `finally`).
        Assert.Null(TaskAmbient.Current);

        var ctx = CtxDeclaring(_workspaceDir, "report.md");

        await BuildVerifier().VerifyAsync(ctx, Persona(), Provider(), TestContext.Current.CancellationToken);

        Assert.Contains("declared: report.md → found", LastPrompt);
    }

    // GUARD (T-G1-5): pins that G1 changes no behaviour for the (still universal, in production) case
    // where no workspace root is set — the settings folder is probed exactly as it is today.
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
