using System.IO;
using Microsoft.Extensions.AI;
using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Integration.ArtifactProbe;

// The near-duplicate hint: two steps each delivering a similarly named, similarly sized file of the same
// type. It is metadata only — no file contents are compared — and it never fails a verdict.
public sealed class NearDuplicateDeliverableTests : IDisposable
{
    private const string PairA = "Urlaubsuebersicht_2026_pro_Mitarbeiter.md";
    private const string PairB = "Mitarbeiter_Urlaubszeiten_Zusammenfassung.md";

    private readonly IAiClientService _ai = Substitute.For<IAiClientService>();
    private readonly ISettingsService _settingsService = Substitute.For<ISettingsService>();
    private readonly AppSettings _settings = new();
    private readonly List<string> _systemPrompts = new();
    private readonly CapturingLogger<AgentVerifier> _log = new();
    private readonly string _dir;

    public NearDuplicateDeliverableTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "PiaTests_NearDuplicate_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _settings.AssistantFilesFolder = _dir;
        _settingsService.GetSettingsAsync().Returns(_ => Task.FromResult(_settings));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task TheObservedPair_TwoStepsOneDeliverable_IsFlagged()
    {
        WriteFile(PairA, 5245);
        WriteFile(PairB, 6776);
        ReturnsVerdict(passed: true, reason: "ok");

        await VerifyAsync((PairA, 0), (PairB, 1));

        Assert.Contains(
            $"- possible duplicate deliverable: step 1 \"{PairA}\" (5245 B) and step 2 \"{PairB}\" (6776 B)"
            + " — same file type, similar size, overlapping names",
            LastPrompt);
        Assert.Contains(AgentVerifier.DuplicateHint, LastPrompt);
        Assert.Equal(1, DupPairs);
    }

    [Theory]
    [InlineData("Bericht_Mitarbeiter.md", 5000, "Bericht_Mitarbeiter.csv", 5000, false)]         // different type
    [InlineData("Urlaub_A.md", 5000, "Krankheit_B.md", 5000, false)]                             // no shared token
    [InlineData("Mitarbeiter_Liste.md", 1000, "Mitarbeiter_Zusammenfassung.md", 5000, false)]    // size ratio 0.2
    [InlineData("Mitarbeiter_Liste.md", 5000, "Mitarbeiter_Zusammenfassung.md", 5000, true)]     // one step, two files
    [InlineData("Bericht.md", 5000, "./Bericht.md", 5000, false)]                                // one file, two spellings
    [InlineData(".scratch/Mitarbeiter_Liste.md", 5000, ".scratch/Mitarbeiter_Entwurf.md", 5000, false)] // working notes
    public async Task ALegitimateSecondArtifact_IsNeverFlagged(
        string first, int firstBytes, string second, int secondBytes, bool sameStep)
    {
        WriteFile(first, firstBytes);
        WriteFile(second, secondBytes);
        ReturnsVerdict(passed: true, reason: "ok");

        await VerifyAsync((first, 0), (second, sameStep ? 0 : 1));

        Assert.Contains("→ found (", LastPrompt); // both files really were probed and found
        Assert.DoesNotContain("possible duplicate deliverable", LastPrompt);
        Assert.Equal(0, DupPairs);
    }

    [Fact]
    public async Task AFlaggedDuplicate_NeverChangesTheVerdict()
    {
        WriteFile(PairA, 5245);
        WriteFile(PairB, 6776);
        ReturnsVerdict(passed: true, reason: "both files were asked for");

        var result = await VerifyAsync((PairA, 0), (PairB, 1));

        Assert.True(result.Passed);
        Assert.Equal("both files were asked for", result.Reason);
    }

    [Fact]
    public async Task TheDuplicateFileNames_NeverReachTheReleaseVisibleTallyLine()
    {
        WriteFile(PairA, 5245);
        WriteFile(PairB, 6776);
        ReturnsVerdict(passed: true, reason: "ok");

        await VerifyAsync((PairA, 0), (PairB, 1));

        Assert.Contains("dupPairs=1", ProbeLine);
        Assert.DoesNotContain(PairA, ProbeLine);
        Assert.DoesNotContain(PairB, ProbeLine);
    }

    // A sink cannot tell SensitiveDebug from LogInformation, so only the source text proves the channel.
    [Fact]
    public void TheDuplicateFactsAreLoggedThroughTheSensitiveChannelOnly()
    {
        var path = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Pia.Wpf", "Services", "AgentVerifier.cs"));
        Assert.True(File.Exists(path), path);
        var source = File.ReadAllText(path);

        Assert.Contains("SensitiveDebug(\"Artifact probe facts:", source, StringComparison.Ordinal);
        Assert.All(
            LoggerCalls(source),
            call => Assert.DoesNotContain("duplicate", call, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Each <c>_logger.Log*</c> call up to its closing paren, so a multi-line template is covered too.</summary>
    private static List<string> LoggerCalls(string source)
    {
        var calls = new List<string>();
        for (var i = source.IndexOf("_logger.Log", StringComparison.Ordinal); i >= 0;
             i = source.IndexOf("_logger.Log", i + 1, StringComparison.Ordinal))
        {
            var end = source.IndexOf(");", i, StringComparison.Ordinal);
            calls.Add(end < 0 ? source[i..] : source[i..end]);
        }
        Assert.NotEmpty(calls);
        return calls;
    }

    private async Task<VerdictResult> VerifyAsync(params (string Artifact, int Ordinal)[] reported)
    {
        var ctx = new RunContext("build a thing", RunProfile.Interactive);
        foreach (var group in reported.GroupBy(r => r.Ordinal).OrderBy(g => g.Key))
        {
            var artifacts = group.Select(g => g.Artifact).ToList();
            ctx.RecordStep(
                new AgentStep
                {
                    Ordinal = group.Key,
                    Title = "S" + group.Key,
                    Intent = "do " + group.Key,
                    // A step producing two files declares one and reports the other: same step, two artifacts.
                    ExpectedArtifact = artifacts.Count > 1 ? artifacts[0] : null,
                },
                new StepTurnResult(true, false, null, "did it", null, Guid.NewGuid(), Guid.NewGuid(),
                    Outcome: new StepOutcomeClaim(true, "done", artifacts[^1])));
        }

        var verifier = new AgentVerifier(_ai, _settingsService, _log);
        return await verifier.VerifyAsync(ctx, Persona(), Provider(), TestContext.Current.CancellationToken);
    }

    private void WriteFile(string relativePath, int bytes)
    {
        var full = Path.Combine(_dir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, new string('x', bytes));
    }

    private string LastPrompt => _systemPrompts[^1];

    private string ProbeLine =>
        Assert.Single(_log.Entries, e => e.Message.Contains("Artifact probe: ", StringComparison.Ordinal)).Message;

    private int DupPairs
    {
        get
        {
            var line = ProbeLine;
            var token = line[(line.IndexOf("dupPairs=", StringComparison.Ordinal) + "dupPairs=".Length)..];
            return int.Parse(token.Trim(), System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    private static AiProvider Provider() => new() { Name = "P", Endpoint = "https://x", ProviderType = AiProviderType.OpenAI };
    private static Persona Persona() => new() { Name = "Pia", SystemPrompt = "sys" };

    private static async IAsyncEnumerable<ChatStreamItem> VerdictStream(
        ToolCallHandler? handler, Dictionary<string, object?> emitArgs)
    {
        if (handler is not null)
            await handler(new FunctionCallContent(Guid.NewGuid().ToString(), "emit_verdict", emitArgs), new ToolDispatchContext(1));
        await Task.Yield();
        yield return new Finished(null, "test-model");
    }

    private void ReturnsVerdict(bool passed, string reason)
    {
        var emitArgs = new Dictionary<string, object?>
        {
            ["passed"] = passed,
            ["reason"] = reason,
            ["missing"] = Array.Empty<object?>(),
        };
        _ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<string?>(), cancellationToken: Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                _systemPrompts.Add(ci.ArgAt<IList<ChatMessage>>(0)[0].Text ?? string.Empty);
                return VerdictStream(ci.ArgAt<ToolCallHandler?>(3), emitArgs);
            });
    }
}
