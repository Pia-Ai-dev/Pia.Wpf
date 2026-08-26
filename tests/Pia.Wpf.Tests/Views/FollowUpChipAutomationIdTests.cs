using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Pia.Controls.Chat;
using Pia.Models;
using Xunit;

namespace Pia.Tests.Views;

/// <summary>
/// Both chip lists bind their id to the container's alternation index, which resolves through a
/// RelativeSource hop the id sweep only sees as "a Binding" — an unset AlternationCount or a missed
/// ContentPresenter would leave every chip reporting the same id, or none.
/// </summary>
[Collection("WpfApplicationStatic")]
public class FollowUpChipAutomationIdTests
{
    [Fact]
    public void SuggestionChips_ReportDistinctAutomationIds()
    {
        var ids = WpfStaHost.Run(() => Ids(Rendered(new PiaSuggestionChips
        {
            ItemsSource = new[] { "Summarize it", "Translate it", "Draft a reply" },
        })));
        WpfStaHost.Pump();

        Assert.Equal(["Suggestion_Chip_0", "Suggestion_Chip_1", "Suggestion_Chip_2"], ids);
    }

    [Fact]
    public void AgentModeChips_ReportDistinctAutomationIds()
    {
        var ids = WpfStaHost.Run(() => Ids(Rendered(new PiaAgentModeChip
        {
            ItemsSource = new[]
            {
                new AgentModeSuggestion("Ship the release", "multi-step"),
                new AgentModeSuggestion("Audit the logs", "multi-step"),
            },
        })));
        WpfStaHost.Pump();

        Assert.Equal(["AgentMode_Chip_0", "AgentMode_Chip_1"], ids);
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
