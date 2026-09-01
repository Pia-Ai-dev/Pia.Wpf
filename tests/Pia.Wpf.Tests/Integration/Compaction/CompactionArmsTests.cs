using System.IO;
using Microsoft.Extensions.AI;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Integration.Compaction;

/// <summary>
/// The mechanics of arms C, D and E, in the default gate: no network, no provider, no cost. Every fact here
/// decides whether the live sweep's number can be READ, which is why they are ordinary <c>[Fact]</c>s rather
/// than part of the sweep — a broken arm has to be visible without spending anything.
/// </summary>
public class CompactionArmsTests
{
    private static RecallBudget Small => CompactionRecallHarness.SmallWindow;

    private static async Task<(RecallTranscript Transcript, List<ChatMessage> Retained, List<ChatMessage> Removed)>
        CompactFirstAsync(CancellationToken ct)
    {
        var transcript = CompactionRecallHarness.SyntheticCorpus().First();
        var (retained, removed) = await CompactionRecallHarness.CompactAsync(transcript, Small, ct);
        return (transcript, retained, removed);
    }

    // ---- arm C -----------------------------------------------------------------------------------

    /// <summary>
    /// PRE-REGISTERED, before any live call: the extractor is written against the plan's candidate list, and on
    /// this corpus that list reaches five of the six planted kinds. A <c>Decision</c> is a sentence — "switch
    /// stage 05 to the columnar writer" — with no identifier in it, so a mechanical index cannot carry it. Two
    /// of the fifteen facts are decisions, which fixes the ceiling at 13 before the sweep can be argued with.
    /// </summary>
    [Fact]
    public async Task ArmC_TheAnchorBlock_CarriesEveryPlantedKindExceptAProseDecision()
    {
        var ct = TestContext.Current.CancellationToken;
        var (transcript, _, removed) = await CompactFirstAsync(ct);
        var block = CompactionArms.AnchorBlock(transcript.Messages, removed);

        var dropped = transcript.Facts
            .Where(f => removed.Any(m => ReferenceEquals(m, transcript.Messages[f.MessageIndex])))
            .ToList();

        var missing = dropped.Where(f => !block.Contains(f.Answer, StringComparison.Ordinal)).ToList();

        Assert.All(missing, f => Assert.Equal(PlantedFactKind.Decision, f.Kind));
        Assert.Equal(
            dropped.Count(f => f.Kind == PlantedFactKind.Decision),
            missing.Count);
    }

    /// <summary>
    /// The association, which is most of what makes an anchor answerable: every line names the ORIGINAL
    /// transcript position its values came from. A flat bag of identifiers would score differently for reasons
    /// the arm is not testing.
    /// </summary>
    [Fact]
    public async Task ArmC_EveryAnchorLine_CitesItsSourceMessagePosition()
    {
        var ct = TestContext.Current.CancellationToken;
        var (transcript, _, removed) = await CompactFirstAsync(ct);
        var block = CompactionArms.AnchorBlock(transcript.Messages, removed);

        var lines = block.Split(Environment.NewLine).Skip(1).Where(l => l.Length > 0).ToList();
        Assert.NotEmpty(lines);

        foreach (var line in lines)
        {
            Assert.StartsWith("#", line);
            var ordinal = int.Parse(line[1..line.IndexOf(' ')]);
            // -1 is the "not found in the transcript" sentinel; a real position is what makes the line citable.
            Assert.InRange(ordinal, 0, transcript.Messages.Count - 1);
        }
    }

    /// <summary>Arm C only adds; it must not drop anything the compactor kept, or its score would be measuring
    /// two changes.</summary>
    [Fact]
    public async Task ArmC_KeepsEveryRetainedMessage_AndAppendsExactlyOneBlock()
    {
        var ct = TestContext.Current.CancellationToken;
        var (transcript, retained, removed) = await CompactFirstAsync(ct);

        var armC = CompactionArms.AnchorIndex(transcript.Messages, retained, removed);

        Assert.Equal(retained.Count + 1, armC.Count);
        Assert.All(retained, m => Assert.Contains(m, armC));
    }

    /// <summary>The extractor must not be reading the generator's answers — it has to find the same shapes in
    /// text the corpus never produced, or arm C is measuring a tuned regex.</summary>
    [Theory]
    [InlineData("wrote it to /srv/data/export-2026.csv today", "/srv/data/export-2026.csv")]
    [InlineData("failed with ORA-01555 on the second pass", "ORA-01555")]
    [InlineData("call read_file first, then patch", "read_file")]
    [InlineData("the shard grew to 12.5 GB overnight", "12.5 GB")]
    [InlineData("it printed \"connection reset by peer\" twice", "connection reset by peer")]
    [InlineData("run id 3f2504e0-4f89-11d3-9a0c-0305e82c3301 is the one", "3f2504e0-4f89-11d3-9a0c-0305e82c3301")]
    public void ArmC_TheExtractor_FindsThePlansCandidateShapes_InTextTheCorpusNeverWrote(string sentence, string expected)
    {
        var found = CompactionArms.Extract(new ChatMessage(ChatRole.Assistant, sentence));

        Assert.Contains(expected, found);
    }

    [Fact]
    public void ArmC_TheExtractor_ReadsAToolNameAndCallIdOffTheContent_NotOutOfItsRendering()
    {
        var call = new ChatMessage(ChatRole.Assistant, [
            new FunctionCallContent("call-77", "probe_pipeline", new Dictionary<string, object?>
            {
                ["path"] = "/workspace/out/stage.json",
            }),
        ]);

        var found = CompactionArms.Extract(call);

        Assert.Contains("probe_pipeline", found);
        Assert.Contains("call-77", found);
        Assert.Contains("/workspace/out/stage.json", found);
    }

    // ---- arm D -----------------------------------------------------------------------------------

    [Fact]
    public async Task ArmD_TheSearch_FindsARemovedFact_ByItsOwnGoldAnswer()
    {
        var ct = TestContext.Current.CancellationToken;
        var (transcript, _, removed) = await CompactFirstAsync(ct);

        var fact = transcript.Facts.First(f =>
            removed.Any(m => ReferenceEquals(m, transcript.Messages[f.MessageIndex])));

        var hits = CompactionArms.Search(transcript.Messages, removed, fact.Answer);

        // The pointer promises the dropped region is searchable. If it is not, arm D measures a lie.
        var hit = Assert.Single(hits);
        Assert.Equal(fact.MessageIndex, hit.Ordinal);
        Assert.Contains(fact.Answer, hit.Snippet);
    }

    [Fact]
    public async Task ArmD_TheSearch_NeverReturnsARetainedMessage()
    {
        var ct = TestContext.Current.CancellationToken;
        var (transcript, retained, removed) = await CompactFirstAsync(ct);

        // "stage" appears throughout, retained and removed alike — the scoping has to come from the removed
        // set, not from the term being rare.
        var hits = CompactionArms.Search(transcript.Messages, removed, "stage", limit: 100);

        Assert.NotEmpty(hits);
        var retainedOrdinals = retained
            .Select(m => Array.FindIndex([.. transcript.Messages], x => ReferenceEquals(x, m)))
            .ToHashSet();
        Assert.All(hits, h => Assert.DoesNotContain(h.Ordinal, retainedOrdinals));
    }

    [Theory]
    [InlineData("SEARCH: PIA-E4003", "PIA-E4003")]
    [InlineData("search: PIA-E4003", "PIA-E4003")]
    [InlineData("SEARCH: \"stage 07 summary\"\nmore text", "stage 07 summary")]
    [InlineData("I cannot find it. SEARCH: probe_stage_04", "probe_stage_04")]
    [InlineData("SEARCH: term.", "term")]
    public void ArmD_ASearchReply_YieldsItsTerm(string reply, string expected)
    {
        Assert.Equal(expected, CompactionArms.SearchTerm(reply));
    }

    [Theory]
    [InlineData("PIA-E4003")]            // an answer, not a request
    [InlineData("UNKNOWN")]
    [InlineData("SEARCH:")]              // asked to search for nothing
    [InlineData("SEARCH:   ")]
    public void ArmD_AnAnswerIsNotMistakenForASearch(string reply)
    {
        Assert.Null(CompactionArms.SearchTerm(reply));
    }

    /// <summary>A search that found nothing and a search that never ran must not look the same to the model, or
    /// arm D's second round is indistinguishable from its first.</summary>
    [Fact]
    public void ArmD_AnEmptyResult_SaysSoRatherThanRenderingNothing()
    {
        var rendered = CompactionArms.RenderHits("nothing-matches-this", []);

        Assert.Contains("no matches", rendered);
    }

    [Fact]
    public async Task ArmD_ThePointer_KeepsEveryRetainedMessage_AndNamesHowManyWereDropped()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, retained, removed) = await CompactFirstAsync(ct);

        var armD = CompactionArms.RecoveryPointer(retained, removed.Count);

        Assert.Equal(retained.Count + 1, armD.Count);
        Assert.Contains(removed.Count.ToString(), armD[^1].Text);
    }

    // ---- arm E -----------------------------------------------------------------------------------

    [Fact]
    public async Task ArmE_WithholdsEveryUserMessage_NotJustTheHeadAndTheNewest()
    {
        var ct = TestContext.Current.CancellationToken;
        var (transcript, retained, _) = await CompactFirstAsync(ct);

        var armE = CompactionArms.PinAllUserMessages(transcript.Messages, retained);

        var allUser = transcript.Messages.Where(m => m.Role == ChatRole.User).ToList();
        Assert.All(allUser, m => Assert.Contains(m, armE));

        // The premise: the shipped compactor drops middle user messages, so arm E has to be strictly larger.
        // If this ever reds, Pia already pins them and arm E has nothing to measure.
        Assert.True(
            allUser.Count(u => !retained.Contains(u)) > 0,
            "the compactor retained every user message, so arm E is not a change");
        Assert.True(armE.Count > retained.Count);
    }

    [Fact]
    public async Task ArmE_IsOrderedByOriginalPosition_AndAddsNothingThatWasNotInTheTranscript()
    {
        var ct = TestContext.Current.CancellationToken;
        var (transcript, retained, _) = await CompactFirstAsync(ct);

        var armE = CompactionArms.PinAllUserMessages(transcript.Messages, retained);

        var positions = armE
            .Select(m => Array.FindIndex([.. transcript.Messages], x => ReferenceEquals(x, m)))
            .ToList();

        Assert.DoesNotContain(-1, positions);
        Assert.Equal(positions.OrderBy(p => p), positions);
    }

    // ---- the readability instrument --------------------------------------------------------------

    /// <summary>
    /// The number every appending arm's score has to be read against. Arm B is guaranteed zero by the leak
    /// filter; arms C, D and E all reintroduce text, so a score without this count cannot be told apart from
    /// the model reading an answer it was handed.
    /// </summary>
    [Fact]
    public async Task TheGoldAnswerCount_IsZeroForArmB_AndNonZeroForAnArmThatAppendsAnswers()
    {
        var ct = TestContext.Current.CancellationToken;
        var (transcript, retained, removed) = await CompactFirstAsync(ct);
        var bank = await CompactionRecallHarness.BankAsync(
            transcript, Small, generator: null, ct, Path.Combine(Path.GetTempPath(), "pia-arms-" + Guid.NewGuid().ToString("N")));

        Assert.NotEmpty(bank);
        Assert.Equal(0, CompactionArms.GoldAnswersPresent(retained, bank));

        var armC = CompactionArms.AnchorIndex(transcript.Messages, retained, removed);
        var armE = CompactionArms.PinAllUserMessages(transcript.Messages, retained);

        // Not asserted as a fixed number: the point is that the count exists and is reported, so a later
        // reading can say which arm was handed how much.
        Assert.True(CompactionArms.GoldAnswersPresent(armC, bank) > 0);
        Assert.InRange(CompactionArms.GoldAnswersPresent(armE, bank), 0, bank.Count);
    }

    /// <summary>
    /// The pre-registration, as a gate test rather than a paragraph: what each arm HOLDS, per transcript,
    /// before a single provider call. A score is only readable against these — an arm handed 13 of 15 answers
    /// verbatim is being asked whether it can read, and one handed 0 is being asked whether it can recall.
    /// Assertions are structural; the numbers themselves go to the test output for the reading to quote.
    /// </summary>
    [Fact]
    public async Task EveryArmsHoldingsAreReported_BeforeAnythingIsSpent()
    {
        var ct = TestContext.Current.CancellationToken;
        var cache = Path.Combine(Path.GetTempPath(), "pia-arms-" + Guid.NewGuid().ToString("N"));

        foreach (var transcript in CompactionRecallHarness.SyntheticCorpus())
        {
            var bank = await CompactionRecallHarness.BankAsync(transcript, Small, generator: null, ct, cache);
            var (retained, removed) = await CompactionRecallHarness.CompactAsync(transcript, Small, ct);
            var block = CompactionArms.AnchorBlock(transcript.Messages, removed);

            var armC = CompactionArms.AnchorIndex(transcript.Messages, retained, removed);
            var armD = CompactionArms.RecoveryPointer(retained, removed.Count);
            var armE = CompactionArms.PinAllUserMessages(transcript.Messages, retained);

            TestContext.Current.TestOutputHelper?.WriteLine(
                $"{transcript.Id}: bank {bank.Count}, removed {removed.Count}/{transcript.Messages.Count} msg"
                + $" | gold present — B {CompactionArms.GoldAnswersPresent(retained, bank)}"
                + $", C {CompactionArms.GoldAnswersPresent(armC, bank)}"
                + $", C-block-only {CompactionArms.GoldAnswersPresent(block, bank)}"
                + $", D {CompactionArms.GoldAnswersPresent(armD, bank)}"
                + $", E {CompactionArms.GoldAnswersPresent(armE, bank)}"
                + $" | tokens — B {CompactionRecallHarness.ApproximateTokens(retained)}"
                + $", C {CompactionRecallHarness.ApproximateTokens(armC)}"
                + $", E {CompactionRecallHarness.ApproximateTokens(armE)}"
                + $", A {CompactionRecallHarness.ApproximateTokens(transcript.Messages)}");

            // Arm D adds a pointer and no content, so it must hold exactly what arm B holds. If this ever
            // reds, arm D is smuggling in the answers it is supposed to have to go and search for.
            Assert.Equal(
                CompactionArms.GoldAnswersPresent(retained, bank),
                CompactionArms.GoldAnswersPresent(armD, bank));
        }

        TempPath.Remove(cache);
    }
}
