namespace Pia.Tests.Operators;

using System.IO;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Services.Interfaces;
using Pia.Services.Operators;
using Pia.Shared.Models;
using Pia.Shared.Operators;
using Xunit;

/// <summary>
/// The two orderings the decrypt-in gate rests on, asserted as orderings rather than as "the dialog appeared":
/// nothing local is read before a consent record exists, and the artifact is committed locally before the
/// irreversible acknowledgement is sent.
/// </summary>
public class AssignmentRunOrchestratorTests : IDisposable
{
    private readonly string _consentDirectory = Path.Combine(
        Path.GetTempPath(), $"pia-consent-{Guid.NewGuid():N}");

    private readonly List<string> _trace = [];
    private readonly TraceScopeReader _scope;
    private readonly TraceApiClient _api = new();
    private readonly InMemoryPendingStore _pending = new();
    private readonly IAssistantChatService _chats = Substitute.For<IAssistantChatService>();
    private readonly JsonlAssignmentConsentStore _consent;

    public AssignmentRunOrchestratorTests()
    {
        Directory.CreateDirectory(_consentDirectory);
        _consent = new JsonlAssignmentConsentStore(
            Path.Combine(_consentDirectory, "assignments.jsonl"),
            NullLogger<JsonlAssignmentConsentStore>.Instance);
        _scope = new TraceScopeReader(_trace);
        _api.Trace = _trace;
        _chats.SaveAsync(Arg.Any<SyncAssistantChat>(), Arg.Any<CancellationToken>())
            .Returns(_ => { _trace.Add("chat-write"); return Task.CompletedTask; });
    }

    public void Dispose()
    {
        if (Directory.Exists(_consentDirectory)) Directory.Delete(_consentDirectory, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private AssignmentRunOrchestrator CreateSut() => new(
        _api, _consent, _scope, _pending, _chats, NullLogger<AssignmentRunOrchestrator>.Instance);

    private static AssignmentScopeItem Item(string entityType = AssignmentInputEntityTypes.Memory, int chars = 12) =>
        new(entityType, Guid.NewGuid(), "a title", chars, DateTime.UtcNow);

    private static AssignmentRequest Request(params AssignmentScopeItem[] items) =>
        new("research", "what did we decide?", items);

    /// <summary>
    /// The whole point of the receipt being a required argument. A caller with no consent record — a headless
    /// or background path is exactly that — reads nothing and sends nothing, rather than being refused after
    /// the content has already been gathered.
    /// </summary>
    [Fact]
    public async Task StartAsync_WithAReceiptNoConsentLogEverWrote_ReadsNothingAndSendsNothing()
    {
        var request = Request(Item());
        var forged = new AssignmentConsentReceipt(
            Guid.NewGuid(), request.SkillName, request.Items, DateTime.UtcNow);

        var outcome = await CreateSut().StartAsync(request, forged, Ct);

        Assert.Equal(AssignmentStartStatus.ConsentMissing, outcome.Status);
        Assert.Empty(_trace);
        Assert.Equal(0, _api.CreateCalls);
        Assert.Empty(await _pending.GetAllAsync());
    }

    /// <summary>A receipt has to be about the request being made. A stale one for a small selection must not
    /// authorise a larger one — otherwise consent is per-session rather than per-selection.</summary>
    [Fact]
    public async Task StartAsync_WithAReceiptForADifferentSelection_IsRefused()
    {
        var consented = Item();
        var receipt = await _consent.RecordAsync("research", "Research", [consented], Ct);
        var request = Request(consented, Item());

        var outcome = await CreateSut().StartAsync(request, receipt, Ct);

        Assert.Equal(AssignmentStartStatus.ConsentMissing, outcome.Status);
        Assert.Equal(0, _api.CreateCalls);
        Assert.DoesNotContain("read", _trace);
    }

    [Fact]
    public async Task StartAsync_ReadsTheRecordsOnlyAfterTheConsentRecordIsOnDisk()
    {
        var item = Item();
        var receipt = await _consent.RecordAsync("research", "Research", [item], Ct);
        var logPath = Path.Combine(_consentDirectory, "assignments.jsonl");

        // The record is durable BEFORE anything is read: RecordAsync awaited the write, so the evidence
        // exists even if the process dies between here and the send.
        Assert.True(File.Exists(logPath));
        Assert.Empty(_trace);

        var outcome = await CreateSut().StartAsync(Request(item), receipt, Ct);

        Assert.Equal(AssignmentStartStatus.Started, outcome.Status);
        Assert.Equal(["read", "create"], _trace);
    }

    /// <summary>Metadata only, like the speaker-consent trail: the entity id is enough to resolve a title
    /// locally when the user asks what they sent, so the title itself never needs to be in the file.</summary>
    [Fact]
    public async Task ConsentRecord_CarriesNoUserContent()
    {
        var item = new AssignmentScopeItem(
            AssignmentInputEntityTypes.Memory, Guid.NewGuid(), "Quarterly revenue plan", 40, DateTime.UtcNow);

        await _consent.RecordAsync("research", "Research", [item], Ct);

        var written = await File.ReadAllTextAsync(Path.Combine(_consentDirectory, "assignments.jsonl"), Ct);
        Assert.DoesNotContain("Quarterly revenue plan", written, StringComparison.Ordinal);
        Assert.Contains(item.EntityId.ToString(), written, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"itemCount\":1", written, StringComparison.Ordinal);
    }

    /// <summary>Refused against the shared constants, so the user is not told "too large" by a server 400 they
    /// cannot act on — and the content never leaves the device to find out.</summary>
    [Fact]
    public async Task StartAsync_OverThePerItemCap_RefusesWithoutReadingOrSending()
    {
        var huge = Item(chars: AssignmentInput.MaxItemChars + 1);
        var receipt = await _consent.RecordAsync("research", "Research", [huge], Ct);

        var outcome = await CreateSut().StartAsync(Request(huge), receipt, Ct);

        Assert.Equal(AssignmentStartStatus.TooLarge, outcome.Status);
        Assert.Empty(_trace);
    }

    [Fact]
    public async Task StartAsync_OverTheTotalCap_RefusesWithoutReadingOrSending()
    {
        var items = Enumerable.Range(0, 5).Select(_ => Item(chars: 7_000)).ToArray();
        var receipt = await _consent.RecordAsync("research", "Research", items, Ct);

        var outcome = await CreateSut().StartAsync(Request(items), receipt, Ct);

        Assert.Equal(AssignmentStartStatus.TooLarge, outcome.Status);
        Assert.Empty(_trace);
    }

    [Fact]
    public async Task StartAsync_RemembersTheRun_SoARestartCanStillCollectIt()
    {
        var item = Item();
        var receipt = await _consent.RecordAsync("research", "Research", [item], Ct);

        var outcome = await CreateSut().StartAsync(Request(item), receipt, Ct);

        var pending = Assert.Single(await _pending.GetAllAsync());
        Assert.Equal(outcome.AssignmentId, pending.AssignmentId);
        Assert.NotEqual(Guid.Empty, pending.ChatId);
    }

    // ---- the pull half -----------------------------------------------------------------------------

    /// <summary>
    /// Collect is irreversible, so the local write has to be the thing that has already succeeded when it is
    /// sent. This asserts the ORDER, which is the only way to catch an "optimisation" that acknowledges first.
    /// </summary>
    [Fact]
    public async Task DrainAsync_CommitsTheChatBeforeAcknowledging()
    {
        var run = await SeedPendingAsync();
        _api.Assignment = Completed(run.AssignmentId, "the finished answer");

        var finished = await CreateSut().DrainAsync(Ct);

        Assert.Equal(1, finished);
        Assert.Equal(["chat-write", "collect"], _trace);
        Assert.Empty(await _pending.GetAllAsync());

        // Kept, not deleted: this entry is the only thing that can still say which chat holds the answer once
        // the server has dropped its copy, and the job list reads it.
        var journalled = Assert.Single(await _pending.GetJournalAsync());
        Assert.Equal(run.ChatId, journalled.ChatId);
        Assert.NotNull(journalled.CollectedAtUtc);
    }

    /// <summary>The failure this ordering exists for: if the local write throws, the acknowledgement must not
    /// be sent and the run must stay pending, so the server still has the artifact next time.</summary>
    [Fact]
    public async Task DrainAsync_WhenTheLocalWriteFails_NeitherAcknowledgesNorForgets()
    {
        var run = await SeedPendingAsync();
        _api.Assignment = Completed(run.AssignmentId, "the finished answer");
        _chats.SaveAsync(Arg.Any<SyncAssistantChat>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new IOException("disk full"));

        await Assert.ThrowsAsync<IOException>(() => CreateSut().DrainAsync(Ct));

        Assert.DoesNotContain("collect", _trace);
        Assert.Single(await _pending.GetAllAsync());
    }

    /// <summary>A failed collect leaves the run pending too — but the artifact is already stored, so the retry
    /// is only about dropping the server's copy.</summary>
    [Fact]
    public async Task DrainAsync_WhenTheAcknowledgementFails_KeepsTheRunPending()
    {
        var run = await SeedPendingAsync();
        _api.Assignment = Completed(run.AssignmentId, "the finished answer");
        _api.CollectResult = false;

        var finished = await CreateSut().DrainAsync(Ct);

        Assert.Equal(0, finished);
        Assert.Contains("chat-write", _trace);
        Assert.Single(await _pending.GetAllAsync());
    }

    /// <summary>The chat id is minted before the run starts precisely so a second pass overwrites its own chat
    /// instead of leaving the user two copies of one answer.</summary>
    [Fact]
    public async Task DrainAsync_RunTwice_ReusesTheSameChatId()
    {
        var run = await SeedPendingAsync();
        _api.Assignment = Completed(run.AssignmentId, "the finished answer");
        _api.CollectResult = false;   // keeps it pending so the second pass sees it again

        var sut = CreateSut();
        await sut.DrainAsync(Ct);
        await sut.DrainAsync(Ct);

        var written = _chats.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IAssistantChatService.SaveAsync))
            .Select(c => ((SyncAssistantChat)c.GetArguments()[0]!).Id)
            .ToList();
        Assert.Equal(2, written.Count);
        Assert.Single(written.Distinct());
        Assert.Equal(run.ChatId, written[0]);
    }

    [Fact]
    public async Task DrainAsync_LeavesARunThatHasNotFinished_Alone()
    {
        var run = await SeedPendingAsync();
        _api.Assignment = new AssignmentDto(
            run.AssignmentId, "research", "Research", "Running", 1, 0, 0, DateTime.UtcNow, DateTime.UtcNow,
            DateTime.UtcNow, null, null, null, null);

        var finished = await CreateSut().DrainAsync(Ct);

        Assert.Equal(0, finished);
        Assert.Empty(_trace);
        Assert.Single(await _pending.GetAllAsync());
    }

    /// <summary>
    /// A run the server no longer answers for. The client cannot tell "deleted" from "unreachable", so without
    /// an age bound the entry would be polled every twenty seconds for the life of the app — and the artifact
    /// went with the plaintext long before, so there is nothing left to wait for.
    /// </summary>
    [Fact]
    public async Task DrainAsync_ARunTheServerNoLongerAnswersFor_IsEventuallyGivenUpOn()
    {
        var recent = new PendingAssignment(
            Guid.NewGuid(), Guid.NewGuid(), "research", "still hoping", DateTime.UtcNow.AddHours(-2));
        var ancient = new PendingAssignment(
            Guid.NewGuid(), Guid.NewGuid(), "research", "long gone",
            DateTime.UtcNow - AssignmentRunOrchestrator.AbandonAfter - TimeSpan.FromHours(1));
        await _pending.AddAsync(recent);
        await _pending.AddAsync(ancient);
        _api.Assignment = null;   // unreachable, or the row has been swept

        var finished = await CreateSut().DrainAsync(Ct);

        Assert.Equal(0, finished);
        Assert.Equal(recent.AssignmentId, Assert.Single(await _pending.GetAllAsync()).AssignmentId);
        // Nothing was stored or acknowledged for either: there was no artifact to store.
        Assert.Empty(_trace);
    }

    /// <summary>A failed run still produces a chat — the user asked for something and deserves to be told it
    /// did not work — and is still acknowledged, because dropping the server's plaintext is the right move
    /// whether or not there was an answer.</summary>
    [Fact]
    public async Task DrainAsync_AFailedRun_StillStoresSomethingAndAcknowledges()
    {
        var run = await SeedPendingAsync();
        _api.Assignment = new AssignmentDto(
            run.AssignmentId, "research", "Research", "Failed", 1, 0, 0, DateTime.UtcNow, DateTime.UtcNow,
            DateTime.UtcNow, DateTime.UtcNow, null, "operator_token_ceiling_exceeded", null);

        var finished = await CreateSut().DrainAsync(Ct);

        Assert.Equal(1, finished);
        Assert.Equal(["chat-write", "collect"], _trace);
    }

    /// <summary>
    /// The headless block, as a dependency direction rather than a runtime check: a background entry point that
    /// could reach the coordinator would satisfy every server-side check while nobody was present to consent.
    /// </summary>
    [Theory]
    [InlineData("HeadlessRunLauncher")]
    [InlineData("BackgroundAssistantTurnRunner")]
    [InlineData("HeadlessTurnExecutor")]
    [InlineData("ScheduledJobBackgroundService")]
    public void NoBackgroundEntryPoint_DependsOnTheAssignmentRunOrchestrator(string typeName)
    {
        var assembly = typeof(AssignmentRunOrchestrator).Assembly;
        var type = assembly.GetTypes().Single(t => t.Name == typeName);

        var offending = type.GetConstructors()
            .SelectMany(c => c.GetParameters())
            .Select(p => p.ParameterType)
            .Concat(type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                .Select(f => f.FieldType))
            .Where(t => t == typeof(IAssignmentRunOrchestrator) || t == typeof(AssignmentRunOrchestrator))
            .ToList();

        Assert.True(
            offending.Count == 0,
            $"{typeName} must not be able to start a background assignment: consent is a human act, and a " +
            "run created without one satisfies every server-side check with nobody present.");
    }

    private async Task<PendingAssignment> SeedPendingAsync()
    {
        var run = new PendingAssignment(
            Guid.NewGuid(), Guid.NewGuid(), "research", "what did we decide?", DateTime.UtcNow);
        await _pending.AddAsync(run);
        return run;
    }

    private static AssignmentDto Completed(Guid id, string artifactText) => new(
        id, "research", "Research", "Completed", 1, 100, 0, DateTime.UtcNow, DateTime.UtcNow,
        DateTime.UtcNow, DateTime.UtcNow, "{}", null, null, artifactText);

    private sealed class TraceScopeReader(List<string> trace) : IAssignmentScopeResolver
    {
        public Task<IReadOnlyList<AssignmentScopeItem>> ListAsync(
            IReadOnlyList<string> declaredInputTypes, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AssignmentScopeItem>>([]);

        public Task<string?> ReadTextAsync(AssignmentScopeItem item, CancellationToken ct = default)
        {
            trace.Add("read");
            return Task.FromResult<string?>("the record's content");
        }
    }

    private sealed class TraceApiClient : IAssignmentApiClient
    {
        public List<string> Trace { get; set; } = [];
        public int CreateCalls { get; private set; }
        public AssignmentDto? Assignment { get; set; }
        public bool CollectResult { get; set; } = true;

        public Task<AssignmentSurface> GetSurfaceAsync(CancellationToken ct = default) =>
            Task.FromResult(AssignmentSurface.Hidden);

        public Task<Guid?> CreateAsync(string skillName, AssignmentInput input, CancellationToken ct = default)
        {
            CreateCalls++;
            Trace.Add("create");
            return Task.FromResult<Guid?>(Guid.NewGuid());
        }

        public Task<IReadOnlyList<AssignmentDto>> ListAsync(
            int skip = 0, int limit = 50, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AssignmentDto>>(Assignment is null ? [] : [Assignment]);

        public Task<AssignmentDto?> GetAsync(Guid assignmentId, CancellationToken ct = default) =>
            Task.FromResult(Assignment);

        public Task<bool> CancelAsync(Guid assignmentId, CancellationToken ct = default)
        {
            Trace.Add("cancel");
            return Task.FromResult(true);
        }

        public Task<bool> CollectAsync(Guid assignmentId, CancellationToken ct = default)
        {
            Trace.Add("collect");
            return Task.FromResult(CollectResult);
        }
    }

    private sealed class InMemoryPendingStore : IAssignmentPendingStore
    {
        private readonly List<PendingAssignment> _pending = [];

        public Task<IReadOnlyList<PendingAssignment>> GetAllAsync() =>
            Task.FromResult<IReadOnlyList<PendingAssignment>>(
                _pending.Where(p => p.CollectedAtUtc is null).ToList());

        public Task<IReadOnlyList<PendingAssignment>> GetJournalAsync() =>
            Task.FromResult<IReadOnlyList<PendingAssignment>>(_pending.ToList());

        public Task AddAsync(PendingAssignment pending)
        {
            _pending.RemoveAll(p => p.AssignmentId == pending.AssignmentId);
            _pending.Add(pending);
            return Task.CompletedTask;
        }

        public Task MarkCollectedAsync(Guid assignmentId)
        {
            var index = _pending.FindIndex(p => p.AssignmentId == assignmentId);
            if (index >= 0) _pending[index] = _pending[index] with { CollectedAtUtc = DateTime.UtcNow };
            return Task.CompletedTask;
        }

        public Task RemoveAsync(Guid assignmentId)
        {
            _pending.RemoveAll(p => p.AssignmentId == assignmentId);
            return Task.CompletedTask;
        }
    }
}
