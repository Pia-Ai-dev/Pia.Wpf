using System;
using Pia.Services;
using Xunit;

namespace Pia.Tests.Services;

public sealed class AuthServiceTokenExpiryTests
{
    private static readonly DateTime Now = new(2026, 8, 29, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void AccessTokenExpiryFrom_UsesTheServerLifetimeMinusAMinute()
    {
        Assert.Equal(Now.AddSeconds(900 - 60), AuthService.AccessTokenExpiryFrom(900, Now));
    }

    [Fact]
    public void AccessTokenExpiryFrom_ShortLifetime_KeepsAtLeastHalfOfIt()
    {
        Assert.Equal(Now.AddSeconds(15), AuthService.AccessTokenExpiryFrom(30, Now));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AccessTokenExpiryFrom_MissingLifetime_FallsBackToFourteenMinutes(int expiresIn)
    {
        Assert.Equal(Now.AddMinutes(14), AuthService.AccessTokenExpiryFrom(expiresIn, Now));
    }
}
