using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Models;
using Pia.Services;
using Xunit;

namespace Pia.Tests.Services;

// No Microsoft.Agents.AI.Compaction type appears here: the experimental MAAI001 surface is contained inside
// AgentContextCompactor.cs, which is why this file compiles without a pragma.
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

    // More than four non-system groups on purpose: the library short-circuits at one included non-system group and
    // floors at its minimum-preserved count, so a smaller fixture would pass vacuously.
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
        // The conservatism lives on two thresholds that cannot throw rather than on a scaled-down window, which
        // would turn an 8k/8k settings typo into a failed step.
        Assert.Equal(0.45, AgentContextCompactor.ToolEvictionThreshold);
        Assert.Equal(0.70, AgentContextCompactor.TruncationThreshold);
        Assert.True(
            AgentContextCompactor.TruncationThreshold >= AgentContextCompactor.ToolEvictionThreshold,
            "the compaction strategy constructor throws when truncation < tool eviction");
    }

    [Fact]
    public void ImageTokenCharge_StaysAtTheRecordedBound()
    {
        // A BOUND, not a measurement: pinnedCost is subtracted from the window, so an under-charged pin leaves a
        // LARGER input budget and errs toward overflowing the context rather than toward compacting.
        Assert.Equal(3500, AgentContextCompactor.ImageTokenCharge);
        Assert.True(
            AgentContextCompactor.ImageTokenCharge >= 1568 * 1568 / 750,
            "the charge must bound the largest image Pia can send (ImageAttachmentProcessor.MaxLongEdge = 1568)");
    }

    [Fact]
    public async Task NullBudget_ReturnsTheSameInstancesInOrder()
    {
        // An unconfigured provider is every provider after upgrade, so this is the opt-in default.
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
        // The reference assertion exists so a future package bump that starts cloning ChatMessages fails loudly
        // here rather than silently changing what the provider sees.
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
        // A first-step request: the library cannot remove anything from it, so a run whose goal alone overflows the
        // window still fails — compaction cannot fix an oversized goal.
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
        // 12 prior steps, not the 8-step default: the default fixture is under the truncation trigger and comes
        // back unchanged.
        var messages = AgentStepShapedMessages(priorSteps: 12);

        var result = await AgentContextCompactor.CompactAsync(
            messages, AgentContextBudget.From(Provider(8_000, 2_000)), Logger, TestContext.Current.CancellationToken);

        Assert.True(
            result.Count < messages.Count,
            $"an over-budget agent step must shrink, but {messages.Count} messages came back as {result.Count}");

        // Without the pin the library's first casualty is the goal.
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
        // A second fixture so "over budget" is pinned by two independent knobs, history length and window size: a
        // future threshold or estimator change cannot leave both green by accident.
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
        // The pinned prefix must be charged ONCE: counting it both as pinnedCost and in the list the library scores
        // made a 2000-token system prompt cost 4000, and evicted history that fit.
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

    // The tool rounds are appended AFTER the step instruction, so it is no longer the most recent group — which is
    // what used to get it evicted.
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
        // With only the head pinned the step instruction was evicted while the run goal stayed, so the model
        // continued against the whole goal and the verifier's ExpectedArtifact check failed on the wrong artifact.
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

        // Not asserting "both messages survive": the library collapses each call+result pair into one synthesized
        // assistant message. What must hold is that no provider sees a result without its call, or the reverse.
        Assert.Equal(callIds, resultIds);
    }

    [Fact]
    public async Task MaxOutputAtOrAboveWindow_DegradesToUncompacted()
    {
        // The pre-check: a provider whose max output equals its window yields no budget.
        Assert.Null(AgentContextBudget.From(Provider(8_192, 8_192)));
        Assert.Null(AgentContextBudget.From(Provider(8_192, 16_384)));

        // The catch: the same numbers passed directly make the strategy constructor throw, and the step must still
        // go out uncompacted — a settings typo cannot fail a step.
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

    // Only the BYTE COUNT matters: the library scores a DataContent at raw bytes / 4, so 300 KB reports ~75k tokens.
    private static byte[] Jpeg()
    {
        var data = new byte[300 * 1024];
        Random.Shared.NextBytes(data);
        return data;
    }

    // The shape AssistantMessage.ToChatMessage builds: one fused ChatMessage, which is the unit the pin protects —
    // there is no such thing as a bare image message on either executor path.
    private static ChatMessage ImageTurn(ChatRole role, string text) =>
        new(role, [new TextContent(text), new DataContent(Jpeg(), "image/jpeg")]);

    [Fact]
    public async Task ImageAttachment_MidList_IsPinnedRatherThanEvicted()
    {
        // A 300 KB JPEG reports ~75k phantom tokens at the library's bytes/4 and the tokenizer seam is internal,
        // which is why the fix is a PIN rather than a threshold. Withheld, this fixture comes back unchanged.
        var image = ImageTurn(ChatRole.User, "here is the screenshot");

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "system"),
            new(ChatRole.User, "THE GOAL: describe the screenshot."),
            image,
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

        // Filtered with plain LINQ so no Enumerable.Any() lands inside an assertion argument.
        var withImages = result.Where(m => m.Contents.OfType<DataContent>().Any()).ToList();
        var survivor = Assert.Single(withImages);
        Assert.Same(image, survivor);
        Assert.Contains("here is the screenshot", survivor.Text);

        var imageAt = result.FindIndex(m => ReferenceEquals(m, image));
        var instructionAt = result.FindIndex(m => ReferenceEquals(m, messages[^1]));
        Assert.True(
            imageAt >= 0 && imageAt < instructionAt,
            $"the pinned image must precede the instruction, but landed at {imageAt} against {instructionAt}");
    }

    [Fact]
    public async Task OverBudget_ReattachesThePinnedImageJustBeforeTheInstruction()
    {
        // The image case at a size where compaction really runs. pinnedCost is ~3500 above the text-only fixtures
        // because the image is charged, and the JPEG's own bytes never reach the library — the turn is withheld.
        var image = ImageTurn(ChatRole.User, "screenshot of the failing test");

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "You are Pia, an agent."),
            new(ChatRole.User, "THE GOAL: explain the screenshot."),
        };
        for (var i = 1; i <= 6; i++)
            messages.Add(new ChatMessage(ChatRole.Assistant, $"step {i} reply: {Bulk(500)}"));
        messages.Add(image);
        for (var i = 7; i <= 12; i++)
            messages.Add(new ChatMessage(ChatRole.Assistant, $"step {i} reply: {Bulk(500)}"));
        messages.Add(new ChatMessage(ChatRole.User, "Execute step 13"));

        var result = await AgentContextCompactor.CompactAsync(
            messages, AgentContextBudget.From(Provider(8_000, 2_000)), Logger, TestContext.Current.CancellationToken);

        Assert.True(
            result.Count < messages.Count,
            $"this fixture must be over budget or it proves nothing, but {messages.Count} messages came back as {result.Count}");

        // The tail pin group, in order: the image, then the instruction about it, last.
        Assert.Same(image, result[^2]);
        Assert.Same(messages[^1], result[^1]);

        Assert.Same(messages[0], result[0]);
        Assert.Contains("THE GOAL", result[1].Text);
    }

    [Fact]
    public async Task ChargingThePinnedImage_TipsAFixtureThatUsedToFit_IntoCompaction()
    {
        // Both halves run the SAME history and provider; the only difference is a DataContent fused onto the pinned
        // instruction. Keep their priorSteps IDENTICAL if this ever needs retuning — the differential is the point.
        var budget = AgentContextBudget.From(Provider(8_000, 2_000));

        var textOnly = AgentStepShapedMessages();
        var textOnlyResult = await AgentContextCompactor.CompactAsync(
            textOnly, budget, Logger, TestContext.Current.CancellationToken);
        Assert.True(
            textOnlyResult.Count == textOnly.Count,
            $"the text-only half must stay UNDER budget or this differential proves nothing, but {textOnly.Count} messages came back as {textOnlyResult.Count}; if this fails, drop BOTH halves to priorSteps: 6");

        var withImage = AgentStepShapedMessages();
        withImage[^1] = ImageTurn(ChatRole.User, withImage[^1].Text);

        var withImageResult = await AgentContextCompactor.CompactAsync(
            withImage, budget, Logger, TestContext.Current.CancellationToken);

        Assert.True(
            withImageResult.Count < withImage.Count,
            $"charging the pinned image must tip this fixture over budget, but {withImage.Count} messages came back as {withImageResult.Count}");

        Assert.Same(withImage[^1], withImageResult[^1]);
        Assert.Contains("Execute step 9", withImageResult[^1].Text);
        Assert.Contains(withImageResult[^1].Contents, c => c is DataContent);
    }

    [Fact]
    public async Task ManyImages_ArePinnedNewestFirstUnderTheSubCap()
    {
        // Pinning BOTH images would charge 7000 of an 8000 window and trip the "pinned prefix leaves no input
        // budget" early return, sending everything uncompacted; the sub-cap admits the NEWEST image only.
        var older = ImageTurn(ChatRole.User, "the first screenshot");
        var newer = ImageTurn(ChatRole.User, "the second screenshot");

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "You are Pia, an agent."),
            new(ChatRole.User, "THE GOAL: compare the screenshots."),
        };
        for (var i = 1; i <= 6; i++)
            messages.Add(new ChatMessage(ChatRole.Assistant, $"step {i} reply: {Bulk(500)}"));
        messages.Add(older);
        for (var i = 7; i <= 12; i++)
            messages.Add(new ChatMessage(ChatRole.Assistant, $"step {i} reply: {Bulk(500)}"));
        messages.Add(newer);
        messages.Add(new ChatMessage(ChatRole.User, "Execute step 13"));

        var result = await AgentContextCompactor.CompactAsync(
            messages, AgentContextBudget.From(Provider(8_000, 2_000)), Logger, TestContext.Current.CancellationToken);

        Assert.True(
            result.Count < messages.Count,
            $"the sub-cap must keep this request compactable, but {messages.Count} messages came back as {result.Count}");

        // Newest-first. What becomes of the OLDER image is the library's decision, not Pia's, and is deliberately
        // asserted in neither direction.
        Assert.Same(newer, result[^2]);
        Assert.Same(messages[^1], result[^1]);
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
        // Not every request Pia builds starts with a system message, so the split must not assume one. 12 replies
        // because at 8 this shape is under the truncation trigger and comes back unchanged.
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
        // The re-concatenation used to skip the pinned prefix by ROLE, which deleted every system message the
        // library returned — including a caller-placed non-leading one. It skips by reference identity now.
        const string reminder = "SYSTEM REMINDER: cite file paths in every step reply.";

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "You are Pia, an agent."),
            new(ChatRole.User, "THE GOAL: ship the context compaction batch."),
        };
        for (var i = 1; i <= 12; i++)
            messages.Add(new ChatMessage(ChatRole.Assistant, $"step {i} reply: {Bulk(500)}"));
        // Among the NEWEST groups: the library removes the oldest non-system groups first, so it is guaranteed to
        // hand this back and the only thing left to fail on is Pia's own splice.
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

        // Matched on TEXT rather than identity, so a future package version that returns a CLONE of the prefix —
        // which identity exclusion would no longer skip — fails here instead of sending the system prompt twice.
        Assert.Single(result, m => m.Text.Contains("You are Pia", StringComparison.Ordinal));

        Assert.Same(messages[0], result[0]);
        Assert.Contains("THE GOAL", result[1].Text);
        Assert.Contains("Execute step 13", result[^1].Text);
    }

    [Fact]
    public async Task OverBudget_EmitsThePinnedSystemPrefixExactlyOnce()
    {
        // The prefix is deliberately in BOTH lists — 'head' and the list handed to the library, so its grouping
        // still sees a System group — which makes the re-concatenation's skip the only thing keeping it out twice.
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
