using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using Xunit;

namespace Pia.Tests.Views;

/// <summary>
/// The id sweep in <see cref="ViewAutomationIdTests"/> only inspects controls inside a DataTemplate, so it
/// cannot see the item CONTAINER — which is the node UIA offers for a list row, and whose default name is the
/// view-model's <c>ToString()</c>. That default is what forced index-based row selection in a walkthrough.
/// </summary>
[Collection("WpfApplicationStatic")]
public class RowContainerAutomationTests
{
    [Fact]
    public void TheChatHistoryRowContainer_IsNamedAndIdentifiedPerItem()
    {
        Assert.Equal(
            "id=binding;name=binding",
            SurveyFirstListBox(() => new Pia.Controls.AssistantHistory.PiaAssistantChatGroupCard()));
    }

    [Fact]
    public void TheRoutineRowContainer_IsNamedAndIdentifiedPerItem()
    {
        Assert.Equal("id=binding;name=binding", SurveyFirstListBox(() => new Pia.Views.RoutinesView()));
    }

    private static string SurveyFirstListBox(Func<FrameworkElement> create) => WpfStaHost.Run(() =>
    {
        var root = create();
        var list = FirstListBox(root)
            ?? throw new InvalidOperationException($"{root.GetType().Name} declares no ListBox any more");
        var style = list.ItemContainerStyle
            ?? throw new InvalidOperationException($"{root.GetType().Name}'s ListBox has no ItemContainerStyle");

        return $"id={Describe(style, AutomationProperties.AutomationIdProperty)};"
            + $"name={Describe(style, AutomationProperties.NameProperty)}";
    });

    /// <summary>A literal would give every row the same id or the same name, which is the bug, not the fix.</summary>
    private static string Describe(Style style, DependencyProperty property)
    {
        var setter = style.Setters.OfType<Setter>().LastOrDefault(s => s.Property == property);
        return setter?.Value switch
        {
            null => "missing",
            BindingBase => "binding",
            _ => "literal",
        };
    }

    private static ListBox? FirstListBox(DependencyObject element)
    {
        if (element is ListBox list)
            return list;

        foreach (var child in LogicalTreeHelper.GetChildren(element).OfType<DependencyObject>())
        {
            if (FirstListBox(child) is { } found)
                return found;
        }

        return null;
    }
}
