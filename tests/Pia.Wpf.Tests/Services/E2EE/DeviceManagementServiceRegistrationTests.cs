using System.Net;
using System.Net.Http;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Localization;
using Pia.Logging;
using Pia.Models;
using Pia.Services.E2EE;
using Pia.Tests.TestInfrastructure;
using Pia.Services.Interfaces;
using Pia.Shared.E2EE;
using Xunit;

namespace Pia.Tests.Services.E2EE;

/// <summary>Registration refusals retain UI details without exposing response bodies through exceptions.</summary>
public class DeviceManagementServiceRegistrationTests
{
    // A refused 400 is a bare JSON string — the server's own explanation, shown as-is.
    [Fact]
    public async Task ABareBadRequestMessage_IsShownUnchanged()
    {
        var ex = await RegisterRefused(HttpStatusCode.BadRequest,
            System.Text.Json.JsonSerializer.Serialize("DeviceName is required."));

        Assert.Equal(E2EEErrorCodes.InvalidField, ex.Code);
        Assert.Equal("DeviceName is required.", ex.UserMessage);
    }

    [Fact]
    public async Task ARegistrationThatSucceeds_ReturnsTheResponse()
    {
        var sut = CreateSut(HttpStatusCode.OK,
            System.Text.Json.JsonSerializer.Serialize(new DeviceRegistrationResponse
            {
                IsFirstDevice = false,
                OnboardingSessionId = "session-1",
                ServerChallenge = "challenge",
            }));

        var response = await sut.RegisterPendingDeviceAsync();

        Assert.Equal("session-1", response.OnboardingSessionId);
    }

    [Fact]
    public async Task AnUnknownEnvelopeCode_FallsBackToTheGenericMessage()
    {
        var ex = await RegisterRefused(HttpStatusCode.Conflict, """{"error":"brand_new_code"}""");

        Assert.Equal("brand_new_code", ex.Code);
        Assert.Equal(LocalizationSource.Instance["E2EE_Register_Generic"], ex.UserMessage);
    }

    [Fact]
    public async Task DeviceChanged_IsMappedToItsOwnReadableMessage()
    {
        var ex = await RegisterRefused(HttpStatusCode.Conflict, """{"error":"device_changed"}""");

        Assert.Equal(E2EEErrorCodes.DeviceChanged, ex.Code);
        Assert.Equal(LocalizationSource.Instance["E2EE_Register_DeviceChanged"], ex.UserMessage);
    }

    [Fact]
    public async Task DeviceMismatch_IsMappedToItsOwnReadableMessage()
    {
        var ex = await RegisterRefused(HttpStatusCode.Forbidden, """{"error":"device_mismatch"}""");

        Assert.Equal(E2EEErrorCodes.DeviceMismatch, ex.Code);
        Assert.Equal(LocalizationSource.Instance["E2EE_Register_DeviceMismatch"], ex.UserMessage);
        Assert.DoesNotContain("[E2EE_Register_", ex.UserMessage);
    }

    [Fact]
    public async Task TooManyPendingDevices_IsMappedToItsOwnReadableMessage()
    {
        var ex = await RegisterRefused(HttpStatusCode.Conflict, """{"error":"too_many_pending_devices"}""");

        Assert.Equal(E2EEErrorCodes.TooManyPendingDevices, ex.Code);
        Assert.Equal(LocalizationSource.Instance["E2EE_Register_TooManyPending"], ex.UserMessage);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "\"private server explanation\"")]
    [InlineData(HttpStatusCode.BadGateway, "private gateway response")]
    [InlineData(HttpStatusCode.Conflict, "{\"error\":\"private unknown error\"}")]
    public async Task RejectionDetails_StayOutOfExceptionTextAndReleaseDiagnostics(HttpStatusCode status, string body)
    {
        var logger = Substitute.For<ILogger<DeviceManagementService>>();
        var sut = CreateSut(status, body, logger);

        var ex = await Assert.ThrowsAsync<DeviceRegistrationRejectedException>(() => sut.RegisterPendingDeviceAsync());

        Assert.Equal((int)status, ex.StatusCode);
        Assert.Contains(((int)status).ToString(), ex.Message);
        Assert.DoesNotContain("private", ex.ToString());
        if (status != HttpStatusCode.Conflict)
            Assert.Contains("private", ex.UserMessage);
        if (!SafeLog.SensitiveLoggingCompiledIn)
            Assert.Empty(logger.ReceivedCalls());
    }

    [Fact]
    public async Task SessionEndedOnRegister_SignsOutOnceWithTheRejectedReason()
    {
        var auth = Substitute.For<IAuthService>();
        var sut = CreateSut(HttpStatusCode.Conflict, """{"error":"session_ended"}""", auth: auth);

        var ex = await Assert.ThrowsAsync<DeviceRegistrationRejectedException>(
            () => sut.RegisterPendingDeviceAsync());

        Assert.Equal(E2EEErrorCodes.SessionEnded, ex.Code);
        await auth.Received(1).SignOutUnexpectedlyAsync(Arg.Any<AuthSignOutReason>());
        await auth.Received(1).SignOutUnexpectedlyAsync(AuthSignOutReason.ServerRejectedSession);
        await auth.DidNotReceive().GetAccessTokenAsync(true, Arg.Any<string?>());
    }

    [Fact]
    public async Task SessionEndedOnTheKeyFetch_SignsOutOnce()
    {
        var auth = Substitute.For<IAuthService>();
        var sut = CreateSut(HttpStatusCode.Conflict, """{"error":"session_ended"}""", auth: auth);

        await Assert.ThrowsAsync<HttpRequestException>(() => sut.FetchKeyHandoverAsync());

        await auth.Received(1).SignOutUnexpectedlyAsync(AuthSignOutReason.ServerRejectedSession);
    }

    [Fact]
    public async Task TooManyPendingDevices_DoesNotSignOut()
    {
        var auth = Substitute.For<IAuthService>();
        var sut = CreateSut(HttpStatusCode.Conflict, """{"error":"too_many_pending_devices"}""", auth: auth);

        await Assert.ThrowsAsync<DeviceRegistrationRejectedException>(() => sut.RegisterPendingDeviceAsync());

        await auth.DidNotReceive().SignOutUnexpectedlyAsync(Arg.Any<AuthSignOutReason>());
    }

    private static DeviceManagementService CreateSut(HttpStatusCode status, string body,
        ILogger<DeviceManagementService>? logger = null, IAuthService? auth = null)
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings { ServerUrl = "https://test.example.com" });

        auth ??= Substitute.For<IAuthService>();
        auth.GetAccessTokenAsync().Returns("token");

        var deviceKeys = Substitute.For<IDeviceKeyService>();
        deviceKeys.GetDeviceId().Returns("device-001");
        deviceKeys.GetAgreementPublicKey().Returns(Convert.ToBase64String(new byte[65]));
        deviceKeys.GetSigningPublicKey().Returns(Convert.ToBase64String(new byte[65]));

        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>())
            .Returns(_ => new HttpClient(new StubHandler(status, body), disposeHandler: true));

        return new DeviceManagementService(
            Substitute.For<IE2EEService>(), deviceKeys, Substitute.For<IRecoveryCodeService>(),
            settings, auth, factory, Substitute.For<IConfirmedApproverStore>(), Substitute.For<IScheduledJobService>(), new UnsignedRequests(),
            logger ?? NullLogger<DeviceManagementService>.Instance);
    }

    private static async Task<DeviceRegistrationRejectedException> RegisterRefused(
        HttpStatusCode status, string body)
    {
        var sut = CreateSut(status, body);
        return await Assert.ThrowsAsync<DeviceRegistrationRejectedException>(
            () => sut.RegisterPendingDeviceAsync());
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
    }
}
