using Pia.Services.Plugins;
using Xunit;

namespace Pia.Tests.Services;

public class McpToolResultCapTests
{
    [Fact]
    public void AResultWithinTheCap_IsReturnedUnchanged()
    {
        var text = new string('x', 100);

        Assert.Same(text, McpPluginToolHandler.CapResult(text, 100));
    }

    [Fact]
    public void ALongerResult_KeepsItsHead_AndNamesBothLengths()
    {
        var text = new string('x', 120) + new string('y', 80);

        var capped = McpPluginToolHandler.CapResult(text, 120);

        Assert.StartsWith(new string('x', 120) + "\n[truncated:", capped, StringComparison.Ordinal);
        Assert.DoesNotContain("y", capped.Split('\n')[0], StringComparison.Ordinal);
        Assert.Contains("first 120 of 200 characters", capped, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCut_NeverSplitsASurrogatePair()
    {
        var text = new string('x', 9) + "😀" + new string('x', 10);

        var capped = McpPluginToolHandler.CapResult(text, 10);

        Assert.StartsWith(new string('x', 9) + "\n", capped, StringComparison.Ordinal);
        Assert.Contains("first 9 of 21 characters", capped, StringComparison.Ordinal);
    }
}
