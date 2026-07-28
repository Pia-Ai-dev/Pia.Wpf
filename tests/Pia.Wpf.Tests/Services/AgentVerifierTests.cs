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
/// Verifier behavior (§13.x). An <c>emit_verdict</c> call parses into a VerdictResult; no-call retries
/// once (firmer) then degrades to ACCEPT; usage is summed from the yielded <see cref="Finished"/> items
/// (the planner does the same since I1) so the orchestrator can accrue it run-level. H1: the verify
/// prompt carries a MECHANICAL declared-artifact probe block, and every probe fault degrades to
/// "verify without the block" rather than failing the run.
/// </summary>
public sealed class AgentVerifierTests : IDisposable
{
    private readonly IAiClientService _ai = Substitute.For<IAiClientService>();
    private readonly ISettingsService _settingsService = Substitute.For<ISettingsService>();
    private readonly AppSettings _settings = new();
    private readonly List<string> _systemPrompts = new();
    private readonly string _dir;

    public AgentVerifierTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "PiaTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _settingsService.GetSettingsAsync().Returns(_ => Task.FromResult(_settings));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private static AiProvider Provider() => new() { Name = "P", Endpoint = "https://x", ProviderType = AiProviderType.OpenAI };
    private static Persona Persona() => new() { Name = "Pia", SystemPrompt = "sys" };

    private static RunContext Ctx()
    {
        var c = new RunContext("build a thing", RunProfile.Interactive);
        c.RecordStep(new AgentStep { Ordinal = 0, Title = "A", Intent = "ia" },
            new StepTurnResult(true, false, null, "step result text", null, Guid.NewGuid(), Guid.NewGuid()));
        return c;
    }

    /// <summary>A run context whose completed steps declare the given artifacts (index = ordinal).</summary>
    private static RunContext CtxDeclaring(params string?[] artifacts)
    {
        var c = new RunContext("build a thing", RunProfile.Interactive);
        for (var i = 0; i < artifacts.Length; i++)
        {
            c.RecordStep(
                new AgentStep { Ordinal = i, Title = "S" + i, Intent = "do " + i, ExpectedArtifact = artifacts[i] },
                new StepTurnResult(true, false, null, "did it", null, Guid.NewGuid(), Guid.NewGuid()));
        }
        return c;
    }

    private AgentVerifier BuildVerifier() => new(_ai, _settingsService, NullLogger<AgentVerifier>.Instance);

    /// <summary>The system prompt of the LAST verify attempt (the probe block lives there).</summary>
    private string LastPrompt => _systemPrompts[^1];

    // Drives one verify turn: invokes the captured toolHandler with a synthetic emit_verdict call
    // (when emitArgs is set) then yields Finished carrying usage — the loop drains the whole stream.
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
                Arg.Any<Func<FunctionCallContent, Task<object?>>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                _systemPrompts.Add(ci.ArgAt<IList<ChatMessage>>(0)[0].Text ?? string.Empty);
                return VerdictStream(ci.ArgAt<Func<FunctionCallContent, Task<object?>>?>(3), emitArgs, usage);
            });
    }

    private string WriteFile(string relativePath, int bytes)
    {
        var full = Path.Combine(_dir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, new string('x', bytes));
        return full;
    }

    [Fact]
    public async Task VerifyAsync_EmitPassedTrue_ReturnsPassed()
    {
        ReturnsVerdict(V(true, "looks complete"));

        var result = await BuildVerifier().VerifyAsync(Ctx(), Persona(), Provider(), TestContext.Current.CancellationToken);

        Assert.True(result.Passed);
        Assert.Empty(result.Missing);
        _ai.Received(1).GetChatCompletionWithToolsAsync(
            Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
            Arg.Any<Func<FunctionCallContent, Task<object?>>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task VerifyAsync_EmitPassedFalse_ReturnsFailWithMissing()
    {
        ReturnsVerdict(V(false, "incomplete", "artifact X"));

        var result = await BuildVerifier().VerifyAsync(Ctx(), Persona(), Provider(), TestContext.Current.CancellationToken);

        Assert.False(result.Passed);
        Assert.Equal("incomplete", result.Reason);
        Assert.Contains("artifact X", result.Missing);
    }

    [Fact]
    public async Task VerifyAsync_NoCall_RetriesOnce_ThenAccept()
    {
        ReturnsVerdict(emitArgs: null); // no emit_verdict call on either attempt

        var result = await BuildVerifier().VerifyAsync(Ctx(), Persona(), Provider(), TestContext.Current.CancellationToken);

        Assert.True(result.Passed); // degrade → accept
        _ai.Received(2).GetChatCompletionWithToolsAsync(
            Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
            Arg.Any<Func<FunctionCallContent, Task<object?>>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task VerifyAsync_CapturesUsageFromFinished()
    {
        ReturnsVerdict(V(true, "ok"), new UsageDetails { InputTokenCount = 7, OutputTokenCount = 3 });

        var result = await BuildVerifier().VerifyAsync(Ctx(), Persona(), Provider(), TestContext.Current.CancellationToken);

        Assert.NotNull(result.Usage);
        Assert.Equal(7, result.Usage!.InputTokenCount);
        Assert.Equal(3, result.Usage.OutputTokenCount);
    }

    // ---- H1: the verdict is anchored in mechanical artifact facts ----

    [Fact]
    public async Task VerifyAsync_ArtifactBlock_ReportsFound_NotFound_AndNonPathDeclarations()
    {
        WriteFile("report.md", 1536);          // 1.5 KB
        WriteFile(Path.Combine("notes", "summary.md"), 10);
        _settings.AssistantFilesFolder = _dir;
        ReturnsVerdict(V(true, "ok"));

        var ctx = CtxDeclaring(
            "report.md",                       // exists at the root
            "missing.md",                      // declared but never written
            "a summary of the Q3 numbers",     // free text — not a file at all
            "write the digest to notes/summary.md"); // a path embedded in prose

        await BuildVerifier().VerifyAsync(ctx, Persona(), Provider(), TestContext.Current.CancellationToken);

        Assert.Contains(AgentVerifier.ArtifactBlockHeader, LastPrompt);
        Assert.Contains("declared: report.md → found (1.5 KB, modified ", LastPrompt);
        Assert.Contains("declared: missing.md → NOT FOUND", LastPrompt);
        Assert.Contains("a summary of the Q3 numbers → not a file reference", LastPrompt);
        Assert.Contains("notes/summary.md: found", LastPrompt);
        // The step is identified 1-based, matching the plan numbering a user sees.
        Assert.Contains("- step 1 \"S0\" declared:", LastPrompt);
        // The critic is told a miss is a FACT — never that it must mechanically fail the run.
        Assert.Contains("NOT FOUND is a verify-relevant FACT", LastPrompt);
        Assert.Contains("NOT an automatic failure", LastPrompt);
    }

    [Fact]
    public async Task VerifyAsync_ArtifactProbe_NeverLeavesTheSandbox()
    {
        // A real file OUTSIDE the sandbox, reachable only by escaping it. Both a traversal and an
        // absolute path must be refused before any stat — the probe reports them as unresolvable, never
        // as "found" (which would prove it read outside the folder).
        var outside = Path.Combine(_dir, "outside");
        Directory.CreateDirectory(outside);
        var sandbox = Path.Combine(_dir, "sandbox");
        Directory.CreateDirectory(sandbox);
        File.WriteAllText(Path.Combine(outside, "secret.md"), "xxxx");
        _settings.AssistantFilesFolder = sandbox;
        ReturnsVerdict(V(true, "ok"));

        var ctx = CtxDeclaring(
            Path.Combine("..", "outside", "secret.md"),
            Path.Combine(outside, "secret.md"));

        await BuildVerifier().VerifyAsync(ctx, Persona(), Provider(), TestContext.Current.CancellationToken);

        Assert.DoesNotContain("found", LastPrompt); // nothing outside the sandbox was ever inspected
        Assert.Contains("not a resolvable path inside the assistant files folder", LastPrompt);
    }

    [Fact]
    public async Task VerifyAsync_ArtifactProbe_FolderDeclaration_IsReportedAsAFolder()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "out.d"));
        _settings.AssistantFilesFolder = _dir;
        ReturnsVerdict(V(true, "ok"));

        await BuildVerifier().VerifyAsync(CtxDeclaring("out.d"), Persona(), Provider(), TestContext.Current.CancellationToken);

        Assert.Contains("found, but it is a folder, not a file", LastPrompt);
    }

    [Theory]
    [InlineData("increase revenue by 12.5")]   // a decimal is not an extension
    [InlineData("ship v1.0 of the plan")]      // nor is a version number
    [InlineData("notes")]                      // no extension at all
    [InlineData(".md")]                        // a bare extension is not a file name
    public async Task VerifyAsync_ProsePlausibleButNotAPath_IsNotReportedAsMissing(string declaration)
    {
        _settings.AssistantFilesFolder = _dir;
        ReturnsVerdict(V(true, "ok"));

        await BuildVerifier().VerifyAsync(CtxDeclaring(declaration), Persona(), Provider(), TestContext.Current.CancellationToken);

        Assert.Contains("not a file reference", LastPrompt);
        Assert.DoesNotContain("NOT FOUND", LastPrompt);
    }

    [Fact]
    public async Task VerifyAsync_ManyDeclarations_AreBounded_ByProbeAndReportCaps()
    {
        _settings.AssistantFilesFolder = _dir;
        ReturnsVerdict(V(true, "ok"));
        var declarations = Enumerable.Range(0, 25).Select(i => $"file{i}.md").ToArray();

        await BuildVerifier().VerifyAsync(CtxDeclaring(declarations), Persona(), Provider(), TestContext.Current.CancellationToken);

        // 12 probed (all missing), 8 more reported unprobed, the last 5 collapsed into one summary line:
        // a 25-step plan cannot turn the verify turn into 25 filesystem walks.
        Assert.Equal(12, CountOccurrences(LastPrompt, "NOT FOUND"));
        Assert.Equal(8, CountOccurrences(LastPrompt, "not probed (probe budget reached)"));
        Assert.Contains("(5 further declared artifact(s) not probed", LastPrompt);
    }

    [Fact]
    public async Task VerifyAsync_ProbesOncePerVerify_NotOncePerAttempt()
    {
        WriteFile("report.md", 8);
        _settings.AssistantFilesFolder = _dir;
        ReturnsVerdict(emitArgs: null); // no-call → the firm retry runs too

        await BuildVerifier().VerifyAsync(CtxDeclaring("report.md"), Persona(), Provider(), TestContext.Current.CancellationToken);

        Assert.Equal(2, _systemPrompts.Count);
        Assert.All(_systemPrompts, p => Assert.Contains(AgentVerifier.ArtifactBlockHeader, p)); // both attempts see the facts
        await _settingsService.Received(1).GetSettingsAsync();                                  // probed exactly once
    }

    [Fact]
    public async Task VerifyAsync_NoDeclaredArtifacts_OmitsTheBlockEntirely()
    {
        _settings.AssistantFilesFolder = _dir;
        ReturnsVerdict(V(true, "ok"));

        await BuildVerifier().VerifyAsync(Ctx(), Persona(), Provider(), TestContext.Current.CancellationToken);

        Assert.DoesNotContain(AgentVerifier.ArtifactBlockHeader, LastPrompt);
        await _settingsService.DidNotReceive().GetSettingsAsync(); // no declarations → no filesystem work at all
    }

    [Fact]
    public async Task VerifyAsync_NoConfiguredFilesFolder_OmitsTheBlock_AndStillVerdicts()
    {
        _settings.AssistantFilesFolder = null; // nothing to probe against
        ReturnsVerdict(V(false, "not done", "report.md"));

        var result = await BuildVerifier().VerifyAsync(CtxDeclaring("report.md"), Persona(), Provider(), TestContext.Current.CancellationToken);

        Assert.DoesNotContain(AgentVerifier.ArtifactBlockHeader, LastPrompt);
        Assert.False(result.Passed); // the LLM still renders the verdict
    }

    [Fact]
    public async Task VerifyAsync_ProbeThrows_OmitsTheBlock_ButStillVerdicts()
    {
        // Guardrail 1: the probe is bookkeeping-grade evidence, never on the critical path. A settings
        // read that blows up must not fail the verify turn (and must not fail the run).
        _settingsService.GetSettingsAsync().Returns<AppSettings>(_ => throw new InvalidOperationException("settings boom"));
        ReturnsVerdict(V(true, "ok"));

        var result = await BuildVerifier().VerifyAsync(CtxDeclaring("report.md"), Persona(), Provider(), TestContext.Current.CancellationToken);

        Assert.True(result.Passed);
        Assert.Single(_systemPrompts);
        Assert.DoesNotContain(AgentVerifier.ArtifactBlockHeader, LastPrompt);
    }

    [Fact]
    public async Task VerifyAsync_ProbeRootDeleted_OmitsTheBlock_ButStillVerdicts()
    {
        _settings.AssistantFilesFolder = Path.Combine(_dir, "gone"); // configured but not on disk
        ReturnsVerdict(V(true, "ok"));

        var result = await BuildVerifier().VerifyAsync(CtxDeclaring("report.md"), Persona(), Provider(), TestContext.Current.CancellationToken);

        Assert.True(result.Passed);
        Assert.DoesNotContain(AgentVerifier.ArtifactBlockHeader, LastPrompt);
    }

    [Fact]
    public async Task VerifyAsync_CancelDuringProbe_Propagates_NeverDegrades()
    {
        // Cancellation is the single exception to failure isolation: the orchestrator's SafeVerify must
        // see a genuine run cancel, not an accept produced by swallowing it.
        _settings.AssistantFilesFolder = _dir;
        using var cts = new CancellationTokenSource();
        _settingsService.GetSettingsAsync().Returns<AppSettings>(_ =>
        {
            cts.Cancel();
            cts.Token.ThrowIfCancellationRequested();
            return _settings;
        });
        ReturnsVerdict(V(true, "ok"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            BuildVerifier().VerifyAsync(CtxDeclaring("report.md"), Persona(), Provider(), cts.Token));

        Assert.Empty(_systemPrompts); // never reached the provider
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }
        return count;
    }
}
