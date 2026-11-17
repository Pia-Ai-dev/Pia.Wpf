using System.Windows;
using System.Windows.Controls;
using Pia.Models;
using Pia.ViewModels;

namespace Pia.Views;

public sealed class MessageRoleTemplateSelector : DataTemplateSelector
{
    public DataTemplate? UserTemplate { get; set; }

    public DataTemplate? AssistantTemplate { get; set; }

    public DataTemplate? RunBlockTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container) => item switch
    {
        RunProgressViewModel => RunBlockTemplate,
        AssistantMessage { IsUser: true } => UserTemplate,
        _ => AssistantTemplate,
    };
}
