using System.IO;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Infrastructure;
using Pia.Services.Consent;
using Pia.Services.Consent.Cloud;
using Pia.Services.Consent.Privacy;
using Pia.Services.Consent.Revocation;
using Pia.Services.Consent.Snippet;
using Xunit;

namespace Pia.Wpf.Tests.Consent.Privacy;

/// <summary>
/// Phase 4 Task 11 / 12: end-to-end verification that meeting content with PII flows
/// through the pre-cloud pipeline (zero original tokens leave the boundary) and that
/// revocation cleans up the persisted snippet while keeping the consent evidence.
/// Asserted at the pipeline boundary rather than over real HTTP — equivalent guarantee
/// without depending on HttpClientFactory plumbing.
/// </summary>
public sealed class Phase4PrivacyEndToEndTests : IDisposable
{
    private readonly string _sessionDir;
    private readonly FakeAuditLog _audit = new();

    public Phase4PrivacyEndToEndTests()
    {
        _sessionDir = Path.Combine(Path.GetTempPath(), "Phase4E2E_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sessionDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_sessionDir, recursive: true); } catch { }
    }

    private sealed class FakeAuditLog : IConsentAuditLog
    {
        public readonly List<AuditEvent> Events = new();
        public void Append(AuditEvent ev) { lock (Events) Events.Add(ev); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CapturingChatClient : IChatClient
    {
        public List<string> SentBodies { get; } = new();
        public Func<string, string> RespondTo { get; set; } = _ => "ok";

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
        {
            var body = string.Join("\n", messages.Select(m => m.Text));
            SentBodies.Add(body);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, RespondTo(body))));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
            => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? key = null) => null;
        public void Dispose() { }
    }

    private static readonly CloudProviderDescriptor EuProvider = new(
        "mistral", "Mistral", CloudJurisdiction.EuOnly, false, "https://example.com/dpa");

    [Fact]
    public async Task StandardMode_PiiRichTranscript_NoOriginalTokensReachCloud()
    {
        var pipeline = new PreCloudPipeline(
            new Pseudonymiser(new RegexPiiDetector()),
            _audit,
            NullLogger<PreCloudPipeline>.Instance);
        var chat = new CapturingChatClient();
        chat.RespondTo = body =>
        {
            // Simulate a cloud reply that quotes the placeholders back.
            return "Summary mentioning [NAME-1] and [EMAIL-1] and [IBAN-1].";
        };

        const string transcript =
            "Anna Schmidt aus der Hauptstraße 12 hat angerufen. "
            + "Sie schreibt an john@example.com und ihr IBAN ist DE89370400440532013000.";

        var scope = ConsentScope.FromProfile(SecurityProfile.Standard);
        var ctx = await pipeline.PrepareAsync(transcript, scope, EuProvider, CancellationToken.None);

        var response = await chat.GetResponseAsync(new[]
        {
            new ChatMessage(ChatRole.User, ctx.PseudonymisedPayload),
        });
        var processed = await pipeline.PostProcessAsync(response.Messages.First().Text!, ctx, CancellationToken.None);

        // Assert: nothing in the body that left the pipeline contains original PII.
        Assert.Single(chat.SentBodies);
        var body = chat.SentBodies[0];
        Assert.DoesNotContain("Anna Schmidt", body);
        Assert.DoesNotContain("Hauptstraße 12", body);
        Assert.DoesNotContain("john@example.com", body);
        Assert.DoesNotContain("DE89370400440532013000", body);

        // Response is reverse-mapped before display.
        Assert.Contains("Anna Schmidt", processed);
        Assert.Contains("john@example.com", processed);
        Assert.Contains("DE89370400440532013000", processed);
    }

    private sealed class FakeBlocklist : IBlocklistFilter
    {
        public List<string> Blocked { get; } = new();
        public void BlockSpeaker(string label) => Blocked.Add(label);
        public bool ShouldDrop(float[] embedding) => false;
        public void Reset() => Blocked.Clear();
    }

    [Fact]
    public async Task RevocationE2E_DeletesSnippet_PreservesEvidence_AppendsAudit()
    {
        // Arrange: complete a session with persisted transcript + snippet.
        var enc = SessionEncryption.CreateSession();
        var recorder = new ConsentSnippetRecorder(
            enc, _audit, TimeProvider.System, NullLogger<ConsentSnippetRecorder>.Instance);
        var snippetPath = recorder.Persist(
            SecurityProfile.Strict, _sessionDir, "Speaker 1", "audio-bytes"u8.ToArray());
        Assert.NotNull(snippetPath);
        Assert.True(File.Exists(snippetPath));

        var consentMgr = new ConsentStateManager(NullLogger<ConsentStateManager>.Instance, TimeProvider.System);
        var entry = consentMgr.GetOrCreate("Speaker 1");
        entry.State = ConsentState.Granted;
        entry.Evidence = new ConsentEvidence(
            "ja, einverstanden", 0.95f, DateTimeOffset.UtcNow, "h", "p", "stt");

        // A transcript-store implementation that simulates redaction + snippet deletion.
        var transcriptStore = new SnippetDeletingTranscriptStore(recorder, _sessionDir, "Speaker 1");
        var sut = new RevocationService(
            consentMgr,
            new FakeBlocklist(),
            transcriptStore,
            new NoOpSummaryStore(),
            Array.Empty<IProviderDeletionClient>(),
            _audit,
            TimeProvider.System,
            NullLogger<RevocationService>.Instance);

        // Act
        var evidence = await sut.RevokeAsync("Speaker 1", CancellationToken.None);

        // Assert: snippet file deleted.
        Assert.False(File.Exists(snippetPath));
        // ConsentEvidence preserved (audit purposes).
        Assert.NotNull(entry.Evidence);
        Assert.Equal("ja, einverstanden", entry.Evidence!.TranscriptText);
        // RevocationEvidence reports clean removal.
        Assert.True(evidence.TranscriptRedacted);
        // Audit chain extended with REVOCATION.
        Assert.Contains(_audit.Events, e => e.EventType == "REVOCATION");
        Assert.Contains(_audit.Events, e => e.EventType == "SNIPPET_PERSISTED");
    }

    private sealed class SnippetDeletingTranscriptStore : IPersistedTranscriptStore
    {
        private readonly ConsentSnippetRecorder _recorder;
        private readonly string _sessionDir;
        private readonly string _expectedLabel;

        public SnippetDeletingTranscriptStore(ConsentSnippetRecorder r, string dir, string expectedLabel)
        {
            _recorder = r;
            _sessionDir = dir;
            _expectedLabel = expectedLabel;
        }

        public Task<bool> RedactSpeakerAsync(string speakerLabel, CancellationToken ct)
        {
            // Treat snippet deletion as part of redaction for the test wiring.
            var deleted = _recorder.Delete(_sessionDir, speakerLabel);
            return Task.FromResult(deleted);
        }
    }
}
