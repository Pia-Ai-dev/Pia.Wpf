using Pia.Models;
using Pia.Services;
using Pia.Services.Exceptions;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// The interview only holds together while every reply is either a question set or a draft. Anything
/// else has to be retryable rather than quietly ending the interview on whatever the model said — except
/// at the turn cap, where there is no next turn to retry into.
/// </summary>
public sealed class AdvancedCreationTurnParsingTests
{
    [Fact]
    public void An_ask_turn_carries_its_questions()
    {
        var turn = AdvancedCreationService.ParseTurn("""
            {"state":"ask","summary":"A weekly competitor digest.","questions":[
              {"id":"tone","kind":"choice","label":"How formal?","options":["Formal","Casual"],"optional":false},
              {"id":"sample","kind":"sample","label":"Paste an example","help":"One real one teaches more."}]}
            """, capReached: false);

        Assert.False(turn.IsComplete);
        Assert.Equal("A weekly competitor digest.", turn.Summary);
        Assert.Equal(2, turn.Questions.Count);
        Assert.Equal(AdvancedCreationAnswerKind.Choice, turn.Questions[0].Kind);
        Assert.Equal(["Formal", "Casual"], turn.Questions[0].Options);
        Assert.Equal(AdvancedCreationAnswerKind.Sample, turn.Questions[1].Kind);
        Assert.Equal("One real one teaches more.", turn.Questions[1].Help);
    }

    [Fact]
    public void A_done_turn_hands_back_the_draft_object_verbatim()
    {
        var turn = AdvancedCreationService.ParseTurn(
            """{"state":"done","summary":"Ready.","draft":{"name":"Digest","goal":"Summarise."}}""",
            capReached: false);

        Assert.True(turn.IsComplete);
        Assert.Empty(turn.Questions);
        Assert.Equal("Digest", DraftParsing.ParseRoutineDraft(turn.DraftJson!).Name);
    }

    [Fact]
    public void A_code_fence_around_the_json_is_tolerated()
    {
        var turn = AdvancedCreationService.ParseTurn("""
            Here you go:
            ```json
            {"state":"done","draft":{"name":"Digest"}}
            ```
            """, capReached: false);

        Assert.True(turn.IsComplete);
        Assert.Equal("Digest", DraftParsing.ParseRoutineDraft(turn.DraftJson!).Name);
    }

    [Fact]
    public void More_than_three_questions_are_cut_to_three()
    {
        var turn = AdvancedCreationService.ParseTurn("""
            {"state":"ask","questions":[
              {"id":"a","label":"A"},{"id":"b","label":"B"},
              {"id":"c","label":"C"},{"id":"d","label":"D"}]}
            """, capReached: false);

        Assert.Equal(3, turn.Questions.Count);
    }

    [Fact]
    public void An_unknown_kind_degrades_to_a_text_box_rather_than_dropping_the_question()
    {
        var turn = AdvancedCreationService.ParseTurn(
            """{"state":"ask","questions":[{"id":"x","kind":"colour-wheel","label":"Which?"}]}""",
            capReached: false);

        Assert.Single(turn.Questions);
        Assert.Equal(AdvancedCreationAnswerKind.Text, turn.Questions[0].Kind);
    }

    [Fact]
    public void A_choice_with_no_options_becomes_a_text_box()
    {
        var turn = AdvancedCreationService.ParseTurn(
            """{"state":"ask","questions":[{"id":"x","kind":"choice","label":"Which?","options":[]}]}""",
            capReached: false);

        Assert.Equal(AdvancedCreationAnswerKind.Text, turn.Questions[0].Kind);
    }

    [Fact]
    public void A_question_missing_its_id_or_label_is_dropped()
    {
        var turn = AdvancedCreationService.ParseTurn("""
            {"state":"ask","questions":[
              {"id":"","label":"No id"},{"id":"b","label":""},{"id":"c","label":"Kept"}]}
            """, capReached: false);

        Assert.Single(turn.Questions);
        Assert.Equal("c", turn.Questions[0].Id);
    }

    /// <summary>"done" with nothing in it parses to an all-null record, so the editor would fill with
    /// nothing and "Use this draft" would visibly do nothing. Better to ask the model again.</summary>
    [Theory]
    [InlineData("""{"state":"done","summary":"Ready."}""")]
    [InlineData("""{"state":"done","draft":null}""")]
    public void Done_without_a_draft_is_refused(string raw)
    {
        Assert.Throws<AdvancedCreationReplyException>(
            () => AdvancedCreationService.ParseTurn(raw, capReached: false));
    }

    [Theory]
    [InlineData("Just some prose, no JSON at all.")]
    [InlineData("""{"state":"ask","questions":[]}""")]
    [InlineData("""{"state":"ask"}""")]
    [InlineData("{ not valid json at all ]")]
    public void An_unusable_reply_before_the_cap_is_retryable(string raw)
    {
        Assert.Throws<AdvancedCreationReplyException>(
            () => AdvancedCreationService.ParseTurn(raw, capReached: false));
    }

    /// <summary>Past the cap the model has already been told to finish. Rendering a seventh question would
    /// make the cap advisory, which is the one thing bounding what an interview can cost.</summary>
    [Fact]
    public void A_question_past_the_cap_is_refused_rather_than_rendered()
    {
        Assert.Throws<AdvancedCreationReplyException>(() => AdvancedCreationService.ParseTurn(
            """{"state":"ask","questions":[{"id":"x","label":"One more thing?"}]}""", capReached: true));
    }

    [Fact]
    public void A_draft_at_the_cap_is_still_accepted()
    {
        var turn = AdvancedCreationService.ParseTurn(
            """{"state":"done","draft":{"name":"Digest"}}""", capReached: true);

        Assert.True(turn.IsComplete);
        Assert.Equal("Digest", DraftParsing.ParseRoutineDraft(turn.DraftJson!).Name);
    }

    [Theory]
    [InlineData("Just some prose, no JSON at all.")]
    public void The_same_reply_at_the_cap_ends_the_interview_on_what_there_is(string raw)
    {
        var turn = AdvancedCreationService.ParseTurn(raw, capReached: true);

        Assert.True(turn.IsComplete);
        Assert.Equal(raw.Trim(), turn.DraftJson);
    }
}
