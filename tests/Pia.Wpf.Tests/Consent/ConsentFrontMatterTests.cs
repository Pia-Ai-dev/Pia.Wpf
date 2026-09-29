using Microsoft.Extensions.Logging.Abstractions;
using Pia.Infrastructure.Vault;
using Pia.Models;
using Pia.Services.Consent;
using Pia.Services.LiveTranscription;
using Xunit;

namespace Pia.Tests.Consent;

public sealed class ConsentFrontMatterTests
{
    private const string SessionA = "3f2a9c1e7b4d4e0f8a6b5c4d3e2f1a0b";
    private const string SessionB = "9b1c2d3e4f5a6b7c8d9e0f1a2b3c4d5e";

    private static readonly DateTimeOffset Granted = new(2026, 9, 29, 10, 1, 2, TimeSpan.FromHours(2));
    private static readonly DateTimeOffset Revoked = new(2026, 9, 29, 10, 20, 0, TimeSpan.FromHours(2));

    private static ConsentRecord Record(params ConsentRecordEntry[] consents)
        => new([SessionA, SessionB], 1, ["transcribe", "store", "summarize"], "de", consents);

    private static string Note(params string[] frontMatter)
        => "---\n" + string.Join("\n", frontMatter) + "\n---\n# Meeting\n\nbody text\n";

    [Fact]
    public void Render_WritesTopLevelKeys_WithOneFlowMapPerSpeaker()
    {
        var lines = ConsentFrontMatter.Render(Record(
            new ConsentRecordEntry("Speaker 1", Granted),
            new ConsentRecordEntry("Speaker 2", Granted, Revoked)));

        Assert.Equal(
            [
                "consentRecord: pia-consent-record/v1",
                $"consentSessions: [{SessionA}, {SessionB}]",
                "consentNoticeVersion: 1",
                "consentNoticePurposes: [transcribe, store, summarize]",
                "consentNoticeLanguage: de",
                "consents:",
                "  - {label: Speaker 1, grantedAt: '2026-09-29T10:01:02+02:00'}",
                "  - {label: Speaker 2, grantedAt: '2026-09-29T10:01:02+02:00', revokedAt: '2026-09-29T10:20:00+02:00'}",
            ],
            lines);
    }

    [Fact]
    public void Render_WithoutAForeignSpeaker_WritesNothing()
    {
        Assert.Empty(ConsentFrontMatter.Render(Record()));
    }

    [Fact]
    public void Render_ForAHostAcknowledgement_WritesItInsteadOfConsents()
    {
        var record = new ConsentRecord([SessionA], 1, ["transcribe", "store"], "en", [], Granted);

        var lines = ConsentFrontMatter.Render(record);

        Assert.Contains("hostAcknowledgedAt: '2026-09-29T10:01:02+02:00'", lines);
        Assert.DoesNotContain(lines, l => l.StartsWith("consents", StringComparison.Ordinal));
        Assert.Contains("consentNoticePurposes: [transcribe, store]", lines);
    }

    [Fact]
    public void ForSpeakers_KeepsTheDetectedLabel_AfterARename()
    {
        var consent = new ConsentStateManager(NullLogger<ConsentStateManager>.Instance, TimeProvider.System);
        consent.GetOrCreate("Speaker 2");
        consent.Grant("Speaker 2", "Anna", Evidence("Speaker 2"));
        Assert.True(consent.Rename("Speaker 2", "Anna"));
        consent.Revoke("Anna", Revoked);
        consent.GetOrCreate("Speaker 3");

        var record = ConsentRecord.ForSpeakers([SessionA], 1, ["transcribe"], "en", consent.Snapshot());

        var entry = Assert.Single(record.Consents);
        Assert.Equal(new ConsentRecordEntry("Speaker 2", Granted, Revoked), entry);
        Assert.DoesNotContain(ConsentFrontMatter.Render(record), l => l.Contains("Anna", StringComparison.Ordinal));
    }

    [Fact]
    public void DirectTranscript_RenderThenReadSessions_RoundTrips()
    {
        var markdown = DirectTranscriptMarkdown.Render(
            "Title", Granted, Revoked, [], [], null, Record(new ConsentRecordEntry("Speaker 1", Granted)));

        Assert.Equal([SessionA, SessionB], ConsentFrontMatter.ReadSessions(markdown));
    }

    [Fact]
    public void DirectTranscript_WithoutARecord_HasNoConsentBlock()
    {
        var withoutRecord = DirectTranscriptMarkdown.Render("Title", Granted, Revoked, [], [], null);
        var withoutSpeaker = DirectTranscriptMarkdown.Render("Title", Granted, Revoked, [], [], null, Record());

        Assert.DoesNotContain("consent", withoutRecord, StringComparison.Ordinal);
        Assert.Equal(withoutRecord, withoutSpeaker);
        Assert.Empty(ConsentFrontMatter.ReadSessions(withoutRecord));
    }

    [Fact]
    public void VaultNote_WithARecord_StillParsesAsFrontMatter_AndRoundTrips()
    {
        var meta = new MeetingVaultMetadata("Sync", Granted, Revoked, "direct", [], [], null, null);

        var markdown = MeetingVaultMarkdown.Render(
            meta, "# Meeting\n", Record(new ConsentRecordEntry("Speaker 1", Granted, Revoked)));

        var parsed = new MarkdownVaultParser().Parse(markdown);
        Assert.Equal(ConsentFrontMatter.Schema, parsed.Frontmatter["consentRecord"]);
        Assert.Equal("Sync", parsed.Frontmatter["title"]);
        Assert.Equal([SessionA, SessionB], ConsentFrontMatter.ReadSessions(markdown));
        Assert.EndsWith("---\n# Meeting\n", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadSessions_IgnoresTheBody()
    {
        var note = Note("title: x") + $"consentSessions: [{SessionA}]\n";

        Assert.Empty(ConsentFrontMatter.ReadSessions(note));
    }

    [Fact]
    public void ReadSessions_ReadsABlockListAnEditorWroteBack_WithQuotesCrlfAndBom()
    {
        var note = "﻿---\r\ntitle: x\r\nconsentSessions:\r\n  - '" + SessionA + "'\r\n  - \"" + SessionB
                   + "\"\r\nconsentNoticeVersion: 1\r\n---\r\nbody\r\n";

        Assert.Equal([SessionA, SessionB], ConsentFrontMatter.ReadSessions(note));
    }

    [Fact]
    public void ReadSessions_WithoutFrontMatter_IsEmpty()
    {
        Assert.Empty(ConsentFrontMatter.ReadSessions($"consentSessions: [{SessionA}]\n"));
        Assert.Empty(ConsentFrontMatter.ReadSessions("---\nconsentSessions: [" + SessionA + "]\n"));
        Assert.Empty(ConsentFrontMatter.ReadSessions(null));
    }

    [Fact]
    public void ReplaceConsents_SwapsTheBlockInPlace_AndLeavesEverythingElse()
    {
        var original = MeetingVaultMarkdown.Render(
            new MeetingVaultMetadata("Sync", Granted, Revoked, "direct", [], ["roadmap"], null, null),
            "# Meeting\n\nconsents: in the body\n",
            Record(new ConsentRecordEntry("Speaker 1", Granted)));

        var updated = ConsentFrontMatter.ReplaceConsents(
            original, Record(new ConsentRecordEntry("Speaker 1", Granted, Revoked)));

        var expected = original.Replace(
            "  - {label: Speaker 1, grantedAt: '2026-09-29T10:01:02+02:00'}\n",
            "  - {label: Speaker 1, grantedAt: '2026-09-29T10:01:02+02:00', revokedAt: '2026-09-29T10:20:00+02:00'}\n",
            StringComparison.Ordinal);
        Assert.NotEqual(original, expected);
        Assert.Equal(expected, updated);
    }

    [Fact]
    public void ReplaceConsents_WhenTheUserRemovedTheBlock_AddsItBeforeTheClosingDelimiter()
    {
        var note = Note("title: Sync", "tags: [roadmap]");

        var updated = ConsentFrontMatter.ReplaceConsents(note, Record(new ConsentRecordEntry("Speaker 1", Granted)));

        Assert.StartsWith("---\ntitle: Sync\ntags: [roadmap]\nconsentRecord: pia-consent-record/v1\n", updated, StringComparison.Ordinal);
        Assert.EndsWith(
            "consents:\n  - {label: Speaker 1, grantedAt: '2026-09-29T10:01:02+02:00'}\n---\n# Meeting\n\nbody text\n",
            updated,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ReplaceConsents_DropsEveryLineOfAReflowedBlock()
    {
        var note = Note(
            "title: Sync",
            "consentRecord: pia-consent-record/v1",
            "consentSessions:",
            $"  - {SessionA}",
            "consents:",
            "- label: Speaker 1",
            "  grantedAt: '2026-09-29T10:01:02+02:00'",
            "tags: [roadmap]");

        var updated = ConsentFrontMatter.ReplaceConsents(note, Record(new ConsentRecordEntry("Speaker 1", Granted, Revoked)));

        var expected = Note(
            ["title: Sync", .. ConsentFrontMatter.Render(Record(new ConsentRecordEntry("Speaker 1", Granted, Revoked))), "tags: [roadmap]"]);
        Assert.Equal(expected, updated);
    }

    [Fact]
    public void ReplaceConsents_WithAnEmptyRecord_RemovesTheBlock()
    {
        var note = Note(["title: Sync", .. ConsentFrontMatter.Render(Record(new ConsentRecordEntry("Speaker 1", Granted)))]);

        Assert.Equal(Note("title: Sync"), ConsentFrontMatter.ReplaceConsents(note, Record()));
    }

    [Fact]
    public void ReplaceConsents_KeepsCrlfLineEndings()
    {
        var note = "---\r\ntitle: Sync\r\n---\r\nbody\r\n";

        var updated = ConsentFrontMatter.ReplaceConsents(note, Record(new ConsentRecordEntry("Speaker 1", Granted)));

        Assert.DoesNotContain("\n", updated.Replace("\r\n", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.EndsWith("\r\n---\r\nbody\r\n", updated, StringComparison.Ordinal);
        Assert.Equal([SessionA, SessionB], ConsentFrontMatter.ReadSessions(updated));
    }

    [Fact]
    public void ReplaceConsents_OnANoteWithoutFrontMatter_PrependsOne()
    {
        var updated = ConsentFrontMatter.ReplaceConsents("# Meeting\n", Record(new ConsentRecordEntry("Speaker 1", Granted)));

        Assert.StartsWith("---\nconsentRecord: pia-consent-record/v1\n", updated, StringComparison.Ordinal);
        Assert.EndsWith("\n---\n# Meeting\n", updated, StringComparison.Ordinal);
        Assert.Equal([SessionA, SessionB], ConsentFrontMatter.ReadSessions(updated));
    }

    [Fact]
    public void ReplaceConsents_LeavesAnUnclosedFrontMatterAlone()
    {
        const string note = "---\ntitle: Sync\nbody\n";

        Assert.Equal(note, ConsentFrontMatter.ReplaceConsents(note, Record(new ConsentRecordEntry("Speaker 1", Granted))));
    }

    private static ConsentEvidence Evidence(string label) => new(
        label, "Anna", "My name is Anna and I accept this recording by Pia.", "en", 0.95f, Granted, "fake-stt",
        ConsentNotice.Version, ConsentNotice.Purposes, "en");
}
