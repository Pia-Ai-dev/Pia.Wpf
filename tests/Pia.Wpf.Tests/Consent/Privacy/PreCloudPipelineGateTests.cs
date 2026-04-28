using Microsoft.Extensions.Logging.Abstractions;
using Pia.Services.Consent;
using Pia.Services.Consent.Cloud;
using Pia.Services.Consent.Privacy;
using Xunit;

namespace Pia.Wpf.Tests.Consent.Privacy;

public sealed class PreCloudPipelineGateTests
{
    private sealed class FakeAuditLog : IConsentAuditLog
    {
        public readonly List<AuditEvent> Events = new();
        public void Append(AuditEvent ev) { lock (Events) Events.Add(ev); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static PreCloudPipeline MakeSut(out FakeAuditLog audit)
    {
        audit = new FakeAuditLog();
        return new PreCloudPipeline(
            new Pseudonymiser(new RegexPiiDetector()),
            audit,
            NullLogger<PreCloudPipeline>.Instance);
    }

    private static readonly CloudProviderDescriptor EuProvider = new(
        "mistral", "Mistral", CloudJurisdiction.EuOnly, false, "https://example.com/dpa");

    private static readonly CloudProviderDescriptor UsProvider = new(
        "openai", "OpenAI", CloudJurisdiction.UsAdequacyFramework, true, "https://example.com/dpa");

    [Fact]
    public async Task PrepareAsync_ScopeDisallowsJurisdiction_Throws_AndAudits()
    {
        var sut = MakeSut(out var audit);
        var scope = new ConsentScope(true, EuCloudProcessing: false, NonEuCloudProcessing: false, false);

        await Assert.ThrowsAsync<CloudCallNotPermittedException>(() =>
            sut.PrepareAsync("hello", scope, EuProvider, CancellationToken.None));

        Assert.Contains(audit.Events, e => e.EventType == "CLOUD_CALL_BLOCKED");
        Assert.DoesNotContain(audit.Events, e => e.EventType == "CLOUD_CALL_PREPARED");
    }

    [Fact]
    public async Task PrepareAsync_StrictMode_BlocksAllProviders()
    {
        var sut = MakeSut(out _);
        var scope = ConsentScope.FromProfile(SecurityProfile.Strict);

        await Assert.ThrowsAsync<CloudCallNotPermittedException>(() =>
            sut.PrepareAsync("hello", scope, EuProvider, CancellationToken.None));
        await Assert.ThrowsAsync<CloudCallNotPermittedException>(() =>
            sut.PrepareAsync("hello", scope, UsProvider, CancellationToken.None));
    }

    [Fact]
    public async Task PrepareAsync_StandardMode_AllowsEu_BlocksUs()
    {
        var sut = MakeSut(out _);
        var scope = ConsentScope.FromProfile(SecurityProfile.Standard);

        var ctx = await sut.PrepareAsync("hello", scope, EuProvider, CancellationToken.None);
        Assert.NotNull(ctx);

        await Assert.ThrowsAsync<CloudCallNotPermittedException>(() =>
            sut.PrepareAsync("hello", scope, UsProvider, CancellationToken.None));
    }

    [Fact]
    public async Task PrepareAsync_PseudonymisesContent_AndAuditsCount()
    {
        var sut = MakeSut(out var audit);
        var scope = ConsentScope.FromProfile(SecurityProfile.Standard);

        var ctx = await sut.PrepareAsync(
            "Anna Schmidt schreibt an john@example.com.",
            scope,
            EuProvider,
            CancellationToken.None);

        Assert.DoesNotContain("Anna Schmidt", ctx.PseudonymisedPayload);
        Assert.DoesNotContain("john@example.com", ctx.PseudonymisedPayload);

        var prep = audit.Events.Single(e => e.EventType == "CLOUD_CALL_PREPARED");
        Assert.Equal(2, prep.Details!["pseudonymCount"]);
        // Audit must NOT include raw content.
        Assert.False(prep.Details!.Values.Any(v => v is string s && s.Contains("Anna Schmidt")));
    }

    [Fact]
    public async Task PostProcessAsync_ReversesPseudonyms()
    {
        var sut = MakeSut(out _);
        var scope = ConsentScope.FromProfile(SecurityProfile.Standard);

        var ctx = await sut.PrepareAsync(
            "Anna Schmidt sagt hallo.",
            scope,
            EuProvider,
            CancellationToken.None);

        var fakeResponse = $"Cloud reply about {ctx.Map.Placeholders.Keys.First()}.";
        var processed = await sut.PostProcessAsync(fakeResponse, ctx, CancellationToken.None);
        Assert.Contains("Anna Schmidt", processed);
    }
}
