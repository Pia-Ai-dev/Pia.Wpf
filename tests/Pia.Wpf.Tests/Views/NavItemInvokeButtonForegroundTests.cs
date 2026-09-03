using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Xunit;

namespace Pia.Tests.Views;

/// <summary>
/// Every navigation label is a TextBlock inside a Button styled with NavItemInvokeButtonStyle, and its colour
/// comes from nowhere else: the style sets no Foreground, so the label has to inherit the one the surrounding
/// NavigationViewItem supplies per state. A Button's THEME style also sets Foreground — to the classic system
/// control text, which stays black under a dark app theme — and a theme-style setter outranks inheritance.
/// That is the whole of the black-nav-text bug, so it is pinned on the mechanism rather than on a colour.
/// </summary>
[Collection("WpfApplicationStatic")]
public class NavItemInvokeButtonForegroundTests
{
    [Fact]
    public void ANavLabel_InheritsTheColourItsNavigationItemSupplies()
    {
        var label = WpfStaHost.Run(() =>
        {
            var probe = new TextBlock { Text = "Assistant" };
            var button = new Button
            {
                Style = (Style)Application.Current.Resources["NavItemInvokeButtonStyle"],
                Content = probe
            };

            // Stands in for the NavigationViewItem, whose template is what actually carries the theme's
            // per-state foreground down onto the content.
            var host = new Grid { Children = { button } };
            TextElement.SetForeground(host, Brushes.White);

            host.Measure(new Size(200, 40));
            host.Arrange(new Rect(0, 0, 200, 40));
            host.UpdateLayout();

            return (probe.Foreground as SolidColorBrush)?.Color;
        });

        Assert.Equal(Colors.White, label);
    }
}
