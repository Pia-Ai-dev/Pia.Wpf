namespace Pia.Tests.Operators;

using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services.Interfaces;
using Pia.Services.Operators;
using Pia.Shared.Operators;
using Xunit;

/// <summary>
/// The availability gate, which decides whether the background-assignment surface exists at all. Every "no"
/// answer collapses to hidden on purpose: a disabled button the user cannot explain is worse than no button,
/// and the reasons — no server, no licence feature, an older server, no grant — are not theirs to debug.
/// </summary>
public class AssignmentApiClientTests
{
    private const string ServerUrl = "https://pia.test";

    private readonly StubHandler _handler = new();
    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();
    private readonly IAuthService _auth = Substitute.For<IAuthService>();
    private readonly IHttpClientFactory _factory = Substitute.For<IHttpClientFactory>();

    public AssignmentApiClientTests()
    {
        _factory.CreateClient().Returns(_ => new HttpClient(_handler));
        _settings.GetSettingsAsync().Returns(new AppSettings { ServerUrl = ServerUrl });
        _auth.GetAccessTokenAsync().Returns("test-token");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private AssignmentApiClient CreateSut() =>
        new(_settings, _auth, _factory, NullLogger<AssignmentApiClient>.Instance);

    [Fact]
    public async Task GetSurfaceAsync_WithGrantedSkills_IsAvailableAndCarriesTheDeclarations()
    {
        _handler.Respond(
            HttpStatusCode.OK,
            """
            [{"name":"research","displayName":"research","mode":"Research",
              "declaredInputTypes":["assistantChat","session","memory"]}]
            """);

        var surface = await CreateSut().GetSurfaceAsync(Ct);

        Assert.True(surface.Available);
        var skill = Assert.Single(surface.Skills);
        Assert.Equal("research", skill.Name);
        Assert.Equal(
            [AssignmentInputEntityTypes.AssistantChat, AssignmentInputEntityTypes.Session,
             AssignmentInputEntityTypes.Memory],
            skill.DeclaredInputTypes);
    }

    /// <summary>An empty array is a real answer — nothing is granted to this user — and it hides the surface
    /// exactly like a refusal, because there is nothing to offer either way.</summary>
    [Fact]
    public async Task GetSurfaceAsync_WithNoGrantedSkills_IsHidden()
    {
        _handler.Respond(HttpStatusCode.OK, "[]");

        Assert.False((await CreateSut().GetSurfaceAsync(Ct)).Available);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task GetSurfaceAsync_OnAnyRefusal_IsHidden(HttpStatusCode status)
    {
        _handler.Respond(status, "{}");

        Assert.False((await CreateSut().GetSurfaceAsync(Ct)).Available);
    }

    /// <summary>A local-only install has no server and no token. Neither is an error state, and neither may
    /// produce a network call.</summary>
    [Fact]
    public async Task GetSurfaceAsync_WithNoServerConfigured_IsHiddenWithoutCallingAnything()
    {
        _settings.GetSettingsAsync().Returns(new AppSettings { ServerUrl = null });

        Assert.False((await CreateSut().GetSurfaceAsync(Ct)).Available);
        Assert.Equal(0, _handler.Calls);
    }

    [Fact]
    public async Task GetSurfaceAsync_WithNoToken_IsHiddenWithoutCallingAnything()
    {
        _auth.GetAccessTokenAsync().Returns((string?)null);

        Assert.False((await CreateSut().GetSurfaceAsync(Ct)).Available);
        Assert.Equal(0, _handler.Calls);
    }

    [Fact]
    public async Task CreateAsync_SendsTheEnvelopeAsTheInputJsonString()
    {
        _handler.Respond(HttpStatusCode.Accepted, $$"""{"id":"{{Guid.NewGuid()}}"}""");
        var envelope = new AssignmentInput(
            AssignmentInput.CurrentSchemaVersion, "what did we decide?",
            [new AssignmentInputItem(
                AssignmentInputEntityTypes.Memory, Guid.NewGuid(), "a title", "we chose Postgres", null)]);

        var id = await CreateSut().CreateAsync("research", envelope, Ct);

        Assert.NotNull(id);
        Assert.NotNull(_handler.LastBody);

        // inputJson is a STRING on the wire, not a nested object — asserted by round-tripping it rather than
        // by matching escaped text, since the serializer is free to escape quotes as ".
        using var sent = JsonDocument.Parse(_handler.LastBody!);
        Assert.Equal("research", sent.RootElement.GetProperty("skillName").GetString());
        var inputJson = sent.RootElement.GetProperty("inputJson").GetString();
        Assert.NotNull(inputJson);

        var envelopeBack = JsonSerializer.Deserialize<AssignmentInput>(
            inputJson!, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(AssignmentInput.CurrentSchemaVersion, envelopeBack!.SchemaVersion);
        Assert.Equal("what did we decide?", envelopeBack.Prompt);
        var item = Assert.Single(envelopeBack.Items);
        Assert.Equal(AssignmentInputEntityTypes.Memory, item.EntityType);
        Assert.Equal("we chose Postgres", item.Text);
    }

    [Fact]
    public async Task CreateAsync_OnARefusal_ReturnsNull()
    {
        _handler.Respond(HttpStatusCode.BadRequest, """{"error":"undeclared_entity_type","message":"…"}""");

        Assert.Null(await CreateSut().CreateAsync(
            "research", new AssignmentInput(AssignmentInput.CurrentSchemaVersion, "hello", []), Ct));
    }

    /// <summary>What the job list renders. The list projection carries progress and no artifact, so polling it
    /// cannot become a way of downloading every result the user owns as a side effect.</summary>
    [Fact]
    public async Task ListAsync_ReturnsTheCallersRunsWithProgressAndNoArtifact()
    {
        _handler.Respond(
            HttpStatusCode.OK,
            """
            [{"id":"11111111-1111-1111-1111-111111111111","skillName":"brief","mode":"Assistant",
              "status":"Running","stepCount":2,"tokensSpent":41000,"tokensAbandoned":0,
              "createdAt":"2026-08-12T10:00:00Z","updatedAt":"2026-08-12T10:04:00Z",
              "startedAt":"2026-08-12T10:00:05Z"}]
            """);

        var runs = await CreateSut().ListAsync(ct: Ct);

        var run = Assert.Single(runs);
        Assert.Equal("Running", run.Status);
        Assert.Equal(2, run.StepCount);
        Assert.Equal(41000, run.TokensSpent);
        Assert.Null(run.ArtifactJson);
        Assert.Null(run.ArtifactText);
        Assert.Null(run.Events);
    }

    /// <summary>An empty list rather than a throw: a job list showing nothing beats one showing an error the
    /// user can do nothing about, and the next refresh tries again.</summary>
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task ListAsync_OnARefusal_IsEmpty(HttpStatusCode status)
    {
        _handler.Respond(status, "{}");

        Assert.Empty(await CreateSut().ListAsync(ct: Ct));
    }

    /// <summary>404 is the ordinary answer for a run with no live workflow behind it — already finished, or
    /// never started — so it is a false, not an error.</summary>
    [Theory]
    [InlineData(HttpStatusCode.NoContent, true)]
    [InlineData(HttpStatusCode.NotFound, false)]
    public async Task CancelAsync_ReportsWhetherThereWasAnythingToStop(HttpStatusCode status, bool expected)
    {
        _handler.Respond(status, "");

        Assert.Equal(expected, await CreateSut().CancelAsync(Guid.NewGuid(), Ct));
    }

    /// <summary>204 and 404 both mean the server has nothing left to hand over, which is what makes the resume
    /// pass safe to run twice. 409 does not — the run is still going.</summary>
    [Theory]
    [InlineData(HttpStatusCode.NoContent, true)]
    [InlineData(HttpStatusCode.NotFound, true)]
    [InlineData(HttpStatusCode.Conflict, false)]
    [InlineData(HttpStatusCode.InternalServerError, false)]
    public async Task CollectAsync_TreatsARepeatAsDone_AndAnUnfinishedRunAsNotDone(
        HttpStatusCode status, bool expected)
    {
        _handler.Respond(status, "");

        Assert.Equal(expected, await CreateSut().CollectAsync(Guid.NewGuid(), Ct));
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private HttpStatusCode _status = HttpStatusCode.OK;
        private string _body = "[]";

        public int Calls { get; private set; }
        public string? LastBody { get; private set; }

        public void Respond(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            if (request.Content is not null)
                LastBody = await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            };
        }
    }
}
