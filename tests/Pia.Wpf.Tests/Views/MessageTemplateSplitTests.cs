using System.Windows;
using System.Windows.Media;
using Microsoft.Extensions.AI;
using Pia.Controls.Chat;
using Pia.Models;
using Pia.ViewModels;
using Xunit;

namespace Pia.Tests.Views;

/// <summary>
/// Collapsed is not uncreated: one template that built both bubbles and hid the wrong one still constructed
/// the assistant stack for a one-line user turn. That is the per-message cost the transcript window multiplies.
/// </summary>
[Collection("WpfApplicationStatic")]
public class MessageTemplateSplitTests
{
    [Fact]
    public void AUserTurn_BuildsNoAssistantBubble() =>
        Assert.Equal(0, Realized<PiaAssistantMessage>(ChatRole.User));

    [Fact]
    public void AnAssistantTurn_BuildsNoUserBubble() =>
        Assert.Equal(0, Realized<PiaCollapsibleMessageText>(ChatRole.Assistant));

    /// <summary>Without this, a template that rendered nothing at all would satisfy both arms above.</summary>
    [Fact]
    public void EachTurn_StillBuildsItsOwnBubble()
    {
        Assert.Equal(1, Realized<PiaCollapsibleMessageText>(ChatRole.User));
        Assert.Equal(1, Realized<PiaAssistantMessage>(ChatRole.Assistant));
    }

    private static int Realized<T>(ChatRole role) where T : DependencyObject
    {
        AssistantViewModel? vm = null;
        try
        {
            var found = WpfStaHost.Run(() =>
            {
                vm = AssistantViewModelBuilder.Create();
                var view = new Pia.Views.AssistantView { DataContext = vm };
                vm.Messages.Add(new AssistantMessage(role, "a turn"));
                // A collapsed ScrollViewer measures nothing, so no container would be generated.
                vm.HasMessages = true;
                view.Measure(new Size(1000, 900));
                view.Arrange(new Rect(0, 0, 1000, 900));
                view.UpdateLayout();

                var container = view.MessageItemsControl.ItemContainerGenerator.ContainerFromIndex(0)
                    ?? throw new InvalidOperationException("the message list generated no container");
                return Count<T>(container);
            });
            WpfStaHost.Pump();
            return found;
        }
        finally
        {
            WpfStaHost.Run(() =>
            {
                vm?.Dispose();
                return 0;
            });
        }
    }

    private static int Count<T>(DependencyObject element) where T : DependencyObject
    {
        var found = element is T ? 1 : 0;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
            found += Count<T>(VisualTreeHelper.GetChild(element, i));
        return found;
    }
}
