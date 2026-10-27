namespace Pia.Tests.Services.E2EE;

using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services.E2EE;
using Pia.Services.Interfaces;
using Pia.Shared.E2EE;
using Xunit;

// Revoking a device and overwriting a wrapped key change what an account trusts, so both go through the device signer.
public class DeviceManagementServiceSignedCallsTests
{
    private readonly IDeviceRequestSigner _signer = Substitute.For<IDeviceRequestSigner>();

    [Fact]
    public async Task Revoking_IsASignedBodylessPost()
    {
        Answer(HttpStatusCode.OK);

        await CreateSut().RevokeDeviceAsync("dev-x");

        await _signer.Received(1).SendAsync(
            Arg.Any<HttpClient>(), HttpMethod.Post, "api/e2ee/devices/dev-x/revoke", null, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(E2EEErrorCodes.SignatureRequired)]
    [InlineData(E2EEErrorCodes.InvalidSignature)]
    [InlineData(E2EEErrorCodes.DeviceNotActive)]
    public async Task Revoking_RefusedForTheSignature_SaysSo(string code)
    {
        Answer(HttpStatusCode.Forbidden, code);

        var ex = await Assert.ThrowsAsync<DeviceSignatureRefusedException>(
            () => CreateSut().RevokeDeviceAsync("dev-x"));

        Assert.Equal(code, ex.Code);
    }

    [Fact]
    public async Task Revoking_RefusedForAnotherReason_IsAnHttpFailure()
    {
        Answer(HttpStatusCode.Forbidden, E2EEErrorCodes.DeviceRevoked);

        await Assert.ThrowsAsync<HttpRequestException>(() => CreateSut().RevokeDeviceAsync("dev-x"));
    }

    private void Answer(HttpStatusCode status, string? code = null) =>
        _signer.SendAsync(
                Arg.Any<HttpClient>(), Arg.Any<HttpMethod>(), Arg.Any<string>(), Arg.Any<byte[]?>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => new HttpResponseMessage(status)
            {
                Content = new StringContent(
                    code is null ? "" : JsonSerializer.Serialize(new { error = code }), Encoding.UTF8, "application/json"),
            });

    private DeviceManagementService CreateSut()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings { ServerUrl = "https://sync.example" });
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient());
        var auth = Substitute.For<IAuthService>();
        auth.GetAccessTokenAsync().Returns("token-1");

        return new DeviceManagementService(
            Substitute.For<IE2EEService>(), Substitute.For<IDeviceKeyService>(), Substitute.For<IRecoveryCodeService>(),
            settings, auth, factory, Substitute.For<IConfirmedApproverStore>(), _signer, TimeProvider.System,
            NullLogger<DeviceManagementService>.Instance);
    }
}
