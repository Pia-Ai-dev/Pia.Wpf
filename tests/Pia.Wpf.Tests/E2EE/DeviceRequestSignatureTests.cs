namespace Pia.Tests.E2EE;

using System.Text;
using Pia.Shared.E2EE;
using Xunit;

// The server tests carry the same vector, so a drift on either side fails both.
public class DeviceRequestSignatureTests
{
    private const string Path = "/api/e2ee/devices/3f2504e0-4f89-41d3-9a0c-0305e82c3301/wrapped-umk";
    private const string DeviceId = "3f2504e0-4f89-41d3-9a0c-0305e82c3301";
    private const string ChallengeId = "ch_0123456789abcdef";
    private const string Body = """{"deviceId":"3f2504e0-4f89-41d3-9a0c-0305e82c3301","ciphertext":"AAEC"}""";
    private static readonly Guid UserId = Guid.Parse("8C1D2E3F-4A5B-4C6D-8E7F-901A2B3C4D5E");

    [Fact]
    public void Payload_MatchesTheSharedVector()
    {
        var payload = DeviceRequestSignature.Payload(
            "POST", Path, UserId, DeviceId, ChallengeId, Encoding.UTF8.GetBytes(Body));

        Assert.Equal(
            "pia-req-v1|POST|/api/e2ee/devices/3f2504e0-4f89-41d3-9a0c-0305e82c3301/wrapped-umk"
            + "|8c1d2e3f-4a5b-4c6d-8e7f-901a2b3c4d5e|3f2504e0-4f89-41d3-9a0c-0305e82c3301|ch_0123456789abcdef"
            + "|Dw9hTg2DSLDxjUhrYKagBBWyhEbKCHEQrel238af7Os=",
            Encoding.UTF8.GetString(payload));
    }

    [Fact]
    public void Payload_ForABodylessRequest_HashesTheEmptyArray()
    {
        var payload = DeviceRequestSignature.Payload(
            "post", "/api/e2ee/devices/3f2504e0-4f89-41d3-9a0c-0305e82c3301/revoke", UserId, DeviceId, ChallengeId, []);

        Assert.Equal(
            "pia-req-v1|POST|/api/e2ee/devices/3f2504e0-4f89-41d3-9a0c-0305e82c3301/revoke"
            + "|8c1d2e3f-4a5b-4c6d-8e7f-901a2b3c4d5e|3f2504e0-4f89-41d3-9a0c-0305e82c3301|ch_0123456789abcdef"
            + "|47DEQpj8HBSa+/TImW+5JCeuQeRkm5NMpJWZG3hSuFU=",
            Encoding.UTF8.GetString(payload));
    }
}
