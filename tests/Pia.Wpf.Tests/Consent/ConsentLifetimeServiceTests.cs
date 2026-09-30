using System.IO;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Infrastructure.Vault;
using Pia.Services.Consent;
using Pia.Services.Interfaces;
using Pia.Services.Wiki;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Consent;

/// <summary>
/// Temp directories only: a real evidence store, vault and ingest state under one throwaway root, so nothing here
/// can reach the real profile, whose evidence folder ignores the profile override.
/// </summary>
public sealed class ConsentLifetimeServiceTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 9, 29, 10, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"pia-consent-lifetime-{Guid.NewGuid():N}");
    private readonly string _evidenceRoot;
    private readonly string _vaultRoot;
    private readonly MovableClock _clock = new(Start);
    private readonly ConsentEvidenceStore _store;
    private readonly IngestStateStore _ingest;
    private readonly IAssistantChatService _chats = Substitute.For<IAssistantChatService>();
    private readonly HashSet<Guid> _existingChats = [];
    private readonly IDirectTranscriptionService _transcription = Substitute.For<IDirectTranscriptionService>();
    private readonly ConsentLiveSessions _recordings = new(NullLogger<ConsentLiveSessions>.Instance);
    private readonly List<ConsentLifetimeService> _built = [];
    private bool _shuttingDown;

    public ConsentLifetimeServiceTests()
    {
        _evidenceRoot = Path.Combine(_root, "ConsentEvidence");
        _vaultRoot = Path.Combine(_root, "Vault");
        Directory.CreateDirectory(_evidenceRoot);
        Directory.CreateDirectory(_vaultRoot);

        var dpapi = Substitute.For<DpapiHelper>(NullLogger<DpapiHelper>.Instance);
        dpapi.Encrypt(Arg.Any<string>())
            .Returns(ci => Convert.ToBase64String(Encoding.UTF8.GetBytes(ci.Arg<string>())));
        dpapi.Decrypt(Arg.Any<string>())
            .Returns(ci => Encoding.UTF8.GetString(Convert.FromBase64String(ci.Arg<string>())));
        _store = new ConsentEvidenceStore(_evidenceRoot, dpapi, NullLogger<ConsentEvidenceStore>.Instance);

        _ingest = new IngestStateStore($"Data Source={Path.Combine(_root, "history.db")}");
        _chats.ExistsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(_existingChats.Contains(ci.Arg<Guid>())));
        _transcription.TranscriptSessionIds.Returns([]);
    }

    public void Dispose()
    {
        foreach (var sut in _built) sut.Dispose();
        TempPath.Remove(_root);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ConsentLifetimeService Build(bool dataRootsOverridden = false)
    {
        var sut = new ConsentLifetimeService(
            _evidenceRoot,
            new VaultStore(_vaultRoot, new MarkdownVaultParser()),
            _ingest,
            _chats,
            _store,
            _transcription,
            _recordings,
            _clock,
            dataRootsOverridden,
            () => _shuttingDown,
            NullLogger<ConsentLifetimeService>.Instance);
        _built.Add(sut);
        return sut;
    }

    private async Task<string> ConsentedSessionAsync()
    {
        var sessionId = Guid.NewGuid().ToString("N");
        var marker = new ConsentSessionMarker(
            sessionId, _clock.GetUtcNow(), ConsentSessionMarker.DirectKind, ConsentNotice.Version, ConsentNotice.Purposes, "de");
        var evidence = new ConsentEvidence(
            "Speaker 1", null, "yes, you may record", "en", 0.9f, _clock.GetUtcNow(), "whisper-base",
            ConsentNotice.Version, ConsentNotice.Purposes, "de");
        await _store.SaveGrantAsync(marker, evidence, Ct);
        Assert.True(File.Exists(Path.Combine(Folder(sessionId), ConsentEvidenceStore.SessionMarkerFileName)));
        return sessionId;
    }

    private string Folder(string sessionId) => Path.Combine(_evidenceRoot, sessionId);

    private Task LogAsync(string sessionId, ConsentCopy copy) => _store.AppendCopyAsync(sessionId, copy, Ct);

    private string Note(string relativePath, string sessionId, string newline = "\n", bool bom = false)
    {
        var path = Path.Combine(_vaultRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var text = string.Join(newline,
            "---", "schema: pia-meeting/v1", $"consentRecord: {ConsentFrontMatter.Schema}",
            $"consentSessions: [{sessionId}]", "consentNoticeVersion: 2", "---", "", "# Meeting", "");
        File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: bom));
        return path;
    }

    private string TopicPage(string name)
    {
        var path = Path.Combine(_vaultRoot, "memory", "topics", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "# Topic\n");
        return path;
    }

    // ---- Session end ---------------------------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SessionEnd_WithoutAManagedCopy_DeletesTheFolder_EvenWhenTheTranscriptWasExported(bool exported)
    {
        var sessionId = await ConsentedSessionAsync();
        if (exported)
            await LogAsync(sessionId, ConsentCopy.Export(@"C:\Users\x\transcript.md", _clock.GetUtcNow()));

        var outcome = await Build().HandleSessionEndedAsync(sessionId, Ct);

        Assert.False(Directory.Exists(Folder(sessionId)));
        Assert.Equal(1, outcome.Deleted);
    }

    [Fact]
    public async Task SessionEnd_WithAVaultNoteNamingTheSession_KeepsTheFolder()
    {
        var sessionId = await ConsentedSessionAsync();
        Note("sources/transcripts/meeting.md", sessionId);
        await LogAsync(sessionId, ConsentCopy.Vault("sources/transcripts/meeting.md", _clock.GetUtcNow()));

        var outcome = await Build().HandleSessionEndedAsync(sessionId, Ct);

        Assert.True(Directory.Exists(Folder(sessionId)));
        Assert.Equal(1, outcome.Kept);
    }

    [Fact]
    public async Task SessionEnd_ForAFolderWithoutAMarker_LeavesItToTheRetentionWindow()
    {
        var v1 = Path.Combine(_evidenceRoot, "v1session");
        Directory.CreateDirectory(v1);
        File.WriteAllText(Path.Combine(v1, "Speaker 1.json"), "protected");

        var sut = Build();
        var ended = await sut.HandleSessionEndedAsync("v1session", Ct);
        var swept = await sut.SweepAsync(Ct);

        Assert.True(Directory.Exists(v1));
        Assert.Equal(ConsentLifetimeOutcome.None, ended);
        Assert.Equal(ConsentLifetimeOutcome.None, swept);
    }

    [Theory]
    [InlineData("..")]
    [InlineData(@"..\ConsentEvidence")]
    public async Task SessionEnd_RefusesAnIdThatLeavesTheRoot(string sessionId)
    {
        await File.WriteAllTextAsync(Path.Combine(_root, ConsentEvidenceStore.SessionMarkerFileName), "x", Ct);

        var outcome = await Build().HandleSessionEndedAsync(sessionId, Ct);

        Assert.Equal(ConsentLifetimeOutcome.None, outcome);
        Assert.True(Directory.Exists(_evidenceRoot));
    }

    [Fact]
    public async Task TheSessionEndedEvent_DeletesTheFolder_OffTheRaisingThread()
    {
        var sessionId = await ConsentedSessionAsync();
        Build();

        _transcription.SessionEnded += Raise.Event<EventHandler<string>>(_transcription, sessionId);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (Directory.Exists(Folder(sessionId)) && DateTime.UtcNow < deadline)
            await Task.Delay(20, Ct);
        Assert.False(Directory.Exists(Folder(sessionId)));
    }

    /// <summary>At exit the chat store may already be disposed and answer "gone"; the next start sweeps instead.</summary>
    [Fact]
    public async Task TheSessionEndedEvent_DuringShutdown_DoesNothing()
    {
        var sessionId = await ConsentedSessionAsync();
        Build();
        _shuttingDown = true;

        _transcription.SessionEnded += Raise.Event<EventHandler<string>>(_transcription, sessionId);
        // Anything the event had scheduled would now be free to delete.
        _shuttingDown = false;
        await Task.Delay(300, Ct);

        Assert.True(Directory.Exists(Folder(sessionId)));
    }

    [Fact]
    public async Task WhileShuttingDown_NeitherEntryPointDeletes()
    {
        var sessionId = await ConsentedSessionAsync();
        var sut = Build();
        _shuttingDown = true;

        Assert.Equal(ConsentLifetimeOutcome.None, await sut.HandleSessionEndedAsync(sessionId, Ct));
        Assert.Equal(ConsentLifetimeOutcome.None, await sut.SweepAsync(Ct));
        Assert.True(Directory.Exists(Folder(sessionId)));
    }

    // ---- Sweep: vault notes and derived pages -------------------------------------------------------

    [Fact]
    public async Task ANoteMovedElsewhereInTheVault_StillKeepsTheFolder()
    {
        var sessionId = await ConsentedSessionAsync();
        var original = Note("sources/transcripts/meeting.md", sessionId, newline: "\r\n", bom: true);
        await LogAsync(sessionId, ConsentCopy.Vault("sources/transcripts/meeting.md", _clock.GetUtcNow()));
        var moved = Path.Combine(_vaultRoot, "archive", "2026", "renamed.md");
        Directory.CreateDirectory(Path.GetDirectoryName(moved)!);
        File.Move(original, moved);

        var outcome = await Build().SweepAsync(Ct);

        Assert.True(Directory.Exists(Folder(sessionId)));
        Assert.Equal(1, outcome.Kept);
        Assert.Equal(0, outcome.Deleted);
    }

    [Fact]
    public async Task ADeletedNote_WhoseTopicPageRemains_KeepsTheFolder_UntilThePageGoesToo()
    {
        var sessionId = await ConsentedSessionAsync();
        var note = Note("sources/transcripts/meeting.md", sessionId);
        await LogAsync(sessionId, ConsentCopy.Vault("sources/transcripts/meeting.md", _clock.GetUtcNow()));
        var page = TopicPage("alpha.md");
        await _ingest.UpsertAsync(new IngestStateEntry(
            "sources/transcripts/meeting.md", "hash", IngestOutcome.Success,
            ["memory/topics/alpha.md", "memory/topics/gone.md"], _clock.GetUtcNow()));
        File.Delete(note);
        var sut = Build();

        var whilePageExists = await sut.SweepAsync(Ct);
        Assert.True(Directory.Exists(Folder(sessionId)));
        Assert.Equal(1, whilePageExists.Kept);

        File.Delete(page);
        var afterPageGone = await sut.SweepAsync(Ct);

        Assert.False(Directory.Exists(Folder(sessionId)));
        Assert.Equal(1, afterPageGone.Deleted);
    }

    [Fact]
    public async Task ANoteThatCannotBeRead_KeepsTheFoldersItCouldHaveNamed()
    {
        var sessionId = await ConsentedSessionAsync();
        var unrelated = Note("sources/other.md", Guid.NewGuid().ToString("N"));

        ConsentLifetimeOutcome outcome;
        using (new FileStream(unrelated, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            outcome = await Build().SweepAsync(Ct);
        }

        Assert.True(Directory.Exists(Folder(sessionId)));
        Assert.Equal(1, outcome.Skipped);
        Assert.Equal(1, outcome.NotesUnreadable);

        var retried = await Build().SweepAsync(Ct);
        Assert.False(Directory.Exists(Folder(sessionId)));
        Assert.Equal(1, retried.Deleted);
    }

    [Fact]
    public async Task AVaultThatIsNotThere_DeletesNothing()
    {
        var sessionId = await ConsentedSessionAsync();
        Directory.Delete(_vaultRoot, recursive: true);

        var outcome = await Build().SweepAsync(Ct);

        Assert.True(Directory.Exists(Folder(sessionId)));
        Assert.Equal(1, outcome.Skipped);
    }

    [Fact]
    public async Task ACopiesLogThatCannotBeRead_KeepsTheFolder()
    {
        var sessionId = await ConsentedSessionAsync();
        var chatId = Guid.NewGuid();
        _existingChats.Add(chatId);
        await LogAsync(sessionId, ConsentCopy.Chat(chatId, _clock.GetUtcNow()));
        _clock.Advance(TimeSpan.FromDays(1));

        ConsentLifetimeOutcome outcome;
        using (new FileStream(
            Path.Combine(Folder(sessionId), ConsentEvidenceStore.CopiesFileName), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            outcome = await Build().SweepAsync(Ct);
        }

        Assert.True(Directory.Exists(Folder(sessionId)));
        Assert.Equal(1, outcome.Skipped);
    }

    // ---- Sweep: chats and the summary grace -----------------------------------------------------------

    [Fact]
    public async Task ALoggedChatThatExists_KeepsTheFolder_AndOnceDeletedTheSweepDeletes()
    {
        var sessionId = await ConsentedSessionAsync();
        var chatId = Guid.NewGuid();
        _existingChats.Add(chatId);
        await LogAsync(sessionId, ConsentCopy.SummaryRequested(_clock.GetUtcNow()));
        await LogAsync(sessionId, ConsentCopy.Chat(chatId, _clock.GetUtcNow()));
        _clock.Advance(TimeSpan.FromDays(1));
        var sut = Build();

        Assert.Equal(1, (await sut.SweepAsync(Ct)).Kept);
        Assert.True(Directory.Exists(Folder(sessionId)));

        _existingChats.Remove(chatId);
        var outcome = await sut.SweepAsync(Ct);

        Assert.False(Directory.Exists(Folder(sessionId)));
        Assert.Equal(1, outcome.Deleted);
    }

    [Fact]
    public async Task ASummaryRequestInsideTheGrace_KeepsTheFolder_AndAfterItWithoutAChatTheSweepDeletes()
    {
        var sessionId = await ConsentedSessionAsync();
        await LogAsync(sessionId, ConsentCopy.SummaryRequested(_clock.GetUtcNow()));
        _clock.Advance(ConsentLifetimeService.SummaryGrace - TimeSpan.FromSeconds(1));
        var sut = Build();

        Assert.Equal(1, (await sut.HandleSessionEndedAsync(sessionId, Ct)).Kept);
        Assert.True(Directory.Exists(Folder(sessionId)));

        _clock.Advance(TimeSpan.FromSeconds(2));
        var outcome = await sut.SweepAsync(Ct);

        Assert.False(Directory.Exists(Folder(sessionId)));
        Assert.Equal(1, outcome.Deleted);
    }

    /// <summary>The id is reported as the first turn starts, before the row that <c>ExistsAsync</c> counts.</summary>
    [Fact]
    public async Task AChatNotYetStored_KeepsTheFolderInsideTheGrace_AndCountsAsGoneAfterIt()
    {
        var sessionId = await ConsentedSessionAsync();
        await LogAsync(sessionId, ConsentCopy.Chat(Guid.NewGuid(), _clock.GetUtcNow()));
        _clock.Advance(TimeSpan.FromMinutes(1));
        var sut = Build();

        Assert.Equal(1, (await sut.SweepAsync(Ct)).Kept);

        _clock.Advance(ConsentLifetimeService.SummaryGrace);
        Assert.Equal(1, (await sut.SweepAsync(Ct)).Deleted);
        Assert.False(Directory.Exists(Folder(sessionId)));
    }

    // ---- Sweep: scope --------------------------------------------------------------------------------

    [Fact]
    public async Task TheSweep_SkipsTheTranscriptStillOpen()
    {
        var live = await ConsentedSessionAsync();
        var ended = await ConsentedSessionAsync();
        _transcription.TranscriptSessionIds.Returns([live]);

        var outcome = await Build().SweepAsync(Ct);

        Assert.True(Directory.Exists(Folder(live)));
        Assert.False(Directory.Exists(Folder(ended)));
        Assert.Equal(1, outcome.Kept);
        Assert.Equal(1, outcome.Deleted);
    }

    [Fact]
    public async Task TheSweep_SkipsARecordingStillRunning()
    {
        var recording = await ConsentedSessionAsync();
        var ended = await ConsentedSessionAsync();
        _recordings.Register(recording);

        var outcome = await Build().SweepAsync(Ct);

        Assert.True(Directory.Exists(Folder(recording)));
        Assert.False(Directory.Exists(Folder(ended)));
        Assert.Equal(1, outcome.Kept);
        Assert.Equal(1, outcome.Deleted);
    }

    [Fact]
    public async Task ARecordingThatEnds_WithoutAManagedCopy_LosesItsFolder_OffTheRaisingThread()
    {
        var sessionId = await ConsentedSessionAsync();
        _recordings.Register(sessionId);
        Build();

        _recordings.Unregister(sessionId);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (Directory.Exists(Folder(sessionId)) && DateTime.UtcNow < deadline)
            await Task.Delay(20, Ct);
        Assert.False(Directory.Exists(Folder(sessionId)));
    }

    [Fact]
    public async Task ARecordingThatEnds_DuringShutdown_KeepsItsFolder()
    {
        var sessionId = await ConsentedSessionAsync();
        _recordings.Register(sessionId);
        Build();
        _shuttingDown = true;

        _recordings.Unregister(sessionId);
        _shuttingDown = false;
        await Task.Delay(300, Ct);

        Assert.True(Directory.Exists(Folder(sessionId)));
    }

    [Fact]
    public async Task TheSweep_IgnoresAV1Folder()
    {
        var v1 = Path.Combine(_evidenceRoot, "v1session");
        Directory.CreateDirectory(v1);
        File.WriteAllText(Path.Combine(v1, "Speaker 1.json"), "protected");
        var v2 = await ConsentedSessionAsync();

        var outcome = await Build().SweepAsync(Ct);

        Assert.True(Directory.Exists(v1));
        Assert.False(Directory.Exists(Folder(v2)));
        Assert.Equal(1, outcome.Deleted);
        Assert.Equal(0, outcome.Kept);
    }

    /// <summary>The vault and the chats then belong to a throwaway profile, the evidence folder to the real one.</summary>
    [Fact]
    public async Task OverriddenDataRoots_DeleteNothing()
    {
        var sessionId = await ConsentedSessionAsync();
        var sut = Build(dataRootsOverridden: true);

        Assert.Equal(ConsentLifetimeOutcome.None, await sut.HandleSessionEndedAsync(sessionId, Ct));
        Assert.Equal(ConsentLifetimeOutcome.None, await sut.SweepAsync(Ct));
        _transcription.SessionEnded += Raise.Event<EventHandler<string>>(_transcription, sessionId);
        await Task.Delay(300, Ct);

        Assert.True(Directory.Exists(Folder(sessionId)));
    }

    private sealed class MovableClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public void Advance(TimeSpan by) => _now += by;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
