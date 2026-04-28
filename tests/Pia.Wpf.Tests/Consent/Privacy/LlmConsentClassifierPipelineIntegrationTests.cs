using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Services.Consent;
using Pia.Services.Consent.Cloud;
using Pia.Services.Consent.Privacy;
using Xunit;

namespace Pia.Wpf.Tests.Consent.Privacy;

public sealed class LlmConsentClassifierPipelineIntegrationTests
{
    private sealed class CapturingChatClient : IChatClient
    {
        public List<string> ReceivedUserMessages { get; } = new();
        public string ResponseText { get; set; } = "{\"decision\":\"grant\",\"confidence\":0.95}";

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
        {
            foreach (var m in messages)
                if (m.Role == ChatRole.User) ReceivedUserMessages.Add(m.Text ?? "");
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, ResponseText)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? key = null) => null;
        public void Dispose() { }
    }

    private sealed class FakeAuditLog : IConsentAuditLog
    {
        public readonly List<AuditEvent> Events = new();
        public void Append(AuditEvent ev) { lock (Events) Events.Add(ev); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static readonly CloudProviderDescriptor EuProvider = new(
        "mistral", "Mistral", CloudJurisdiction.EuOnly, false, "https://example.com/dpa");

    [Fact]
    public async Task StrictMode_BlocksCloudCall_AndAudits()
    {
        var chat = new CapturingChatClient();
        var audit = new FakeAuditLog();
        var pipeline = new PreCloudPipeline(
            new Pseudonymiser(new RegexPiiDetector()),
            audit,
            NullLogger<PreCloudPipeline>.Instance);
        var sut = new LlmConsentClassifier(
            _ => Task.FromResult<IChatClient?>(chat),
            pipeline,
            () => (ConsentScope.FromProfile(SecurityProfile.Strict), EuProvider),
            NullLogger<LlmConsentClassifier>.Instance);

        var result = await sut.ClassifyAsync("ja, einverstanden", "Recording prompt", CancellationToken.None);

        Assert.Equal(ConsentDecision.Ambiguous, result.Decision);
        Assert.Empty(chat.ReceivedUserMessages);
        Assert.Contains(audit.Events, e => e.EventType == "CLOUD_CALL_BLOCKED");
    }

    [Fact]
    public async Task StandardMode_PseudonymisesUserMessage_BeforeCloudCall()
    {
        var chat = new CapturingChatClient();
        var audit = new FakeAuditLog();
        var pipeline = new PreCloudPipeline(
            new Pseudonymiser(new RegexPiiDetector()),
            audit,
            NullLogger<PreCloudPipeline>.Instance);
        var sut = new LlmConsentClassifier(
            _ => Task.FromResult<IChatClient?>(chat),
            pipeline,
            () => (ConsentScope.FromProfile(SecurityProfile.Standard), EuProvider),
            NullLogger<LlmConsentClassifier>.Instance);

        await sut.ClassifyAsync("Hallo, ich bin Anna Schmidt, ja einverstanden", "Recording prompt", CancellationToken.None);

        Assert.Single(chat.ReceivedUserMessages);
        Assert.DoesNotContain("Anna Schmidt", chat.ReceivedUserMessages[0]);
        Assert.Contains("[NAME-1]", chat.ReceivedUserMessages[0]);
        Assert.Contains(audit.Events, e => e.EventType == "CLOUD_CALL_PREPARED");
    }
}
