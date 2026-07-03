using Microsoft.Extensions.AI;
using Pia.Models;
using Xunit;

namespace Pia.Tests.Models;

public class AssistantMessageReasoningStateTests
{
    private static AssistantMessage NewAssistant() => new(ChatRole.Assistant);

    [Fact]
    public void ShowLiveReasoning_True_WhileStreamingWithNoAnswerYet()
    {
        var msg = NewAssistant();
        msg.IsStreaming = true;
        msg.ThinkingContent = "weighing options";

        Assert.True(msg.ShowLiveReasoning);
        Assert.False(msg.ShowReasoningSummary);
    }

    [Fact]
    public void OnceAnswerArrives_FlipsToSummary()
    {
        var msg = NewAssistant();
        msg.IsStreaming = true;
        msg.ThinkingContent = "weighing options";
        msg.Content = "the answer";

        Assert.False(msg.ShowLiveReasoning);
        Assert.True(msg.ShowReasoningSummary);
    }

    [Fact]
    public void AfterStreaming_WithReasoning_ShowsSummary()
    {
        var msg = NewAssistant();
        msg.ThinkingContent = "weighing options";
        msg.Content = "the answer";
        msg.IsStreaming = false;

        Assert.False(msg.ShowLiveReasoning);
        Assert.True(msg.ShowReasoningSummary);
    }

    [Fact]
    public void NoReasoning_NeverShowsSummary()
    {
        var msg = NewAssistant();
        msg.Content = "the answer";
        msg.IsStreaming = false;

        Assert.False(msg.ShowReasoningSummary);
        Assert.False(msg.ShowLiveReasoning);
    }

    [Fact]
    public void ReasoningDurationLabel_DrivesHasReasoningDuration()
    {
        var msg = NewAssistant();
        Assert.False(msg.HasReasoningDuration);

        msg.ReasoningDurationLabel = "Thought for 8s";
        Assert.True(msg.HasReasoningDuration);
    }

    [Fact]
    public void ComputedFlags_RaisePropertyChanged_WhenDependenciesChange()
    {
        var msg = NewAssistant();
        var raised = new List<string?>();
        msg.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        msg.IsStreaming = true;
        Assert.Contains(nameof(AssistantMessage.ShowLiveReasoning), raised);

        raised.Clear();
        msg.Content = "hi";
        Assert.Contains(nameof(AssistantMessage.ShowLiveReasoning), raised);
        Assert.Contains(nameof(AssistantMessage.ShowReasoningSummary), raised);

        raised.Clear();
        msg.ThinkingContent = "reasoned";
        Assert.Contains(nameof(AssistantMessage.ShowReasoningSummary), raised);

        raised.Clear();
        msg.ReasoningDurationLabel = "Thought for 3s";
        Assert.Contains(nameof(AssistantMessage.HasReasoningDuration), raised);
    }
}
