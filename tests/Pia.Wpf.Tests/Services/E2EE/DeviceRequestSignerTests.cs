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
using Pia.Tests.TestInfrastructure;
using Xunit;

public sealed class DeviceRequestSignerTests : IDisposable
{
    private const string DeviceId = "3f2504e0-4f89-41d3-9a0c-0305e82c3301";
    private const string UserId = "8c1d2e3f-4a5b-4c6d-8e7f-901a2b3c4d5e";
    private const string WrapPath = $"api/e2ee/devices/{DeviceId}/wrapped-umk";

    private readonly InMemoryDeviceKeys _keys = new(DeviceId);
    private readonly AppSettings _stored = new() { SyncUserId = UserId };
    private readonly VerifyingServer _server;
    private readonly DeviceRequestSigner _sut;

    public DeviceRequestSignerTests()
    {
        _server = new VerifyingServer(_keys);
        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(_stored);
        _sut = new DeviceRequestSigner(_keys, settings, NullLogger<DeviceRequestSigner>.Instance);
    }

    [Fact]
    public async Task Signs_TheExactBytesItSends_WithAFreshChallenge()
    {
        var body = Encoding.UTF8.GetBytes("""{"deviceId":"x","ciphertext":"AAEC"}""");

        using var response = await SendAsync(WrapPath, body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var sent = Assert.Single(_server.Requests);
        Assert.True(sent.SignatureVerified);
        Assert.Equal(body, sent.Body);
        Assert.Equal("application/json; charset=utf-8", sent.ContentType);
        Assert.Equal(1, _server.ChallengesIssued);
    }

    [Fact]
    public async Task Signs_ABodylessRequest_OverTheEmptyBody()
    {
        using var response = await SendAsync($"api/e2ee/devices/{DeviceId}/revoke", null);

        var sent = Assert.Single(_server.Requests);
        Assert.True(sent.SignatureVerified);
        Assert.Empty(sent.Body);
    }

    // A deployment prefix in the server URL stays out of the signature, so a stripping proxy does not break it.
    [Fact]
    public async Task Signs_TheRoutePath_WithoutTheServersPathPrefix()
    {
        using var response = await SendAsync(WrapPath, [], baseAddress: "https://sync.example/pia/");

        var sent = Assert.Single(_server.Requests);
        Assert.Equal($"/pia/{WrapPath}", sent.WirePath);
        Assert.True(sent.SignatureVerified);
    }

    [Fact]
    public async Task WithoutAKey_SendsUnsigned_AndAsksForNoChallenge()
    {
        _keys.HasSigningKey = false;

        using var response = await SendAsync(WrapPath, []);

        Assert.False(Assert.Single(_server.Requests).Signed);
        Assert.Equal(0, _server.ChallengesIssued);
    }

    [Fact]
    public async Task AfterRecoveryWithTheSigningKey_SignsDespiteKeyLossDetection()
    {
        _keys.KeyLossDetected = true;

        using var response = await SendAsync(WrapPath, []);

        Assert.True(Assert.Single(_server.Requests).SignatureVerified);
        Assert.Equal(1, _server.ChallengesIssued);
    }

    [Fact]
    public async Task WithoutAnAccountId_SendsUnsigned()
    {
        _stored.SyncUserId = null;

        using var response = await SendAsync(WrapPath, []);

        Assert.False(Assert.Single(_server.Requests).Signed);
        Assert.Equal(0, _server.ChallengesIssued);
    }

    [Fact]
    public async Task AnOldServer_GetsUnsignedRequests_AndIsAskedForAChallengeOnlyOnce()
    {
        _server.Challenge = ChallengeMode.OldServer;

        using (await SendAsync(WrapPath, [])) { }
        using (await SendAsync(WrapPath, [])) { }

        Assert.All(_server.Requests, r => Assert.False(r.Signed));
        Assert.Equal(2, _server.Requests.Count);
        Assert.Equal(1, _server.ChallengeCalls);
    }

    [Fact]
    public async Task AnOldServerThatStartsAskingForSignatures_GetsASignedRetry()
    {
        _server.Challenge = ChallengeMode.OldServer;
        using (await SendAsync(WrapPath, [])) { }

        _server.Challenge = ChallengeMode.Issue;
        _server.RequireSignature = true;
        using var response = await SendAsync(WrapPath, []);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(_server.Requests[^1].SignatureVerified);
    }

    [Fact]
    public async Task ANotFoundWithAnErrorCode_IsNotTakenForAnOldServer()
    {
        _server.Challenge = ChallengeMode.NotFoundWithCode;

        using var response = await SendAsync(WrapPath, []);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(_server.Requests);
    }

    [Fact]
    public async Task ARefusedChallenge_IsReturned_WithoutSendingTheRequest()
    {
        _server.Challenge = ChallengeMode.DeviceNotActive;

        using var response = await SendAsync(WrapPath, []);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(E2EEErrorCodes.DeviceNotActive, await E2EEErrorBody.ReadCodeAsync(response, TestContext.Current.CancellationToken));
        Assert.Empty(_server.Requests);
    }

    [Fact]
    public async Task AnInvalidSignature_IsRetriedOnce_WithAFreshChallenge()
    {
        _server.RefuseSignatures = 1;

        using var response = await SendAsync(WrapPath, [1, 2, 3]);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, _server.ChallengesIssued);
        Assert.NotEqual(_server.Requests[0].ChallengeId, _server.Requests[1].ChallengeId);
        Assert.Equal(_server.Requests[0].Body, _server.Requests[1].Body);
    }

    [Fact]
    public async Task ASecondInvalidSignature_IsSurfaced()
    {
        _server.RefuseSignatures = 2;

        using var response = await SendAsync(WrapPath, []);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(E2EEErrorCodes.InvalidSignature, await E2EEErrorBody.ReadCodeAsync(response, TestContext.Current.CancellationToken));
        Assert.Equal(2, _server.Requests.Count);
    }

    public void Dispose() => _keys.Dispose();

    private Task<HttpResponseMessage> SendAsync(
        string path, byte[]? body, string baseAddress = "https://sync.example/")
    {
        var client = new HttpClient(_server, disposeHandler: false) { BaseAddress = new Uri(baseAddress) };
        return _sut.SendAsync(client, HttpMethod.Post, path, body, TestContext.Current.CancellationToken);
    }

    private enum ChallengeMode { Issue, OldServer, NotFoundWithCode, DeviceNotActive }

    private sealed record Received(
        string WirePath, byte[] Body, string? ContentType, bool Signed, string? ChallengeId, bool SignatureVerified);

    // Checks a signature the way the server does: over the route path from /api/ on and the received bytes.
    private sealed class VerifyingServer(InMemoryDeviceKeys device) : HttpMessageHandler
    {
        private readonly HashSet<string> _openChallenges = [];

        public ChallengeMode Challenge { get; set; }
        public bool RequireSignature { get; set; }
        public int RefuseSignatures { get; set; }
        public int ChallengeCalls { get; private set; }
        public int ChallengesIssued { get; private set; }
        public List<Received> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var wirePath = request.RequestUri!.AbsolutePath;
            if (wirePath.EndsWith("/challenge"))
                return IssueChallenge();

            var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(ct);
            var signed = request.Headers.TryGetValues(DeviceRequestSignature.SignatureHeader, out var signatures);
            var challengeId = request.Headers.TryGetValues(DeviceRequestSignature.ChallengeIdHeader, out var ids)
                ? ids.Single()
                : null;
            var verified = signed && challengeId is not null && _openChallenges.Remove(challengeId)
                && device.Verify(
                    DeviceRequestSignature.Payload(
                        request.Method.Method, wirePath[wirePath.IndexOf("/api/", StringComparison.Ordinal)..],
                        Guid.Parse(UserId), request.Headers.GetValues(DeviceRequestSignature.DeviceIdHeader).Single(),
                        challengeId, body),
                    signatures!.Single(), device.GetSigningPublicKey());
            Requests.Add(new Received(
                wirePath, body, request.Content?.Headers.ContentType?.ToString(), signed, challengeId, verified));

            if (signed && RefuseSignatures-- > 0)
                return Error(HttpStatusCode.Forbidden, E2EEErrorCodes.InvalidSignature);
            if (!signed && RequireSignature)
                return Error(HttpStatusCode.Forbidden, E2EEErrorCodes.SignatureRequired);
            if (signed && !verified)
                return Error(HttpStatusCode.Forbidden, E2EEErrorCodes.InvalidSignature);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }

        private HttpResponseMessage IssueChallenge()
        {
            ChallengeCalls++;
            switch (Challenge)
            {
                case ChallengeMode.OldServer:
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                case ChallengeMode.NotFoundWithCode:
                    return Error(HttpStatusCode.NotFound, "no_such_thing");
                case ChallengeMode.DeviceNotActive:
                    return Error(HttpStatusCode.Forbidden, E2EEErrorCodes.DeviceNotActive);
            }

            ChallengesIssued++;
            var id = Guid.NewGuid().ToString("N");
            _openChallenges.Add(id);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(
                        new DeviceChallengeResponse { ChallengeId = id, ExpiresAt = DateTime.UtcNow.AddSeconds(60) },
                        JsonSerializerOptions.Web),
                    Encoding.UTF8, "application/json"),
            };
        }

        private static HttpResponseMessage Error(HttpStatusCode status, string code) => new(status)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { error = code }), Encoding.UTF8, "application/json"),
        };
    }
}
