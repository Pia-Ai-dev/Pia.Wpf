using System.IO;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Infrastructure.Vault;
using Pia.Services.Consent;
using Pia.Services.Interfaces;
using Pia.Tests.TestInfrastructure;
using Pia.ViewModels;
using Pia.ViewModels.Models;
using Xunit;

namespace Pia.Tests.ViewModels;

/// <summary>A real vault and evidence store under one throwaway root; only the chat store and the ingest are doubles.</summary>
public sealed class ConsentCopiesViewModelTests : IDisposable
{
    private const string SessionA = "3f2a9c1e7b4d4e0f8a6b5c4d3e2f1a0b";
    private const string SessionB = "9b1c2d3e4f5a6b7c8d9e0f1a2b3c4d5e";
    private const string Unrelated = "0a1b2c3d4e5f60718293a4b5c6d7e8f9";

    private static readonly DateTimeOffset GrantedAt = new(2026, 9, 29, 10, 1, 2, TimeSpan.FromHours(2));
    private static readonly DateTimeOffset RevokedAt = new(2026, 9, 29, 10, 20, 0, TimeSpan.FromHours(2));

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"pia-consent-copies-{Guid.NewGuid():N}");
    private readonly string _vaultRoot;
    private readonly string _evidenceRoot;
    private readonly ConsentEvidenceStore _store;
    private readonly IIngestScheduler _ingest = Substitute.For<IIngestScheduler>();
    private readonly IAssistantChatService _chats = Substitute.For<IAssistantChatService>();
    private readonly HashSet<Guid> _storedChats = [];
    private readonly IChatSessionManager _sessions = Substitute.For<IChatSessionManager>();

    public ConsentCopiesViewModelTests()
    {
        _vaultRoot = Path.Combine(_root, "Vault");
        _evidenceRoot = Path.Combine(_root, "ConsentEvidence");
        Directory.CreateDirectory(_vaultRoot);
        Directory.CreateDirectory(_evidenceRoot);

        var dpapi = Substitute.For<DpapiHelper>(NullLogger<DpapiHelper>.Instance);
        dpapi.Encrypt(Arg.Any<string>())
            .Returns(ci => Convert.ToBase64String(Encoding.UTF8.GetBytes(ci.Arg<string>())));
        dpapi.Decrypt(Arg.Any<string>())
            .Returns(ci => Encoding.UTF8.GetString(Convert.FromBase64String(ci.Arg<string>())));
        _store = new ConsentEvidenceStore(_evidenceRoot, dpapi, NullLogger<ConsentEvidenceStore>.Instance);

        _chats.ExistsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(_storedChats.Contains(ci.Arg<Guid>())));
        _chats.DeleteAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                _storedChats.Remove(ci.Arg<Guid>());
                return Task.CompletedTask;
            });
    }

    public void Dispose() => TempPath.Remove(_root);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ConsentCopiesViewModel Build() => new(
        new ConsentCopyService(
            new VaultStore(_vaultRoot, new MarkdownVaultParser()),
            _ingest,
            _chats,
            _store,
            NullLogger<ConsentCopyService>.Instance),
        NullLogger<ConsentCopiesViewModel>.Instance,
        _sessions);

    private static ConsentRecord Record(IReadOnlyList<string> sessionIds, params ConsentRecordEntry[] consents)
        => new(sessionIds, ConsentNotice.Version, ConsentNotice.Purposes, "de", consents);

    private string NotePath(string reference) => Path.Combine(_vaultRoot, reference.Replace('/', Path.DirectorySeparatorChar));

    private string Note(string reference, ConsentRecord record)
    {
        var path = NotePath(reference);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var lines = new List<string> { "---", "schema: pia-meeting/v1", "title: Kickoff" };
        lines.AddRange(ConsentFrontMatter.Render(record));
        lines.AddRange(["---", "", "# Kickoff", "", "**Anna** _10:02:00_", "hello there", ""]);
        File.WriteAllText(path, string.Join("\n", lines));
        return path;
    }

    private string Note(string reference, string sessionId)
        => Note(reference, Record([sessionId], new ConsentRecordEntry("Speaker 1", GrantedAt, null, "Anna", sessionId)));

    // The store logs copies only for a session somebody consented in.
    private async Task LogAsync(string sessionId, ConsentCopy copy)
    {
        if (!File.Exists(Path.Combine(_evidenceRoot, sessionId, ConsentEvidenceStore.SessionMarkerFileName)))
            await GrantAsync(sessionId);
        await _store.AppendCopyAsync(sessionId, copy, Ct);
    }

    private async Task GrantAsync(string sessionId)
    {
        var marker = new ConsentSessionMarker(
            sessionId, GrantedAt, ConsentSessionMarker.DirectKind, ConsentNotice.Version, ConsentNotice.Purposes, "de");
        var evidence = new ConsentEvidence(
            "Speaker 1", "Anna", "yes, you may record", "en", 0.9f, GrantedAt, "whisper-base",
            ConsentNotice.Version, ConsentNotice.Purposes, "de");
        await _store.SaveGrantAsync(marker, evidence, Ct);
    }

    private static ChatSession NewSession() => new(
        Substitute.For<ITokenMapService>(),
        Substitute.For<IAiClientService>(),
        Substitute.For<IPluginService>(),
        Substitute.For<IActionCardBuilder>(),
        Substitute.For<IToolPermissionService>(),
        Substitute.For<ILocalizationService>(),
        NullLogger.Instance,
        _ => true);

    // ---- Listing -------------------------------------------------------------------------------------

    [Fact]
    public async Task Load_ListsTheNotesNamingAnyOfTheSessions_AndNoOthers()
    {
        Note("sources/a.md", SessionA);
        Note("notes/moved/b.md", SessionB);
        Note("sources/other.md", Unrelated);
        var sut = Build();

        await sut.LoadAsync([SessionA, SessionB], Ct);

        Assert.Equal(["notes/moved/b.md", "sources/a.md"], sut.Notes.Select(n => n.Reference));
        Assert.Equal([1, 2], sut.Notes.Select(n => n.Key));
        Assert.True(sut.HasNotes);
        Assert.True(sut.HasCopies);
        Assert.False(sut.HasExports);
        Assert.False(sut.HasChats);
    }

    [Fact]
    public async Task Load_WithoutAnyCopy_HasNone()
    {
        Note("sources/other.md", Unrelated);
        var sut = Build();

        await sut.LoadAsync([SessionA], Ct);

        Assert.False(sut.HasCopies);
    }

    [Fact]
    public async Task Exports_AreListedOncePerFile_LatestFirst_AndNothingHereDeletesThem()
    {
        var exported = Path.Combine(_root, "Exports", "kickoff.md");
        Directory.CreateDirectory(Path.GetDirectoryName(exported)!);
        File.WriteAllText(exported, "# Kickoff\n");
        var other = Path.Combine(_root, "Exports", "retro.md");
        var t0 = new DateTimeOffset(2026, 9, 29, 8, 0, 0, TimeSpan.Zero);
        // One save is logged under every session of the transcript.
        await LogAsync(SessionA, ConsentCopy.Export(exported, t0));
        await LogAsync(SessionB, ConsentCopy.Export(exported, t0));
        await LogAsync(SessionA, ConsentCopy.Export(other, t0.AddMinutes(5)));
        await LogAsync(SessionB, ConsentCopy.Export(exported, t0.AddMinutes(10)));
        Note("sources/a.md", SessionA);
        var sut = Build();

        await sut.LoadAsync([SessionA, SessionB], Ct);
        await sut.DeleteAllNotesCommand.ExecuteAsync(null);
        await sut.DeleteAllChatsCommand.ExecuteAsync(null);

        Assert.Equal(
            [(exported, t0.AddMinutes(10).LocalDateTime), (other, t0.AddMinutes(5).LocalDateTime)],
            sut.Exports.Select(e => (e.Path, e.SavedAt)));
        Assert.True(File.Exists(exported));
    }

    [Fact]
    public async Task AnUnreadableCopiesLog_StillListsWhatTheScanFinds_AndSaysSo()
    {
        await LogAsync(SessionA, ConsentCopy.SummaryRequested(GrantedAt));
        Note("sources/a.md", SessionA);
        var sut = Build();

        using (new FileStream(
            Path.Combine(_evidenceRoot, SessionA, ConsentEvidenceStore.CopiesFileName), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await sut.LoadAsync([SessionA], Ct);
        }

        Assert.True(sut.CopiesLogUnreadable);
        Assert.Equal(["sources/a.md"], sut.Notes.Select(n => n.Reference));
        Assert.True(sut.HasCopies);
    }

    [Fact]
    public async Task AnUnreadableCopiesLog_AloneStillCountsAsACopy()
    {
        await LogAsync(SessionA, ConsentCopy.SummaryRequested(GrantedAt));
        var sut = Build();

        using (new FileStream(
            Path.Combine(_evidenceRoot, SessionA, ConsentEvidenceStore.CopiesFileName), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await sut.LoadAsync([SessionA], Ct);
        }

        Assert.Empty(sut.Notes);
        Assert.True(sut.HasCopies);
    }

    [Fact]
    public async Task AnUnreadableNote_StillListsTheOthers_AndSaysSomeCouldNotBeChecked()
    {
        Note("sources/a.md", SessionA);
        var locked = Note("sources/locked.md", Unrelated);
        await LogAsync(SessionA, ConsentCopy.Vault("sources/a.md", GrantedAt));
        var sut = Build();

        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await sut.LoadAsync([SessionA], Ct);
        }

        Assert.True(sut.VaultUnchecked);
        Assert.False(sut.CopiesLogUnreadable);
        Assert.Equal(["sources/a.md"], sut.Notes.Select(n => n.Reference));
    }

    [Fact]
    public async Task AVaultThatIsNotThere_AfterAVaultSave_AloneStillCountsAsACopy()
    {
        await LogAsync(SessionA, ConsentCopy.Vault("sources/a.md", GrantedAt));
        Directory.Delete(_vaultRoot, recursive: true);
        var sut = Build();

        await sut.LoadAsync([SessionA], Ct);

        Assert.True(sut.VaultUnchecked);
        Assert.Empty(sut.Notes);
        Assert.True(sut.HasCopies);
    }

    [Fact]
    public async Task AVaultThatIsNotThere_WithoutAVaultSave_IsNotACopy()
    {
        Directory.Delete(_vaultRoot, recursive: true);
        var sut = Build();

        await sut.LoadAsync([SessionA], Ct);

        Assert.False(sut.VaultUnchecked);
        Assert.False(sut.HasCopies);
    }

    [Fact]
    public async Task ACompleteScan_ChecksEveryNote()
    {
        Note("sources/a.md", SessionA);
        var sut = Build();

        await sut.LoadAsync([SessionA], Ct);

        Assert.False(sut.VaultUnchecked);
    }

    // ---- Vault notes ---------------------------------------------------------------------------------

    [Fact]
    public async Task DeleteNote_DeletesIt_AndRemovesWhatItContributedToTheTopicPages()
    {
        var path = Note("sources/a.md", SessionA);
        var sut = Build();
        await sut.LoadAsync([SessionA], Ct);

        await sut.DeleteNoteCommand.ExecuteAsync(sut.Notes.Single());

        Assert.False(File.Exists(path));
        Assert.Empty(sut.Notes);
        Assert.False(sut.DeleteFailed);
        await _ingest.Received(1).RemoveAsync("sources/a.md", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteNote_RefusesANoteThatNoLongerNamesTheSession()
    {
        var path = Note("sources/a.md", SessionA);
        var sut = Build();
        await sut.LoadAsync([SessionA], Ct);
        // Edited between listing and deleting: the note is no longer this session's copy.
        Note("sources/a.md", Unrelated);

        await sut.DeleteNoteCommand.ExecuteAsync(sut.Notes.Single());

        Assert.True(File.Exists(path));
        Assert.Single(sut.Notes);
        Assert.True(sut.DeleteFailed);
        await _ingest.DidNotReceive().RemoveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteNote_RefusesAReferenceOutsideTheVault()
    {
        var outside = Path.Combine(_root, "outside.md");
        File.WriteAllText(outside, $"---\nconsentSessions: [{SessionA}]\n---\n");
        var service = new ConsentCopyService(
            new VaultStore(_vaultRoot, new MarkdownVaultParser()), _ingest, _chats, _store, NullLogger<ConsentCopyService>.Instance);

        Assert.False(await service.DeleteNoteAsync("../outside.md", [SessionA], Ct));
        Assert.False(await service.DeleteNoteAsync(outside, [SessionA], Ct));

        Assert.True(File.Exists(outside));
    }

    [Fact]
    public async Task DeleteAllNotes_DeletesEveryListedNote()
    {
        var a = Note("sources/a.md", SessionA);
        var b = Note("notes/b.md", SessionB);
        var sut = Build();
        await sut.LoadAsync([SessionA, SessionB], Ct);

        await sut.DeleteAllNotesCommand.ExecuteAsync(null);

        Assert.False(File.Exists(a));
        Assert.False(File.Exists(b));
        Assert.False(sut.HasNotes);
        await _ingest.Received(1).RemoveAsync("sources/a.md", Arg.Any<CancellationToken>());
        await _ingest.Received(1).RemoveAsync("notes/b.md", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task KeptNotes_RecordTheRevocation_AndKeepTheirOwnShownAs()
    {
        var path = Note("sources/a.md", SessionA);
        var sut = Build();
        await sut.LoadAsync([SessionA], Ct);

        await sut.RecordRevocationInKeptNotesAsync(
            Record([SessionA], new ConsentRecordEntry("Speaker 1", GrantedAt, RevokedAt, null, SessionA)), Ct);

        var text = File.ReadAllText(path);
        Assert.Contains(
            "  - {label: Speaker 1, shownAs: Anna, grantedAt: '2026-09-29T10:01:02+02:00', revokedAt: '2026-09-29T10:20:00+02:00'}\n",
            text,
            StringComparison.Ordinal);
        Assert.Contains("title: Kickoff\n", text, StringComparison.Ordinal);
        Assert.Contains("hello there", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AKeptNote_DoesNotClaimALaterSessionOfTheTranscript()
    {
        // Saved before the retry, so it holds none of the second session's words.
        var path = Note("sources/a.md", SessionA);
        var sut = Build();
        await sut.LoadAsync([SessionA, SessionB], Ct);

        await sut.RecordRevocationInKeptNotesAsync(
            Record(
                [SessionA, SessionB],
                new ConsentRecordEntry("Speaker 1", GrantedAt, RevokedAt, null, SessionA),
                new ConsentRecordEntry("Speaker 2", GrantedAt, null, null, SessionB)),
            Ct);

        var text = File.ReadAllText(path);
        Assert.Contains($"consentSessions: [{SessionA}]\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Speaker 2", text, StringComparison.Ordinal);
        Assert.Contains("revokedAt: '2026-09-29T10:20:00+02:00'", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADeletedNote_IsNotWrittenBackByTheRevocationRecord()
    {
        var deleted = Note("sources/a.md", SessionA);
        var kept = Note("sources/b.md", SessionA);
        var sut = Build();
        await sut.LoadAsync([SessionA], Ct);

        await sut.DeleteNoteCommand.ExecuteAsync(sut.Notes.First(n => n.Reference == "sources/a.md"));
        await sut.RecordRevocationInKeptNotesAsync(
            Record([SessionA], new ConsentRecordEntry("Speaker 1", GrantedAt, RevokedAt, null, SessionA)), Ct);

        Assert.False(File.Exists(deleted));
        Assert.Contains("revokedAt:", File.ReadAllText(kept), StringComparison.Ordinal);
    }

    // ---- Chats ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Chats_ListTheLoggedSummaryChatsStillStored_OnceEach()
    {
        var stored = Guid.NewGuid();
        var gone = Guid.NewGuid();
        _storedChats.Add(stored);
        await LogAsync(SessionA, ConsentCopy.Chat(stored, GrantedAt));
        await LogAsync(SessionB, ConsentCopy.Chat(stored, GrantedAt));
        await LogAsync(SessionA, ConsentCopy.Chat(gone, GrantedAt));
        var sut = Build();

        await sut.LoadAsync([SessionA, SessionB], Ct);

        Assert.Equal([stored], sut.Chats.Select(c => c.ChatId));
        Assert.True(sut.HasCopies);
    }

    [Fact]
    public async Task DeleteChat_DeletesIt()
    {
        var chatId = Guid.NewGuid();
        _storedChats.Add(chatId);
        await LogAsync(SessionA, ConsentCopy.Chat(chatId, GrantedAt));
        var sut = Build();
        await sut.LoadAsync([SessionA], Ct);

        await sut.DeleteChatCommand.ExecuteAsync(sut.Chats.Single());

        await _chats.Received(1).DeleteAsync(chatId, Arg.Any<CancellationToken>());
        Assert.Empty(sut.Chats);
        _sessions.DidNotReceive().GetOrCreateActiveForNewChat();
    }

    [Fact]
    public async Task DeleteAllChats_MovesTheOpenSummaryChatToAFreshOne()
    {
        // Left open, the chat's next turn would store it again.
        var open = Guid.NewGuid();
        var other = Guid.NewGuid();
        _storedChats.UnionWith([open, other]);
        await LogAsync(SessionA, ConsentCopy.Chat(open, GrantedAt));
        await LogAsync(SessionA, ConsentCopy.Chat(other, GrantedAt.AddMinutes(1)));
        var session = NewSession();
        session.SetIdentity(open, DateTime.UtcNow, null, null, false);
        _sessions.ActiveSession.Returns(session);
        _sessions.GetOrCreateActiveForNewChat().Returns(NewSession());
        var sut = Build();
        await sut.LoadAsync([SessionA], Ct);

        await sut.DeleteAllChatsCommand.ExecuteAsync(null);

        Assert.Empty(_storedChats);
        Assert.False(sut.HasChats);
        _sessions.Received(1).GetOrCreateActiveForNewChat();
    }

    // ---- Evidence ------------------------------------------------------------------------------------

    [Fact]
    public async Task TheLiveSessionsEvidenceFolder_OutlastsTheDialog()
    {
        await GrantAsync(SessionA);
        var chatId = Guid.NewGuid();
        _storedChats.Add(chatId);
        await LogAsync(SessionA, ConsentCopy.Chat(chatId, GrantedAt));
        Note("sources/a.md", SessionA);
        var sut = Build();
        await sut.LoadAsync([SessionA], Ct);

        await sut.DeleteAllNotesCommand.ExecuteAsync(null);
        await sut.DeleteAllChatsCommand.ExecuteAsync(null);
        await sut.RecordRevocationInKeptNotesAsync(
            Record([SessionA], new ConsentRecordEntry("Speaker 1", GrantedAt, RevokedAt, null, SessionA)), Ct);

        Assert.True(File.Exists(Path.Combine(_evidenceRoot, SessionA, ConsentEvidenceStore.SessionMarkerFileName)));
        Assert.True(File.Exists(Path.Combine(_evidenceRoot, SessionA, ConsentEvidenceStore.CopiesFileName)));
    }
}
