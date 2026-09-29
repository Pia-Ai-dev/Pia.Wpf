namespace Pia.Tests.E2EE;

using System.Text.Json;
using Pia.Shared.E2EE;
using Xunit;

public class RecoveryActivationProofTests
{
    private const string SessionId = "c2Vzc2lvbi1pZC1mb3ItdGVzdA==";
    private const string ProofKeyBase64 = "phOEJR9lkjOF4YJxhf4nT9XmassAoNeahsxCoJFIiyc=";
    private const string ProofBase64 = "y+52MODeXs+WHJ+ElRnSblizJFBBznh7MVOnhS0eB1M=";

    private static readonly byte[] Umk = Convert.FromHexString(
        "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f");

    [Fact]
    public void DeriveProofKey_MatchesTheSharedVector()
    {
        Assert.Equal(ProofKeyBase64, Convert.ToBase64String(RecoveryActivationProof.DeriveProofKey(Umk)));
    }

    [Fact]
    public void Compute_MatchesTheSharedVector()
    {
        var proofKey = Convert.FromBase64String(ProofKeyBase64);

        Assert.Equal(ProofBase64, RecoveryActivationProof.Compute(proofKey, SessionId));
    }

    [Fact]
    public void Verify_AcceptsTheSharedVector()
    {
        Assert.True(RecoveryActivationProof.Verify(Convert.FromBase64String(ProofKeyBase64), SessionId, ProofBase64));
    }

    [Theory]
    [InlineData("z+52MODeXs+WHJ+ElRnSblizJFBBznh7MVOnhS0eB1M=")]
    [InlineData("y+52MODeXs+WHJ+ElRnSblizJFBBznh7MVOnhS0e")]
    [InlineData("not base64!")]
    [InlineData("")]
    public void Verify_RejectsAWrongOrMalformedProof(string proof)
    {
        Assert.False(RecoveryActivationProof.Verify(Convert.FromBase64String(ProofKeyBase64), SessionId, proof));
    }

    [Fact]
    public void Verify_RejectsTheRightProofWithBytesAppended()
    {
        var extended = Convert.ToBase64String([.. Convert.FromBase64String(ProofBase64), 0]);

        Assert.False(RecoveryActivationProof.Verify(Convert.FromBase64String(ProofKeyBase64), SessionId, extended));
    }

    [Fact]
    public void Verify_RejectsAProofForAnotherSession()
    {
        var proofKey = Convert.FromBase64String(ProofKeyBase64);

        Assert.False(RecoveryActivationProof.Verify(proofKey, "b3RoZXItc2Vzc2lvbg==", ProofBase64));
    }

    [Fact]
    public void Verify_RejectsAProofUnderAnotherKey()
    {
        var otherKey = RecoveryActivationProof.DeriveProofKey(new byte[32]);

        Assert.False(RecoveryActivationProof.Verify(otherKey, SessionId, ProofBase64));
    }

    [Fact]
    public void ProofKeyRequest_DefaultsToTheCurrentVersion()
    {
        var json = JsonSerializer.Serialize(new RecoveryProofKeyRequest { ProofKey = ProofKeyBase64 }, JsonSerializerOptions.Web);

        Assert.Equal($$"""{"proofKey":"{{ProofKeyBase64}}","version":1}""", json);
    }

    [Fact]
    public void StatusResponse_FromAServerWithoutTheField_ReportsNoProofKey()
    {
        var status = JsonSerializer.Deserialize<E2EEStatusResponse>(
            """{"isEnabled":true,"umkVersion":1,"hasRecoveryKey":true}""", JsonSerializerOptions.Web)!;

        Assert.False(status.HasRecoveryProofKey);
    }
}
