using System.IO;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Integration.ArtifactProbe;

/// <summary>The measurement script reimplements the file-shapedness rule in PowerShell, so both sides replay one committed case table and drift cannot pass unnoticed.</summary>
public sealed class DeclarationClassifierParityTests : IDisposable
{
    // Declarations past this many are skipped and never classified, so the table is replayed in chunks.
    private const int MaxReportedDeclarations = 20;

    /// <summary>Five levels up from the test binary: <c>bin/{config}/{tfm}</c> → project → <c>tests</c> → root.</summary>
    private static readonly string CasesPath = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "scripts", "artifact-declaration-cases.json"));

    private static readonly JsonSerializerOptions CaseJson = new(JsonSerializerDefaults.Web);

    private readonly IAiClientService _ai = Substitute.For<IAiClientService>();
    private readonly ISettingsService _settingsService = Substitute.For<ISettingsService>();
    private readonly AppSettings _settings = new();
    private readonly List<string> _systemPrompts = new();
    private readonly string _dir;

    public DeclarationClassifierParityTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "PiaTests_ArtifactParity_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _settings.AssistantFilesFolder = _dir;
        _settingsService.GetSettingsAsync().Returns(_ => Task.FromResult(_settings));
        ReturnsVerdict();
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private sealed record ParityTable(List<ParityCase>? Cases);

    private sealed record ParityCase(string Declaration, bool FileShaped);

    [Fact]
    public async Task DeclarationCorpus_EveryPinnedCase_ClassifiesAsTheTableSays()
    {
        Assert.True(File.Exists(CasesPath), $"the shared case table is missing: {CasesPath}");
        var table = JsonSerializer.Deserialize<ParityTable>(File.ReadAllText(CasesPath), CaseJson);
        var cases = table?.Cases ?? new List<ParityCase>();
        Assert.NotEmpty(cases);

        var mismatches = new List<string>();
        foreach (var chunk in cases.Chunk(MaxReportedDeclarations))
        {
            var prompt = await ProbeAsync(
                chunk.Select(c => c.Declaration).ToList(), TestContext.Current.CancellationToken);

            for (var i = 0; i < chunk.Length; i++)
            {
                // Located by step ordinal and title, never by the declaration text: some rows are echoed
                // truncated and some carry a tab or newline that is rewritten to a space.
                var line = FactLine(prompt, i);
                // Any arm other than "not a file reference" means the candidate list was non-empty — NOT FOUND,
                // the unresolvable arm and both budget-reached arms are all legitimately file-shaped.
                var fileShaped = !line.EndsWith(" → not a file reference", StringComparison.Ordinal);
                if (fileShaped != chunk[i].FileShaped)
                    mismatches.Add($"{chunk[i].Declaration} → expected {chunk[i].FileShaped}, got {fileShaped}");
            }
        }

        Assert.True(mismatches.Count == 0,
            "the shared case table and the verifier disagree:\n" + string.Join("\n", mismatches));
    }

    private static string FactLine(string prompt, int index)
    {
        var prefix = $"- step {index + 1} \"S{index}\" declared: ";
        return prompt.Split('\n').First(l => l.StartsWith(prefix, StringComparison.Ordinal)).TrimEnd('\r');
    }

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
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(), cancellationToken: Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                _systemPrompts.Add(ci.ArgAt<IList<ChatMessage>>(0)[0].Text ?? string.Empty);
                return VerdictStream(ci.ArgAt<ToolCallHandler?>(3));
            });
    }
}
