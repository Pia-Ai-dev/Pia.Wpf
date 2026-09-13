using Pia.Models;
using Pia.Services;
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

    [Fact]
    public void Done_without_a_draft_falls_back_to_the_object_rather_than_emptying_the_editor()
    {
        var turn = AdvancedCreationService.ParseTurn(
            """{"state":"done","summary":"Ready."}""", capReached: false);

        Assert.True(turn.IsComplete);
        Assert.False(string.IsNullOrWhiteSpace(turn.DraftJson));
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

    [Theory]
    [InlineData("Just some prose, no JSON at all.")]
    [InlineData("""{"state":"ask","questions":[]}""")]
    public void The_same_reply_at_the_cap_ends_the_interview_on_what_there_is(string raw)
    {
        var turn = AdvancedCreationService.ParseTurn(raw, capReached: true);

        Assert.True(turn.IsComplete);
        Assert.Equal(raw.Trim(), turn.DraftJson);
    }
}
