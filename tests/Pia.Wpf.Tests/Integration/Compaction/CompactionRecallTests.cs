using System.IO;
using Pia.Models;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Integration.Compaction;

/// <summary>
/// The recall sweep. The bank-building half is ordinary gate tests — it needs no network, and it is the half
/// that decides whether a number means anything. The sweep itself is <see cref="LiveApiFactAttribute"/>, so a
/// default <c>dotnet test</c> reports it as Not Run and never reaches a provider.
/// </summary>
public class CompactionRecallTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pia-recall-" + Guid.NewGuid().ToString("N"));

    private static RecallBudget Small => CompactionRecallHarness.SmallWindow;

    [Fact]
    public async Task TheBank_AsksOnlyAboutContentTheCompactorRemoved()
    {
        var transcript = CompactionRecallHarness.SyntheticCorpus().First();
        var (retained, removed) = await CompactionRecallHarness.CompactAsync(
            transcript, Small, TestContext.Current.CancellationToken);

        Assert.NotEmpty(removed);

        var bank = await CompactionRecallHarness.BankAsync(
            transcript, Small, generator: null, TestContext.Current.CancellationToken, _dir);

        Assert.NotEmpty(bank);

        var retainedTrace = SyntheticTranscript.Trace(retained);
        var removedTrace = SyntheticTranscript.Trace(removed);

        foreach (var question in bank)
        {
            // Present in what was dropped and absent from what survived: either half missing makes the arm
            // comparison meaningless in a way no later assertion can detect.
            Assert.Equal(1, SyntheticTranscript.CountOccurrences(removedTrace, question.GoldAnswer));
            Assert.Equal(0, SyntheticTranscript.CountOccurrences(retainedTrace, question.GoldAnswer));
        }
    }

    /// <summary>The leak filter is what stops restatement luck, so its rejection has to be observable.</summary>
    [Fact]
    public async Task TheBank_RejectsAFactTheTailRestates()
    {
        var transcript = CompactionRecallHarness.SyntheticCorpus().First();
        var plain = await CompactionRecallHarness.BankAsync(
            transcript, Small, generator: null, TestContext.Current.CancellationToken, _dir);

        Assert.NotEmpty(plain);

        // Restate one dropped fact inside the message the tail pin protects; nothing else changes.
        var leaked = plain[0].GoldAnswer;
        var messages = transcript.Messages.ToList();
        messages[^1] = new Microsoft.Extensions.AI.ChatMessage(
            messages[^1].Role, $"{messages[^1].Text} (for the record: {leaked})");

        var restated = new RecallTranscript(
            transcript.Id + "-restated", transcript.Fingerprint + "r", messages, transcript.Facts);

        var filtered = await CompactionRecallHarness.BankAsync(
            restated, Small, generator: null, TestContext.Current.CancellationToken, _dir);

        Assert.DoesNotContain(leaked, filtered.Select(q => q.GoldAnswer));
        Assert.Equal(plain.Count - 1, filtered.Count);
    }

    /// <summary>
    /// The correction in the plan's §15: the removed set belongs to (transcript, budget), so a cache keyed on
    /// the transcript alone answers the second budget's questions from the first budget's removed set.
    /// </summary>
    [Fact]
    public async Task TheBankCache_IsKeyedOnTheBudgetAsWellAsTheTranscript()
    {
        var transcript = CompactionRecallHarness.SyntheticCorpus().First();
        var wide = new RecallBudget("wide", new AgentContextBudget(64_000, 4_000), "test");

        var small = await CompactionRecallHarness.BankAsync(
            transcript, Small, generator: null, TestContext.Current.CancellationToken, _dir);
        var loose = await CompactionRecallHarness.BankAsync(
            transcript, wide, generator: null, TestContext.Current.CancellationToken, _dir);

        Assert.Equal(2, Directory.GetFiles(_dir, "*.bank.json").Length);

        // A window this transcript fits under removes nothing, so its bank is empty — the clearest possible
        // proof that the two budgets do not share a key.
        Assert.NotEmpty(small);
        Assert.Empty(loose);
    }

    [LiveApiFact]
    public async Task OneQuestionEndToEnd_BeforeTheSweepSpendsAnything()
    {
        var provider = CompactionRecallHarness.ResolveProvider();
        if (provider is null)
        {
            Assert.Skip($"{CompactionRecallHarness.ProviderVariable} names no configured provider");
            return;
        }

        var transcript = CompactionRecallHarness.SyntheticCorpus().First();
        var bank = await CompactionRecallHarness.BankAsync(
            transcript, Small, generator: null, TestContext.Current.CancellationToken, _dir);

        Assert.NotEmpty(bank);

        var arm = await CompactionRecallHarness.RunArmAsync(
            "A:uncompacted", transcript.Messages, [bank[0]], provider, provider, TestContext.Current.CancellationToken);

        // The whole transcript is in context, so a miss here is the harness, the prompt or the judge - never
        // compaction. Two calls to learn that before spending 240.
        Assert.Equal(1, arm.Answered);
        Assert.True(arm.Score > 0, $"the uncompacted arm could not answer a question about text it still holds ({CompactionRecallHarness.Percent(arm.Score)})");
    }

    [LiveApiFact]
    public async Task ArmsAandB_OnTheSyntheticCorpus_AtTheSmallWindow()
    {
        await SweepAsync(Small);
    }

    /// <summary>
    /// The second measurement the plan's §15 asks for. It skips rather than inventing a window: with
    /// <c>MaxContextWindowTokens</c> unset on every configured provider, compaction never fires for that user
    /// at all, and a made-up number would read exactly like a measured one.
    /// </summary>
    [LiveApiFact]
    public async Task ArmsAandB_OnTheSyntheticCorpus_AtTheConfiguredWindow()
    {
        var provider = CompactionRecallHarness.ResolveProvider();
        var budget = CompactionRecallHarness.ConfiguredWindow(provider);
        if (budget is null)
        {
            Assert.Skip(
                $"no window to measure: {CompactionRecallHarness.WindowVariable} is unset and "
                + $"{provider?.Name ?? "the provider"} has no MaxContextWindowTokens");
            return;
        }

        await SweepAsync(budget);
    }

    private async Task SweepAsync(RecallBudget budget)
    {
        var answering = CompactionRecallHarness.ResolveProvider();
        if (answering is null)
        {
            Assert.Skip($"{CompactionRecallHarness.ProviderVariable} names no configured provider");
            return;
        }

        var judging = CompactionRecallHarness.ResolveProvider(CompactionRecallHarness.JudgeProviderVariable) ?? answering;
        var ct = TestContext.Current.CancellationToken;
        var rows = new List<RecallRow>();

        foreach (var transcript in CompactionRecallHarness.SyntheticCorpus())
        {
            var bank = await CompactionRecallHarness.BankAsync(transcript, budget, generator: null, ct);
            if (bank.Count == 0)
                continue;

            var (retained, _) = await CompactionRecallHarness.CompactAsync(transcript, budget, ct);

            var uncompacted = await CompactionRecallHarness.RunArmAsync(
                "A:uncompacted", transcript.Messages, bank, answering, judging, ct);
            var current = await CompactionRecallHarness.RunArmAsync(
                "B:current", retained, bank, answering, judging, ct);

            rows.Add(new RecallRow(transcript.Id, bank.Count, uncompacted, current));
        }

        Assert.NotEmpty(rows);

        var scorecard = CompactionRecallHarness.WriteScorecard("synthetic", budget, answering, judging, rows);
        TestContext.Current.TestOutputHelper?.WriteLine($"scorecard: {scorecard}");
        foreach (var row in rows)
        {
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"{row.TranscriptId}: bank {row.BankSize}, "
                + $"A {CompactionRecallHarness.Percent(row.Uncompacted.Score)} @ {row.Uncompacted.ApproximateTokens} tok, "
                + $"B {CompactionRecallHarness.Percent(row.Current.Score)} @ {row.Current.ApproximateTokens} tok");
        }

        // The plan's §11 stop rule, as an assertion rather than a note: below this the instrument is broken and
        // no B number may be read.
        var ceiling = rows.Average(r => r.Uncompacted.Score);
        Assert.True(
            ceiling >= 0.90,
            $"arm A averaged {CompactionRecallHarness.Percent(ceiling)} on content it still holds, so the bank or "
            + $"the judge is broken - fix the instrument before reading arm B. Scorecard: {scorecard}");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
