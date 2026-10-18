using Xunit;

namespace Pia.Tests.TestInfrastructure;

/// <summary>A start error reaches the release log, so it must name no path, not even through an inner exception.</summary>
public static class PathFreeAssert
{
    public static void IsPathFree(Exception ex)
    {
        Assert.Null(ex.InnerException);
        Assert.DoesNotMatch(@"[A-Za-z]:[\\/]", ex.Message);
        Assert.DoesNotMatch(@"\\\\[\w.$?-]", ex.Message);
    }
}
