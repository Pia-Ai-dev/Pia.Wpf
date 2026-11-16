using System.Windows;
using System.Windows.Automation;
using Pia.Views.SettingsViews;
using Xunit;

namespace Pia.Tests.Views;

/// <summary>Rows carry no textual on/off mark, so a screen reader tells the groups apart only by their names.</summary>
[Collection("WpfApplicationStatic")]
public class McpServersViewAutomationTests
{
    private McpServersView _view = null!;

    [Theory]
    [InlineData("McpServers_Detail_AllowedTools")]
    [InlineData("McpServers_Detail_WithheldGroup")]
    public void EachToolGroup_CarriesAnAccessibleName(string automationId)
    {
        WpfStaHost.Run(() => { _view = new McpServersView(); return 0; });
        WpfStaHost.Pump();

        var name = WpfStaHost.Run(() => AutomationProperties.GetName(Find(_view, automationId)));

        Assert.False(string.IsNullOrWhiteSpace(name), $"{automationId} has no AutomationProperties.Name");
    }

    private static DependencyObject Find(DependencyObject root, string automationId) =>
        FindOrNull(root, automationId)
        ?? throw new InvalidOperationException($"No element with AutomationId {automationId}");

    private static DependencyObject? FindOrNull(DependencyObject root, string automationId)
    {
        if (AutomationProperties.GetAutomationId(root) == automationId)
            return root;

        return LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>()
            .Select(child => FindOrNull(child, automationId))
            .FirstOrDefault(hit => hit is not null);
    }
}
