using Pia.Models;
using Pia.Models.Flow;
using Pia.Services.Flow;
using Wpf.Ui.Controls;
using Xunit;

namespace Pia.Tests.Services.Flow;

/// <summary>Each source vocabulary → the single <see cref="FlowSeverity"/> target (design §8, §11).</summary>
public class FlowSeverityMapperTests
{
    [Theory]
    [InlineData(ControlAppearance.Success, FlowSeverity.Success)]
    [InlineData(ControlAppearance.Caution, FlowSeverity.Warning)]
    [InlineData(ControlAppearance.Danger, FlowSeverity.Error)]
    [InlineData(ControlAppearance.Info, FlowSeverity.Info)]
    [InlineData(ControlAppearance.Primary, FlowSeverity.Info)]
    [InlineData(ControlAppearance.Secondary, FlowSeverity.Info)]
    public void FromSnackbar_MapsAppearance(ControlAppearance appearance, FlowSeverity expected)
    {
        Assert.Equal(expected, FlowSeverityMapper.FromSnackbar(appearance));
    }

    [Theory]
    [InlineData(ChatState.WaitingForTool, FlowSeverity.ActionRequired)]
    [InlineData(ChatState.Completed, FlowSeverity.Success)]
    [InlineData(ChatState.Error, FlowSeverity.Error)]
    [InlineData(ChatState.Idle, FlowSeverity.Info)]
    [InlineData(ChatState.Running, FlowSeverity.Info)]
    public void FromChatState_MapsState(ChatState state, FlowSeverity expected)
    {
        Assert.Equal(expected, FlowSeverityMapper.FromChatState(state));
    }
}
