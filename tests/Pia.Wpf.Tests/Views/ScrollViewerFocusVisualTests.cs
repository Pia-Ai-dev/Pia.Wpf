using System.Windows;
using System.Windows.Controls;
using Xunit;

namespace Pia.Tests.Views;

/// <summary>
/// A ScrollViewer takes keyboard focus whenever automation sets it there, and WPF-UI's default focus visual
/// then rings the whole page.
/// </summary>
[Collection("WpfApplicationStatic")]
public class ScrollViewerFocusVisualTests
{
    // AccountView takes the app-wide implicit style; the other two replace it with a local one.
    [Theory]
    [InlineData(typeof(Pia.Views.SettingsViews.AccountView))]
    [InlineData(typeof(Pia.Views.AssistantView))]
    [InlineData(typeof(Pia.Controls.Cards.FileDiffCard))]
    public void ScrollViewer_DrawsNoFocusVisual(Type viewType)
    {
        var withFocusVisual = WpfStaHost.Run(() =>
        {
            var root = (FrameworkElement)Activator.CreateInstance(viewType)!;
            var scrollViewers = new List<ScrollViewer>();
            Collect(root, scrollViewers);
            Assert.NotEmpty(scrollViewers);
            return scrollViewers.Count(sv => sv.FocusVisualStyle is not null);
        });

        Assert.Equal(0, withFocusVisual);
    }

    private static void Collect(DependencyObject element, List<ScrollViewer> found)
    {
        if (element is ScrollViewer scrollViewer)
            found.Add(scrollViewer);

        foreach (var child in LogicalTreeHelper.GetChildren(element).OfType<DependencyObject>())
            Collect(child, found);
    }
}
