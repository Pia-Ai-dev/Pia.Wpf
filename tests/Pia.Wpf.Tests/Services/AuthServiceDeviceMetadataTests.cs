using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// The server learns a device's app/OS version at registration and never again, so token requests carry
/// it — but only while it differs from what was last accepted, or every refresh pays for a value the
/// server already has.
/// </summary>
public class AuthServiceDeviceMetadataTests
{
    private const string DeviceId = "11111111-2222-3333-4444-555555555555";

    private sealed class PassthroughDpapi(ILogger<DpapiHelper> logger) : DpapiHelper(logger)
    {
        public override string Encrypt(string plainText) => plainText;

        public override string Decrypt(string encryptedText) => encryptedText;
    }

    private sealed class CapturingHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public HttpRequestHeaders? LastHeaders { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastHeaders = request.Headers;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(
                    """{"accessToken":"at","refreshToken":"rt","expiresIn":3600}""",
                    Encoding.UTF8, "application/json")
            });
        }
    }

    private static (AuthService Sut, AppSettings Settings, CapturingHandler Handler) CreateSut(
        string? reportedMetadata = null,
        string? deviceId = DeviceId,
        HttpStatusCode status = HttpStatusCode.OK)
    {
        var stored = new AppSettings
        {
            ServerUrl = "https://server.example",
            SyncEnabled = true,
            EncryptedRefreshToken = "rt",
            SyncDeviceId = deviceId,
            ReportedDeviceMetadata = reportedMetadata
        };
        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(stored);

        var handler = new CapturingHandler(status);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(handler, disposeHandler: false));

        var sut = new AuthService(
            settings, new PassthroughDpapi(NullLogger<DpapiHelper>.Instance), factory,
            Substitute.For<ILocalizationService>(), NullLogger<AuthService>.Instance);

        return (sut, stored, handler);
    }

    [Fact]
    public async Task AVersionTheServerHasNotSeen_RidesAlongWithTheRefresh()
    {
        var (sut, _, handler) = CreateSut(reportedMetadata: null);

        await sut.GetAccessTokenAsync();

        Assert.Equal(DeviceId, Single(handler, AuthService.DeviceIdHeader));
        Assert.Equal(AppVersionInfo.FileVersion, Single(handler, AuthService.AppVersionHeader));
        Assert.Equal(Environment.OSVersion.ToString(), Single(handler, AuthService.OsVersionHeader));
    }

    [Fact]
    public async Task AVersionTheServerAlreadyHas_LeavesTheRefreshBare()
    {
        var (sut, _, handler) = CreateSut(reportedMetadata: AuthService.DeviceMetadataFingerprint());

        await sut.GetAccessTokenAsync();

        Assert.False(handler.LastHeaders!.Contains(AuthService.DeviceIdHeader));
        Assert.False(handler.LastHeaders.Contains(AuthService.AppVersionHeader));
        Assert.False(handler.LastHeaders.Contains(AuthService.OsVersionHeader));
    }

    // Nothing to attribute the versions to, so nothing is sent — and nothing is marked as reported either.
    [Fact]
    public async Task ADeviceWithoutAnId_SendsNothingAndRecordsNothing()
    {
        var (sut, settings, handler) = CreateSut(reportedMetadata: null, deviceId: null);

        await sut.GetAccessTokenAsync();

        Assert.False(handler.LastHeaders!.Contains(AuthService.AppVersionHeader));
        Assert.Null(settings.ReportedDeviceMetadata);
    }

    [Fact]
    public async Task ARefreshThatSucceeds_RecordsWhatTheServerAccepted()
    {
        var (sut, settings, _) = CreateSut(reportedMetadata: null);

        await sut.GetAccessTokenAsync();

        Assert.Equal(AuthService.DeviceMetadataFingerprint(), settings.ReportedDeviceMetadata);
    }

    // Recording it here would retire the headers over a refresh the server never processed.
    [Fact]
    public async Task ARefreshThatFails_RecordsNothing()
    {
        var (sut, settings, _) = CreateSut(reportedMetadata: null, status: HttpStatusCode.InternalServerError);

        await sut.GetAccessTokenAsync();

        Assert.Null(settings.ReportedDeviceMetadata);
    }

    [Fact]
    public void TheReportedVersion_IsTheFourPartFileVersion()
    {
        var parts = AppVersionInfo.FileVersion.Split('.');

        Assert.Equal(4, parts.Length);
        Assert.All(parts, part => Assert.True(int.TryParse(part, out _)));
        // AssemblyVersion is pinned to Major.Minor.0.0, which is exactly what this must not be.
        Assert.NotEqual("0.0", $"{parts[2]}.{parts[3]}");
    }

    private static string? Single(CapturingHandler handler, string header) =>
        handler.LastHeaders!.GetValues(header).Single();
}
