using Pia.Services;
using Xunit;

namespace Pia.Tests.Services;

public class AgentStepInstructionTests
{
    /// <summary>The byte-compatibility pin for the extraction: outside a workspace the composer must still
    /// produce exactly what the two duplicated builders did.</summary>
    [Fact]
    public void Compose_WithNoWorkspace_IsTodaysStringExactly()
    {
        var expected = "Execute step 1: do it. Expected: r.md "
            + AgentToolCarryover.ReReadHint + " " + RunScratchFolder.StepHint;

        Assert.Equal(expected, AgentStepInstruction.Compose(0, "do it", "r.md", workspaceRoot: null, tools: null));
    }
}
