using System;
using System.Security.Cryptography;
using System.Text;
using Pia.Services;
using Xunit;

namespace Pia.Tests.Services;

public sealed class PkceCodesTests
{
    [Fact]
    public void Create_VerifierIs43UnpaddedBase64UrlCharacters()
    {
        var (verifier, _) = PkceCodes.Create();

        Assert.Equal(43, verifier.Length);
        Assert.All(verifier, c => Assert.True(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'));
    }

    [Fact]
    public void Create_ChallengeIsBase64UrlOfSha256OverTheVerifier()
    {
        var (verifier, challenge) = PkceCodes.Create();

        var expected = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.Equal(expected, challenge);
    }

    [Fact]
    public void ComputeChallenge_MatchesRfc7636AppendixB()
    {
        Assert.Equal(
            "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM",
            PkceCodes.ComputeChallenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));
    }

    [Fact]
    public void Create_TwoPairsDiffer()
    {
        var first = PkceCodes.Create();
        var second = PkceCodes.Create();

        Assert.NotEqual(first.Verifier, second.Verifier);
        Assert.NotEqual(first.Challenge, second.Challenge);
    }
}
