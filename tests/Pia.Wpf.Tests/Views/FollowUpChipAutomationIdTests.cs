using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Extensions.AI;
using Pia.Controls.Chat;
using Pia.Models;
using Xunit;

namespace Pia.Tests.Views;

/// <summary>
/// Both chip lists key their id on the hosting message's Id plus the container's alternation index,
/// resolved through an ElementName + RelativeSource hop the id sweep only sees as "a Binding" — a
/// missing DataContext, an unset AlternationCount, or a missed ContentPresenter would leave every chip
/// reporting the same id, or none.
/// </summary>
[Collection("WpfApplicationStatic")]
public class FollowUpChipAutomationIdTests
{
    [Fact]
    public void SuggestionChips_ReportDistinctAutomationIds()
    {
        var message = new AssistantMessage(ChatRole.Assistant);
        var ids = WpfStaHost.Run(() => Ids(Rendered(new PiaSuggestionChips
        {
            DataContext = message,
            ItemsSource = new[] { "Summarize it", "Translate it", "Draft a reply" },
        })));
        WpfStaHost.Pump();

        Assert.Equal(
            [$"Suggestion_Chip_{message.Id}_0", $"Suggestion_Chip_{message.Id}_1", $"Suggestion_Chip_{message.Id}_2"],
            ids);
    }

    [Fact]
    public void AgentModeChips_ReportDistinctAutomationIds()
    {
        var message = new AssistantMessage(ChatRole.Assistant);
        var ids = WpfStaHost.Run(() => Ids(Rendered(new PiaAgentModeChip
        {
            DataContext = message,
            ItemsSource = new[]
            {
                new AgentModeSuggestion("Ship the release", "multi-step"),
                new AgentModeSuggestion("Audit the logs", "multi-step"),
            },
        })));
        WpfStaHost.Pump();

        Assert.Equal(
            [$"AgentMode_Chip_{message.Id}_0", $"AgentMode_Chip_{message.Id}_1"],
            ids);
    }

    private static T Rendered<T>(T view) where T : FrameworkElement
    {
        view.Measure(new Size(1000, 1000));
        view.Arrange(new Rect(0, 0, 1000, 1000));
        view.UpdateLayout();
        return view;
    }

    private static string[] Ids(DependencyObject root) =>
        [.. Descendants(root).OfType<Button>().Select(AutomationProperties.GetAutomationId)];

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var nested in Descendants(child))
                yield return nested;
        }
    }
}
