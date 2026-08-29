using Pia.Models;
using Pia.Services.LiveTranscription;
using Xunit;

namespace Pia.Tests.Services.LiveTranscription;

/// <summary>
/// Pins the vault-source document a saved meeting produces. The frontmatter has no in-app parser — the
/// ingest compiler and the user's editor read it — so these assertions are the only contract it has.
/// </summary>
public class MeetingVaultMarkdownTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 12, 9, 0, 0, TimeSpan.FromHours(2));
    private static readonly DateTimeOffset End = new(2026, 8, 12, 9, 47, 0, TimeSpan.FromHours(2));

    private static MeetingVaultMetadata Meta(
        string title = "Q3 roadmap sync",
        IReadOnlyCollection<string>? attendees = null,
        IReadOnlyCollection<string>? tags = null,
        string? project = null,
        string? notes = null)
        => new(title, Start, End, "teams", attendees ?? [], tags ?? [], project, notes);

    [Fact]
    public void Render_EmitsTheFullFrontmatter_ThenTheBody()
    {
        var md = MeetingVaultMarkdown.Render(
            Meta(attendees: ["Anna Weber", "Tom Kraus"], tags: ["roadmap", "planning"],
                project: "Platform", notes: "Follow-up to the June offsite."),
            "# Meeting\n\nbody text\n");

        Assert.Equal(
            $"""
            ---
            schema: pia-meeting/v1
            generator: {AppVersionInfo.Generator}
            aiGenerated: true
            title: Q3 roadmap sync
            date: 2026-08-12
            start: 2026-08-12T09:00:00.0000000+02:00
            end: 2026-08-12T09:47:00.0000000+02:00
            source: teams
            attendees: [Anna Weber, Tom Kraus]
            tags: [roadmap, planning]
            project: Platform
            notes: |-
              Follow-up to the June offsite.
            ---
            # Meeting

            body text

            """.ReplaceLineEndings("\n"),
            md);
    }

    [Fact]
    public void Render_OmitsEveryOptionalKey_WhenNothingWasEntered()
    {
        var md = MeetingVaultMarkdown.Render(Meta(), string.Empty);

        Assert.DoesNotContain("attendees:", md, StringComparison.Ordinal);
        Assert.DoesNotContain("tags:", md, StringComparison.Ordinal);
        Assert.DoesNotContain("project:", md, StringComparison.Ordinal);
        Assert.DoesNotContain("notes:", md, StringComparison.Ordinal);

        // Non-vacuity: the required keys are still there, so the "does not contain" checks mean something.
        Assert.Contains("schema: pia-meeting/v1", md, StringComparison.Ordinal);
        Assert.Contains("title: Q3 roadmap sync", md, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_NeverCarriesTheManagedPageOwnershipKeys()
    {
        // sources/ is the RAW layer; emitting these would claim the file as a Pia-managed memory page.
        var md = MeetingVaultMarkdown.Render(Meta(), "body");

        Assert.DoesNotContain("pia: managed", md, StringComparison.Ordinal);
        Assert.DoesNotContain("schemaVersion:", md, StringComparison.Ordinal);
        Assert.DoesNotContain("\nid:", md, StringComparison.Ordinal);
        Assert.DoesNotContain("\ntype:", md, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Bob: Team A", "'Bob: Team A'")]
    [InlineData("- leading dash", "'- leading dash'")]
    [InlineData("#hashtag", "'#hashtag'")]
    [InlineData("it's fine", "'it''s fine'")]
    [InlineData("say \"hi\"", "'say \"hi\"'")]
    [InlineData("comma, inside", "'comma, inside'")]
    [InlineData("plain title", "plain title")]
    public void Render_QuotesATitleOnlyWhenTheStructureNeedsIt(string title, string expected)
    {
        var md = MeetingVaultMarkdown.Render(Meta(title), "body");

        Assert.Contains($"title: {expected}\n", md, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_QuotesAnAttendeeContainingAComma_SoTheFlowListStaysOneEntry()
    {
        var md = MeetingVaultMarkdown.Render(Meta(attendees: ["Weber, Anna", "Tom"]), "body");

        Assert.Contains("attendees: ['Weber, Anna', Tom]\n", md, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_IndentsEveryNotesLine_AndDropsBlanksAtTheEdges()
    {
        var md = MeetingVaultMarkdown.Render(Meta(notes: "\r\nfirst\r\n\r\nsecond\r\n\r\n"), "body");

        Assert.Contains("notes: |-\n  first\n\n  second\n---\n", md, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_DropsBlankEntriesFromTheCommaSeparatedFields()
    {
        var md = MeetingVaultMarkdown.Render(
            Meta(tags: MeetingVaultMarkdown.SplitList(" roadmap , , planning ,")), "body");

        Assert.Contains("tags: [roadmap, planning]\n", md, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildReference_CombinesTheLocalStartStampWithTheSlug()
    {
        var reference = MeetingVaultMarkdown.BuildReference(Start, "Café (work)! Q3");

        Assert.Equal(
            $"sources/transcripts/meeting-{Start.LocalDateTime:yyyyMMdd-HHmm}-cafe-work-q3.md", reference);
    }

    [Fact]
    public void BuildReference_FallsBackToTheSlugPlaceholder_WhenTheTitleHasNoUsableCharacters()
    {
        var reference = MeetingVaultMarkdown.BuildReference(Start, "!!!");

        Assert.EndsWith("-section.md", reference, StringComparison.Ordinal);
    }
}
