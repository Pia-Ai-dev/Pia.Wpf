using Pia.Shared.Models;
using Pia.ViewModels;
using Wpf.Ui.Controls;
using Xunit;

namespace Pia.Tests.ViewModels;

public class AssistantFeedbackOutcomeTests
{
    [Fact]
    public void NotSent_ShowsTheFailure()
    {
        var outcome = AssistantViewModel.FeedbackOutcome(null, positive: false);

        Assert.Equal("Msg_Assistant_FeedbackFailed", outcome.Text);
        Assert.Equal(ControlAppearance.Danger, outcome.Appearance);
    }

    [Fact]
    public void StoredOnly_SaysNobodyWasNotified()
    {
        var outcome = AssistantViewModel.FeedbackOutcome(
            new AiFeedbackResponse { Delivery = AiFeedbackResponse.DeliveryStoredOnly }, positive: false);

        Assert.Equal("Msg_Assistant_FeedbackStoredOnly", outcome.Text);
        Assert.Equal(ControlAppearance.Caution, outcome.Appearance);
    }

    [Theory]
    [InlineData(AiFeedbackResponse.DeliveryNotified, false)]
    [InlineData(null, false)]
    [InlineData(AiFeedbackResponse.DeliveryStoredOnly, true)]
    public void Otherwise_ThanksAsBefore(string? delivery, bool positive)
    {
        var outcome = AssistantViewModel.FeedbackOutcome(new AiFeedbackResponse { Delivery = delivery }, positive);

        Assert.Equal("Msg_Assistant_FeedbackSent", outcome.Text);
        Assert.Equal(ControlAppearance.Success, outcome.Appearance);
    }
}
