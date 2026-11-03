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

    [Fact]
    public async Task ReplacingThisDevicesCopy_IsASignedPostOfTheSerializedWrap()
    {
        Answer(HttpStatusCode.OK);
        byte[]? sent = null;
        _signer.SendAsync(
                Arg.Any<HttpClient>(), HttpMethod.Post, "api/e2ee/devices/dev-self/wrapped-umk",
                Arg.Do<byte[]?>(b => sent = b), Arg.Any<CancellationToken>())
            .Returns(_ => new HttpResponseMessage(HttpStatusCode.OK));

        await ActivateTheActiveDeviceViaRecoveryAsync();

        var blob = JsonSerializer.Deserialize<WrappedUmkBlob>(sent!, JsonSerializerOptions.Web)!;
        Assert.Equal("dev-self", blob.DeviceId);
        Assert.Equal("self-wrapped", blob.Ciphertext);
        Assert.Equal("dev-self", blob.CreatedByDeviceId);
    }

    [Fact]
    public async Task ReplacingThisDevicesCopy_RefusedForTheSignature_SaysSo()
    {
        Answer(HttpStatusCode.Forbidden, E2EEErrorCodes.SignatureRequired);

        await Assert.ThrowsAsync<DeviceSignatureRefusedException>(ActivateTheActiveDeviceViaRecoveryAsync);
    }

    private async Task ActivateTheActiveDeviceViaRecoveryAsync()
    {
        var keys = Substitute.For<IDeviceKeyService>();
        keys.GetDeviceId().Returns("dev-self");
        var e2ee = Substitute.For<IE2EEService>();
        e2ee.WrapUmkForSelf().Returns(("self-wrapped", "salt"));
        var recovery = Substitute.For<IRecoveryCodeService>();
        recovery.UnwrapUmkFromRecovery(Arg.Any<RecoveryWrappedUmkBlob>(), "code").Returns(new byte[32]);
        var server = new ActiveDeviceServer();

        await CreateSut(e2ee, keys, recovery, server).ActivateViaRecoveryAsync("code", "session");
    }

    // Answers the reads of a recovery for a device that is already Active; the write goes through the signer.
    private sealed class ActiveDeviceServer : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            object body = path.EndsWith("/status")
                ? new DeviceStatusResponse { DeviceId = "dev-self", Status = DeviceStatus.Active }
                : new RecoveryWrappedUmkBlob { Ciphertext = "c", KdfSalt = "s", UmkVersion = 1 };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(body, body.GetType(), JsonSerializerOptions.Web),
                    Encoding.UTF8, "application/json"),
            });
        }
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

    private DeviceManagementService CreateSut(
        IE2EEService? e2ee = null, IDeviceKeyService? keys = null, IRecoveryCodeService? recovery = null,
        HttpMessageHandler? server = null)
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings { ServerUrl = "https://sync.example" });
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>())
            .Returns(_ => server is null ? new HttpClient() : new HttpClient(server, disposeHandler: false));
        var auth = Substitute.For<IAuthService>();
        auth.GetAccessTokenAsync().Returns("token-1");

        return new DeviceManagementService(
            e2ee ?? Substitute.For<IE2EEService>(), keys ?? Substitute.For<IDeviceKeyService>(),
            recovery ?? Substitute.For<IRecoveryCodeService>(), settings, auth, factory,
            Substitute.For<IConfirmedApproverStore>(), Substitute.For<IScheduledJobService>(), _signer,
            NullLogger<DeviceManagementService>.Instance);
    }
}
