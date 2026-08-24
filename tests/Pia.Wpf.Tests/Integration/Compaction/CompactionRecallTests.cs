using System.IO;
using Microsoft.Extensions.AI;
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

    /// <summary>
    /// A zero can mean two very different things: the model said it could not find the fact, or the provider
    /// returned an empty body that scored like a refusal. Arm A cannot distinguish them - it has the fact -
    /// so three arm-B questions are asked here for the answer TEXT, which is what lets the reading say arm B
    /// refused rather than failed.
    /// </summary>
    [LiveApiFact]
    public async Task ArmB_Zero_IsARefusal_NotAnEmptyResponse()
    {
        var provider = CompactionRecallHarness.ResolveProvider();
        if (provider is null)
        {
            Assert.Skip($"{CompactionRecallHarness.ProviderVariable} names no configured provider");
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        var transcript = CompactionRecallHarness.SyntheticCorpus().First();
        var bank = await CompactionRecallHarness.BankAsync(transcript, Small, generator: null, ct, _dir);
        var (retained, _) = await CompactionRecallHarness.CompactAsync(transcript, Small, ct);

        var lines = new List<string>();
        var arm = await CompactionRecallHarness.RunArmAsync(
            "B:current", retained, [.. bank.Take(3)], provider, provider, ct, lines.Add);

        foreach (var line in lines)
            TestContext.Current.TestOutputHelper?.WriteLine(line);

        Assert.Equal(3, lines.Count);
        Assert.DoesNotContain("answer=<empty>", lines);
        Assert.Equal(0, arm.UnreadableVerdicts);
    }

    /// <summary>
    /// The control arm the plan does not have, and the number that decides whether arm B can be read at all:
    /// the same bank asked with NO context. The planted answers are formulaic (an error code is
    /// <c>PIA-E</c> plus an index), so a model that can extrapolate the pattern would score on arm B without
    /// recalling anything — a subtler cousin of the restatement luck the leak filter catches. A high score
    /// here does not fail compaction; it fails the instrument.
    /// </summary>
    [LiveApiFact]
    public async Task NoContextControl_ShowsWhetherTheBankIsGuessableWithoutTheTranscript()
    {
        var provider = CompactionRecallHarness.ResolveProvider();
        if (provider is null)
        {
            Assert.Skip($"{CompactionRecallHarness.ProviderVariable} names no configured provider");
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        var transcript = CompactionRecallHarness.SyntheticCorpus().First();
        var bank = await CompactionRecallHarness.BankAsync(transcript, Small, generator: null, ct, _dir);

        var control = await CompactionRecallHarness.RunArmAsync(
            "0:no-context", [], bank, provider, provider, ct);

        TestContext.Current.TestOutputHelper?.WriteLine(
            $"no-context control on {transcript.Id}: {CompactionRecallHarness.Percent(control.Score)} over {control.Answered} questions");

        Assert.True(
            control.Score <= 0.20,
            $"the bank is guessable without the transcript ({CompactionRecallHarness.Percent(control.Score)}), "
            + "so an arm B score is measuring pattern extrapolation rather than recall - randomise the planted "
            + "answers in SyntheticTranscript before reading any arm.");
    }

    [LiveApiFact]
    public async Task ArmsAandB_OnTheSyntheticCorpus_AtTheSmallWindow()
    {
        await SweepAsync(Small);
    }

    /// <summary>
    /// The second measurement the plan's §15 asks for. Two reasons it can have nothing to measure, and both
    /// are findings rather than failures: no window configured at all, or a window so wide the corpus fits
    /// under it. The second only became reachable once every provider started resolving one — before that,
    /// this always took the first branch.
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

        // Checked BEFORE the sweep spends anything. A window the whole corpus fits under removes nothing, so
        // every bank is empty and the sweep would otherwise reach its own Assert.NotEmpty(rows) and read as a
        // broken instrument — when what it actually found is that compaction never fires at this window.
        var ct = TestContext.Current.CancellationToken;
        var removals = 0;
        foreach (var transcript in CompactionRecallHarness.SyntheticCorpus())
        {
            var (_, removed) = await CompactionRecallHarness.CompactAsync(transcript, budget, ct);
            removals += removed.Count;
        }

        if (removals == 0)
        {
            Assert.Skip(
                $"{budget.Source} is {budget.Budget.WindowTokens} tokens and the whole synthetic corpus fits "
                + "under it, so compaction removes nothing and there is no recall to measure");
            return;
        }

        await SweepAsync(budget);
    }

    /// <summary>
    /// Every arm, one sweep. All of them together on purpose: the answering and judging model is part of the
    /// measurement, so an arm run on a different provider than its baseline cannot be compared to it — which is
    /// exactly why B4's Mistral numbers do not carry over to a DeepSeek run.
    /// </summary>
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
        var lost = new List<string>();

        foreach (var transcript in CompactionRecallHarness.SyntheticCorpus())
        {
            var bank = await CompactionRecallHarness.BankAsync(transcript, budget, generator: null, ct);
            if (bank.Count == 0)
                continue;

            var (retained, removed) = await CompactionRecallHarness.CompactAsync(transcript, budget, ct);

            var anchorIndex = CompactionArms.AnchorIndex(transcript.Messages, retained, removed);
            var anchorBlock = CompactionArms.AnchorBlock(transcript.Messages, removed);
            var pointer = CompactionArms.RecoveryPointer(retained, removed.Count);
            var pinnedUsers = CompactionArms.PinAllUserMessages(transcript.Messages, retained);

            // Per transcript, because one provider fault at the last of four would otherwise throw away every
            // call the first three already paid for - and a partial scorecard that SAYS it is partial beats no
            // scorecard at all.
            try
            {
                var arms = new List<ArmResult>
                {
                    await CompactionRecallHarness.RunArmAsync(
                        "A:uncompacted", transcript.Messages, bank, answering, judging, ct),
                    await CompactionRecallHarness.RunArmAsync(
                        "B:current", retained, bank, answering, judging, ct,
                        goldPresent: CompactionArms.GoldAnswersPresent(retained, bank)),
                    await CompactionRecallHarness.RunArmAsync(
                        "C:anchor-index", anchorIndex, bank, answering, judging, ct,
                        goldPresent: CompactionArms.GoldAnswersPresent(anchorIndex, bank)),
                    // The control that decides whether arm C's number is recall or reading: the same bank
                    // against the anchor block and NOTHING else. Not an arm — an instrument check, the way the
                    // no-context control is for arm B.
                    await CompactionRecallHarness.RunArmAsync(
                        "C0:anchors-only", [new ChatMessage(ChatRole.System, anchorBlock)], bank,
                        answering, judging, ct,
                        goldPresent: CompactionArms.GoldAnswersPresent(anchorBlock, bank)),
                    await CompactionRecallHarness.RunArmAsync(
                        "D:recovery-pointer", pointer, bank, answering, judging, ct,
                        recover: reply => CompactionArms.SearchTerm(reply) is { } term
                            ? CompactionArms.RenderHits(
                                term, CompactionArms.Search(transcript.Messages, removed, term))
                            : null,
                        goldPresent: CompactionArms.GoldAnswersPresent(pointer, bank)),
                    await CompactionRecallHarness.RunArmAsync(
                        "E:pin-all-user", pinnedUsers, bank, answering, judging, ct,
                        goldPresent: CompactionArms.GoldAnswersPresent(pinnedUsers, bank)),
                };

                rows.Add(new RecallRow(transcript.Id, bank.Count, arms));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lost.Add($"{transcript.Id}: {ex.GetType().Name} {ex.Message}");
            }
        }

        Assert.NotEmpty(rows);

        var scorecard = CompactionRecallHarness.WriteScorecard("synthetic", budget, answering, judging, rows);
        TestContext.Current.TestOutputHelper?.WriteLine($"scorecard: {scorecard}");
        foreach (var failure in lost)
            TestContext.Current.TestOutputHelper?.WriteLine($"LOST TRANSCRIPT {failure}");
        foreach (var row in rows)
        {
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"{row.TranscriptId}: bank {row.BankSize} | "
                + string.Join(" | ", row.Arms.Select(a =>
                    $"{a.Arm} {CompactionRecallHarness.Percent(a.Score)} @ {a.ApproximateTokens} tok"
                    + $" gold={a.GoldPresent}" + (a.Recovered > 0 ? $" searched={a.Recovered}" : string.Empty))));
        }

        // The plan's §11 stop rule, as an assertion rather than a note: below this the instrument is broken and
        // no other arm's number may be read.
        var ceiling = rows.Average(r => r.Ceiling.Score);
        Assert.True(
            ceiling >= 0.90,
            $"arm A averaged {CompactionRecallHarness.Percent(ceiling)} on content it still holds, so the bank or "
            + $"the judge is broken - fix the instrument before reading any other arm. Scorecard: {scorecard}");
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
