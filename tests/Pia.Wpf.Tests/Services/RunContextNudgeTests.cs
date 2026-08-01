using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// Batch 08 G7 (D4): <see cref="RunContext.SetNudge"/>/<see cref="RunContext.AppendNudge"/> in isolation —
/// the cap (head kept, matching <c>AgentPlanner.MaxAnalysisChars</c>'s shape), the flatten/trim, blank ⇒
/// null, and the exact fence text every executor and prompt builder is required to emit byte-for-byte
/// (<c>AgentRunNudgeParityTests</c> is where those call sites are actually exercised).
/// </summary>
public sealed class RunContextNudgeTests
{
    private static RunContext Ctx() => new("goal", RunProfile.Interactive);

    [Fact]
    public void AppendNudge_WithNoNudgeSet_ReturnsUserTextVerbatim()
    {
        var ctx = Ctx();

        Assert.Equal("do the thing", ctx.AppendNudge("do the thing"));
    }

    [Fact]
    public void AppendNudge_WithNudgeSet_WrapsInTheStatedFence()
    {
        var ctx = Ctx();
        ctx.SetNudge("focus on the CSV export");

        var appended = ctx.AppendNudge("do the thing");

        Assert.Equal(
            "do the thing\n\n"
            + "--- Steering note from the user (follow it for the remaining steps) ---\n"
            + "focus on the CSV export\n"
            + "--- end of steering note ---",
            appended);
    }

    /// <summary>
    /// The two ends of the over-long nudge fixture, the same shape <c>AgentPlannerTests</c> uses for
    /// <c>MaxAnalysisChars</c>: a homogeneous filler cannot say WHICH 1000 chars survived, so markers at
    /// each end make the truncation DIRECTION observable, not just its length.
    /// </summary>
    private const string NudgeHead = "HEAD-OF-NUDGE";
    private const string NudgeTail = "TAIL-OF-NUDGE";

    [Fact]
    public void Nudge_IsCappedHeadKept()
    {
        var ctx = Ctx();
        ctx.SetNudge(NudgeHead + new string('x', 5_000) + NudgeTail);

        var appended = ctx.AppendNudge("instruction");

        Assert.Contains(NudgeHead, appended);
        Assert.DoesNotContain(NudgeTail, appended);
        // Tight on purpose: the fence text + "instruction" + the capped 1000-char nudge (+ the trailing
        // ellipsis) bounds the whole appended string well under the un-capped 5000+26 chars it would be
        // without the cap — a regression that widens or drops MaxNudgeChars shows up as length, not just
        // as "still contains the head".
        Assert.True(appended.Length < 1_200, $"appended nudge was {appended.Length} chars");
    }

    [Fact]
    public void Nudge_IsFlattenedAndTrimmed_AndBlankBecomesNull()
    {
        var ctx = Ctx();
        // \r, \n and \t each become their OWN space (the same char-for-char replace
        // AgentRunService.NormalizeStepText uses) — a lone \n and a lone \t give an unambiguous one-space
        // result; \r\n is covered separately below, where it is expected to leave TWO spaces.
        ctx.SetNudge("  line one\nline two\ttabbed  ");

        var appended = ctx.AppendNudge("instruction");

        Assert.DoesNotContain("\r", appended);
        Assert.DoesNotContain("\n\n\n", appended); // no run of blank lines from an untranslated newline
        // No stray leading/trailing space from the untrimmed input surviving into the fence.
        Assert.Contains("---\nline one line two tabbed\n---", appended);
    }

    [Fact]
    public void Nudge_CrLf_FlattensToTwoSpaces_NeverALiteralCr()
    {
        // \r and \n are each replaced independently (never collapsed), so a Windows-style \r\n leaves TWO
        // spaces — the same char-for-char shape AgentRunService.NormalizeStepText produces. Stated as its
        // own fact so a future reader does not "fix" the double space into a single one and silently change
        // the shared shape both places rely on.
        var ctx = Ctx();
        ctx.SetNudge("line one\r\nline two");

        var appended = ctx.AppendNudge("instruction");

        Assert.DoesNotContain("\r", appended);
        Assert.Contains("line one  line two", appended);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n\t")]
    public void Nudge_BlankOrWhitespace_BecomesNull_NoFenceAtAll(string? blank)
    {
        var ctx = Ctx();
        ctx.SetNudge(blank);

        Assert.Null(ctx.Nudge);
        // A blank nudge must render NO fence at all, not an empty one — an empty-but-present fence would
        // still add two marker lines the model has to interpret as "the user said nothing".
        Assert.Equal("instruction", ctx.AppendNudge("instruction"));
    }
}
