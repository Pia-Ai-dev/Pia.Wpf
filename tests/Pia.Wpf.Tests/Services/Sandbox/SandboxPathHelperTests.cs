using Pia.Services.Plugins.Sandbox;
using Xunit;

namespace Pia.Tests.Services.Sandbox;

public sealed class SandboxPathHelperTests
{
    [Theory]
    [InlineData(@"D:\x", @"D:\")]
    [InlineData(@"D:\x\y", @"D:\")]
    [InlineData("D:/x", "D:/")]
    [InlineData(@"D:\x", "D:/")]
    [InlineData("D:/x", @"D:\")]
    [InlineData(@"D:\", @"D:\")]
    [InlineData(@"D:\x", @"D:\x")]
    [InlineData(@"D:\x\", @"D:\x")]
    [InlineData(@"D:\x", @"D:\x\")]
    [InlineData(@"D:\x\y", @"D:\x\")]
    [InlineData(@"d:\X\y", @"D:\x")]
    [InlineData(@"C:\a/b", @"C:\a")]
    [InlineData(@"\\server\share\x", @"\\server\share")]
    [InlineData(@"\\server\share\x", @"\\server\share\")]
    public void IsSameOrUnder_HoldsForTheFolderItselfAndEverythingBelow(string path, string root)
    {
        Assert.True(SandboxLaunchSpecBuilder.IsSameOrUnder(path, root));
    }

    [Theory]
    [InlineData(@"D:\", @"D:\x")]
    [InlineData(@"D:\xy", @"D:\x")]
    [InlineData(@"D:\x", @"C:\")]
    [InlineData(@"E:\x", @"D:\")]
    [InlineData(@"\\server\shared", @"\\server\share")]
    [InlineData(@"\\server\share\x", "")]
    [InlineData(@"\\server\share\x", @"\")]
    [InlineData(@"C:\x", "   ")]
    public void IsSameOrUnder_IsFalseForASiblingAParentAndAnEmptyRoot(string path, string root)
    {
        Assert.False(SandboxLaunchSpecBuilder.IsSameOrUnder(path, root));
    }

    [Theory]
    [InlineData(@"D:\x", @"D:\", true)]
    [InlineData(@"D:\x\", @"D:\x", false)]
    [InlineData(@"D:\x", "D:/x/", false)]
    [InlineData(@"D:\", @"D:\", false)]
    [InlineData(@"D:\x\y", @"D:\x\", true)]
    [InlineData(@"D:\xy", @"D:\x", false)]
    public void IsStrictlyUnder_LeavesOutTheFolderItself(string path, string root, bool expected)
    {
        Assert.Equal(expected, SandboxLaunchSpecBuilder.IsStrictlyUnder(path, root));
    }

    [Theory]
    [InlineData(@"D:\x", @"D:\x\", true)]
    [InlineData(@"D:\x", "d:/X", true)]
    [InlineData(@"D:\", "D:/", true)]
    [InlineData(@"D:\x", @"D:\x\y", false)]
    [InlineData(@"D:\x", @"D:\xy", false)]
    public void SamePath_IgnoresCaseSlashesAndTrailingSeparators(string left, string right, bool expected)
    {
        Assert.Equal(expected, SandboxLaunchSpecBuilder.SamePath(left, right));
    }

    [Theory]
    [InlineData(@"C:\", true)]
    [InlineData("D:/", true)]
    [InlineData(@"\\fileserver\jdoe$", true)]
    [InlineData(@"\\fileserver\jdoe$\", true)]
    [InlineData(@"C:\Users", false)]
    [InlineData(@"\\fileserver\jdoe$\tools", false)]
    [InlineData("tools", false)]
    [InlineData("", false)]
    public void IsVolumeRoot_IsTrueOnlyForADriveOrShareRoot(string path, bool expected)
    {
        Assert.Equal(expected, SandboxLaunchSpecBuilder.IsVolumeRoot(path));
    }
}
