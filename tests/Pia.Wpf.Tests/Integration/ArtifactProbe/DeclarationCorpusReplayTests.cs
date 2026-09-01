using System.IO;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Integration.ArtifactProbe;

// Replays a hand-written corpus, so it measures this corpus and not what real runs declare.
// It says nothing about the found-versus-missing split against a real filesystem.
// It touches nothing about the artifact a step reports for itself.
public sealed class DeclarationCorpusReplayTests : IDisposable
{
    // Mirrors the verifier's private cap on an interpolated declaration.
    private const int DeclarationLineCap = 200;

    private readonly IAiClientService _ai = Substitute.For<IAiClientService>();
    private readonly ISettingsService _settingsService = Substitute.For<ISettingsService>();
    private readonly AppSettings _settings = new();
    private readonly List<string> _systemPrompts = new();
    private readonly string _dir;

    public DeclarationCorpusReplayTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "PiaTests_ArtifactProbe_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _settings.AssistantFilesFolder = _dir;
        _settingsService.GetSettingsAsync().Returns(_ => Task.FromResult(_settings));
        ReturnsVerdict();
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    public static TheoryData<string, string> Corpus => DeclarationCorpus.Rows();

    [Theory]
    [MemberData(nameof(Corpus))]
    public async Task EveryCorpusDeclaration_RendersTheExpectedOutcomeArm(string declaration, string expectedOutcome)
    {
        var prompt = await ProbeAsync(declaration, TestContext.Current.CancellationToken);

        Assert.Contains($"- step 1 \"S0\" declared: {declaration} → {expectedOutcome}", prompt);
    }

    [Fact]
    public void Corpus_HasNoDuplicateDeclarations()
    {
        // A duplicate row would collide as a theory case id.
        var duplicates = DeclarationCorpus.Cases
            .GroupBy(c => c.Declaration, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        Assert.Empty(duplicates);
    }

    [Fact]
    public async Task FileNameInProse_ThatExistsOnDisk_RendersTheFoundArm()
    {
        // Keeps the corpus's NOT FOUND column honest: it is a property of the empty fixture folder, not of the classifier.
        WriteFile("report.md", 1536);

        var prompt = await ProbeAsync("a summary saved to report.md", TestContext.Current.CancellationToken);

        Assert.Contains(AgentVerifier.ArtifactBlockHeader, prompt);
        Assert.Contains("declared: a summary saved to report.md → report.md: found (1.5 KB, modified ", prompt);
    }

    [Fact]
    public async Task Declaration_SpanningSeveralLines_IsFlattenedOntoOneFactLine()
    {
        var prompt = await ProbeAsync("notes.md\tand then\ndraft the memo", TestContext.Current.CancellationToken);

        Assert.Contains("declared: notes.md and then draft the memo → notes.md: NOT FOUND", prompt);
        Assert.Equal(1, CountOccurrences(prompt, "- step "));
    }

    [Fact]
    public async Task Declaration_LongerThanTheLineCap_IsTruncated_AndAFileNameBeyondItIsNeverProbed()
    {
        var declaration = "report.md must be delivered "
            + string.Join(' ', Enumerable.Repeat("and reviewed", 20)) + " late.md";
        Assert.True(declaration.IndexOf("late.md", StringComparison.Ordinal) > DeclarationLineCap,
            "the fixture must put the second file name beyond the cap, or it stops testing its own claim");

        var prompt = await ProbeAsync(declaration, TestContext.Current.CancellationToken);

        Assert.Contains($"declared: {declaration[..DeclarationLineCap]}… → report.md: NOT FOUND", prompt);
        Assert.DoesNotContain("late.md", prompt);
    }

    [Fact]
    public async Task Declaration_NamingFourFiles_ProbesOnlyTheFirstThree()
    {
        var prompt = await ProbeAsync("a.md, b.md, c.md, d.md", TestContext.Current.CancellationToken);

        Assert.Contains("→ a.md: NOT FOUND; b.md: NOT FOUND; c.md: NOT FOUND", prompt);
        Assert.DoesNotContain("d.md:", prompt);
    }

    [Fact]
    public async Task ProbeBudget_Exhausted_CapsTheRemainingCandidates_ThenWholeDeclarations()
    {
        string[] declarations =
        [
            "a.md, b.md, c.md",
            "d.md, e.md, f.md",
            "g.md, h.md, i.md",
            "j.md, k.md",
            "l.md, m.md, n.md",   // the twelfth probe lands here, mid-declaration
            "o.md",
        ];

        var prompt = await ProbeAsync(declarations, TestContext.Current.CancellationToken);

        Assert.Contains(
            "declared: l.md, m.md, n.md → l.md: NOT FOUND; m.md: not probed (probe budget reached); "
            + "n.md: not probed (probe budget reached)",
            prompt);
        Assert.Contains("declared: o.md → not probed (probe budget reached)", prompt);
        // Prefixed counting holds only because every probed candidate here sits in a multi-candidate declaration.
        Assert.Equal(12, CountOccurrences(prompt, ": NOT FOUND"));
        Assert.Equal(3, CountOccurrences(prompt, "not probed (probe budget reached)"));
    }

    [Fact]
    public async Task ReportCap_KeepsTwentyFactLines_AndTalliesTheRest()
    {
        // Dot-free prose, so the report cap is measured without the probe cap interfering.
        var declarations = Enumerable.Range(0, 22).Select(i => $"a summary of item {i}").ToList();

        var prompt = await ProbeAsync(declarations, TestContext.Current.CancellationToken);

        // Counted on the fact-line form: the block's closing instruction quotes the bare phrase once.
        Assert.Equal(20, CountOccurrences(prompt, "→ not a file reference"));
        Assert.Contains("- (2 further declared artifact(s) not probed — probe budget reached)", prompt);
        Assert.DoesNotContain("a summary of item 20", prompt);
        Assert.DoesNotContain("a summary of item 21", prompt);
    }

    private Task<string> ProbeAsync(string declaration, CancellationToken ct) =>
        ProbeAsync(new[] { declaration }, ct);

    private async Task<string> ProbeAsync(IReadOnlyList<string> declarations, CancellationToken ct)
    {
        var ctx = new RunContext("build a thing", RunProfile.Interactive);
        for (var i = 0; i < declarations.Count; i++)
        {
            ctx.RecordStep(
                new AgentStep { Ordinal = i, Title = "S" + i, Intent = "do " + i, ExpectedArtifact = declarations[i] },
                new StepTurnResult(true, false, null, "did it", null, Guid.NewGuid(), Guid.NewGuid()));
        }

        var verifier = new AgentVerifier(_ai, _settingsService, NullLogger<AgentVerifier>.Instance);
        await verifier.VerifyAsync(ctx, Persona(), Provider(), ct);

        return _systemPrompts[^1];
    }

    private void WriteFile(string relativePath, int bytes)
    {
        var full = Path.Combine(_dir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, new string('x', bytes));
    }

    private static AiProvider Provider() => new() { Name = "P", Endpoint = "https://x", ProviderType = AiProviderType.OpenAI };
    private static Persona Persona() => new() { Name = "Pia", SystemPrompt = "sys" };

    private static async IAsyncEnumerable<ChatStreamItem> VerdictStream(ToolCallHandler? handler)
    {
        if (handler is not null)
        {
            var args = new Dictionary<string, object?>
            {
                ["passed"] = true,
                ["reason"] = "ok",
                ["missing"] = Array.Empty<object?>(),
            };
            await handler(new FunctionCallContent(Guid.NewGuid().ToString(), "emit_verdict", args), new ToolDispatchContext(1));
        }

        await Task.Yield();
        yield return new Finished(null, "test-model");
    }

    private void ReturnsVerdict()
    {
        _ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<string?>(), cancellationToken: Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                _systemPrompts.Add(ci.ArgAt<IList<ChatMessage>>(0)[0].Text ?? string.Empty);
                return VerdictStream(ci.ArgAt<ToolCallHandler?>(3));
            });
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
