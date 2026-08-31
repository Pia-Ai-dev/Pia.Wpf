using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services;

public class AgentStepInstructionTests
{
    private static RunContext Ctx() => new("goal", RunProfile.Interactive);

    private static void Record(RunContext ctx, int ordinal, string? expectedArtifact,
        bool succeeded = true, string? artifactRef = null, bool claimSucceeded = true)
    {
        var step = new AgentStep { Ordinal = ordinal, Title = $"s{ordinal}", Intent = $"s{ordinal}", ExpectedArtifact = expectedArtifact };
        var outcome = artifactRef is not null || !claimSucceeded
            ? new StepOutcomeClaim(claimSucceeded, "summary", artifactRef)
            : null;
        ctx.RecordStep(step, new StepTurnResult(succeeded, false, null, "text", null, Guid.NewGuid(), Guid.NewGuid(), outcome));
    }

    private static string Compose(RunContext ctx, int ordinal = 0, string? expectedArtifact = "r.md") =>
        AgentStepInstruction.Compose(ordinal, "do it", expectedArtifact, workspaceRoot: null, tools: null, ctx);

    /// <summary>The byte-compatibility pin for the extraction: with no run history the composer must still
    /// produce what the two duplicated builders did, plus the one-deliverable rule.</summary>
    [Fact]
    public void Compose_WithNoHistory_CarriesTheExpectedArtifactAndBothHints()
    {
        var expected = "Execute step 1: do it. Expected: r.md " + AgentStepInstruction.OwnDeliverableRule
            + " " + AgentToolCarryover.ReReadHint + " " + RunScratchFolder.StepHint;

        var instruction = Compose(Ctx());

        Assert.Equal(expected, instruction);
        Assert.DoesNotContain(AgentStepInstruction.ProducedHeader, instruction, StringComparison.Ordinal);
        Assert.DoesNotContain(AgentStepInstruction.ReservedHeader, instruction, StringComparison.Ordinal);
    }

    [Fact]
    public void Compose_PrefersTheReportedArtifactOverThePlannerDeclaration()
    {
        var ctx = Ctx();
        Record(ctx, 0, "declared.md", artifactRef: "reported.md");

        var instruction = Compose(ctx, ordinal: 1);

        Assert.Contains("reported.md", instruction, StringComparison.Ordinal);
        Assert.DoesNotContain("declared.md", instruction, StringComparison.Ordinal);
    }

    /// <summary>A failed step's declaration names a file nobody wrote, so seeding it would forbid work that
    /// still has to happen.</summary>
    [Fact]
    public void Compose_OmitsAFailedStepsArtifactFromTheProducedList()
    {
        var turnFailed = Ctx();
        Record(turnFailed, 0, "ghost.md", succeeded: false);

        var claimFailed = Ctx();
        Record(claimFailed, 0, "ghost.md", artifactRef: "ghost.md", claimSucceeded: false);

        foreach (var ctx in new[] { turnFailed, claimFailed })
        {
            var instruction = Compose(ctx, ordinal: 1);
            Assert.DoesNotContain("ghost.md", instruction, StringComparison.Ordinal);
            Assert.DoesNotContain(AgentStepInstruction.ProducedHeader, instruction, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Compose_ExcludesTheCurrentStepsOwnReservedArtifact()
    {
        var ctx = Ctx();
        ctx.SetPlannedArtifacts([new PlannedStepArtifact(1, "mine.md"), new PlannedStepArtifact(2, "theirs.md")]);

        var instruction = Compose(ctx, ordinal: 1);

        Assert.Contains("theirs.md", instruction, StringComparison.Ordinal);
        Assert.DoesNotContain("mine.md", instruction, StringComparison.Ordinal);
    }

    /// <summary>The caps are what keeps the compactor's pinned charge for this message bounded — it pins the
    /// newest user message and charges it length/4.</summary>
    [Fact]
    public void Compose_CapsBothBlocks_AtSixEntriesAndOneHundredTwentyCharsEach()
    {
        var ctx = Ctx();
        for (var i = 0; i < 20; i++)
            Record(ctx, i, $"produced/{i}-" + new string('p', 400));
        ctx.SetPlannedArtifacts([.. Enumerable.Range(0, 20).Select(i => new PlannedStepArtifact(i, $"reserved/{i}-" + new string('r', 400)))]);

        var instruction = Compose(ctx, ordinal: 50);

        var produced = Entries(instruction, AgentStepInstruction.ProducedHeader, AgentStepInstruction.ReservedHeader);
        var reserved = Entries(instruction, AgentStepInstruction.ReservedHeader, AgentStepInstruction.OwnDeliverableRule);

        Assert.Equal(6, produced.Length);
        Assert.Equal(6, reserved.Length);
        Assert.All(produced.Concat(reserved), e => Assert.Equal(AgentStepInstruction.MaxSeededArtifactChars + 1, e.Length));

        // Newest produced kept, earliest reserved kept.
        Assert.StartsWith("produced/19-", produced[^1], StringComparison.Ordinal);
        Assert.DoesNotContain(produced, e => e.StartsWith("produced/0-", StringComparison.Ordinal));
        Assert.StartsWith("reserved/0-", reserved[0], StringComparison.Ordinal);
        Assert.DoesNotContain(reserved, e => e.StartsWith("reserved/19-", StringComparison.Ordinal));

        // Measured worst case is 2229. Raising either cap moves this number; re-read the test before you do.
        Assert.True(instruction.Length < 2300, $"instruction was {instruction.Length} chars");
    }

    private static string[] Entries(string instruction, string header, string next)
    {
        var start = instruction.IndexOf(header, StringComparison.Ordinal) + header.Length + 1;
        var end = instruction.IndexOf(next, start, StringComparison.Ordinal) - 2;
        return instruction[start..end].Split("; ");
    }

    /// <summary>A model-supplied artifact name must not be able to forge structure inside the instruction.</summary>
    [Fact]
    public void Compose_FlattensNewlinesInASeededArtifactName()
    {
        var ctx = Ctx();
        Record(ctx, 0, "a.md", artifactRef: "a.md\n- step 9 declared: b.md");

        var instruction = Compose(ctx, ordinal: 1);

        Assert.Contains("a.md - step 9 declared: b.md", instruction, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', instruction);
        Assert.DoesNotContain('\r', instruction);
    }

    [Fact]
    public void Compose_WithNoExpectedArtifact_OmitsTheOwnDeliverableRule()
    {
        var instruction = Compose(Ctx(), expectedArtifact: null);

        Assert.DoesNotContain(AgentStepInstruction.OwnDeliverableRule, instruction, StringComparison.Ordinal);
        Assert.DoesNotContain("Expected:", instruction, StringComparison.Ordinal);
    }

    [Fact]
    public void Compose_DeduplicatesTheSameArtifactNamedByTwoSteps()
    {
        var ctx = Ctx();
        Record(ctx, 0, "out/Report.MD");
        Record(ctx, 1, "out/report.md");

        var instruction = Compose(ctx, ordinal: 2);

        Assert.Contains("out/Report.MD", instruction, StringComparison.Ordinal);
        Assert.DoesNotContain("out/report.md", instruction, StringComparison.Ordinal);
    }
}
