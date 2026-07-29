using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Models;
using Pia.Services;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// Unit coverage for the compaction adapter. Execution is deferred to Windows/CI — net10.0-windows
/// cannot run on the machine these were written on.
/// <para>
/// No Microsoft.Agents.AI.Compaction type appears here: the experimental (MAAI001) surface is
/// contained inside AgentContextCompactor.cs, and these tests exercise it through Pia-only types.
/// That containment is part of what is being asserted, implicitly, by this file compiling without a
/// pragma.
/// </para>
/// </summary>
public class AgentContextCompactorTests
{
    private static readonly NullLogger Logger = NullLogger.Instance;

    private static string Bulk(int approximateTokens) => new('x', approximateTokens * 4);

    private static AiProvider Provider(int? window, int? maxOutput) => new()
    {
        Name = "Budgeted",
        Endpoint = "https://example.invalid/v1",
        MaxContextWindowTokens = window,
        MaxOutputTokens = maxOutput,
    };

    /// <summary>
    /// The shape a Headless agent step actually sends: a system prompt, the run goal, the replies of
    /// every previous step, then the ephemeral "execute step N" instruction. More than four
    /// non-system groups on purpose — the library short-circuits at one included non-system group
    /// and floors at its minimum-preserved count, so a smaller fixture would pass vacuously.
    /// <para>
    /// SIZING (measured against Microsoft.Agents.AI 1.15.0, not derived): at window 8000 / max output
    /// 2000 the eight-reply default is NOT over budget — 8 × 2014 chars / 4 = 4028 estimated tokens
    /// against a truncation trigger of 0.70 × (8000 − 2000 − pinnedCost) ≈ 4190 — so a shrink
    /// assertion on it fails (measured in=11, out=11). Tests that need real truncation pass
    /// <c>priorSteps: 12</c> (measured in=15, out=11) or a 6000 window (in=11, out=8). The 0.45 tool
    /// eviction trigger does fire on the default fixture, but it has no ToolCall group to evict.
    /// </para>
    /// </summary>
    private static List<ChatMessage> AgentStepShapedMessages(int priorSteps = 8, int replyTokens = 500)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "You are Pia, an agent."),
            new(ChatRole.User, "THE GOAL: ship the context compaction batch."),
        };

        for (var i = 1; i <= priorSteps; i++)
            messages.Add(new ChatMessage(ChatRole.Assistant, $"step {i} reply: {Bulk(replyTokens)}"));

        messages.Add(new ChatMessage(ChatRole.User, $"Execute step {priorSteps + 1}"));
        return messages;
    }

    [Fact]
    public void Thresholds_StayAtTheRecordedConservatism()
    {
        // The 30% conservatism the design chose lives HERE — on two thresholds that cannot throw —
        // rather than on a scaled-down window, which turns an 8k/8k settings typo into a failed
        // step. If someone moves these, they are re-opening that decision.
        Assert.Equal(0.45, AgentContextCompactor.ToolEvictionThreshold);
        Assert.Equal(0.70, AgentContextCompactor.TruncationThreshold);
        Assert.True(
            AgentContextCompactor.TruncationThreshold >= AgentContextCompactor.ToolEvictionThreshold,
            "the compaction strategy constructor throws when truncation < tool eviction");
    }

    [Fact]
    public async Task NullBudget_ReturnsTheSameInstancesInOrder()
    {
        // An unconfigured provider is every provider after upgrade. This is the opt-in default and
        // the upgrade-safety proof.
        var messages = AgentStepShapedMessages();

        var result = await AgentContextCompactor.CompactAsync(
            messages, AgentContextBudget.From(Provider(null, null)), Logger, TestContext.Current.CancellationToken);

        Assert.Equal(messages.Count, result.Count);
        for (var i = 0; i < messages.Count; i++)
            Assert.Same(messages[i], result[i]);
    }

    [Fact]
    public async Task UnconfiguredProvider_YieldsNoBudgetAtAll()
    {
        Assert.Null(AgentContextBudget.From(Provider(null, null)));
        Assert.Null(AgentContextBudget.From(Provider(null, 4096)));
        Assert.Null(AgentContextBudget.From(Provider(0, 0)));
        Assert.Null(AgentContextBudget.From(null));

        // A window with no max output configured is usable: the whole window is input budget.
        var windowOnly = AgentContextBudget.From(Provider(128_000, null));
        Assert.NotNull(windowOnly);
        Assert.Equal(128_000, windowOnly!.Value.WindowTokens);
        Assert.Equal(0, windowOnly.Value.MaxOutputTokens);

        await Task.CompletedTask;
    }

    [Fact]
    public async Task UnderBudget_PassesThroughReferenceIdentical()
    {
        // Verified true of the library today. The reference assertion exists so a future package
        // bump that starts cloning ChatMessages fails loudly here instead of silently changing what
        // the provider sees.
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "system"),
            new(ChatRole.User, "goal"),
            new(ChatRole.Assistant, "step 1 reply"),
            new(ChatRole.Assistant, "step 2 reply"),
            new(ChatRole.Assistant, "step 3 reply"),
            new(ChatRole.User, "Execute step 4"),
        };

        var result = await AgentContextCompactor.CompactAsync(
            messages, AgentContextBudget.From(Provider(128_000, 4_096)), Logger, TestContext.Current.CancellationToken);

        Assert.Equal(messages.Count, result.Count);
        for (var i = 0; i < messages.Count; i++)
            Assert.Same(messages[i], result[i]);
    }

    [Fact]
    public async Task ShortList_IsNeverCompacted_EvenFarOverBudget()
    {
        // A first-step request. The library cannot remove anything from it, and a run whose goal
        // alone overflows the window still fails exactly as it does today — compaction cannot fix an
        // oversized goal.
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "system"),
            new(ChatRole.User, $"THE GOAL: {Bulk(25_000)}"),
            new(ChatRole.User, "Execute step 1"),
        };

        var result = await AgentContextCompactor.CompactAsync(
            messages, AgentContextBudget.From(Provider(8_192, 2_048)), Logger, TestContext.Current.CancellationToken);

        Assert.Equal(messages.Count, result.Count);
        for (var i = 0; i < messages.Count; i++)
            Assert.Same(messages[i], result[i]);
    }

    [Fact]
    public async Task OverBudget_ShrinksButKeepsSystemAndGoalFirst()
    {
        // 12 prior steps, not the 8-step default: the default fixture is under the truncation trigger
        // and comes back unchanged (see AgentStepShapedMessages). Measured here: in=15, out=11.
        var messages = AgentStepShapedMessages(priorSteps: 12);

        var result = await AgentContextCompactor.CompactAsync(
            messages, AgentContextBudget.From(Provider(8_000, 2_000)), Logger, TestContext.Current.CancellationToken);

        Assert.True(
            result.Count < messages.Count,
            $"an over-budget agent step must shrink, but {messages.Count} messages came back as {result.Count}");

        // The pin. Without it the library's first casualty is the goal — verified empirically before
        // the splice existed.
        Assert.Equal(ChatRole.System, result[0].Role);
        Assert.Same(messages[0], result[0]);
        Assert.Equal(ChatRole.User, result[1].Role);
        Assert.Same(messages[1], result[1]);
        Assert.Contains("THE GOAL", result[1].Text);

        // The ephemeral step instruction is the most recent message and must survive too.
        Assert.Contains(result, m => m.Text.Contains("Execute step 13", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OverBudget_OnASmallerWindow_AlsoShrinks()
    {
        // The other half of the sizing note: the same eight-reply fixture DOES truncate once the window
        // is small enough for it to be over budget. Measured: in=11, out=8. Kept as a second fixture so
        // "over budget" is pinned by two independent knobs (history length and window size) — a future
        // threshold or estimator change cannot leave both green by accident.
        var messages = AgentStepShapedMessages();

        var result = await AgentContextCompactor.CompactAsync(
            messages, AgentContextBudget.From(Provider(6_000, 2_000)), Logger, TestContext.Current.CancellationToken);

        Assert.True(
            result.Count < messages.Count,
            $"an over-budget agent step must shrink, but {messages.Count} messages came back as {result.Count}");
        Assert.Same(messages[0], result[0]);
        Assert.Contains("THE GOAL", result[1].Text);
        Assert.Contains(result, m => m.Text.Contains("Execute step 9", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnderBudgetWithALargeSystemPrompt_EvictsNothing()
    {
        // The pinned prefix must be charged ONCE. It used to be subtracted from the window as pinnedCost
        // AND left in the list the library counts, so a 2000-token system prompt cost 4000 and compaction
        // evicted history that fit with thousands of tokens to spare. Measured before: in=11, out=10 at
        // ~800 tokens of history and out=6 at ~2000. Measured now: unchanged, both times.
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, Bulk(2_000)),
            new(ChatRole.User, "THE GOAL: do the thing."),
        };
        for (var i = 1; i <= 8; i++)
            messages.Add(new ChatMessage(ChatRole.Assistant, $"step {i}: {Bulk(250)}"));
        messages.Add(new ChatMessage(ChatRole.User, "Execute step 9"));

        var result = await AgentContextCompactor.CompactAsync(
            messages, AgentContextBudget.From(Provider(8_000, 2_000)), Logger, TestContext.Current.CancellationToken);

        // 2000 (system) + ~2000 (history) input + 2000 reserved output = ~6000 of 8000 — nothing to evict.
        Assert.Equal(messages.Count, result.Count);
        for (var i = 0; i < messages.Count; i++)
            Assert.Same(messages[i], result[i]);
    }

    /// <summary>
    /// What the IN-STEP tool loop sends on round N: the step-shaped request plus the assistant/tool
    /// exchanges the loop appended AFTER the step instruction. The instruction is no longer the most
    /// recent group, which is what used to get it evicted.
    /// </summary>
    private static List<ChatMessage> ToolLoopShapedMessages(int rounds, int priorSteps = 8)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "You are Pia, an agent."),
            new(ChatRole.User, "THE GOAL: audit the repo."),
        };
        for (var i = 1; i <= priorSteps; i++)
            messages.Add(new ChatMessage(ChatRole.Assistant, $"step {i} reply: {Bulk(700)}"));
        messages.Add(new ChatMessage(ChatRole.User,
            $"Execute step {priorSteps + 1}: write the audit report. Expected: report.md"));

        for (var r = 0; r < rounds; r++)
        {
            var callId = $"call_{r}";
            messages.Add(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent(callId, "read_file", new Dictionary<string, object?> { ["path"] = $"/f{r}" })]));
            messages.Add(new ChatMessage(ChatRole.Tool,
                [new FunctionResultContent(callId, $"{{\"text\":\"{Bulk(1_500)}\"}}")]));
        }

        return messages;
    }

    [Theory]
    [InlineData(3, 4_000, 1_000)]
    [InlineData(5, 8_000, 2_000)]
    [InlineData(8, 8_000, 2_000)]
    public async Task InStepToolLoop_KeepsTheStepInstruction_NotJustTheGoal(int rounds, int window, int maxOutput)
    {
        // The regression this fixture exists for: with only the head pinned, every one of these three
        // configurations came back as [system, goal, assistant, tool, assistant, tool] — the step
        // instruction GONE while the run goal stayed. The model is then asked to continue with no
        // statement of which step or artifact it is producing, answers against the whole run goal, and
        // AgentVerifier's ExpectedArtifact check fails on the wrong artifact.
        var messages = ToolLoopShapedMessages(rounds);

        var result = await AgentContextCompactor.CompactAsync(
            messages, AgentContextBudget.From(Provider(window, maxOutput)), Logger, TestContext.Current.CancellationToken);

        Assert.True(result.Count < messages.Count, "this fixture must be over budget or it proves nothing");
        Assert.Contains("THE GOAL", result[1].Text);
        Assert.Contains(result, m => m.Text.Contains("Execute step 9", StringComparison.Ordinal));
        // It goes back LAST: the loop appended rounds behind it, so the pinned instruction is re-attached
        // at the end of the request rather than at the position it no longer has neighbours for.
        Assert.Contains("Execute step 9", result[^1].Text);
        Assert.Same(messages[10], result[^1]);
    }

    [Fact]
    public async Task InStepToolLoop_StillNeverOrphansAToolCall()
    {
        // The tail pin must not break pairing: it withholds a USER message, so no call/result group is
        // split, and the re-attached instruction sits after the last tool result (a valid shape).
        var messages = ToolLoopShapedMessages(rounds: 6);

        var result = await AgentContextCompactor.CompactAsync(
            messages, AgentContextBudget.From(Provider(8_000, 2_000)), Logger, TestContext.Current.CancellationToken);

        var callIds = result.SelectMany(m => m.Contents.OfType<FunctionCallContent>()).Select(c => c.CallId).ToHashSet();
        var resultIds = result.SelectMany(m => m.Contents.OfType<FunctionResultContent>()).Select(c => c.CallId).ToHashSet();
        Assert.Equal(callIds, resultIds);
    }

    [Fact]
    public async Task OverBudget_NeverOrphansAToolCallOrItsResult()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "system"),
            new(ChatRole.User, "THE GOAL: inspect the repository."),
        };

        for (var i = 0; i < 6; i++)
        {
            var callId = $"call_{i}";
            messages.Add(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent(callId, "list_files", new Dictionary<string, object?> { ["path"] = $"/dir{i}" })]));
            messages.Add(new ChatMessage(ChatRole.Tool,
                [new FunctionResultContent(callId, $"{{\"files\":\"{Bulk(600)}\"}}")]));
        }

        messages.Add(new ChatMessage(ChatRole.Assistant, "final answer"));

        var result = await AgentContextCompactor.CompactAsync(
            messages, AgentContextBudget.From(Provider(4_000, 1_000)), Logger, TestContext.Current.CancellationToken);

        var callIds = result
            .SelectMany(m => m.Contents.OfType<FunctionCallContent>())
            .Select(c => c.CallId)
            .ToHashSet();
        var resultIds = result
            .SelectMany(m => m.Contents.OfType<FunctionResultContent>())
            .Select(c => c.CallId)
            .ToHashSet();

        // Deliberately NOT asserting "both messages survive": the library collapses each
        // call+result pair into one synthesized assistant text message, so a message-identity
        // assertion would fail for the wrong reason and read like a real bug. What must hold is
        // that no provider ever sees a result without its call, or a call without its result.
        Assert.Equal(callIds, resultIds);
    }

    [Fact]
    public async Task MaxOutputAtOrAboveWindow_DegradesToUncompacted()
    {
        // Case 1 — the pre-check: a provider whose max output equals its window yields no budget.
        Assert.Null(AgentContextBudget.From(Provider(8_192, 8_192)));
        Assert.Null(AgentContextBudget.From(Provider(8_192, 16_384)));

        // Case 2 — the catch: a budget constructed directly with the same numbers makes the
        // strategy constructor throw ArgumentOutOfRangeException. The step must still go out, just
        // uncompacted. This is the assertion that proves a settings typo cannot fail a step.
        var messages = AgentStepShapedMessages();

        var result = await AgentContextCompactor.CompactAsync(
            messages, new AgentContextBudget(8_192, 8_192), Logger, TestContext.Current.CancellationToken);

        Assert.Equal(messages.Count, result.Count);
        for (var i = 0; i < messages.Count; i++)
            Assert.Same(messages[i], result[i]);
    }

    [Fact]
    public async Task NegativeWindow_DegradesToUncompacted()
    {
        var messages = AgentStepShapedMessages();

        var result = await AgentContextCompactor.CompactAsync(
            messages, new AgentContextBudget(-1, 0), Logger, TestContext.Current.CancellationToken);

        Assert.Equal(messages.Count, result.Count);
    }

    [Fact]
    public async Task ImageAttachment_DoesNotEvictTheGoal()
    {
        // The library scores a DataContent at raw bytes / 4, so a 300 KB JPEG reports ~75k phantom
        // tokens on a list whose text is a handful of tokens. That inflation is unfixable from here
        // (the tokenizer seam is internal), which is exactly why the pin exists rather than trusting
        // the thresholds.
        //
        // KNOWN OPEN HAZARD, measured, deliberately NOT asserted either way: on this fixture the
        // attachment MESSAGE itself is evicted (in=7 -> out=6, no DataContent survives), so a
        // multi-step Live agent run on a provider with a window loses the image from step 2 onward
        // while the goal that refers to it stays pinned — a confidently wrong answer instead of a
        // failure. Fixing it means pinning arbitrary mid-list DataContent messages and inventing a
        // real token estimate for them (a provider counts a 300 KB JPEG at ~1-2k tokens, not 76k),
        // which is a bigger change than this file's splice. Not asserted as "image survives" (it
        // does not) and not asserted as "image is dropped" (that would freeze the bug in place).
        var jpeg = new byte[300 * 1024];
        Random.Shared.NextBytes(jpeg);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "system"),
            new(ChatRole.User, "THE GOAL: describe the screenshot."),
            new(ChatRole.User, [new DataContent(jpeg, "image/jpeg")]),
            new(ChatRole.Assistant, "step 1 reply"),
            new(ChatRole.Assistant, "step 2 reply"),
            new(ChatRole.Assistant, "step 3 reply"),
            new(ChatRole.User, "Execute step 4"),
        };

        var result = await AgentContextCompactor.CompactAsync(
            messages, AgentContextBudget.From(Provider(8_000, 2_000)), Logger, TestContext.Current.CancellationToken);

        Assert.Equal(ChatRole.System, result[0].Role);
        Assert.Same(messages[0], result[0]);
        Assert.Contains("THE GOAL", result[1].Text);
    }

    [Fact]
    public async Task Cancellation_IsRethrownRatherThanSwallowed()
    {
        // A cancelled run is stopping anyway; swallowing the cancel here would mask the stop.
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AgentContextCompactor.CompactAsync(
            AgentStepShapedMessages(), AgentContextBudget.From(Provider(8_000, 2_000)), Logger, cts.Token));
    }

    [Fact]
    public async Task NoSystemMessage_StillPinsTheGoal()
    {
        // Not every request Pia builds starts with a system message; the split must not assume one.
        // 12 replies for the same reason as OverBudget_ShrinksButKeepsSystemAndGoalFirst: at 8 replies
        // this shape is under the truncation trigger and comes back unchanged (measured in=10, out=10),
        // so the shrink assertion was failing for a fixture reason, not a code reason. Measured with 12:
        // in=14, out=10.
        var messages = new List<ChatMessage> { new(ChatRole.User, "THE GOAL: no system prompt here.") };
        for (var i = 1; i <= 12; i++)
            messages.Add(new ChatMessage(ChatRole.Assistant, $"step {i} reply: {Bulk(500)}"));
        messages.Add(new ChatMessage(ChatRole.User, "Execute step 13"));

        var result = await AgentContextCompactor.CompactAsync(
            messages, AgentContextBudget.From(Provider(8_000, 2_000)), Logger, TestContext.Current.CancellationToken);

        Assert.True(result.Count < messages.Count);
        Assert.Same(messages[0], result[0]);
        Assert.Contains("Execute step 13", result[^1].Text);
    }

    [Fact]
    public async Task OverBudget_KeepsANonLeadingSystemMessage()
    {
        // The re-concatenation used to filter 'kept' by ROLE to skip the pinned prefix it had already
        // emitted from 'head', so EVERY system message the library returned was deleted — including one a
        // caller placed after a user message, which is not in 'head' at all and lives only in the list
        // handed to the library. The skip is by reference identity now, and this reminder proves it.
        //
        // MEASURED against Microsoft.Agents.AI 1.15.0, not derived: in=16, 14 handed to the library,
        // kept=10 — of which TWO are system messages: the pinned prefix (reference-identical to
        // messages[0], which is what makes identity exclusion work) and this reminder. out=12 now; it was
        // 11, with the reminder silently gone, under the role filter.
        //
        // Over budget for the reason recorded on AgentStepShapedMessages: 12 replies of 2014 chars / 4 ≈
        // 6040 estimated tokens against a truncation trigger of 0.70 × (8000 − 2000 − pinnedCost ≈ 14) ≈
        // 4190. The same 12-reply / 8000 / 2000 fixture as OverBudget_ShrinksButKeepsSystemAndGoalFirst
        // (in=15, out=11) plus this one small message.
        //
        // The reminder sits among the NEWEST groups on purpose: Microsoft.Agents.AI documents
        // TruncationCompactionStrategy as "preserves system messages and removes the oldest non-system
        // message groups first", so the library handing it back is guaranteed twice over and the only
        // thing left for this test to fail on is Pia's own splice.
        const string reminder = "SYSTEM REMINDER: cite file paths in every step reply.";

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "You are Pia, an agent."),
            new(ChatRole.User, "THE GOAL: ship the context compaction batch."),
        };
        for (var i = 1; i <= 12; i++)
            messages.Add(new ChatMessage(ChatRole.Assistant, $"step {i} reply: {Bulk(500)}"));
        messages.Add(new ChatMessage(ChatRole.System, reminder));
        messages.Add(new ChatMessage(ChatRole.User, "Execute step 13"));

        var result = await AgentContextCompactor.CompactAsync(
            messages, AgentContextBudget.From(Provider(8_000, 2_000)), Logger, TestContext.Current.CancellationToken);

        Assert.True(
            result.Count < messages.Count,
            $"this fixture must be over budget or it proves nothing, but {messages.Count} messages came back as {result.Count}");

        // Present exactly once, and still a system message: not deleted, and not smuggled back inside
        // someone else's text.
        var survivor = Assert.Single(result, m => m.Text.Contains(reminder, StringComparison.Ordinal));
        Assert.Equal(ChatRole.System, survivor.Role);

        // The other half of trading a role filter for identity: the pinned prefix is in BOTH lists, so it
        // must still ship exactly once. Matched on TEXT rather than identity, so a future package version
        // that returns a CLONE of the prefix — which identity exclusion would no longer skip — fails here
        // instead of quietly sending the system prompt twice.
        Assert.Single(result, m => m.Text.Contains("You are Pia", StringComparison.Ordinal));

        // The pins are unaffected by the switch.
        Assert.Same(messages[0], result[0]);
        Assert.Contains("THE GOAL", result[1].Text);
        Assert.Contains("Execute step 13", result[^1].Text);
    }

    [Fact]
    public async Task OverBudget_EmitsThePinnedSystemPrefixExactlyOnce()
    {
        // The prefix is deliberately in BOTH lists — 'head', and the list handed to the library so its
        // grouping still sees a System group — so the skip in the re-concatenation is the only thing that
        // keeps it from shipping twice. A GUARD, not a regression test: it held under the role filter too,
        // and it fails the moment identity exclusion degrades into "keep everything", or the library starts
        // returning a CLONE of the prefix instead of the caller's instance. Measured in=15, out=11 both
        // before and after the change — kept's only system message here IS the pinned one, so skipping by
        // role and skipping by identity remove the same single message on this fixture.
        var messages = AgentStepShapedMessages(priorSteps: 12);

        var result = await AgentContextCompactor.CompactAsync(
            messages, AgentContextBudget.From(Provider(8_000, 2_000)), Logger, TestContext.Current.CancellationToken);

        Assert.True(
            result.Count < messages.Count,
            $"this fixture must be over budget or it proves nothing, but {messages.Count} messages came back as {result.Count}");

        var system = Assert.Single(result, m => m.Role == ChatRole.System);
        Assert.Same(messages[0], system);
        Assert.Same(messages[0], result[0]);
    }
}
