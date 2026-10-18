using Microsoft.Mxc.Sdk.V1;
using Pia.Services.Exceptions;
using Pia.Services.Plugins.Sandbox;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Services.Sandbox;

public sealed class MxcSpawnErrorsTests
{
    private const string MissingFolder =
        "BaseContainer is unavailable; DACL fallback requires write-DAC permission on "
        + @"'C:\Users\me\AppData\Local\Pia\McpSandbox\files', which the current user lacks "
        + "(Das System kann die angegebene Datei nicht finden. (os error 2)).";

    private const string AdminOwnedFolder =
        @"BaseContainer is unavailable; DACL fallback requires write-DAC permission on 'C:\Program Files\nodejs', "
        + "which the current user lacks (ERROR_ACCESS_DENIED (WRITE_DAC not granted)).";

    private const string MissingText =
        "The sandbox could not start the server: a folder it needs is missing. Open the server's settings and detect runtimes again.";

    private const string AdminOwnedText =
        "The sandbox could not start the server: Pia may not change the permissions of a folder it needs, because it belongs to an administrator.";

    /// <summary>Wrapped exactly as the launcher wraps it, so the assertion covers what reaches the log.</summary>
    private static SandboxStartException Wrapped(MxcException ex) => new(MxcSpawnErrors.Describe(ex));

    [Fact]
    public void AMissingFolder_MeasuredInGerman_BecomesThePathFreeMissingText()
    {
        var ex = new MxcException(ErrorCode.BackendUnavailable, MissingFolder);

        Assert.Equal(MissingText, MxcSpawnErrors.Describe(ex));
        PathFreeAssert.IsPathFree(Wrapped(ex));
    }

    [Fact]
    public void AMissingPath_OsError3_IsAMissingFolderToo()
    {
        Assert.Equal(MissingText,
            MxcSpawnErrors.Describe(ErrorCode.BackendUnavailable, @"no 'C:\x\y' (os error 3)", null));
    }

    [Fact]
    public void AnAdminOwnedFolder_AsMeasured_BecomesThePathFreeAdminText()
    {
        var ex = new MxcException(ErrorCode.BackendUnavailable, AdminOwnedFolder);

        Assert.Equal(AdminOwnedText, MxcSpawnErrors.Describe(ex));
        PathFreeAssert.IsPathFree(Wrapped(ex));
    }

    [Fact]
    public void AnAccessDenied_OsError5_IsAnAdminOwnedFolderToo()
    {
        Assert.Equal(AdminOwnedText,
            MxcSpawnErrors.Describe(ErrorCode.BackendUnavailable, @"open 'C:\x': Zugriff verweigert (os error 5)", null));
    }

    [Fact]
    public void AnyOtherCode_GivesTheGenericTextNamingTheCode()
    {
        var ex = new MxcException(ErrorCode.PolicyValidation, @"bad policy for 'C:\Users\me\Secret'");

        Assert.Equal("The sandbox could not start the server (PolicyValidation).", MxcSpawnErrors.Describe(ex));
        PathFreeAssert.IsPathFree(Wrapped(ex));
    }

    [Fact]
    public void BackendUnavailable_WithoutAKnownCause_IsGenericToo()
    {
        var ex = new MxcException(ErrorCode.BackendUnavailable, @"EnumeratePaths is not supported by this version of Windows on 'C:\x'");

        Assert.Equal("The sandbox could not start the server (BackendUnavailable).", MxcSpawnErrors.Describe(ex));
        PathFreeAssert.IsPathFree(Wrapped(ex));
    }

    [Fact]
    public void TheGenericText_CarriesMxcsRemediation()
    {
        var text = MxcSpawnErrors.Describe(ErrorCode.PolicyValidation, "x", "Remove the duplicate path.");

        Assert.Equal("The sandbox could not start the server (PolicyValidation). Remove the duplicate path.", text);
        PathFreeAssert.IsPathFree(new SandboxStartException(text));
    }

    [Theory]
    [InlineData(@"Grant access to 'C:\Tools\node'.")]
    [InlineData(@"Check \\server\share.")]
    public void ARemediationNamingAPath_IsLeftOut(string remediation)
    {
        var text = MxcSpawnErrors.Describe(ErrorCode.PolicyValidation, "x", remediation);

        Assert.Equal("The sandbox could not start the server (PolicyValidation).", text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void AnEmptyRemediation_AddsNothing(string? remediation)
    {
        Assert.Equal("The sandbox could not start the server (BackendError).",
            MxcSpawnErrors.Describe(ErrorCode.BackendError, "x", remediation));
    }
}
