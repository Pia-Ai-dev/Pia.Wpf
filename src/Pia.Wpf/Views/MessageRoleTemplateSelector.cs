using System.Windows;
using System.Windows.Controls;
using Pia.Models;

namespace Pia.Views;

public sealed class MessageRoleTemplateSelector : DataTemplateSelector
{
    public DataTemplate? UserTemplate { get; set; }

    public DataTemplate? AssistantTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container) =>
        item is AssistantMessage { IsUser: true } ? UserTemplate : AssistantTemplate;
}
